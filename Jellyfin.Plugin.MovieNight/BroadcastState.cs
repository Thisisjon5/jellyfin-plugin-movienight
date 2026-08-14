namespace Jellyfin.Plugin.MovieNight;

/// <summary>
/// Broadcast lifecycle state, per spec §5.4.
/// </summary>
public enum BroadcastState
{
    /// <summary>No broadcast running.</summary>
    Idle,

    /// <summary>ffmpeg spawned, waiting for the first HLS segments to appear.</summary>
    Starting,

    /// <summary>Broadcasting normally.</summary>
    Live,

    /// <summary>ffmpeg exited 0 (source file played to completion).</summary>
    Ended,

    /// <summary>ffmpeg crashed, failed to start, timed out starting, or was killed by the watchdog.</summary>
    Failed,
}
