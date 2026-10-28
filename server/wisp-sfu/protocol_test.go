package main

import (
	"context"
	"encoding/json"
	"github.com/coder/websocket"
	"strings"
	"testing"
	"time"
)

func TestProtocolVersionRejectionHasCloseReason(t *testing.T) {
	_, srv := testHub(t, 0)
	ctx, cancel := context.WithTimeout(context.Background(), 4*time.Second)
	defer cancel()
	ws, _, err := websocket.Dial(ctx, "wss"+strings.TrimPrefix(srv.URL, "https")+"/rtc", &websocket.DialOptions{HTTPClient: srv.Client()})
	if err != nil {
		t.Fatal(err)
	}
	defer ws.CloseNow()
	if err = ws.Write(ctx, websocket.MessageText, []byte(`{"type":"join","version":1}`)); err != nil {
		t.Fatal(err)
	}
	_, _, err = ws.Read(ctx)
	if websocket.CloseStatus(err) != websocket.StatusProtocolError || !strings.Contains(err.Error(), "protocol version 2 required") {
		t.Fatal("missing protocol rejection diagnostic", err)
	}
}
func TestUnknownProtocolMessageHasCloseReason(t *testing.T) {
	_, srv := testHub(t, 0)
	ws, ready := joinTest(t, srv, "user", "fixture")
	if ready.Type != "ready" {
		t.Fatal("join rejected")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 4*time.Second)
	defer cancel()
	data, _ := json.Marshal(message{Type: "unsupported-message"})
	if err := ws.Write(ctx, websocket.MessageText, data); err != nil {
		t.Fatal(err)
	}
	for {
		_, _, err := ws.Read(ctx)
		if err == nil {
			continue
		}
		if websocket.CloseStatus(err) != websocket.StatusProtocolError || !strings.Contains(err.Error(), "unsupported protocol message") {
			t.Fatal("missing unsupported message diagnostic", err)
		}
		break
	}
}
