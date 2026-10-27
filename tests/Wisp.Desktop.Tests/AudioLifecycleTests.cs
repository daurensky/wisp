using System.Reflection;
using NAudio.Wave;
using Wisp.Desktop.Media;
using Xunit;
namespace Wisp.Desktop.Tests;

public sealed class AudioLifecycleTests
{
    [Fact]
    public async Task CancelledCaptureStillCatchesEndpointFailureOnItsNativeThread()
    {
        var type=typeof(VoiceService).Assembly.GetType("Wisp.Desktop.Media.CommunicationsCapture",true)!;
        using var input=(IWaveIn)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{"invalid-test-endpoint",(Action<string>)(_=>{})},null)!;
        var stopped=new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        input.RecordingStopped+=(_,args)=>stopped.TrySetResult(args.Exception);
        // Previously an endpoint exception after cancellation escaped a Thread
        // because the catch filter was false, terminating the test/app process.
        input.StopRecording();input.StartRecording();
        Assert.Null(await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
