using CertWatch.Core.Browsers;
using CertWatch.Core.Monitoring;

namespace CertWatch.App.Tracking;

/// <summary>Polls one browser's address bar on a background thread and feeds the site to a monitor.</summary>
internal sealed class BrowserFollower : IDisposable
{
    private readonly AddressBarReader _reader;
    private readonly SiteMonitor _monitor;
    private readonly TimeSpan _pollInterval;
    private readonly Thread _thread;
    private volatile bool _stopping;

    public BrowserFollower(BrowserDefinition browser, SiteMonitor monitor, Func<int, bool> acceptProcess, TimeSpan? pollInterval = null)
    {
        _reader = new AddressBarReader(browser, acceptProcess);
        _monitor = monitor;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(700);
        _thread = new Thread(Run) { IsBackground = true, Name = $"CertWatch follower ({browser.Id})" };
        // UI Automation client calls belong on an MTA thread.
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Raw address-bar text (background thread). Subscribers must not log or store it as-is: it can hold paths and query strings.</summary>
    public event Action<string?>? AddressRead;

    private void Run()
    {
        // Not a possible address, so the first read is always reported (including "no window").
        string? previous = "\0";
        while (!_stopping)
        {
            try
            {
                var text = _reader.Read();
                if (text != previous)
                {
                    previous = text;
                    AddressRead?.Invoke(text);
                    _monitor.SetCurrentSite(AddressParser.Parse(text));
                }
            }
            catch (Exception)
            {
                // Keep polling; a browser restarting or UIA hiccup shouldn't stop CertWatch.
            }
            Thread.Sleep(_pollInterval);
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}
