using System.Net.WebSockets;
using System.Text.RegularExpressions;

namespace Wisp.Core;

/// <summary>Protocol metadata only. SDP, ICE credentials and authentication payloads are never captured.</summary>
public sealed record SfuDiagnosticContext(string Phase, string LastMessageType, string? CommunityId,
    string? ChannelId, string WebSocketState, WebSocketCloseStatus? CloseStatus, string? CloseDescription,
    ServerConnectionState ConnectionState, string? PeerState);

public sealed class SfuDiagnosticException : IOException
{
    public SfuDiagnosticContext Context { get; }
    public string OriginalExceptionType { get; }
    public string OriginalMessage { get; }
    public string? OriginalStackTrace { get; }
    public SfuDiagnosticException(string message, SfuDiagnosticContext context, Exception? original = null)
        : base(SfuProtocol.Sanitize(message))
    {
        Context = context with
        {
            Phase=SfuProtocol.SafeLabel(context.Phase), LastMessageType=SfuProtocol.SafeLabel(context.LastMessageType),
            CommunityId=SfuProtocol.Sanitize(context.CommunityId), ChannelId=SfuProtocol.Sanitize(context.ChannelId),
            CloseDescription=SfuProtocol.Sanitize(context.CloseDescription), PeerState=SfuProtocol.SafeLabel(context.PeerState)
        };
        OriginalExceptionType=original?.GetType().FullName??typeof(IOException).FullName!;
        OriginalMessage=SfuProtocol.Sanitize(original?.Message)??"";
        OriginalStackTrace=SfuProtocol.Sanitize(original?.StackTrace);
    }
    public override string ToString() => base.ToString()+Environment.NewLine+
        $"SFU phase={Context.Phase}; last-message={Context.LastMessageType}; community={Context.CommunityId}; channel={Context.ChannelId}; connection={Context.ConnectionState}; peer={Context.PeerState}; websocket={Context.WebSocketState}; close={(int?)Context.CloseStatus} {Context.CloseStatus}; close-description={Context.CloseDescription}"+
        Environment.NewLine+$"Original exception: {OriginalExceptionType}: {OriginalMessage}"+
        (OriginalStackTrace is null?"":Environment.NewLine+OriginalStackTrace);
}

public sealed class SfuProtocolException(string message) : IOException(message);

public static class SfuProtocol
{
    public static bool IsKnownMessage(string? type) => type is "ready" or "roster" or "answer" or "offer" or "remove" or "report";
    public static string SafeLabel(string? label) => label is not null && Regex.IsMatch(label,"^[a-z][a-z0-9-]{0,31}$")?label:"unknown";
    public static void RequireKnownMessage(string? type)
    {
        if(!IsKnownMessage(type))throw new SfuProtocolException("Несовместимый протокол SFU: неизвестное сообщение «"+SafeLabel(type)+"». Обнови клиент и сервер.");
    }
    public static string DescribeClose(WebSocketCloseStatus? status,string? description) => (status,description) switch
    {
        (WebSocketCloseStatus.PolicyViolation,"Account or community access denied") => "SFU отказал в доступе: сессия аккаунта истекла либо нет доступа к голосовому каналу.",
        (WebSocketCloseStatus.PolicyViolation,"server full") => "SFU достиг установленного владельцем ограничения подключений.",
        (WebSocketCloseStatus.ProtocolError,_) => "Клиент и SFU не согласовали протокол версии 2. Обнови клиент и сервер.",
        (WebSocketCloseStatus.PolicyViolation,_) => "SFU отклонил подключение по правилам доступа.",
        _ => "SFU закрыл соединение (код "+(status is null?"не указан":((int)status).ToString())+"). Будет выполнено повторное подключение."
    };
    public static string? Sanitize(string? text)
    {
        if(text is null)return null;
        text=text.Length>16000?text[..16000]:text;
        // Never retain an SDP line, even if a dependency includes it in an error.
        text=Regex.Replace(text,@"(?m)^\s*[vosiuepcbtrzkam]=[^\r\n]*","[SDP redacted]");
        text=Regex.Replace(text,@"(?i)\bBearer\s+[A-Za-z0-9_.-]+","Bearer [redacted]");
        text=Regex.Replace(text,@"(?im)(\b(?:access[_-]?token|refresh[_-]?token|token|authorization|password|ice-pwd|ice-ufrag)\b[\""']?\s*[:=]\s*)[^\r\n]*","$1[redacted]");
        return Regex.Replace(text,@"(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{43}(?![A-Za-z0-9_-])","[token redacted]");
    }
}
