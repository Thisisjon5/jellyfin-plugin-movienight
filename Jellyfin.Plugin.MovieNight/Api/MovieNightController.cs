using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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

    // SPIKE (single-process/multi-input switcher, see planning/DECISIONS.md 2026-08-14): a third,
    // always-tunable test channel - see GetSwitcherSpikeStream.
    private const string SwitcherSpikeChannelId = "movienightswitcher";
    private const string SwitcherSpikeChannelName = "Movie Night (switcher spike)";

    // ffmpeg's -hls_segment_filename uses %03d (minimum 3 digits, not a cap) - a broadcast running
    // past segment 999 (i.e. beyond ~66 minutes at 4s/segment) produces 4+ digit names, so this
    // must not anchor to exactly 3 digits. The three prefixes correspond to the three encoders
    // spike 5's video-switcher design can be pointing at: the live/original movie, the always-
    // running filler, and a resumed movie - see BroadcastManager.ResolveServedFilePath.
    private static readonly Regex SegmentFileNamePattern = new(@"^(?:segment|filler|resume)_\d+\.ts$", RegexOptions.Compiled);

    private readonly IServerApplicationHost _appHost;
    private readonly BroadcastManager _broadcastManager;
    private readonly LiveSession _liveSession;
    private readonly LadderEncoder _ladderEncoder;
    private readonly LiveSourceKnobs _knobs;
    private readonly ILogger<MovieNightController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MovieNightController"/> class.
    /// </summary>
    /// <param name="appHost">Used to build loopback URLs against the server's own HTTP port.</param>
    /// <param name="broadcastManager">Source of truth for whether a broadcast is live and where its HLS output lives.</param>
    /// <param name="liveSession">The live broadcast - supplies the guide block for the live channel.</param>
    /// <param name="ladderEncoder">Owns the ladder directory (for the debug-only anonymous route).</param>
    /// <param name="knobs">Whether the anonymous ladder route is open.</param>
    /// <param name="logger">Logger (feed copy-loop instrumentation).</param>
    public MovieNightController(IServerApplicationHost appHost, BroadcastManager broadcastManager, LiveSession liveSession, LadderEncoder ladderEncoder, LiveSourceKnobs knobs, ILogger<MovieNightController> logger)
    {
        _appHost = appHost;
        _broadcastManager = broadcastManager;
        _liveSession = liveSession;
        _ladderEncoder = ladderEncoder;
        _knobs = knobs;
        _logger = logger;
    }

    /// <summary>
    /// DEBUG ONLY: the ladder with no auth, for A/B-ing whether the <c>api_key</c> on the real
    /// route is what breaks a client. 404 unless <see cref="LiveSourceKnobs.UseAnonymousRoute"/>
    /// is on (set via <c>POST /MovieNight/api/debug/live/source?anonymousRoute=true</c>), which
    /// also makes the tuner host hand out this URL. Same exact-name whitelist as the real route.
    /// </summary>
    /// <param name="path">Path below <c>stream/hls-open/</c>.</param>
    /// <returns>The file, or 404.</returns>
    [HttpGet("stream/hls-open/{**path}")]
    public IActionResult GetOpenLadderFile([FromRoute] string path)
    {
        if (!_knobs.UseAnonymousRoute)
        {
            return NotFound();
        }

        var fullPath = _ladderEncoder.ResolveFile(path);
        _ladderEncoder.RecordHit(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), "open:" + path, fullPath is not null);
        if (fullPath is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        return path.EndsWith(".m3u8", StringComparison.Ordinal)
            ? PhysicalFile(fullPath, "application/vnd.apple.mpegurl")
            : PhysicalFile(fullPath, "video/mp2t");
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

        // Both spike channels are opt-in via plugin config (Jon's call, 2026-08-14: the channel
        // list should only show what's actually being tested, not everything all the time).
        var config = Plugin.Instance?.Configuration;
        if (config?.EnableRawTsSpikeChannel == true)
        {
            var rawTsUrl = $"http://127.0.0.1:{_appHost.HttpPort}/MovieNight/stream/live.ts";
            m3u += $"#EXTINF:-1 tvg-id=\"{RawTsSpikeChannelId}\" tvg-name=\"{RawTsSpikeChannelName}\",{RawTsSpikeChannelName}\n{rawTsUrl}\n";
        }

        if (config?.EnableSwitcherSpikeChannel == true)
        {
            var switcherUrl = $"http://127.0.0.1:{_appHost.HttpPort}/MovieNight/stream/switcher.ts";
            m3u += $"#EXTINF:-1 tvg-id=\"{SwitcherSpikeChannelId}\" tvg-name=\"{SwitcherSpikeChannelName}\",{SwitcherSpikeChannelName}\n{switcherUrl}\n";
        }

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
    public Task GetRawTsSpikeStream(CancellationToken cancellationToken) =>
        PipeRawTsProcessAsync(_broadcastManager.StartRawTsSpikeProcess, _broadcastManager.StopRawTsSpikeProcess, cancellationToken);

    /// <summary>
    /// SPIKE (single-process/multi-input switcher, see planning/DECISIONS.md 2026-08-14): pipes
    /// the single-process/multi-input switcher's raw MPEG-TS stdout straight to the client - see
    /// <see cref="BroadcastManager.StartSwitcherSpikeProcess"/>. Use
    /// <c>POST /MovieNight/api/debug/switcher-select</c> while tuned in to switch which of the 3
    /// inputs is live, and watch whether the switch is seamless through Jellyfin's own remux hop.
    /// </summary>
    /// <param name="cancellationToken">Bound to the HTTP request - lets the encoder be killed the moment the client disconnects.</param>
    /// <returns>A task that completes once the client disconnects or the encoder exits.</returns>
    [HttpGet("stream/switcher.ts")]
    public Task GetSwitcherSpikeStream(CancellationToken cancellationToken) =>
        PipeRawTsProcessAsync(_broadcastManager.StartSwitcherSpikeProcess, _broadcastManager.StopSwitcherSpikeProcess, cancellationToken);

    /// <summary>
    /// SWITCHER V2: the movie feed the switcher process consumes as its input 0. The load-bearing
    /// property is that this response NEVER ends while a v2 session is active - when the feeder is
    /// restarted at a new position (resume-at-timestamp), the copy loop simply stalls until the
    /// replacement feeder exists and then continues, so the switcher's input sees a pause in
    /// bytes, never an EOF, and the switcher process itself never has to restart.
    /// </summary>
    /// <param name="cancellationToken">Bound to the switcher's HTTP connection.</param>
    /// <returns>A task that completes when the v2 session is cleared or the consumer disconnects.</returns>
    [HttpGet("stream/feed.ts")]
    public async Task GetSwitcherV2Feed(CancellationToken cancellationToken)
    {
        if (!IsLoopbackRequest())
        {
            HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            return;
        }

        HttpContext.Response.ContentType = "video/mp2t";
        _logger.LogInformation("Movie Night: feed.ts consumer connected");
        _broadcastManager.HandleSw2ConsumerConnected();
        try
        {
            while (!cancellationToken.IsCancellationRequested && _broadcastManager.IsSwitcherV2SessionActive())
            {
                var feeder = _broadcastManager.GetSw2FeederProcess();
                if (feeder is null)
                {
                    // Lazy feeder start: the movie only begins being consumed once the feed has an
                    // actual consumer (this loop), so no backlog ever builds up ahead of demand.
                    _broadcastManager.EnsureSw2FeederStarted();
                    await Task.Delay(300, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                long copied = 0;
                var sourcePid = SafePid(feeder);
                var stretchStarted = DateTime.UtcNow;
                var lastHeartbeat = stretchStarted;
                _logger.LogInformation("Movie Night: feed.ts copying from feeder pid {Pid}", sourcePid);
                try
                {
                    // Chunked manual copy (not CopyToAsync) so every chunk boundary is a chance to
                    // notice the current feeder changed - a blocked whole-stream copy was in the
                    // suspect set for the v0.3.27 pause failure where the slate's bytes never
                    // reached the switcher. Ends when the source EOFs (feeder killed/movie ended)
                    // or a swap replaced it.
                    var buffer = new byte[64 * 1024];
                    var source = feeder.StandardOutput.BaseStream;
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                        if (read <= 0)
                        {
                            break;
                        }

                        await HttpContext.Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        copied += read;

                        // Once-a-minute heartbeat (added for the soak test): effective bitrate per
                        // stretch is the server-side drift detector - a feeder pacing off realtime
                        // shows up here long before a bad resume position would reveal it.
                        var now = DateTime.UtcNow;
                        if ((now - lastHeartbeat).TotalSeconds >= 60)
                        {
                            lastHeartbeat = now;
                            var elapsed = (now - stretchStarted).TotalSeconds;
                            _logger.LogInformation(
                                "Movie Night: feed.ts heartbeat - pid {Pid}, {Elapsed:F0}s into stretch, {Bytes} bytes (~{Kbps:F0} kbps)",
                                sourcePid,
                                elapsed,
                                copied,
                                copied * 8 / 1000.0 / elapsed);
                        }

                        if (!ReferenceEquals(_broadcastManager.GetSw2FeederProcess(), feeder))
                        {
                            _logger.LogInformation("Movie Night: feed.ts source swapped away from pid {Pid} mid-copy", sourcePid);
                            break;
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                    // Feeder disposed mid-copy; loop and re-fetch.
                }

                _logger.LogInformation("Movie Night: feed.ts source pid {Pid} ended after {Bytes} bytes", sourcePid, copied);
                _broadcastManager.HandleSw2FeederEnded(feeder);
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Consumer (the switcher process) disconnected - expected.
        }
        finally
        {
            _broadcastManager.HandleSw2ConsumerDisconnected();
            _logger.LogInformation("Movie Night: feed.ts consumer disconnected");
        }
    }

    private static int SafePid(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    /// <summary>
    /// Shared plumbing for both raw-MPEG-TS spike endpoints: starts a process via
    /// <paramref name="startProcess"/>, streams its stdout to the response body until the client
    /// disconnects or the process exits, then stops it via <paramref name="stopProcess"/>.
    /// </summary>
    private async Task PipeRawTsProcessAsync(Func<Process?> startProcess, Action<Process> stopProcess, CancellationToken cancellationToken)
    {
        if (!IsLoopbackRequest())
        {
            HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            return;
        }

        var process = startProcess();
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
            stopProcess(process);
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

        // The live channel (custom tuner host, channel id "movienight") and the legacy M3U channel
        // share this guide block; the live session wins while it is live.
        var status = _broadcastManager.GetStatus();
        var live = _liveSession.IsLive;
        var channelName = live ? _liveSession.ChannelName : status.ChannelName ?? DefaultChannelName;
        var title = (live ? _liveSession.NowPlaying : status.NowPlaying) ?? channelName;
        var start = (live ? _liveSession.StartedUtc : status.StartedAtUtc) ?? DateTime.UtcNow;
        var runTime = live ? _liveSession.RunTimeTicks : status.RunTimeTicks;
        var duration = runTime is long ticks ? TimeSpan.FromTicks(ticks) : TimeSpan.FromHours(4);
        var stop = start + duration;
        const string Format = "yyyyMMddHHmmss +0000";

        // Both spike channels are opt-in via plugin config - see GetPlaylist. Their guide blocks
        // are a fixed rolling window rather than tied to broadcast state, since they're always
        // tunable whenever enabled.
        var config = Plugin.Instance?.Configuration;
        var spikeBlocks = string.Empty;
        if (config?.EnableRawTsSpikeChannel == true)
        {
            var rawTsStart = DateTime.UtcNow;
            var rawTsStop = rawTsStart + TimeSpan.FromHours(4);
            spikeBlocks += $"""
                  <channel id="{RawTsSpikeChannelId}">
                    <display-name>{RawTsSpikeChannelName}</display-name>
                  </channel>
                  <programme start="{rawTsStart.ToString(Format, CultureInfo.InvariantCulture)}" stop="{rawTsStop.ToString(Format, CultureInfo.InvariantCulture)}" channel="{RawTsSpikeChannelId}">
                    <title>{RawTsSpikeChannelName}</title>
                  </programme>

                """;
        }

        if (config?.EnableSwitcherSpikeChannel == true)
        {
            var switcherStart = DateTime.UtcNow;
            var switcherStop = switcherStart + TimeSpan.FromHours(4);
            spikeBlocks += $"""
                  <channel id="{SwitcherSpikeChannelId}">
                    <display-name>{SwitcherSpikeChannelName}</display-name>
                  </channel>
                  <programme start="{switcherStart.ToString(Format, CultureInfo.InvariantCulture)}" stop="{switcherStop.ToString(Format, CultureInfo.InvariantCulture)}" channel="{SwitcherSpikeChannelId}">
                    <title>{SwitcherSpikeChannelName}</title>
                  </programme>

                """;
        }

        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <tv>
              <channel id="movienight">
                <display-name>{channelName}</display-name>
              </channel>
              <programme start="{start.ToString(Format, CultureInfo.InvariantCulture)}" stop="{stop.ToString(Format, CultureInfo.InvariantCulture)}" channel="movienight">
                <title>{title}</title>
              </programme>
            {spikeBlocks}</tv>
            """;

        return Content(xml, "application/xml");
    }

    private bool IsLoopbackRequest()
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        return remoteIp is not null && IPAddress.IsLoopback(remoteIp);
    }
}
