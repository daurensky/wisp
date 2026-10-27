package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"github.com/coder/websocket"
	"net/http"
	"net/http/httptest"
	"os"
	"os/exec"
	"strings"
	"testing"
	"time"
)

func testHub(t *testing.T, max int) (*hub, *httptest.Server) {
	t.Helper()
	api, closeFn, err := mediaAPI("", 0)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(closeFn)
	h := newHub(api, func(_ context.Context, m message) (identity, error) {
		if m.Token != "fixture" {
			return identity{}, denied
		}
		return identity{m.Peer, m.Community, m.Channel, m.Session}, nil
	}, max)
	srv := httptest.NewTLSServer(httpHandler(h))
	t.Cleanup(srv.Close)
	return h, srv
}

// Avoid production auth shortcuts: only this test binary has an injected verifier.
func httpHandler(h *hub) *testHandler { return &testHandler{h} }

type testHandler struct{ h *hub }

func (h *testHandler) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	if r.URL.Path == "/healthz" {
		fmt.Fprintln(w, "wisp-sfu protocol=2")
		return
	}
	h.h.serve(w, r)
}
func joinTest(t *testing.T, srv *httptest.Server, uid, token string) (*websocket.Conn, message) {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 4*time.Second)
	defer cancel()
	ws, _, err := websocket.Dial(ctx, "wss"+strings.TrimPrefix(srv.URL, "https")+"/rtc", &websocket.DialOptions{HTTPClient: srv.Client()})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { ws.CloseNow() })
	b, _ := json.Marshal(message{Type: "join", Version: 2, Peer: uid, Token: token, Community: "community", Channel: "voice", Session: strings.Repeat("a", 32)})
	if err = ws.Write(ctx, websocket.MessageText, b); err != nil {
		t.Fatal(err)
	}
	for {
		_, data, err := ws.Read(ctx)
		if err != nil {
			return ws, message{Type: "rejected"}
		}
		var m message
		_ = json.Unmarshal(data, &m)
		if m.Type == "ready" {
			return ws, m
		}
	}
}
func TestAdmission(t *testing.T) {
	h, srv := testHub(t, 0)
	if _, m := joinTest(t, srv, "denied", "bad"); m.Type != "rejected" {
		t.Fatal("invalid auth accepted")
	}
	for _, uid := range []string{"a", "b", "c", "d", "e", "f", "g"} {
		if _, m := joinTest(t, srv, uid, "fixture"); m.Type != "ready" {
			t.Fatal("unlimited room rejected participant", uid)
		}
	}
	if _, m := joinTest(t, srv, "a", "fixture"); m.Type != "ready" {
		t.Fatal("authenticated reconnect rejected by stale login")
	}
	h.mu.Lock()
	defer h.mu.Unlock()
	if len(h.sessions) != 7 {
		t.Fatal("takeover leaked duplicate sessions", len(h.sessions))
	}
}
func TestTakeoverPreservesCapacityAndRequiresAuthentication(t *testing.T) {
	h, srv := testHub(t, 1)
	if _, m := joinTest(t, srv, "a", "fixture"); m.Type != "ready" {
		t.Fatal("first login rejected")
	}
	h.mu.Lock()
	var original *session
	for s := range h.sessions {
		original = s
	}
	h.mu.Unlock()
	if _, m := joinTest(t, srv, "a", "bad"); m.Type != "rejected" {
		t.Fatal("invalid takeover admitted")
	}
	select {
	case <-original.ctx.Done():
		t.Fatal("invalid token kicked legitimate user")
	default:
	}
	if _, m := joinTest(t, srv, "b", "fixture"); m.Type != "rejected" {
		t.Fatal("capacity ignored")
	}
	if _, m := joinTest(t, srv, "a", "fixture"); m.Type != "ready" {
		t.Fatal("capacity blocked own reconnect")
	}
	select {
	case <-original.ctx.Done():
	default:
		t.Fatal("takeover left old transport alive")
	}
	h.mu.Lock()
	defer h.mu.Unlock()
	if len(h.sessions) != 1 {
		t.Fatal("takeover leaked session")
	}
}
func TestNativeInterop(t *testing.T) {
	probe := os.Getenv("WISP_SFU_PROBE")
	if probe == "" {
		t.Skip("build tools/Wisp.Sfu.Probe, set WISP_SFU_PROBE to its DLL and DOTNET to dotnet")
	}
	db := testStore(t)
	api, closeFn, err := mediaAPI("127.0.0.1", 0)
	if err != nil {
		t.Fatal(err)
	}
	defer closeFn()
	h := newHub(api, db.authenticate, 0)
	db.hub = h
	srv := testAPI(t, db)
	pin := sha256.Sum256(srv.Certificate().Raw)
	dotnet := os.Getenv("DOTNET")
	if dotnet == "" {
		dotnet = "dotnet"
	}
	ctx, cancel := context.WithTimeout(context.Background(), 120*time.Second)
	defer cancel()
	cmd := exec.CommandContext(ctx, dotnet, probe, "wss"+strings.TrimPrefix(srv.URL, "https")+"/rtc", hex.EncodeToString(pin[:]))
	out, err := cmd.CombinedOutput()
	t.Log(string(out))
	if err != nil {
		t.Fatal(err)
	}
}
