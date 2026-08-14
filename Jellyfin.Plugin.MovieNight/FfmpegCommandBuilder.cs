using System.Collections.Generic;
using System.IO;

namespace Jellyfin.Plugin.MovieNight;

/// <summary>
/// The hardware acceleration types this plugin knows how to target. Mirrors the subset of
/// <c>HardwareAccelerationType</c> spec §5.3 names an encoder for; anything else (v4l2m2m,
/// videotoolbox, rkmpp) falls back to software.
/// </summary>
public enum HardwareAccel
{
    /// <summary>Software encode via libx264 (also the forced fallback for unsupported/unknown accel types).</summary>
    None,

    /// <summary>Intel Quick Sync Video.</summary>
    Qsv,

    /// <summary>VA-API (Intel/AMD on Linux).</summary>
    Vaapi,

    /// <summary>NVIDIA NVENC.</summary>
    Nvenc,

    /// <summary>AMD AMF.</summary>
    Amf,
}

/// <summary>
/// Builds the ffmpeg argument list for broadcasting a movie to HLS. Returns discrete argv entries
/// (for <c>ProcessStartInfo.ArgumentList</c>) rather than a manually-quoted string: paths (movie
/// files, HLS output dirs) come from the user's own library and can contain spaces or other
/// shell-significant characters that a hand-quoted string would mishandle. Phase 2: single "Low"
/// rung only per spec §5.2 (720p @ 2.5 Mbps H.264 High profile, AAC-LC 96k stereo) - the multi-rung
/// ABR ladder is Phase 4. Pure list building, no process execution, so it's unit-testable without
/// ffmpeg installed.
/// </summary>
public static class FfmpegCommandBuilder
{
    /// <summary>
    /// Builds the full ffmpeg argument list (everything after the binary path).
    /// </summary>
    /// <param name="inputFile">Path to the source movie file.</param>
    /// <param name="outputDir">Directory to write the HLS playlist and segments into.</param>
    /// <param name="accel">Hardware acceleration type, from the server's own configured encoding options.</param>
    /// <param name="vaapiDevice">The VA-API render device path (e.g. <c>/dev/dri/renderD128</c>), required when <paramref name="accel"/> is <see cref="HardwareAccel.Vaapi"/>.</param>
    /// <param name="startPositionSeconds">Input seek offset (ffmpeg <c>-ss</c>) - used to resume from where a paused broadcast left off (spike 5, universal pause). 0 for a normal Go Live.</param>
    /// <param name="startNumber">First HLS segment number (ffmpeg <c>-start_number</c>) - used so a resumed encode doesn't overwrite/collide with segment files already sitting in <paramref name="outputDir"/> from before the pause. 0 for a normal Go Live.</param>
    /// <returns>The argument list, ready to assign to <c>ProcessStartInfo.ArgumentList</c>.</returns>
    public static IReadOnlyList<string> Build(string inputFile, string outputDir, HardwareAccel accel, string? vaapiDevice = null, double startPositionSeconds = 0, int startNumber = 0)
    {
        var args = new List<string>();
        if (startPositionSeconds > 0)
        {
            args.AddRange(["-ss", startPositionSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)]);
        }

        args.AddRange(["-re", "-i", inputFile]);

        AddVideoArgs(args, accel, vaapiDevice);

        args.AddRange(["-c:a", "aac", "-b:a", "96k", "-ac", "2"]);
        args.AddRange(["-force_key_frames", "expr:gte(t,n_forced*2)"]);
        args.AddRange([
            "-f", "hls",
            "-hls_time", "4",
            "-hls_list_size", "10",
            "-hls_flags", "delete_segments+independent_segments",
            "-start_number", startNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-hls_segment_filename", Path.Combine(outputDir, "segment_%03d.ts"),
            Path.Combine(outputDir, "master.m3u8"),
        ]);

        return args;
    }

    private static void AddVideoArgs(List<string> args, HardwareAccel accel, string? vaapiDevice)
    {
        switch (accel)
        {
            case HardwareAccel.Qsv:
                // Software decode hands scale_qsv system-memory frames it can't read - hwupload
                // first, same reason the VAAPI branch below does it. Root-caused via a live
                // failure: "Impossible to convert between the formats supported by the filter
                // ... and the filter 'auto_scale_0'" with a bare scale_qsv filter.
                // scale_qsv also rejects the round-to-even "-2" width libavfilter's software
                // scale/scale_vaapi accept ("Size values less than -1 are not acceptable",
                // root-caused via a second live failure) - QSV only accepts -1.
                args.AddRange(["-init_hw_device", "qsv=hw", "-filter_hw_device", "hw", "-c:v", "h264_qsv", "-preset", "veryfast", "-vf", "format=nv12,hwupload=extra_hw_frames=64,scale_qsv=-1:720"]);
                break;
            case HardwareAccel.Vaapi when !string.IsNullOrEmpty(vaapiDevice):
                args.AddRange(["-init_hw_device", $"vaapi=hw:{vaapiDevice}", "-filter_hw_device", "hw", "-c:v", "h264_vaapi", "-vf", "format=nv12,hwupload,scale_vaapi=-2:720"]);
                break;
            case HardwareAccel.Nvenc:
                args.AddRange(["-c:v", "h264_nvenc", "-preset", "p4", "-vf", "scale=-2:720"]);
                break;
            case HardwareAccel.Amf:
                args.AddRange(["-c:v", "h264_amf", "-vf", "scale=-2:720"]);
                break;
            default:
                args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-vf", "scale=-2:720"]);
                break;
        }

        args.AddRange(["-profile:v", "high", "-level", "4.0", "-b:v", "2500k", "-maxrate", "2500k", "-bufsize", "5000k"]);
    }
}
