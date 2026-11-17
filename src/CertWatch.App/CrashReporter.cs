using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace CertWatch.App;

/// <summary>
/// Last-chance handler: an unhandled exception is written to %LOCALAPPDATA%\CertWatch\crash.log
/// and shown in a message box, so the app never just vanishes. The process still exits afterwards,
/// because state after an unknown failure can't be trusted.
/// </summary>
internal static class CrashReporter
{
    internal static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CertWatch", "crash.log");

    private const long MaxLogBytes = 1024 * 1024;
    private static int _reported;

    public static void Install(Application app)
    {
        app.DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception, showDialog: true);
        TaskScheduler.UnobservedTaskException += (_, e) => Write(e.Exception, "unobserved task exception (not fatal)");
    }

    private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Report(e.Exception, showDialog: true);
        Application.Current?.Shutdown(1);
    }

    private static void Report(Exception? ex, bool showDialog)
    {
        // Only the first fatal error is reported; a cascade of follow-on failures would stack dialogs.
        if (Interlocked.Exchange(ref _reported, 1) != 0) return;

        var logged = Write(ex, "fatal");
        if (!showDialog) return;

        var where = logged ? $"Details were saved to:\n{LogPath}" : "The details could not be saved to a log file.";
        try
        {
            MessageBox.Show($"CertWatch hit an unexpected error and has to close.\n\n{ex?.GetType().Name}: {ex?.Message}\n\n{where}",
                "CertWatch", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception)
        {
            // No UI available (e.g. failure during shutdown); the log is the record.
        }
    }

    private static bool Write(Exception? ex, string kind)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > MaxLogBytes) info.Delete();

            var version = typeof(CrashReporter).Assembly.GetName().Version;
            File.AppendAllText(LogPath,
                $"==== {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} · CertWatch {version} · {kind}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
