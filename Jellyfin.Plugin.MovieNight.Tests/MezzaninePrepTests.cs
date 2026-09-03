using System.Collections.Generic;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.MovieNight.Tests;

public class MezzaninePrepTests
{
    [Fact]
    public void QuoteFilterPath_EscapesWindowsDriveColonAndQuotes()
    {
        Assert.Equal(@"'C\:/Movies/It\'s Here/sub.srt'", MezzaninePrep.QuoteFilterPath(@"C:\Movies\It's Here\sub.srt"));
        Assert.Equal("'/movies/plain.mkv'", MezzaninePrep.QuoteFilterPath("/movies/plain.mkv"));
    }

    [Theory]
    [InlineData("subrip", "text")]
    [InlineData("ass", "text")]
    [InlineData("mov_text", "text")]
    [InlineData("PGSSUB", "bitmap")]
    [InlineData("dvdsub", "bitmap")]
    [InlineData(null, "unsupported")]
    [InlineData("eia_608", "unsupported")]
    public void ClassifySubtitle_KnownCodecs(string? codec, string expected)
    {
        Assert.Equal(expected, MezzaninePrep.ClassifySubtitle(codec));
    }

    [Fact]
    public void DescribeSubtitle_EmbeddedText_UsesRelativeIndexAmongSubtitleStreams()
    {
        var streams = new List<MediaStream>
        {
            new() { Index = 0, Type = MediaStreamType.Video, Codec = "h264" },
            new() { Index = 1, Type = MediaStreamType.Audio, Codec = "dts" },
            new() { Index = 2, Type = MediaStreamType.Subtitle, Codec = "subrip", Language = "eng" },
            new() { Index = 3, Type = MediaStreamType.Subtitle, Codec = "pgssub", Language = "eng" },
            new() { Index = 4, Type = MediaStreamType.Subtitle, Codec = "subrip", Language = "fre" },
            new() { Index = 5, Type = MediaStreamType.Subtitle, Codec = "subrip", Language = "ger", IsExternal = true, Path = "/movies/film.ger.srt" },
        };

        var french = MezzaninePrep.DescribeSubtitle("/movies/film.mkv", streams[4], streams);
        Assert.Equal("/movies/film.mkv", french.SourcePath);
        Assert.Equal(2, french.RelativeSubtitleIndex);
        Assert.False(french.IsBitmap);

        var pgs = MezzaninePrep.DescribeSubtitle("/movies/film.mkv", streams[3], streams);
        Assert.True(pgs.IsBitmap);
        Assert.Equal(3, pgs.AbsoluteStreamIndex);

        var external = MezzaninePrep.DescribeSubtitle("/movies/film.mkv", streams[5], streams);
        Assert.Equal("/movies/film.ger.srt", external.SourcePath);
        Assert.Null(external.RelativeSubtitleIndex);
    }

    [Fact]
    public void BuildArgs_Software_NoSubtitle()
    {
        var args = MezzaninePrep.BuildArgs("/movies/film.mkv", "/data/out.partial.mp4", HardwareAccel.None, null, null);
        var joined = string.Join(' ', args);

        Assert.Contains("-progress pipe:1", joined);
        Assert.Contains("-filter_complex [0:v:0]scale=-2:720,format=yuv420p[vout]", joined);
        Assert.Contains("-map [vout] -map 0:a:0?", joined);
        Assert.Contains("-c:v libx264", joined);
        Assert.Contains("-c:a aac -b:a 192k -ac 2 -ar 48000", joined);
        Assert.Contains("-movflags +faststart", joined);
        Assert.Equal("/data/out.partial.mp4", args[^1]);
    }

    [Fact]
    public void BuildArgs_EmbeddedTextSubtitle_BurnsInBeforeScale()
    {
        var spec = new SubtitleBurnSpec("/movies/film.mkv", 1, 4, false);
        var joined = string.Join(' ', MezzaninePrep.BuildArgs("/movies/film.mkv", "/data/o.mp4", HardwareAccel.None, null, spec));

        Assert.Contains("[0:v:0]subtitles='/movies/film.mkv':si=1,scale=-2:720,format=yuv420p[vout]", joined);
    }

    [Fact]
    public void BuildArgs_BitmapSubtitle_OverlaysStream()
    {
        var spec = new SubtitleBurnSpec("/movies/film.mkv", null, 3, true);
        var joined = string.Join(' ', MezzaninePrep.BuildArgs("/movies/film.mkv", "/data/o.mp4", HardwareAccel.Qsv, null, spec));

        Assert.Contains("[0:v:0][0:3]overlay,format=nv12,hwupload=extra_hw_frames=64,scale_qsv=-1:720[vout]", joined);
        Assert.Contains("-c:v h264_qsv", joined);
        Assert.Contains("-g 96 -forced_idr 1", joined);
        Assert.DoesNotContain("force_key_frames", joined);
    }
}
