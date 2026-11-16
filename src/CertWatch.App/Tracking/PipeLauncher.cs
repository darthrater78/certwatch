using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CertWatch.App.Tracking;

/// <summary>
/// Starts a Chromium browser with --remote-debugging-pipe. Chromium reads DevTools commands from
/// C-runtime file descriptor 3 and writes replies to descriptor 4; on Windows those descriptors are
/// passed through STARTUPINFO.lpReserved2 (the CRT's inheritance block). The child is restricted
/// to inheriting exactly those two pipe handles (PROC_THREAD_ATTRIBUTE_HANDLE_LIST), so nothing else
/// of CertWatch's leaks into the browser, and no other program can reach the pipes.
/// </summary>
internal static partial class PipeLauncher
{
    internal sealed record Launched(Process Process, Stream ToBrowser, Stream FromBrowser);

    public static Launched Start(string exePath, IReadOnlyList<string> arguments)
    {
        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = 1 };

        // Pipe A: CertWatch writes, browser reads (fd 3). Pipe B: browser writes (fd 4), CertWatch reads.
        if (!CreatePipe(out var browserReads, out var weWrite, ref sa, 0)) throw new Win32Exception();
        if (!CreatePipe(out var weRead, out var browserWrites, ref sa, 0)) throw new Win32Exception();
        // Our ends must not be inheritable.
        SetHandleInformation(weWrite, HANDLE_FLAG_INHERIT, 0);
        SetHandleInformation(weRead, HANDLE_FLAG_INHERIT, 0);

        var crtBlock = IntPtr.Zero;
        var attributeList = IntPtr.Zero;
        var handleList = IntPtr.Zero;
        try
        {
            crtBlock = BuildCrtInheritanceBlock(browserReads.DangerousGetHandle(), browserWrites.DangerousGetHandle(), out var crtSize);

            handleList = Marshal.AllocHGlobal(IntPtr.Size * 2);
            Marshal.WriteIntPtr(handleList, 0, browserReads.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, IntPtr.Size, browserWrites.DangerousGetHandle());

            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributeList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(attributeList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handleList,
                    IntPtr.Size * 2, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.StartupInfo.cbReserved2 = (short)crtSize;
            si.StartupInfo.lpReserved2 = crtBlock;
            si.lpAttributeList = attributeList;

            var commandLine = new StringBuilder(Quote(exePath));
            foreach (var arg in arguments) commandLine.Append(' ').Append(Quote(arg));

            if (!CreateProcessW(exePath, commandLine, IntPtr.Zero, IntPtr.Zero, true, EXTENDED_STARTUPINFO_PRESENT,
                    IntPtr.Zero, null, ref si, out var pi))
                throw new Win32Exception();

            CloseHandle(pi.hThread);
            Process process;
            try { process = Process.GetProcessById(pi.dwProcessId); }
            finally { CloseHandle(pi.hProcess); }

            return new Launched(process,
                new FileStream(weWrite, FileAccess.Write, 1, false),
                new FileStream(weRead, FileAccess.Read, 4096, false));
        }
        catch
        {
            weWrite.Dispose();
            weRead.Dispose();
            throw;
        }
        finally
        {
            // The browser has its own copies now (or failed to start); close ours of its ends.
            browserReads.Dispose();
            browserWrites.Dispose();
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            if (handleList != IntPtr.Zero) Marshal.FreeHGlobal(handleList);
            if (crtBlock != IntPtr.Zero) Marshal.FreeHGlobal(crtBlock);
        }
    }

    /// <summary>
    /// CRT inheritance block: int count, then one flag byte per descriptor, then one HANDLE per
    /// descriptor, packed. Descriptors 0–2 are left closed; 3 and 4 are open pipes.
    /// </summary>
    private static IntPtr BuildCrtInheritanceBlock(IntPtr fd3, IntPtr fd4, out int size)
    {
        const int count = 5;
        const byte FOPEN = 0x01, FPIPE = 0x08;
        size = sizeof(int) + count + count * IntPtr.Size;
        var block = Marshal.AllocHGlobal(size);
        Marshal.WriteInt32(block, 0, count);
        byte[] flags = [0, 0, 0, FOPEN | FPIPE, FOPEN | FPIPE];
        Marshal.Copy(flags, 0, block + sizeof(int), count);
        IntPtr[] handles = [new IntPtr(-1), new IntPtr(-1), new IntPtr(-1), fd3, fd4];
        for (var i = 0; i < count; i++)
            Marshal.WriteIntPtr(block, sizeof(int) + count + i * IntPtr.Size, handles[i]);
        return block;
    }

    /// <summary>Windows command-line quoting (CommandLineToArgvW rules).</summary>
    private static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0) return arg;
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var ch in arg)
        {
            if (ch == '\\') { backslashes++; continue; }
            sb.Append('\\', ch == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            sb.Append(ch);
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    private const uint HANDLE_FLAG_INHERIT = 0x1;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_HANDLE_LIST = new(0x00020002);

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeHandle hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
