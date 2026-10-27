package main

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"strings"
	"testing"
	"time"
)

type fixtureTransport func(*http.Request) (*http.Response, error)

func (f fixtureTransport) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }
func TestFirebaseAuthorization(t *testing.T) {
	for _, tc := range []struct {
		name                      string
		member, voice, validToken bool
		age                       int64
		session                   string
		accept                    bool
	}{
		{"valid", true, true, true, 0, strings.Repeat("a", 32), true},
		{"non member", false, true, true, 0, strings.Repeat("a", 32), false},
		{"text channel", true, false, true, 0, strings.Repeat("a", 32), false},
		{"invalid token", true, true, false, 0, strings.Repeat("a", 32), false},
		{"expired seat", true, true, true, 46000, strings.Repeat("a", 32), false},
		{"future seat", true, true, true, -15000, strings.Repeat("a", 32), false},
		{"other session", true, true, true, 0, strings.Repeat("b", 32), false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			client := &http.Client{Transport: fixtureTransport(func(r *http.Request) (*http.Response, error) {
				var v any
				switch {
				case strings.Contains(r.URL.Path, "accounts:lookup"):
					if !tc.validToken {
						return &http.Response{StatusCode: 400, Body: io.NopCloser(strings.NewReader(`{}`))}, nil
					}
					v = map[string]any{"users": []any{map[string]any{"localId": "alice"}}}
				case strings.Contains(r.URL.Path, "/members/"):
					v = tc.member
				case strings.Contains(r.URL.Path, "snapshot/Channels"):
					v = []any{map[string]any{"Id": "voice", "IsVoice": tc.voice}}
				case strings.Contains(r.URL.Path, "rtcRooms/"):
					v = map[string]any{"s0": map[string]any{"UserId": "alice", "SessionId": tc.session, "SeenAt": time.Now().UnixMilli() - tc.age}}
				default:
					t.Fatalf("unexpected Firebase path: %s", r.URL.Path)
				}
				b, _ := json.Marshal(v)
				return &http.Response{StatusCode: 200, Body: io.NopCloser(strings.NewReader(string(b)))}, nil
			})}
			auth := firebaseAuthWithClient(config{APIKey: "fixture", DatabaseURL: "https://fixture.firebaseio.com"}, client)
			id, err := auth(context.Background(), message{Token: "id-token", Community: "community", Channel: "voice", Session: strings.Repeat("a", 32)})
			if (err == nil) != tc.accept {
				t.Fatalf("accept=%v, err=%v", tc.accept, err)
			}
			if tc.accept && id.UID != "alice" {
				t.Fatal("identity not bound to verified token")
			}
		})
	}
}
