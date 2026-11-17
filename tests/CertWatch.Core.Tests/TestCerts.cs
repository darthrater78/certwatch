using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CertWatch.Core.Tests;

/// <summary>Builds throwaway root → intermediate → leaf chains for tests.</summary>
internal static class TestCerts
{
    public static X509Certificate2 Root(string cn)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN=\"{cn}\"", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
    }

    /// <summary>A copy of <paramref name="root"/> (same subject and key) signed by another CA.</summary>
    public static X509Certificate2 CrossSign(X509Certificate2 root, X509Certificate2 otherCa)
    {
        using var key = root.GetECDsaPrivateKey()!;
        var req = new CertificateRequest(root.SubjectName, key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var issued = req.Create(otherCa, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), RandomNumberGenerator.GetBytes(12));
        return issued.CopyWithPrivateKey(key);
    }

    public static X509Certificate2 Issue(X509Certificate2 issuer, string cn, bool isCa, string[]? dnsNames = null, bool withSct = false)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN=\"{cn}\"", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(isCa, false, 0, true));
        if (dnsNames is not null)
        {
            var san = new SubjectAlternativeNameBuilder();
            foreach (var n in dnsNames) san.AddDnsName(n);
            req.CertificateExtensions.Add(san.Build());
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        }
        if (withSct)
        {
            // Placeholder SCT list (OCTET STRING); only its presence matters to CertWatch.
            req.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.11129.2.4.2", [0x04, 0x10, .. new byte[16]], false));
        }
        var serial = RandomNumberGenerator.GetBytes(12);
        using var issued = req.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90), serial);
        return issued.CopyWithPrivateKey(key);
    }
}
