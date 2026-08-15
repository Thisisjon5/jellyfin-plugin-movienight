using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="MovieNightDebugController"/> class.
    /// </summary>
    /// <param name="sessionManager">Used to read session play state and push playstate commands.</param>
    /// <param name="broadcastManager">Used to drive the switcher spike.</param>
    public MovieNightDebugController(ISessionManager sessionManager, BroadcastManager broadcastManager)
    {
        _sessionManager = sessionManager;
        _broadcastManager = broadcastManager;
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
