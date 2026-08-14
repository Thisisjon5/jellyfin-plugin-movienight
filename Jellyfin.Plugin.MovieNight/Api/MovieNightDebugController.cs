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
    /// <param name="broadcastManager">Used to swap the running encoder for the freeze/unfreeze spike.</param>
    public MovieNightDebugController(ISessionManager sessionManager, BroadcastManager broadcastManager)
    {
        _sessionManager = sessionManager;
        _broadcastManager = broadcastManager;
    }

    /// <summary>
    /// Spike 5: pauses by splicing a background filler encoder into the served playlist with
    /// EXT-X-DISCONTINUITY (see planning/DECISIONS.md "mechanism replaced: filler-channel
    /// splice") - replaces spike 4's SIGSTOP approach, which worked mechanically but desynced
    /// across clients due to Jellyfin's own remux hop.
    /// </summary>
    /// <returns>200 if the splice to filler succeeded, 409 if the broadcast isn't Live or is already paused.</returns>
    [HttpPost("freeze")]
    public async Task<ActionResult> Freeze()
    {
        var ok = await _broadcastManager.Pause().ConfigureAwait(false);
        return ok ? Ok() : Conflict();
    }

    /// <summary>
    /// Spike 5 counterpart: kills the filler, respawns the movie a little before the pause point,
    /// and splices back once it's confirmed flowing.
    /// </summary>
    /// <returns>200 if the splice back to the movie succeeded, 409 if the broadcast isn't paused.</returns>
    [HttpPost("unfreeze")]
    public async Task<ActionResult> Unfreeze()
    {
        var ok = await _broadcastManager.Resume().ConfigureAwait(false);
        return ok ? Ok() : Conflict();
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
}
