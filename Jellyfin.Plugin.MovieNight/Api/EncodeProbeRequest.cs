using System.Collections.Generic;

namespace Jellyfin.Plugin.MovieNight.Api;

/// <summary>
/// Request body for <see cref="MovieNightDebugController.EncodeProbe"/>.
/// </summary>
public class EncodeProbeRequest
{
    /// <summary>Gets or sets the full ffmpeg argument list ("{out}" = scratch output dir placeholder).</summary>
    public IReadOnlyList<string>? Args { get; set; }

    /// <summary>Gets or sets how many seconds to let ffmpeg run (default 15).</summary>
    public int Seconds { get; set; } = 15;
}
