using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthNav;
using RynthNav.Routing;

namespace RynthNav.RouteTests;

// Custom-server portals (CustomPortals.cs, 2026-10-29): a scan writes the portal objects down per
// server, taking one learns its landing, and only this server's portals with both ends on the map
// reach the route planner.
internal static partial class Program
{
    private static void RunCustomPortals()
    {
        Run(nameof(CustomScanAddsAndSkipsBuiltIn), CustomScanAddsAndSkipsBuiltIn);
        Run(nameof(CustomLandingMakesARouteLink), CustomLandingMakesARouteLink);
        Run(nameof(CustomPortalsArePerServer), CustomPortalsArePerServer);
        Run(nameof(CustomPortalRoutesATrip), CustomPortalRoutesATrip);
        Run(nameof(CustomPortalsRoundTrip), CustomPortalsRoundTrip);
        Run(nameof(CustomForgetAndClear), CustomForgetAndClear);
    }

    private static readonly DateTime T0 = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
    private const string Ilt = "Infinite Leaftide", Other = "Coldeve";

    private static List<PortalLink> BuiltIn() => new() { new PortalLink(10.0, 20.0, 50.0, 60.0, "Portal to Holtburg") };

    private static void CustomScanAddsAndSkipsBuiltIn()
    {
        var s = new CustomPortalStore();
        Check(s.Saw(Ilt, "Leaftide Event Portal", 70001, 0xA9B40001, true, 30.0, 40.0, BuiltIn(), T0) == CustomScanResult.Added, "a new portal is added");
        Check(s.Saw(Ilt, "Leaftide Event Portal", 70001, 0xA9B40001, true, 30.0, 40.01, BuiltIn(), T0) == CustomScanResult.Updated, "seen again a couple of metres off: the same one");
        Check(s.For(Ilt).Count == 1, "one saved");
        Check(Math.Abs(s.For(Ilt)[0].Ew - 40.01) < 1e-9, "the live spot replaces the first sighting");
        Check(s.Saw(Ilt, "Portal to Holtburg", 0, 0xA9B40001, true, 10.0, 20.01, BuiltIn(), T0) == CustomScanResult.BuiltIn, "portals.tsv already routes the Holtburg portal");
        Check(s.Saw(Ilt, "Mystery Portal", 0, 0xA9B40001, true, 10.01, 20.0, BuiltIn(), T0) == CustomScanResult.BuiltIn, "any portal right on a portals.tsv one is that one");
        Check(s.Saw(Ilt, "Dungeon Portal", 0, 0x01D90123, false, 0, 0, BuiltIn(), T0) == CustomScanResult.OffMap, "a portal inside a dungeon isn't mapped");
        Check(s.For(Ilt).Count == 1 && s.Links(Ilt).Count == 0, "scanned only: no landing yet, so no route link");
    }

    private static void CustomLandingMakesARouteLink()
    {
        var s = new CustomPortalStore();
        s.Saw(Ilt, "Leaftide Event Portal", 70001, 0xA9B40001, true, 30.0, 40.0, BuiltIn(), T0);
        CustomPortal? p = s.Learned(Ilt, "Leaftide Event Portal", 70001, 0xA9B40001, 30.0, 40.0, true, -12.346, 7.891, 0x7D640012, BuiltIn(), T0);
        Check(p != null && p.Routable, "a landing on the map: routable");
        List<PortalLink> links = s.Links(Ilt);
        Check(links.Count == 1 && links[0].Name == "Leaftide Event Portal", "one route link");
        Near(links[0].DstNs, -12.35, 1e-9, "landing rounded to the hundredth");

        CustomPortal? d = s.Learned(Ilt, "Hidden Vault Portal", 0, 0xA9B40001, 31.0, 41.0, false, 0, 0, 0x01D90123, BuiltIn(), T0);
        Check(d != null && d.DestKnown && !d.Routable, "an unscanned portal into a dungeon: added and kept, not routed");
        Check(s.Links(Ilt).Count == 1, "still one route link");
        Check(s.Learned(Ilt, "Portal to Holtburg", 0, 0xA9B40001, 10.0, 20.0, true, 50, 60, 0, BuiltIn(), T0) == null, "a portals.tsv portal isn't learned again");
    }

    private static void CustomPortalsArePerServer()
    {
        var s = new CustomPortalStore();
        s.Learned(Ilt, "Leaftide Event Portal", 0, 0xA9B40001, 30.0, 40.0, true, 1, 2, 0, null, T0);
        Check(s.Links(Ilt).Count == 1, "on its own server");
        Check(s.Links(Other).Count == 0 && s.For(Other).Count == 0, "never on another server");
        Check(s.Links("infinite leaftide").Count == 1, "the server name is matched without case");
    }

    private static void CustomPortalRoutesATrip()
    {
        // Far apart on foot; the custom portal stands beside the start and lands beside the goal.
        var s = new CustomPortalStore();
        s.Learned(Ilt, "Leaftide Express", 0, 0, 0.0, 0.05, true, 40.0, 40.0, 0, null, T0);
        var portals = new List<PortalLink>(BuiltIn());
        portals.AddRange(s.Links(Ilt));
        List<RouteStep> steps = PortalRoute.Plan(portals, null, 0, 0, 40.1, 40.0, out double est, out int used);
        Check(used == 1 && steps.Count >= 2 && steps[0].UsePortal && steps[0].Label == "Leaftide Express", "the plan takes the custom portal");
        Check(est < NavCoords.Distance(0, 0, 40.1, 40.0) / 4, "and it's far cheaper than the walk");
        List<RouteStep> without = PortalRoute.Plan(BuiltIn(), null, 0, 0, 40.1, 40.0, out _, out int usedWithout);
        Check(usedWithout == 0 || without.TrueForAll(x => x.Label != "Leaftide Express"), "another server's plan never has it");
    }

    private static void CustomPortalsRoundTrip()
    {
        var s = new CustomPortalStore();
        s.Saw(Ilt, "Scanned Only", 123, 0xA9B40001, true, 30.5, 40.25, null, T0);
        s.Learned(Ilt, "Leaftide Event Portal", 70001, 0xA9B40001, 30.0, 40.0, true, -12.34, 7.89, 0x7D640012, null, T0);
        s.Learned(Other, "Vault", 0, 0xA9B40001, 31.0, 41.0, false, 0, 0, 0x01D90123, null, T0);
        var back = CustomPortalStore.Parse(s.Serialize());
        Check(back.For(Ilt).Count == 2 && back.For(Other).Count == 1, "both servers' portals come back");
        Check(back.Links(Ilt).Count == 1 && back.Links(Other).Count == 0, "the same route links");
        CustomPortal p = back.For(Ilt)[1];
        Check(p.Name == "Leaftide Event Portal" && p.Wcid == 70001 && p.Cell == 0xA9B40001 && p.DestCell == 0x7D640012, "name, wcid and cells");
        Near(p.DestNs, -12.34, 1e-9, "landing NS");
        Check(p.LearnedUtc == T0 && back.For(Ilt)[0].LearnedUtc == default, "learned time, and none for the scanned one");
        Check(!back.For(Ilt)[0].DestKnown, "the scanned one has no landing");
        Check(CustomPortalStore.Parse("# comment\nbroken line\n\t\t\n").For(Ilt).Count == 0, "junk lines are skipped");
    }

    private static void CustomForgetAndClear()
    {
        var s = new CustomPortalStore();
        s.Saw(Ilt, "Event Portal A", 0, 0, true, 1, 1, null, T0);
        s.Saw(Ilt, "Event Portal B", 0, 0, true, 5, 5, null, T0);
        s.Saw(Ilt, "Vault", 0, 0, true, 9, 9, null, T0);
        s.Saw(Other, "Event Portal C", 0, 0, true, 1, 1, null, T0);
        int v = s.Version;
        Check(s.Forget(Ilt, "event") == 2 && s.For(Ilt).Count == 1, "forget by part of the name");
        Check(s.Version > v, "a change bumps the version (saved)");
        Check(s.For(Other).Count == 1, "another server's untouched");
        Check(s.Clear(Ilt) == 1 && s.For(Ilt).Count == 0, "clear the server");
    }
}
