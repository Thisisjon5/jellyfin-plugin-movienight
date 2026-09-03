using Xunit;

namespace Jellyfin.Plugin.MovieNight.Tests;

/// <summary>
/// The rule that decides whether losing the last feed.ts consumer tears the feeders down.
/// <para>
/// It was wrong in exactly one case and it cost a live broadcast on 2026-09-03: under the ladder
/// architecture the encoder is the ONLY feed.ts consumer for the whole broadcast, so when the
/// pause swap ended its read the count hit zero, the feeders were killed out from under it, and
/// the encoder exited 218 and restarted - resetting HLS sequence numbers and throwing every
/// connected client off. Viewers never touch feed.ts at all; they fetch ladder segments.
/// </para>
/// </summary>
public class FeedTeardownRuleTests
{
    [Fact]
    public void LadderSessionLive_LastConsumerGone_DoesNotStopFeeders()
    {
        // The 2026-09-03 regression, stated directly: this MUST be false.
        Assert.False(BroadcastManager.ShouldStopFeedersOnDisconnect(0, ladderSessionActive: true, hasMovie: true));
    }

    [Fact]
    public void NoLadderSession_LastConsumerGone_StopsFeeders()
    {
        // v3 behaviour is preserved when no ladder session owns the feed: one consumer per tune,
        // so zero consumers really does mean nobody is watching. Killing avoids the unconsumed
        // feeder drifting and producing the ~1:50 echo measured on 2026-08-16.
        Assert.True(BroadcastManager.ShouldStopFeedersOnDisconnect(0, ladderSessionActive: false, hasMovie: true));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void ConsumersRemain_NeverStopsFeeders(int remaining)
    {
        Assert.False(BroadcastManager.ShouldStopFeedersOnDisconnect(remaining, ladderSessionActive: false, hasMovie: true));
        Assert.False(BroadcastManager.ShouldStopFeedersOnDisconnect(remaining, ladderSessionActive: true, hasMovie: true));
    }

    [Fact]
    public void NoMovieConfigured_NothingToStop()
    {
        Assert.False(BroadcastManager.ShouldStopFeedersOnDisconnect(0, ladderSessionActive: false, hasMovie: false));
    }
}
