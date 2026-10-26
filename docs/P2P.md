# Default Firebase mode: WebRTC mesh

Firebase mode uses `RtcMesh`/`RtcPeer`: up to four direct DTLS/SRTP peer connections per user with SCTP control/file channels, configurable UDP STUN (Google defaults), no TURN by the owner’s explicit decision and no participant forwarding. Firebase authenticates signalling and atomically limits the room to five leased seats. SDP is bound to both peers' current session IDs. See [FIREBASE.md](FIREBASE.md) for setup, permissions, NAT limitations and transport details. There is no guaranteed connectivity through symmetric NAT or blocked UDP.

The following TLS implementation remains a legacy fallback only when Firebase configuration is absent.

# Direct P2P sessions

Wisp uses participant-hosted TLS 1.2/1.3 TCP sessions. One community owner runs the
listener inside the desktop client and forwards chat and voice/video frames to
the other participants. This is a star topology, not a full mesh. There is no
hosted account database, signalling service, media server, or automatic cloud relay.
The host leaving ends that session; local SQLite history remains on each PC.

An invite contains the host endpoint, SHA-256 certificate pin and a 256-bit random
capability token. Share invitations privately. The token rotates whenever the
community starts hosting. Certificates are persisted locally; the host binds
each observed account ID to the first client certificate it sees. This is local
identity with trust on first use, not a global username authority. TLS private
keys and the SQLite database are local application data and should be protected
with Windows account permissions and device encryption.

Direct connections work on a LAN or over a user-managed VPN. Internet users must
configure an accessible endpoint, typically a port forward for TCP 45832 and a
Windows Firewall rule. The invite initially advertises a LAN IPv4 address; replace
its host with your public address when using port forwarding. CGNAT can prevent
inbound connections. Wisp deliberately does not silently use STUN/TURN services;
a VPN or an accessible participant is required in those networks.

The hosting peer authoritatively limits each voice channel to five participants.
Voice, screen frames and soundboard commands go only to current members of the
same voice channel. Deafened peers receive no voice or soundboard commands and
cannot transmit microphone audio. Media queues are bounded and realtime frames
are dropped on congestion. TCP head-of-line blocking can increase voice latency
on lossy links; this initial implementation is best suited to LAN/VPN networks.
Screen sharing uses the shared WGC/GDI capture and software H.264 MFT pipeline; the TLS fallback retains TCP head-of-line limitations.

Network settings are local (`network.json`); one string URL per ICE server. STUN servers see public IPs, and SDP stored in Firebase contains ICE addresses. User-facing connection-path notices show candidate types only. No TURN server/API or relay CI job is included in this scope.

Direct mesh recovery uses a fresh Attempt ID per negotiation with bounded 2/5/15/30-second retries, trickle candidates (64 fixed slots), server-derived lease time and a single room SSE connection. Room events contain only wake metadata: SDP/ICE addresses are fetched through pair-private paths with session checks and a 10-minute read TTL. No stream stays open outside voice. A network-address change debounces for 1.5 seconds before ICE restart; direct reachability is still constrained by NAT/firewall policy.

Voice FEC/PLC and the 40–80ms adaptive PCM buffer are receiver-local changes; no extra audio data is inserted into the external protocol. Screen key-frame/pause commands use the existing events channel. RTP timestamps follow elapsed time, including skipped frames/DTX gaps. All clients must update together; the owner must publish hardening rules after distributing the client and stopping old processes, before starting new ones. See FIREBASE.md.

SIPSorcery 10.0.17 retains completed nomination state after restartIce. Previously nominated pairs therefore request a fresh native peer/Attempt through bounded backoff; no private library state is patched. A real local-peer test covers this fallback and media/control after replacement, but network-interface switching remains a manual scenario.
