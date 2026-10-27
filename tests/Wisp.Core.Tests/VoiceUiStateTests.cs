using Wisp.Core;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class VoiceUiStateTests
{
    [Fact]
    public void SpeechRequiresSustainedEnergyAndExpiresWithoutMorePackets()
    {
        var clock = new Clock();
        var activity = new SpeechActivity(clock);
        var silence = new short[960];
        var speech = Enumerable.Range(0, 960).Select(i => (short)(1000 * Math.Sin(i * .08))).ToArray();
        activity.Observe(silence); Assert.False(activity.Speaking);
        activity.Observe(speech); Assert.False(activity.Speaking);
        activity.Observe(silence); Assert.False(activity.Speaking);
        activity.Observe(speech); activity.Observe(speech); Assert.True(activity.Speaking);
        clock.Advance(239); Assert.True(activity.Speaking);
        clock.Advance(2); Assert.False(activity.Speaking);
        activity.Observe(speech); activity.Observe(speech); activity.Reset(); Assert.False(activity.Speaking);
    }
    [Fact]
    public void FullScaleSamplesDoNotOverflowAndQuietBackgroundIsNotSpeech()
    {
        var activity = new SpeechActivity();
        var quiet = Enumerable.Repeat((short)30, 960).ToArray();
        for (int i = 0; i < 20; i++) activity.Observe(quiet);
        Assert.False(activity.Speaking);
        var loud = Enumerable.Repeat(short.MinValue, 960).ToArray();
        activity.Observe(loud); activity.Observe(loud); Assert.True(activity.Speaking);
    }
    [Fact]
    public void PendingMembershipIsImmediateAndStaleCompletionsCannotRejoin()
    {
        var state = new VoiceJoinIntent();
        long a = state.Begin("a"); Assert.True(state.Connecting); Assert.Equal("a", state.ChannelId);
        long b = state.Begin("b"); Assert.False(state.Complete(a)); Assert.Equal("b", state.ChannelId);
        Assert.True(state.Complete(b)); Assert.False(state.Connecting);
        long c = state.Begin("c"); state.Leave(); Assert.False(state.Complete(c)); Assert.Null(state.ChannelId);
    }
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => ticks;
        public void Advance(int milliseconds) => ticks += milliseconds;
    }
}
