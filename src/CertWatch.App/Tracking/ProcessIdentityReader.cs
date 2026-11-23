using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
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

    private static readonly Dictionary<string, string?> Descriptions = new(StringComparer.OrdinalIgnoreCase);
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
        return ProcessIdentity.From(processName, path, path is null ? null : DescriptionOf(path));
    }

    private static string? ImagePath(int processId)
    {
        using var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle.IsInvalid) return null;
        var buffer = new char[MaxPathChars];
        var size = buffer.Length;
        return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
    }

    private static string? DescriptionOf(string path)
    {
        // Reading a file on a network share could send this PC's credentials to that server.
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        lock (Gate)
        {
            if (Descriptions.TryGetValue(path, out var known)) return known;
        }
        string? description = null;
        try
        {
            description = FileVersionInfo.GetVersionInfo(path).FileDescription;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception)
        {
            // No readable version resource: the exe name stands alone.
        }
        lock (Gate)
        {
            if (Descriptions.Count >= MaxCachedPaths) Descriptions.Clear();
            Descriptions[path] = description;
        }
        return description;
    }
}
