using System.IO;
using System.Collections;
using System.Reflection;
using NAudio.Wave;
using Wisp.Core;
using Wisp.Desktop.Media;
using Xunit;
namespace Wisp.Desktop.Tests;
public sealed class PeerVolumeTests
{
    [Fact]
    public void UserGainChangesOnlyTheirPcmAndPersistsAcrossRestart()
    {
        string root=Path.Combine(Path.GetTempPath(),"wisp-volume-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            using var voice=new VoiceService();voice.LoadPeerVolumes(root);
            var type=typeof(VoiceService).GetNestedType("PlaybackPeer",BindingFlags.NonPublic)!;
            var peer=Activator.CreateInstance(type,nonPublic:true)!;
            var peers=(IDictionary)typeof(VoiceService).GetField("peers",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(voice)!;
            peers.Add("alice",peer);
            var buffer=(PcmJitterBuffer)type.GetField("Buffer")!.GetValue(peer)!;
            buffer.Write(Enumerable.Repeat((short)8192,960*8).ToArray());
            voice.SetPeerVolume("alice",.5f);
            var samples=new float[960];((ISampleProvider)peer).Read(samples,0,samples.Length);
            Assert.InRange(samples.Max(),.12f,.13f);
            Assert.Equal(1f,voice.GetPeerVolume("bob"));
            voice.SetPeerVolume("alice",0);((ISampleProvider)peer).Read(samples,0,samples.Length);Assert.All(samples,s=>Assert.Equal(0,s));
            voice.SetPeerVolume("alice",.25f);voice.SavePeerVolumes();
            using var restored=new VoiceService();restored.LoadPeerVolumes(root);Assert.Equal(.25f,restored.GetPeerVolume("alice"));
            restored.SetPeerVolume("alice",-1);Assert.Equal(0,restored.GetPeerVolume("alice"));
            restored.SetPeerVolume("bob",float.NaN);Assert.Equal(1,restored.GetPeerVolume("bob"));
        }
        finally{Directory.Delete(root,true);}
    }
}
