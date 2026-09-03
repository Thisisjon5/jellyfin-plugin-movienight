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
/// Pure list building, no process execution - unit-tested per backend.
/// </summary>
public static class LadderCommandBuilder
{
    /// <summary>Segment duration in seconds. Also the keyframe cadence on every rung.</summary>
    public const int SegmentSeconds = 4;

    /// <summary>Segments kept in each rung's playlist (a 24 s window; players start ~3 from the end).</summary>
    public const int WindowSegments = 6;

    /// <summary>Maximum rung count the setting accepts.</summary>
    public const int MaxRungs = 3;

    /// <summary>Rung presets below the top rung, in descending order.</summary>
    private static readonly LadderRung[] LowerRungs =
    [
        new(480, 1500, 96),
        new(360, 800, 64),
    ];

    /// <summary>
    /// Builds the rung table for a given rung count and top-rung bitrate. The top rung is always
    /// 720p (Jon's ruling, 2026-09-03).
    /// </summary>
    /// <param name="rungCount">1-3; clamped.</param>
    /// <param name="topRungKbps">Top rung video bitrate in kbps; clamped to 500-20000.</param>
    /// <returns>The rungs, top first.</returns>
    public static IReadOnlyList<LadderRung> PlanRungs(int rungCount, int topRungKbps)
    {
        var count = Math.Clamp(rungCount, 1, MaxRungs);
        var top = Math.Clamp(topRungKbps, 500, 20000);
        var rungs = new List<LadderRung> { new(720, top, 128) };
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
    /// <returns>The argument list, ready for <c>ProcessStartInfo.ArgumentList</c>.</returns>
    public static IReadOnlyList<string> Build(string feedUrl, string outputDir, IReadOnlyList<LadderRung> rungs, HardwareAccel accel, string? vaapiDevice = null)
    {
        ArgumentNullException.ThrowIfNull(rungs);
        if (rungs.Count == 0)
        {
            throw new ArgumentException("At least one rung is required", nameof(rungs));
        }

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
                args.AddRange(["-init_hw_device", "qsv=hw", "-filter_hw_device", "hw"]);
                break;
            case HardwareAccel.Vaapi:
                args.AddRange(["-init_hw_device", $"vaapi=hw:{vaapiDevice}", "-filter_hw_device", "hw"]);
                break;
        }

        // genpts because each feeder swap (pause -> slate -> resume) starts a fresh mpegts
        // timeline behind feed.ts; ffmpeg's demuxer-level discontinuity compensation then keeps
        // the output monotonic. This is the v3 persistent-process idiom (v0.3.25/v0.3.28).
        args.AddRange(["-fflags", "+genpts", "-i", feedUrl]);

        args.AddRange(["-filter_complex", BuildFilterGraph(rungs, accel)]);

        for (var i = 0; i < rungs.Count; i++)
        {
            var rung = rungs[i];
            var v = FormattableString.Invariant($"v:{i}");
            var a = FormattableString.Invariant($"a:{i}");
            args.AddRange(["-map", FormattableString.Invariant($"[v{i}]"), "-map", "0:a:0"]);
            AddVideoEncoderArgs(args, v, rung, accel);
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
            "-hls_time", SegmentSeconds.ToString(CultureInfo.InvariantCulture),
            "-hls_list_size", WindowSegments.ToString(CultureInfo.InvariantCulture),
            "-hls_delete_threshold", "2",
            "-hls_flags", "delete_segments+independent_segments+program_date_time",
            "-hls_segment_type", "mpegts",
            "-master_pl_name", "master.m3u8",
            "-hls_segment_filename", Path.Combine(outputDir, "v%v", "seg_%d.ts"),
            Path.Combine(outputDir, "v%v", "index.m3u8"),
        ]);

        return args;
    }

    /// <summary>
    /// Builds the filter graph: one split feeding one scale chain per rung, each ending in
    /// <c>[v{i}]</c>. For a single rung there is no split.
    /// </summary>
    /// <param name="rungs">Rung table.</param>
    /// <param name="accel">Backend - decides which scaler and whether frames are uploaded to hardware first.</param>
    /// <returns>The filter_complex string.</returns>
    public static string BuildFilterGraph(IReadOnlyList<LadderRung> rungs, HardwareAccel accel)
    {
        ArgumentNullException.ThrowIfNull(rungs);
        var graph = new StringBuilder();
        if (rungs.Count == 1)
        {
            graph.Append("[0:v]").Append(ScaleChain(rungs[0].Height, accel)).Append("[v0]");
            return graph.ToString();
        }

        graph.Append("[0:v]split=").Append(rungs.Count.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < rungs.Count; i++)
        {
            graph.Append(FormattableString.Invariant($"[s{i}]"));
        }

        for (var i = 0; i < rungs.Count; i++)
        {
            graph.Append(FormattableString.Invariant($";[s{i}]")).Append(ScaleChain(rungs[i].Height, accel)).Append(FormattableString.Invariant($"[v{i}]"));
        }

        return graph.ToString();
    }

    private static string ScaleChain(int height, HardwareAccel accel) => accel switch
    {
        // scale_qsv needs QSV-resident frames (hwupload first) and only accepts -1, not -2, for
        // the keep-aspect dimension - both found live against real hardware (CLAUDE.md gotchas).
        // Each branch uploads independently, which is the T2-proven shape.
        HardwareAccel.Qsv => FormattableString.Invariant($"format=nv12,hwupload=extra_hw_frames=64,scale_qsv=-1:{height}"),
        HardwareAccel.Vaapi => FormattableString.Invariant($"format=nv12,hwupload,scale_vaapi=-2:{height}"),

        // Software scaler for everything else, pinned to 8-bit 4:2:0: a 10-bit source would
        // otherwise make libx264 emit High10, which no living-room client plays.
        _ => FormattableString.Invariant($"scale=-2:{height},format=yuv420p"),
    };

    private static void AddVideoEncoderArgs(List<string> args, string v, LadderRung rung, HardwareAccel accel)
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
        // v0.3.22). QSV gets a frame-count GOP + forced_idr; the slate's 30 fps vs a film's 24
        // means its segments run a little long during a pause, which is harmless.
        if (accel == HardwareAccel.Qsv)
        {
            args.AddRange([$"-g:{v}", "96", $"-forced_idr:{v}", "1"]);
        }
        else
        {
            args.AddRange([$"-force_key_frames:{v}", FormattableString.Invariant($"expr:gte(t,n_forced*{SegmentSeconds})")]);
            if (accel == HardwareAccel.None)
            {
                // x264 would otherwise add scene-cut keyframes between the forced ones, which
                // are harmless for one rung but let the HLS muxer cut rungs at different places.
                args.AddRange([$"-sc_threshold:{v}", "0"]);
            }
        }
    }
}
