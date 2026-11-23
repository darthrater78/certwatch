using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using CertWatch.App.Logging;
using CertWatch.Core.ProcessFlow;

namespace CertWatch.App.Tracking;

/// <summary>
/// Reads a process's exe path with the least access Windows offers, and the friendly name from that
/// file's version resource. The exe is read as data and never loaded or run. A process Windows won't
/// describe simply shows by its process name.
/// </summary>
internal static partial class ProcessIdentityReader
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int MaxPathChars = 32768, MaxCachedPaths = 512;

    private static readonly Dictionary<string, (string? Description, string? Product)> Names = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, [Out] char[] path, ref int size);

    public static ProcessIdentity Read(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return Read(processId, process.ProcessName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return ProcessIdentity.From($"PID {processId}", null, null);
        }
    }

    public static ProcessIdentity Read(int processId, string processName)
    {
        var path = ImagePath(processId);
        var (description, product) = path is null ? default : VersionNames(path);
        var identity = ProcessIdentity.From(processName, path, description, product);
        LogService.Debug("process", () => $"PID {processId}: exe {(path is null ? "path not read" : Path.GetFileName(path))}, description \"{description}\", product \"{product}\", shown as {identity.Label}");
        return identity;
    }

    private static string? ImagePath(int processId)
    {
        using var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        var error = Marshal.GetLastPInvokeError();
        if (!handle.IsInvalid)
        {
            var buffer = new char[MaxPathChars];
            var size = buffer.Length;
            if (QueryFullProcessImageName(handle, 0, buffer, ref size)) return new string(buffer, 0, size);
            error = Marshal.GetLastPInvokeError();
        }
        LogService.Debug("process", () => $"PID {processId}: limited exe path query failed (Windows error {error})");
        return MainModulePath(processId);
    }

    // The route PowerShell's Get-Process uses. It needs more access, so it's only the fallback.
    private static string? MainModulePath(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            LogService.Debug("process", () => $"PID {processId}: main module not readable ({ex.Message})");
            return null;
        }
    }

    private static (string? Description, string? Product) VersionNames(string path)
    {
        // Reading a file on a network share could send this PC's credentials to that server.
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return default;
        lock (Gate)
        {
            if (Names.TryGetValue(path, out var known)) return known;
        }
        (string?, string?) names = default;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            names = (info.FileDescription, info.ProductName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception)
        {
            // No readable version resource: the exe name stands alone.
            LogService.Debug("process", () => $"{Path.GetFileName(path)}: version details not readable ({ex.Message})");
        }
        lock (Gate)
        {
            if (Names.Count >= MaxCachedPaths) Names.Clear();
            Names[path] = names;
        }
        return names;
    }
}
