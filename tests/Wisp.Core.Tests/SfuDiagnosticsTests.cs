using System.Net.WebSockets;
using Wisp.Core;
using Xunit;
namespace Wisp.Core.Tests;

public sealed class SfuDiagnosticsTests
{
    [Fact]
    public void ReportsProtocolAndAccessFailuresInsteadOfGenericNetworkFailure()
    {
        Assert.Contains("доступ",SfuProtocol.DescribeClose(WebSocketCloseStatus.PolicyViolation,"Account or community access denied"));
        Assert.Contains("ограничения",SfuProtocol.DescribeClose(WebSocketCloseStatus.PolicyViolation,"server full"));
        Assert.Contains("версии 2",SfuProtocol.DescribeClose(WebSocketCloseStatus.ProtocolError,"unsupported protocol message"));
        var error=Assert.Throws<SfuProtocolException>(()=>SfuProtocol.RequireKnownMessage("future-message"));
        Assert.Contains("future-message",error.Message);
        Assert.True(SfuProtocol.IsKnownMessage("roster"));
    }
    [Fact]
    public void DiagnosticsRetainContextAndExceptionButNeverTokensOrSdp()
    {
        var token=new string('x',43);
        var context=new SfuDiagnosticContext("negotiating","answer","community","voice","CloseReceived",WebSocketCloseStatus.PolicyViolation,"token="+token,ServerConnectionState.Connecting,"ice-failed");
        var source=new InvalidDataException("bad reply\na=ice-pwd:private-ice-password\nrefreshToken="+token+"\nAuthorization: Bearer jwt.private.secret\npassword=\"a secret with spaces\"");
        var error=new SfuDiagnosticException("Ошибка сигнала",context,source);
        var log=error.ToString();
        Assert.Contains("InvalidDataException",log);Assert.Contains("last-message=answer",log);
        Assert.Contains("channel=voice",log);Assert.Contains("close=1008",log);
        Assert.DoesNotContain(token,log);Assert.DoesNotContain("private-ice-password",log);Assert.DoesNotContain("jwt.private.secret",log);
        Assert.DoesNotContain("a secret with spaces",log);
        Assert.Throws<SfuProtocolException>(()=>SfuProtocol.RequireKnownMessage(token));
        Assert.DoesNotContain(token,Assert.Throws<SfuProtocolException>(()=>SfuProtocol.RequireKnownMessage(token)).Message);
    }
}
