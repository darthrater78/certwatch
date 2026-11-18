using CertWatch.Core.Browsers;

namespace CertWatch.Core.Tests;

public class CertViewerTextTests
{
    // The General tab of Chromium's certificate viewer, as UI Automation lists its text (the user's screenshot).
    private static readonly string[] General =
    [
        "General", "Details", "Issued To",
        "Common Name (CN)", "claude.ai", "Organization (O)", "<Not Part Of Certificate>", "Organizational Unit (OU)", "<Not Part Of Certificate>",
        "Issued By",
        "Common Name (CN)", "YE2", "Organization (O)", "Let's Encrypt", "Organizational Unit (OU)", "<Not Part Of Certificate>",
        "Validity Period", "Issued On", "Sunday, November 8, 2026 at 8:43:37 AM", "Expires On", "Saturday, February 6, 2027 at 8:43:36 AM",
        "SHA-256 Fingerprints",
        "Certificate", "189f10934c50e254cb76f26fb5223ca05598a36fe92a82b87039af9a9395e88a",
        "Public Key", "4a0e9c5d73f7ba9d6d94b877f801e527ed925fa061b3825e48c9e05d35219c09",
    ];

    [Fact]
    public void Reads_the_certificate_fingerprint_not_the_public_key()
    {
        var (sha, _, _) = CertViewerText.Parse(General);
        Assert.Equal("189F10934C50E254CB76F26FB5223CA05598A36FE92A82B87039AF9A9395E88A", sha);
    }

    [Fact]
    public void Reads_the_issuer_not_the_subject()
    {
        var (_, cn, o) = CertViewerText.Parse(General);
        Assert.Equal("YE2", cn);
        Assert.Equal("Let's Encrypt", o);
    }

    [Fact]
    public void Joins_a_fingerprint_wrapped_over_two_text_runs()
    {
        var (sha, _, _) = CertViewerText.Parse(["Certificate", "189f10934c50e254cb76f26fb5223ca05598a36fe92a82b87039af9a9395", "e88a"]);
        Assert.Equal("189F10934C50E254CB76F26FB5223CA05598A36FE92A82B87039AF9A9395E88A", sha);
    }

    [Fact]
    public void Accepts_colon_separated_fingerprints()
    {
        var hex = string.Join(":", Enumerable.Range(0, 32).Select(i => "AB"));
        Assert.Equal(string.Concat(Enumerable.Repeat("AB", 32)), CertViewerText.Parse(["SHA-256", hex]).Sha256);
    }

    [Fact]
    public void Missing_values_are_null()
    {
        var (sha, cn, o) = CertViewerText.Parse(["Issued By", "Common Name (CN)", "<Not Part Of Certificate>", "Validity Period"]);
        Assert.Null(sha);
        Assert.Null(cn);
        Assert.Null(o);
    }

    [Theory]
    [InlineData("Certificate Viewer: claude.ai", "claude.ai")]
    [InlineData("certificate viewer:  Portal.Contoso.com ", "portal.contoso.com")]
    [InlineData("Certificate Viewer: <script>", null)]
    [InlineData("Certificate Viewer: ", null)]
    [InlineData("Downloads", null)]
    [InlineData(null, null)]
    public void Host_comes_from_the_viewer_title_only(string? title, string? host)
    {
        Assert.Equal(host, CertViewerText.HostFromTitle(title));
    }

    [Fact]
    public void Issuer_label_and_match()
    {
        var r = new CertViewerReading("claude.ai", "AB", "YE2", "Let's Encrypt", DateTimeOffset.UtcNow);
        Assert.Equal("YE2 (Let's Encrypt)", r.IssuerLabel);
        Assert.True(r.Matches("ab"));
        Assert.False(r.Matches("CD"));
        Assert.False(r.Matches(null));
    }
}
