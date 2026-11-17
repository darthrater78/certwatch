using System.Security.Cryptography.X509Certificates;
using CertWatch.Core.Browsers;
using CertWatch.Core.Diagnostics;
using CertWatch.Core.History;
using CertWatch.Core.Network;
using CertWatch.Core.Tls;
using CertWatch.Core.Trust;

namespace CertWatch.Core.Monitoring;

public sealed record SiteCheck(ProbeResult Probe, Classification Classification, Observation Observation, EgressInfo? Egress);

/// <summary>
/// Follows one browser's current site: checks it when the site changes, then re-checks it on an
/// interval so a policy flip during a demo shows up without reloading anything.
/// Platform-specific code only has to call <see cref="SetCurrentSite"/>.
/// </summary>
public sealed class SiteMonitor : IAsyncDisposable
{
    private readonly ICertProbe _probe;
    private readonly InspectionClassifier _classifier;
    private readonly HistoryStore _history;
    private readonly string _browserId;
    private readonly TimeSpan _recheckInterval;
    private readonly IRouteLookup? _routes;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Task _loop;
    private readonly Lock _gate = new();
    private SiteEndpoint? _current;

    public SiteMonitor(ICertProbe probe, InspectionClassifier classifier, HistoryStore history,
        string browserId, TimeSpan recheckInterval, IRouteLookup? routes = null)
    {
        _routes = routes;
        _probe = probe;
        _classifier = classifier;
        _history = history;
        _browserId = browserId;
        _recheckInterval = recheckInterval;
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Raised (on a background thread) after every check.</summary>
    public event Action<SiteCheck>? Checked;
    /// <summary>Raised (on a background thread) when the followed site changes, before it is checked.</summary>
    public event Action<SiteEndpoint?>? SiteChanged;

    public SiteEndpoint? CurrentSite
    {
        get { lock (_gate) return _current; }
    }

    public void SetCurrentSite(SiteEndpoint? site)
    {
        lock (_gate)
        {
            if (Nullable.Equals(_current, site)) return;
            _current = site;
        }
        SiteChanged?.Invoke(site);
        _wake.Release();
    }

    /// <summary>Check the current site now (e.g. a "Re-check" button).</summary>
    public void RecheckNow() => _wake.Release();

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (CurrentSite is { } s)
                {
                    var check = await CheckAsync(s, _stop.Token);
                    // Drop results for a site the user already navigated away from.
                    if (Nullable.Equals(CurrentSite, s)) Checked?.Invoke(check);
                }

                // Sleep until the interval elapses or the site changes.
                await _wake.WaitAsync(_recheckInterval, _stop.Token);
                while (_wake.CurrentCount > 0) await _wake.WaitAsync(0);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Never let one bad check kill monitoring; log it, wait and try again.
                Diag.Error(_browserId, $"Check failed: {ex.GetType().Name}: {ex.Message}");
                try { await Task.Delay(TimeSpan.FromSeconds(2), _stop.Token); } catch (OperationCanceledException) { return; }
            }
        }
    }

    public async Task<SiteCheck> CheckAsync(SiteEndpoint site, CancellationToken ct = default)
    {
        var probe = await _probe.ProbeAsync(site, ct);
        var classification = _classifier.Classify(probe);
        var previous = _history.GetLatest(site.Host, site.Port, _browserId);
        EgressInfo? egress = null;
        if (_routes is not null && probe.RemoteAddress is { } remote)
        {
            try
            {
                if (_routes.Lookup(remote) is { } facts) egress = EgressClassifier.Classify(facts);
            }
            catch (Exception ex)
            {
                // Route lookup is best-effort; the certificate verdict stands on its own.
                Diag.Warn(_browserId, $"Route lookup for {remote} failed: {ex.Message}");
            }
        }

        var change = ChangeDetector.Detect(previous, classification.Verdict, probe.LeafFingerprint, classification.IssuerName,
            egress?.InterfaceLabel);

        var leaf = probe.Leaf;
        var observation = new Observation(
            probe.Timestamp, site.Host, site.Port, _browserId,
            classification.Verdict, classification.Reason, classification.Vendor,
            probe.LeafFingerprint,
            leaf?.GetNameInfo(X509NameType.SimpleName, false),
            classification.IssuerName, classification.RootName,
            leaf is null ? null : new DateTimeOffset(leaf.NotAfter.ToUniversalTime()),
            probe.Protocol == default ? null : probe.Protocol.ToString(),
            classification.MutualTls,
            change)
        {
            RemoteAddress = probe.RemoteAddress?.ToString(),
            EgressInterface = egress?.InterfaceLabel,
            EgressKind = egress?.Kind.ToString(),
            EgressRoute = egress?.RouteLabel,
        };

        if (change == ChangeKind.None && previous is not null)
        {
            // Nothing changed: just mark the existing row as still current.
            _history.Touch(previous.Id, probe.Timestamp);
            observation = observation with { Id = previous.Id };
        }
        else
        {
            observation = _history.Add(observation);
        }

        return new SiteCheck(probe, classification, observation, egress);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _loop; } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
