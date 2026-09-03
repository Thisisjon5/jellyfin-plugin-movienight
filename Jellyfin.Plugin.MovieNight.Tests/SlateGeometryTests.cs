using System.Linq;
using Xunit;

namespace Jellyfin.Plugin.MovieNight.Tests;

/// <summary>
/// The slate must be synthesised to the movie feed's exact geometry, or the pause swap kills a
/// hardware-accelerated ladder encoder (2026-09-03, reproduced and fixed offline with
/// debug/encode-probe - see <see cref="SourceGeometry"/>). These pin the two pure pieces: the
/// ffprobe parse and the slate args it drives.
/// </summary>
public class SlateGeometryTests
{
    // Exactly what ffprobe printed for the Afro Samurai mezzanine on 2026-09-03 - note the field
    // order is ffprobe's own (width, height, SAR, pix_fmt, fps), not the order requested.
    private const string AfroSamuraiCsv = "1080,720,853:720,yuv420p,30000/1001";

    [Fact]
    public void Parse_Mezzanine_CarriesEveryParameterTheEncoderCaresAbout()
    {
        Assert.True(SourceGeometry.TryParseFfprobeCsv(AfroSamuraiCsv, out var g));
        Assert.NotNull(g);
        Assert.Equal(1080, g!.Width);
        Assert.Equal(720, g.Height);
        Assert.Equal("30000/1001", g.FrameRate);
        Assert.Equal("yuv420p", g.PixelFormat);
        Assert.Equal("853:720", g.SampleAspectRatio);
        Assert.Equal("853/720", g.SetSarArgument);
    }

    [Theory]
    [InlineData("1920,1080,1:1,yuv420p,24000/1001")]
    [InlineData("1920,1080,N/A,yuv420p,24000/1001")]
    [InlineData("1920,1080,0:1,yuv420p,24000/1001")]
    [InlineData("1920,1080,,yuv420p,24000/1001")]
    public void Parse_SquareOrUnknownSar_MeansNoSetSar(string csv)
    {
        Assert.True(SourceGeometry.TryParseFfprobeCsv(csv, out var g));
        Assert.Null(g!.SampleAspectRatio);
        Assert.Null(g.SetSarArgument);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("0,720,853:720,yuv420p,30000/1001")]
    [InlineData("1080,720,853:720,yuv420p,0/0")]
    [InlineData("1080,720,853:720,,30000/1001")]
    [InlineData("1080,720,853:720,yuv420p")]
    public void Parse_Unusable_ReturnsFalse(string? csv)
    {
        Assert.False(SourceGeometry.TryParseFfprobeCsv(csv, out var g));
        Assert.Null(g);
    }

    [Fact]
    public void Parse_RawMkv_TrailingEmptyFieldTolerated()
    {
        // Verbatim from the raw Afro Samurai MKV on the NAS: a sixth, empty field.
        Assert.True(SourceGeometry.TryParseFfprobeCsv("720,480,853:720,yuv420p,30000/1001,", out var g));
        Assert.Equal(720, g!.Width);
        Assert.Equal(480, g.Height);
        Assert.Equal("30000/1001", g.FrameRate);
        Assert.Equal("yuv420p", g.PixelFormat);
        Assert.Equal("853:720", g.SampleAspectRatio);
    }

    [Fact]
    public void SlateArgs_MatchTheMovieFeed()
    {
        SourceGeometry.TryParseFfprobeCsv(AfroSamuraiCsv, out var g);
        var joined = string.Join(' ', BroadcastManager.BuildSw2SlateArgs(g));

        // The four parameters that trigger ffmpeg's filter-graph reconfigure, plus SAR for a
        // correct picture. This is the arg set that survived the seam in the offline probe.
        Assert.Contains("color=c=darkslategray:s=1080x720:r=30000/1001", joined);
        Assert.Contains("-vf setsar=853/720", joined);
        Assert.Contains("-pix_fmt yuv420p", joined);
        Assert.Contains("-ar 48000", joined);
        Assert.DoesNotContain("1280x720", joined);
    }

    [Fact]
    public void SlateArgs_NoGeometry_FallsBackToHistoricalDefaultsWithoutSetSar()
    {
        var args = BroadcastManager.BuildSw2SlateArgs(null);
        var joined = string.Join(' ', args);

        Assert.Contains("color=c=darkslategray:s=1280x720:r=30", joined);
        Assert.Contains("-pix_fmt yuv420p", joined);
        Assert.DoesNotContain("setsar", joined);
        Assert.Equal("pipe:1", args.Last());
    }

    [Fact]
    public void SlateArgs_SquarePixelSource_NoSetSarInserted()
    {
        SourceGeometry.TryParseFfprobeCsv("1920,1080,1:1,yuv420p,24000/1001", out var g);
        var joined = string.Join(' ', BroadcastManager.BuildSw2SlateArgs(g));

        Assert.Contains("s=1920x1080:r=24000/1001", joined);
        Assert.DoesNotContain("setsar", joined);
    }
}
