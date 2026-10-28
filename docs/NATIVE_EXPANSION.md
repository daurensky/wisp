# Нативные компоненты Wisp — 29.10.2026

[Исходное ТЗ](NATIVE_EXPANSION_SPECIFICATION.md) дополняет предыдущие улучшения. Последнее уточнение пользователя: **Onest + Grayscale**, существующие серверные эмодзи отправляет любой участник; добавление/управление остаются owner/admin.

## Реализовано

| Область | Поведение |
|---|---|
| Текст | Нативные WPF Grayscale/Display, Onest, layout rounding и pixel snapping; без BlurEffect и отключения аппаратного ускорения. Стили охватывают popup/context menu/tooltip. |
| Серверы/каналы | Круглые значки, фирменный fallback, активная левая полоска; шестерёнка сервера только manager, отдельные plus текст/голос с заданным типом, rename/delete каналов через авторизованный API. Боковая панель 288px; hover-gear не подключает к голосу. |
| Настройки/профиль | Общая почти полнооконная SettingsShell. Только аккаунт/профиль, внешний вид, голос/звук, уведомления. Server settings используют ту же оболочку. Просмотр профиля без заголовка/крестика, закрывается Escape/снаружи, баннер16:9 и увеличенный аватар. |
| Изображения/GIF | Общий нативный редактор avatar/servericon1:1/banner16:9, drag/zoom/reset, preview, GIF timeline/coverFrame. Все кадры компонуются с disposal/transparency и обрезаются; timing/loop сохраняются. Ограниченные размеры/память/выход до4МиБ, без скрытого превращения анимации в статичную картинку. Исходник не изменяется, принятый tempdraft удаляется после сохранения/отмены. |
| GIF playback | В профиле Always; аватары чата проигрываются при фокусе и возобновляются после возврата. В остальных местах hover/reset на выбранную обложку. Общий ограниченный cache байтов/замороженных cover, ленивые отдельные animators; повреждённый GIF получает статичный fallback. |
| Composer/media | Динамический placeholder, спокойный focus, пустая/whitespace отправка disabled. Record слева от send. AttachmentCard для image/video/файлов, overlaydownload, нативный просмотр video с pause/seek/volume, deafen. |
| Голосовые сообщения | Реальная waveform PCM, длительность/seek/progress/pause. Liveволна заменяет ввод при записи; stop/send/cancel. В активном звонке запись получает уже захватываемый PCM без второго микрофона; writer/queue ограничены, максимум60с, подписки/ресурсы освобождаются. |
| Нижняя панель | Две группы: voiceactions сверху, avatar+username/mic/deafen/settings снизу. Область имени/аватара открывает профиль. Нейтральный phone-hangup; persistentmute/deafen работают вне звонка. |
| Soundboard | Native Popup над кнопкой, Escape/снаружи, без shade; загрузка/error/empty состояния. Upload/edit/previewgain сохраняется на сервере и применяется к PCM, включая уже закэшированные звуки. Индивидуальный voicegain в исправленном contextmenu сохранён. |
| Демонстрация | Расширенный picker с адаптивными16:9превью, tabприложение/экран; resolution/FPS/bitrate/Запустить одной строкой. Настройки идут в реальный capture/encoder. Floatingcontrols снизу, независимый streamgain/mute/restore. |
| Mini/зрители | Собственное mini появляется автоматически; overlay выше внутренних модалок, drag/snap справа сверху/снизу, реальное aspect. Собственный красный X завершает источник; чужой — только просмотр. Аватары реальных зрителей/речевые кольца; host-authoritative paged snapshots по прежнему512-байтному событию с проверкой отправителя/session/roster. |
| Запуск/состояния | Home при обычном запуске; авторизация/настройки/данные сохраняются. Ручной ASCIIсилуэт исходного graphiclogo попадает в `%LOCALAPPDATA%/Wisp/logs/diagnostics.log`. Навигация/трей/SFU/reconnect/независимые аудиопотоки сохранены. Удалённый администратором голосовой канал не создаёт бесконечный rejoin. |

Ошибка emoji403 была вызвана повторным PUT существующего emoji перед отправкой. Клиент проверяет immutable bytes/hash через доступный участнику GET и не выполняет upload. API по-прежнему запрещает обычному участнику загрузку нового emoji или управление сервером. Удалён также повторный upload того же файла в цепочке SendMessage.

## Проверка текущего этапа

Локальная solution cross-build: **0 warnings/errors**, Core **96PASS**, WPF **188** именованных ресурсов без brokenreferences/duplicatekeys; проверяются и новые корневые XAMLкомпоненты.

Реальный PostgreSQL/API + Go race: **10PASS**, `go vet` PASS. Nativeprobe6клиентов/30downlinks: Opusmic+отдельное shareaudio+H264,2МБфайл одновременно с медиа, token/invites/takeover/rejoin. В probe отдельно проверены ownerupload → memberreuse → messageaccepted и membernewupload403, а в APIтестах — managerGET, права каналов, удаление активногоvoiceканала, cover/gainmetadata.

Независимые Linuxпроверки: PCM/waveforms/60s/subscriptionretry/gain **6/6PASS**; GIFcodec — Pillow **36файлов/180кадров**, disposal1/2/3, отдельная interlacefixture, transparency/delays/loop. WPFeditor/controls дополнены Windowsтестами; до завершения нового WindowsCI они не объявляются прошедшими.

## Обновление

Не требуется новая регистрация для существующей PostgreSQL v2. Обновите backend и portable; сертификат/pin, профили, сообщения и вложения сохраняются. Новые поля additive, локальные каналы soft-delete сохраняют старую историю.

```bash
cd /opt/wisp-server-v2-src
git pull --ff-only
bash server/wisp-sfu/deploy/install.sh 89.126.195.20
```

Старый клиент полностью закройте через трей перед заменой portable. В сессии production VPS не менялся. Порядок действий и продолжение — [PROGRESS.md](../PROGRESS.md).

Физические Windowsмикрофоны/драйверы/GPU/GraphicsCaptureразрешения/несколько мониторов/DPI и маршрут Казахстан—Корея требуют проверки на устройствах. CI использует явно отмеченные fixtureпревью и реальную программную обработку; они не выдаются за capture физического монитора.
