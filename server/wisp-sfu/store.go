package main

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"golang.org/x/crypto/argon2"
	"net/http"
	"net/mail"
	"regexp"
	"strings"
	"sync"
	"time"
)

const schema = `
CREATE TABLE IF NOT EXISTS wisp_users (
 id text PRIMARY KEY, email text UNIQUE NOT NULL, salt bytea NOT NULL, password_hash bytea NOT NULL,
 profile jsonb NOT NULL, created_at timestamptz NOT NULL DEFAULT now());
CREATE TABLE IF NOT EXISTS wisp_sessions (
 access_hash bytea PRIMARY KEY, refresh_hash bytea UNIQUE NOT NULL, user_id text NOT NULL REFERENCES wisp_users(id) ON DELETE CASCADE,
 access_until timestamptz NOT NULL, refresh_until timestamptz NOT NULL);
CREATE INDEX IF NOT EXISTS wisp_sessions_expiry ON wisp_sessions(refresh_until);
ALTER TABLE wisp_sessions ADD COLUMN IF NOT EXISTS previous_access_hash bytea;
ALTER TABLE wisp_sessions ADD COLUMN IF NOT EXISTS previous_access_until timestamptz;
CREATE TABLE IF NOT EXISTS wisp_communities (id text PRIMARY KEY, owner_id text NOT NULL REFERENCES wisp_users(id));
CREATE TABLE IF NOT EXISTS wisp_members (community_id text NOT NULL REFERENCES wisp_communities(id) ON DELETE CASCADE,
 user_id text NOT NULL REFERENCES wisp_users(id), PRIMARY KEY(community_id,user_id));
ALTER TABLE wisp_members ADD COLUMN IF NOT EXISTS role text NOT NULL DEFAULT 'member';
CREATE SEQUENCE IF NOT EXISTS wisp_doc_version;
CREATE TABLE IF NOT EXISTS wisp_documents (path text PRIMARY KEY, author_id text NOT NULL REFERENCES wisp_users(id),
 data jsonb NOT NULL, version bigint NOT NULL DEFAULT nextval('wisp_doc_version'), created_at timestamptz NOT NULL DEFAULT now());
CREATE INDEX IF NOT EXISTS wisp_documents_prefix ON wisp_documents(path text_pattern_ops);
CREATE TABLE IF NOT EXISTS wisp_invites (token_hash bytea PRIMARY KEY, community_id text NOT NULL REFERENCES wisp_communities(id) ON DELETE CASCADE,
 expires_at timestamptz NOT NULL);
`

type store struct {
	requests         chan struct{}
	db               *pgxpool.Pool
	hub              *hub
	registrationOpen bool
	passwords        chan struct{}
	rateMu           sync.Mutex
	attempts         map[string]rateWindow
	presenceMu       sync.Mutex
	presence         map[string]map[string]json.RawMessage
}
type rateWindow struct {
	start time.Time
	count int
}

func openStore(ctx context.Context, dsn string, registrationOpen bool) (*store, error) {
	cfg, err := pgxpool.ParseConfig(dsn)
	if err != nil {
		return nil, err
	}
	cfg.MaxConns = 4
	cfg.MinConns = 0
	cfg.MaxConnIdleTime = 2 * time.Minute
	db, err := pgxpool.NewWithConfig(ctx, cfg)
	if err != nil {
		return nil, err
	}
	if err = db.Ping(ctx); err != nil {
		db.Close()
		return nil, err
	}
	if _, err = db.Exec(ctx, schema); err != nil {
		db.Close()
		return nil, err
	}
	return &store{db: db, registrationOpen: registrationOpen, passwords: make(chan struct{}, 2), requests: make(chan struct{}, 8), attempts: map[string]rateWindow{}, presence: map[string]map[string]json.RawMessage{}}, nil
}
func randomToken() string {
	b := make([]byte, 32)
	if _, err := rand.Read(b); err != nil {
		panic(err)
	}
	return base64.RawURLEncoding.EncodeToString(b)
}
func randomID() string {
	b := make([]byte, 16)
	if _, err := rand.Read(b); err != nil {
		panic(err)
	}
	return hex.EncodeToString(b)
}
func digest(value string) []byte { sum := sha256.Sum256([]byte(value)); return sum[:] }
func writeJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(value)
}
func apiError(w http.ResponseWriter, status int, message string) {
	writeJSON(w, status, map[string]string{"error": message})
}
func readJSON(w http.ResponseWriter, r *http.Request, value any, max int64) bool {
	r.Body = http.MaxBytesReader(w, r.Body, max)
	decoder := json.NewDecoder(r.Body)
	if decoder.Decode(value) != nil {
		apiError(w, 400, "Invalid request")
		return false
	}
	return true
}
func (s *store) access(ctx context.Context, token string) (string, error) {
	if len(token) != 43 {
		return "", denied
	}
	var uid string
	err := s.db.QueryRow(ctx, `SELECT user_id FROM wisp_sessions WHERE refresh_until>now() AND ((access_hash=$1 AND access_until>now()) OR (previous_access_hash=$1 AND previous_access_until>now()))`, digest(token)).Scan(&uid)
	if err != nil {
		return "", denied
	}
	return uid, nil
}
func (s *store) bearer(w http.ResponseWriter, r *http.Request) (string, bool) {
	if !strings.HasPrefix(r.Header.Get("Authorization"), "Bearer ") {
		apiError(w, 401, "Authentication required")
		return "", false
	}
	uid, err := s.access(r.Context(), strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer "))
	if err != nil {
		apiError(w, 401, "Session expired")
		return "", false
	}
	return uid, true
}
func (s *store) member(ctx context.Context, community, uid string) bool {
	var yes bool
	err := s.db.QueryRow(ctx, `SELECT EXISTS(SELECT 1 FROM wisp_members WHERE community_id=$1 AND user_id=$2)`, community, uid).Scan(&yes)
	return err == nil && yes
}
func (s *store) owner(ctx context.Context, community, uid string) bool {
	var yes bool
	err := s.db.QueryRow(ctx, `SELECT EXISTS(SELECT 1 FROM wisp_communities WHERE id=$1 AND owner_id=$2)`, community, uid).Scan(&yes)
	return err == nil && yes
}
func (s *store) manager(ctx context.Context, community, uid string) bool {
	var yes bool
	err := s.db.QueryRow(ctx, `SELECT EXISTS(SELECT 1 FROM wisp_members m JOIN wisp_communities c ON c.id=m.community_id WHERE c.id=$1 AND m.user_id=$2 AND (c.owner_id=$2 OR m.role='admin'))`, community, uid).Scan(&yes)
	return err == nil && yes
}
func (s *store) isVoice(ctx context.Context, cid, channel string) bool {
	var b []byte
	if s.db.QueryRow(ctx, `SELECT data FROM wisp_documents WHERE path=$1`, "communities/"+cid+"/snapshot").Scan(&b) != nil {
		return false
	}
	var snapshot struct {
		Channels []struct {
			ID    string `json:"Id"`
			Voice bool   `json:"IsVoice"`
		}
	}
	if json.Unmarshal(b, &snapshot) != nil {
		return false
	}
	for _, c := range snapshot.Channels {
		if c.ID == channel && c.Voice {
			return true
		}
	}
	return false
}
func (s *store) authenticate(ctx context.Context, m message) (identity, error) {
	uid, err := s.access(ctx, m.Token)
	if err != nil || !sessionPattern.MatchString(m.Session) || !keyPattern.MatchString(m.Community) || !keyPattern.MatchString(m.Channel) || !s.member(ctx, m.Community, uid) || !s.isVoice(ctx, m.Community, m.Channel) {
		return identity{}, denied
	}
	return identity{uid, m.Community, m.Channel, m.Session}, nil
}
func (s *store) authRate(key string) bool {
	s.rateMu.Lock()
	defer s.rateMu.Unlock()
	now := time.Now()
	v := s.attempts[key]
	if now.Sub(v.start) > time.Minute {
		v = rateWindow{start: now}
	}
	v.count++
	s.attempts[key] = v
	if len(s.attempts) > 4096 {
		for k, x := range s.attempts {
			if now.Sub(x.start) > time.Minute {
				delete(s.attempts, k)
			}
		}
		if len(s.attempts) > 4096 {
			return false
		}
	}
	return v.count <= 12
}
func (s *store) profile(ctx context.Context, uid string) (json.RawMessage, error) {
	var p json.RawMessage
	err := s.db.QueryRow(ctx, `SELECT profile FROM wisp_users WHERE id=$1`, uid).Scan(&p)
	return p, err
}
func (s *store) newSession(ctx context.Context, uid string) (map[string]any, error) {
	access, refresh := randomToken(), randomToken()
	_, _ = s.db.Exec(ctx, `DELETE FROM wisp_sessions WHERE refresh_until<now()`)
	_, err := s.db.Exec(ctx, `INSERT INTO wisp_sessions(access_hash,refresh_hash,user_id,access_until,refresh_until) VALUES($1,$2,$3,now()+interval '15 minutes',now()+interval '90 days')`, digest(access), digest(refresh), uid)
	if err != nil {
		return nil, err
	}
	p, err := s.profile(ctx, uid)
	return map[string]any{"accessToken": access, "refreshToken": refresh, "expiresIn": 900, "profile": p}, err
}
func (s *store) auth(w http.ResponseWriter, r *http.Request) {
	controller := http.NewResponseController(w)
	_ = controller.SetReadDeadline(time.Now().Add(10 * time.Second))
	defer controller.SetReadDeadline(time.Time{})
	if r.Method != "POST" {
		apiError(w, 405, "POST required")
		return
	}
	host := r.RemoteAddr
	if i := strings.LastIndex(host, ":"); i >= 0 {
		host = host[:i]
	}
	if !s.authRate(host) {
		apiError(w, 429, "Too many attempts; retry in a minute")
		return
	}
	type credentials struct{ Email, Password, Username, RefreshToken string }
	var in credentials
	if !readJSON(w, r, &in, 8192) {
		return
	}
	action := strings.TrimPrefix(r.URL.Path, "/v1/auth/")
	if action == "logout" {
		if len(in.RefreshToken) != 43 {
			apiError(w, 400, "Invalid session")
			return
		}
		_, err := s.db.Exec(r.Context(), `DELETE FROM wisp_sessions WHERE refresh_hash=$1`, digest(in.RefreshToken))
		if err != nil {
			apiError(w, 503, "Database unavailable")
			return
		}
		writeJSON(w, 200, true)
		return
	}
	if action == "refresh" {
		if len(in.RefreshToken) != 43 {
			apiError(w, 401, "Session expired")
			return
		}
		access, refresh := randomToken(), randomToken()
		var uid string
		// Existing API/voice requests may still carry the old token until the next
		// in-band SFU auth update. Bound overlap to 90s and never extend its expiry.
		err := s.db.QueryRow(r.Context(), `UPDATE wisp_sessions SET previous_access_hash=access_hash,previous_access_until=LEAST(access_until,now()+interval '90 seconds'),access_hash=$1,refresh_hash=$2,access_until=now()+interval '15 minutes',refresh_until=now()+interval '90 days' WHERE refresh_hash=$3 AND refresh_until>now() RETURNING user_id`, digest(access), digest(refresh), digest(in.RefreshToken)).Scan(&uid)
		if err != nil {
			apiError(w, 401, "Session expired")
			return
		}
		p, err := s.profile(r.Context(), uid)
		if err != nil {
			apiError(w, 503, "Database unavailable")
			return
		}
		writeJSON(w, 200, map[string]any{"accessToken": access, "refreshToken": refresh, "expiresIn": 900, "profile": p})
		return
	}
	if action != "register" && action != "login" {
		apiError(w, 404, "Unknown auth action")
		return
	}
	email := strings.ToLower(strings.TrimSpace(in.Email))
	addr, err := mail.ParseAddress(email)
	if err != nil || addr.Address != email || len(email) > 254 || len(in.Password) < 8 || len(in.Password) > 128 {
		apiError(w, 400, "Valid email and password of 8–128 characters required")
		return
	}
	select {
	case s.passwords <- struct{}{}:
		defer func() { <-s.passwords }()
	default:
		apiError(w, 429, "Authentication busy; retry shortly")
		return
	}
	var uid string
	if action == "register" {
		if !s.registrationOpen {
			apiError(w, 403, "Registration closed by server owner")
			return
		}
		if !usernamePattern.MatchString(in.Username) {
			apiError(w, 400, "Username must contain 3–32 letters, digits, underscores, dots or hyphens")
			return
		}
		uid = randomID()
		salt := make([]byte, 16)
		_, _ = rand.Read(salt)
		hash := argon2.IDKey([]byte(in.Password), salt, 2, 32*1024, 1, 32)
		profile := map[string]any{"Id": uid, "Username": in.Username, "About": "", "Status": "online"}
		data, _ := json.Marshal(profile)
		_, err = s.db.Exec(r.Context(), `INSERT INTO wisp_users(id,email,salt,password_hash,profile) VALUES($1,$2,$3,$4,$5) ON CONFLICT(email) DO NOTHING`, uid, email, salt, hash, data)
		if err != nil {
			apiError(w, 503, "Database unavailable")
			return
		}
		var actual string
		if s.db.QueryRow(r.Context(), `SELECT id FROM wisp_users WHERE email=$1`, email).Scan(&actual) != nil || actual != uid {
			apiError(w, 409, "Email already registered")
			return
		}
	} else {
		var salt, expected []byte
		err = s.db.QueryRow(r.Context(), `SELECT id,salt,password_hash FROM wisp_users WHERE email=$1`, email).Scan(&uid, &salt, &expected)
		if err != nil {
			salt = make([]byte, 16)
			expected = make([]byte, 32)
		}
		actual := argon2.IDKey([]byte(in.Password), salt, 2, 32*1024, 1, 32)
		if err != nil || subtle.ConstantTimeCompare(actual, expected) != 1 {
			apiError(w, 401, "Invalid email or password")
			return
		}
	}
	out, err := s.newSession(r.Context(), uid)
	if err != nil {
		apiError(w, 503, "Database unavailable")
		return
	}
	writeJSON(w, 200, out)
}

var usernamePattern = regexp.MustCompile(`^[\p{L}\p{N}_.-]{3,32}$`)
var hashPattern = regexp.MustCompile(`^[a-f0-9]{64}$`)

func (s *store) invites(w http.ResponseWriter, r *http.Request) {
	uid, ok := s.bearer(w, r)
	if !ok {
		return
	}
	if r.Method != "POST" {
		apiError(w, 405, "POST required")
		return
	}
	var input struct{ Community, Token string }
	if !readJSON(w, r, &input, 4096) {
		return
	}
	if r.URL.Path == "/v1/invites/create" {
		if !s.member(r.Context(), input.Community, uid) {
			apiError(w, 403, "Not a community member")
			return
		}
		token := randomToken()
		_, err := s.db.Exec(r.Context(), `INSERT INTO wisp_invites(token_hash,community_id,expires_at) VALUES($1,$2,now()+interval '7 days')`, digest(token), input.Community)
		if err != nil {
			apiError(w, 503, "Database unavailable")
			return
		}
		writeJSON(w, 200, map[string]string{"token": token})
		return
	}
	if r.URL.Path != "/v1/invites/accept" || len(input.Token) != 43 {
		apiError(w, 400, "Invalid invitation")
		return
	}
	var cid string
	err := s.db.QueryRow(r.Context(), `SELECT community_id FROM wisp_invites WHERE token_hash=$1 AND expires_at>now()`, digest(input.Token)).Scan(&cid)
	if err != nil {
		apiError(w, 404, "Invitation expired or not found")
		return
	}
	if _, err = s.db.Exec(r.Context(), `INSERT INTO wisp_members(community_id,user_id) VALUES($1,$2) ON CONFLICT DO NOTHING`, cid, uid); err != nil {
		apiError(w, 503, "Database unavailable")
		return
	}
	writeJSON(w, 200, map[string]string{"community": cid})
}
func (s *store) getDocument(ctx context.Context, path string) (json.RawMessage, int64, error) {
	var data json.RawMessage
	var version int64
	err := s.db.QueryRow(ctx, `SELECT data,version FROM wisp_documents WHERE path=$1`, path).Scan(&data, &version)
	if errors.Is(err, pgx.ErrNoRows) {
		return json.RawMessage("null"), 0, nil
	}
	return data, version, err
}
func documentRoot(parts []string) int {
	if parts[0] == "profiles" {
		if len(parts) >= 4 && parts[2] == "Images" {
			return 4
		}
		return 3
	}
	if len(parts) >= 4 {
		switch parts[2] {
		case "messages":
			return 5
		case "files":
			if len(parts) >= 6 && parts[4] == "Chunks" {
				return 6
			}
			return 5
		case "emojis", "sounds":
			return 4
		case "snapshot":
			return 3
		}
	}
	return len(parts)
}
func jsonChild(data json.RawMessage, parts []string) json.RawMessage {
	for _, part := range parts {
		var obj map[string]json.RawMessage
		if json.Unmarshal(data, &obj) != nil {
			return json.RawMessage("null")
		}
		data = obj[part]
		if data == nil {
			return json.RawMessage("null")
		}
	}
	return data
}
func (s *store) listDocuments(ctx context.Context, prefix string, limit int) (map[string]json.RawMessage, error) {
	rows, err := s.db.Query(ctx, `SELECT path,CASE WHEN $3 THEN data-'Data' ELSE data END FROM wisp_documents WHERE path LIKE $1 ORDER BY created_at DESC,path LIMIT $2`, escapeLike(prefix)+"/%", limit, (strings.HasSuffix(prefix, "/sounds") || strings.HasSuffix(prefix, "/emojis")))
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := map[string]json.RawMessage{}
	for rows.Next() {
		var path string
		var data json.RawMessage
		if err = rows.Scan(&path, &data); err != nil {
			return nil, err
		}
		key := strings.TrimPrefix(path, prefix+"/")
		if !strings.Contains(key, "/") {
			out[key] = data
		}
	}
	return out, rows.Err()
}
func escapeLike(s string) string {
	return strings.NewReplacer(`\`, `\\`, `%`, `\%`, `_`, `\_`).Replace(s)
}
