using System.Reflection;
using Wisp.Core;
using Wisp.Desktop.Media;
using Xunit;

namespace Wisp.Desktop.Tests;

public sealed class VideoLifecycleTests
{
    [Fact]
    public async Task CaptureAsyncDisposalWaitsForOwnedWorkWithoutBlockingCaller()
    {
        var capture = new ScreenShareService();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(ScreenShareService).GetField("worker", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(capture, completed.Task);
        var cleanup = capture.DisposeAsync().AsTask();
        try
        {
            Assert.False(capture.IsSharing);
            Assert.False(cleanup.IsCompleted);
        }
        finally { completed.TrySetResult(); }
        await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
        await capture.DisposeAsync();
    }

    [Fact]
    public async Task ReceiverAsyncDisposalFinishesItsWaitingWorkerAndRejectsLateFrames()
    {
        int frames = 0, errors = 0;
        var receiver = new ScreenVideoReceiver(new ScreenStreamInfo(640, 360, 30, 1_000_000),
            _ => Interlocked.Increment(ref frames), _ => Interlocked.Increment(ref errors));
        await receiver.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(receiver.Completion.IsCompletedSuccessfully);
        receiver.Enqueue([0, 0, 0, 1, 0x65]);
        await receiver.DisposeAsync();
        Assert.Equal(0, frames);
        Assert.Equal(0, errors);
    }
}
