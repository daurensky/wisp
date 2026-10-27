# SFU connection status and desktop polish

Branch: `codex/sfu-desktop-polish`, based on `codex/sfu-server`. No Firebase schema or production server change is required.

- The connection row shows “Подключён · SFU · N мс” and the current channel. Click it for server WSS address, HTTPS RTT and measurement time, media/signaling transport, encryption, ICE candidate types, channel and participant count. <100 ms is green; 100–199 ms is amber; >=200 ms or disconnected is red. A failed health probe clears the old latency and shows amber while media is still connected. Connecting and disconnected states are explicit.
- RTT is a small HTTPS `/healthz` request to the same server, with the same certificate pin, every five seconds while joined. It includes server processing and is not ICMP or a microphone-to-headphones latency measurement. Redirects, unrelated protocol markers and oversized responses are rejected; both headers and response body have a three-second deadline. No Firebase token is sent. Probing stops on leave and does not generate warning sounds.
- The microphone uses WASAPI `AudioCategory_GameChat` instead of `Communications`. Microsoft documents GameChat as voice chat without attenuating other streams. The preferred communication devices, noise suppression and Opus chain are retained; no system-wide Sound/Communications preference or volume is changed.
- “Захват без жёлтой рамки” defaults on in the screen-share dialog. Wisp requests Windows borderless-capture consent and disables the WGC border only when allowed. If unsupported or denied (including portable builds without the required package identity), the existing GDI capture is selected. GDI can cost more CPU and some occluded/hardware-accelerated windows may capture differently. Turning the checkbox off restores WGC with the Windows indicator. Other apps capturing the same source can still cause Windows to display their own indicator. A delayed consent result cannot start a share after leaving/replacing the call.
- The Windows SDK target is 22621 to bind the borderless API; minimum supported OS stays 19041. The API is checked at runtime for Windows 10 compatibility.
- Chat rows have a separate transparent item-container template: clicking a message does not select its background. Native text selection/copy is preserved.
- ScrollViewer wheel input has finite 150 ms easing, retargeted from the current offset. Nested viewers route to the deepest viewer that can move; modifiers, direct scrollbar input, pixel virtualization and reduced-motion preferences are retained. Timers stop at rest, on unload and on window deactivation.
- Roster row margins are removed and avatar/text spacing is reduced; voice participant indentation is halved. The two-line status and comfortable hit targets remain.
- A custom generated Wisp orb logo replaces the generic brand glyph and Windows application icon: login, rail, title bars, modal headers, executable/Explorer, taskbar and tray. The ICO contains 16/20/24/32/48/64/128/256 px sizes. The default Io user avatar is a separate asset.

## Validation

Local solution build: zero warnings/errors. Core: 87 passed, zero failures/skips. Resources: 157 valid named WPF resources. Race-enabled SFU interoperability: five real .NET clients, shared UDP mux, pinned HTTPS health RTT, microphone/share audio/video/files, delayed signaling and leave/rejoin passed. Desktop UI checks now cover the status bands and modal details, chat selection appearance, logo/tray resources, enabled wheel easing and existing virtualization/text-copy behavior. Windows CI is required to execute them.

Manual Windows checks still needed: start a call while music is playing and verify the volume stays constant; observe the OS borderless-consent result for this portable build; compare 1080p/60 CPU usage and capture correctness on the user's GPU; scroll with a real precision touchpad/wheel; check tray and pinned taskbar icon at 100–200% DPI.

Primary references:

- https://learn.microsoft.com/en-us/windows/win32/api/audiosessiontypes/ne-audiosessiontypes-audio_stream_category
- https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired
- https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscaptureaccess.requestaccessasync
