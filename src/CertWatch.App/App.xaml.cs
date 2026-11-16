using System.Threading;
using System.Windows;

namespace CertWatch.App;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\CertWatch.SingleInstance";
    private const string ShowEventName = @"Local\CertWatch.ShowWindow";

    // Held for the life of the process so a second launch can tell CertWatch is already running.
    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var firstInstance);
        if (!firstInstance)
        {
            // Already running (maybe hidden in the tray): bring that window forward and quit.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }

        var options = StartupOptions.Parse(e.Args);
        if (options.Error is not null)
            MessageBox.Show(options.Error, "CertWatch", MessageBoxButton.OK, MessageBoxImage.Warning);

        var window = new MainWindow(options);
        window.Show();

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var listener = new Thread(() =>
        {
            while (_showEvent.WaitOne()) Dispatcher.BeginInvoke(window.ShowFromTray);
        }) { IsBackground = true, Name = "CertWatch show-window listener" };
        listener.Start();
    }
}
