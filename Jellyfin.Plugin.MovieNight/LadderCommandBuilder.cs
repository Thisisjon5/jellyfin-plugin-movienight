using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.MovieNight;

/// <summary>
/// Builds the ffmpeg argument list for the live ladder encoder: ONE process reading
/// <c>feed.ts</c>, N encoded rungs out through <c>var_stream_map</c>, sliding-window HLS.
/// <para>
/// Every rung is encoded - there is no <c>-c copy</c> rung. That is the consequence of Jon's
/// 2026-09-03 ruling that the rung count is a setting (ROADMAP-2026-09-03.md §3): N=1 is then
/// the same code path as N=3, and GOP alignment across rungs is intrinsic to sharing one decoded
/// frame stream and one keyframe cadence, so the mezzanine no longer has to carry a matching
/// fixed GOP (the constraint T2 found on the copy rung).
/// </para>
/// <para>
/// <b>The seam rule (0.4.1, from the first live test):</b> the feed changes resolution, aspect,
/// pixel format and frame rate at every pause and resume (film ↔ slate). ffmpeg rebuilds the
/// filter graph on such a change, but an already-open encoder cannot accept new frame
/// dimensions, and on QSV a rebuilt <c>hwupload</c> hands the encoder a new hardware frames
/// context - the encoder died at the first pause. So every rung's chain scales to FIT and pads
/// to an exact fixed size in system memory, ends in a fixed pixel format, and hardware encoders
/// take system-memory frames directly. Nothing the encoder sees can change mid-stream.
/// </para>
/// Pure list building, no process execution - unit-tested per backend.
/// </summary>
public static class LadderCommandBuilder
{
    /// <summary>Default segment duration in seconds. Also the keyframe cadence on every rung.</summary>
    public const int DefaultSegmentSeconds = 4;

    /// <summary>Segments kept in each rung's playlist (6 × 4 s = a 24 s window; players start ~3 from the end).</summary>
    public const int WindowSegments = 6;

    /// <summary>Maximum rung count the setting accepts.</summary>
    public const int MaxRungs = 3;

    /// <summary>HLS flags for a fresh Go Live.</summary>
    private const string HlsFlags = "delete_segments+independent_segments+program_date_time";

    /// <summary>Rung presets below the top rung, in descending order.</summary>
    private static readonly LadderRung[] LowerRungs =
    [
        new(854, 480, 1500, 96),
        new(640, 360, 800, 64),
    ];

    /// <summary>
    /// Builds the rung table for a given rung count and top-rung bitrate. The top rung is always
    /// 1280x720 (Jon's ruling, 2026-09-03).
    /// </summary>
    /// <param name="rungCount">1-3; clamped.</param>
    /// <param name="topRungKbps">Top rung video bitrate in kbps; clamped to 500-20000.</param>
    /// <returns>The rungs, top first.</returns>
    public static IReadOnlyList<LadderRung> PlanRungs(int rungCount, int topRungKbps)
    {
        var count = Math.Clamp(rungCount, 1, MaxRungs);
        var top = Math.Clamp(topRungKbps, 500, 20000);
        var rungs = new List<LadderRung> { new(1280, 720, top, 128) };
        rungs.AddRange(LowerRungs.Take(count - 1));
        return rungs;
    }

    /// <summary>
    /// Builds the full ffmpeg argument list (everything after the binary path).
    /// </summary>
    /// <param name="feedUrl">Loopback URL of <c>feed.ts</c>.</param>
    /// <param name="outputDir">Ladder directory; rung <c>i</c> writes into <c>v{i}/</c> below it (the caller creates those - the HLS muxer will not).</param>
    /// <param name="rungs">Rung table from <see cref="PlanRungs"/>.</param>
    /// <param name="accel">Encoder backend, from the server's own hardware-acceleration setting.</param>
    /// <param name="vaapiDevice">VA-API render device, required for <see cref="HardwareAccel.Vaapi"/>.</param>
    /// <param name="segmentSeconds">Segment duration and keyframe cadence, 2-6 s; clamped.</param>
    /// <param name="hardwareScale">QSV only: use the T2-era <c>hwupload</c> + <c>scale_qsv</c> chain instead of software scale/pad. A/B knob; it cannot letterbox and is what died at the first seam.</param>
    /// <returns>The argument list, ready for <c>ProcessStartInfo.ArgumentList</c>.</returns>
    public static IReadOnlyList<string> Build(string feedUrl, string outputDir, IReadOnlyList<LadderRung> rungs, HardwareAccel accel, string? vaapiDevice = null, int segmentSeconds = DefaultSegmentSeconds, bool hardwareScale = false)
    {
        ArgumentNullException.ThrowIfNull(rungs);
        if (rungs.Count == 0)
        {
            throw new ArgumentException("At least one rung is required", nameof(rungs));
        }

        var seconds = Math.Clamp(segmentSeconds, 2, 6);

        // VAAPI without a device path cannot init a hw context; fall back to software rather than
        // spawn something that dies on its first frame. Same rule FfmpegCommandBuilder applies.
        if (accel == HardwareAccel.Vaapi && string.IsNullOrEmpty(vaapiDevice))
        {
            accel = HardwareAccel.None;
        }

        var args = new List<string> { "-nostdin", "-hide_banner" };

        switch (accel)
        {
            case HardwareAccel.Qsv:
                // The device is still declared so h264_qsv binds to it; without -filter_hw_device
                // (software-scale mode) no filter touches it and frames reach the encoder in
                // system memory, which h264_qsv uploads itself.
                args.AddRange(hardwareScale
                    ? ["-init_hw_device", "qsv=hw", "-filter_hw_device", "hw"]
                    : ["-init_hw_device", "qsv=hw"]);
                break;
            case HardwareAccel.Vaapi:
                args.AddRange(["-init_hw_device", $"vaapi=hw:{vaapiDevice}", "-filter_hw_device", "hw"]);
                break;
        }

        // genpts because each feeder swap (pause -> slate -> resume) starts a fresh mpegts
        // timeline behind feed.ts; ffmpeg's demuxer-level discontinuity compensation then keeps
        // the output monotonic. This is the v3 persistent-process idiom (v0.3.25/v0.3.28).
        args.AddRange(["-fflags", "+genpts", "-i", feedUrl]);

        args.AddRange(["-filter_complex", BuildFilterGraph(rungs, accel, hardwareScale)]);

        for (var i = 0; i < rungs.Count; i++)
        {
            var rung = rungs[i];
            var v = FormattableString.Invariant($"v:{i}");
            var a = FormattableString.Invariant($"a:{i}");
            args.AddRange(["-map", FormattableString.Invariant($"[v{i}]"), "-map", "0:a:0"]);
            AddVideoEncoderArgs(args, v, rung, accel, seconds);
            args.AddRange([
                $"-c:{a}", "aac",
                $"-b:{a}", FormattableString.Invariant($"{rung.AudioKbps}k"),
                $"-ac:{a}", "2",
                $"-ar:{a}", "48000",
            ]);
        }

        var streamMap = string.Join(' ', Enumerable.Range(0, rungs.Count).Select(i => FormattableString.Invariant($"v:{i},a:{i}")));

        args.AddRange([
            "-f", "hls",
            "-var_stream_map", streamMap,
            "-hls_time", seconds.ToString(CultureInfo.InvariantCulture),
            "-hls_list_size", WindowSegments.ToString(CultureInfo.InvariantCulture),
            "-hls_delete_threshold", "2",
            "-hls_flags", HlsFlags,
            "-hls_segment_type", "mpegts",
            "-master_pl_name", "master.m3u8",
            "-hls_segment_filename", Path.Combine(outputDir, "v%v", "seg_%d.ts"),
            Path.Combine(outputDir, "v%v", "index.m3u8"),
        ]);

        return args;
    }

    /// <summary>
    /// Adapts a <see cref="Build"/> argument list for a crash restart into the same directory:
    /// segment numbering and the playlist media sequence continue from
    /// <paramref name="startNumber"/> instead of resetting to 0 (a reset makes every HLS client
    /// abandon the stream), and a discontinuity is declared before the first new segment because
    /// the new process starts a fresh timeline.
    /// </summary>
    /// <param name="args">The original argument list.</param>
    /// <param name="startNumber">First segment number the restarted process should write.</param>
    /// <returns>A new argument list.</returns>
    public static IReadOnlyList<string> WithRestartContinuity(IReadOnlyList<string> args, int startNumber)
    {
        ArgumentNullException.ThrowIfNull(args);
        var result = new List<string>(args.Count + 2);
        for (var i = 0; i < args.Count; i++)
        {
            if (i > 0 && args[i - 1] == "-hls_flags")
            {
                result.Add(args[i] + "+discont_start");
                continue;
            }

            if (i == args.Count - 1)
            {
                result.Add("-start_number");
                result.Add(Math.Max(0, startNumber).ToString(CultureInfo.InvariantCulture));
            }

            result.Add(args[i]);
        }

        return result;
    }

    /// <summary>
    /// Builds the filter graph: one split feeding one fixed-geometry chain per rung, each ending in
    /// <c>[v{i}]</c>. For a single rung there is no split.
    /// </summary>
    /// <param name="rungs">Rung table.</param>
    /// <param name="accel">Backend - decides the pixel format the chain ends in and whether frames are uploaded.</param>
    /// <param name="hardwareScale">QSV only: the legacy hardware chain.</param>
    /// <returns>The filter_complex string.</returns>
    public static string BuildFilterGraph(IReadOnlyList<LadderRung> rungs, HardwareAccel accel, bool hardwareScale = false)
    {
        ArgumentNullException.ThrowIfNull(rungs);
        var graph = new StringBuilder();
        if (rungs.Count == 1)
        {
            graph.Append("[0:v]").Append(ScaleChain(rungs[0], accel, hardwareScale)).Append("[v0]");
            return graph.ToString();
        }

        graph.Append("[0:v]split=").Append(rungs.Count.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < rungs.Count; i++)
        {
            graph.Append(FormattableString.Invariant($"[s{i}]"));
        }

        for (var i = 0; i < rungs.Count; i++)
        {
            graph.Append(FormattableString.Invariant($";[s{i}]")).Append(ScaleChain(rungs[i], accel, hardwareScale)).Append(FormattableString.Invariant($"[v{i}]"));
        }

        return graph.ToString();
    }

    /// <summary>
    /// Scale-to-fit + pad to the rung's exact size, square pixels, fixed pixel format. The output
    /// of this chain is byte-for-byte the same shape whatever arrives - a 2.39:1 film gets
    /// letterboxed, the 16:9 slate fills the frame, a 10-bit source is squashed to 8-bit here and
    /// not by the encoder picking High10.
    /// </summary>
    private static string ScaleChain(LadderRung rung, HardwareAccel accel, bool hardwareScale)
    {
        if (accel == HardwareAccel.Qsv && hardwareScale)
        {
            // The T2-era chain, kept only as an A/B knob. scale_qsv needs QSV-resident frames
            // (hwupload first) and only accepts -1 for the keep-aspect dimension (CLAUDE.md).
            return FormattableString.Invariant($"format=nv12,hwupload=extra_hw_frames=64,scale_qsv=-1:{rung.Height}");
        }

        var fit = FormattableString.Invariant(
            $"scale={rung.Width}:{rung.Height}:force_original_aspect_ratio=decrease:flags=bicubic,pad={rung.Width}:{rung.Height}:-1:-1:color=black,setsar=1");

        return accel switch
        {
            // h264_qsv takes nv12 system-memory frames and uploads them itself.
            HardwareAccel.Qsv => fit + ",format=nv12",

            // h264_vaapi needs hardware frames; the upload happens after the fixed-geometry
            // chain so its parameters never change. Untested backend.
            HardwareAccel.Vaapi => fit + ",format=nv12,hwupload",

            // libx264 / nvenc / amf: 8-bit 4:2:0 system memory.
            _ => fit + ",format=yuv420p",
        };
    }

    private static void AddVideoEncoderArgs(List<string> args, string v, LadderRung rung, HardwareAccel accel, int segmentSeconds)
    {
        switch (accel)
        {
            case HardwareAccel.Qsv:
                args.AddRange([$"-c:{v}", "h264_qsv", $"-preset:{v}", "veryfast"]);
                break;
            case HardwareAccel.Vaapi:
                args.AddRange([$"-c:{v}", "h264_vaapi"]);
                break;
            case HardwareAccel.Nvenc:
                args.AddRange([$"-c:{v}", "h264_nvenc", $"-preset:{v}", "p4"]);
                break;
            case HardwareAccel.Amf:
                args.AddRange([$"-c:{v}", "h264_amf"]);
                break;
            default:
                args.AddRange([$"-c:{v}", "libx264", $"-preset:{v}", "veryfast"]);
                break;
        }

        args.AddRange([
            $"-profile:{v}", "high",
            $"-level:{v}", "4.0",
            $"-b:{v}", FormattableString.Invariant($"{rung.VideoKbps}k"),
            $"-maxrate:{v}", FormattableString.Invariant($"{rung.VideoKbps}k"),
            $"-bufsize:{v}", FormattableString.Invariant($"{rung.VideoKbps * 2}k"),
        ]);

        // Keyframe cadence = segment boundaries, identical on every rung because every rung sees
        // the same timestamps. QSV MUST NOT get -force_key_frames: on the NAS's real hardware it
        // makes h264_qsv emit no keyframe-flagged packets at all and Go Live times out (CLAUDE.md,
        // v0.3.22). QSV gets a frame-count GOP + forced_idr sized for 24 fps; the slate's 30 fps
        // means its segments run a little long during a pause, which is harmless.
        if (accel == HardwareAccel.Qsv)
        {
            args.AddRange([$"-g:{v}", (24 * segmentSeconds).ToString(CultureInfo.InvariantCulture), $"-forced_idr:{v}", "1"]);
        }
        else
        {
            args.AddRange([$"-force_key_frames:{v}", FormattableString.Invariant($"expr:gte(t,n_forced*{segmentSeconds})")]);
            if (accel == HardwareAccel.None)
            {
                // x264 would otherwise add scene-cut keyframes between the forced ones, which
                // are harmless for one rung but let the HLS muxer cut rungs at different places.
                args.AddRange([$"-sc_threshold:{v}", "0"]);
            }
        }
    }
}
