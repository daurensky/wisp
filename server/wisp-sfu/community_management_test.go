package main

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"image"
	"image/color"
	"image/png"
	"strings"
	"testing"
)

func TestCommunityManagementRolesMediaAndRemoval(t *testing.T) {
	s := testStore(t)
	srv := testAPI(t, s)
	owner := registerTest(t, srv, "owner_manage")
	admin := registerTest(t, srv, "admin_manage")
	member := registerTest(t, srv, "member_manage")
	outsider := registerTest(t, srv, "outsider_manage")
	cid := "management"
	check := func(want int, method, path, token string, value any) json.RawMessage {
		t.Helper()
		status, raw := apiCall(t, srv, method, path, token, value)
		if status != want {
			t.Fatalf("%s %s: got %d want %d: %s", method, path, status, want, raw)
		}
		return raw
	}
	base := "/v1/data/communities/" + cid
	check(200, "PUT", base+"/snapshot", owner.AccessToken, snapshot(owner.Profile.ID, cid))
	for _, a := range []account{admin, member} {
		if _, err := s.db.Exec(context.Background(), `INSERT INTO wisp_members(community_id,user_id) VALUES($1,$2)`, cid, a.Profile.ID); err != nil {
			t.Fatal(err)
		}
	}
	management := base + "/management"
	check(403, "PUT", management, member.AccessToken, map[string]any{"Action": "rename", "Name": "stolen"})
	check(403, "PUT", management, outsider.AccessToken, map[string]any{"Action": "rename", "Name": "stolen"})
	check(200, "PUT", management, owner.AccessToken, map[string]any{"Action": "role", "UserId": admin.Profile.ID, "Role": "admin"})
	check(403, "PUT", management, admin.AccessToken, map[string]any{"Action": "role", "UserId": member.Profile.ID, "Role": "admin"})
	check(403, "PUT", management, admin.AccessToken, map[string]any{"Action": "kick", "UserId": owner.Profile.ID})
	check(200, "PUT", management, admin.AccessToken, map[string]any{"Action": "rename", "Name": "Managed community"})
	img := image.NewRGBA(image.Rect(0, 0, 2, 2))
	img.Set(0, 0, color.White)
	var encoded bytes.Buffer
	_ = png.Encode(&encoded, img)
	transfer := func(kind string) map[string]any {
		hash := sha256.New()
		hash.Write([]byte(cid + "\x00" + kind + "\x00"))
		hash.Write(encoded.Bytes())
		return map[string]any{"Id": hex.EncodeToString(hash.Sum(nil)), "Name": "icon.png", "Kind": kind, "Data": encoded.Bytes()}
	}
	icon := transfer("icon")
	check(200, "PUT", management, admin.AccessToken, map[string]any{"Action": "icon", "Asset": icon})
	snap := check(200, "GET", base+"/snapshot", member.AccessToken, nil)
	if !strings.Contains(string(snap), "Managed community") || !strings.Contains(string(snap), icon["Id"].(string)) || !strings.Contains(string(snap), `"admin"`) {
		t.Fatal("management changes not synchronized", string(snap))
	}
	check(403, "GET", base+"/icon", outsider.AccessToken, nil)
	emoji := transfer("emoji")
	assetPath := base + "/emojis/" + emoji["Id"].(string)
	check(403, "PUT", assetPath, member.AccessToken, emoji)
	check(200, "PUT", assetPath, admin.AccessToken, emoji)
	check(200, "PUT", management, owner.AccessToken, map[string]any{"Action": "renameAsset", "Kind": "emoji", "AssetId": emoji["Id"], "Name": "wave.png"})
	raw := check(200, "GET", assetPath, member.AccessToken, nil)
	if !strings.Contains(string(raw), "wave.png") {
		t.Fatal("asset rename missing")
	}
	check(403, "PUT", management, member.AccessToken, map[string]any{"Action": "deleteAsset", "Kind": "emoji", "AssetId": emoji["Id"]})
	check(200, "PUT", management, admin.AccessToken, map[string]any{"Action": "deleteAsset", "Kind": "emoji", "AssetId": emoji["Id"]})
	raw = check(200, "GET", assetPath, member.AccessToken, nil)
	if string(raw) != "null" {
		t.Fatal("asset deletion missing", string(raw))
	}
	check(200, "PUT", management, admin.AccessToken, map[string]any{"Action": "kick", "UserId": member.Profile.ID})
	check(403, "GET", base+"/snapshot", member.AccessToken, nil)
	check(200, "PUT", management, owner.AccessToken, map[string]any{"Action": "role", "UserId": admin.Profile.ID, "Role": "member"})
	check(403, "PUT", management, admin.AccessToken, map[string]any{"Action": "rename", "Name": "denied after demotion"})
}
