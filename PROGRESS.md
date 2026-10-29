# Wisp — исправления UI/UX (29.10.2026)

Новое [ТЗ пользователя](docs/UI_UX_FIXES_SPECIFICATION.md). Ветка `codex/postgres-backend`, исходный commit **de98934aca2d86867fec6e8bf63d94197d30b84c**, дерево было чистым. Предыдущее ТЗ завершено, повторно не выполняется. Banнер400×140/20:7 заменяет прежние16:9; Onest/Grayscale/голос/SFU/данные сохраняются.

## Текущий этап — аудит завершён, реализация

- Изучены `ModalHost`, `SettingsShell/SettingsView`, `PointerFocus/Motion/Theme`, `ServerRailItem/VirtualRows`, `ProfileView/ImageEditor/ImageEditingService`, `MainWindow` voice/rail/channels/Home. Изменения распределены по независимым компонентам; MainWindow и итоговая интеграция выполняются отдельно.
- **Подтверждено кодом:** RenderServers очищает список, Loaded сбрасывает pillHeight в0, hover заново создаёт анимацию неизменившейся цели. `PointerFocus.PreviewKeyDown` делает визуальный focus на любом keydown (включая Alt), обработчика деактивации нет. About наследует TextBox.VerticalContentAlignment=Center;16:9 продублировано в3компонентах; statuspicker явно создаётся в edit. Голосовые действия остаются видимыми вне звонка. Gear и mainButton имеют раздельные границы/фон, строки пересоздаются при обновлениях.
- **Пока не доказана точная причина именно белого кадра:** opacity применяется и к card, и к содержащему его shade, а при смене секции fade используется для page. Это подтверждает просвечивание, но источник белого требует работающей WPFпроверки. Утверждать драйверную/системную причину без доказательств нельзя.
- Изменены этот журнал и копия ТЗ; компоненты в работе. Новые результаты не выдаются за предыдущие141PASS.
- **Этап реализации завершён:** общий48px inset и headerseparator/opaque modal lifecycle; key-preserved rail/channelrows; serveravatars40px/radius8; banner400×140/20:7/единые16pxinsets/TopAbout/безstatuseditor; focus-visible/cleanupactivation; voiceactionsCollapsed/UniformGrid; HomeASCII. Изменены `ModalHost`, `Settings*`, `Design/{PointerFocus,Motion,Tokens,VirtualRows,Palette*}`, `ServerRailItem*`, `ChannelRow*`, `ProfileLayout/ProfileView/ImageEditor/ImageEditingService`, `HomeBrandView*`, `MainWindow*` и UItests. Сборка solution **0warnings/errors**,195WPFресурсов/diffcheckPASS. При интеграции исправлен missingSystem.IO в новом тесте.
- Новые checks выполняют настоящие pointermove/click, native activation/focus/keyboard, counterfactual старого whiteopacity/hovergap, кадры CompositionTarget.Rendering сконтрастной подложкой, обе темы/800×560 иобычныйразмер. Пока только скомпилированы: **WindowsUIисполнение ещё не подтверждено**.
- **Checkpoint опубликован:** `8ba4db3c34a3cb864c9fa9326035bc64b96312f9`,38изменённых/новыхфайлов; cross-buildDesktopTestsпослеevidence5.15с,0warnings/errors.
- **Первый WindowsCI [40893705513](https://github.com/daurensky/wisp/actions/runs/40893705513):**96Core PASS,45DesktopPASS/1UIFAIL приначальнойзагрузкеServerRailItem. Подтверждена runtimeXAMLошибка: StaticResourceDouble8 переданвColumnDefinition.Width, требующийGridLength. Исправлено явной инициализациейPillGapColumn.Width=newGridLength(Ui.Metric(...)); сохраняетсяобщий8pxресурс. [Исправление7e7fd07](https://github.com/daurensky/wisp/commit/7e7fd07f72f1eabcaa84300c1d416bf801f0a51f), повторнаяDesktopTestsbuild0warnings/errors18.32с.
- **Второй WindowsCI [40895174895](https://github.com/daurensky/wisp/actions/runs/40895174895):** исправленныйrail загружается,**96Core PASS,45DesktopPASS/1UIFAIL** на начальноможиданииHidden вновомnativepointertest. Добавлено ожиданиефактическойдоставкипозицииMouse.GetPosition иhoverstate, прежниеassertions/проверкаclickсохранены; диагностируютсяDPsource/focus. Это корректировка синхронизации теста, причина ещёпроверяется повторнымCI. [a21b94f](https://github.com/daurensky/wisp/commit/a21b94f141682193c38e721b65a3fbb45173c72f), DesktopTestsbuild0warnings/errors20.26с. Текущий [Windows40896277727](https://github.com/daurensky/wisp/actions/runs/40896277727) выполняется.
- **Третий WindowsCI [40896277727](https://github.com/daurensky/wisp/actions/runs/40896277727):** nativepointerсинхронизация пройдена, старыйhovergap воспроизведён и новыеgearclick/update работают в тёмнойтеме. UIharnessостановилсянасветлойтеме из-за собственногонакопленногоcounter: послеdarkchatclick счётчик1, аgearassertошибочноожидал0. Исправленосравнением«до/после»gearclick, чтобыпроверятьименноотсутствиепобочногоchatдействия. Новые5UIchecksтеперьсобираютвсеошибкизаодинпроход, неослабляятребования; остальныеcleanup/старые141сценариисохранены.
- **Четвёртый CI [40897557447](https://github.com/daurensky/wisp/actions/runs/40897557447),68401a9:** channellegacygap + nativegearclick/update прошли обе темы (ui-evidence получен), rail/ASCII/400×140profile/About/statuschecks прошли. Aggregateвыявил2ошибкиharness: OwnerназначалсяещёнепоказанномуprobeWindow, ицветвcornerмерилсячерезVisualBrushнормализациюbounds. OwnerназначаетсяпослеShow; pixelcheckпереведённаполныйRenderTargetBitmapссырымCopyPixels; надругомцветесохраняютсякоординаты/снимок. ВCIсистемныеанимациивыключены, поэтому legacy/newopacitypipeline дополнительноисполняетсялокальнымиWPFclocks вtestvisualsбезизменениянастроекОС. Backend/LinuxCI10PASS.
- **Пятый CI [40899592698](https://github.com/daurensky/wisp/actions/runs/40899592698),fe8261e:** Focuskeyboard/Alt/reactivation/tooltips/pressedcleanup прошли доnativepopupslider; выявлен реальноотсутствовавший FocusRing вSliderTemplate (раньшеменялсянеиспользуемыйForeground). Добавлен общийkeyboard-only BorderFocusRing. Legacyopacitycounterfactualзамерялсядо первогоtick,когдаclockещёнеактивен; теперьсэмплируютсяреальныекомпозиционныекадры110мс иbrightpeak, безизменениянастроекОС. PixelnewpathостаётсяfullRenderTargetBitmap.
- **Следующее конкретное действие:** выполнить WindowsCI опубликованного дерева; разобрать реальные ошибки/TRX/ui-evidence/snapshots, исправить обнаруженное, затем проверитьportable и отдельно описать подтверждённые4причины. ProductionVPS не меняется; backendвэтомТЗнепереписывается.

---

# Wisp — комплексная доработка WPF, голоса и демонстрации (29.10.2026)

Новое пользовательское [ТЗ](docs/NATIVE_EXPANSION_SPECIFICATION.md), дополнение к предыдущим требованиям. Исходный commit `4058b21`, ветка `codex/postgres-backend`, исходное дерево было чистым. Шрифт **Onest** сохраняется.

## Итог — программная часть завершена и проверена (29.10.2026)

- Проверенное дерево **451cb920ce2eaa8f97fb5460a7198295064cda5b** (последние функциональные изменения dd7ab22). [Windows40782081082](https://github.com/daurensky/wisp/actions/runs/40782081082): **96Core +45Desktop =141PASS**, C#0warnings/errors, RNNoise/applicationbuild/publish/package PASS. [Linux40782081241](https://github.com/daurensky/wisp/actions/runs/40782081241):10Go/API/PostgreSQL/native/race/vet/staticSFU PASS.
- [Готовый Windowsportable](https://github.com/daurensky/wisp/actions/runs/40782081082/artifacts/12858165749), [25снимков/TRX](https://github.com/daurensky/wisp/actions/runs/40782081082/artifacts/12857472781), [LinuxSFU](https://github.com/daurensky/wisp/actions/runs/40782081241/artifacts/12858210126). Все получены;25снимков просмотрены, дополнительные полноразмерные проверки picker/settings/profile и моноширинный ASCII выполнены. Проверки sourcepreview используют явно заданные fixtures.
- Portable `/workspace/wisp/artifacts/Wisp-native-v4-win-x64.zip`: **90,162,400байт,522файла**, SHA256 **0639d2dc52f0dd8a8c7aa01b1ac8275477fef21c1128874fcfb431ce5ae975ee**. Проверены внешний artifactdigest, Wisp.exe/DLL/RNNoise, Onest/RNNoiseлицензии и побайтовое соответствие8WAV. LinuxELF amd64 SHA256 **5869391214e59ea53ceabe074faf4f92d22a2783eb46e7d8670d0d38f76a3010**.
- Выполнены Grayscale+Onest, права emoji reuse/upload, rail/channelactions/managerAPI, общая полнооконная SettingsShell, headerlessprofile, реальный editor crop/GIF/staticcover, GIFfocus/hover, waveform/совместная запись, composer/медиа, SoundboardPopup/реальные gain, новый sharepicker/autoownmini/globaloverlay/drag/aspect/зрители/независимая громкость, startupHome/ASCII. Обновление аватара не сбрасывает чат/selection/scroll; selectedcover на паузе сохраняет позицию animator.
- Все файлы/поведение/результаты: [docs/NATIVE_EXPANSION.md](docs/NATIVE_EXPANSION.md). PR https://github.com/daurensky/wisp/pull/8 . Следующие commits после451cb92 меняют только отчёты/инструкции, исполняемый код не меняется.
- **Осталось:** обновить существующий VPS и проверить физические Windowsмикрофоны/драйверы/GPU/GraphicsCaptureразрешения/мониторы/DPI и маршрут КЗ—Корея. В сессии productionVPS не менялся; регистрации заново не требуется, PostgreSQLv2/файлы/сертификат сохраняются.
- **Следующее конкретное действие:** выполнить команды ниже на VPS, полностью закрыть старый Wisp через трей и заменить portable. Затем два пользователя проверяют голос+запись одновременно, shareсо звуком/60FPS/mini, mute/streamgain/зрителей, profileGIFeditor/cover, emojiотправку участником и запрет загрузки новогоemoji. При новом сбое продолжать с логом, не переписывать завершённые этапы.

```bash
cd /opt/wisp-server-v2-src
git pull --ff-only
bash server/wisp-sfu/deploy/install.sh 89.126.195.20
```

### История текущего ТЗ — завершённые этапы

#### Аудит, backend и интеграция (историческая запись)

- **Backend:** отправка существующих emoji участником больше не выполняет запрещённый повторный PUT; новые emoji по-прежнему доступны только owner/admin. Добавлены manager-only GET, создание/переименование/удаление каналов, громкость Soundboard и static coverFrame профиля/иконки. Миграции additive, v2-данные сохраняются.
- **Пройдены:** Core **96 PASS**; реальный PostgreSQL/API/Go race **10 PASS**, включая native SFU шести клиентов/30 downlinks, голос+отдельное audio+H264+2MBфайл, token/takeover/rejoin, фактическое owner-upload → member-reuse/send и member-new-upload403. Полный лог `/workspace/v4-backend-native.log`. Реальные PCM/waveform/60s/подписки/gain: **6/6 PASS** Linux-smoke. GIFcodec независимо проверен Pillow на disposal1/2/3, transparency, timing/loop. Это не проверка физических Windows-устройств.
- **UI/components:** Grayscale+Onest; native serverrail/pill; общая полнооконная SettingsShell; четыре секции настроек; server management; профиль с AlwaysGIF,16:9баннером и увеличенной аватаркой; reusable image/GIFeditor; realwaveforms; AttachmentCard/MediaViewer; anchored SoundboardPopup; source/share dialog и глобальный overlaymini. Ключевые новые файлы `SettingsShell*`, `CommunitySettingsView.cs`, `ServerRailItem*`, `ImageEditor*`, `Media/ImageEditing*`, `WaveformView*`, `RecordingWaveformView*`, `AttachmentCard*`, `MediaViewer.cs`, `SoundboardPopup*`, `ScreenShareDialog*`, `ScreenViewerSurface*`.
- **Интеграция MainWindow:** startupHome/ASCIIлог, две нижние группы, нейтральный leave, managergear и sectionplus/channelgear; динамический composer/disabledsend; запись одновременно со звонком; медиа-карточки; popupзвуков/актуальная громкость; обработка удалённого канала. MainWindow разделён дополнительно на `Channels`, `Composer`, `Soundboard`, обновляется `Streams`.
- **Исправлены при интеграции:** повторная загрузка emoji/файла перед отправкой; запрещённая запись во время звонка; stale cached Soundboardgain; draftstate send/stop/cancel; дубли перенесённых screenметодов и затенение WPF TextInput. Общая Windowsсборка/новыеUIтесты ещё **не подтверждены**.
- **Интеграция завершена:** stream hooks/viewer snapshots/autoownmini/realvolume подключены. Solution cross-build **0warnings/errors**,96Core PASS;188ресурсов (включая все новые корневые XAML),diff-check и go vet PASS. Добавлены UIпроверки Grayscale/профилей/4секций/права/каналы/реальные gain/mini/snapshots и7imageeditorтестов. [Подробный отчёт](docs/NATIVE_EXPANSION.md).
- **Checkpoint опубликован:** `9a61fc45ac5402a2869c0e11bbfbc375b13176b1` в `codex/postgres-backend`,83изменённых/новых файла. Последняя solutionсборка14.13с:0warnings/errors. В harness включены реальные ImageEditor save/cancel/cover/zoom и livePCM recordingwave; UIисполнение ещё ожидается.
- **Первый CI77f1546:** [Linux40780946869](https://github.com/daurensky/wisp/actions/runs/40780946869) PASS (PostgreSQL/native/race/vet/staticSFU). [Windows40780946705](https://github.com/daurensky/wisp/actions/runs/40780946705):96Core PASS,42Desktop PASS/3FAIL. Исправлены именно устаревшие assertions ширины252→288, имени кнопки «Запустить» и типа InvalidDataException для обрезанного GIF; сценарии не удалялись. Реальный editor save/cancel/cover/livewave и ранние права/Grayscale/streamchecks уже выполнены,6снимков получены/просмотрены.
- **Follow-up:** выбранная coverFrame показывается также на паузе AlwaysGIF без перемотки внутреннего animator; после редактирования профиля существующие аватары чата обновляются через weakhosts без сброса строк/scroll/selection, voiceavatars обновляются сразу. Добавлены регрессии blur/resume и изменения уже отправленного аватара. Файлы `AvatarCatalog.cs`, `MainWindow.Avatars.cs`, `MainWindow.Profile.cs`, `MainWindow.cs`, Design/UI/imageтесты.
- **Follow-up `451cb92` проверен:** Windows [40782081082](https://github.com/daurensky/wisp/actions/runs/40782081082) прошёл Core/DesktopUI и applicationbuild; portable пока собирается. Linux [40782081241](https://github.com/daurensky/wisp/actions/runs/40782081241) PASS:10Go/native/race/vet/staticSFU. Исправления ранних тестов и новые cover/avatarрегрессии включены в этот проход.
- Portable/TRX/25снимков получены и проверены; финальные результаты и точка продолжения указаны выше. ProductionVPS не менялся.

---

# Wisp — выполнение нового ТЗ улучшений (28.10.2026)

Источник: [полный новый текст пользователя](docs/IMPROVEMENT_SPECIFICATION.md). Начальная точка: `5f5ec8d`, ветка `codex/postgres-backend`, рабочее дерево было чистым. Новые требования заменяют противоречащие им решения предыдущего Master; Onest и существующие данные PostgreSQL сохраняются.

## Итог нового ТЗ — программная часть завершена (28.10.2026)

- Последние проверенные исходники/тесты: **a466347**. [Windows CI40688202550](https://github.com/daurensky/wisp/actions/runs/40688202550): **93Core+29Desktop=122PASS**,0warnings/errors, RNNoise/build/package PASS. [Linux CI40688202551](https://github.com/daurensky/wisp/actions/runs/40688202551): PostgreSQL/API/native/race/vet/staticbuild PASS.
- [Готовый portable](https://github.com/daurensky/wisp/actions/runs/40688202550/artifacts/12815199083), [18снимков/TRX](https://github.com/daurensky/wisp/actions/runs/40688202550/artifacts/12815718466), [SFU Linux](https://github.com/daurensky/wisp/actions/runs/40688202551/artifacts/12815518708). Снимки получены и просмотрены; source-picker использует заявленные тестовые previewfixtures. Контраст кнопок входа/регистрации исправлен и проверен отдельно.
- Portable `/workspace/wisp/artifacts/Wisp-improvements-win-x64.zip`: **90,049,093байта,522файла**, SHA256 **37e203f39febab567043cb790bca024cd4c23410c69e16454539f390df9f8fc5**. Проверены Wisp.exe, DLL, RNNoise, лицензия Onest и побайтовое совпадение8WAV. Артефакт GitHub также проверен по опубликованному SHA256.
- Реализованы нейтральный UI/Onest, четыре области без отдельной панели звонка/поиска, общая нижняя панель с persistentmute/deafen, профиль view/edit, сервер/роли/медиа с backendpermissions, picker/центральный stream/индикаторы/события зрителей, emoji-popup22/48px, hoverGIF/reset, точное single/multi-message copy, выделяемые уведомления, effects×0.5, минимальная Home. Исправлены сохранение refresh-token на Home и гонка создания сообщества/синхронизации доступа.
- Данные PostgreSQL v2, локальные кэши, сертификат и протокол2 сохранены. Регистрация заново не требуется. Полный итог/изменённые файлы/ограничения: [docs/IMPROVEMENTS.md](docs/IMPROVEMENTS.md). PR: https://github.com/daurensky/wisp/pull/8 . Финальный commit после a466347 меняет только отчёт и этот журнал.
- **Осталось:** установить обновление на реальный VPS и проверить физические Windowsустройства/GraphicsCapture/звук/мониторы/DPI и маршрут КЗ—Корея. В сессии VPS не обновлялся. Один ранний локальный nativeпроход получил takeoverтаймаут; последующие probe и независимые LinuxCI прошли. Причина единичного таймаута не установлена и не объявляется исправленной без доказательства.
- **Следующее конкретное действие:** выполнить на VPS команды ниже, полностью закрыть старый клиент через трей, заменить portable, проверить двух пользователей: muteдо звонка, переход A→B/Home без разрыва, запуск и просмотр app/monitor со звуком, вход/выход зрителя, выход ведущего, GIF/emoji/copy и права администратора. При новой ошибке продолжить с её логом; не переписывать завершённые этапы.

```bash
cd /opt/wisp-server-v2-src
git pull --ff-only
bash server/wisp-sfu/deploy/install.sh 89.126.195.20
```

### История реализации нового ТЗ

## Этапы2–8 — реализованы (историческая запись)

- Нейтральная палитра, Onest, четыре колонки с границами. Удалены поиск участников и отдельная панель звонка. Общая нижняя панель, скроллбар у края; повторный выбор сервера не перезагружает чат. Mute/deafen доступны вне звонка и сохраняются между запусками.
- Объединены аккаунт/профиль; явный режим редактирования с preview/save/cancel/валидацией. Управление сервером: название/значок, owner/admin/member, роли, исключение с подтверждением, upload/rename/delete эмодзи/звуков с серверной проверкой прав. Миграции additive, данные сохранены.
- Выбор экрана: вкладки приложение/экран, реальные ограниченные превью, выбор рамкой, обновление источников. Звук включён при поддержке. Просмотр по действию в центре; mini/full опциональны; «В ЭФИРЕ» в roster; локальный preview только при просмотре, до10fps. События зрителей через совместимые поля screen-state.
- GIF: общий hover/reset первого кадра и lazy animator. Emoji popup +22/48px. Точное копирование текста/emoji и выделяемые уведомления. Системные эффекты×0.5, речь/демонстрация/Soundboard сохранены. Минимальная Home с конечной анимацией и двумя действиями.
- Существенные файлы: `MainWindow*.cs`, `ProfileView.cs`, `SettingsView.cs`, `ScreenSourcePicker.cs`, `ScreenViewer.cs`, `EmojiPicker.cs`, `AvatarCatalog.cs`, `SelectableChatText.cs`, `NotificationArea.cs`, `VoiceParticipantRow.cs`, `Design/*`, `Theme.xaml`, `Media/{ScreenShareService,SoundService}.cs`; Core `{Models,LocalStore,ServerBackend,VoicePreferences,StreamViewerPresence,EmojiContent}.cs`; Go `{community_management,data,store,rooms}.go`; связанные тесты.
- Исправлены integration compileошибки namespace ScreenStreamInfo и затенение Button тестовым helper. Сборка solution:0warnings/errors;93Core PASS;171WPFресурс валиден. Go real PostgreSQL/race:7PASS, nativeprobe покаSKIP до новой сборки. WindowsUI/снимки новых изменений ещё не проверены.
- **Следующее конкретное действие:** собрать обновлённый multi-client viewerprobe, повторить сборку после membershiprefresh, опубликовать checkpoint и выполнить Windows CI/снимки. Cross-message selection требует отдельной проверки/доработки. Затем portable и итоговый отчёт. Не считать новое ТЗ завершённым по результатам предыдущего Master.

### Проверка этапа9 — первый опубликованный checkpoint da9de59

- Windows CI40684614634: **93Core+29Desktop=122PASS**,RNNoise/build/package PASS. Linux CI40684614770 PASS. 17снимков получены; просмотрены основной UI/обе темы/Home/центр трансляции/профиль/настройки; слишком короткая навигация «Аккаунт и профиль» расширяется.
- Локальный real nativeprobe обновлён:6клиентов/30downlinks,2зрителя,viewjoin/leave/dedup/recovery, голос после выхода зрителя, H264/отдельноеaudio/2MB/token/takeover/rejoin PASS;Go/race полный8PASS. Первый локальный проход старой сборки дал таймаут при takeover; новая сборка и независимый LinuxCI прошли. Причина единичного таймаута не установлена, он не скрывается.
- Добавлены cross-message selection (новый `ChatTextSelection.cs`), regression точного копирования нескольких сообщений, lazyGIF/reset, выбор источников с удалением закрытого источника, cleanupпри синхронном отказе захвата. Эти последние изменения собираются локально, но требуют нового WindowsCI перед завершением.
- **Продолжить:** опубликовать follow-up, выполнить его WindowsCI, разобрать реальные ошибки/снимки (включая source picker), скачать финальный portable, обновить итоговый отчёт/PROGRESS и сохранить всё вgit.

### Follow-up fd4d420 проверен

- Windows40685705915 и Linux40685705917:PASS; cross-message selection, picker/tab/закрытый источник, GIF reset и fallbackcapture cleanup включены в прошедшие UIпроверки.122теста и portable собраны.
- Визуальная проверка выявила обрезку текста навигации настроек; ширина увеличена. Профиль позволяет менять статус. Последний review уменьшил decode первого кадра до displayразмера, включил popupmotion через его anchor и учёл deafen в previewSoundboard. Новая локальная сборка:0warnings/errors,171ресурс.
- Добавлен [отчёт нового ТЗ](docs/IMPROVEMENTS.md), README и инструкция сервера уточняют сохранение существующих данных v2 и новый доступ owner/admin/member.
- **Следующее конкретное действие:** опубликовать этот последний review, дождаться его WindowsCI, посмотреть source-picker/emoji/profile снимки, скачать финальный portable и зафиксировать итог. Последние4небольших productionправки пока не объявляются прошедшими Windows.

### Финальный review синхронизации

- `b7d649e` проверяет последние GIF/popup/deafen правки; Windows40687242374 UI/build PASS, package ещё выполняется. Предыдущие122PASS подтверждены также TRX последнего follow-up (18снимков); source-picker и настройки просмотрены.
- Найдены и исправлены два сценария интеграции: refresh-token на Home сохраняется до раннего return; проверка отозванного доступа пропускает серверы, создание которых ещё выполнялось при начале запроса. Асинхронное создание использует capturedcommunity, не текущую навигацию.
- Исходники последнего исправления: `5a9957b`. **Продолжить:** дождаться Windows/Linux CI этого commit, скачать и проверить его portable/TRX/снимки, затем обновить итог в начале PROGRESS и docs/IMPROVEMENTS, опубликовать документацию и PR. Функциональные правки после этого не планируются, если проверки не найдут регрессию.

---

# Wisp — выполнение Master Implementation Specification

Исходное ТЗ: пользовательский «WISP — MASTER IMPLEMENTATION SPECIFICATION», получен 27.10.2026; интерфейс по приложенному референсу. Рабочая ветка `codex/postgres-backend`. Исходный commit: `3bb11eff73337cdaf071e955b4204c26265ba0ff`.

## Предыдущий Master — программная часть завершена (28.10.2026)

- Проверенный commit исходников и тестов: **68b4a2a**. Windows CI **76 Core +29 Desktop =105 PASS**,0 warnings/errors; Linux CI PostgreSQL/native/race/vet PASS. Все16 снимков основных экранов получены и просмотрены.
- [Windows CI](https://github.com/daurensky/wisp/actions/runs/40649165988), [Linux CI](https://github.com/daurensky/wisp/actions/runs/40649165812), [portable](https://github.com/daurensky/wisp/actions/runs/40649165988/artifacts/12799600158), [снимки и TRX](https://github.com/daurensky/wisp/actions/runs/40649165988/artifacts/12798463431).
- Реализованы нативный редизайн с Onest/обеими темами, независимая голосовая навигация, общий выбранный shared WASAPI выход, независимый плеер, Wisp Original, семантическая доставка звуков, динамический трей и диагностика SFU. Данные/протокол v2 сохранены.
- Подробный итог, подтверждённые причины, новые сервисы и полный список изменённых файлов: [docs/MASTER_IMPLEMENTATION.md](docs/MASTER_IMPLEMENTATION.md).
- **Следующее конкретное действие:** обновить существующий VPS по командам ниже, затем полностью закрыть старый клиент через трей и заменить portable; на реальном Windows-ПК проверить музыку другого приложения + звонок + голосовое сообщение, навигацию A→B и демонстрацию. Это остаётся аппаратной проверкой, а не незавершённой автоматической реализацией. Регистрация заново не требуется.

```bash
cd /opt/wisp-server-v2-src
git pull --ff-only
bash server/wisp-sfu/deploy/install.sh 89.126.195.20
```

Не повторять завершённые этапы. При новом «Продолжай» сначала читать этот итог и git, а при регрессии продолжать с конкретного нового лога/сценария. Ниже — история, включая исправленные промежуточные падения тестов.

## Этап 1 — аудит (завершён первичный проход)

- Проверены архитектура WPF-клиента, SFU v2, PostgreSQL/SQLite, существующие проверки и исходное звуковое API.
- Подтверждено: смена сообщества вызывает `Disconnect`; состояния просматриваемого сообщества и голосового подключения смешаны. Статус `!rtc.Joined` ошибочно отображается как авария даже вне звонка.
- Подтверждено: звуки вызываются при смене темы, отправке сообщения, создании каналов и прочих запрещённых ТЗ действиях; голосовые сообщения используют системное устройство вместо выбранного выхода и теряют плеер при выгрузке строки.
- Переданные ресурсы: восемь WAV Wisp Original + README. gg sans не приложен; предусмотрен разрешённый ТЗ fallback Segoe UI. README архива используется как описание ресурсов, а не как дополнительное задание.
- Параллельно выполняются независимые изменения WASAPI/плеера, семантических звуков, дизайн-системы, трея, диагностики SFU и компонентов настроек. Интеграция MainWindow выполняется отдельно.

### Изменённые файлы этапа

- `PROGRESS.md` — этот журнал. Остальные файлы в работе и пока не объявляются завершёнными.

### Проверки

- До начала работы репозиторий чистый; исходная сборка ранее прошла 58 core + 12 desktop тестов и реальный native/Go SFU probe. Это исходные результаты, а не проверка новых изменений.
- Архив перечислен без запуска содержащихся файлов. Новый код пока не проверен.

## Первоначальный план (выполнен; историческая запись)

1. Разделить выбранное сообщество/канал и активную голосовую сессию, сохранить медиа при навигации, исправить статусы.
2. Интегрировать WASAPI, выбранный выход, независимое воспроизведение сообщений, Wisp Original и дедупликацию событий.
3. Подключить динамический трей к существующему детектору речи.
4. Переработать главный интерфейс, главную страницу, голосовую панель, настройки/профили и модальные формы в едином стиле.
5. Проверить SFU-диагностику и совместимость, собрать и выполнить доступные тесты, затем Windows CI/скриншоты/portable.
6. Опубликовать изменения в git, обновить отчёт и указать аппаратные сценарии, которые нельзя проверить в Linux.

## Начальная точка продолжения (история)

Прочитать текущее состояние git, собрать результаты независимых компонентов; в `MainWindow.cs` ввести независимый контекст активного голосового сообщества и исправить навигацию/маршрутизацию голосовых событий. Не повторять уже завершённый первичный аудит. Следующий этап фиксировать после интеграции и целевых проверок.

## Этапы 2–6 — реализация и интеграция (28.10.2026, код готов к Windows-проверке)

### Реализовано

- Независимый `voiceCommunity` сохраняет RTC/микрофон/демонстрацию при `SetCommunity` и `ShowHome`. Активный roster, звуковая панель, имена трансляций и presence берутся из контекста звонка. Статус вне звонка нейтральный, ручной выход сразу сбрасывает аварийный индикатор.
- SFU сохраняет оригинальную причину ошибки входа и формирует безопасный диагностический снимок состояния/последнего сообщения/закрытия WebSocket; несовместимые сообщения получают явную протокольную причину. Протокол v2 и данные сохраняются.
- Нейтральный shared WASAPI capture без ненужного `IAudioClient2.SetClientProperties`. Общий helper выбранного shared render-устройства для разговора, сообщений, звуков и демонстрации, без изменения master volume или параметров Windows.
- Плеер голосового сообщения отделён от виртуализированной строки: выгрузка UI не прерывает запись; выбранный выход, seek/play/pause и смена устройства сохраняются.
- Оригинальные 8 WAV включены без изменения байтов; семантическая политика отделяет свои/чужие события, начальный roster/историю и повторную доставку. Произвольные звуки UI удалены. Ограничены очередь и число одновременно проигрываемых сигналов.
- Кешируемые иконки трея 16/20/24/32, состояния вне звонка/тишина/речь/mute/deafen/авария, существующий детектор речи; без второго микрофона или render loop.
- Централизованные палитры обеих тем, Onest Regular/Medium/SemiBold/Bold, нативные главная/настройки/профиль/панель звонка, поиск участников, адаптивные колонки. Caption buttons прямоугольные, maximize всегда квадрат.

### Уточнение шрифта

По последней инструкции пользователя **Onest**, не gg sans и не Google Sans. Включены статические начертания из официального `google/fonts/ofl/onest`, полученные из OFL variable font с fontTools; лицензия в `Assets/Fonts/Onest-OFL.txt` и portable. Google Sans не входит в проект.

### Основные файлы

- `src/Wisp.Desktop/MainWindow.cs`, `MainWindow.Workspace.cs`, `SettingsView.cs`, `ProfileView.cs`, `Theme.xaml`, `ThemedWindow.cs`, `Dialogs.cs`, `ServerSetupWindow.cs`, `ThemeManager.cs`, `Design/*`, `Assets/Fonts/*`.
- `Media/CommunicationsCapture.cs`, `SharedAudioOutput.cs`, `FilePlaybackSession.cs`, `VoiceService.cs`, `ShareAudioPlayback.cs`, `SoundService.cs`, `NativeVideoCodec.cs`, `VoiceMessagePlayer.cs`, `Assets/Sounds/*`, `TrayIndicator.cs`, `VoiceTrayIcons.cs`.
- `src/Wisp.Core/SoundNotifications.cs`, `VoiceTrayState.cs`, `SfuDiagnostics.cs`, `SfuRoom.cs`; `server/wisp-sfu/main.go`, `protocol_test.go`.
- Новые регрессионные тесты Core/Desktop и обновляемый `ThemeTests.cs`.

### Пройдённые проверки именно новых изменений

- Core: **76/76 PASS**, Linux .NET 8, `master-core-tests.log` в рабочем каталоге вне репозитория.
- WPF resources: **166** ключей, нет дублей/битых ссылок, допустимые отступы/радиусы.
- Независимые проверки диагностик SFU: 2 Core + 2 Go protocol tests с race detector PASS в предыдущем рабочем окружении; повторная полная серверная проверка ещё предстоит.
- Производственный desktop-проект собирается без предупреждений; сборка всей solution пока ожидает завершения новых методов Windows UI-теста. Windows UI/настоящие WASAPI-устройства на Linux не проверены.

### Осталось и точное продолжение

1. Дождаться завершения `ThemeTests.cs` (снимки основных экранов, навигация A→B во время звонка, статусы, caption buttons) и проверки media lifetime/deafen. Не повторять написанные компоненты.
2. Собрать solution целиком, устранить конкретные регрессии; повторить core/Go/native probe по изменённым областям.
3. Опубликовать в git, запустить Windows CI, проверить тесты/скриншоты и визуально исправить обнаруженное, упаковать portable.
4. Обновить этот файл, документацию и итоговый отчёт со списком изменений/проверок/аппаратных ограничений. Не объявлять универсально устранённым ducking Bluetooth/драйвера без физического теста.

## Этап 7 — локальная проверка и подготовка Windows CI

- Полная `Wisp.sln` собирается: **0 warnings, 0 errors** (Linux cross-build WPF).
- Исправлены найденные при интеграции ошибки: старый presence при переходе в другое сообщество, звук B в звонке A, имена/вложения активного звонка при просмотре B, ложное подключение во время ручного выхода, задержка обновления списка трансляций. Компактная панель звонка обновляет roster, а не держит статический снимок.
- Windows UI-тест дополнен реальной навигацией кнопкой сообщества без Enter/Leave, сохранением состояния демонстрации, caption/maximize, новыми настройками/профилем/Home/звонком и скриншотами. Пока выполнена только сборка, не Windows-исполнение.
- Проверка аудио отдельно: **13/13 PASS** на PCM/fake renderers; 8 WAV побайтово соответствуют архиву, очередь/активные уведомления очищаются на deafen/отключение уведомлений, последующее локальное подтверждение допустимо.
- Полный native PostgreSQL/SFU probe запущен, результат будет дописан после завершения; Windows CI и визуальный просмотр — следующий шаг.
- Создан локальный git checkpoint `fe22a25` (интеграция); последующие исправления войдут в следующий commit. Последовательность пользовательского ТЗ не заменяется новым заданием.

**Продолжить:** закончить текущий native probe, опубликовать актуальное дерево в `codex/postgres-backend`, дождаться Windows CI, разобрать реальные ошибки/скриншоты. После этого обновить результаты и сохранить финальный журнал в git.

### Результат полного локального SFU-прохода

**PASS:** real PostgreSQL/API + Go race detector, семь Go-тестов включая явные protocol-close reasons; native probe шести клиентов/30 downlinks, голос+отдельный звук демонстрации+H.264, файл 2MB одновременно с медиа, обновление токена, инвайты в обе стороны, takeover и повторный вход после принудительного разрыва. Probe61.17s, полный Go-проход63.07s. `go vet` без замечаний. Это локальный native transport test, а не проверка физических Windows-устройств.

### Первый Windows CI (2b07183)

- Core76/76 PASS; Linux CI полный Go/native/race/vet PASS.
- Desktop27 PASS,2 FAIL. Не объявляются завершёнными все Windows-сценарии.
- Подтверждены ошибки тестовых условий: автономный palette-тест создавал `pack://` URI до WPF static initialization; UI-тест предполагал ActualWidth>=1200, хотя рабочий стол CI ограничивает окно меньшим размером. Исправляются инициализация и явная проверка обеих веток адаптивной компоновки; проверки навигации/голоса не удаляются.
- Дополнительно кнопка добровольного выхода сохраняется после исчерпания восстановления, чтобы снять реальный аварийный статус вручную.
- Публикация исходной реализации в GitHub: `2b07183`. Следующее действие: повторный Windows CI после этих конкретных исправлений, затем визуальный просмотр снимков.

### Второй Windows CI и визуальная проверка (6595111)

- **76 Core + 29 Desktop PASS**,0 warnings/errors; RNNoise/portable packaging PASS. Windows run40645623565, Linux run40645623428 — успешны.
- Просмотрены реальные снимки Home, профиля/GIF, настроек, навигации во время звонка. Они демонстрируют реальный компактный viewport CI; широкая раскладка дополнительно проверяется на порогах1360/1100/1040 без изменения настроек Windows.
- На снимке настроек найдена настоящая визуальная ошибка: ComboBox отображал record `AudioDeviceChoice { ... }`, потому что шаблон терял `SelectionBoxItemTemplate`. Исправлен общий шаблон; добавлены assertions на дружелюбные названия устройств вместо record.
- Финальные UI-ограничения: прозрачный контейнер голосовой строки даже при клавиатурном выборе; на Home отключены действия без выбранного сообщества, а создание канала недоступно не-владельцу. Глобальные создание/вступление сохранены.
- Следующее действие: проверить сборку этих последних изменений, опубликовать их и получить последний Windows PASS; затем обновить итоговый журнал/ссылку на portable. Не повторять завершённые Core/native-аудиты без нового сбоя.

### Третий Windows CI (ef23cd0)

- Core76 PASS, Linux/native PASS; Desktop28 PASS,1 FAIL. Новые UI-проверки выбора голосовой строки, Home и понятных названий устройств прошли.
- Единственный сбой — timeout2s ожидания начальной инициализации fake notification renderer в тесте очистки уведомлений, до проверки PCM/очереди. Тот же сценарий проходил в предыдущих Windows/изолированных прогонах. Увеличено только время ожидания запуска до10s; ошибка фонового renderer теперь немедленно передаётся в тест вместо маскировки timeout. Проверки тишины, PCM, deafen и независимости потоков сохранены.
- Следующий шаг: финальный повтор Windows после стабилизации этого ожидания, затем скачать/проверить portable и обновить итоговые ссылки. Новых продуктовых изменений не требуется.

### Четвёртый CI (aa2b410) — успешен

- Windows105/105:76 Core +29 Desktop PASS, сборка0 warnings/errors, RNNoise и portable packaging PASS. Run40647074433.
- Linux полный native/Go/race/vet PASS, run40647074332. Изолированные13 PCM/audio tests повторно PASS (106ms).
- Снимки подтверждают понятные имена устройств, Onest, контраст обеих тем, профиль и настройки. Последний визуальный штрих — явный TextSecondary для стрелки ComboBox в тёмной теме; функциональность и протокол не меняются.
- Следующее действие: проверить сборку последней визуальной правки, получить окончательный portable; записать финальные результаты/ссылки. Аппаратные проверки остаются отдельно.

### Пятый CI (2a33628) — успешен и архив проверен

- Windows76+29 PASS,0 warnings/errors; Linux/native/race/vet PASS.
- Проверен portable:522 файла, Onest-OFL, RNNoise и все8 WAV совпадают с исходниками; SHA256 внутреннего ZIP `024a849b158c19a7b5b7a471a7f0bfd7e34846dd68a13d97bd7f572496c71e7e`.
- Завершается только набор доказательств UI: к уже проверенным13 снимкам добавлены начальная настройка сервера, серверный вход и регистрация. Тест проверяет переключение режима и отказ некорректного сертификатного отпечатка, не делает сетевых запросов и не записывает ложную конфигурацию.
- Следующий шаг: Windows-исполнение этих дополнительных startup-снимков, затем окончательный отчёт/portable. Продуктовый код больше не изменяется.

### Дополнительные startup-снимки

Все3 дополнительных снимка созданы; вспомогательный тест отказа настройки остановился на несовпадении имени доступности кнопки (tooltip вместо текста). Поиск исправлен на единственную default-кнопку, работающую по Enter. Продуктовый код не изменён; проверка отказа неверного SHA-256 сохранена. Следующее действие — повтор только доступного CI и финализация журнала.

## Финальная фиксация и ограничения

- Все завершённые этапы и результаты сохранены в git ветки `codex/postgres-backend`; PR8 остаётся draft для проверки на реальном ПК, merge/deploy автоматически не выполнялись.
- Проверен окончательный архив:522файла,90 015 636байт, SHA256 `36a56e27498443cd1bae9498ad4cfd4ecc5bcf9f6100e19ef8eee51248a5e5bf`. Содержит RNNoise, Onest-OFL и8 побайтово оригинальных WAV.
- Дополнительные startup-сценарии PASS: серверный вход/регистрация/переключение режима/отказ неверного SHA-256;16скриншотов, включая все основные стартовые и рабочие экраны. Снимки CI содержат тестовые профили/красный GIF, не пользовательские данные. Реальный viewport CI компактный; широкие ветки отдельно проверены при1360/1100/1040.
- Аппаратно не проверены: конкретные микрофоны/наушники/Bluetooth-профили и ducking их драйверов, физические мониторы/захват окон/разрешения, DPI100–200% и реальный маршрут Казахтелеком↔Астана↔Корея. Глобальные настройки Windows не меняются; универсальное устранение внешнего ducking не заявляется.
- Нет известных незавершённых программных пунктов исходного ТЗ. После аппаратного теста при сбое использовать `%LOCALAPPDATA%\Wisp\logs\diagnostics.log` и `journalctl -u wisp-sfu`, сохранить конкретный сценарий в новом этапе этого файла.
