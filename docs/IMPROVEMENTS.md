# Wisp — UI/UX-улучшения, 28.10.2026

Исходное [ТЗ пользователя](IMPROVEMENT_SPECIFICATION.md), ветка `codex/postgres-backend`. Стек WPF/.NET8 + Go/Pion + PostgreSQL сохранён. Onest встроен локально. Старые данные и протокол2 сохраняются.

## Реализовано

| Область | Результат |
| --- | --- |
| Дизайн | Нейтральные графитовые поверхности обеих тем, общие токены, тонкие границы, читаемые состояния и клавиатурный focus. |
| Layout | Четыре области, дополнительная колонка звонка и поиск участников удалены. Скроллбар у края, сообщения имеют внутренние отступы. |
| Навигация | Повторное нажатие активного сервера ничего не перезагружает. Просмотр других серверов/Home сохраняет голос и демонстрацию. |
| Управление | Аватар/имя, микрофон, deafen, демонстрация, просмотр, Soundboard и настройки объединены внизу. Mute/deafen сохраняются до/после звонка и между запусками. |
| Профиль | Общий раздел аккаунта/профиля; баннер с перекрывающим аватаром. Явный edit, карандаши только в edit, preview/save/cancel, проверка формата/размера, статус. |
| GIF | Общий hover-компонент, первый кадр в покое, reset при уходе, без повторной загрузки сети; речь по-прежнему выделяет кольцо аватара. |
| Home | Бренд Wisp с конечной анимацией и двумя основными действиями, без слоганов и лишних карточек. |
| Звуки | Только встроенные эффекты дополнительно умножаются на0.5. Речь, звук демонстрации и пользовательский Soundboard сохраняют отдельную громкость. Slider уведомлений удалён, enabled остаётся. |
| Экран | Приложения/мониторы во вкладках, реальные ограниченные миниатюры, выбранная рамка, обновление/пустые состояния. Звук включается при поддержке; рамка использует прежний default с запросом разрешения ОС. |
| Просмотр | По клику «В ЭФИРЕ»/кнопке просмотра, центр вместо чата, боковые области сохраняются; mini/full только по действию. Локальный preview до10fps, без второго кодировщика. Остановка/недоступность видны. |
| Зрители | Совместимые дополнительные поля screen-state сообщают намерение просмотра. Только ведущий слышит вход/выход, повторы/recovery-базисы тихие. Выход зрителя не останавливает источник или голос. |
| Сервер | Контекстное меню, название/значок с preview, роли owner/admin/member, подтверждённое исключение, upload/rename/delete эмодзи/звуков. Backend проверяет права, отозванный доступ завершает сеанс. Баны не добавлялись: исходная модель их не поддерживает. |
| Сообщения | Native emoji-popup у кнопки, клики вне/Escape, unicode/custom22/48px с отдельной проверяемой классификацией. Копирование сохраняет настоящие переносы/пробелы/custom tokens, без служебного конца абзаца; можно выделять несколько реализованных строк сообщений. Уведомления также выделяются. |

## Проверка

Первый checkpoint `da9de59`: [Windows](https://github.com/daurensky/wisp/actions/runs/40684614634), [Linux](https://github.com/daurensky/wisp/actions/runs/40684614770) PASS.93Core+29Desktop=122PASS,17снимков. Follow-up `fd4d420` с cross-message selection и picker/GIF regression: [Windows](https://github.com/daurensky/wisp/actions/runs/40685705915), [Linux](https://github.com/daurensky/wisp/actions/runs/40685705917) PASS. Последние небольшие исправления preview/motion/deafen проходят финальный CI; актуальные ссылки будут добавлены в PROGRESS.

Локально: solution0warnings/errors,171ресурс WPF,93Core PASS; реальный PostgreSQL/API,8Go/race tests и go vet. Nativeprobe:6клиентов/30downlinks, отдельные Opus mic/shareaudio/H264,2MB вложение во время медиа,2зрителя и дедупликация событий, mute/deafen, обновление токена, инвайты в обе стороны, takeover и rejoin. Один старый локальный проход получил таймаут takeover; обновлённый probe и два независимых LinuxCI прошли. Точная причина единичного таймаута не установлена.

## Ограничения проверки

Физические драйверы/микрофоны/Bluetooth, реальные разрешения захвата, защищённые окна приложений, несколько мониторов, DPI и маршрут Казахстан—Корея требуют проверки на Windows-ПК. Превью защищённого окна может быть недоступно; источник остаётся доступным для Graphics Capture. Звук приложения требует поддерживаемой версии Windows; обязательные индикаторы ОС не обходятся. CI проверяет picker на контролируемых изображениях, а не выдаёт их за снимки реального монитора.

Cross-message selection действует на реализованные виртуализированные строки; история остаётся виртуализированной. В фоне нет постоянной анимации Home или дополнительного захвата микрофона. VPS в этой сессии не обновлялся — команды приведены в SERVER.md.

## Существенно изменённые файлы

```text
PROGRESS.md
docs/IMPROVEMENT_SPECIFICATION.md
server/wisp-sfu/community_management.go
server/wisp-sfu/community_management_test.go
server/wisp-sfu/data.go
server/wisp-sfu/rooms.go
server/wisp-sfu/store.go
src/Wisp.Core/EmojiContent.cs
src/Wisp.Core/LocalStore.cs
src/Wisp.Core/Models.cs
src/Wisp.Core/ServerBackend.cs
src/Wisp.Core/StreamViewerPresence.cs
src/Wisp.Core/VoicePreferences.cs
src/Wisp.Desktop/AvatarCatalog.cs
src/Wisp.Desktop/ChatTextSelection.cs
src/Wisp.Desktop/Design/GifPlayback.cs
src/Wisp.Desktop/Design/Palette.Dark.xaml
src/Wisp.Desktop/Design/Palette.Light.xaml
src/Wisp.Desktop/Design/Tokens.xaml
src/Wisp.Desktop/Design/Ui.cs
src/Wisp.Desktop/EmojiPicker.cs
src/Wisp.Desktop/MainWindow.CommunitySettings.cs
src/Wisp.Desktop/MainWindow.Profile.cs
src/Wisp.Desktop/MainWindow.Streams.cs
src/Wisp.Desktop/MainWindow.Workspace.cs
src/Wisp.Desktop/MainWindow.cs
src/Wisp.Desktop/Media/ScreenShareService.cs
src/Wisp.Desktop/Media/SoundService.cs
src/Wisp.Desktop/NotificationArea.cs
src/Wisp.Desktop/ProfileView.cs
src/Wisp.Desktop/ScreenSourcePicker.cs
src/Wisp.Desktop/ScreenViewer.cs
src/Wisp.Desktop/SelectableChatText.cs
src/Wisp.Desktop/SettingsView.cs
src/Wisp.Desktop/Theme.xaml
src/Wisp.Desktop/VoiceParticipantRow.cs
tests/Wisp.Core.Tests/EmojiContentTests.cs
tests/Wisp.Core.Tests/VoicePreferencesTests.cs
tests/Wisp.Desktop.Tests/DesignSystemTests.cs
tests/Wisp.Desktop.Tests/ThemeTests.cs
tools/Wisp.Sfu.Probe/Program.cs
src/Wisp.Desktop/Design/Motion.cs
README.md
docs/SERVER.md
docs/IMPROVEMENTS.md
```
