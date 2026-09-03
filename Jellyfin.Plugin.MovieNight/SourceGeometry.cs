using System;
using System.Globalization;

namespace Jellyfin.Plugin.MovieNight;

/// <summary>
/// The video parameters of whatever the movie feeder emits, so the slate feeder can be
/// synthesised to match them EXACTLY.
/// <para>
/// Why this exists (2026-09-03, first live pause on real clients): the ladder encoder reads one
/// continuous feed, and pause swaps that feed from the movie to the slate. If the slate's
/// width, height, frame rate or pixel format differ from the movie's, ffmpeg tries to rebuild
/// its filter graph for the new geometry - and the QSV hardware filters cannot be rebuilt
/// mid-stream ("Impossible to convert between the formats supported by the filter
/// 'Parsed_scale_qsv_2'..."), so the encoder dies, restarts, resets the HLS sequence and
/// throws every client off. Verified offline with debug/encode-probe: a matched slate goes
/// through the seam cleanly; the old fixed 1280x720@30 slate reproduces the failure every time.
/// Sample aspect ratio is carried for correctness of the picture, but libx264 rounds it and
/// that did not trigger a reconfigure, so it is not what keeps the encoder alive.
/// </para>
/// </summary>
/// <param name="Width">Coded width in pixels.</param>
/// <param name="Height">Coded height in pixels.</param>
/// <param name="FrameRate">ffprobe r_frame_rate, e.g. "30000/1001" - passed to lavfi verbatim.</param>
/// <param name="PixelFormat">ffprobe pix_fmt, e.g. "yuv420p".</param>
/// <param name="SampleAspectRatio">ffprobe sample_aspect_ratio as "num:den", or null when unknown/square.</param>
public sealed record SourceGeometry(int Width, int Height, string FrameRate, string PixelFormat, string? SampleAspectRatio)
{
    /// <summary>
    /// The ffprobe -show_entries selector <see cref="TryParseFfprobeCsv"/> pairs with. NOTE: ffprobe's
    /// CSV output is in ITS canonical field order regardless of the order requested here - measured
    /// on the NAS 2026-09-03 as width, height, sample_aspect_ratio, pix_fmt, r_frame_rate. The
    /// selector is written in that order so the two cannot drift apart by reading this constant.
    /// </summary>
    public const string FfprobeEntries = "stream=width,height,sample_aspect_ratio,pix_fmt,r_frame_rate";

    /// <summary>Gets the lavfi size string, e.g. "1080x720".</summary>
    public string LavfiSize => string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height}");

    /// <summary>Gets the setsar filter argument ("853/720"), or null when no SAR needs setting.</summary>
    public string? SetSarArgument => SampleAspectRatio?.Replace(':', '/');

    /// <summary>
    /// Parses one line of <c>ffprobe -select_streams v:0 -show_entries {FfprobeEntries} -of csv=p=0</c>.
    /// </summary>
    /// <param name="csvLine">The first non-empty line of ffprobe's stdout.</param>
    /// <param name="geometry">The parsed geometry.</param>
    /// <returns>False when the line is not usable.</returns>
    public static bool TryParseFfprobeCsv(string? csvLine, out SourceGeometry? geometry)
    {
        geometry = null;
        if (string.IsNullOrWhiteSpace(csvLine))
        {
            return false;
        }

        // Canonical ffprobe order: width, height, sample_aspect_ratio, pix_fmt, r_frame_rate.
        // A raw MKV printed a trailing empty field ("...,30000/1001,"), so only the first five count.
        var parts = csvLine.Trim().Split(',');
        if (parts.Length < 5
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
            || width <= 0 || height <= 0)
        {
            return false;
        }

        var pixFmt = parts[3].Trim();
        var frameRate = parts[4].Trim();
        if (string.IsNullOrEmpty(pixFmt) || string.IsNullOrEmpty(frameRate) || frameRate == "0/0")
        {
            return false;
        }

        // "0:1" and "N/A" both mean "unknown/square" to ffprobe; treat them as no SAR.
        var sar = parts[2].Trim();
        if (string.IsNullOrEmpty(sar) || sar == "N/A" || sar == "0:1" || sar == "1:1")
        {
            sar = null;
        }

        geometry = new SourceGeometry(width, height, frameRate, pixFmt, sar);
        return true;
    }

    /// <inheritdoc />
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height} @{FrameRate} {PixelFormat}{(SampleAspectRatio is null ? string.Empty : " sar " + SampleAspectRatio)}");
}
