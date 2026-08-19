using Xunit;

namespace Jellyfin.Plugin.MovieNight.Tests;

/// <summary>
/// T0 GATE SPIKE. The ladder route is anonymous (the client fetches it directly), so its
/// exact-name whitelist is the only thing standing between a request and the filesystem.
/// Delete alongside the spike.
/// </summary>
public class T0GateTests
{
    [Theory]
    [InlineData("master.m3u8")]
    [InlineData("v0/index.m3u8")]
    [InlineData("v2/index.m3u8")]
    [InlineData("v1/seg_0.ts")]
    [InlineData("v1/seg_1234.ts")]
    public void IsServableName_LadderFiles_Allowed(string path)
    {
        Assert.True(T0Gate.IsServableName(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../../config/system.xml")]
    [InlineData("v1/../../../etc/passwd")]
    [InlineData("v3/index.m3u8")]
    [InlineData("v1/seg_.ts")]
    [InlineData("v1/seg_1.ts.bak")]
    [InlineData("index.m3u8")]
    [InlineData(@"v1\..\..\secrets")]
    public void IsServableName_AnythingElse_Rejected(string? path)
    {
        Assert.False(T0Gate.IsServableName(path));
    }
}
