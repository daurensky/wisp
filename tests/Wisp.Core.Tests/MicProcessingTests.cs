using Wisp.Core;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class MicProcessingTests
{
    [Fact]
    public void StagesRunInOrderAndDisabledStageIsSkipped()
    {
        var calls = new List<int>();
        var first = new Fake(1, calls);
        var disabled = new Fake(2, calls) { Enabled = false };
        var last = new Fake(3, calls);
        using var chain = new MicProcessingChain(first, disabled, last);
        var frame = new short[960];
        Assert.True(chain.Process(frame));
        Assert.Equal(new[] { 1, 3 }, calls);
        Assert.Equal(13, frame[0]);
    }

    [Fact]
    public void PartialFailureRestoresInputQuarantinesStageAndReportsOnce()
    {
        var calls = new List<int>();
        var bad = new Fake(2, calls) { Fail = true };
        using var chain = new MicProcessingChain(new Fake(1, calls), bad, new Fake(3, calls));
        int notices = 0;
        chain.ProcessorFailed += (_, _) => { notices++; throw new InvalidOperationException("UI failure"); };
        var frame = new short[960];
        chain.Process(frame);
        Assert.Equal(13, frame[0]);
        Assert.False(bad.Enabled);
        bad.Enabled = true; // A failed native state cannot be revived by a UI toggle.
        Array.Clear(frame);
        chain.Process(frame);
        Assert.Equal(13, frame[0]);
        Assert.Equal(new[] { 1, 2, 3, 1, 3 }, calls);
        Assert.Equal(1, notices);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void MutedOrDeafenedFramesDoNotReachAnyProcessor(bool muted, bool deafened)
    {
        var calls = new List<int>();
        using var chain = new MicProcessingChain(new Fake(1, calls));
        var frame = Enumerable.Repeat((short)7, 960).ToArray();
        Assert.False(chain.Process(frame, muted, deafened));
        Assert.Empty(calls);
        Assert.All(frame, sample => Assert.Equal(7, sample));
    }

    [Fact]
    public void EmptyChainIsTransparentAndFrameSizeIsValidated()
    {
        using var chain = new MicProcessingChain();
        var frame = Enumerable.Repeat((short)-32768, 960).ToArray();
        Assert.True(chain.Process(frame));
        Assert.All(frame, sample => Assert.Equal(short.MinValue, sample));
        Assert.Throws<ArgumentException>(() => chain.Process(new short[480]));
    }

    [Fact]
    public void SteadyStateDoesNotAllocateAndDisposalOwnsStagesOnce()
    {
        var processor = new NoAllocationProcessor();
        var chain = new MicProcessingChain(processor);
        var frame = new short[960];
        for (int i = 0; i < 100; i++) chain.Process(frame);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) chain.Process(frame);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        chain.Dispose(); chain.Dispose();
        Assert.Equal(1, processor.Disposals);
        Assert.Throws<ObjectDisposedException>(() => chain.Process(frame));
    }

    private sealed class Fake(int id, List<int> calls) : IMicProcessor
    {
        public bool Enabled { get; set; } = true;
        public bool Fail { get; init; }
        public float Process(Span<short> frame)
        {
            calls.Add(id);
            frame[0] = (short)(frame[0] * 10 + id);
            if (Fail) throw new InvalidOperationException("Broken processor");
            return .5f;
        }
        public void Dispose() { }
    }

    private sealed class NoAllocationProcessor : IMicProcessor
    {
        public bool Enabled { get; set; } = true;
        public int Disposals;
        public float Process(Span<short> frame) => -1;
        public void Dispose() => Disposals++;
    }
}
