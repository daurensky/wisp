# Wisp Original

Восемь оригинальных процедурно синтезированных UI-звуков. Мягкие стеклянные тоны, короткий воздушный хвост, без речи и сторонних семплов.

Формат: WAV / linear PCM signed 16-bit / 48 000 Hz / mono.
Mono подходит для интерфейсных уведомлений и не смещает внимание между ушами.

Event mapping (длительность):
- voice_join.wav — участник вошёл в текущий голосовой канал (0.58 s).
- voice_leave.wav — участник вышел из текущего голосового канала (0.58 s).
- stream_start.wav — запущена демонстрация в текущем голосовом канале (0.78 s).
- incoming_message.wav — новое входящее сообщение согласно настройкам уведомлений (0.38 s).
- mute.wav — собственный микрофон выключен (0.31 s).
- unmute.wav — собственный микрофон включён (0.34 s).
- deafen.wav — собственный звук выключен (0.43 s).
- undeafen.wav — собственный звук включён (0.48 s).

В переключениях mute/deafen звучит только событие локального пользователя. Если deafen автоматически меняет mute, воспроизводите только deafen/undeafen, без второго звука. Срабатывайте на подтверждённое изменение состояния, а не на повторный рендер, смену темы, сервера или восстановление списка участников. При входе не проигрывайте join для каждого уже присутствующего участника. Не дублируйте локальное действие повторным событием от сервера.

## C# WPF

Пример ниже рассчитан на современный .NET WPF для Windows. Добавьте файлы в Assets/Sounds с Build Action = Content и Copy to Output Directory = Copy if newer.
System.Media.SoundPlayer воспроизводит эти PCM WAV асинхронно через системное устройство вывода. Класс храните на уровне приложения; вызовите Dispose при завершении.

    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Media;

    public sealed class WispSounds : IDisposable
    {
        private readonly Dictionary<string, SoundPlayer> players = new();
        public WispSounds()
        {
            string[] events = { "voice_join", "voice_leave", "stream_start",
                "incoming_message", "mute", "unmute", "deafen", "undeafen" };
            foreach (var name in events)
            {
                var player = new SoundPlayer(Path.Combine(AppContext.BaseDirectory,
                    "Assets", "Sounds", name + ".wav"));
                player.Load();
                players.Add(name, player);
            }
        }
        public void Play(string eventName)
        {
            if (players.TryGetValue(eventName, out var player)) player.Play();
        }
        public void Dispose()
        {
            foreach (var player in players.Values) player.Dispose();
        }
    }

Пример вызова: sounds.Play("voice_join");
SoundPlayer — простой вариант без отдельного регулятора громкости и гарантированного микширования нескольких уведомлений. Для этих возможностей подключите WAV к существующему аудиомикшеру приложения. Правило deafen должно определять, воспроизводятся ли локальные уведомления при отключённом звуке; SoundPlayer сам о состоянии приложения не знает.

Можно интегрировать, изменять и распространять эти созданные для проекта аудиофайлы вместе с приложением, включая коммерческое использование; указание авторства не требуется. Это не обещание исключительных прав на короткие звуковые мотивы.
