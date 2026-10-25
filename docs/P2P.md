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
Screen sharing sends compressed still frames, not a hardware video codec.
