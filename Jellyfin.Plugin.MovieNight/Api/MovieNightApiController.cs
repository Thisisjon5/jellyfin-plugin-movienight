using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MovieNight.Api;

/// <summary>
/// Admin-only control API for the host page. Independently requires admin auth (never trusts that
/// only the dashboard config page links here) per spec §7.
/// <para>
/// <c>live/*</c> drives the 0.4 pipeline (ROADMAP-2026-09-03.md): v3 feeders into the ladder
/// encoder, served through the custom tuner host. <c>prepare*</c> and <c>library/*</c> back the
/// movie picker and the mezzanine/caption prep. The older <c>golive</c>/<c>stop</c>/<c>pause</c>/
/// <c>resume</c> drive the legacy HLS path and stay until its deletion is ruled (roadmap-08-16 §3).
/// </para>
/// </summary>
[ApiController]
[Route("MovieNight/api")]
[Authorize(Policy = "RequiresElevation")]
public class MovieNightApiController : ControllerBase
{
    private readonly BroadcastManager _broadcastManager;
    private readonly LiveSession _liveSession;
    private readonly MezzaninePrep _prep;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="MovieNightApiController"/> class.
    /// </summary>
    /// <param name="broadcastManager">The legacy broadcast state machine.</param>
    /// <param name="liveSession">The live (ladder) broadcast.</param>
    /// <param name="prep">Mezzanine preparation.</param>
    /// <param name="libraryManager">Movie search for the picker.</param>
    public MovieNightApiController(BroadcastManager broadcastManager, LiveSession liveSession, MezzaninePrep prep, ILibraryManager libraryManager)
    {
        _broadcastManager = broadcastManager;
        _liveSession = liveSession;
        _prep = prep;
        _libraryManager = libraryManager;
    }

    // ---------------------------------------------------------------- live (0.4 pipeline)

    /// <summary>Snapshot of the live broadcast: session, feed, encoder, and who is fetching the ladder.</summary>
    /// <returns>Status object.</returns>
    [HttpGet("live/status")]
    public ActionResult GetLiveStatus() => Ok(_liveSession.GetStatus());

    /// <summary>
    /// Goes live with a movie. Uses the prepared mezzanine if one exists (captions burned in,
    /// cheap decode), otherwise the original file. Returns once the first segment exists.
    /// </summary>
    /// <param name="itemId">Library item id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with status, 409 with the failure reason.</returns>
    [HttpPost("live/golive")]
    public async Task<ActionResult> LiveGoLive([FromQuery] Guid itemId, CancellationToken cancellationToken)
    {
        var (success, message) = await _liveSession.GoLiveAsync(itemId, cancellationToken).ConfigureAwait(false);
        var body = new { Success = success, Message = message, Status = _liveSession.GetStatus() };
        return success ? Ok(body) : Conflict(body);
    }

    /// <summary>Stops the live broadcast and withdraws the channel.</summary>
    /// <returns>200 with status.</returns>
    [HttpPost("live/stop")]
    public async Task<ActionResult> LiveStop()
    {
        await _liveSession.StopAsync().ConfigureAwait(false);
        return Ok(_liveSession.GetStatus());
    }

    /// <summary>Pauses for everyone (slate).</summary>
    /// <returns>200/409 with the position.</returns>
    [HttpPost("live/pause")]
    public async Task<ActionResult> LivePause()
    {
        var (success, output) = await _liveSession.PauseAsync().ConfigureAwait(false);
        return success ? Ok(new { Message = output }) : Conflict(new { Message = output });
    }

    /// <summary>Resumes for everyone, a little before the pause point.</summary>
    /// <returns>200/409 with the position.</returns>
    [HttpPost("live/resume")]
    public async Task<ActionResult> LiveResume()
    {
        var (success, output) = await _liveSession.ResumeAsync().ConfigureAwait(false);
        return success ? Ok(new { Message = output }) : Conflict(new { Message = output });
    }

    // ---------------------------------------------------------------- library / prepare

    /// <summary>
    /// Movies for the picker: name search, first 50 by sort name, with whether a mezzanine exists.
    /// </summary>
    /// <param name="search">Optional substring to match against names.</param>
    /// <returns>Movie rows.</returns>
    [HttpGet("library/movies")]
    public ActionResult GetMovies([FromQuery] string? search)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            Recursive = true,
            IsVirtualItem = false,
            Limit = 50,
            OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)],
        };
        if (!string.IsNullOrWhiteSpace(search))
        {
            query.SearchTerm = search.Trim();
        }

        var rows = _libraryManager.GetItemList(query)
            .Where(i => !string.IsNullOrEmpty(i.Path))
            .Select(i => new
            {
                i.Id,
                i.Name,
                i.ProductionYear,
                i.RunTimeTicks,
                i.Path,
                HasMezzanine = _prep.GetMezzaninePath(i.Id) is not null,
            });
        return Ok(rows);
    }

    /// <summary>Subtitle streams of a movie, for the burn-in dropdown.</summary>
    /// <param name="itemId">Library item id.</param>
    /// <returns>Subtitle rows, or 404.</returns>
    [HttpGet("library/{itemId}/subtitles")]
    public ActionResult GetSubtitles([FromRoute] Guid itemId)
    {
        var rows = _prep.ListSubtitleStreams(itemId);
        return rows is null ? NotFound() : Ok(rows);
    }

    /// <summary>
    /// Starts preparing a movie's mezzanine (720p h264/AAC mp4, optional burned-in captions). One
    /// job at a time; poll <c>prepare/status</c>.
    /// </summary>
    /// <param name="itemId">Library item id.</param>
    /// <param name="subtitleStreamIndex">Absolute stream index of the subtitle track to burn in, omitted for none.</param>
    /// <returns>200/409 with a message.</returns>
    [HttpPost("prepare")]
    public ActionResult Prepare([FromQuery] Guid itemId, [FromQuery] int? subtitleStreamIndex)
    {
        var (success, message) = _prep.Start(itemId, subtitleStreamIndex);
        return success ? Ok(new { Message = message }) : Conflict(new { Message = message });
    }

    /// <summary>Current/last prep job plus every prepared file on disk.</summary>
    /// <returns>Status object.</returns>
    [HttpGet("prepare/status")]
    public ActionResult GetPrepareStatus() => Ok(_prep.GetStatus());

    /// <summary>Cancels the running prep job, discarding partial output.</summary>
    /// <returns>200.</returns>
    [HttpPost("prepare/cancel")]
    public ActionResult CancelPrepare()
    {
        _prep.Cancel();
        return Ok();
    }

    /// <summary>Deletes a prepared mezzanine.</summary>
    /// <param name="itemId">Library item id.</param>
    /// <returns>200 if deleted, 404 if none existed.</returns>
    [HttpDelete("prepare/{itemId}")]
    public ActionResult DeletePrepared([FromRoute] Guid itemId)
        => _prep.Delete(itemId) ? Ok() : NotFound();

    // ---------------------------------------------------------------- legacy HLS path

    /// <summary>
    /// Gets a snapshot of the legacy broadcast state.
    /// </summary>
    /// <returns>The current <see cref="BroadcastStatus"/>.</returns>
    [HttpGet("status")]
    public ActionResult<BroadcastStatus> GetStatus() => _broadcastManager.GetStatus();

    /// <summary>
    /// Legacy path: starts broadcasting the requested item through the per-client HLS route.
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

    /// <summary>Legacy path: stops the current broadcast, if any.</summary>
    /// <returns>The resulting (idle) status.</returns>
    [HttpPost("stop")]
    public async Task<ActionResult<BroadcastStatus>> Stop()
    {
        await _broadcastManager.StopAsync().ConfigureAwait(false);
        return Ok(_broadcastManager.GetStatus());
    }

    /// <summary>Legacy path: pauses via the filler splice.</summary>
    /// <returns>The resulting status, 409 if the broadcast isn't live or is already paused.</returns>
    [HttpPost("pause")]
    public async Task<ActionResult<BroadcastStatus>> Pause()
    {
        var ok = await _broadcastManager.Pause().ConfigureAwait(false);
        return ok ? Ok(_broadcastManager.GetStatus()) : Conflict(_broadcastManager.GetStatus());
    }

    /// <summary>Legacy path: resumes a paused broadcast.</summary>
    /// <returns>The resulting status, 409 if the broadcast isn't paused.</returns>
    [HttpPost("resume")]
    public async Task<ActionResult<BroadcastStatus>> Resume()
    {
        var ok = await _broadcastManager.Resume().ConfigureAwait(false);
        return ok ? Ok(_broadcastManager.GetStatus()) : Conflict(_broadcastManager.GetStatus());
    }

    /// <summary>
    /// Forces an immediate guide refresh - after the admin changes settings that affect the
    /// channel list, so it updates within seconds instead of at the next automatic refresh.
    /// </summary>
    /// <returns>200 once the refresh has been triggered.</returns>
    [HttpPost("refresh-channels")]
    public ActionResult RefreshChannels()
    {
        _broadcastManager.RefreshChannelsGuide();
        return Ok();
    }
}
