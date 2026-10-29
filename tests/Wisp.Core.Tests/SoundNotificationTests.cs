using Wisp.Core;
using Xunit;
namespace Wisp.Core.Tests;

public sealed class SoundNotificationTests
{
    private static RtcSeat Seat(string user,string session="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")=>new(user,session,0);
    private static ChatMessage Message(string id,string author="remote")=>new(id,"text",author,author,"hello",DateTimeOffset.UtcNow);
    private static (SoundNotificationPolicy Policy,List<SoundEvent> Heard) Setup()
    {
        var heard=new List<SoundEvent>();var policy=new SoundNotificationPolicy("me");policy.Emitted+=heard.Add;return(policy,heard);
    }
    [Fact]
    public void AdmissionAcknowledgesLocalJoinOnceAndDoesNotReplayExistingUsers()
    {
        var(p,heard)=Setup();
        p.ObserveVoiceRoster("a",[Seat("remote")]);Assert.Empty(heard);
        p.ConfirmVoiceConnected("a",[Seat("me"),Seat("remote")]);
        p.ConfirmVoiceConnected("a",[Seat("me"),Seat("remote")]);
        Assert.Equal(new[]{SoundEvent.VoiceJoined},heard);
    }
    [Fact]
    public void RosterDiffsScopeEventsToActiveVoiceRoomAndIgnoreStateOnlyUpdates()
    {
        var(p,heard)=Setup();p.ConfirmVoiceConnected("a",[Seat("me")]);heard.Clear();
        p.ObserveVoiceRoster("b",[Seat("stranger")]);
        p.ObserveVoiceRoster("a",[Seat("me"),Seat("remote")]);
        p.ObserveVoiceRoster("a",[Seat("me") with{Muted=true},Seat("remote") with{Deafened=true}]);
        p.ObserveVoiceRoster("a",[Seat("me"),Seat("remote","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]);
        p.ObserveVoiceRoster("a",[Seat("me")]);
        Assert.Equal(new[]{SoundEvent.VoiceJoined,SoundEvent.VoiceLeft},heard);
    }
    [Fact]
    public void RecoveryEstablishesSilentBaselineAndVoluntaryExitAcknowledgesOnlyOnce()
    {
        var(p,heard)=Setup();p.ConfirmVoiceConnected("a",[Seat("me"),Seat("old")]);heard.Clear();
        p.ConnectionLost();p.ObserveVoiceRoster("a",[]);Assert.Empty(heard);
        p.ConfirmVoiceConnected("a",[Seat("me"),Seat("old"),Seat("during-gap")],recovered:true);
        Assert.Empty(heard);
        p.ObserveVoiceRoster("a",[Seat("me"),Seat("old"),Seat("during-gap"),Seat("new")]);
        p.ConfirmVoiceLeft();p.ConfirmVoiceLeft();
        Assert.Equal(new[]{SoundEvent.VoiceJoined,SoundEvent.VoiceLeft},heard);
    }
    [Fact]
    public void ControlSoundsAcknowledgeStateChangesAndDeafenTakesPrecedence()
    {
        var(p,heard)=Setup();p.ObserveLocalAudio(false,false,notify:false);
        p.ObserveLocalAudio(true,false);p.ObserveLocalAudio(true,false);
        p.ObserveLocalAudio(false,false);p.ObserveLocalAudio(true,true);
        p.ObserveLocalAudio(false,false);
        Assert.Equal(new[]{SoundEvent.MicMuted,SoundEvent.MicUnmuted,SoundEvent.Deafened,SoundEvent.Undeafened},heard);
    }
    [Fact]
    public void StreamAnnouncementsDeduplicateOwnEchoesAndRestoredSessions()
    {
        var(p,heard)=Setup();p.ConfirmVoiceConnected("a",[Seat("me")]);heard.Clear();
        p.ObserveStreamStarted("b","remote","irrelevant");
        p.ObserveStreamStarted("a","me","local");p.ObserveStreamStarted("a","me","local");
        p.ObserveStreamStarted("a","remote","existing",restoring:true);p.ObserveStreamStarted("a","remote","existing");
        p.ObserveStreamStarted("a","remote","fresh");
        Assert.Equal(new[]{SoundEvent.ScreenShareStarted,SoundEvent.ScreenShareStarted},heard);
    }
    [Fact]
    public void InitialHistoryOwnMessagesPollingAndDuplicateDeliveryStaySilent()
    {
        var(p,heard)=Setup();
        p.ObserveMessages("server/text",[Message("old")]);
        p.ObserveMessages("server/text",[Message("old"),Message("own","me"),Message("new")]);
        p.ObserveMessages("server/text",[Message("old"),Message("own","me"),Message("new")]);
        p.ObserveMessages("server/text",[Message("earlier-page")],history:true);
        p.ObserveMessages("server/text",[Message("earlier-page")]);
        p.ObserveMessages("other/text",[Message("other-history")]);
        Assert.Equal(new[]{SoundEvent.MessageReceived},heard);
    }
    [Fact]
    public void DeafenSuppressesRemoteNotificationsButRetainsLocalAcknowledgement()
    {
        var(p,heard)=Setup();p.ConfirmVoiceConnected("a",[Seat("me")]);p.ObserveMessages("a/text",[]);heard.Clear();
        p.ObserveLocalAudio(false,true);p.ObserveVoiceRoster("a",[Seat("me"),Seat("remote")]);
        p.ObserveStreamStarted("a","remote","s1");p.ObserveMessages("a/text",[Message("quiet")]);
        p.ObserveLocalAudio(false,false);p.ObserveMessages("a/text",[Message("quiet"),Message("audible")]);
        Assert.Equal(new[]{SoundEvent.Deafened,SoundEvent.Undeafened,SoundEvent.MessageReceived},heard);
    }
    [Fact]
    public void NotificationPreferencesPersistAndClampOnlyTheLocalCueVolume()
    {
        string root=Path.Combine(Path.GetTempPath(),"wisp-cue-settings-"+Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(new SoundNotificationPreferences(),SoundNotificationPreferences.Load(root));
            new SoundNotificationPreferences(false,4).Save(root);
            Assert.Equal(new SoundNotificationPreferences(false,1),SoundNotificationPreferences.Load(root));
            new SoundNotificationPreferences(true,float.NaN).Save(root);
            Assert.Equal(.8f,SoundNotificationPreferences.Load(root).Volume);
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
