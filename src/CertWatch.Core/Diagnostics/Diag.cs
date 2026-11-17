namespace CertWatch.Core.Diagnostics;

public enum DiagLevel { Debug, Info, Warning, Error }

/// <summary>
/// Minimal diagnostics hook for the core library. The app subscribes and routes messages to its
/// log; with nothing subscribed, messages are dropped. Debug messages are only built when the app
/// has turned debug detail on, so they cost nothing otherwise.
/// </summary>
public static class Diag
{
    public static event Action<DiagLevel, string, string>? Message;

    /// <summary>Set by the app when debug detail is enabled.</summary>
    public static volatile bool DebugEnabled;

    public static void Info(string source, string text) => Message?.Invoke(DiagLevel.Info, source, text);
    public static void Warn(string source, string text) => Message?.Invoke(DiagLevel.Warning, source, text);
    public static void Error(string source, string text) => Message?.Invoke(DiagLevel.Error, source, text);

    public static void Debug(string source, Func<string> text)
    {
        if (DebugEnabled) Message?.Invoke(DiagLevel.Debug, source, text());
    }
}
