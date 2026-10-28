using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wisp.Core;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class CommunityMetadataTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"wisp-community-metadata-"+Guid.NewGuid().ToString("N"));

    [Fact]
    public void CoverFramesAndSoundGainPersistWhileRemovedChannelsKeepCachedHistory()
    {
        string profileId,communityId,channelId,assetId,session;
        using(var store=new LocalStore(root))
        {
            var profile=store.Register("alice","fixture-password") with{AvatarCoverFrame=3,BannerCoverFrame=5};
            store.SaveProfile(profile);profileId=profile.Id;session=store.CreateSession(profile.Id);
            var community=store.CreateCommunity("Friends",profile.Id) with{IconCoverFrame=7};communityId=community.Id;
            var channels=store.GetChannels(communityId);channelId=channels.First(c=>!c.IsVoice).Id;
            store.SaveMessage(new("saved",channelId,profile.Id,profile.Username,"Preserved history",DateTimeOffset.UtcNow));
            store.SaveSnapshot(new(community,channels,[profile]));
            string file=Path.Combine(root,"sound.wav");File.WriteAllBytes(file,[1,2,3]);
            assetId=store.ImportAsset(community.Id,file,"sound").Id;
            store.SetAssetVolume(assetId,.25f);
            Assert.Throws<ArgumentOutOfRangeException>(()=>store.SetAssetVolume(assetId,float.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(()=>store.SaveProfile(profile with{AvatarCoverFrame=500}));
            store.SaveSnapshot(new(community,channels.Where(c=>c.Id!=channelId).ToList(),[profile]));
            Assert.DoesNotContain(store.GetChannels(communityId),c=>c.Id==channelId);
            Assert.Equal("Preserved history",Assert.Single(store.GetMessages(channelId)).Text);
        }
        using var restored=new LocalStore(root);
        Assert.Equal(3,restored.Login("alice","fixture-password")!.AvatarCoverFrame);
        Assert.Equal(5,restored.RestoreSession(session)!.BannerCoverFrame);
        Assert.Equal(profileId,restored.RestoreSession(session)!.Id);
        Assert.Equal(7,Assert.Single(restored.GetCommunities()).IconCoverFrame);
        Assert.Equal(.25f,restored.GetAsset(assetId)!.Volume);
        Assert.DoesNotContain(restored.GetChannels(communityId),c=>c.Id==channelId);
        Assert.Equal("Preserved history",Assert.Single(restored.GetMessages(channelId)).Text);
    }

    [Fact]
    public void OlderDocumentsDefaultToFirstCoverAndFullSoundVolume()
    {
        var profile=JsonSerializer.Deserialize<UserProfile>("{\"Id\":\"user\",\"Username\":\"tester\"}")!;
        var community=JsonSerializer.Deserialize<Community>("{\"Id\":\"community\",\"Name\":\"Friends\",\"OwnerId\":\"user\"}")!;
        var asset=JsonSerializer.Deserialize<AssetTransfer>("{\"Id\":\"id\",\"Name\":\"sound.wav\",\"Kind\":\"sound\",\"Data\":\"AQ==\"}")!;
        Assert.Equal(0,profile.AvatarCoverFrame);Assert.Equal(0,profile.BannerCoverFrame);
        Assert.Equal(0,community.IconCoverFrame);Assert.Equal(1f,asset.Volume);
    }

    [Fact]
    public async Task ExistingEmojiReuseDoesNotRequireAnUploadButNewEmojiStillDoes()
    {
        Directory.CreateDirectory(root);var file=Path.Combine(root,"emoji.gif");byte[] bytes=[1,2,3,4];await File.WriteAllBytesAsync(file,bytes);
        string id=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("community\0emoji\0").Concat(bytes).ToArray())).ToLowerInvariant();
        var asset=new MediaAsset(id,"community","wave.gif","emoji",file,bytes.Length);
        bool exists=true;int writes=0;
        using var backend=new ServerBackend(new("https://example.com:8443",new string('a',64)),new Handler(request=>
        {
            if(request.RequestUri!.AbsolutePath=="/v1/auth/register")return Reply(new{accessToken=new string('a',43),refreshToken=new string('b',43),expiresIn=900,profile=new UserProfile("member","tester")});
            Assert.Equal("/v1/data/communities/community/emojis/"+id,request.RequestUri.AbsolutePath);
            if(request.Method==HttpMethod.Get)return Reply(exists?new AssetTransfer(id,"renamed.gif","emoji",bytes):null);
            Assert.Equal(HttpMethod.Put,request.Method);writes++;
            return new(HttpStatusCode.Forbidden){Content=new StringContent("{}")};
        }));
        await backend.SignInAsync("tester@example.com","fixture-password",true,"tester");
        await CommunityAssets.UploadEmojiAsync(backend,asset);Assert.Equal(0,writes);
        exists=false;
        var error=await Assert.ThrowsAsync<ServerRequestException>(()=>CommunityAssets.UploadEmojiAsync(backend,asset));
        Assert.Equal(HttpStatusCode.Forbidden,error.Status);Assert.Equal(1,writes);
    }

    private static HttpResponseMessage Reply(object? value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value))};
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(send(request));}
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
