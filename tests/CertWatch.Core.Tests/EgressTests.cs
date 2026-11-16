using System.Net;
using CertWatch.Core.History;
using CertWatch.Core.Network;
using CertWatch.Core.Trust;

namespace CertWatch.Core.Tests;

public class EgressClassifierTests
{
    private static RouteFacts Facts(string prefix, int len, string? nextHop, string name = "Ethernet 3",
        string description = "Virtual Adapter", string type = "Ethernet", bool hasGateway = false) =>
        new(IPAddress.Parse("104.18.32.7"), 23, name, description, type, IPAddress.Parse(prefix), len,
            nextHop is null ? null : IPAddress.Parse(nextHop), hasGateway);

    [Fact]
    public void Host_route_without_gateway_is_tunnel()
    {
        var e = EgressClassifier.Classify(Facts("104.18.32.7", 32, null));
        Assert.Equal(EgressKind.Tunnel, e.Kind);
        Assert.Contains("/32", e.Reason);
        Assert.Equal("104.18.32.7/32 → no gateway (on-link)", e.RouteLabel);
    }

    [Fact]
    public void Host_route_on_known_adapter_names_product()
    {
        var e = EgressClassifier.Classify(Facts("104.18.32.7", 32, null, description: "Island Network Adapter"));
        Assert.Equal(EgressKind.Tunnel, e.Kind);
        Assert.Contains("Island", e.Reason);
    }

    [Fact]
    public void Specific_route_via_gateway_on_adapter_without_default_gateway_is_tunnel()
    {
        // Island Private Access as seen on the demo machine, with a neutral adapter name so the
        // decision rests on the routing, not the name.
        var f = new RouteFacts(IPAddress.Parse("100.64.0.32"), 31, "ZTNA", "Virtual Adapter", "Ethernet",
            IPAddress.Parse("100.64.0.0"), 10, IPAddress.Parse("172.16.99.2"), InterfaceHasGateway: false);
        var e = EgressClassifier.Classify(f);
        Assert.Equal(EgressKind.Tunnel, e.Kind);
        Assert.Contains("no default gateway", e.Reason);
        Assert.Equal("100.64.0.0/10 → via 172.16.99.2", e.RouteLabel);
    }

    [Fact]
    public void On_link_subnet_on_gatewayless_virtual_switch_is_direct()
    {
        // Hyper-V / WSL style: on-link /20 on an adapter with no gateway.
        var f = new RouteFacts(IPAddress.Parse("172.28.16.5"), 40, "vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", "Ethernet",
            IPAddress.Parse("172.28.16.0"), 20, null, InterfaceHasGateway: false);
        Assert.Equal(EgressKind.Direct, EgressClassifier.Classify(f).Kind);
    }

    [Fact]
    public void Default_route_via_gateway_is_direct()
    {
        var e = EgressClassifier.Classify(Facts("0.0.0.0", 0, "10.0.0.1", "Wi-Fi", "Intel Wi-Fi 6E AX211", "Wireless80211", hasGateway: true));
        Assert.Equal(EgressKind.Direct, e.Kind);
        Assert.Equal("Default route via 10.0.0.1", e.Reason);
        Assert.Equal("Wi-Fi", e.InterfaceLabel);
    }

    [Fact]
    public void Default_route_on_known_vpn_adapter_is_tunnel()
    {
        var e = EgressClassifier.Classify(Facts("0.0.0.0", 0, "100.64.0.1", "Ethernet 4", "PANGP Virtual Ethernet Adapter", hasGateway: true));
        Assert.Equal(EgressKind.Tunnel, e.Kind);
    }

    [Fact]
    public void Local_subnet_on_lan_adapter_is_direct()
    {
        var e = EgressClassifier.Classify(Facts("10.0.0.0", 24, null, "Ethernet", "Realtek PCIe GbE", hasGateway: true));
        Assert.Equal(EgressKind.Direct, e.Kind);
    }
}

public class EgressChangeTests
{
    private static Observation Prev(string? egressIf) => new(DateTimeOffset.UtcNow, "h", 443, "edge", Verdict.NotInspected, "",
        null, "A", null, null, null, null, null, false, ChangeKind.None) { EgressInterface = egressIf };

    [Fact] public void Interface_move_is_a_change() =>
        Assert.Equal(ChangeKind.EgressChanged, ChangeDetector.Detect(Prev("Wi-Fi"), Verdict.NotInspected, "A", null, "Island Adapter"));

    [Fact] public void Same_interface_is_no_change() =>
        Assert.Equal(ChangeKind.None, ChangeDetector.Detect(Prev("Wi-Fi"), Verdict.NotInspected, "A", null, "Wi-Fi"));

    [Fact] public void Certificate_change_wins_over_interface_change() =>
        Assert.Equal(ChangeKind.InspectionStarted, ChangeDetector.Detect(Prev("Wi-Fi"), Verdict.Inspected, "B", "Island CA", "Island Adapter"));

    [Fact] public void Unknown_previous_interface_is_no_change() =>
        Assert.Equal(ChangeKind.None, ChangeDetector.Detect(Prev(null), Verdict.NotInspected, "A", null, "Wi-Fi"));
}

public class ClearHistoryTests
{
    [Fact]
    public void Deletes_one_site_or_one_browser_and_keeps_the_rest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"certwatch-test-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new HistoryStore(path, new HistoryCipher(HistoryCipher.NewMasterKey()));
            var o = new Observation(DateTimeOffset.UtcNow, "a.example", 443, "edge", Verdict.NotInspected, "r", null,
                "FP", null, null, null, null, null, false, ChangeKind.FirstSeen) { EgressInterface = "Wi-Fi", EgressKind = "Direct" };
            store.Add(o);
            store.Add(o with { Host = "b.example" });
            store.Add(o with { Browser = "island" });

            Assert.Equal("Wi-Fi", store.GetLatest("a.example", 443, "edge")!.EgressInterface);

            // A site inspected earlier keeps that fact in its summary after it stops being inspected.
            store.Add(o with { Host = "b.example", Verdict = Verdict.Inspected, Issuer = "Island CA", Change = ChangeKind.InspectionStarted });
            store.Add(o with { Host = "b.example", Verdict = Verdict.NotInspected, Change = ChangeKind.InspectionStopped });
            var b = store.GetSiteSummaries("edge").Single(x => x.Latest.Host == "b.example");
            Assert.Equal(Verdict.NotInspected, b.Latest.Verdict);
            Assert.NotNull(b.LastInspected);
            Assert.Equal("Island CA", b.InspectedBy);
            Assert.Null(store.GetSiteSummaries("edge").Single(x => x.Latest.Host == "a.example").LastInspected);

            Assert.Equal(1, store.DeleteSite("a.example", 443, "edge"));
            Assert.Null(store.GetLatest("a.example", 443, "edge"));
            Assert.NotNull(store.GetLatest("a.example", 443, "island"));
            Assert.Single(store.GetSiteSummaries("edge"));

            Assert.Equal(3, store.DeleteBrowser("edge"));
            Assert.Empty(store.GetSiteSummaries("edge"));
            Assert.Single(store.GetSiteSummaries("island"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        }
    }
}

public class PathTreatmentTests
{
    [Theory]
    [InlineData(Verdict.Inspected, "Tunnel", Treatment.SteeredAndInspected)]
    [InlineData(Verdict.NotInspected, "Tunnel", Treatment.SteeredNotInspected)]
    [InlineData(Verdict.Inspected, "Direct", Treatment.InspectedOnDevice)]
    [InlineData(Verdict.NotInspected, "Direct", Treatment.Direct)]
    [InlineData(Verdict.Inspected, null, Treatment.Inspected)]
    [InlineData(Verdict.NotInspected, null, Treatment.NotInspected)]
    [InlineData(Verdict.Untrusted, "Tunnel", Treatment.Unknown)]
    public void Combines_inspection_and_steering(Verdict v, string? egress, Treatment expected) =>
        Assert.Equal(expected, PathTreatment.Of(v, egress));
}
