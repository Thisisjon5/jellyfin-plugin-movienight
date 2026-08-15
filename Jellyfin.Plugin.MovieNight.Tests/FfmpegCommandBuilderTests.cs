using Xunit;

namespace Jellyfin.Plugin.MovieNight.Tests;

public class FfmpegCommandBuilderTests
{
    [Fact]
    public void Build_None_UsesSoftwareEncoder()
    {
        var args = FfmpegCommandBuilder.Build("/movies/foo.mkv", "/tmp/hls", HardwareAccel.None);

        Assert.Contains("libx264", args);
        Assert.Contains("-i", args);
        Assert.Contains("/movies/foo.mkv", args);
    }

    [Fact]
    public void Build_Qsv_UsesQsvEncoder()
    {
        var args = FfmpegCommandBuilder.Build("/movies/foo.mkv", "/tmp/hls", HardwareAccel.Qsv);

        Assert.Contains("h264_qsv", args);
    }

    [Fact]
    public void Build_Qsv_UsesExplicitGopInsteadOfForceKeyFrames()
    {
        // -force_key_frames wedges h264_qsv on real hardware (no keyframe-flagged packets at all
        // for 25fps sources -> hls muxer never writes a segment -> Go Live times out); QSV gets
        // an explicit GOP + forced_idr instead. See the comment in Build().
        var args = FfmpegCommandBuilder.Build("/movies/foo.mkv", "/tmp/hls", HardwareAccel.Qsv);
        var joined = string.Join(' ', args);

        Assert.DoesNotContain("-force_key_frames", args);
        Assert.Contains("-g 100 -forced_idr 1", joined);
    }

    [Fact]
    public void Build_NonQsv_KeepsForceKeyFrames()
    {
        var args = FfmpegCommandBuilder.Build("/movies/foo.mkv", "/tmp/hls", HardwareAccel.None);

        Assert.Contains("-force_key_frames", args);
        Assert.DoesNotContain("-forced_idr", args);
    }

    [Fact]
    public void Build_Vaapi_WithDevice_UsesVaapiEncoder()
    {
        var args = FfmpegCommandBuilder.Build("/movies/foo.mkv", "/tmp/hls", HardwareAccel.Vaapi, "/dev/dri/renderD128");

        Assert.Contains("h264_vaapi", args);
        Assert.Contains("vaapi=hw:/dev/dri/renderD128", args);
    }

    [Fact]
    public void Build_Vaapi_WithoutDevice_FallsBackToSoftware()
    {
        var args = FfmpegCommandBuilder.Build("/movies/foo.mkv", "/tmp/hls", HardwareAccel.Vaapi, vaapiDevice: null);

        Assert.Contains("libx264", args);
        Assert.DoesNotContain("h264_vaapi", args);
    }

    [Fact]
    public void Build_Nvenc_UsesNvencEncoder()
    {
        var args = FfmpegCommandBuilder.Build("/movies/foo.mkv", "/tmp/hls", HardwareAccel.Nvenc);

        Assert.Contains("h264_nvenc", args);
    }

    [Fact]
    public void Build_Amf_UsesAmfEncoder()
    {
        var args = FfmpegCommandBuilder.Build("/movies/foo.mkv", "/tmp/hls", HardwareAccel.Amf);

        Assert.Contains("h264_amf", args);
    }

    [Fact]
    public void Build_AlwaysIncludesAacAudioAndHlsOutput()
    {
        var args = FfmpegCommandBuilder.Build("/movies/foo.mkv", "/tmp/hls", HardwareAccel.None);
        var joined = string.Join(' ', args);

        Assert.Contains("-c:a aac -b:a 96k -ac 2", joined);
        Assert.Contains("-f hls", joined);
        Assert.Contains("-hls_time 4", joined);
        Assert.Contains("segment_%03d.ts", joined);
        Assert.Contains("master.m3u8", joined);
    }

    [Fact]
    public void Build_PathsAreDiscreteArgvEntries_NotShellQuoted()
    {
        // A path containing a space or quote must survive as one argv entry, not get mangled by
        // manual string quoting - this is the whole reason Build() returns a list.
        var args = FfmpegCommandBuilder.Build("/movies/A Movie (2024).mkv", "/tmp/hls out", HardwareAccel.None);

        Assert.Contains("/movies/A Movie (2024).mkv", args);
        Assert.DoesNotContain(args, a => a.Contains('"'));
    }
}
