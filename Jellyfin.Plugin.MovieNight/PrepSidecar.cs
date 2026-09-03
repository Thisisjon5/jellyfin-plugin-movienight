using System;

namespace Jellyfin.Plugin.MovieNight;

/// <summary>Persisted next to each prepared mezzanine mp4, describing how it was made.</summary>
public sealed class PrepSidecar
{
    /// <summary>Gets or sets the movie's name at prep time.</summary>
    public string? ItemName { get; set; }

    /// <summary>Gets or sets the burned-in subtitle's absolute stream index, or null.</summary>
    public int? SubtitleStreamIndex { get; set; }

    /// <summary>Gets or sets the burned-in subtitle's display title, or null.</summary>
    public string? SubtitleTitle { get; set; }

    /// <summary>Gets or sets the encoder backend used.</summary>
    public string? Accel { get; set; }

    /// <summary>Gets or sets when the file was produced.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>Gets or sets how many streams the source had (diagnostic).</summary>
    public int SourceStreamCount { get; set; }
}
