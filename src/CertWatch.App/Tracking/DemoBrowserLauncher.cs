using System.Diagnostics;
using System.IO;
using CertWatch.Core.Browsers;
using CertWatch.Core.Cdp;
using Microsoft.Win32;

namespace CertWatch.App.Tracking;

internal sealed record DemoBrowser(CdpPipeClient Cdp, Process Process, string ProfileDirectory);

/// <summary>
/// Starts a Chromium browser for the demo with a private DevTools pipe and its own CertWatch profile.
/// A separate profile is required: Chrome 136+ (and browsers built on it) refuse remote debugging
/// on the user's normal profile. The user's own browser windows are untouched.
/// </summary>
internal static class DemoBrowserLauncher
{
    public static async Task<DemoBrowser> LaunchAsync(BrowserDefinition browser, CancellationToken ct = default)
    {
        var exe = FindExecutable(browser)
            ?? throw new InvalidOperationException($"Couldn't find {browser.DisplayName}'s program file.");

        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CertWatch", "profiles", browser.Id);
        Directory.CreateDirectory(profile);

        var launched = PipeLauncher.Start(exe,
        [
            "--remote-debugging-pipe",
            $"--user-data-dir={profile}",
            "--no-first-run",
            "--no-default-browser-check",
            "--new-window",
        ]);
        var cdp = new CdpPipeClient(launched.ToBrowser, launched.FromBrowser);

        // One call with a generous wait: the first start of a new profile can be slow.
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startup.CancelAfter(TimeSpan.FromSeconds(25));
        while (!startup.IsCancellationRequested)
        {
            if (await cdp.IsAliveAsync(startup.Token)) return new DemoBrowser(cdp, launched.Process, profile);
            if (cdp.IsClosed || launched.Process.HasExited) break;
            await Task.Delay(300, startup.Token).ContinueWith(_ => { });
        }

        await cdp.DisposeAsync();
        throw new InvalidOperationException(
            $"{browser.DisplayName} started but didn't answer on its DevTools pipe. If a CertWatch demo window of {browser.DisplayName} is already open, close it and try again; otherwise {browser.DisplayName} may block remote debugging by policy.");
    }

    /// <summary>The browser's .exe: from a running copy, then App Paths in the registry, then usual install folders.</summary>
    public static string? FindExecutable(BrowserDefinition browser) =>
        FromRunningProcess(browser) ?? FromAppPaths(browser) ?? FromInstallFolders(browser);

    /// <summary>Accepts a path only if it exists and is the browser's own program file (e.g. chrome.exe).</summary>
    private static bool IsBrowserExe(string? path, BrowserDefinition browser) =>
        path is not null && File.Exists(path) &&
        browser.ProcessNames.Contains(Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase);

    private static string? FromRunningProcess(BrowserDefinition browser)
    {
        foreach (var p in browser.ProcessNames.SelectMany(Process.GetProcessesByName))
        {
            using (p)
            {
                var path = TryGetPath(p);
                if (IsBrowserExe(path, browser)) return path;
            }
        }
        return null;
    }

    private static string? TryGetPath(Process p)
    {
        try { return p.MainModule?.FileName; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    private static string? FromAppPaths(BrowserDefinition browser)
    {
        var keys = from name in browser.ProcessNames
                   from hive in new[] { "HKEY_CURRENT_USER", "HKEY_LOCAL_MACHINE" }
                   select $@"{hive}\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{name}.exe";
        return keys
            .Select(k => (Registry.GetValue(k, "", null) as string)?.Trim('"'))
            .FirstOrDefault(path => IsBrowserExe(path, browser));
    }

    private static string? FromInstallFolders(BrowserDefinition browser)
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates = browser.Id switch
        {
            "chrome" => [$@"{pf}\Google\Chrome\Application\chrome.exe", $@"{pf86}\Google\Chrome\Application\chrome.exe", $@"{local}\Google\Chrome\Application\chrome.exe"],
            "edge" => [$@"{pf86}\Microsoft\Edge\Application\msedge.exe", $@"{pf}\Microsoft\Edge\Application\msedge.exe"],
            "brave" => [$@"{pf}\BraveSoftware\Brave-Browser\Application\brave.exe", $@"{local}\BraveSoftware\Brave-Browser\Application\brave.exe"],
            "vivaldi" => [$@"{local}\Vivaldi\Application\vivaldi.exe", $@"{pf}\Vivaldi\Application\vivaldi.exe"],
            _ => [],
        };
        return candidates.FirstOrDefault(c => IsBrowserExe(c, browser));
    }

}
