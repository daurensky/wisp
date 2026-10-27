# Voice connection recovery

The reported VPS service stayed running, with a 104.7 MiB memory peak under its 768 MiB limit. Those logs do not show an OOM or service restart. TLS handshake failures alone do not explain loss of an established voice connection. The exact original incident remains unconfirmed without session-level diagnostics.

## Changes

- Refresh previously replaced the sole valid access-token hash immediately. The SFU's 20-second check could reject its cached token before the client's next 60-second auth update. Refresh now retains the previous hash for **at most 90 seconds, never beyond its original expiry**. Refresh tokens still rotate once, and logout revokes the session and both access hashes.
- A new authenticated voice login replaces that account's previous voice session, including stale sockets. Authorization and community membership are checked before replacement. An optional global capacity still applies to other accounts. WebSocket ping/pong detects vanished transports; ICE failed timeout is 45 seconds.
- Failed native transport callbacks schedule cleanup outside the callback and bind it to the failed attempt, so delayed cleanup cannot close a subsequent connection.
- The desktop attempts to rejoin the selected voice channel after waits of 1, 2, 4, 8 and 15 seconds. Each attempt still has its own admission timeout. Leaving, switching communities, signing out or quitting cancels pending recovery. Screen capture stops on connection loss and is not restarted by voice recovery.
- The microphone's background Thread catches device errors even after cancellation. A false exception filter used to let such an error terminate the process.
- Invites accept wrapped whitespace and a case-insensitive `wisp-invite:` prefix; expired/unknown and obsolete-format invitations get specific errors. A rejected invitation no longer disconnects the current voice call. The exact reported invite failure was not reproducible; tests cover a newly registered account joining an existing community and the reverse invitation.

Reliable data is not silently dropped on overload. The current desktop uploads chat attachments, voice messages, sound bytes and emoji over HTTPS; the soundboard RTC event contains only the asset ID. The native probe separately exercises legacy SCTP bulk traffic for transport regression coverage. GOMAXPROCS stays 1 for the target one-core VPS; memory limits were not increased without evidence of exhaustion.

## Update an existing v2 deployment

```bash
cd /opt/wisp-server-v2-src
git pull --ff-only
bash server/wisp-sfu/deploy/install.sh 89.126.195.20
```

Upgrade the Windows portable client as well. This update preserves existing v2 accounts, communities, media and TLS identity. Startup adds two nullable session columns with `ADD COLUMN IF NOT EXISTS`; it does not start another clean database or import old external data.

## Diagnostics

The server now records session-stop reasons (media transport failure, auth expiry/revocation, signaling failure, takeover or control backlog). No tokens, passwords or SDP are logged.

```bash
systemctl status wisp-sfu --no-pager -l
journalctl -u wisp-sfu --since '10 minutes ago' --no-pager
```

Windows exceptions and failed reconnect attempts are recorded locally in `%LOCALAPPDATA%\Wisp\logs\diagnostics.log`, with one rotated previous file. Account/invitation token-shaped strings are redacted. Logs do not make native driver crashes recoverable: for a disappearing process also inspect Windows Event Viewer → Application for the faulting module. No automatic upload is performed.

Verification includes bounded token overlap/expiry, one-use refresh tokens, authenticated takeover, abrupt signaling loss and same-instance reentry, six native clients with voice/video/share audio, bidirectional invitations through real PostgreSQL, cancelled microphone-thread failure and cancellation of pending desktop recovery. Real driver and provider routing still require validation on the affected PC.
