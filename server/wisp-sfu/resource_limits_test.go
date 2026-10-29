package main

import (
	"context"
	"encoding/json"
	"fmt"
	"testing"
	"time"
)

func TestAuthRateCacheIsBoundedWithoutBlockingKnownClients(t *testing.T) {
	s := &store{attempts: map[string]rateWindow{}}
	for i := 0; i < 4096; i++ {
		if !s.authRate(fmt.Sprintf("client-%d", i)) {
			t.Fatal("client rejected before the cache reached capacity")
		}
	}
	for i := 0; i < 32; i++ {
		if s.authRate(fmt.Sprintf("overflow-%d", i)) {
			t.Fatal("new client admitted while every retained window is live")
		}
	}
	if len(s.attempts) != 4096 {
		t.Fatalf("rejected keys grew the cache to %d entries", len(s.attempts))
	}
	for i := 1; i < 12; i++ {
		if !s.authRate("client-0") {
			t.Fatal("cache capacity consumed an existing client's allowance")
		}
	}
	if s.authRate("client-0") {
		t.Fatal("per-client rate limit was bypassed")
	}
}

func TestAuthRateExpiredWindowsResetAndMakeRoom(t *testing.T) {
	now := time.Now()
	s := &store{attempts: map[string]rateWindow{}}
	for i := 0; i < 4096; i++ {
		s.attempts[fmt.Sprintf("client-%d", i)] = rateWindow{start: now, count: 12}
	}
	s.attempts["client-0"] = rateWindow{start: now.Add(-2 * time.Minute), count: 12}
	if !s.authRate("client-0") || s.attempts["client-0"].count != 1 {
		t.Fatal("known client did not receive a fresh allowance after expiry")
	}
	s.attempts["client-1"] = rateWindow{start: now.Add(-2 * time.Minute), count: 12}
	if !s.authRate("new-client") {
		t.Fatal("expired window did not make room for a new client")
	}
	if _, exists := s.attempts["client-1"]; exists || len(s.attempts) != 4096 {
		t.Fatal("expired entry was not replaced within the cache bound")
	}
}

func TestAdminSnapshotSavesWithOnePoolConnectionAvailable(t *testing.T) {
	s := testStore(t)
	srv := testAPI(t, s)
	owner := registerTest(t, srv, "snapshot_owner")
	admin := registerTest(t, srv, "snapshot_admin")
	cid := "snapshot-pool-pressure"
	status, _ := apiCall(t, srv, "PUT", "/v1/data/communities/"+cid+"/snapshot", owner.AccessToken, snapshot(owner.Profile.ID, cid))
	if status != 200 {
		t.Fatal("owner could not create snapshot", status)
	}
	if _, err := s.db.Exec(context.Background(), `INSERT INTO wisp_members(community_id,user_id,role) VALUES($1,$2,'admin')`, cid, admin.Profile.ID); err != nil {
		t.Fatal(err)
	}
	// Keep exactly one connection free. A snapshot transaction must not borrow
	// a second connection to authorize its administrator while holding a lock.
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	for i := int32(1); i < s.db.Config().MaxConns; i++ {
		conn, err := s.db.Acquire(ctx)
		if err != nil {
			t.Fatal(err)
		}
		defer conn.Release()
	}
	raw, err := json.Marshal(snapshot(owner.Profile.ID, cid))
	if err != nil {
		t.Fatal(err)
	}
	if err = s.saveSnapshot(ctx, admin.Profile.ID, cid, raw); err != nil {
		t.Fatal("administrator snapshot save exhausted the pool", err)
	}
	if _, _, err = s.getDocument(ctx, "communities/"+cid+"/snapshot"); err != nil {
		t.Fatal("snapshot transaction did not release its connection", err)
	}
}
