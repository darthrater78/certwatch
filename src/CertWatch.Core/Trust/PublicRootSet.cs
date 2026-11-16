using System.Security.Cryptography.X509Certificates;

namespace CertWatch.Core.Trust;

public interface IPublicRootSet
{
    /// <summary>True when the chain ends at (or is issued by) a root in the public CA program.</summary>
    bool ChainsToPublicRoot(IReadOnlyList<X509Certificate2> chain);
    /// <summary>The public root's name the chain ends at, if any.</summary>
    string? FindPublicRootName(IReadOnlyList<X509Certificate2> chain);
}

/// <summary>
/// Public roots from the Mozilla CA bundle. Used instead of the machine's trust store on purpose:
/// an inspection root installed by enterprise policy is trusted by the machine but is not public,
/// and that difference is exactly what CertWatch reports.
/// Roots are matched by public key (SPKI), not by exact certificate: a public root is often also
/// served as a cross-signed copy (same key, different issuer), e.g. GTS Root R4 cross-signed by the
/// retired "GlobalSign Root CA", and Windows may build the chain through that copy.
/// </summary>
public sealed class PublicRootSet : IPublicRootSet
{
    private readonly Dictionary<string, string> _nameBySpki = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _nameBySubject = new(StringComparer.Ordinal);

    public PublicRootSet(IEnumerable<X509Certificate2> roots)
    {
        foreach (var root in roots)
        {
            var name = root.GetNameInfo(X509NameType.SimpleName, false);
            _nameBySpki.TryAdd(SpkiHash(root), name);
            _nameBySubject.TryAdd(root.SubjectName.Name, name);
        }
    }

    public int Count => _nameBySpki.Count;

    public static string SpkiHash(X509Certificate2 cert) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));

    public static PublicRootSet LoadEmbedded()
    {
        using var stream = typeof(PublicRootSet).Assembly.GetManifestResourceStream("CertWatch.Core.public-roots.pem")
            ?? throw new InvalidOperationException("Embedded public root bundle missing");
        using var reader = new StreamReader(stream);
        var collection = new X509Certificate2Collection();
        collection.ImportFromPem(reader.ReadToEnd());
        return new PublicRootSet(collection);
    }

    public bool ChainsToPublicRoot(IReadOnlyList<X509Certificate2> chain) => FindPublicRootName(chain) is not null;

    public string? FindPublicRootName(IReadOnlyList<X509Certificate2> chain)
    {
        // Skip the leaf: a site certificate is never a root, even if its key were reused.
        for (var i = chain.Count > 1 ? 1 : 0; i < chain.Count; i++)
        {
            if (_nameBySpki.TryGetValue(SpkiHash(chain[i]), out var rootName)) return rootName;
        }

        // Partial chain (the root was not sent and isn't in the local store): accept a top certificate
        // issued by a public root's subject name. Cross-signed public roots also land here.
        if (chain.Count > 0 && _nameBySubject.TryGetValue(chain[^1].IssuerName.Name, out var name))
            return name;

        return null;
    }
}
