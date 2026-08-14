using System;

namespace Jellyfin.Plugin.MovieNight;

/// <summary>
/// A snapshot of the current broadcast state, for the status API.
/// </summary>
/// <param name="State">The current state.</param>
/// <param name="ChannelName">The configured channel name, if a broadcast has been started.</param>
/// <param name="NowPlaying">The display name of the item being broadcast, if any.</param>
/// <param name="StartedAtUtc">When the broadcast transitioned to <see cref="BroadcastState.Live"/>.</param>
/// <param name="RunTimeTicks">The source item's runtime, if known - used to size the EPG programme block.</param>
/// <param name="LastFailureReason">A human-readable reason for the most recent <see cref="BroadcastState.Failed"/> transition.</param>
public sealed record BroadcastStatus(
    BroadcastState State,
    string? ChannelName,
    string? NowPlaying,
    DateTime? StartedAtUtc,
    long? RunTimeTicks,
    string? LastFailureReason);
