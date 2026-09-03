using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace mk8.email.Application.Tests;

internal static class TestCertificateFactory
{
    public static string Create(string directory, string hostName = "mail.mk8n.com")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={hostName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(hostName);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            false));

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Combine(directory, $"{hostName}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12));
        return path;
    }
}
