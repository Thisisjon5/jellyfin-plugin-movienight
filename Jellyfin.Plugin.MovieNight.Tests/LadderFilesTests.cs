using Xunit;

namespace Jellyfin.Plugin.MovieNight.Tests;

/// <summary>
/// The ladder route is the one route clients call directly, so its exact-name whitelist and its
/// credential-propagating playlist rewrite are the two things that must not regress.
/// </summary>
public class LadderFilesTests
{
    [Theory]
    [InlineData("master.m3u8")]
    [InlineData("v0/index.m3u8")]
    [InlineData("v2/index.m3u8")]
    [InlineData("v1/seg_0.ts")]
    [InlineData("v1/seg_1234.ts")]
    [InlineData("/v1/seg_1.ts")]
    public void IsServableName_LadderFiles_Allowed(string path)
    {
        Assert.True(LadderFiles.IsServableName(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../../config/system.xml")]
    [InlineData("v1/../../../etc/passwd")]
    [InlineData("v10/index.m3u8")]
    [InlineData("v1/seg_.ts")]
    [InlineData("v1/seg_1.ts.bak")]
    [InlineData("index.m3u8")]
    [InlineData("v1/init_1.mp4")]
    [InlineData(@"v1\..\..\secrets")]
    public void IsServableName_AnythingElse_Rejected(string? path)
    {
        Assert.False(LadderFiles.IsServableName(path));
    }

    [Fact]
    public void AppendQueryToUris_MasterPlaylist_KeyOnEveryVariantLine()
    {
        const string master = "#EXTM3U\n#EXT-X-VERSION:6\n#EXT-X-STREAM-INF:BANDWIDTH=4500000,RESOLUTION=1280x720,CODECS=\"avc1.640028,mp4a.40.2\"\nv0/index.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=1700000,RESOLUTION=854x480\nv1/index.m3u8\n";

        var rewritten = LadderFiles.AppendQueryToUris(master, "api_key=abc123");

        Assert.Equal("#EXTM3U\n#EXT-X-VERSION:6\n#EXT-X-STREAM-INF:BANDWIDTH=4500000,RESOLUTION=1280x720,CODECS=\"avc1.640028,mp4a.40.2\"\nv0/index.m3u8?api_key=abc123\n#EXT-X-STREAM-INF:BANDWIDTH=1700000,RESOLUTION=854x480\nv1/index.m3u8?api_key=abc123\n", rewritten);
    }

    [Fact]
    public void AppendQueryToUris_MediaPlaylist_TagsUntouched_SegmentsTagged()
    {
        const string index = "#EXTM3U\n#EXT-X-VERSION:6\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:7\n#EXT-X-INDEPENDENT-SEGMENTS\n#EXT-X-PROGRAM-DATE-TIME:2026-09-03T20:00:00.000+0000\n#EXTINF:4.004000,\nseg_7.ts\n#EXTINF:4.004000,\nseg_8.ts\n";

        var rewritten = LadderFiles.AppendQueryToUris(index, "api_key=k");

        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:7\n", rewritten);
        Assert.Contains("\nseg_7.ts?api_key=k\n", rewritten);
        Assert.Contains("\nseg_8.ts?api_key=k\n", rewritten);
        Assert.DoesNotContain("#EXTINF:4.004000,?api_key", rewritten);
        Assert.EndsWith("seg_8.ts?api_key=k\n", rewritten);
    }

    [Fact]
    public void AppendQueryToUris_NoQuery_ReturnsInputUnchanged()
    {
        const string index = "#EXTM3U\n#EXTINF:4,\nseg_1.ts\n";
        Assert.Same(index, LadderFiles.AppendQueryToUris(index, null));
        Assert.Same(index, LadderFiles.AppendQueryToUris(index, string.Empty));
    }

    [Fact]
    public void AppendQueryToUris_HandlesCrLfAndExistingQuery()
    {
        const string index = "#EXTM3U\r\n#EXTINF:4,\r\nseg_1.ts?x=1\r\n";
        var rewritten = LadderFiles.AppendQueryToUris(index, "api_key=k");
        Assert.Equal("#EXTM3U\n#EXTINF:4,\nseg_1.ts?x=1&api_key=k\n", rewritten);
    }
}
