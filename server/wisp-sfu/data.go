package main

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"github.com/jackc/pgx/v5"
	"image"
	_ "image/gif"
	_ "image/jpeg"
	_ "image/png"
	"net/http"
	"path/filepath"
	"regexp"
	"strings"
	"time"
)

func (s *store) data(w http.ResponseWriter, r *http.Request) {
	select {
	case s.requests <- struct{}{}:
		defer func() { <-s.requests }()
	default:
		apiError(w, 429, "Server busy; retry shortly")
		return
	}
	controller := http.NewResponseController(w)
	_ = controller.SetReadDeadline(time.Now().Add(20 * time.Second))
	defer controller.SetReadDeadline(time.Time{})
	uid, ok := s.bearer(w, r)
	if !ok {
		return
	}
	path := strings.TrimPrefix(r.URL.Path, "/v1/data/")
	parts := strings.Split(path, "/")
	if len(parts) < 2 || len(parts) > 7 {
		apiError(w, 400, "Invalid data path")
		return
	}
	for _, p := range parts {
		if !keyPattern.MatchString(p) {
			apiError(w, 400, "Invalid data path")
			return
		}
	}
	ctx, cancel := context.WithTimeout(r.Context(), 12*time.Second)
	defer cancel()
	if r.Method == "GET" {
		var result any
		var err error
		var version int64
		switch parts[0] {
		case "users":
			if len(parts) != 3 || parts[1] != uid || parts[2] != "communities" {
				apiError(w, 403, "Access denied")
				return
			}
			rows, e := s.db.Query(ctx, `SELECT community_id FROM wisp_members WHERE user_id=$1`, uid)
			if e != nil {
				err = e
				break
			}
			defer rows.Close()
			m := map[string]bool{}
			for rows.Next() {
				var id string
				if rows.Scan(&id) != nil {
					err = denied
					break
				}
				m[id] = true
			}
			result = m
		case "profiles":
			p, e := s.profile(ctx, parts[1])
			if e != nil {
				result = nil
				break
			}
			if len(parts) == 2 {
				var v map[string]json.RawMessage
				_ = json.Unmarshal(p, &v)
				v["Info"] = p
				result = v
				break
			}
			if parts[2] == "Info" {
				result = jsonChild(p, parts[3:])
				break
			}
			if parts[2] == "Id" || parts[2] == "Username" {
				result = jsonChild(p, parts[2:])
				break
			}
			if len(parts) < 4 || parts[2] != "Images" || (parts[3] != "avatar" && parts[3] != "banner") {
				apiError(w, 404, "Unknown profile field")
				return
			}
			root := strings.Join(parts[:4], "/")
			var raw json.RawMessage
			raw, version, err = s.getDocument(ctx, root)
			result = jsonChild(raw, parts[4:])
		case "communities":
			cid := parts[1]
			if len(parts) < 3 || !s.member(ctx, cid, uid) {
				apiError(w, 403, "Not a community member")
				return
			}
			switch parts[2] {
			case "presence":
				if len(parts) != 3 {
					apiError(w, 404, "Unknown presence path")
					return
				}
				result = s.livePresence(cid)
			case "rtcRooms":
				if s.hub == nil {
					result = map[string]any{}
					break
				}
				rooms := s.hub.rooms(cid)
				if len(parts) == 3 {
					result = rooms
				} else if len(parts) == 4 {
					result = rooms[parts[3]]
				} else {
					apiError(w, 404, "Unknown room path")
					return
				}
			case "members":
				rows, e := s.db.Query(ctx, `SELECT user_id FROM wisp_members WHERE community_id=$1`, cid)
				if e != nil {
					err = e
					break
				}
				defer rows.Close()
				m := map[string]bool{}
				for rows.Next() {
					var id string
					if rows.Scan(&id) != nil {
						err = denied
						break
					}
					m[id] = true
				}
				result = m
			case "icon":
				raw, v, e := s.getDocument(ctx, "communities/"+cid+"/icon")
				result, version, err = raw, v, e
			case "snapshot":
				raw, _, e := s.getDocument(ctx, strings.Join(parts[:3], "/"))
				if e != nil {
					err = e
					break
				}
				if len(parts) > 3 {
					result = jsonChild(raw, parts[3:])
					break
				}
				var doc map[string]json.RawMessage
				if json.Unmarshal(raw, &doc) != nil {
					result = nil
					break
				}
				rows, e := s.db.Query(ctx, `SELECT u.profile FROM wisp_users u JOIN wisp_members m ON m.user_id=u.id WHERE m.community_id=$1 ORDER BY u.id`, cid)
				if e != nil {
					err = e
					break
				}
				defer rows.Close()
				members := []json.RawMessage{}
				for rows.Next() {
					var p json.RawMessage
					if rows.Scan(&p) != nil {
						err = denied
						break
					}
					members = append(members, p)
				}
				b, _ := json.Marshal(members)
				doc["Members"] = b
				roles := map[string]string{}
				roleRows, e := s.db.Query(ctx, `SELECT m.user_id,CASE WHEN c.owner_id=m.user_id THEN 'owner' ELSE m.role END FROM wisp_members m JOIN wisp_communities c ON c.id=m.community_id WHERE m.community_id=$1`, cid)
				if e != nil {
					err = e
					break
				}
				for roleRows.Next() {
					var id, role string
					if roleRows.Scan(&id, &role) != nil {
						err = denied
						break
					}
					roles[id] = role
				}
				roleRows.Close()
				doc["Roles"], _ = json.Marshal(roles)
				result = doc
			case "messages", "files", "emojis", "sounds":
				rootLen := documentRoot(parts)
				if len(parts) < rootLen || len(parts) == 3 || parts[2] == "messages" && len(parts) == 4 {
					result, err = s.listDocuments(ctx, path, 100)
					break
				}
				root := strings.Join(parts[:rootLen], "/")
				var raw json.RawMessage
				raw, version, err = s.getDocument(ctx, root)
				result = jsonChild(raw, parts[rootLen:])
			default:
				apiError(w, 404, "Unknown data path")
				return
			}
		default:
			apiError(w, 404, "Unknown data path")
			return
		}
		if err != nil {
			apiError(w, 503, "Database unavailable")
			return
		}
		w.Header().Set("ETag", fmt.Sprintf(`"%d"`, version))
		writeJSON(w, 200, result)
		return
	}
	if r.Method != "PUT" && r.Method != "DELETE" {
		apiError(w, 405, "PUT or DELETE required")
		return
	}
	if parts[0] == "communities" && len(parts) == 4 && parts[2] == "presence" && parts[3] == uid {
		if !s.member(ctx, parts[1], uid) {
			apiError(w, 403, "Not a community member")
			return
		}
		if r.Method == "DELETE" {
			s.presenceMu.Lock()
			delete(s.presence[parts[1]], uid)
			s.presenceMu.Unlock()
			writeJSON(w, 200, nil)
			return
		}
		var input map[string]json.RawMessage
		if !readJSON(w, r, &input, 32768) {
			return
		}
		p, err := s.profile(ctx, uid)
		if err != nil {
			apiError(w, 503, "Database unavailable")
			return
		}
		now, _ := json.Marshal(time.Now().UnixMilli())
		input["Profile"] = p
		input["SeenAt"] = now
		input["ChannelId"] = json.RawMessage("null")
		value, _ := json.Marshal(input)
		s.presenceMu.Lock()
		if s.presence[parts[1]] == nil {
			s.presence[parts[1]] = map[string]json.RawMessage{}
		}
		s.presence[parts[1]][uid] = value
		s.presenceMu.Unlock()
		writeJSON(w, 200, true)
		return
	}
	if parts[0] == "communities" && len(parts) == 3 && parts[2] == "management" {
		if r.Method != "PUT" {
			apiError(w, 405, "PUT required")
			return
		}
		s.manageCommunity(w, r, ctx, uid, parts[1])
		return
	}
	if r.Method == "DELETE" && parts[0] == "profiles" && len(parts) == 4 && parts[1] == uid && parts[2] == "Images" && (parts[3] == "avatar" || parts[3] == "banner") {
		if _, err := s.db.Exec(ctx, `DELETE FROM wisp_documents WHERE path=$1 AND author_id=$2`, path, uid); err != nil {
			apiError(w, 503, "Database unavailable")
			return
		}
		writeJSON(w, 200, true)
		return
	}
	if r.Method == "DELETE" {
		apiError(w, 403, "Deletion not supported for this resource")
		return
	}
	var value json.RawMessage
	if !readJSON(w, r, &value, 6*1024*1024) {
		return
	}
	if parts[0] == "profiles" && len(parts) == 3 && parts[1] == uid && parts[2] == "Info" {
		var p map[string]any
		if json.Unmarshal(value, &p) != nil || p["Id"] != uid {
			apiError(w, 400, "Invalid profile")
			return
		}
		name, _ := p["Username"].(string)
		about, _ := p["About"].(string)
		if !usernamePattern.MatchString(name) || len(about) > 8000 {
			apiError(w, 400, "Invalid profile name or about text")
			return
		}
		clean := map[string]any{"Id": uid, "Username": name, "About": about, "Status": "online", "AvatarPath": nil, "BannerPath": nil}
		if status, ok := p["Status"].(string); ok {
			if status != "online" && status != "idle" && status != "dnd" && status != "invisible" && status != "offline" {
				apiError(w, 400, "Invalid status")
				return
			}
			clean["Status"] = status
		}
		if game, ok := p["Game"].(string); ok {
			if len(game) > 256 {
				apiError(w, 400, "Game name too long")
				return
			}
			clean["Game"] = game
		}
		for _, key := range []string{"AvatarRevision", "AvatarAssetId", "BannerAssetId"} {
			if value, ok := p[key].(string); ok {
				if !hashPattern.MatchString(value) {
					apiError(w, 400, "Invalid media identifier")
					return
				}
				clean[key] = value
			}
		}
		raw, _ := json.Marshal(clean)
		if _, err := s.db.Exec(ctx, `UPDATE wisp_users SET profile=$1 WHERE id=$2`, raw, uid); err != nil {
			apiError(w, 503, "Database unavailable")
			return
		}
		writeJSON(w, 200, true)
		return
	}
	if parts[0] == "communities" && len(parts) == 3 && parts[2] == "snapshot" {
		if err := s.saveSnapshot(ctx, uid, parts[1], value); err != nil {
			apiError(w, 403, "Only the owner can save a valid community snapshot")
			return
		}
		writeJSON(w, 200, true)
		return
	}
	if !s.validDocument(ctx, uid, parts, value) {
		apiError(w, 403, "Resource is invalid or access is denied")
		return
	}
	etag := r.Header.Get("If-Match")
	tx, err := s.db.Begin(ctx)
	if err != nil {
		apiError(w, 503, "Database unavailable")
		return
	}
	defer tx.Rollback(ctx)
	// A path-scoped lock makes create-only uploads and ownership checks atomic.
	if _, err = tx.Exec(ctx, `SELECT pg_advisory_xact_lock(hashtextextended($1,0))`, path); err != nil {
		apiError(w, 503, "Database unavailable")
		return
	}
	var old json.RawMessage
	var author string
	var version int64
	err = tx.QueryRow(ctx, `SELECT data,author_id,version FROM wisp_documents WHERE path=$1`, path).Scan(&old, &author, &version)
	if err != nil && err != pgx.ErrNoRows {
		apiError(w, 503, "Database unavailable")
		return
	}
	if etag == "null_etag" && err != pgx.ErrNoRows || etag != "" && etag != "null_etag" && etag != fmt.Sprintf(`"%d"`, version) {
		apiError(w, 412, "Resource already exists or changed")
		return
	}
	if author != "" && author != uid {
		apiError(w, 409, "Resource belongs to another uploader")
		return
	}
	if len(old) > 0 && parts[0] == "communities" && parts[2] != "messages" {
		var a, b any
		if json.Unmarshal(old, &a) != nil || json.Unmarshal(value, &b) != nil {
			apiError(w, 400, "Invalid JSON")
			return
		}
		aa, _ := json.Marshal(a)
		bb, _ := json.Marshal(b)
		if !bytes.Equal(aa, bb) {
			apiError(w, 409, "Published content is immutable")
			return
		}
		writeJSON(w, 200, true)
		return
	}
	if _, err = tx.Exec(ctx, `INSERT INTO wisp_documents(path,author_id,data) VALUES($1,$2,$3) ON CONFLICT(path) DO UPDATE SET data=excluded.data,version=nextval('wisp_doc_version')`, path, uid, value); err != nil {
		apiError(w, 503, "Database unavailable")
		return
	}
	if tx.Commit(ctx) != nil {
		apiError(w, 503, "Database unavailable")
		return
	}
	writeJSON(w, 200, true)
}
func (s *store) saveSnapshot(ctx context.Context, uid, cid string, data json.RawMessage) error {
	var snap struct {
		Community struct{ ID, Name, OwnerID, IconAssetID string }
		Channels  []struct {
			ID, CommunityID, Name string
			IsVoice               bool
		}
	}
	// Existing .NET property casing includes Id/OwnerId, handled case-insensitively by encoding/json.
	if json.Unmarshal(data, &snap) != nil || snap.Community.ID != cid || !keyPattern.MatchString(snap.Community.OwnerID) || len(snap.Community.Name) < 1 || len(snap.Community.Name) > 100 || len(snap.Channels) > 500 {
		return denied
	}
	ids := map[string]bool{}
	for _, ch := range snap.Channels {
		if !keyPattern.MatchString(ch.ID) || ch.CommunityID != cid || len(ch.Name) < 1 || len(ch.Name) > 100 || ids[ch.ID] {
			return denied
		}
		ids[ch.ID] = true
	}
	channels := []map[string]any{}
	for _, ch := range snap.Channels {
		channels = append(channels, map[string]any{"Id": ch.ID, "CommunityId": cid, "Name": ch.Name, "IsVoice": ch.IsVoice})
	}
	data, _ = json.Marshal(map[string]any{"Community": map[string]any{"Id": cid, "OwnerId": snap.Community.OwnerID, "Name": snap.Community.Name, "IconAssetId": snap.Community.IconAssetID}, "Channels": channels, "Members": []any{}})
	tx, err := s.db.Begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback(ctx)
	_, err = tx.Exec(ctx, `INSERT INTO wisp_communities(id,owner_id) VALUES($1,$2) ON CONFLICT DO NOTHING`, cid, uid)
	if err != nil {
		return err
	}
	var owner string
	if tx.QueryRow(ctx, `SELECT owner_id FROM wisp_communities WHERE id=$1 FOR UPDATE`, cid).Scan(&owner) != nil || owner != snap.Community.OwnerID || (owner != uid && !s.manager(ctx, cid, uid)) {
		return denied
	}
	if _, err = tx.Exec(ctx, `INSERT INTO wisp_members(community_id,user_id) VALUES($1,$2) ON CONFLICT DO NOTHING`, cid, uid); err != nil {
		return err
	}
	if _, err = tx.Exec(ctx, `INSERT INTO wisp_documents(path,author_id,data) VALUES($1,$2,$3) ON CONFLICT(path) DO UPDATE SET data=excluded.data,version=nextval('wisp_doc_version')`, "communities/"+cid+"/snapshot", uid, data); err != nil {
		return err
	}
	return tx.Commit(ctx)
}
func (s *store) validDocument(ctx context.Context, uid string, p []string, data json.RawMessage) bool {
	if p[0] == "profiles" {
		if len(p) != 4 || p[1] != uid || p[2] != "Images" || (p[3] != "avatar" && p[3] != "banner") {
			return false
		}
		return validateTransfer(data, "", p[3], 4*1024*1024, "")
	}
	if p[0] != "communities" || !s.member(ctx, p[1], uid) || len(p) < 4 {
		return false
	}
	cid := p[1]
	switch p[2] {
	case "messages":
		if len(p) != 5 || !s.channelExists(ctx, cid, p[3]) {
			return false
		}
		var m struct {
			ID, ChannelID, AuthorID, AuthorName, Text string
			SentAt                                    time.Time
			AttachmentID                              *string
			Emojis                                    map[string]string
		}
		if json.Unmarshal(data, &m) != nil || m.ID != p[4] || m.ChannelID != p[3] || m.AuthorID != uid || len(m.Text) > 32000 || len(m.AuthorName) > 128 || m.SentAt.IsZero() || len(m.Emojis) > 32 {
			return false
		}
		for token, id := range m.Emojis {
			if !emojiTokenPattern.MatchString(token) || !hashPattern.MatchString(id) {
				return false
			}
			asset, _, err := s.getDocument(ctx, "communities/"+cid+"/emojis/"+id)
			if err != nil || string(asset) == "null" {
				return false
			}
		}
		if m.AttachmentID != nil {
			if !hashPattern.MatchString(*m.AttachmentID) {
				return false
			}
			raw, _, err := s.getDocument(ctx, "communities/"+cid+"/files/"+*m.AttachmentID+"/Info")
			if err != nil || string(raw) == "null" {
				return false
			}
		}
		return true
	case "emojis":
		return s.manager(ctx, cid, uid) && len(p) == 4 && hashPattern.MatchString(p[3]) && validateTransfer(data, cid, "emoji", 256*1024, p[3])
	case "sounds":
		return s.manager(ctx, cid, uid) && len(p) == 4 && hashPattern.MatchString(p[3]) && validateTransfer(data, cid, "sound", 4*1024*1024, p[3])
	case "files":
		if !hashPattern.MatchString(p[3]) {
			return false
		}
		if len(p) == 6 && p[4] == "Chunks" {
			if len(p[5]) != 3 || p[5][0] != 'c' || p[5] < "c00" || p[5] > "c31" {
				return false
			}
			var b []byte
			return json.Unmarshal(data, &b) == nil && len(b) > 0 && len(b) <= 512*1024
		}
		if len(p) == 5 && p[4] == "Info" {
			return s.verifyFile(ctx, uid, cid, p[3], data)
		}
	}
	return false
}
func (s *store) channelExists(ctx context.Context, cid, id string) bool {
	raw, _, err := s.getDocument(ctx, "communities/"+cid+"/snapshot")
	if err != nil {
		return false
	}
	var v struct{ Channels []struct{ ID string } }
	if json.Unmarshal(raw, &v) != nil {
		return false
	}
	for _, c := range v.Channels {
		if c.ID == id {
			return true
		}
	}
	return false
}
func validateTransfer(raw json.RawMessage, cid, kind string, max int, id string) bool {
	var a struct {
		ID, Name, Kind string
		Data           []byte
	}
	if json.Unmarshal(raw, &a) != nil || a.Kind != kind || len(a.Name) == 0 || len(a.Name) > 200 || filepath.Base(a.Name) != a.Name || strings.ContainsAny(a.Name, `\/`) || len(a.Data) == 0 || len(a.Data) > max || !hashPattern.MatchString(a.ID) {
		return false
	}
	if id != "" && a.ID != id {
		return false
	}
	if kind == "avatar" || kind == "banner" || kind == "emoji" || kind == "icon" {
		config, _, err := image.DecodeConfig(bytes.NewReader(a.Data))
		if err != nil || config.Width <= 0 || config.Height <= 0 || config.Width > 2048 || config.Height > 2048 {
			return false
		}
		if _, _, err = image.Decode(bytes.NewReader(a.Data)); err != nil {
			return false
		}
	}
	if kind == "sound" && !(len(a.Data) >= 12 && string(a.Data[:4]) == "RIFF" && string(a.Data[8:12]) == "WAVE" || len(a.Data) >= 3 && string(a.Data[:3]) == "ID3" || len(a.Data) >= 2 && a.Data[0] == 255 && a.Data[1]&224 == 224) {
		return false
	}

	if cid != "" {
		hash := sha256.New()
		hash.Write([]byte(cid + "\x00" + kind + "\x00"))
		hash.Write(a.Data)
		return a.ID == hex.EncodeToString(hash.Sum(nil))
	}
	sum := sha256.Sum256(a.Data)
	return a.ID == hex.EncodeToString(sum[:])
}
func (s *store) verifyFile(ctx context.Context, uid, cid, id string, raw json.RawMessage) bool {
	var meta struct {
		Name, Kind string
		Size       int64
		Chunks     int
	}
	if json.Unmarshal(raw, &meta) != nil || meta.Size < 0 || meta.Size > 16*1024*1024 || meta.Chunks != int((meta.Size+512*1024-1)/(512*1024)) || meta.Chunks > 32 || meta.Kind != "attachment" && meta.Kind != "voice" || meta.Kind == "voice" && meta.Size > 5*1024*1024 || len(meta.Name) == 0 || len(meta.Name) > 200 || strings.ContainsAny(meta.Name, `\/`) {
		return false
	}
	h := sha256.New()
	h.Write([]byte(cid + "\x00" + meta.Kind + "\x00"))
	var size int64
	for i := 0; i < meta.Chunks; i++ {
		var data json.RawMessage
		var author string
		err := s.db.QueryRow(ctx, `SELECT data,author_id FROM wisp_documents WHERE path=$1`, fmt.Sprintf("communities/%s/files/%s/Chunks/c%02d", cid, id, i)).Scan(&data, &author)
		if err != nil || author != uid {
			return false
		}
		var chunk []byte
		if json.Unmarshal(data, &chunk) != nil || len(chunk) != int(min(int64(512*1024), meta.Size-size)) {
			return false
		}
		h.Write(chunk)
		size += int64(len(chunk))
	}
	return size == meta.Size && id == hex.EncodeToString(h.Sum(nil))
}
func (s *store) livePresence(cid string) map[string]json.RawMessage {
	s.presenceMu.Lock()
	defer s.presenceMu.Unlock()
	out := map[string]json.RawMessage{}
	now := time.Now().UnixMilli()
	for uid, raw := range s.presence[cid] {
		var p struct{ SeenAt int64 }
		if json.Unmarshal(raw, &p) != nil || now-p.SeenAt > 60000 {
			delete(s.presence[cid], uid)
			continue
		}
		out[uid] = append(json.RawMessage(nil), raw...)
	}
	return out
}

var emojiTokenPattern = regexp.MustCompile(`^:[\p{L}\p{N}_-]{1,40}:$`)
