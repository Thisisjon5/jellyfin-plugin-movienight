using System.Linq;
using Xunit;

namespace Jellyfin.Plugin.MovieNight.Tests;

public class LadderCommandBuilderTests
{
    private const string Feed = "http://127.0.0.1:8096/MovieNight/stream/feed.ts";
    private const string Out = "/tmp/ladder";

    private const string Fit720 = "scale=1280:720:force_original_aspect_ratio=decrease:flags=bicubic,pad=1280:720:-1:-1:color=black,setsar=1";
    private const string Fit480 = "scale=854:480:force_original_aspect_ratio=decrease:flags=bicubic,pad=854:480:-1:-1:color=black,setsar=1";
    private const string Fit360 = "scale=640:360:force_original_aspect_ratio=decrease:flags=bicubic,pad=640:360:-1:-1:color=black,setsar=1";

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(9, 3)]
    public void PlanRungs_ClampsCount_TopIsAlways720(int requested, int expected)
    {
        var rungs = LadderCommandBuilder.PlanRungs(requested, 4000);

        Assert.Equal(expected, rungs.Count);
        Assert.Equal(1280, rungs[0].Width);
        Assert.Equal(720, rungs[0].Height);
        Assert.Equal(4000, rungs[0].VideoKbps);
        Assert.True(rungs.Select(r => r.Height).SequenceEqual(rungs.Select(r => r.Height).OrderByDescending(h => h)));
    }

    [Fact]
    public void Build_OneRung_NoSplit_FixedGeometry_SingleVariant()
    {
        var rungs = LadderCommandBuilder.PlanRungs(1, 4000);
        var args = LadderCommandBuilder.Build(Feed, Out, rungs, HardwareAccel.None);
        var joined = string.Join(' ', args);

        Assert.Contains("-filter_complex [0:v]" + Fit720 + ",format=yuv420p[v0]", joined);
        Assert.DoesNotContain("split", joined);
        Assert.Contains("-var_stream_map v:0,a:0 ", joined);
        Assert.Contains("-c:v:0 libx264", joined);
        Assert.Contains("-b:v:0 4000k -maxrate:v:0 4000k -bufsize:v:0 8000k", joined);
        Assert.Contains("-force_key_frames:v:0 expr:gte(t,n_forced*4)", joined);
        Assert.Contains("-sc_threshold:v:0 0", joined);
        Assert.Contains("-hls_time 4 -hls_list_size 6", joined);
        Assert.Contains("-hls_flags delete_segments+independent_segments+program_date_time ", joined);
        Assert.Contains("-hls_segment_type mpegts", joined);
        Assert.Equal(Out + "/v%v/index.m3u8", args[^1]);
    }

    [Fact]
    public void Build_ThreeRungs_SplitsOnceAndMapsEachRung()
    {
        var rungs = LadderCommandBuilder.PlanRungs(3, 5000);
        var args = LadderCommandBuilder.Build(Feed, Out, rungs, HardwareAccel.None);
        var joined = string.Join(' ', args);

        Assert.Contains("[0:v]split=3[s0][s1][s2];[s0]" + Fit720 + ",format=yuv420p[v0];[s1]" + Fit480 + ",format=yuv420p[v1];[s2]" + Fit360 + ",format=yuv420p[v2]", joined);
        Assert.Contains("-var_stream_map v:0,a:0 v:1,a:1 v:2,a:2", joined);
        Assert.Equal(3, args.Count(a => a == "0:a:0"));
        Assert.Contains("-c:v:2 libx264", joined);
        Assert.Contains("-b:v:1 1500k", joined);
        Assert.Contains("-b:v:2 800k", joined);
        Assert.Contains("-b:a:0 128k", joined);
        Assert.Contains("-b:a:2 64k", joined);
    }

    [Fact]
    public void Build_Qsv_SoftwareScaleToNv12_NoHwupload_UsesGopNotForceKeyFrames()
    {
        // 0.4.1: frames reach h264_qsv in system memory through a fixed-geometry chain, so a
        // slate seam cannot hand the encoder a new hardware frames context. -force_key_frames
        // still wedges h264_qsv on the NAS (CLAUDE.md); the -g/-forced_idr idiom stays.
        var rungs = LadderCommandBuilder.PlanRungs(2, 4000);
        var args = LadderCommandBuilder.Build(Feed, Out, rungs, HardwareAccel.Qsv);
        var joined = string.Join(' ', args);

        Assert.Contains("-init_hw_device qsv=hw ", joined);
        Assert.DoesNotContain("-filter_hw_device", joined);
        Assert.DoesNotContain("hwupload", joined);
        Assert.DoesNotContain("scale_qsv", joined);
        Assert.Contains("[s0]" + Fit720 + ",format=nv12[v0]", joined);
        Assert.Contains("[s1]" + Fit480 + ",format=nv12[v1]", joined);
        Assert.Contains("-c:v:0 h264_qsv", joined);
        Assert.Contains("-g:v:0 96 -forced_idr:v:0 1", joined);
        Assert.Contains("-g:v:1 96 -forced_idr:v:1 1", joined);
        Assert.DoesNotContain("force_key_frames", joined);
    }

    [Fact]
    public void Build_Qsv_HardwareScaleKnob_RestoresLegacyChain()
    {
        var rungs = LadderCommandBuilder.PlanRungs(1, 4000);
        var joined = string.Join(' ', LadderCommandBuilder.Build(Feed, Out, rungs, HardwareAccel.Qsv, hardwareScale: true));

        Assert.Contains("-init_hw_device qsv=hw -filter_hw_device hw", joined);
        Assert.Contains("[0:v]format=nv12,hwupload=extra_hw_frames=64,scale_qsv=-1:720[v0]", joined);
        Assert.DoesNotContain("scale_qsv=-2", joined);
    }

    [Fact]
    public void Build_SegmentSeconds_DrivesHlsTimeAndKeyframes()
    {
        var rungs = LadderCommandBuilder.PlanRungs(1, 4000);

        var x264 = string.Join(' ', LadderCommandBuilder.Build(Feed, Out, rungs, HardwareAccel.None, segmentSeconds: 2));
        Assert.Contains("-hls_time 2", x264);
        Assert.Contains("expr:gte(t,n_forced*2)", x264);

        var qsv = string.Join(' ', LadderCommandBuilder.Build(Feed, Out, rungs, HardwareAccel.Qsv, segmentSeconds: 2));
        Assert.Contains("-g:v:0 48 ", qsv);

        var clamped = string.Join(' ', LadderCommandBuilder.Build(Feed, Out, rungs, HardwareAccel.None, segmentSeconds: 99));
        Assert.Contains("-hls_time 6", clamped);
    }

    [Fact]
    public void Build_Nvenc_SoftwareFixedGeometryThenNvenc()
    {
        var rungs = LadderCommandBuilder.PlanRungs(1, 4000);
        var joined = string.Join(' ', LadderCommandBuilder.Build(Feed, Out, rungs, HardwareAccel.Nvenc));

        Assert.Contains("-c:v:0 h264_nvenc -preset:v:0 p4", joined);
        Assert.Contains(Fit720 + ",format=yuv420p", joined);
        Assert.Contains("-force_key_frames:v:0", joined);
        Assert.DoesNotContain("init_hw_device", joined);
    }

    [Fact]
    public void Build_Vaapi_WithDevice_UploadsAfterFit_WithoutDevice_FallsBackToSoftware()
    {
        var rungs = LadderCommandBuilder.PlanRungs(1, 4000);

        var with = string.Join(' ', LadderCommandBuilder.Build(Feed, Out, rungs, HardwareAccel.Vaapi, "/dev/dri/renderD128"));
        Assert.Contains("vaapi=hw:/dev/dri/renderD128", with);
        Assert.Contains("-c:v:0 h264_vaapi", with);
        Assert.Contains(Fit720 + ",format=nv12,hwupload[v0]", with);

        var without = string.Join(' ', LadderCommandBuilder.Build(Feed, Out, rungs, HardwareAccel.Vaapi, vaapiDevice: null));
        Assert.Contains("-c:v:0 libx264", without);
        Assert.DoesNotContain("vaapi", without);
    }

    [Fact]
    public void Build_AudioIsAlwaysAacStereo48k()
    {
        var rungs = LadderCommandBuilder.PlanRungs(2, 4000);
        var joined = string.Join(' ', LadderCommandBuilder.Build(Feed, Out, rungs, HardwareAccel.None));

        Assert.Contains("-c:a:0 aac", joined);
        Assert.Contains("-ac:a:1 2 -ar:a:1 48000", joined);
    }

    [Fact]
    public void WithRestartContinuity_ContinuesNumberingAndDeclaresDiscontinuity()
    {
        var rungs = LadderCommandBuilder.PlanRungs(1, 4000);
        var original = LadderCommandBuilder.Build(Feed, Out, rungs, HardwareAccel.None);

        var restarted = LadderCommandBuilder.WithRestartContinuity(original, 57);
        var joined = string.Join(' ', restarted);

        Assert.Equal(original.Count + 2, restarted.Count);
        Assert.Contains("-hls_flags delete_segments+independent_segments+program_date_time+discont_start ", joined);
        Assert.Contains("-start_number 57 " + Out + "/v%v/index.m3u8", joined);
        Assert.Equal(Out + "/v%v/index.m3u8", restarted[^1]);
        Assert.Equal("-start_number", restarted[^3]);

        // The original list is untouched, so a second restart recomputes from disk.
        Assert.DoesNotContain("-start_number", original);
        Assert.DoesNotContain("discont_start", string.Join(' ', original));
    }
}
