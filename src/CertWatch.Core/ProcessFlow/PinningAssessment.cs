using CertWatch.Core.Trust;

namespace CertWatch.Core.ProcessFlow;

/// <summary>Where a destination stands on the question "is this process pinned?".</summary>
public enum PinningState
{
    NotChecked,
    Checking,
    NeedsHostname,
    NotInspected,
    RootNotTrusted,
    NeedsHandshakeReading,
    WaitingForConnection,
    Refused,
    Likely,
    NoSign,
}

/// <summary>How the list groups results. The order is the order they're shown in.</summary>
public enum PinningGroup { NeedsAttention, CantTell, NoSign }

/// <summary>A plain answer: a short label, a headline, why, and what to do next.</summary>
public sealed record PinningAssessment(PinningState State, string Label, string Headline, string Why, string Next)
{
    public PinningGroup Group => State switch
    {
        PinningState.Likely or PinningState.Refused => PinningGroup.NeedsAttention,
        PinningState.NoSign => PinningGroup.NoSign,
        _ => PinningGroup.CantTell,
    };
}

/// <summary>
/// Pinning only shows when a client is handed a replaced certificate it should otherwise trust, so
/// each answer first establishes whether that test has actually happened. Anything short of it is
/// "can't tell", never "not pinned".
/// </summary>
public static class PinningAssessor
{
    private const string TurnOnReading = "Start CertWatch as administrator, turn on Read this process's handshakes, then restart the app.";

    public static PinningAssessment Assess(FlowDestination d, bool readingHandshakes)
    {
        if (d.NeedsHostname)
            return new(PinningState.NeedsHostname, "Can't tell", "Can't tell: this server needs a hostname",
                "The server has a certificate but won't send it until told which site is wanted, and CertWatch doesn't know which site this connection used.",
                readingHandshakes ? "Open a new connection from the app so CertWatch can read its hostname." : TurnOnReading);
        if (d.Classification is not { } classification) return NoCertificateYet(d);
        if (classification.Verdict == Verdict.Error)
            return new(PinningState.Checking, "Can't tell", "Can't tell yet", "CertWatch couldn't get a certificate from this destination.", "It tries again automatically.");
        if (classification.Verdict == Verdict.NotInspected || d.ProcessGotOtherIssuer) return NotInspected(d);
        if (!d.InspectionTrustedHere)
            return new(PinningState.RootNotTrusted, "Can't tell", "Can't tell: this PC doesn't trust the inspection root",
                "Any app would refuse this certificate, so a refusal says nothing about pinning.",
                "Install the inspection root on this PC, then restart the app.");
        return InspectedAndTrusted(d, readingHandshakes);
    }

    private static PinningAssessment NoCertificateYet(FlowDestination d)
    {
        if (!d.CanCheck)
            return new(PinningState.NotChecked, "Not checked", "Not checked", "CertWatch only checks TLS ports (443 and 8443) unless it sees a real TLS handshake.",
                "Turn on handshake reading if this connection uses TLS on another port.");
        return d.OpenConnections > 0
            ? new(PinningState.Checking, "Checking", "Checking this destination", "CertWatch is getting its certificate.", "This usually takes a few seconds.")
            : new(PinningState.NotChecked, "Not checked", "Not checked", "The connection closed before CertWatch could check it.", "Restart the app to try again.");
    }

    private static PinningAssessment NotInspected(FlowDestination d)
    {
        var refused = d.Refused > 0 ? $" The process refused {Times(d.Refused)}, but not because of a replaced certificate." : "";
        return new(PinningState.NotInspected, "Can't tell", "Can't tell: not inspected",
            $"This connection has the site's own certificate, so a pin would still pass.{refused}",
            "Turn on inspection for this destination, then restart the app.");
    }

    private static PinningAssessment InspectedAndTrusted(FlowDestination d, bool readingHandshakes)
    {
        if (d.Pinning == PinningSignal.Likely)
            return new(PinningState.Likely, "Pinning likely", "Pinning likely",
                $"The process refused this inspected certificate {Times(d.Refused)} and never accepted it, even though this PC trusts it. An app with its own private trust store behaves the same way.",
                "Add an inspection bypass for this destination, then restart the app. If it connects, inspection was what broke it.");
        if (d.Accepted > 0)
            return new(PinningState.NoSign, "No sign", "No sign of pinning",
                $"The process completed {Connections(d.Accepted)} while this destination was being inspected.",
                "No action needed.");
        if (d.Refused > 0)
            return new(PinningState.Refused, $"Refused {d.Refused} of {ProcessFlowTracker.PinningRefusals}", "Refused, not enough to call it pinning",
                $"The process refused the inspected certificate {Times(d.Refused)}. {ProcessFlowTracker.PinningRefusals} refusals with none accepted reads as pinning likely.",
                "Use the app's failing feature again, or restart it, so it retries.");
        if (!readingHandshakes)
            return new(PinningState.NeedsHandshakeReading, "Can't tell", "Can't tell: handshakes aren't being read",
                "This destination is inspected, but CertWatch can only see whether the process accepts the certificate by reading its handshakes.",
                TurnOnReading);
        return new(PinningState.WaitingForConnection, "Waiting", "Waiting for a new connection",
            "This destination is inspected. Connections opened before handshake reading started can't be judged.",
            "Restart the app, or use the feature that connects here.");
    }

    private static string Times(int count) => count == 1 ? "once" : $"{count} times";
    private static string Connections(int count) => count == 1 ? "1 connection" : $"{count} connections";
}
