using Wisp.Core;
using Xunit;
namespace Wisp.Core.Tests;

public sealed class VoiceTrayStateTests
{
    [Theory]
    [InlineData(false, false, false, false, false, VoiceTrayState.Disconnected)]
    [InlineData(false, false, true, true, true, VoiceTrayState.Disconnected)]
    [InlineData(true, false, false, false, false, VoiceTrayState.Silent)]
    [InlineData(true, false, false, false, true, VoiceTrayState.Speaking)]
    [InlineData(true, false, true, false, true, VoiceTrayState.Muted)]
    [InlineData(true, false, true, true, true, VoiceTrayState.Deafened)]
    [InlineData(false, true, true, true, true, VoiceTrayState.Error)]
    public void RealVoiceStateDeterminesIndicator(bool connected, bool failed, bool muted, bool deafened, bool speaking, VoiceTrayState expected)
        => Assert.Equal(expected, VoiceTrayStateResolver.Resolve(connected, failed, muted, deafened, speaking));

    [Fact]
    public void FailureRecoveryAndVoluntaryLeaveClearTheRedIndicator()
    {
        Assert.Equal(VoiceTrayState.Error, VoiceTrayStateResolver.Resolve(false, true, false, false, false));
        Assert.Equal(VoiceTrayState.Silent, VoiceTrayStateResolver.Resolve(true, false, false, false, false));
        Assert.Equal(VoiceTrayState.Disconnected, VoiceTrayStateResolver.Resolve(false, false, true, false, false));
    }
}
