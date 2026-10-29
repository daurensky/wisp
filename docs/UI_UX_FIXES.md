# Wisp — UI/UX исправления, 29.10.2026

[Новое ТЗ](UI_UX_FIXES_SPECIFICATION.md) заменяет старые пропорции баннера на **400×140 /20:7**. Onest/Grayscale, нативный WPF, данные и SFU сохраняются.

## Изменения

- Настройки пользователя и сервера: общий внешний отступ48 (прежний16×3), полная тонкая нижняя граница шапки. Navigation адаптируется в минимуме800×560, сохраняя400pxбаннер; scroll работает. Непрозрачные card/page/shell, отдельно анимируемый scrim, без artificialdelay. Roundedclip сохраняет углы карточки.
- Rail: keyedReconcile сохраняет UiRow/control/artwork и текущую анимацию. Pill меняет высоту только при изменении целевого состояния, не обнуляется послеLoaded. Аватары40×40, квадрат/radius8, увеличенный зазор8 междуavatarиpill; общий renderer для upload/fallback/preview.
- Профиль: единый ProfileLayout для view/edit/crop/output400×140/20:7, общие16pxinsets и стили полей. AboutTop/Left/multiline/fixedviewport/scroll; управлениеstatusубрано, существующийprofile.Statusсохраняется. GIFанимация/coverframe сохраняются; если400×140GIFнеумещается в лимит, выдаётсяошибка, без искажения или скрытого уменьшения/flatten.
- Voice: весь дополнительный блокCollapsed вне канала; mic/deafen остаются доступны. UniformGrid4равныеобласти с одинаковыми промежутками, симметричными краями и центрированными иконками. Optimisticjoin показывает «Соединение».
- Каналы: ChannelRow имеет общийhit-testфон и siblingbuttons дляканала/chat/gear. Hover/selectedзаливка общая, gearнеисчезает при движении и in-placeобновлении, gearclick не вызываетJoin/Select. ВложенныхButtonнет.
- Focus-visible: общий per-windowinputmodality, Tab/стрелки дают cue, mouse/Altнеоставляютрамку. Deactivated очищаетvisualfocus/hover/pressed/mousecapture/tooltips и nativepopup; keyboardnavigationсохранена. Hoveropacity принадлежитControlTemplate, ане конфликтующей локальнойMotionанимации.
- Настройки: Handтолько на интерактивных navigationitems. Home содержит реальныйручнойASCIIрисунок исходного логотипа, monospaceConsolas/Viewbox/сохранениепробелов/фирменные цвета двухтем. Остальные графические логотипы сохранены, startup-log также.

## Причины и границы доказательств

| Баг | Подтверждено кодом | Проверка в работающем WPF |
|---|---|---|
| Белое мигание | Card и содержащий его shade одновременноfadeиз0; Sectionpage также становиласьпрозрачной. Это пропускает нижнее содержимое даже приправильномSurfacebrush. | ОжидаетсяCI: legacycounterfactual с белойподложкой + per-CompositionTarget.Rendering pixelchecks новогоopen/switch/close в обеихтемах/размерах. Неутверждаетсяпричина драйвера/внешнегоокна без доказательств. |
| Полоска сервера | RenderServers.Clear/Addпересоздавал всеэлементы; LoadedобнулялHeight; hoverповторносоздавалclock длятойжецели. | ОжидаетсяCI: identity/clock/hover/reload/settingssectionchecks. |
| Шестерёнка | Siblingbuttonsимели собственныеmargin, аDockPanelнеимелhit-testbackground; selectedфон принадлежалтолькоглавнойкнопке; refreshпересоздавалстроку. | ОжидаетсяCI: старыйhovergap + реальныйсистемныйкурсор main→gap→gear→click/update вновойстрокевобеихтемах. НеутверждаетсяневернаяlocalVisibilityгипотеза. |
| Фокус/hover | ЛюбойPreviewKeyDown, включаяAlt, включалcue; Window.Deactivatedнеочищалего. Motion.Interactiveоставляллокальныеhover/scaleзначения, конфликтовавшие сTemplateTriggers. | ОжидаетсяCI: mouse→Tab→activationtransfer/return→popup/slider/pressedcleanup. |

## Проверка

Solution иDesktopTests cross-build: **0warnings/errors**,195WPFресурсов/diffcheckPASS. Новые UIchecks скомпилированы, но не объявляются прошедшими до WindowsCI. Снимки иTRX/ui-evidence будут добавлены после выполнения. Исходный baseline141PASS относится к предыдущемуТЗ.

Основные файлы: `ModalHost.cs`, `SettingsShell*`, `SettingsView.cs`, `Design/{PointerFocus,Motion,VirtualRows,Tokens,Palette*}`, `Theme.xaml`, `ServerRailItem*`, `ChannelRow*`, `ProfileLayout.cs`, `ProfileView.cs`, `ImageEditor.xaml.cs`, `Media/ImageEditingService.cs`, `HomeBrandView*`, `MainWindow*.cs`, `tests/Wisp.Desktop.Tests/*Checks.cs`, `ThemeTests.cs`, `ImageEditorTests.cs`, `PROGRESS.md`.

Backend в этом задании не меняется. Новую регистрацию/очистку базы/перенастройку сервера выполнять не нужно. Проверка физических мониторов/DPI/драйверов остаётся отдельным этапом на устройстве пользователя.
