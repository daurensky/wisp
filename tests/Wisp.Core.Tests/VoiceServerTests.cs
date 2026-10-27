using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Wisp.Core;
using Xunit;
namespace Wisp.Core.Tests;
public sealed class VoiceServerTests
{
    [Theory]
    [InlineData("ws://89.126.195.20:8443/rtc")]
    [InlineData("wss://89.126.195.20:8443/rtc?token=bad")]
    [InlineData("wss://user:password@host/rtc")]
    [InlineData("wss://host/other")]
    public void RejectsUnsafeSignalingEndpoints(string url) => Assert.Throws<InvalidDataException>(() => new VoiceServerSettings(true,url,new string('a',64)).Endpoint());
    [Fact]
    public void RequiresExactUnexpiredPinnedCertificateEvenForSelfSignedServer()
    {
        using var key = ECDsa.Create();
        var req = new CertificateRequest("CN=fixture",key,HashAlgorithmName.SHA256);
        using var valid = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddDays(1));
        using var expired = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2),DateTimeOffset.UtcNow.AddDays(-1));
        var settings=new VoiceServerSettings(true,CertificateSha256:valid.GetCertHashString(HashAlgorithmName.SHA256));
        Assert.True(settings.ValidateCertificate(valid,SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(settings.ValidateCertificate(expired,SslPolicyErrors.None));
        Assert.False(settings.ValidateCertificate(null,SslPolicyErrors.None));
        Assert.False((settings with {CertificateSha256=new string('b',64)}).ValidateCertificate(valid,SslPolicyErrors.None));
        Assert.False((settings with {CertificateSha256=new string('z',64)}).ValidateCertificate(valid,SslPolicyErrors.None));
        Assert.Throws<InvalidDataException>(()=>(settings with {CertificateSha256=""}).Endpoint());
        Assert.False((settings with {CertificateSha256=expired.GetCertHashString(HashAlgorithmName.SHA256)}).ValidateCertificate(expired,SslPolicyErrors.None));
    }
}
