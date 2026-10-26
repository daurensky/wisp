using Wisp.Core;
using Xunit;
namespace Wisp.Core.Tests;

public sealed class MediaTimingTests
{
    [Fact]
    public void RtpClockIncludesDroppedVideoAndDtxGapsAndWrapsNaturally()
    {
        var clock = new Clock();
        var audio = new RtpMediaClock(48000, 960, clock);
        var video = new RtpMediaClock(90000, 1, clock);
        Assert.Equal(0u, audio.Next()); Assert.Equal(0u, video.Next());
        clock.Ticks += TimeSpan.FromSeconds(2).Ticks;
        Assert.Equal(96000u, audio.Next()); Assert.Equal(180000u, video.Next());
        clock.Ticks += TimeSpan.FromHours(14).Ticks;
        Assert.Equal(unchecked((uint)(50402UL * 90000)), video.Next());
    }
    [Fact]
    public void ReceiverReportsReduceOnlyAfterSustainedLossAndRespectTheUserCap()
    {
        var clock = new Clock();
        var rate = new VideoRateController(4000000, 60, clock);
        var bad = new VideoNetworkReport(.15, 25, 100);
        Assert.False(rate.Observe("bob", bad)); Assert.False(rate.Observe("bob", bad));
        Assert.True(rate.Observe("bob", bad)); Assert.Equal(3200000, rate.Bitrate);
        for (int i = 0; i < 10; i++) Assert.False(rate.Observe("bob", bad));
        clock.Ticks += TimeSpan.FromSeconds(6).Ticks;
        Assert.True(rate.Observe("bob", bad)); Assert.Equal(2560000, rate.Bitrate);
        for (int i = 0; i < 40; i++) Assert.False(rate.Observe("bob", new(0, 0, 40)));
        Assert.Equal(2560000, rate.Bitrate);
    }
    [Fact]
    public void KeyFrameFloodIsLimitedToOnceASecondWithMonotonicTime()
    {
        var clock = new Clock(); var gate = new CommandRateGate(clock);
        Assert.True(gate.TryEnter());
        for (int i = 0; i < 1000; i++) Assert.False(gate.TryEnter());
        clock.Ticks += TimeSpan.FromSeconds(1).Ticks; Assert.True(gate.TryEnter());
    }
    private sealed class Clock : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }
}
