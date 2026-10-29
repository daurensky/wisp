using System.Collections.Concurrent;
using System.IO;
using Wisp.Desktop.Media;
using Xunit;

namespace Wisp.Desktop.Tests;

public sealed class AudioWaveformCacheTests
{
    [Fact]
    public async Task AnalysisLimitsDecodersAndSharesInFlightWorkBeyondCompletedCacheCapacity()
    {
        using var files = new InputFiles(40);
        using var entered = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var calls = new ConcurrentDictionary<string, int>();
        int active = 0, maximumActive = 0;
        var cache = new AudioWaveformCache(path =>
        {
            calls.AddOrUpdate(path, 1, (_, count) => count + 1);
            int running = Interlocked.Increment(ref active);
            int previous;
            do { previous = Volatile.Read(ref maximumActive); }
            while (running > previous && Interlocked.CompareExchange(ref maximumActive, running, previous) != previous);
            entered.Release();
            try
            {
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Analysis was not released.");
                return Result();
            }
            finally { Interlocked.Decrement(ref active); }
        });
        var loads = files.Paths.Select(path => cache.LoadAsync(path)).ToArray();
        var sameFirst = cache.LoadAsync(files.Paths[0]);
        try
        {
            await entered.WaitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await entered.WaitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, Volatile.Read(ref active));
            Assert.Equal(2, calls.Count);
        }
        finally { release.Set(); }
        var results = await Task.WhenAll(loads).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(results[0], await sameFirst);
        Assert.Equal(2, maximumActive);
        Assert.Equal(files.Paths.Length, calls.Count);
        Assert.All(calls.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task CompletedCacheKeepsThirtyTwoResultsAndEvictsTheOldest()
    {
        using var files = new InputFiles(33);
        int calls = 0;
        var cache = new AudioWaveformCache(_ => { Interlocked.Increment(ref calls); return Result(); });
        var results = new List<AudioWaveformData>();
        foreach (string path in files.Paths) results.Add(await cache.LoadAsync(path));
        Assert.Same(results[^1], await cache.LoadAsync(files.Paths[^1]));
        Assert.NotSame(results[0], await cache.LoadAsync(files.Paths[0]));
        Assert.Equal(34, calls);
    }

    [Fact]
    public async Task CancellingOneConsumerKeepsSharedAnalysisAvailableToOtherConsumers()
    {
        using var files = new InputFiles(1);
        using var entered = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        var cache = new AudioWaveformCache(_ =>
        {
            Interlocked.Increment(ref calls); entered.Release();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Analysis was not released.");
            return Result();
        });
        var cancelled = cache.LoadAsync(files.Paths[0], cancellation.Token);
        var survivor = cache.LoadAsync(files.Paths[0]);
        try
        {
            await entered.WaitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            Assert.False(survivor.IsCompleted);
        }
        finally { release.Set(); }
        var result = await survivor.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(result, await cache.LoadAsync(files.Paths[0]));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FailedAnalysisCanRetryWithoutChangingTheFile()
    {
        using var files = new InputFiles(1);
        int calls = 0;
        var cache = new AudioWaveformCache(_ => Interlocked.Increment(ref calls) == 1
            ? throw new IOException("Temporary decoder failure.") : Result());
        await Assert.ThrowsAsync<IOException>(() => cache.LoadAsync(files.Paths[0]));
        var result = await cache.LoadAsync(files.Paths[0]);
        Assert.Same(result, await cache.LoadAsync(files.Paths[0]));
        Assert.Equal(2, calls);
    }

    private static AudioWaveformData Result() => new([.5f], TimeSpan.FromSeconds(1));

    private sealed class InputFiles : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "wisp-waveform-cache-" + Guid.NewGuid().ToString("N"));
        public string[] Paths { get; }
        public InputFiles(int count)
        {
            Directory.CreateDirectory(root);
            Paths = Enumerable.Range(0, count).Select(index => Path.Combine(root, index + ".wav")).ToArray();
            foreach (string path in Paths) File.WriteAllBytes(path, [0]);
        }
        public void Dispose() => Directory.Delete(root, true);
    }
}
