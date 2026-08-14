using System;

namespace Jellyfin.Plugin.MovieNight.Api;

/// <summary>
/// Request body for <c>POST /MovieNight/api/golive</c>.
/// </summary>
/// <param name="ItemId">The library item id of the movie to broadcast.</param>
/// <param name="ChannelName">The channel name to use for this broadcast (used in the M3U and EPG).</param>
public sealed record GoLiveRequest(Guid ItemId, string ChannelName);
