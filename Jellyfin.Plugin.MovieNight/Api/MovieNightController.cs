using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MovieNight.Api;

/// <summary>
/// Serves the Movie Night HLS stream, M3U playlist, and EPG to Jellyfin's own Live TV tuner. All
/// endpoints are anonymous but loopback-only: media never leaves the server unauthenticated,
/// clients only ever reach it through Jellyfin's authed Live TV path.
/// </summary>
[ApiController]
[Route("MovieNight")]
[AllowAnonymous]
public class MovieNightController : ControllerBase
{
    private const string DefaultChannelName = "Movie Night";
    private const string MasterPlaylistFileName = "master.m3u8";

    // SPIKE (raw MPEG-TS tuner feed, see planning/DECISIONS.md 2026-08-14): a second, always-
    // tunable test channel independent of BroadcastManager's own Live state - see GetRawTsSpikeStream.
    private const string RawTsSpikeChannelId = "movienightrawts";
    private const string RawTsSpikeChannelName = "Movie Night (raw TS spike)";

    // ffmpeg's -hls_segment_filename uses %03d (minimum 3 digits, not a cap) - a broadcast running
    // past segment 999 (i.e. beyond ~66 minutes at 4s/segment) produces 4+ digit names, so this
    // must not anchor to exactly 3 digits. The three prefixes correspond to the three encoders
    // spike 5's video-switcher design can be pointing at: the live/original movie, the always-
    // running filler, and a resumed movie - see BroadcastManager.ResolveServedFilePath.
    private static readonly Regex SegmentFileNamePattern = new(@"^(?:segment|filler|resume)_\d+\.ts$", RegexOptions.Compiled);

    private readonly IServerApplicationHost _appHost;
    private readonly BroadcastManager _broadcastManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="MovieNightController"/> class.
    /// </summary>
    /// <param name="appHost">Used to build loopback URLs against the server's own HTTP port.</param>
    /// <param name="broadcastManager">Source of truth for whether a broadcast is live and where its HLS output lives.</param>
    public MovieNightController(IServerApplicationHost appHost, BroadcastManager broadcastManager)
    {
        _appHost = appHost;
        _broadcastManager = broadcastManager;
    }

    /// <summary>
    /// M3U playlist consumed by Jellyfin's M3U tuner host. Lists the channel only while a
    /// broadcast is live - empty while idle, so the channel itself only appears in Jellyfin's Live
    /// TV guide/channel list when there's actually something to watch (rather than showing
    /// perpetually with a "tune failed" off-air state).
    /// </summary>
    /// <returns>The M3U playlist text, or 403 if the caller isn't loopback.</returns>
    [HttpGet("playlist.m3u")]
    public IActionResult GetPlaylist()
    {
        if (!IsLoopbackRequest())
        {
            return Forbid();
        }

        var status = _broadcastManager.GetStatus();
        var m3u = "#EXTM3U\n";
        if (status.State == BroadcastState.Live)
        {
            var channelName = status.ChannelName ?? DefaultChannelName;
            var streamUrl = $"http://127.0.0.1:{_appHost.HttpPort}/MovieNight/stream/{MasterPlaylistFileName}";
            m3u += $"#EXTINF:-1 tvg-id=\"movienight\" tvg-name=\"{channelName}\",{channelName}\n{streamUrl}\n";
        }

        // Always listed, independent of broadcast state - see StartRawTsSpikeProcess for why.
        var rawTsUrl = $"http://127.0.0.1:{_appHost.HttpPort}/MovieNight/stream/live.ts";
        m3u += $"#EXTINF:-1 tvg-id=\"{RawTsSpikeChannelId}\" tvg-name=\"{RawTsSpikeChannelName}\",{RawTsSpikeChannelName}\n{rawTsUrl}\n";

        return Content(m3u, "application/vnd.apple.mpegurl");
    }

    /// <summary>
    /// SPIKE (raw MPEG-TS tuner feed, see planning/DECISIONS.md 2026-08-14): pipes a fresh
    /// synthetic testsrc/tone ffmpeg process's raw MPEG-TS stdout straight to the client, instead
    /// of the hand-rolled HLS the real channel uses. Answers whether Jellyfin's M3U tuner accepts
    /// and remuxes a raw continuous-stream URL the same way it does HLS - see
    /// <see cref="BroadcastManager.StartRawTsSpikeProcess"/>. Independent of the real broadcast's
    /// state entirely; debug-only.
    /// </summary>
    /// <param name="cancellationToken">Bound to the HTTP request - lets the encoder be killed the moment the client disconnects.</param>
    /// <returns>A task that completes once the client disconnects or the encoder exits.</returns>
    [HttpGet("stream/live.ts")]
    public async Task GetRawTsSpikeStream(CancellationToken cancellationToken)
    {
        if (!IsLoopbackRequest())
        {
            HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            return;
        }

        var process = _broadcastManager.StartRawTsSpikeProcess();
        if (process is null)
        {
            HttpContext.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
            return;
        }

        HttpContext.Response.ContentType = "video/mp2t";
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(HttpContext.Response.Body, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected - expected, not an error.
        }
        finally
        {
            _broadcastManager.StopRawTsSpikeProcess(process);
        }
    }

    /// <summary>
    /// Serves the live broadcast's HLS master playlist (always from
    /// <see cref="BroadcastManager.HlsDirectory"/>, hand-written by BroadcastManager) and segments
    /// (resolved to whichever source directory actually holds them, by filename prefix - see
    /// <see cref="BroadcastManager.ResolveServedFilePath"/>). Both are matched by this one route,
    /// dispatched internally, so there's no route ambiguity between a literal and a parameterized
    /// template for the same URL shape.
    /// </summary>
    /// <param name="fileName">Either <c>master.m3u8</c> or a segment file name like <c>segment_000.ts</c>.</param>
    /// <returns>The requested file, 404 if missing/invalid, or 403 if the caller isn't loopback.</returns>
    [HttpGet("stream/{fileName}")]
    public IActionResult GetStreamFile([FromRoute] string fileName)
    {
        if (!IsLoopbackRequest())
        {
            return Forbid();
        }

        var safeName = Path.GetFileName(fileName);
        var isMasterPlaylist = string.Equals(safeName, MasterPlaylistFileName, StringComparison.Ordinal);
        if (isMasterPlaylist)
        {
            var masterPath = Path.Combine(Path.GetFullPath(_broadcastManager.HlsDirectory), MasterPlaylistFileName);
            return System.IO.File.Exists(masterPath) ? PhysicalFile(masterPath, "application/vnd.apple.mpegurl") : NotFound();
        }

        if (!SegmentFileNamePattern.IsMatch(safeName))
        {
            return NotFound();
        }

        var fullPath = _broadcastManager.ResolveServedFilePath(safeName);
        return fullPath is null ? NotFound() : PhysicalFile(fullPath, "video/mp2t");
    }

    /// <summary>
    /// XMLTV guide data: one channel, one programme block for the current broadcast (or a short
    /// stub when idle - the channel itself is absent from the guide while idle per
    /// <see cref="GetPlaylist"/>, so this mainly matters for the moment between Go Live and the
    /// next guide refresh).
    /// </summary>
    /// <returns>The XMLTV document, or 403 if the caller isn't loopback.</returns>
    [HttpGet("epg.xml")]
    public IActionResult GetEpg()
    {
        if (!IsLoopbackRequest())
        {
            return Forbid();
        }

        var status = _broadcastManager.GetStatus();
        var channelName = status.ChannelName ?? DefaultChannelName;
        var title = status.NowPlaying ?? channelName;
        var start = status.StartedAtUtc ?? DateTime.UtcNow;
        var duration = status.RunTimeTicks is long ticks ? TimeSpan.FromTicks(ticks) : TimeSpan.FromHours(4);
        var stop = start + duration;
        const string Format = "yyyyMMddHHmmss +0000";

        // SPIKE (raw MPEG-TS tuner feed): the test channel is always tunable (see GetPlaylist), so
        // its guide block is a fixed rolling window rather than tied to broadcast state.
        var rawTsStart = DateTime.UtcNow;
        var rawTsStop = rawTsStart + TimeSpan.FromHours(4);

        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <tv>
              <channel id="movienight">
                <display-name>{channelName}</display-name>
              </channel>
              <programme start="{start.ToString(Format, CultureInfo.InvariantCulture)}" stop="{stop.ToString(Format, CultureInfo.InvariantCulture)}" channel="movienight">
                <title>{title}</title>
              </programme>
              <channel id="{RawTsSpikeChannelId}">
                <display-name>{RawTsSpikeChannelName}</display-name>
              </channel>
              <programme start="{rawTsStart.ToString(Format, CultureInfo.InvariantCulture)}" stop="{rawTsStop.ToString(Format, CultureInfo.InvariantCulture)}" channel="{RawTsSpikeChannelId}">
                <title>{RawTsSpikeChannelName}</title>
              </programme>
            </tv>
            """;

        return Content(xml, "application/xml");
    }

    private bool IsLoopbackRequest()
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        return remoteIp is not null && IPAddress.IsLoopback(remoteIp);
    }
}
