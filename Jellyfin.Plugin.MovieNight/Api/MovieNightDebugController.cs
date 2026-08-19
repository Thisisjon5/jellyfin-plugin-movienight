using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MovieNight.Api;

/// <summary>
/// Temporary spike endpoints for the universal-pause architecture decision (see
/// planning/DECISIONS.md, 2026-08-14). Answers two questions before any real pause code gets
/// written: does <see cref="SessionInfo.PlayState"/> report correctly for a Live TV tuner
/// session, and does <see cref="ISessionManager.SendPlaystateCommand"/> get honored by real
/// clients on the Movie Night channel. Delete this controller once both spikes are answered
/// and the real implementation lands.
/// </summary>
[ApiController]
[Route("MovieNight/api/debug")]
[Authorize(Policy = "RequiresElevation")]
public class MovieNightDebugController : ControllerBase
{
    private readonly ISessionManager _sessionManager;
    private readonly BroadcastManager _broadcastManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationHost _appHost;
    private readonly T0Gate _t0Gate;

    /// <summary>
    /// Initializes a new instance of the <see cref="MovieNightDebugController"/> class.
    /// </summary>
    /// <param name="sessionManager">Used to read session play state and push playstate commands.</param>
    /// <param name="broadcastManager">Used to drive the switcher spike.</param>
    /// <param name="libraryManager">Used to resolve item ids for the switcher v2 session.</param>
    /// <param name="appHost">Used to build the loopback feed URL for switcher v2.</param>
    /// <param name="t0Gate">Used to build and measure the T0 gate spike.</param>
    public MovieNightDebugController(ISessionManager sessionManager, BroadcastManager broadcastManager, ILibraryManager libraryManager, IServerApplicationHost appHost, T0Gate t0Gate)
    {
        _sessionManager = sessionManager;
        _broadcastManager = broadcastManager;
        _libraryManager = libraryManager;
        _appHost = appHost;
        _t0Gate = t0Gate;
    }

    /// <summary>
    /// T0 GATE SPIKE: generates the static three-rung ladder the gate tunes. Run once before
    /// testing; takes a while (three software x264 rungs) and writes ~30 MB per minute of ladder.
    /// </summary>
    /// <param name="seconds">Ladder duration, 30-900 (default 300).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>ffmpeg exit code, per-rung segment counts, and stderr.</returns>
    [HttpPost("t0/build-ladder")]
    public async Task<ActionResult> T0BuildLadder([FromQuery] int seconds, CancellationToken cancellationToken)
        => Ok(await _t0Gate.BuildLadderAsync(seconds == 0 ? 300 : seconds, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// T0 GATE SPIKE: the gate's instrument. Shows whether the ladder exists and, crucially, which
    /// remote addresses have fetched its playlists and segments - the server's own address only
    /// means Jellyfin is still sitting in the middle; the viewer's address means the client is
    /// pulling directly and the per-client hop is gone.
    /// </summary>
    /// <returns>Ladder state plus the recorded fetches and tuner-host calls.</returns>
    [HttpGet("t0/status")]
    public ActionResult T0Status() => Ok(_t0Gate.GetStatus());

    /// <summary>
    /// T0 GATE SPIKE: clears recorded hits, so one tune attempt is measured without the previous
    /// attempt's noise. Call immediately before each tune.
    /// </summary>
    /// <returns>200.</returns>
    [HttpPost("t0/reset")]
    public ActionResult T0Reset()
    {
        _t0Gate.ResetHits();
        return Ok();
    }

    /// <summary>
    /// T0 GATE SPIKE: sets the media-source fields the gate turns on, without a new plugin release.
    /// <para>
    /// This exists because of how the first gate run (v0.3.32.0) went: it failed on a relative URL
    /// AND on <c>Container="hls"</c> at the same time, which are two independent variables, and a
    /// release-per-experiment loop to separate them would cost a NAS restart each (~4 minutes on
    /// this box). Same lesson as the encode-probe endpoint in CLAUDE.md - make the knob remote
    /// before running the experiment.
    /// </para>
    /// Every parameter is optional; omitted ones keep their current value.
    /// </summary>
    /// <param name="baseUrl">Absolute base for the ladder URL (e.g. http://100.108.120.127:8096), "loopback" for the server's own address, or "" for a relative URL.</param>
    /// <param name="container">Declared container, e.g. "ts" or "hls". Pass "null" to clear it (an EMPTY query value binds to null and is indistinguishable from "not supplied", which silently no-opped on 2026-08-19).</param>
    /// <param name="subProtocol">Declared transcoding sub-protocol: "hls" or "http".</param>
    /// <param name="directPlay">SupportsDirectPlay.</param>
    /// <param name="directStream">SupportsDirectStream.</param>
    /// <param name="supportsTranscoding">SupportsTranscoding.</param>
    /// <param name="requiresOpening">RequiresOpening.</param>
    /// <returns>The settings now in effect.</returns>
    [HttpPost("t0/source")]
    public ActionResult T0Source(
        [FromQuery] string? baseUrl,
        [FromQuery] string? container,
        [FromQuery] string? subProtocol,
        [FromQuery] bool? directPlay,
        [FromQuery] bool? directStream,
        [FromQuery] bool? supportsTranscoding,
        [FromQuery] bool? requiresOpening)
    {
        if (baseUrl is not null)
        {
            _t0Gate.SourceBaseUrl = baseUrl;
        }

        if (container is not null)
        {
            _t0Gate.SourceContainer = string.Equals(container, "null", StringComparison.OrdinalIgnoreCase) ? null : container;
        }

        if (subProtocol is not null)
        {
            _t0Gate.SourceSubProtocol = subProtocol;
        }

        if (directPlay.HasValue)
        {
            _t0Gate.SourceSupportsDirectPlay = directPlay.Value;
        }

        if (directStream.HasValue)
        {
            _t0Gate.SourceSupportsDirectStream = directStream.Value;
        }

        if (supportsTranscoding.HasValue)
        {
            _t0Gate.SourceSupportsTranscoding = supportsTranscoding.Value;
        }

        if (requiresOpening.HasValue)
        {
            _t0Gate.SourceRequiresOpening = requiresOpening.Value;
        }

        return Ok(_t0Gate.GetSourceSettings());
    }

    /// <summary>
    /// Lists every active session's play state - spike 1 (does IsPaused/PositionTicks report for
    /// a Live TV tuner session). Tune the Movie Night channel, hit native pause on the client,
    /// and re-poll this endpoint to see whether IsPaused flips and PositionTicks tracks.
    /// </summary>
    /// <returns>One row per active session.</returns>
    [HttpGet("sessions")]
    public ActionResult GetSessions()
    {
        var rows = _sessionManager.Sessions.Select(s => new
        {
            s.Id,
            s.Client,
            s.DeviceName,
            NowPlaying = s.NowPlayingItem?.Name,
            IsPaused = s.PlayState?.IsPaused,
            PositionTicks = s.PlayState?.PositionTicks,
        });
        return Ok(rows);
    }

    /// <summary>
    /// Pushes a playstate command to a session - spike 2 (does SendPlaystateCommand get honored
    /// by a real client on the Movie Night channel). Grab a session id from
    /// <see cref="GetSessions"/> and POST here with e.g. "Pause".
    /// </summary>
    /// <param name="sessionId">Target session id, from <see cref="GetSessions"/>.</param>
    /// <param name="command">One of the <see cref="PlaystateCommand"/> enum names (e.g. Pause, Unpause).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 once the command has been sent.</returns>
    [HttpPost("playstate")]
    public async Task<ActionResult> SendPlaystate([FromQuery] string sessionId, [FromQuery] string command, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<PlaystateCommand>(command, ignoreCase: true, out var parsedCommand))
        {
            return BadRequest($"Unknown PlaystateCommand '{command}'");
        }

        await _sessionManager.SendPlaystateCommand(
            sessionId,
            sessionId,
            new PlaystateRequest { Command = parsedCommand },
            cancellationToken).ConfigureAwait(false);

        return Ok();
    }

    /// <summary>
    /// SPIKE (single-process/multi-input switcher, see planning/DECISIONS.md 2026-08-14): tune
    /// the "Movie Night (switcher spike)" channel first, then POST here to switch which of its 3
    /// static inputs is live via ffmpeg's zmq control filter - watch whether the switch is
    /// seamless through Jellyfin's own remux hop.
    /// </summary>
    /// <param name="input">Which of the 3 static inputs to switch to (0, 1, or 2).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with zmqsend's own output on success, 409 with a diagnostic message otherwise.</returns>
    [HttpPost("switcher-select")]
    public async Task<ActionResult> SwitcherSelect([FromQuery] int input, CancellationToken cancellationToken)
    {
        var (success, output) = await _broadcastManager.SendSwitcherSpikeCommandAsync(input, cancellationToken).ConfigureAwait(false);
        return success ? Ok(output) : Conflict(output);
    }

    /// <summary>
    /// Diagnostic snapshot of every encoder output directory (added 2026-08-14 for the 25fps Go
    /// Live stall - see BroadcastManager.DescribeOutputDirectories). Poll this DURING a Starting
    /// broadcast to see whether ffmpeg is actually writing segments.
    /// </summary>
    /// <returns>Per-directory existence plus file names, sizes, and last-write times.</returns>
    [HttpGet("encoder-dirs")]
    public ActionResult GetEncoderDirs() => Ok(_broadcastManager.DescribeOutputDirectories());

    /// <summary>
    /// SWITCHER V2: configures the movie the switcher spike channel should carry (feeder starts
    /// immediately from position 0; the switcher itself spawns when a client tunes the channel).
    /// </summary>
    /// <param name="itemId">Library item id of the movie.</param>
    /// <returns>200 with the resolved path, or 404.</returns>
    [HttpPost("sw2/golive")]
    public ActionResult Sw2GoLive([FromQuery] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
#pragma warning disable CA3003 // itemId is an opaque library key, same rationale as GoLiveAsync
        if (item is null || string.IsNullOrEmpty(item.Path) || !System.IO.File.Exists(item.Path))
        {
            return NotFound($"Item {itemId} not found or has no file on disk");
        }

        _broadcastManager.ConfigureSwitcherV2(item.Path, _appHost.HttpPort);
#pragma warning restore CA3003
        return Ok(new { item.Path });
    }

    /// <summary>SWITCHER V2: pause - logs movie position, swaps the feed to the slate, stops the movie feeder.</summary>
    /// <returns>200/409 with the position.</returns>
    [HttpPost("sw2/pause")]
    public async Task<ActionResult> Sw2Pause()
    {
        var (success, output) = await _broadcastManager.PauseSwitcherV2Async().ConfigureAwait(false);
        return success ? Ok(output) : Conflict(output);
    }

    /// <summary>SWITCHER V2: resume - movie feeder restarts a little before the pause position, feed swaps back.</summary>
    /// <returns>200/409 with the resume position.</returns>
    [HttpPost("sw2/resume")]
    public async Task<ActionResult> Sw2Resume()
    {
        var (success, output) = await _broadcastManager.ResumeSwitcherV2Async().ConfigureAwait(false);
        return success ? Ok(output) : Conflict(output);
    }

    /// <summary>SWITCHER V2: session status snapshot.</summary>
    /// <returns>Movie path, pause state, position, process liveness.</returns>
    [HttpGet("sw2/status")]
    public ActionResult Sw2Status() => Ok(_broadcastManager.GetSwitcherV2Status());

    /// <summary>SWITCHER V2: tear the session down (channel reverts to the color test).</summary>
    /// <returns>200.</returns>
    [HttpPost("sw2/stop")]
    public ActionResult Sw2Stop()
    {
        _broadcastManager.ClearSwitcherV2();
        return Ok();
    }

    /// <summary>
    /// Diagnostic encode probe (added 2026-08-14 for the 25fps Go Live stall - see
    /// BroadcastManager.RunEncodeProbeAsync): runs ffmpeg with the given argument list for a fixed
    /// number of seconds into a scratch directory, then reports files created + full stderr.
    /// Admin-only, isolated from broadcast state. Use "{out}" in args as the output directory
    /// placeholder.
    /// </summary>
    /// <param name="request">Argument list and duration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Files created and stderr text.</returns>
    [HttpPost("encode-probe")]
    public async Task<ActionResult> EncodeProbe([FromBody] EncodeProbeRequest request, CancellationToken cancellationToken)
    {
        if (request.Args is null || request.Args.Count == 0)
        {
            return BadRequest("args required");
        }

        var seconds = Math.Clamp(request.Seconds, 1, 120);
        var result = await _broadcastManager.RunEncodeProbeAsync(request.Args, seconds, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }
}
