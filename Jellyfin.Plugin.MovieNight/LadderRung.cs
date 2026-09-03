namespace Jellyfin.Plugin.MovieNight;

/// <summary>
/// One rung of the live ladder.
/// </summary>
/// <param name="Height">Output height in pixels (width follows the source aspect).</param>
/// <param name="VideoKbps">Video bitrate, kbps (CBR-ish: maxrate = bitrate, bufsize = 2x).</param>
/// <param name="AudioKbps">AAC audio bitrate, kbps.</param>
public sealed record LadderRung(int Height, int VideoKbps, int AudioKbps);
