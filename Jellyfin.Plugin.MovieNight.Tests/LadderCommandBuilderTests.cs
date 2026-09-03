using System.Linq;
using Xunit;

namespace Jellyfin.Plugin.MovieNight.Tests;

public class LadderCommandBuilderTests
{
    private const string Feed = "http://127.0.0.1:8096/MovieNight/stream/feed.ts";
    private const string Out = "/tmp/ladder";

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
        Assert.Equal(720, rungs[0].Height);
        Assert.Equal(4000, rungs[0].VideoKbps);
        Assert.True(rungs.Select(r => r.Height).SequenceEqual(rungs.Select(r => r.Height).OrderByDescending(h => h)));
    }

    [Fact]
    public void Build_OneRung_NoSplit_SingleVariant()
    {
        var rungs = LadderCommandBuilder.PlanRungs(1, 4000);
        var args = LadderCommandBuilder.Build(Feed, Out, rungs, HardwareAccel.None);
        var joined = string.Join(' ', args);

        Assert.Contains("-filter_complex [0:v]scale=-2:720,format=yuv420p[v0]", joined);
        Assert.DoesNotContain("split", joined);
        Assert.Contains("-var_stream_map v:0,a:0 ", joined);
        Assert.Contains("-c:v:0 libx264", joined);
        Assert.Contains("-b:v:0 4000k -maxrate:v:0 4000k -bufsize:v:0 8000k", joined);
        Assert.Contains("-force_key_frames:v:0 expr:gte(t,n_forced*4)", joined);
        Assert.Contains("-sc_threshold:v:0 0", joined);
        Assert.Contains("-hls_list_size 6", joined);
        Assert.Contains("delete_segments+independent_segments+program_date_time", joined);
        Assert.Contains("-hls_segment_type mpegts", joined);
        // Path.Combine uses the platform separator, so compare the same way rather than hardcoding
        // "/" - this assertion failed on Windows only; the builder itself is correct there.
        Assert.Equal(System.IO.Path.Combine(Out, "v%v", "index.m3u8"), args[^1]);
    }

    [Fact]
    public void Build_ThreeRungs_SplitsOnceAndMapsEachRung()
    {
        var rungs = LadderCommandBuilder.PlanRungs(3, 5000);
        var args = LadderCommandBuilder.Build(Feed, Out, rungs, HardwareAccel.None);
        var joined = string.Join(' ', args);

        Assert.Contains("[0:v]split=3[s0][s1][s2];[s0]scale=-2:720,format=yuv420p[v0];[s1]scale=-2:480,format=yuv420p[v1];[s2]scale=-2:360,format=yuv420p[v2]", joined);
        Assert.Contains("-var_stream_map v:0,a:0 v:1,a:1 v:2,a:2", joined);
        Assert.Equal(3, args.Count(a => a == "0:a:0"));
        Assert.Contains("-c:v:2 libx264", joined);
        Assert.Contains("-b:v:1 1500k", joined);
        Assert.Contains("-b:v:2 800k", joined);
        Assert.Contains("-b:a:0 128k", joined);
        Assert.Contains("-b:a:2 64k", joined);
    }

    [Fact]
    public void Build_Qsv_UploadsPerRung_UsesGopNotForceKeyFrames()
    {
        // -force_key_frames wedges h264_qsv on the NAS (CLAUDE.md); the ladder must keep the
        // -g/-forced_idr idiom, and scale_qsv needs hwupload first and -1 not -2.
        var rungs = LadderCommandBuilder.PlanRungs(2, 4000);
        var args = LadderCommandBuilder.Build(Feed, Out, rungs, HardwareAccel.Qsv);
        var joined = string.Join(' ', args);

        Assert.Contains("-init_hw_device qsv=hw -filter_hw_device hw", joined);
        Assert.Contains("[s0]format=nv12,hwupload=extra_hw_frames=64,scale_qsv=-1:720[v0]", joined);
        Assert.Contains("[s1]format=nv12,hwupload=extra_hw_frames=64,scale_qsv=-1:480[v1]", joined);
        Assert.Contains("-c:v:0 h264_qsv", joined);
        Assert.Contains("-g:v:0 96 -forced_idr:v:0 1", joined);
        Assert.Contains("-g:v:1 96 -forced_idr:v:1 1", joined);
        Assert.DoesNotContain("force_key_frames", joined);
        Assert.DoesNotContain("scale_qsv=-2", joined);
    }

    [Fact]
    public void Build_Nvenc_SoftwareScaleThenNvenc()
    {
        var rungs = LadderCommandBuilder.PlanRungs(1, 4000);
        var joined = string.Join(' ', LadderCommandBuilder.Build(Feed, Out, rungs, HardwareAccel.Nvenc));

        Assert.Contains("-c:v:0 h264_nvenc -preset:v:0 p4", joined);
        Assert.Contains("scale=-2:720,format=yuv420p", joined);
        Assert.Contains("-force_key_frames:v:0", joined);
        Assert.DoesNotContain("init_hw_device", joined);
    }

    [Fact]
    public void Build_Vaapi_WithDevice_UsesVaapi_WithoutDevice_FallsBackToSoftware()
    {
        var rungs = LadderCommandBuilder.PlanRungs(1, 4000);

        var with = string.Join(' ', LadderCommandBuilder.Build(Feed, Out, rungs, HardwareAccel.Vaapi, "/dev/dri/renderD128"));
        Assert.Contains("vaapi=hw:/dev/dri/renderD128", with);
        Assert.Contains("-c:v:0 h264_vaapi", with);
        Assert.Contains("scale_vaapi=-2:720", with);

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
}
