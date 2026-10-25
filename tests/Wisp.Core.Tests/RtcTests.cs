using System.Buffers.Binary;
using Wisp.Core;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class RtcTests
{
    [Fact]
    public void OpusCodecRoundTripsOneTwentyMillisecondFrame()
    {
        var encoder = Concentus.OpusCodecFactory.CreateEncoder(48000, 1, Concentus.Enums.OpusApplication.OPUS_APPLICATION_VOIP);
        var decoder = Concentus.OpusCodecFactory.CreateDecoder(48000, 1);
        try
        {
            encoder.Bitrate = 32000;
            var pcm = Enumerable.Range(0, 960).Select(i => (short)(3000 * Math.Sin(2 * Math.PI * 440 * i / 48000))).ToArray();
            var packet = new byte[1275]; var decoded = new short[960];
            var size = encoder.Encode(pcm.AsSpan(), 960, packet.AsSpan(), packet.Length);
            Assert.InRange(size, 1, 1275);
            Assert.Equal(960, decoder.Decode(packet.AsSpan(0, size), decoded.AsSpan(), 960, false));
            Assert.Contains(decoded, sample => sample != 0);
        }
        finally { (encoder as IDisposable)?.Dispose(); (decoder as IDisposable)?.Dispose(); }
    }
    [Fact]
    public void FragmentsReassembleAndRejectOversizedAllocations()
    {
        var original = new byte[100000]; Random.Shared.NextBytes(original);
        var receiver = new RtcFragments(200000); byte[]? result = null;
        foreach (var chunk in RtcFragments.Encode(original)) result = receiver.Receive(chunk);
        Assert.Equal(original, result);
        var bad = RtcFragments.Encode(new byte[1]).First();
        BinaryPrimitives.WriteInt32BigEndian(bad.AsSpan(16), int.MaxValue);
        Assert.Throws<InvalidDataException>(() => receiver.Receive(bad));
    }
    [Fact]
    public void MissingFragmentsDiscardTheFrameAndNextFrameRecovers()
    {
        var receiver = new RtcFragments(100000);
        var packets = RtcFragments.Encode(new byte[40000]).ToArray();
        Assert.Null(receiver.Receive(packets[0]));
        Assert.Null(receiver.Receive(packets[2]));
        var next = new byte[] { 1, 2, 3 };
        Assert.Equal(next, receiver.Receive(RtcFragments.Encode(next).Single()));
    }
    [Fact]
    public async Task TwoPeersConnectDirectlyAndTransferOpusScreenAndFragmentedControl()
    {
        using var a = new RtcPeer("bob", "room", useGoogleStun: false);
        using var b = new RtcPeer("alice", "room", useGoogleStun: false);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(string _) { if (a.Connected && b.Connected) ready.TrySetResult(); }
        a.StateChanged += Changed; b.StateChanged += Changed;
        var offer = await a.OfferAsync(); var answer = await b.AnswerAsync(offer); a.AcceptAnswer(answer);
        if (a.Connected && b.Connected) ready.TrySetResult();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var opus = new TaskCompletionSource<WireMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var image = new TaskCompletionSource<WireMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var file = new TaskCompletionSource<WireMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.MessageReceived += m => { if (m.Type == "audio") opus.TrySetResult(m); if (m.Type == "screen") image.TrySetResult(m); if (m.Type == "asset") file.TrySetResult(m); };
        await a.SendAsync(new("audio", "forged", "wrong", new byte[] { 1, 2, 3 }));
        var audio = await opus.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("alice", audio.SenderId); Assert.Equal("room", audio.ChannelId); Assert.Equal(new byte[] { 1, 2, 3 }, audio.Payload);
        var jpeg = new byte[30000]; Random.Shared.NextBytes(jpeg);
        await a.SendAsync(new("screen", "forged", "wrong", jpeg));
        Assert.Equal(jpeg, (await image.Task.WaitAsync(TimeSpan.FromSeconds(10))).Payload);
        var payload = new byte[256000]; Random.Shared.NextBytes(payload);
        await a.SendAsync(new("asset", "forged", null, payload));
        Assert.Equal(payload, (await file.Task.WaitAsync(TimeSpan.FromSeconds(10))).Payload);
    }
}
