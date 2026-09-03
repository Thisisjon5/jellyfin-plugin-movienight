namespace Jellyfin.Plugin.MovieNight;

/// <summary>
/// What to burn in.
/// </summary>
/// <param name="SourcePath">File the <c>subtitles</c> filter reads: the movie itself for embedded text tracks, the sidecar file for external ones.</param>
/// <param name="RelativeSubtitleIndex">Index among the movie's embedded subtitle streams (the filter's <c>si</c>), null for external files or bitmap tracks.</param>
/// <param name="AbsoluteStreamIndex">The stream's absolute index, used to address bitmap tracks for overlay.</param>
/// <param name="IsBitmap">True for PGS/DVD/DVB bitmap subtitles (overlay), false for text (subtitles filter).</param>
public sealed record SubtitleBurnSpec(string SourcePath, int? RelativeSubtitleIndex, int AbsoluteStreamIndex, bool IsBitmap);
