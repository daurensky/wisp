# Native WPF UI refactor

Branch: `codex/ui-refactor`, based on the current `codex/noise-suppression`
(`69f9dcc`). Draft PR: https://github.com/daurensky/wisp/pull/4.
The WPF/.NET 8 stack, Firebase contracts, SQLite schema, codecs and media pipeline
are retained. No WebView, browser renderer, UI framework or icon NuGet was added.

## Discovery and iterations

The original UI was built in code: MainWindow owned four StackPanels, AuthWindow
was in Dialogs.cs, and FormDialog used ModalHost inside the main window. Shared
styles lived in Theme.xaml; ThemeManager changed six brushes. The other visual
components were ScreenViewer, VoiceMessagePlayer, NotificationArea, AvatarCatalog
and ScrollChrome. ThemedWindow only colored the native Windows caption.

The plan was shown before changes. Linux solution builds succeeded after the
resource foundation, icons, chrome, modal shell, messenger layout, media widgets,
motion policy, recycling tests and final resource checks. The commands are:

```sh
python tools/check_ui_resources.py
dotnet build Wisp.sln -c Release
dotnet test tests/Wisp.Core.Tests -c Release
```

69 Core tests continue to pass. WPF can be cross-compiled on Linux; loading a
ResourceDictionary, drawing actual controls and exercising WindowChrome require
Windows. Windows CI includes native RNNoise/codec tests and a real STA UI test.
Runtime failures found during the first passes were fixed: stopping frozen WPF
transforms, implicit styles not matching a derived ListBox, independent SVG
subpath origins and a duplicate resource key. New assertions/static checks cover
these cases. Final run results and artifact links are recorded in TESTING.md.

## Design and components

- Design/Tokens.xaml: 4/8/12/16/24/32 DIP spacing, 4/8/12 corner radii, 12/14/16/20
  type scale, 36 DIP controls, 40 DIP rows, modal widths 400/480/560 and semantic
  colors. Dark background stays black. ThemeManager replaces frozen brushes for
  dark/light palettes; compatibility aliases keep media views consistent.
- Theme.xaml: Primary, Secondary, Ghost and Danger buttons, visible keyboard
  focus, input/password/checkbox/combo/list/tooltip/menu/seek/scroll styles.
  Buttons have a square minimum hit area; destructive notification clearing asks
  for confirmation. Avatar removal in the profile is only staged until Save.
- Icons.xaml and Icon.cs: 50 small Lucide geometries, frozen and painted with the
  inherited Foreground. Pinned source commit
  `7f1f31d71cc1b28c46b86e0e50fb63406530b5a6`,
  https://github.com/lucide-icons/lucide/tree/7f1f31d71cc1b28c46b86e0e50fb63406530b5a6/icons.
  The exact upstream ISC license and the MIT notice for inherited Feather icons
  ship as licenses/Lucide.txt. Each original SVG path starts a new absolute
  subpath when combined; icon bounds are checked on Windows.
- Buttons.cs and ToggleIconButton.cs: labels plus icons, named icon-only actions,
  current tooltips, cross-fading on/off glyphs, checked colors and finite press
  feedback. Toggle controls expose the UI Automation Toggle pattern. Mic/deafen
  UI updates immediately alongside the existing local action, before awaiting
  the existing presence update. Send/copy show brief checkmark feedback.
- ThemedWindow: a WindowChrome title bar with branded title, minimize,
  maximize/restore and close. Caption hit testing stays with Windows; resizing,
  caption double-click and Win+arrows use the system window behavior. Monitor
  work-area coordinates are physical and allow negative monitor origins.
  Screen-view fullscreen temporarily hides the custom caption and restores it.
- ModalHost/FormDialog: one in-window header, close button, scrollable content,
  bottom actions, one primary action on the right, Esc, Enter and cyclic Tab.
  No empty header is stacked above a second title. Previous focus is restored
  only when a newer modal has not taken over. Close disables actions and removes
  the overlay after its finite transition; interruption/reduced motion also
  completes removal.
- VirtualRows: data factories with WPF recycling virtualization, rather than
  pre-created UIElement items. Avatar/GIF/attachment/player visuals are built for
  realized rows. No outer ScrollViewer disables virtualization. The same control
  is used for servers, channels, members, messages, emoji and sound catalogues.
- MainWindow: aligned panels, 5-person voice capacity badges, separate voice-chat
  text button, avatars/name/time, consecutive-message grouping, inline text-size
  emojis and a compact composer. At widths below 1120 DIP the member column is
  hidden; minimum window size is 800×560 DIP. Grouping changes presentation only.
- ScreenViewer/VoiceMessagePlayer/NotificationArea: the same theme, icons, spacing,
  focus conventions and toggle feedback. Video pixels remain black-backed.

## Motion and performance policy

Motion.Fast/Normal/Slow are 120/200/300 ms, with shared frozen easing resources.
Transitions use opacity and transforms, never animated layout dimensions or blur.
No idle render loop was added. Brief feedback timers stop/unsubscribe after use
or unloading. Hover transitions and press scaling are shared, not implemented
separately by each screen. Bulk history loading does not animate each message;
scrolling skips entrance effects. The notification count appears only on change.

The live "Уменьшить анимации" setting is stored locally in reduce-motion.txt next
to the existing local preferences. Windows ClientAreaAnimation is respected too.
Transitions stop when the window deactivates/minimizes or content unloads; changing
motion preferences also stops them. GIFs keep infinite repeat while allowed, but
pause when hidden, inactive, minimized or reduced motion is enabled. Existing
voice/network/game-presence timers were not changed.

No typing/speaking activity state is invented. The existing client has no such UI
state to display; adding a new detector or wire status would change business/media
logic and is outside this visual refactor. Existing online/game presence remains.

## Screen and dialog coverage

Main messenger, local/Firebase authentication, community creation, invitations,
joining, channel creation, own profile/settings, member profile, audio devices,
RNNoise preference, STUN settings, screen-source/quality/audio selection, active
screen list, fullscreen/mini viewing, emoji picker/upload, sound panel/upload,
notifications/clear confirmation, attachment preview and voice playback.

The OS file-open/save pickers and the WinForms tray menu remain native Windows
surfaces. Replacing them would add duplicate OS behavior; the startup emergency
MessageBox also remains as the fallback when the application cannot initialize.
No community/profile/media/network feature was added or removed.

## Validation limits and manual acceptance

The static resource checker rejects duplicate keys, broken resource references
and off-grid spacing/radii. Windows UI checks cover default/persisted dark/light
colors, native chrome presence, one-window modals, one title, live preferences,
icon geometry bounds, frozen-transform stopping, send/emoji/seek regressions,
compact layout and a 1000-row list realizing fewer than 50 rows in a small viewport.
PNG snapshots from actual Windows WPF rendering are stored in the test-results
artifact; these are UI evidence, not screenshots of a user's hardware.

Not yet verified on a physical Windows PC: border drag/Aero Snap/Win+arrows,
caption drag across monitors with 100/125/150/200% DPI, taskbar avoidance on each
monitor, keyboard/screen-reader interaction end to end, voice while closing to
tray, real GPU/CPU/RAM in idle and a five-person call. No claim of zero CPU or a
measured memory improvement is made. Compare these against the previous portable
build on the same machine and record measured results in TESTING.md.

## Changed files

New: Design/Tokens.xaml, Design/Icons.xaml, Design/Ui.cs, Design/Icon.cs,
Design/Buttons.cs, Design/Motion.cs, Design/ToggleIconButton.cs,
Design/VirtualRows.cs, docs/licenses/Lucide.txt, tools/check_ui_resources.py and
this report.

Updated: Theme.xaml, ThemeManager.cs, ThemedWindow.cs, ModalHost.cs, Dialogs.cs,
MainWindow.cs, AvatarCatalog.cs, NotificationArea.cs, ScreenViewer.cs,
VoiceMessagePlayer.cs, ScrollChrome.cs, Wisp.Desktop.csproj, ThemeTests.cs,
windows.yml, README.md and docs/TESTING.md. Source paths for UI files are under
src/Wisp.Desktop; tests are under tests/Wisp.Desktop.Tests.
