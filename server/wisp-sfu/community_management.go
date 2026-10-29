package main

import (
	"context"
	"encoding/json"
	"errors"
	"math"
	"net/http"
	"path/filepath"
	"strings"
	"unicode/utf8"
)

// A community row lock makes authority checks and management changes atomic.
func (s *store) manageCommunity(w http.ResponseWriter, r *http.Request, ctx context.Context, uid, cid string) {
	var command struct {
		Action, Name, UserID, Role, Kind, AssetID, ChannelID string
		Volume                                               *float64
		CoverFrame                                           *int
		IsVoice                                              *bool
		Asset                                                json.RawMessage
	}
	if !readJSON(w, r, &command, 6*1024*1024) {
		return
	}
	tx, err := s.db.Begin(ctx)
	if err != nil {
		apiError(w, 503, "Database unavailable")
		return
	}
	defer tx.Rollback(ctx)
	var owner, role string
	err = tx.QueryRow(ctx, `SELECT c.owner_id,m.role FROM wisp_communities c JOIN wisp_members m ON m.community_id=c.id AND m.user_id=$2 WHERE c.id=$1 FOR UPDATE OF c`, cid, uid).Scan(&owner, &role)
	if err != nil || owner != uid && role != "admin" {
		apiError(w, 403, "Community management permission required")
		return
	}
	var kick, removeChannel string
	switch command.Action {
	case "rename", "icon":
		var raw json.RawMessage
		if tx.QueryRow(ctx, `SELECT data FROM wisp_documents WHERE path=$1 FOR UPDATE`, "communities/"+cid+"/snapshot").Scan(&raw) != nil {
			apiError(w, 404, "Community unavailable")
			return
		}
		var snapshot map[string]json.RawMessage
		var community map[string]any
		if json.Unmarshal(raw, &snapshot) != nil || json.Unmarshal(snapshot["Community"], &community) != nil {
			apiError(w, 400, "Invalid community")
			return
		}
		if command.Action == "rename" {
			name := strings.TrimSpace(command.Name)
			if len(name) == 0 || utf8.RuneCountInString(name) > 80 || strings.ContainsAny(name, "\x00\r\n") {
				apiError(w, 400, "Name must contain 1-80 characters")
				return
			}
			community["Name"] = name
		} else {
			if !validateTransfer(command.Asset, cid, "icon", 4*1024*1024, "") {
				apiError(w, 400, "Icon must be PNG, JPEG or GIF up to 4 MiB and 2048 pixels")
				return
			}
			var a struct{ ID string }
			_ = json.Unmarshal(command.Asset, &a)
			community["IconAssetId"] = a.ID
			frame := 0
			if command.CoverFrame != nil {
				frame = *command.CoverFrame
			}
			if frame < 0 || frame > 499 {
				apiError(w, 400, "Invalid GIF cover frame")
				return
			}
			community["IconCoverFrame"] = frame
			_, err = tx.Exec(ctx, `INSERT INTO wisp_documents(path,author_id,data) VALUES($1,$2,$3) ON CONFLICT(path) DO UPDATE SET author_id=excluded.author_id,data=excluded.data,version=nextval('wisp_doc_version')`, "communities/"+cid+"/icon", uid, command.Asset)
		}
		snapshot["Community"], _ = json.Marshal(community)
		raw, _ = json.Marshal(snapshot)
		if err == nil {
			_, err = tx.Exec(ctx, `UPDATE wisp_documents SET data=$2,version=nextval('wisp_doc_version') WHERE path=$1`, "communities/"+cid+"/snapshot", raw)
		}
	case "createChannel", "renameChannel", "deleteChannel":
		if !keyPattern.MatchString(command.ChannelID) {
			apiError(w, 400, "Invalid channel")
			return
		}
		var raw json.RawMessage
		if tx.QueryRow(ctx, `SELECT data FROM wisp_documents WHERE path=$1 FOR UPDATE`, "communities/"+cid+"/snapshot").Scan(&raw) != nil {
			apiError(w, 404, "Community unavailable")
			return
		}
		var snapshot map[string]json.RawMessage
		var channels []map[string]json.RawMessage
		if json.Unmarshal(raw, &snapshot) != nil || json.Unmarshal(snapshot["Channels"], &channels) != nil {
			apiError(w, 400, "Invalid community")
			return
		}
		found := -1
		for i, channel := range channels {
			var id string
			_ = json.Unmarshal(channel["Id"], &id)
			if id == command.ChannelID {
				found = i
				break
			}
		}
		if command.Action == "createChannel" {
			if found >= 0 {
				apiError(w, 409, "Channel already exists")
				return
			}
			if len(channels) >= 100 {
				apiError(w, 400, "At most 100 channels")
				return
			}
			name := strings.TrimSpace(command.Name)
			if name == "" || utf8.RuneCountInString(name) > 64 || strings.ContainsAny(name, "\x00\r\n") {
				apiError(w, 400, "Channel name must contain 1-64 characters")
				return
			}
			voice := command.IsVoice != nil && *command.IsVoice
			channel := map[string]json.RawMessage{}
			channel["Id"], _ = json.Marshal(command.ChannelID)
			channel["CommunityId"], _ = json.Marshal(cid)
			channel["Name"], _ = json.Marshal(name)
			channel["IsVoice"], _ = json.Marshal(voice)
			channels = append(channels, channel)
		} else if found < 0 {
			apiError(w, 404, "Channel unavailable")
			return
		} else if command.Action == "renameChannel" {
			name := strings.TrimSpace(command.Name)
			if name == "" || utf8.RuneCountInString(name) > 64 || strings.ContainsAny(name, "\x00\r\n") {
				apiError(w, 400, "Channel name must contain 1-64 characters")
				return
			}
			channels[found]["Name"], _ = json.Marshal(name)
		} else {
			channels = append(channels[:found], channels[found+1:]...)
			removeChannel = command.ChannelID
		}
		snapshot["Channels"], _ = json.Marshal(channels)
		raw, _ = json.Marshal(snapshot)
		_, err = tx.Exec(ctx, `UPDATE wisp_documents SET data=$2,version=nextval('wisp_doc_version') WHERE path=$1`, "communities/"+cid+"/snapshot", raw)
	case "role":
		if owner != uid || command.UserID == owner || command.Role != "admin" && command.Role != "member" {
			apiError(w, 403, "Only the owner can change member roles")
			return
		}
		result, e := tx.Exec(ctx, `UPDATE wisp_members SET role=$3 WHERE community_id=$1 AND user_id=$2`, cid, command.UserID, command.Role)
		err = e
		if e == nil && result.RowsAffected() == 0 {
			apiError(w, 404, "Member unavailable")
			return
		}
	case "kick":
		if command.UserID == owner || command.UserID == uid {
			apiError(w, 403, "The owner cannot be removed")
			return
		}
		var targetRole string
		if tx.QueryRow(ctx, `SELECT role FROM wisp_members WHERE community_id=$1 AND user_id=$2`, cid, command.UserID).Scan(&targetRole) != nil {
			apiError(w, 404, "Member unavailable")
			return
		}
		if owner != uid && targetRole == "admin" {
			apiError(w, 403, "Only the owner can remove an administrator")
			return
		}
		_, err = tx.Exec(ctx, `DELETE FROM wisp_members WHERE community_id=$1 AND user_id=$2`, cid, command.UserID)
		kick = command.UserID
	case "renameAsset", "deleteAsset":
		if command.Kind != "emoji" && command.Kind != "sound" || !hashPattern.MatchString(command.AssetID) {
			apiError(w, 400, "Invalid asset")
			return
		}
		collection := "emojis"
		if command.Kind == "sound" {
			collection = "sounds"
		}
		path := "communities/" + cid + "/" + collection + "/" + command.AssetID
		if command.Action == "deleteAsset" {
			_, err = tx.Exec(ctx, `DELETE FROM wisp_documents WHERE path=$1`, path)
		} else {
			name := strings.TrimSpace(command.Name)
			if (name != "" && (len(name) > 200 || filepath.Base(name) != name || strings.ContainsAny(name, `\/`))) || (name == "" && command.Volume == nil) {
				apiError(w, 400, "Invalid asset name")
				return
			}
			var raw json.RawMessage
			if tx.QueryRow(ctx, `SELECT data FROM wisp_documents WHERE path=$1 FOR UPDATE`, path).Scan(&raw) != nil {
				apiError(w, 404, "Asset unavailable")
				return
			}
			var asset map[string]json.RawMessage
			if json.Unmarshal(raw, &asset) != nil {
				apiError(w, 400, "Invalid asset")
				return
			}
			if name != "" {
				asset["Name"], _ = json.Marshal(name)
			}
			if command.Volume != nil {
				volume := *command.Volume
				if command.Kind != "sound" || math.IsNaN(volume) || math.IsInf(volume, 0) || volume < 0 || volume > 1 {
					apiError(w, 400, "Sound volume must be between 0 and 1")
					return
				}
				asset["Volume"], _ = json.Marshal(volume)
			}
			raw, _ = json.Marshal(asset)
			_, err = tx.Exec(ctx, `UPDATE wisp_documents SET data=$2,version=nextval('wisp_doc_version') WHERE path=$1`, path, raw)
		}
	default:
		apiError(w, 400, "Unknown management action")
		return
	}
	if err != nil || tx.Commit(ctx) != nil {
		apiError(w, 503, "Database unavailable")
		return
	}
	if removeChannel != "" && s.hub != nil {
		s.hub.removeCommunityChannel(cid, removeChannel)
	}
	if kick != "" {
		s.presenceMu.Lock()
		delete(s.presence[cid], kick)
		s.presenceMu.Unlock()
		if s.hub != nil {
			s.hub.removeCommunityMember(cid, kick)
		}
	}
	writeJSON(w, 200, true)
}

// Both public snapshots and manager settings use one authoritative membership view.
func (s *store) communitySnapshot(ctx context.Context, cid string) (map[string]json.RawMessage, error) {
	raw, _, err := s.getDocument(ctx, "communities/"+cid+"/snapshot")
	if err != nil {
		return nil, err
	}
	var doc map[string]json.RawMessage
	if json.Unmarshal(raw, &doc) != nil || doc == nil {
		return nil, errors.New("invalid community snapshot")
	}
	rows, err := s.db.Query(ctx, `SELECT u.id,u.profile,CASE WHEN c.owner_id=m.user_id THEN 'owner' ELSE m.role END FROM wisp_users u JOIN wisp_members m ON m.user_id=u.id JOIN wisp_communities c ON c.id=m.community_id WHERE m.community_id=$1 ORDER BY u.id`, cid)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	members := []json.RawMessage{}
	roles := map[string]string{}
	for rows.Next() {
		var id, role string
		var profile json.RawMessage
		if err = rows.Scan(&id, &profile, &role); err != nil {
			return nil, err
		}
		members = append(members, profile)
		roles[id] = role
	}
	if err = rows.Err(); err != nil {
		return nil, err
	}
	doc["Members"], _ = json.Marshal(members)
	doc["Roles"], _ = json.Marshal(roles)
	return doc, nil
}
