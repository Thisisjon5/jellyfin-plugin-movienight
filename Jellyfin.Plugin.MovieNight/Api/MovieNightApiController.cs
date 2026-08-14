using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MovieNight.Api;

/// <summary>
/// Admin-only control API: status polling, Go Live, Stop. Independently requires admin auth
/// (never trusts that only the dashboard config page links here) per spec §7.
/// </summary>
[ApiController]
[Route("MovieNight/api")]
[Authorize(Policy = "RequiresElevation")]
public class MovieNightApiController : ControllerBase
{
    private readonly BroadcastManager _broadcastManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="MovieNightApiController"/> class.
    /// </summary>
    /// <param name="broadcastManager">The broadcast state machine this API controls.</param>
    public MovieNightApiController(BroadcastManager broadcastManager)
    {
        _broadcastManager = broadcastManager;
    }

    /// <summary>
    /// Gets a snapshot of the current broadcast state. Polled by the config page UI.
    /// </summary>
    /// <returns>The current <see cref="BroadcastStatus"/>.</returns>
    [HttpGet("status")]
    public ActionResult<BroadcastStatus> GetStatus() => _broadcastManager.GetStatus();

    /// <summary>
    /// Starts broadcasting the requested item. Idempotent-ish: rejected (no-op) if a broadcast is
    /// already starting or live - call <see cref="Stop"/> first to switch movies.
    /// </summary>
    /// <param name="request">Which item to broadcast, under which channel name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the resulting status if the broadcast went live, 409 if it didn't.</returns>
    [HttpPost("golive")]
    public async Task<ActionResult<BroadcastStatus>> GoLive([FromBody] GoLiveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var channelName = string.IsNullOrWhiteSpace(request.ChannelName) ? "Movie Night" : request.ChannelName;
        var wentLive = await _broadcastManager.GoLiveAsync(request.ItemId, channelName, cancellationToken).ConfigureAwait(false);
        var status = _broadcastManager.GetStatus();
        return wentLive ? Ok(status) : Conflict(status);
    }

    /// <summary>
    /// Stops the current broadcast, if any.
    /// </summary>
    /// <returns>The resulting (idle) status.</returns>
    [HttpPost("stop")]
    public async Task<ActionResult<BroadcastStatus>> Stop()
    {
        await _broadcastManager.StopAsync().ConfigureAwait(false);
        return Ok(_broadcastManager.GetStatus());
    }

    /// <summary>
    /// Pauses the live broadcast for every viewer - splices the served output to the always-warm
    /// filler encoder (see <see cref="BroadcastManager.Pause"/>). The real, user-facing version of
    /// what was previously only reachable via the debug controller.
    /// </summary>
    /// <returns>The resulting status, 409 if the broadcast isn't live or is already paused.</returns>
    [HttpPost("pause")]
    public async Task<ActionResult<BroadcastStatus>> Pause()
    {
        var ok = await _broadcastManager.Pause().ConfigureAwait(false);
        return ok ? Ok(_broadcastManager.GetStatus()) : Conflict(_broadcastManager.GetStatus());
    }

    /// <summary>
    /// Resumes a paused broadcast for every viewer (see <see cref="BroadcastManager.Resume"/>).
    /// </summary>
    /// <returns>The resulting status, 409 if the broadcast isn't paused.</returns>
    [HttpPost("resume")]
    public async Task<ActionResult<BroadcastStatus>> Resume()
    {
        var ok = await _broadcastManager.Resume().ConfigureAwait(false);
        return ok ? Ok(_broadcastManager.GetStatus()) : Conflict(_broadcastManager.GetStatus());
    }

    /// <summary>
    /// Forces an immediate guide refresh - called after the admin toggles which spike test
    /// channels are enabled in plugin config, so the channel list picks up the change right away
    /// instead of waiting for the next automatic refresh.
    /// </summary>
    /// <returns>200 once the refresh has been triggered.</returns>
    [HttpPost("refresh-channels")]
    public ActionResult RefreshChannels()
    {
        _broadcastManager.RefreshChannelsGuide();
        return Ok();
    }
}
