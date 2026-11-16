using System.IO;
using System.Security.Cryptography;
using CertWatch.Core.History;

namespace CertWatch.App;

/// <summary>
/// Keeps the history database's master key in %LOCALAPPDATA%\CertWatch\history.key, encrypted with
/// Windows DPAPI under the current user's account: only this Windows account on this PC can unwrap
/// it. If it can't be unwrapped (copied from another account or PC), the unreadable history is set
/// aside and a fresh database and key are started.
/// </summary>
internal static class HistoryKeyStore
{
    private static readonly byte[] Entropy = "CertWatch history key v1"u8.ToArray();

    public static HistoryStore OpenHistory(string databasePath)
    {
        var keyPath = Path.Combine(Path.GetDirectoryName(databasePath)!, "history.key");
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);

        var key = TryLoad(keyPath);
        if (key is null)
        {
            if (File.Exists(keyPath) || File.Exists(databasePath)) SetAside(databasePath, keyPath);
            key = HistoryCipher.NewMasterKey();
            File.WriteAllBytes(keyPath, ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
        }

        try
        {
            return new HistoryStore(databasePath, new HistoryCipher(key));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[]? TryLoad(string keyPath)
    {
        if (!File.Exists(keyPath)) return null;
        try
        {
            var key = ProtectedData.Unprotect(File.ReadAllBytes(keyPath), Entropy, DataProtectionScope.CurrentUser);
            return key.Length == 32 ? key : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// Renames history that can't be decrypted (or plaintext history with no key yet is migrated by the
    /// store itself, so only an existing-but-unusable key triggers this).
    /// </summary>
    private static void SetAside(string databasePath, string keyPath)
    {
        if (!File.Exists(keyPath)) return; // first run with encryption: the store migrates plaintext rows itself
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        foreach (var f in new[] { databasePath, databasePath + "-wal", databasePath + "-shm", keyPath })
        {
            if (File.Exists(f)) File.Move(f, $"{f}.unreadable-{stamp}");
        }
    }
}
