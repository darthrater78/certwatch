using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Threading;
using CertWatch.Core.Diagnostics;

namespace CertWatch.App.Logging;

public sealed record LogEntry(DateTime Time, DiagLevel Level, string Source, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss.fff");
    public string LevelText => Level.ToString();
    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss.fff} {Level,-7} [{Source}] {Message}";
}

/// <summary>
/// The app's log. Keeps the last <see cref="Capacity"/> entries in memory for the Log window, and,
/// only when the user turns it on, appends them to a daily file under %LOCALAPPDATA%\CertWatch\logs.
/// Entries can contain site names, so file logging is off by default and "Clear everything" deletes
/// both the memory buffer and the files.
/// </summary>
public static class LogService
{
    public const int Capacity = 5000;
    private static readonly Lock FileLock = new();
    private static Dispatcher? _dispatcher;
    private static bool _writeToFile;

    public static ObservableCollection<LogEntry> Entries { get; } = [];

    public static string LogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CertWatch", "logs");

    public static void Initialize(Dispatcher dispatcher, bool debugDetail, bool writeToFile)
    {
        _dispatcher = dispatcher;
        Diag.DebugEnabled = debugDetail;
        _writeToFile = writeToFile;
        Diag.Message += Add;
    }

    public static bool DebugDetail
    {
        get => Diag.DebugEnabled;
        set { Diag.DebugEnabled = value; Info("log", value ? "Debug detail on" : "Debug detail off"); }
    }

    public static bool WriteToFile
    {
        get => _writeToFile;
        set { _writeToFile = value; Info("log", value ? $"Writing log to {LogDirectory}" : "Stopped writing log to file"); }
    }

    public static void Info(string source, string text) => Add(DiagLevel.Info, source, text);
    public static void Warn(string source, string text) => Add(DiagLevel.Warning, source, text);
    public static void Error(string source, string text) => Add(DiagLevel.Error, source, text);
    public static void Debug(string source, Func<string> text) => Diag.Debug(source, text);

    private static void Add(DiagLevel level, string source, string text)
    {
        var entry = new LogEntry(DateTime.Now, level, source, text);
        if (_writeToFile) AppendToFile(entry);
        _dispatcher?.BeginInvoke(() =>
        {
            Entries.Add(entry);
            while (Entries.Count > Capacity) Entries.RemoveAt(0);
        });
    }

    private static void AppendToFile(LogEntry entry)
    {
        try
        {
            lock (FileLock)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(Path.Combine(LogDirectory, $"certwatch-{entry.Time:yyyyMMdd}.log"), entry + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Never let logging break the app.
        }
    }

    public static string AllText() => string.Join(Environment.NewLine, Entries.Select(e => e.ToString()));

    /// <summary>Clears the in-memory log and deletes the log files.</summary>
    public static void Clear()
    {
        Entries.Clear();
        lock (FileLock)
        {
            try
            {
                if (Directory.Exists(LogDirectory))
                    foreach (var f in Directory.GetFiles(LogDirectory, "certwatch-*.log")) File.Delete(f);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
