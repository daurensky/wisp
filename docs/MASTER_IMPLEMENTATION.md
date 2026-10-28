# Native Wisp master implementation

## Scope and architecture

The existing .NET8/WPF client, Go/Pion SFU protocol2, PostgreSQL and local SQLite cache are retained. No Electron, WebView, fake friends/roles/DMs or external Windows configuration changes are introduced. Existing v2 accounts, communities, media, server certificates and session storage remain compatible.

The selected community/channel is independent of `voiceCommunity`/`voiceChannelId`. Rail navigation, text-channel changes and Home do not call disconnect or restart the RTC session. The active-call roster, soundboard, stream names, presence and retries use the retained voice context. Joining another voice channel replaces the connection; leave/signout/quit close it. Voice rows keep their ordinary background; only the viewed text channel is selected.

## Confirmed corrections

- Community navigation explicitly called Disconnect; this was removed and the contexts separated. The selected-community presence snapshot is cleared on navigation so users from the previous community are not merged into the next list.
- `!rtc.Joined` incorrectly produced an error while idle. Idle/voluntary leave are now neutral, pending admission is yellow, connected includes the SFU RTT, and real loss retains a failure flag through recovery. Poor/unknown RTT is a warning, not a false session-loss indicator.
- Admission exceptions used to become generic cancellation after Fail cancelled its token. Structured SFU diagnostics now preserve the original error, phase, last message, channel, peer state and WebSocket close status/reason. SDP, access/refresh token-shaped values, Bearer values and secret fields are redacted. Unsupported versions/messages receive explicit protocol close reasons.
- Capture no longer calls unnecessary `IAudioClient2.SetClientProperties(GameChat/Other)`. The observed HRESULT0x88890001 is AUDCLNT_E_NOT_INITIALIZED; the previous properties structure was already16bytes. Neutral shared IAudioClient capture and Multimedia endpoints preserve RNNoise/Opus and avoid falsely warning on successful capture. No master-volume, registry, driver or other-process settings are written.
- Voice-message playback used the default waveOut endpoint and was disposed with its virtualized row. An owned playback session now survives row unload, preserves pause/seek on output changes, and shares the selected WASAPI endpoint with other independent render streams. Failed renderer initialization/switches release their resources.
- Arbitrary UI notification sounds were removed. The original8 WAV resources are byte-identical to the user archive,48kHz/16bit/mono. `SoundNotificationPolicy` owns local mute/deafen, roster diffs, incoming-message IDs/history baselines and stream-session dedup. Initial/recovered rosters/history are quiet. Optional backwards-compatible stream start timestamps distinguish an existing broadcast from a new start. Soundboard playback is independent of notification playback; mailbox and active sample counts are bounded. Deafen clears queued/active remote cues before its own acknowledgement.

These are code-level findings/regressions. The exact earlier real-network disconnect and device-specific ducking were not proven from the supplied VPS logs. Those logs showed no service restart or OOM.

## Native UI

The centralized palette uses dark blue/gray surfaces, violet/blue accents and a contrast-tested light theme. Four locally embedded Onest faces replace the earlier gg sans/Google Sans request. Onest came from the official Google Fonts OFL source and is not installed or downloaded at runtime.

Community rail72px, channels252px, members220px and optional call panel260px; call panel collapses below1200px, members below1050px, minimum window800px. Compact layouts retain a live call modal. Virtualization is retained for messages/channels/members/call participants. No continuous background rendering or blur is used.

Home exposes real communities/account/actions. Settings has account/profile, theme/Reduce Motion, audio/RNNoise, notification volume, server/details and screen-share sections. Profiles retain round avatars, GIF banners, about/status/game data. Search filters actual members. Existing emoji/sound/file/share forms use the same modal/theme system. Caption buttons have rectangular hit areas, red close hover and one square maximize glyph in normal/maximized states.

## Tray

`VoiceTrayStateResolver`, `VoiceTrayIcons` and `TrayIndicator` reuse the standard Wisp icon outside calls and cache16/20/24/32px state images. Priority: genuine active failure, disconnected, deafen, mute, speaking, silent. Orbs are blue/dark blue/red with a small highlight; mute/deafen use crossed blue symbols. The existing post-processing speech detector drives changes with its hold time; there is no new microphone or per-frame NotifyIcon update. Window/taskbar icons are unchanged. Cached native icon handles are disposed on full exit.

## Verification and limits

PROGRESS.md records stage-by-stage results and the next action. Windows CI executes themes/navigation/settings/GIF/caption/virtualization, codec/RNNoise, actual shared-source PCM/device-lifetime, original sound hashes, dedup and tray-handle regressions; screenshots are archived with test results. Linux runs Core and real PostgreSQL/native-six-client SFU/race tests.

Physical microphones/speakers, Bluetooth profile switching, actual Windows driver ducking, DPI100–200%, provider packet loss and capture permissions still require checks on the affected Windows PC. Native driver access violations cannot be promised recoverable by managed exception handling. Diagnostics are available locally without automatic upload. No production VPS access or deployment was performed.

Update the existing server using SERVER.md, then replace the portable client after fully quitting via the tray. No new registration or database reset is required for existing v2 deployments. Wisp Original/Onest notices are included with the distribution.

## Changed files

```text
PROGRESS.md
README.md
docs/MASTER_IMPLEMENTATION.md
docs/TESTING.md
server/wisp-sfu/main.go
server/wisp-sfu/protocol_test.go
src/Wisp.Core/SfuDiagnostics.cs
src/Wisp.Core/SfuRoom.cs
src/Wisp.Core/SoundNotifications.cs
src/Wisp.Core/VoiceTrayState.cs
src/Wisp.Desktop/Assets/Fonts/Onest-Bold.ttf
src/Wisp.Desktop/Assets/Fonts/Onest-Medium.ttf
src/Wisp.Desktop/Assets/Fonts/Onest-OFL.txt
src/Wisp.Desktop/Assets/Fonts/Onest-Regular.ttf
src/Wisp.Desktop/Assets/Fonts/Onest-SemiBold.ttf
src/Wisp.Desktop/Assets/Fonts/README.md
src/Wisp.Desktop/Assets/Sounds/Wisp-Original-README.md
src/Wisp.Desktop/Assets/Sounds/deafen.wav
src/Wisp.Desktop/Assets/Sounds/message.wav
src/Wisp.Desktop/Assets/Sounds/mute.wav
src/Wisp.Desktop/Assets/Sounds/stream-start.wav
src/Wisp.Desktop/Assets/Sounds/undeafen.wav
src/Wisp.Desktop/Assets/Sounds/unmute.wav
src/Wisp.Desktop/Assets/Sounds/voice-join.wav
src/Wisp.Desktop/Assets/Sounds/voice-leave.wav
src/Wisp.Desktop/Design/Palette.Dark.xaml
src/Wisp.Desktop/Design/Palette.Light.xaml
src/Wisp.Desktop/Design/Tokens.xaml
src/Wisp.Desktop/Design/Typography.cs
src/Wisp.Desktop/Dialogs.cs
src/Wisp.Desktop/MainWindow.Workspace.cs
src/Wisp.Desktop/MainWindow.cs
src/Wisp.Desktop/Media/CommunicationsCapture.cs
src/Wisp.Desktop/Media/FilePlaybackSession.cs
src/Wisp.Desktop/Media/NativeVideoCodec.cs
src/Wisp.Desktop/Media/ShareAudioPlayback.cs
src/Wisp.Desktop/Media/SharedAudioOutput.cs
src/Wisp.Desktop/Media/SoundService.cs
src/Wisp.Desktop/Media/VoiceService.cs
src/Wisp.Desktop/ProfileView.cs
src/Wisp.Desktop/ServerSetupWindow.cs
src/Wisp.Desktop/SettingsView.cs
src/Wisp.Desktop/Theme.xaml
src/Wisp.Desktop/ThemeManager.cs
src/Wisp.Desktop/ThemedWindow.cs
src/Wisp.Desktop/TrayIndicator.cs
src/Wisp.Desktop/VoiceMessagePlayer.cs
src/Wisp.Desktop/VoiceTrayIcons.cs
src/Wisp.Desktop/Wisp.Desktop.csproj
tests/Wisp.Core.Tests/SfuDiagnosticsTests.cs
tests/Wisp.Core.Tests/SoundNotificationTests.cs
tests/Wisp.Core.Tests/VoiceTrayStateTests.cs
tests/Wisp.Desktop.Tests/DesignSystemTests.cs
tests/Wisp.Desktop.Tests/SharedMediaPlaybackTests.cs
tests/Wisp.Desktop.Tests/SoundServiceTests.cs
tests/Wisp.Desktop.Tests/ThemeTests.cs
tests/Wisp.Desktop.Tests/TrayIndicatorTests.cs
```

## Final results (28 October 2026)

- Validated source/test commit: `68b4a2a278c17518046c00d79e980ce4144d36c0`.
- [Windows CI](https://github.com/daurensky/wisp/actions/runs/40649165988):76 Core +29 Desktop tests passed,0 warnings/errors, native RNNoise and portable packaging passed. This includes startup setup/login/signup validation, voice navigation, themes/settings/profile, original sounds, shared playback and tray lifetimes.
- [Linux CI](https://github.com/daurensky/wisp/actions/runs/40649165812): real PostgreSQL/API, seven Go tests/race detector, six native clients/30 downlinks, Opus+independent share audio+H.264, file transfer during media, token refresh, takeover/reentry and invitations; go vet/static build passed.
- Thirteen isolated PCM/audio tests also passed; these overlap Desktop coverage and are not counted as additional unique tests.
- [Sixteen screenshots and TRX results](https://github.com/daurensky/wisp/actions/runs/40649165988/artifacts/12798463431) cover dark/light/compact community, navigation during a call, Home, call modal, profiles, audio/notification settings, setup, login and registration. Images were visually inspected; the discovered raw device-record label was corrected through the actual ComboBox data template, and dark dropdown indicator contrast was corrected.
- [Verified portable](https://github.com/daurensky/wisp/actions/runs/40649165988/artifacts/12799600158):522files,90,015,636bytes; SHA256 `36a56e27498443cd1bae9498ad4cfd4ecc5bcf9f6100e19ef8eee51248a5e5bf`. All8 audio assets match the uploaded archive exactly; RNNoise and Onest OFL notice are present.

Implementation and diagnostics are complete. The physical Windows/Bluetooth/device/driver/DPI/capture/provider checks described above remain manual. No production deployment or OS audio changes were performed. PROGRESS.md is the authoritative continuation log; later documentation-only commits do not change this validated executable.
