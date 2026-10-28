package main

import (
	"context"
	"encoding/json"
	"net/http"
	"path/filepath"
	"strings"
)

// A community row lock makes authority checks and management changes atomic.
func (s *store) manageCommunity(w http.ResponseWriter, r *http.Request, ctx context.Context, uid, cid string) {
	var command struct {
		Action, Name, UserID, Role, Kind, AssetID string
		Asset                                     json.RawMessage
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
	var kick string
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
			if len(name) == 0 || len(name) > 100 {
				apiError(w, 400, "Name must contain 1-100 bytes")
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
			_, err = tx.Exec(ctx, `INSERT INTO wisp_documents(path,author_id,data) VALUES($1,$2,$3) ON CONFLICT(path) DO UPDATE SET author_id=excluded.author_id,data=excluded.data,version=nextval('wisp_doc_version')`, "communities/"+cid+"/icon", uid, command.Asset)
		}
		snapshot["Community"], _ = json.Marshal(community)
		raw, _ = json.Marshal(snapshot)
		if err == nil {
			_, err = tx.Exec(ctx, `UPDATE wisp_documents SET data=$2,version=nextval('wisp_doc_version') WHERE path=$1`, "communities/"+cid+"/snapshot", raw)
		}
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
			if len(name) == 0 || len(name) > 200 || filepath.Base(name) != name || strings.ContainsAny(name, `\/`) {
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
			asset["Name"], _ = json.Marshal(name)
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
