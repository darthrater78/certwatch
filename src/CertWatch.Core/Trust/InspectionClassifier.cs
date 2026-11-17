using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using CertWatch.Core.Tls;

namespace CertWatch.Core.Trust;

public enum Verdict
{
    /// <summary>The chain ends at a public root: the site's own certificate.</summary>
    NotInspected,
    /// <summary>A private root this machine trusts replaced the certificate: a proxy is decrypting.</summary>
    Inspected,
    /// <summary>A private or self-signed chain this machine does not trust.</summary>
    Untrusted,
    /// <summary>No certificate could be obtained.</summary>
    Error,
}

public sealed record Classification(
    Verdict Verdict,
    string Reason,
    string? Vendor,
    string? IssuerName,
    string? RootName,
    bool RootIsPublic,
    bool MutualTls);

public sealed class InspectionClassifier(IPublicRootSet publicRoots)
{
    public Classification Classify(ProbeResult probe)
    {
        if (!probe.Succeeded)
            return new(Verdict.Error, probe.Error ?? "No certificate received", null, null, null, false, probe.ClientCertificateRequested);

        var issuer = probe.Leaf!.GetNameInfo(X509NameType.SimpleName, forIssuer: true);
        var top = probe.Top!;
        var topIsSelfSigned = top.SubjectName.RawData.AsSpan().SequenceEqual(top.IssuerName.RawData);
        var rootName = topIsSelfSigned
            ? top.GetNameInfo(X509NameType.SimpleName, false)
            : top.GetNameInfo(X509NameType.SimpleName, forIssuer: true);
        var mtls = probe.ClientCertificateRequested;

        var publicRoot = publicRoots.FindPublicRootName(probe.Chain);
        if (publicRoot is not null)
        {
            var reason = probe.PolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)
                ? "Public certificate, but it doesn't match this site's name"
                : "Chain ends at a public root: this is the site's own certificate";
            return new(Verdict.NotInspected, reason, null, issuer, publicRoot, true, mtls);
        }

        var vendor = InspectionVendors.Identify(probe.Chain);
        var chainTrustedHere = !probe.PolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors);

        // Backstop for public roots missing from the bundle (or reached through an unusual path):
        // publicly trusted site certificates carry Certificate Transparency proofs (SCTs) signed by
        // public logs. A certificate minted on the fly by an inspection proxy can't have them.
        if (vendor is null && HasEmbeddedScts(probe.Leaf!))
            return new(Verdict.NotInspected, "Certificate is publicly logged (Certificate Transparency): this is the site's own certificate",
                null, issuer, rootName, true, mtls);

        if (chainTrustedHere || vendor is not null)
        {
            var who = vendor ?? "an unidentified proxy";
            var reason = chainTrustedHere
                ? $"Replaced by {who}: private root trusted by this machine"
                : $"Replaced by {who}, but this machine doesn't trust its root";
            return new(Verdict.Inspected, reason, vendor, issuer, rootName, false, mtls);
        }

        var untrustedReason = probe.Chain.Count == 1 && topIsSelfSigned
            ? "Self-signed certificate"
            : "Private chain this machine doesn't trust";
        return new(Verdict.Untrusted, untrustedReason, null, issuer, rootName, false, mtls);
    }

    private const string SctListOid = "1.3.6.1.4.1.11129.2.4.2";

    internal static bool HasEmbeddedScts(X509Certificate2 leaf) =>
        leaf.Extensions.Cast<X509Extension>().Any(e => e.Oid?.Value == SctListOid && e.RawData.Length > 8);
}
