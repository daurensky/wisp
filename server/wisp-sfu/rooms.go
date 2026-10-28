package main

import (
	"time"
)

type rtcSeat struct {
	UserID    string `json:"UserId"`
	SessionID string `json:"SessionId"`
	SeenAt    int64  `json:"SeenAt"`
	Muted     bool   `json:"Muted"`
	Deafened  bool   `json:"Deafened"`
}

func (h *hub) broadcastRosterLocked(community, channel string) {
	roster := []rtcSeat{}
	now := time.Now().UnixMilli()
	for s := range h.sessions {
		if s.ident.Community == community && s.ident.Channel == channel {
			roster = append(roster, rtcSeat{s.ident.UID, s.ident.Session, now, s.muted, s.deafened})
		}
	}
	for s := range h.sessions {
		if s.ident.Community == community && s.ident.Channel == channel {
			s.send(message{Type: "roster", Participants: roster})
		}
	}
}
func (h *hub) rooms(community string) map[string]map[string]rtcSeat {
	h.mu.Lock()
	defer h.mu.Unlock()
	rooms := map[string]map[string]rtcSeat{}
	now := time.Now().UnixMilli()
	for s := range h.sessions {
		if s.ident.Community != community {
			continue
		}
		if rooms[s.ident.Channel] == nil {
			rooms[s.ident.Channel] = map[string]rtcSeat{}
		}
		rooms[s.ident.Channel][s.ident.UID] = rtcSeat{s.ident.UID, s.ident.Session, now, s.muted, s.deafened}
	}
	return rooms
}

func (h *hub) removeCommunityMember(community, uid string) {
	h.mu.Lock()
	var removed []*session
	for s := range h.sessions {
		if s.ident.Community == community && s.ident.UID == uid {
			removed = append(removed, s)
		}
	}
	h.mu.Unlock()
	for _, s := range removed {
		s.stop("community-membership-removed")
	}
}
