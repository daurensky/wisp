package main

import (
	"context"
	"crypto/sha256"
	"encoding/base64"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"strings"
	"sync"
	"testing"
	"time"
)

func TestSettingsPermissionsChannelsSoundVolumeAndCovers(t *testing.T) {
	s := testStore(t)
	srv := testAPI(t, s)
	owner := registerTest(t, srv, "settings_owner")
	member := registerTest(t, srv, "settings_member")
	cid := "settings"
	base := "/v1/data/communities/" + cid
	call := func(want int, method, path, token string, value any) json.RawMessage {
		t.Helper()
		status, raw := apiCall(t, srv, method, path, token, value)
		if status != want {
			t.Fatalf("%s %s: got %d want %d: %s", method, path, status, want, raw)
		}
		return raw
	}
	call(200, "PUT", base+"/snapshot", owner.AccessToken, snapshot(owner.Profile.ID, cid))
	if _, err := s.db.Exec(context.Background(), `INSERT INTO wisp_members(community_id,user_id) VALUES($1,$2)`, cid, member.Profile.ID); err != nil {
		t.Fatal(err)
	}
	management := base + "/management"
	call(403, "GET", management, member.AccessToken, nil)
	call(200, "GET", management, owner.AccessToken, nil)
	for _, action := range []string{"createChannel", "renameChannel", "deleteChannel"} {
		call(403, "PUT", management, member.AccessToken, map[string]any{"Action": action, "ChannelId": "new-text", "Name": "Renamed"})
	}
	call(200, "PUT", management, owner.AccessToken, map[string]any{"Action": "createChannel", "ChannelId": "new-text", "Name": "Новый текстовый", "IsVoice": false})
	call(200, "PUT", management, owner.AccessToken, map[string]any{"Action": "createChannel", "ChannelId": "new-voice", "Name": "Голосовой", "IsVoice": true})
	call(409, "PUT", management, owner.AccessToken, map[string]any{"Action": "createChannel", "ChannelId": "new-text", "Name": "duplicate"})
	call(200, "PUT", management, owner.AccessToken, map[string]any{"Action": "renameChannel", "ChannelId": "new-text", "Name": "Переименован"})
	var doc struct {
		Channels []struct {
			Id, Name string
			IsVoice  bool
		}
	}
	if err := json.Unmarshal(call(200, "GET", base+"/snapshot", member.AccessToken, nil), &doc); err != nil {
		t.Fatal(err)
	}
	text, voice := false, false
	for _, channel := range doc.Channels {
		if channel.Id == "new-text" {
			text = channel.Name == "Переименован" && !channel.IsVoice
		}
		if channel.Id == "new-voice" {
			voice = channel.IsVoice
		}
	}
	if !text || !voice {
		t.Fatal("channel type/name update missing", doc)
	}
	var create sync.WaitGroup
	statuses := make(chan int, 2)
	for _, id := range []string{"concurrent-a", "concurrent-b"} {
		create.Add(1)
		go func(id string) {
			defer create.Done()
			status, _ := apiCall(t, srv, "PUT", management, owner.AccessToken, map[string]any{"Action": "createChannel", "ChannelId": id, "Name": id, "IsVoice": false})
			statuses <- status
		}(id)
	}
	create.Wait()
	close(statuses)
	for status := range statuses {
		if status != 200 {
			t.Fatal("concurrent create failed", status)
		}
	}
	rawChannels := call(200, "GET", base+"/snapshot", member.AccessToken, nil)
	if !strings.Contains(string(rawChannels), "concurrent-a") || !strings.Contains(string(rawChannels), "concurrent-b") {
		t.Fatal("concurrent channel updates overwrote each other")
	}

	message := map[string]any{"Id": "keep-history", "ChannelId": "new-text", "AuthorId": member.Profile.ID, "AuthorName": member.Profile.Username, "Text": "history", "SentAt": time.Now()}
	call(200, "PUT", base+"/messages/new-text/keep-history", member.AccessToken, message)
	call(200, "PUT", management, owner.AccessToken, map[string]any{"Action": "deleteChannel", "ChannelId": "new-text"})
	call(404, "GET", base+"/messages/new-text", member.AccessToken, nil)
	call(403, "PUT", base+"/messages/new-text/keep-history", member.AccessToken, message)
	if raw, _, err := s.getDocument(context.Background(), "communities/"+cid+"/messages/new-text/keep-history"); err != nil || !strings.Contains(string(raw), "history") {
		t.Fatal("channel removal destroyed retained history", err)
	}

	wav := make([]byte, 52)
	copy(wav, "RIFF")
	binary.LittleEndian.PutUint32(wav[4:], 44)
	copy(wav[8:], "WAVEfmt ")
	binary.LittleEndian.PutUint32(wav[16:], 16)
	binary.LittleEndian.PutUint16(wav[20:], 1)
	binary.LittleEndian.PutUint16(wav[22:], 1)
	binary.LittleEndian.PutUint32(wav[24:], 48000)
	binary.LittleEndian.PutUint32(wav[28:], 96000)
	binary.LittleEndian.PutUint16(wav[32:], 2)
	binary.LittleEndian.PutUint16(wav[34:], 16)
	copy(wav[36:], "data")
	binary.LittleEndian.PutUint32(wav[40:], 8)
	hash := sha256.New()
	hash.Write([]byte(cid + "\x00sound\x00"))
	hash.Write(wav)
	soundId := hex.EncodeToString(hash.Sum(nil))
	sound := map[string]any{"Id": soundId, "Name": "cue.wav", "Kind": "sound", "Data": wav, "Volume": 0.25}
	call(200, "PUT", base+"/sounds/"+soundId, owner.AccessToken, sound)
	call(403, "PUT", management, member.AccessToken, map[string]any{"Action": "renameAsset", "Kind": "sound", "AssetId": soundId, "Volume": 0.5})
	call(400, "PUT", management, owner.AccessToken, map[string]any{"Action": "renameAsset", "Kind": "sound", "AssetId": soundId, "Volume": 2})
	call(200, "PUT", management, owner.AccessToken, map[string]any{"Action": "renameAsset", "Kind": "sound", "AssetId": soundId, "Volume": 0.6})
	var stored struct {
		Name   string
		Volume float64
	}
	if err := json.Unmarshal(call(200, "GET", base+"/sounds/"+soundId, member.AccessToken, nil), &stored); err != nil || stored.Volume != 0.6 || stored.Name != "cue.wav" {
		t.Fatal("sound gain-only edit changed name or failed", stored, err)
	}
	profile := map[string]any{"Id": owner.Profile.ID, "Username": owner.Profile.Username, "About": "cover", "AvatarCoverFrame": 1, "BannerCoverFrame": 2}
	call(200, "PUT", "/v1/data/profiles/"+owner.Profile.ID+"/Info", owner.AccessToken, profile)
	var saved struct{ AvatarCoverFrame, BannerCoverFrame int }
	if err := json.Unmarshal(call(200, "GET", "/v1/data/profiles/"+owner.Profile.ID+"/Info", member.AccessToken, nil), &saved); err != nil || saved.AvatarCoverFrame != 1 || saved.BannerCoverFrame != 2 {
		t.Fatal("profile covers missing", saved, err)
	}
	profile["AvatarCoverFrame"] = 500
	call(400, "PUT", "/v1/data/profiles/"+owner.Profile.ID+"/Info", owner.AccessToken, profile)
	gifBytes, err := base64.StdEncoding.DecodeString("R0lGODlhAgACAIEAAP8AAAAAAAAAAAAAACH/C05FVFNDQVBFMi4wAwEBAAAh+QQACAAAACwAAAAAAgACAAAIBgABCAQQEAAh+QQBCAABACwAAAAAAgACAIEAAP8AAAAAAAAAAAAIBgABCAQQEAA7")
	if err != nil {
		t.Fatal(err)
	}
	iconHash := sha256.New()
	iconHash.Write([]byte(cid + "\x00icon\x00"))
	iconHash.Write(gifBytes)
	icon := map[string]any{"Id": hex.EncodeToString(iconHash.Sum(nil)), "Name": "icon.gif", "Kind": "icon", "Data": gifBytes, "Volume": 1}
	call(200, "PUT", management, owner.AccessToken, map[string]any{"Action": "icon", "Asset": icon, "CoverFrame": 1})
	var iconSnapshot struct{ Community struct{ IconCoverFrame int } }
	if err = json.Unmarshal(call(200, "GET", base+"/snapshot", member.AccessToken, nil), &iconSnapshot); err != nil || iconSnapshot.Community.IconCoverFrame != 1 {
		t.Fatal("server icon cover missing", err)
	}
	call(400, "PUT", management, owner.AccessToken, map[string]any{"Action": "icon", "Asset": icon, "CoverFrame": 500})

}

func TestDeletingVoiceChannelClosesItsSessions(t *testing.T) {
	h, srv := testHub(t, 0)
	_, ready := joinTest(t, srv, "a", "fixture")
	if ready.Type != "ready" {
		t.Fatal("join failed")
	}
	h.mu.Lock()
	var live *session
	for current := range h.sessions {
		live = current
	}
	h.mu.Unlock()
	h.removeCommunityChannel("other", "voice")
	select {
	case <-live.ctx.Done():
		t.Fatal("unrelated channel removal closed the call")
	default:
	}
	h.removeCommunityChannel("community", "voice")
	select {
	case <-live.ctx.Done():
	case <-time.After(time.Second):
		t.Fatal("deleted voice channel retained active session")
	}
}
