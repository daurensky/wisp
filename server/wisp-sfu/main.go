// Wisp's small-room SFU. Media is forwarded as RTP, never decoded or recorded.
package main

import (
	"context"
	"crypto/tls"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"log"
	"net"
	"net/http"
	"net/url"
	"os"
	"os/signal"
	"regexp"
	"sync"
	"syscall"
	"time"

	"github.com/coder/websocket"
	"github.com/pion/ice/v4"
	"github.com/pion/interceptor"
	"github.com/pion/rtcp"
	"github.com/pion/webrtc/v4"
)

type config struct {
	Listen      string `json:"listen"`
	PublicIP    string `json:"publicIp"`
	UDPPort     int    `json:"udpPort"`
	Certificate string `json:"certificate"`
	PrivateKey  string `json:"privateKey"`
	APIKey      string `json:"apiKey"`
	DatabaseURL string `json:"databaseUrl"`
	MaxSessions int    `json:"maxSessions"`
}
type message struct {
	Type      string                   `json:"type"`
	Version   int                      `json:"version,omitempty"`
	ID        string                   `json:"id,omitempty"`
	Peer      string                   `json:"peer,omitempty"`
	Token     string                   `json:"token,omitempty"`
	Community string                   `json:"community,omitempty"`
	Channel   string                   `json:"channel,omitempty"`
	Session   string                   `json:"session,omitempty"`
	SDP       string                   `json:"sdp,omitempty"`
	Candidate *webrtc.ICECandidateInit `json:"candidate,omitempty"`
	Loss      float64                  `json:"loss,omitempty"`
	Jitter    float64                  `json:"jitter,omitempty"`
}
type identity struct{ UID, Community, Channel, Session string }
type authenticate func(context.Context, message) (identity, error)

var keyPattern = regexp.MustCompile(`^[A-Za-z0-9_-]{1,128}$`)
var sessionPattern = regexp.MustCompile(`^[a-fA-F0-9]{32}$`)
var denied = errors.New("access denied")

// Google verifies the ID token. RTDB rules verify membership; a fresh owned seat
// and a real voice channel are checked independently. No admin credential exists.
func firebaseAuth(c config) authenticate {
	client := &http.Client{Timeout: 8 * time.Second, CheckRedirect: func(_ *http.Request, _ []*http.Request) error { return http.ErrUseLastResponse }}
	return firebaseAuthWithClient(c, client)
}
func firebaseAuthWithClient(c config, client *http.Client) authenticate {
	return func(ctx context.Context, m message) (identity, error) {
		if len(m.Token) == 0 || len(m.Token) > 8192 || !keyPattern.MatchString(m.Community) || !keyPattern.MatchString(m.Channel) || !sessionPattern.MatchString(m.Session) {
			return identity{}, denied
		}
		req, _ := http.NewRequestWithContext(ctx, "POST", "https://identitytoolkit.googleapis.com/v1/accounts:lookup?key="+url.QueryEscape(c.APIKey), jsonReader(map[string]string{"idToken": m.Token}))
		req.Header.Set("Content-Type", "application/json")
		var account struct {
			Users []struct {
				LocalID  string `json:"localId"`
				Disabled bool   `json:"disabled"`
			} `json:"users"`
		}
		if err := fetchJSON(client, req, &account); err != nil || len(account.Users) != 1 || account.Users[0].Disabled || !keyPattern.MatchString(account.Users[0].LocalID) {
			return identity{}, denied
		}
		uid := account.Users[0].LocalID
		get := func(path string, out any) error {
			r, _ := http.NewRequestWithContext(ctx, "GET", c.DatabaseURL+"/communities/"+m.Community+"/"+path+".json?auth="+url.QueryEscape(m.Token), nil)
			return fetchJSON(client, r, out)
		}
		var member bool
		if get("members/"+uid, &member) != nil || !member {
			return identity{}, denied
		}
		var channels []struct {
			ID      string `json:"Id"`
			IsVoice bool   `json:"IsVoice"`
		}
		if get("snapshot/Channels", &channels) != nil {
			return identity{}, denied
		}
		voice := false
		for _, ch := range channels {
			if ch.ID == m.Channel && ch.IsVoice {
				voice = true
			}
		}
		if !voice {
			return identity{}, denied
		}
		var slots map[string]struct {
			UserID    string `json:"UserId"`
			SessionID string `json:"SessionId"`
			SeenAt    int64  `json:"SeenAt"`
		}
		if get("rtcRooms/"+m.Channel, &slots) != nil {
			return identity{}, denied
		}
		now := time.Now().UnixMilli()
		for i := 0; i < 5; i++ {
			s := slots[fmt.Sprintf("s%d", i)]
			if s.UserID == uid && s.SessionID == m.Session && now-s.SeenAt < 45000 && s.SeenAt <= now+10000 {
				return identity{uid, m.Community, m.Channel, m.Session}, nil
			}
		}
		return identity{}, denied
	}
}

type link struct {
	owner      *session
	pc         *webrtc.PeerConnection
	source     *session
	channels   []*webrtc.DataChannel
	candidates int
	answer     bool
}
type session struct {
	hub         *hub
	ident       identity
	authMessage message
	ctx         context.Context
	cancel      context.CancelFunc
	ws          *websocket.Conn
	out         chan message
	mu          sync.Mutex
	links       map[string]*link
	tracks      [3]*webrtc.TrackLocalStaticRTP
	targets     map[*link]bool
	once        sync.Once
	ready       bool // guarded by hub.mu
	dataStart   time.Time
	dataBytes   int
}
type hub struct {
	api        *webrtc.API
	auth       authenticate
	max        int
	mu         sync.Mutex
	sessions   map[*session]bool
	handshakes chan struct{}
}

func newHub(api *webrtc.API, auth authenticate, max int) *hub {
	return &hub{api: api, auth: auth, max: max, sessions: map[*session]bool{}, handshakes: make(chan struct{}, 16)}
}
func (s *session) send(m message) {
	select {
	case <-s.ctx.Done():
	case s.out <- m:
	default:
		go s.stop()
	}
}
func (s *session) stop() {
	s.once.Do(func() {
		s.cancel()
		s.ws.CloseNow()
		h := s.hub
		h.mu.Lock()
		delete(h.sessions, s)
		var closing []*webrtc.PeerConnection
		for other := range h.sessions {
			other.mu.Lock()
			for id, l := range other.links {
				if l.source == s {
					delete(other.links, id)
					closing = append(closing, l.pc)
					other.send(message{Type: "remove", ID: id, Peer: s.ident.UID})
				}
			}
			other.mu.Unlock()
		}
		h.mu.Unlock()
		s.mu.Lock()
		var detached []*link
		for _, l := range s.links {
			closing = append(closing, l.pc)
			detached = append(detached, l)
		}
		s.links = map[string]*link{}
		s.targets = map[*link]bool{}
		s.mu.Unlock()
		for _, l := range detached {
			if l.source != nil {
				l.source.mu.Lock()
				delete(l.source.targets, l)
				l.source.mu.Unlock()
			}
		}

		// Closing PCs invokes asynchronous callbacks; never close under the room lock.
		for _, pc := range closing {
			_ = pc.Close()
		}
	})
}
func (s *session) newPC() (*webrtc.PeerConnection, error) {
	pc, err := s.hub.api.NewPeerConnection(webrtc.Configuration{})
	if err != nil {
		return nil, err
	}
	pc.OnConnectionStateChange(func(st webrtc.PeerConnectionState) {
		if st == webrtc.PeerConnectionStateFailed {
			go s.stop()
		}
	})
	return pc, nil
}
func (s *session) relay(data []byte, label string) {
	// Bound fragmented file/control traffic separately from SRTP. Slow consumers
	// are disconnected instead of accumulating unbounded SCTP buffers.
	if len(data) > 65536 || label == "wisp-events-v2" && len(data) > 4096 {
		go s.stop()
		return
	}
	s.mu.Lock()
	now := time.Now()
	if now.Sub(s.dataStart) > time.Second {
		s.dataStart = now
		s.dataBytes = 0
	}
	s.dataBytes += len(data)
	if s.dataBytes > 32*1024*1024 {
		s.mu.Unlock()
		go s.stop()
		return
	}
	var targets []*link
	for l := range s.targets {
		targets = append(targets, l)
	}
	s.mu.Unlock()
	index := 0
	if label == "wisp-events-v2" {
		index = 1
	}
	for _, l := range targets {
		dc := l.channels[index]
		if dc.ReadyState() != webrtc.DataChannelStateOpen {
			continue
		}
		if dc.BufferedAmount() > 1024*1024 {
			go l.owner.stop()
			continue
		}
		if err := dc.Send(data); err != nil {
			go l.owner.stop()
		}
	}
}
func (s *session) publish(offer string) error {
	s.mu.Lock()
	if s.links["pub"] != nil {
		s.mu.Unlock()
		return denied
	}
	s.mu.Unlock()
	pc, err := s.newPC()
	if err != nil {
		return err
	}
	l := &link{pc: pc}
	s.mu.Lock()
	s.links["pub"] = l
	s.mu.Unlock()
	pc.OnDataChannel(func(dc *webrtc.DataChannel) {
		label := dc.Label()
		if label != "wisp-control-v2" && label != "wisp-events-v2" {
			_ = dc.Close()
			return
		}
		s.mu.Lock()
		duplicate := false
		for _, old := range l.channels {
			if old.Label() == label {
				duplicate = true
			}
		}
		if !duplicate {
			l.channels = append(l.channels, dc)
		}
		s.mu.Unlock()
		if duplicate {
			_ = dc.Close()
			return
		}
		dc.OnMessage(func(m webrtc.DataChannelMessage) { s.relay(m.Data, label) })
	})
	pc.OnTrack(func(t *webrtc.TrackRemote, _ *webrtc.RTPReceiver) {
		index := -1
		for i, tr := range pc.GetTransceivers() {
			if tr.Receiver() != nil && tr.Receiver().Track() == t {
				index = i
				break
			}
		}
		if index < 0 || index > 2 {
			return
		}
		start := time.Now()
		bytes := 0
		for {
			packet, _, err := t.ReadRTP()
			if err != nil {
				return
			}
			if len(packet.Payload) > 1400 {
				return
			}
			if time.Since(start) > time.Second {
				start = time.Now()
				bytes = 0
			}
			bytes += len(packet.Payload)
			if bytes > 3*1024*1024 {
				go s.stop()
				return
			}
			_ = s.tracks[index].WriteRTP(packet)
		}
	})
	if err = pc.SetRemoteDescription(webrtc.SessionDescription{Type: webrtc.SDPTypeOffer, SDP: offer}); err != nil {
		return err
	}
	answer, err := pc.CreateAnswer(nil)
	if err != nil {
		return err
	}
	sdp, err := localSDP(s.ctx, pc, answer)
	if err != nil {
		return err
	}
	s.send(message{Type: "answer", ID: "pub", SDP: sdp})
	return s.hub.activate(s)
}
func localSDP(ctx context.Context, pc *webrtc.PeerConnection, d webrtc.SessionDescription) (string, error) {
	done := webrtc.GatheringCompletePromise(pc)
	if err := pc.SetLocalDescription(d); err != nil {
		return "", err
	}
	timer := time.NewTimer(5 * time.Second)
	defer timer.Stop()
	select {
	case <-ctx.Done():
		return "", ctx.Err()
	case <-timer.C:
		return "", errors.New("gather timeout")
	case <-done:
		return pc.LocalDescription().SDP, nil
	}
}
func (h *hub) activate(s *session) error {
	h.mu.Lock()
	defer h.mu.Unlock()
	if !h.sessions[s] {
		return denied
	}
	s.ready = true
	for other := range h.sessions {
		if other == s || !other.ready || other.ident.Community != s.ident.Community || other.ident.Channel != s.ident.Channel {
			continue
		}
		if err := s.subscribe(other); err != nil {
			return err
		}
		if err := other.subscribe(s); err != nil {
			return err
		}
	}
	return nil
}
func (s *session) subscribe(source *session) error {
	pc, err := s.newPC()
	if err != nil {
		return err
	}
	l := &link{pc: pc, source: source, owner: s}
	id := "s_" + source.ident.UID + "_" + source.ident.Session
	s.mu.Lock()
	s.links[id] = l
	s.mu.Unlock()
	for i, t := range source.tracks {
		sender, err := pc.AddTrack(t)
		if err != nil {
			return err
		}
		go func(video bool) {
			for {
				packets, _, err := sender.ReadRTCP()
				if err != nil {
					return
				}
				for _, p := range packets {
					switch r := p.(type) {
					case *rtcp.PictureLossIndication:
						source.mu.Lock()
						pub := source.links["pub"]
						source.mu.Unlock()
						if pub != nil {
							for _, tr := range pub.pc.GetTransceivers() {
								if tr.Kind() == webrtc.RTPCodecTypeVideo && tr.Receiver() != nil && tr.Receiver().Track() != nil {
									_ = pub.pc.WriteRTCP([]rtcp.Packet{&rtcp.PictureLossIndication{MediaSSRC: uint32(tr.Receiver().Track().SSRC())}})
								}
							}
						}
					case *rtcp.ReceiverReport:
						if video {
							for _, sample := range r.Reports {
								source.send(message{Type: "report", Peer: s.ident.UID, Loss: float64(sample.FractionLost) / 256, Jitter: float64(sample.Jitter) / 90})
							}
						}
					}
				}
			}
		}(i == 2)
	}
	for _, label := range []string{"wisp-control-v2", "wisp-events-v2"} {
		dc, err := pc.CreateDataChannel(label, nil)
		if err != nil {
			return err
		}
		l.channels = append(l.channels, dc)
	}
	source.mu.Lock()
	source.targets[l] = true
	source.mu.Unlock()
	offer, err := pc.CreateOffer(nil)
	if err != nil {
		return err
	}
	sdp, err := localSDP(s.ctx, pc, offer)
	if err != nil {
		return err
	}
	s.send(message{Type: "offer", ID: id, Peer: source.ident.UID, Session: source.ident.Session, SDP: sdp})
	return nil
}
func (h *hub) serve(w http.ResponseWriter, r *http.Request) {
	if r.URL.Path != "/rtc" || r.URL.RawQuery != "" {
		http.NotFound(w, r)
		return
	}
	select {
	case h.handshakes <- struct{}{}:
	default:
		http.Error(w, "busy", 503)
		return
	}
	released := false
	defer func() {
		if !released {
			<-h.handshakes
		}
	}()
	ws, err := websocket.Accept(w, r, &websocket.AcceptOptions{})
	if err != nil {
		return
	}
	defer ws.CloseNow()
	ws.SetReadLimit(70000)
	ctx, cancel := context.WithCancel(r.Context())
	defer cancel()
	joinCtx, joinCancel := context.WithTimeout(ctx, 12*time.Second)
	_, data, err := ws.Read(joinCtx)
	var m message
	if err != nil || json.Unmarshal(data, &m) != nil || m.Type != "join" || m.Version != 1 {
		joinCancel()
		return
	}
	ident, err := h.auth(joinCtx, m)
	joinCancel()
	if err != nil {
		_ = ws.Close(websocket.StatusPolicyViolation, "Firebase access denied")
		return
	}
	s := &session{hub: h, ident: ident, authMessage: m, ctx: ctx, cancel: cancel, ws: ws, out: make(chan message, 32), links: map[string]*link{}, targets: map[*link]bool{}}
	codecs := trackCodecs()
	for i := range s.tracks {
		t, e := webrtc.NewTrackLocalStaticRTP(codecs[i].RTPCodecCapability, fmt.Sprintf("t%d", i), ident.UID)
		if e != nil {
			return
		}
		s.tracks[i] = t
	}
	h.mu.Lock()
	allowed := len(h.sessions) < h.max
	roomCount := 0
	for old := range h.sessions {
		if old.ident.UID == ident.UID {
			allowed = false
		}
		if old.ident.Community == ident.Community && old.ident.Channel == ident.Channel {
			roomCount++
		}
	}
	if roomCount >= 5 {
		allowed = false
	}
	if allowed {
		h.sessions[s] = true
	}
	h.mu.Unlock()
	if !allowed {
		_ = ws.Close(websocket.StatusPolicyViolation, "room or server full; duplicate login")
		return
	}
	<-h.handshakes
	released = true
	defer s.stop()
	go func() {
		for {
			select {
			case <-ctx.Done():
				return
			case out := <-s.out:
				b, _ := json.Marshal(out)
				wc, cc := context.WithTimeout(ctx, 8*time.Second)
				err := ws.Write(wc, websocket.MessageText, b)
				cc()
				if err != nil {
					s.stop()
					return
				}
			}
		}
	}()
	s.send(message{Type: "ready", Version: 1})
	// Auth refresh is received in-band, then checked every 20s with the seat lease.
	go func() {
		ticker := time.NewTicker(20 * time.Second)
		defer ticker.Stop()
		for {
			select {
			case <-ctx.Done():
				return
			case <-ticker.C:
				s.mu.Lock()
				a := s.authMessage
				pub := s.links["pub"]
				s.mu.Unlock()
				if pub == nil {
					s.stop()
					return
				}
				ac, cc := context.WithTimeout(ctx, 10*time.Second)
				id, err := h.auth(ac, a)
				cc()
				if err != nil || id != s.ident {
					s.stop()
					return
				}
			}
		}
	}()
	budgetStart := time.Now()
	budget := 0
	for {
		_, data, err = ws.Read(ctx)
		if err != nil {
			return
		}
		if time.Since(budgetStart) > time.Second {
			budgetStart = time.Now()
			budget = 0
		}
		budget++
		if budget > 256 {
			return
		}
		var in message
		if json.Unmarshal(data, &in) != nil {
			return
		}
		switch in.Type {
		case "offer":
			if in.ID != "pub" || len(in.SDP) > 64000 || s.publish(in.SDP) != nil {
				return
			}
		case "answer":
			s.mu.Lock()
			l := s.links[in.ID]
			valid := l != nil && l.source != nil && !l.answer
			if valid {
				l.answer = true
			}
			s.mu.Unlock()
			if !valid || len(in.SDP) > 64000 || l.pc.SetRemoteDescription(webrtc.SessionDescription{Type: webrtc.SDPTypeAnswer, SDP: in.SDP}) != nil {
				return
			}
		case "candidate":
			s.mu.Lock()
			l := s.links[in.ID]
			if l != nil {
				l.candidates++
			}
			s.mu.Unlock()
			if l == nil || l.candidates > 64 || in.Candidate == nil || len(in.Candidate.Candidate) > 2048 || l.pc.AddICECandidate(*in.Candidate) != nil {
				return
			}
		case "auth":
			if len(in.Token) == 0 || len(in.Token) > 8192 {
				return
			}
			s.mu.Lock()
			s.authMessage.Token = in.Token
			s.mu.Unlock()
		default:
			return
		}
	}
}
func trackCodecs() [3]webrtc.RTPCodecParameters {
	return [3]webrtc.RTPCodecParameters{
		{RTPCodecCapability: webrtc.RTPCodecCapability{MimeType: webrtc.MimeTypeOpus, ClockRate: 48000, Channels: 2, SDPFmtpLine: "minptime=10;useinbandfec=1"}, PayloadType: 111},
		{RTPCodecCapability: webrtc.RTPCodecCapability{MimeType: webrtc.MimeTypeOpus, ClockRate: 48000, Channels: 2, SDPFmtpLine: "stereo=1;minptime=10"}, PayloadType: 112},
		{RTPCodecCapability: webrtc.RTPCodecCapability{MimeType: webrtc.MimeTypeH264, ClockRate: 90000, SDPFmtpLine: "packetization-mode=1;profile-level-id=42e02a;level-asymmetry-allowed=1", RTCPFeedback: []webrtc.RTCPFeedback{{Type: "nack"}, {Type: "nack", Parameter: "pli"}}}, PayloadType: 96},
	}
}
func mediaAPI(publicIP string, port int) (*webrtc.API, func(), error) {
	var settings webrtc.SettingEngine
	settings.SetSCTPMaxReceiveBufferSize(1024 * 1024)
	settings.SetSCTPMaxMessageSize(65536)
	var closeFn = func() {}
	if port != 0 {
		conn, err := net.ListenUDP("udp4", &net.UDPAddr{Port: port})
		if err != nil {
			return nil, closeFn, err
		}
		mux := ice.NewUDPMuxDefault(ice.UDPMuxParams{UDPConn: conn})
		settings.SetICEUDPMux(mux)
		settings.SetNAT1To1IPs([]string{publicIP}, webrtc.ICECandidateTypeHost)
		settings.SetNetworkTypes([]webrtc.NetworkType{webrtc.NetworkTypeUDP4})
		closeFn = func() { _ = mux.Close() }
	}
	settings.SetICETimeouts(10*time.Second, 20*time.Second, 2*time.Second)
	engine := &webrtc.MediaEngine{}
	for i, c := range trackCodecs() {
		kind := webrtc.RTPCodecTypeAudio
		if i == 2 {
			kind = webrtc.RTPCodecTypeVideo
		}
		if err := engine.RegisterCodec(c, kind); err != nil {
			return nil, closeFn, err
		}
	}
	registry := &interceptor.Registry{}
	if err := webrtc.RegisterDefaultInterceptors(engine, registry); err != nil {
		return nil, closeFn, err
	}
	return webrtc.NewAPI(webrtc.WithMediaEngine(engine), webrtc.WithInterceptorRegistry(registry), webrtc.WithSettingEngine(settings)), closeFn, nil
}
func main() {
	file := flag.String("config", "/etc/wisp-sfu/server.json", "configuration path")
	flag.Parse()
	b, err := os.ReadFile(*file)
	if err != nil {
		log.Fatal("cannot read configuration")
	}
	var c config
	if json.Unmarshal(b, &c) != nil {
		log.Fatal("invalid configuration")
	}
	db, err := url.Parse(c.DatabaseURL)
	if err != nil || db.Scheme != "https" || db.Host != "wisp-dd1ec-default-rtdb.asia-southeast1.firebasedatabase.app" || db.RawQuery != "" || db.Path != "" {
		log.Fatal("unexpected Firebase database")
	}
	if net.ParseIP(c.PublicIP) == nil || c.UDPPort < 1024 || c.UDPPort > 65535 || c.MaxSessions < 1 || c.MaxSessions > 20 || c.APIKey == "" {
		log.Fatal("invalid media configuration")
	}
	api, closeFn, err := mediaAPI(c.PublicIP, c.UDPPort)
	if err != nil {
		log.Fatal(err)
	}
	defer closeFn()
	h := newHub(api, firebaseAuth(c), c.MaxSessions)
	mux := http.NewServeMux()
	mux.HandleFunc("/rtc", h.serve)
	mux.HandleFunc("/healthz", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/plain")
		fmt.Fprintln(w, "wisp-sfu protocol=1")
	})
	srv := &http.Server{Addr: c.Listen, Handler: mux, ReadHeaderTimeout: 5 * time.Second, IdleTimeout: 30 * time.Second, MaxHeaderBytes: 8192, TLSConfig: &tls.Config{MinVersion: tls.VersionTLS12}}
	stop := make(chan os.Signal, 1)
	signal.Notify(stop, syscall.SIGTERM, syscall.SIGINT)
	go func() {
		<-stop
		h.mu.Lock()
		var sessions []*session
		for s := range h.sessions {
			sessions = append(sessions, s)
		}
		h.mu.Unlock()
		for _, s := range sessions {
			s.stop()
		}
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_ = srv.Shutdown(ctx)
	}()
	log.Print("Wisp SFU protocol 1 started (no media recording)")
	if err := srv.ListenAndServeTLS(c.Certificate, c.PrivateKey); err != nil && !errors.Is(err, http.ErrServerClosed) {
		log.Fatal("HTTPS listener failed: ", err)
	}
}
