package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"github.com/jackc/pgx/v5"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"strings"
	"testing"
)

func testStore(t *testing.T) *store {
	t.Helper()
	dsn := os.Getenv("WISP_TEST_DATABASE_URL")
	if dsn == "" {
		t.Skip("WISP_TEST_DATABASE_URL is required for PostgreSQL integration")
	}
	root, err := pgx.Connect(context.Background(), dsn)
	if err != nil {
		t.Fatal(err)
	}
	schemaName := "test_" + randomID()
	if _, err = root.Exec(context.Background(), "CREATE SCHEMA "+schemaName); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		_, _ = root.Exec(context.Background(), "DROP SCHEMA "+schemaName+" CASCADE")
		root.Close(context.Background())
	})
	u, err := url.Parse(dsn)
	if err != nil {
		t.Fatal(err)
	}
	q := u.Query()
	q.Set("search_path", schemaName)
	u.RawQuery = q.Encode()
	s, err := openStore(context.Background(), u.String(), true)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(s.db.Close)
	return s
}
func testAPI(t *testing.T, s *store) *httptest.Server {
	t.Helper()
	mux := http.NewServeMux()
	mux.HandleFunc("/v1/auth/", s.auth)
	mux.HandleFunc("/v1/data/", s.data)
	mux.HandleFunc("/v1/invites/", s.invites)
	mux.HandleFunc("/healthz", func(w http.ResponseWriter, r *http.Request) { fmt.Fprintln(w, "wisp-sfu protocol=2") })
	if s.hub != nil {
		mux.HandleFunc("/rtc", s.hub.serve)
	}
	srv := httptest.NewTLSServer(mux)
	t.Cleanup(srv.Close)
	return srv
}
func apiCall(t *testing.T, srv *httptest.Server, method, path, token string, value any) (int, json.RawMessage) {
	t.Helper()
	var r *http.Request
	var err error
	if value == nil {
		r, err = http.NewRequest(method, srv.URL+path, nil)
	} else {
		r, err = http.NewRequest(method, srv.URL+path, jsonReader(value))
	}
	if err != nil {
		t.Fatal(err)
	}
	r.Header.Set("Content-Type", "application/json")
	if token != "" {
		r.Header.Set("Authorization", "Bearer "+token)
	}
	response, err := srv.Client().Do(r)
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	var b json.RawMessage
	if json.NewDecoder(response.Body).Decode(&b) != nil {
		t.Fatal("invalid API JSON response")
	}
	return response.StatusCode, b
}

type account struct {
	AccessToken, RefreshToken string
	Profile                   struct{ ID, Username string }
}

func registerTest(t *testing.T, srv *httptest.Server, name string) account {
	t.Helper()
	status, data := apiCall(t, srv, "POST", "/v1/auth/register", "", map[string]string{"email": name + "@example.com", "password": "fixture-password-123", "username": name})
	if status != 200 {
		t.Fatalf("register %s: %d %s", name, status, data)
	}
	var a account
	if json.Unmarshal(data, &a) != nil || len(a.AccessToken) != 43 || len(a.Profile.ID) != 32 {
		t.Fatal("bad account response")
	}
	return a
}
func snapshot(uid, cid string) any {
	return map[string]any{"Community": map[string]any{"Id": cid, "Name": "Test community", "OwnerId": uid}, "Channels": []any{map[string]any{"Id": "voice", "CommunityId": cid, "Name": "Voice", "IsVoice": true}, map[string]any{"Id": "text", "CommunityId": cid, "Name": "Text", "IsVoice": false}}, "Members": []any{}}
}
func TestPostgresAccountsAndCommunityAccess(t *testing.T) {
	s := testStore(t)
	srv := testAPI(t, s)
	alice := registerTest(t, srv, "alice")
	bob := registerTest(t, srv, "bob")
	eve := registerTest(t, srv, "eve")
	status, _ := apiCall(t, srv, "POST", "/v1/auth/login", "", map[string]string{"email": "alice@example.com", "password": "wrong-password"})
	if status != 401 {
		t.Fatal("wrong password accepted")
	}
	status, data := apiCall(t, srv, "POST", "/v1/auth/login", "", map[string]string{"email": "alice@example.com", "password": "fixture-password-123"})
	if status != 200 {
		t.Fatal("valid login rejected", string(data))
	}
	var salt, hash []byte
	if s.db.QueryRow(context.Background(), `SELECT salt,password_hash FROM wisp_users WHERE id=$1`, alice.Profile.ID).Scan(&salt, &hash) != nil || len(salt) != 16 || len(hash) != 32 || strings.Contains(string(hash), "fixture") {
		t.Fatal("password not stored as salted Argon2id")
	}
	status, _ = apiCall(t, srv, "PUT", "/v1/data/communities/community/snapshot", alice.AccessToken, snapshot(alice.Profile.ID, "community"))
	if status != 200 {
		t.Fatal("owner cannot create community", status)
	}
	status, _ = apiCall(t, srv, "GET", "/v1/data/communities/community/snapshot", bob.AccessToken, nil)
	if status != 403 {
		t.Fatal("non member read community")
	}
	status, data = apiCall(t, srv, "POST", "/v1/invites/create", alice.AccessToken, map[string]string{"community": "community"})
	if status != 200 {
		t.Fatal("invite creation", status)
	}
	var invite struct{ Token string }
	_ = json.Unmarshal(data, &invite)
	status, _ = apiCall(t, srv, "POST", "/v1/invites/accept", bob.AccessToken, map[string]string{"token": invite.Token})
	if status != 200 {
		t.Fatal("valid invitation rejected")
	}
	status, data = apiCall(t, srv, "GET", "/v1/data/communities/community/snapshot", bob.AccessToken, nil)
	if status != 200 || !strings.Contains(string(data), bob.Profile.ID) {
		t.Fatal("canonical community members missing")
	}
	status, _ = apiCall(t, srv, "PUT", "/v1/data/communities/community/snapshot", bob.AccessToken, snapshot(bob.Profile.ID, "community"))
	if status != 403 {
		t.Fatal("member stole community ownership")
	}
	status, _ = apiCall(t, srv, "PUT", "/v1/data/profiles/"+alice.Profile.ID+"/Info", bob.AccessToken, map[string]string{"Id": alice.Profile.ID, "Username": "owned"})
	if status != 403 {
		t.Fatal("foreign profile write")
	}
	message := map[string]any{"Id": "message", "ChannelId": "text", "AuthorId": alice.Profile.ID, "AuthorName": "alice", "Text": "hello :wave:", "SentAt": "2026-10-27T12:00:00Z"}
	status, _ = apiCall(t, srv, "PUT", "/v1/data/communities/community/messages/text/message", alice.AccessToken, message)
	if status != 200 {
		t.Fatal("message upload", status)
	}
	status, data = apiCall(t, srv, "GET", "/v1/data/communities/community/messages/text", bob.AccessToken, nil)
	if status != 200 || !strings.Contains(string(data), "hello") {
		t.Fatal("member cannot read chat")
	}
	status, _ = apiCall(t, srv, "PUT", "/v1/data/communities/community/messages/text/forged", bob.AccessToken, message)
	if status != 403 {
		t.Fatal("forged author accepted")
	}
	status, _ = apiCall(t, srv, "GET", "/v1/data/communities/community/messages/text", eve.AccessToken, nil)
	if status != 403 {
		t.Fatal("chat leaked to non member")
	}
	status, data = apiCall(t, srv, "POST", "/v1/auth/refresh", "", map[string]string{"refreshToken": alice.RefreshToken})
	if status != 200 {
		t.Fatal("refresh failed")
	}
	var refreshed account
	_ = json.Unmarshal(data, &refreshed)
	status, _ = apiCall(t, srv, "POST", "/v1/auth/refresh", "", map[string]string{"refreshToken": alice.RefreshToken})
	if status != 401 {
		t.Fatal("used refresh token accepted twice")
	}
	status, _ = apiCall(t, srv, "GET", "/v1/data/communities/community/snapshot", alice.AccessToken, nil)
	if status != 401 {
		t.Fatal("rotated access token remained valid")
	}
	status, _ = apiCall(t, srv, "GET", "/v1/data/communities/community/snapshot", refreshed.AccessToken, nil)
	if status != 200 {
		t.Fatal("new access token unusable")
	}
}

func TestPostgresFileIntegrityAndCrossCommunityIsolation(t *testing.T) {
	s := testStore(t)
	srv := testAPI(t, s)
	a := registerTest(t, srv, "uploader")
	b := registerTest(t, srv, "outsider")
	status, _ := apiCall(t, srv, "PUT", "/v1/data/communities/filespace/snapshot", a.AccessToken, snapshot(a.Profile.ID, "filespace"))
	if status != 200 {
		t.Fatal("create community")
	}
	bytes := []byte("A small content-addressed attachment")
	hash := sha256.Sum256(append([]byte("filespace\x00attachment\x00"), bytes...))
	id := hex.EncodeToString(hash[:])
	prefix := "/v1/data/communities/filespace/files/" + id
	status, _ = apiCall(t, srv, "PUT", prefix+"/Chunks/c00", a.AccessToken, bytes)
	if status != 200 {
		t.Fatal("chunk upload", status)
	}
	status, _ = apiCall(t, srv, "PUT", prefix+"/Info", a.AccessToken, map[string]any{"Name": "fixture.txt", "Kind": "attachment", "Size": len(bytes), "Chunks": 1})
	if status != 200 {
		t.Fatal("manifest/hash verification", status)
	}
	status, data := apiCall(t, srv, "GET", prefix+"/Chunks/c00", a.AccessToken, nil)
	var decoded []byte
	_ = json.Unmarshal(data, &decoded)
	if status != 200 || string(decoded) != string(bytes) {
		t.Fatal("attachment did not round trip")
	}
	status, _ = apiCall(t, srv, "PUT", prefix+"/Chunks/c00", a.AccessToken, []byte("replacement"))
	if status != 409 {
		t.Fatal("immutable chunk overwritten", status)
	}
	status, _ = apiCall(t, srv, "GET", prefix+"/Chunks/c00", b.AccessToken, nil)
	if status != 403 {
		t.Fatal("file leaked outside its community")
	}
	badID := strings.Repeat("a", 64)
	bad := "/v1/data/communities/filespace/files/" + badID
	status, _ = apiCall(t, srv, "PUT", bad+"/Chunks/c00", a.AccessToken, bytes)
	if status != 200 {
		t.Fatal("staging chunk", status)
	}
	status, _ = apiCall(t, srv, "PUT", bad+"/Info", a.AccessToken, map[string]any{"Name": "bad.txt", "Kind": "attachment", "Size": len(bytes), "Chunks": 1})
	if status != 403 {
		t.Fatal("bad content hash published", status)
	}
	status, _ = apiCall(t, srv, "PUT", "/v1/data/profiles/"+a.Profile.ID+"/Images/avatar", a.AccessToken, map[string]any{"Id": badID, "Name": "bad.png", "Kind": "avatar", "Data": []byte("not an image")})
	if status != 403 {
		t.Fatal("invalid image accepted")
	}
}
