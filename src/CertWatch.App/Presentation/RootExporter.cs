using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Windows;

namespace CertWatch.App.Presentation;

/// <summary>
/// Saves the root of a chain (.cer DER or .pem), or the whole chain as one .pem. For an inspected
/// site that's the inspection root. Returns a status line for the tab, or null if nothing was saved.
/// </summary>
internal static class RootExporter
{
    public static string? Export(IReadOnlyList<X509Certificate2> chain, string site)
    {
        if (chain.Count == 0)
        {
            MessageBox.Show("There's no certificate to export here. Export works on a live result: open the site in the browser first.", "CertWatch");
            return null;
        }

        var root = chain[^1];
        var isRoot = root.SubjectName.RawData.AsSpan().SequenceEqual(root.IssuerName.RawData);
        var name = root.GetNameInfo(X509NameType.SimpleName, false);
        var safe = string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = isRoot ? $"Export root: {name}" : $"Export top certificate: {name} (root not available)",
            FileName = string.IsNullOrWhiteSpace(safe) ? "root" : safe,
            Filter = "Root certificate, DER (*.cer)|*.cer|Root certificate, PEM (*.pem)|*.pem|Full chain, PEM (*.pem)|*.pem",
            AddExtension = true,
        };
        if (dialog.ShowDialog() != true) return null;

        try
        {
            switch (dialog.FilterIndex)
            {
                case 1: File.WriteAllBytes(dialog.FileName, root.RawData); break;
                case 2: File.WriteAllText(dialog.FileName, root.ExportCertificatePem() + "\n"); break;
                default: File.WriteAllText(dialog.FileName, string.Join("\n", chain.Select(c => c.ExportCertificatePem())) + "\n"); break;
            }
            return isRoot
                ? $"Exported {name} (root for {site}) to {dialog.FileName}"
                : $"Exported {name} to {dialog.FileName}. The root itself wasn't available on this machine, so this is the top certificate the site sent.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Couldn't save the file.\n\n{ex.Message}", "CertWatch");
            return null;
        }
    }
}
