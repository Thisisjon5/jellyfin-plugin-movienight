using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
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

    // ffmpeg's -hls_segment_filename uses %03d (minimum 3 digits, not a cap) - a broadcast running
    // past segment 999 (i.e. beyond ~66 minutes at 4s/segment) produces 4+ digit names, so this
    // must not anchor to exactly 3 digits.
    private static readonly Regex SegmentFileNamePattern = new(@"^segment_\d+\.ts$", RegexOptions.Compiled);

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
        if (status.State != BroadcastState.Live)
        {
            return Content("#EXTM3U\n", "application/vnd.apple.mpegurl");
        }

        var channelName = status.ChannelName ?? DefaultChannelName;
        var streamUrl = $"http://127.0.0.1:{_appHost.HttpPort}/MovieNight/stream/{MasterPlaylistFileName}";
        var m3u = $"#EXTM3U\n#EXTINF:-1 tvg-id=\"movienight\" tvg-name=\"{channelName}\",{channelName}\n{streamUrl}\n";
        return Content(m3u, "application/vnd.apple.mpegurl");
    }

    /// <summary>
    /// Serves the live broadcast's HLS master playlist and segments from
    /// <see cref="BroadcastManager.HlsDirectory"/> - <c>master.m3u8</c> and <c>segment_NNN.ts</c>
    /// are both matched here, dispatched internally, so there's no route ambiguity between a
    /// literal and a parameterized template for the same URL shape.
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
        if (!isMasterPlaylist && !SegmentFileNamePattern.IsMatch(safeName))
        {
            return NotFound();
        }

        // Belt-and-suspenders against path traversal: safeName is already restricted to either the
        // exact literal "master.m3u8" or the anchored segment pattern, then re-verify the resolved
        // full path still lands inside HlsDirectory before touching the filesystem.
        var hlsDirectoryFull = Path.GetFullPath(_broadcastManager.HlsDirectory);
        var fullPath = Path.GetFullPath(Path.Combine(hlsDirectoryFull, safeName));
        if (!fullPath.StartsWith(hlsDirectoryFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return NotFound();
        }

        // CA3003 flags this as tainted despite the checks above: the analyzer's dataflow doesn't
        // model an anchored Regex.IsMatch/exact-literal-equals as sanitization. safeName is provably
        // restricted to "master.m3u8" or ^segment_\d+\.ts$, and fullPath is provably inside HlsDirectory.
#pragma warning disable CA3003
        if (!System.IO.File.Exists(fullPath))
        {
            return NotFound();
        }
#pragma warning restore CA3003

        var contentType = isMasterPlaylist ? "application/vnd.apple.mpegurl" : "video/mp2t";
        return PhysicalFile(fullPath, contentType);
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

        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <tv>
              <channel id="movienight">
                <display-name>{channelName}</display-name>
              </channel>
              <programme start="{start.ToString(Format, CultureInfo.InvariantCulture)}" stop="{stop.ToString(Format, CultureInfo.InvariantCulture)}" channel="movienight">
                <title>{title}</title>
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
