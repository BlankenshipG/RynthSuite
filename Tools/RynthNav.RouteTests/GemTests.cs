using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthNav;
using RynthNav.Routing;

namespace RynthNav.RouteTests;

// Portal gems in the pack (PackGems.cs, 2026-10-29): a gem is a recall the planner may take, landing
// where it was learned to, where its personal recall goes, or where the Atlas's recall of its spell goes.
internal static partial class Program
{
    private static void RunGems()
    {
        Run(nameof(GemLandingsFromEverySource), GemLandingsFromEverySource);
        Run(nameof(GemCostsMoreThanTheSpell), GemCostsMoreThanTheSpell);
        Run(nameof(GemTakenWhenItSavesWalking), GemTakenWhenItSavesWalking);
        Run(nameof(GemMemoryRoundTrip), GemMemoryRoundTrip);
        Run(nameof(GemLooksLikeTeleport), GemLooksLikeTeleport);
    }

    private static readonly Atlas GemAtlas = Atlas.Parse(@"{""locations"":[],""recalls"":[
        {""spell"":2023,""name"":""Aerlinthe Recall"",""ns"":40.0,""ew"":-40.0},
        {""spell"":2931,""name"":""Deep Vault Recall"",""in"":""a dungeon""}]}");

    private static PackGem Gem(uint wcid, string name, int spell, int count = 1) => new() { Id = 0x80000000 + wcid, Wcid = wcid, Name = name, SpellId = spell, Count = count };

    private static void GemLandingsFromEverySource()
    {
        var mem = new RecallMemory();
        mem.Set("ILT|Kelp", RecallSlot.Lifestone, new RecallSpot { OnMap = true, Ns = 10, Ew = 10, Label = "Holtburg" });
        var learned = new GemMemory();
        learned.Set(Ilt, 900, "Leaftide Town Gem", new RecallSpot { OnMap = true, Ns = -5, Ew = 5, Label = "Leaftide" });
        var gems = new List<PackGem>
        {
            Gem(900, "Leaftide Town Gem", 0, 3),
            Gem(901, "Gem of Lifestone Recall", RecallPlanner.LifestoneRecall),
            Gem(902, "Aerlinthe Gem", 2023, 2),
            Gem(903, "Vault Gem", 2931),
            Gem(904, "Mystery Gem", 9999),
            Gem(902, "Aerlinthe Gem", 2023, 5),   // a second stack: one option
        };
        var notes = new List<string>();
        List<RecallOption> o = GemPlanner.Build(gems, GemAtlas, mem, "ILT|Kelp", learned, Ilt, 240, notes);
        Check(o.Count == 3, $"learned, personal and Atlas gems usable ({o.Count})");
        Check(o.Exists(x => x.ItemWcid == 900 && x.Ns == -5 && x.Via == "learned" && x.ItemCount == 3), "a learned gem");
        Check(o.Exists(x => x.ItemWcid == 901 && x.Ns == 10), "a lifestone recall gem lands at your lifestone tie");
        Check(o.Exists(x => x.ItemWcid == 902 && x.Ns == 40 && x.Via == "Aerlinthe Recall"), "an Aerlinthe gem lands where the recall does");
        Check(o.TrueForAll(x => x.IsGem && x.SpellId == 0 && x.Command.StartsWith("/mt usei ")), "used, not cast; saved routes use /mt usei");
        Check(notes.Exists(n => n.Contains("Vault Gem") && n.Contains("dungeon")), "a dungeon gem is left out, with a note");
        Check(notes.Exists(n => n.Contains("Mystery Gem") && n.Contains("use one once")), "an unknown gem says how to learn it");
        Check(GemPlanner.Build(gems, GemAtlas, mem, "ILT|Kelp", learned, Other, 240, null).Count == 2, "learned landings are per server");
    }

    private static void GemCostsMoreThanTheSpell()
    {
        var spell = new RecallOption { Name = "Aerlinthe Recall", SpellId = 2023, Ns = 40, Ew = -40, CastSeconds = RecallPlanner.SpellCastSeconds };
        RecallOption gem = GemPlanner.Build(new List<PackGem> { Gem(902, "Aerlinthe Gem", 2023) }, GemAtlas, new RecallMemory(), "c", new GemMemory(), Ilt, 240, null)[0];
        var links = new List<RecallLink> { gem.ToLink(), spell.ToLink() };
        List<RouteStep> steps = PortalRoute.Plan(new List<PortalLink>(), links, 0, 0, 40.1, -40, out _, out _);
        Check(steps.Count > 0 && steps[0].UseRecall && steps[0].RecallIndex == 1, "the spell, which spends nothing, beats the gem to the same place");
        Check(gem.ToLink().CostUnits > spell.ToLink().CostUnits, "the gem's cost includes what spending it counts as");
    }

    private static void GemTakenWhenItSavesWalking()
    {
        RecallOption gem = GemPlanner.Build(new List<PackGem> { Gem(902, "Aerlinthe Gem", 2023) }, GemAtlas, new RecallMemory(), "c", new GemMemory(), Ilt, 240, null)[0];
        List<RouteStep> far = PortalRoute.Plan(new List<PortalLink>(), new List<RecallLink> { gem.ToLink() }, 0, 0, 40.1, -40, out _, out int used);
        Check(used == 1 && far[0].UseRecall && far[0].Label == "Aerlinthe Gem (gem)", "far away: the gem");
        PortalRoute.Plan(new List<PortalLink>(), new List<RecallLink> { gem.ToLink() }, 40.5, -40, 40.1, -40, out _, out int near);
        Check(near == 0, "a short walk: the gem is kept");
    }

    private static void GemMemoryRoundTrip()
    {
        var m = new GemMemory();
        m.Set(Ilt, 900, "Leaftide Town Gem", new RecallSpot { OnMap = true, Ns = -5.5, Ew = 5.25, Label = "Leaftide" });
        m.Set(Other, 77, "Vault Gem", new RecallSpot { OnMap = false, Label = "a dungeon (landblock 01D9)" });
        GemMemory back = GemMemory.Parse(m.Serialize());
        Check(back.Count == 2, "both come back");
        RecallSpot? a = back.Get("infinite leaftide", 900);
        Check(a != null && a.OnMap && a.Ns == -5.5 && a.Ew == 5.25 && a.Label == "Leaftide", "a map landing, server matched without case");
        Check(back.Get(Other, 77) is { OnMap: false }, "a dungeon landing");
        Check(back.Forget(Ilt, 900) && back.Get(Ilt, 900) == null, "forget");
    }

    private static void GemLooksLikeTeleport()
    {
        var none = new GemMemory();
        Check(GemPlanner.LooksLikeTeleportItem(GemPlanner.ItemTypeGem, 1234, "Shiny Thing", GemAtlas, none, Ilt, 1), "a gem with a use spell");
        Check(GemPlanner.LooksLikeTeleportItem(0, 2023, "Odd Stone", GemAtlas, none, Ilt, 2), "anything casting an Atlas recall");
        Check(GemPlanner.LooksLikeTeleportItem(0, 5555, "Portal Stone to Leaftide", GemAtlas, none, Ilt, 3), "a portal-named item with a spell");
        Check(!GemPlanner.LooksLikeTeleportItem(0, 5555, "Mana Stone", GemAtlas, none, Ilt, 4), "not a mana stone");
        Check(!GemPlanner.LooksLikeTeleportItem(GemPlanner.ItemTypeGem, 0, "Diamond", GemAtlas, none, Ilt, 5), "not a plain gem with no spell");
    }
}
