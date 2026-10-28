# Validation

## Automated

- `dotnet build Wisp.sln -c Release`: client/core build.
- `dotnet test tests/Wisp.Core.Tests -c Release`: codec/recovery/timing, local cache, input intent, certificate pinning, server HTTP routes/token headers/cache isolation and dynamic rosters beyond five.
- Windows-only desktop tests: WPF themes, overlays, selected-message appearance, icon resources, virtualization, relative wheel input, codec/RNNoise and saved per-user PCM attenuation. Native RNNoise is built and required in CI.
- `python tools/check_ui_resources.py`: design-resource consistency.
- Connection recovery: bounded old-token overlap, account takeover, abrupt signaling loss/reentry, cancellation of desktop retry, and microphone-thread shutdown error handling. See [RECOVERY.md](RECOVERY.md).
- Go with a real isolated PostgreSQL schema: accounts/password hashing/login, token rotation, membership/invitations, ownership, chat, attachment hashes/immutability and cross-community isolation.
- Native .NET/Go integration: six signed-in clients register through the actual API, join via invitations, create 30 downlinks, exchange microphone/share audio/H.264/events/files through one UDP mux, measure pinned HTTPS RTT, update mute/deafen, leave during delayed SDP and rejoin. No production auth bypass exists. `go test -race` checks Go data races.

```bash
dotnet build tools/Wisp.Sfu.Probe -c Release
export WISP_TEST_DATABASE_URL='postgresql://testuser@127.0.0.1:5432/testdb?sslmode=disable'
export WISP_SFU_PROBE="$PWD/tools/Wisp.Sfu.Probe/bin/Release/net8.0/Wisp.Sfu.Probe.dll"
cd server/wisp-sfu
go test -race -count=1 -v -timeout 180s ./...
go vet ./...
```

The database role must be able to create/drop test schemas; each test cleans up its own schema. Never point this test command at a production database. CI starts an isolated loopback PostgreSQL service. Old provider-specific rules/lease/mesh tests were removed with their implementation; the PostgreSQL/API tests replace them.

## Manual Windows/VPS checks

1. Upgrade the VPS using SERVER.md; confirm `/healthz` reports protocol2. For an existing v2 deployment preserve accounts/data; new registration is needed only for the initial migration from the old external backend. Relaunch and verify session restore.
2. Create a community and invitation. Join from another PC; confirm text, uploaded file, GIF avatar/banner, custom emoji and sound-panel persistence outside a voice call.
3. Join a voice channel from six PCs. Verify visible membership, speaking rings, mute/deafen, exit/rejoin and that counts have no `/5` cap.
4. Click another voice participant, set 25%, confirm only that voice becomes quieter and the setting survives a restart. Check 0% and unchanged other users.
5. Play music in another application, join voice, change devices. Wisp must not lower the other application's volume. Check the shared Multimedia endpoint and neutral microphone capture; no category warning should be shown on successful capture. Driver/Windows and Bluetooth profile behavior cannot be established by software-only tests.
6. Scroll rapidly over text, avatars and lists; there must be no trailing 150ms easing backlog. Drag scrollbars and select/copy chat text. Hover rail icons and confirm the gap below remains unchanged.
7. Share an app/monitor with audio, compare 1080p/60 load and blocked/occluded/minimized sources; check borderless consent or GDI fallback, viewer fullscreen/mini, multiple monitors and continued microphone audio.
8. Close to tray and continue talking. Fully quit via tray before replacing a portable build.

Keep the PR draft until physical audio/capture and the real Astana VPS routes are validated. A first local six-client test hit a publishing-connect timeout; a subsequent diagnostic run passed. Preserve diagnostic output and investigate if CI or real machines reproduce it rather than attributing it to the database without evidence.

Master implementation: test navigation between communities/Home during voice+share, the unified bottom voice controls and central stream surface, selected audio output for simultaneous call/message/notification/soundboard/share streams, original semantic sound delivery, neutral idle status, tray state priority/handle disposal, Onest, rectangular captions and redesigned settings/profile screenshots. See MASTER_IMPLEMENTATION.md.

## UI/UX improvement regressions

The current suite also checks neutral palettes, four-column layout with no extra call panel/member search, same-community clicks preserving row identity/history, mute/deafen intent outside calls and on disk, embedded stream loading/mini/full/ended states, source-picker tabs and removed windows, shared hover GIF/reset, unified profile view/edit/cancel/save/status,22/48px emoji classification, and exact single/multi-message clipboard text. Source-picker screenshots use deterministic preview fixtures; physical Graphics Capture remains a manual check.

Real PostgreSQL tests exercise owner/admin/member authorization, metadata/icon updates, role grant/revocation, media rename/delete, outsider rejection and kicked-member access. Native integration includes two viewers sending explicit join/leave/replay events, verifies cue deduplication and confirms that voice remains connected after leaving viewing.

After updating server and clients, check hover GIFs on message/member/voice/profile, the emoji popup at window edges, actual app/monitor previews and source disappearance, host-only viewer cues, and that leaving a voice channel ends its broadcast while a viewer closing playback does not.

Native expansion: Grayscale inheritance in controls/popups/tooltips, 4-section full settings, headerless profile/Always GIF, selected composited GIF covers, actual image crop/disposal/interlace/timing/loop/cleanup, borrowed call PCM/real waveforms/60s, media preview/seek/deafen, Soundboard gain permissions/persistence, global mini over modals, host-authorized paged audience snapshots and independent stream gain. See NATIVE_EXPANSION.md and the latest PROGRESS.md for measured results.
