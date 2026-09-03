namespace Jellyfin.Plugin.MovieNight;

/// <summary>
/// One rung of the live ladder. Output geometry is FIXED per rung (the source is scaled to fit and
/// letterboxed/pillarboxed to exactly this size), because the feed changes resolution and aspect
/// at every slate seam and an open encoder cannot take new frame dimensions.
/// </summary>
/// <param name="Width">Output width in pixels.</param>
/// <param name="Height">Output height in pixels.</param>
/// <param name="VideoKbps">Video bitrate, kbps (CBR-ish: maxrate = bitrate, bufsize = 2x).</param>
/// <param name="AudioKbps">AAC audio bitrate, kbps.</param>
public sealed record LadderRung(int Width, int Height, int VideoKbps, int AudioKbps);
