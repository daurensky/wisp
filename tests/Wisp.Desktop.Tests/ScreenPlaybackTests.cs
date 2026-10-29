using System.Collections;
using System.Reflection;
using NAudio.Wave;
using Wisp.Desktop;
using Wisp.Desktop.Media;
using Xunit;

namespace Wisp.Desktop.Tests;

public sealed class ScreenPlaybackTests
{
    [Fact]
    public void StreamGainChangesOnlyItsOwnPcmAndSurvivesPlaybackRemoval()
    {
        using var playback=new ShareAudioPlayback();
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var peerType=typeof(ShareAudioPlayback).GetNestedType("Peer",BindingFlags.NonPublic)!;
        var peers=(IDictionary)typeof(ShareAudioPlayback).GetField("peers",flags)!.GetValue(playback)!;
        var a=Activator.CreateInstance(peerType,nonPublic:true)!;var b=Activator.CreateInstance(peerType,nonPublic:true)!;
        peers.Add("alice",a);peers.Add("bob",b);
        var pcm=Enumerable.Repeat((short)8192,1920).ToArray();var bytes=new byte[pcm.Length*2];Buffer.BlockCopy(pcm,0,bytes,0,bytes.Length);
        foreach(var peer in new[]{a,b})((BufferedWaveProvider)peerType.GetField("Buffer")!.GetValue(peer)!).AddSamples(bytes,0,bytes.Length);
        playback.SetVolume("alice",.25f);
        var left=new float[480];var right=new float[480];
        ((ISampleProvider)peerType.GetProperty("Samples")!.GetValue(a)!).Read(left,0,left.Length);
        ((ISampleProvider)peerType.GetProperty("Samples")!.GetValue(b)!).Read(right,0,right.Length);
        Assert.All(left,value=>Assert.InRange(value,.0624f,.0626f));
        Assert.All(right,value=>Assert.InRange(value,.249f,.251f));
        playback.SetVolume("alice",0);((ISampleProvider)peerType.GetProperty("Samples")!.GetValue(a)!).Read(left,0,left.Length);Assert.All(left,value=>Assert.Equal(0,value));
        playback.SetVolume("alice",.35f);playback.SetMuted("alice",true);Assert.Equal(.35f,playback.GetVolume("alice"));
        playback.Remove("alice");Assert.Equal(.35f,playback.GetVolume("alice"));Assert.Equal(1,playback.GetVolume("bob"));
        playback.SetVolume("bad",float.NaN);Assert.Equal(1,playback.GetVolume("bad"));playback.SetVolume("bad",-1);Assert.Equal(0,playback.GetVolume("bad"));
    }

    [Fact]
    public void MutedAndDeafenedStreamsDoNotOpenAnAudioDevice()
    {
        using var playback=new ShareAudioPlayback();playback.SetVolume("source",.4f);
        playback.SetMuted("source",true);playback.Receive("source",[1,2,3]);
        playback.SetMuted("source",false);playback.Deafened=true;playback.Receive("source",[1,2,3]);
        Assert.Equal(.4f,playback.GetVolume("source"));
        Assert.Null(typeof(ShareAudioPlayback).GetField("output",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(playback));
    }

    [Fact]
    public void LaunchSelectionUsesChosenCaptureQualityAndRequiresAnActualSource()
    {
        var settings=new ScreenShareViewModel();Assert.False(settings.CanLaunch);Assert.Null(settings.Selection());
        bool changed=false;settings.PropertyChanged+=(_,args)=>changed|=args.PropertyName==nameof(settings.CanLaunch);
        var source=new CaptureSource("monitor:2","Second monitor");settings.Source=source;settings.ResolutionIndex=1;settings.FrameRateIndex=2;settings.BitrateIndex=3;
        Assert.True(changed);Assert.True(settings.CanLaunch);Assert.Equal(new ScreenShareSelection(source,1080,60,8_000_000),settings.Selection());
        settings.ResolutionIndex=0;settings.FrameRateIndex=0;settings.BitrateIndex=0;
        Assert.Equal(new ScreenShareSelection(source,720,15,2_000_000),settings.Selection());
        settings.Source=null;Assert.False(settings.CanLaunch);Assert.Null(settings.Selection());
    }
}
