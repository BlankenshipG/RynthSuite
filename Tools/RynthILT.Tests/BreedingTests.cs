using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RynthCore.Plugin.RynthILT;
using RynthCore.Plugin.RynthILT.Hub;
using RynthCore.Plugin.Shared;
using RynthCore.RynthAiTests.Harness;

namespace RynthILT.Tests;

// The pet breeding tracker (IltBreedingRules / IltBreedingEssence / IltBreedingSupplies / IltPetBreeding):
// the server's rules and where each value came from, the parsers for ACECustom's appraisal block,
// chat and "@breed-debug" (formats copied from PetDevice_Breeding.cs / PetDevice_Maturity.cs), each
// breeding gate, the pair planner's odds, the supply refusals, and the JSON RynthOracle reads.
internal static class BreedingTests
{
    public static void Register(Runner r)
    {
        r.Add("breeding: tier table has one WCID per tier in every family, no repeats", TierTable);
        r.Add("breeding: rules come from the server, else the ILT profile, else ACE", RuleSources);
        r.Add("breeding: stat mutation odds follow ACE's decay, floor and incense cap", MutationOdds);
        r.Add("breeding: appraisal text gives the stud's charges, refill window and growth", AppraisalParse);
        r.Add("breeding: chat lines give charges, the guardian, growth stages and events", ChatParse);
        r.Add("breeding: @breed-debug reply is captured whole or line by line and parsed", DebugParse);
        r.Add("breeding: each gate (charges, rest, growth, neutered, shiny, bond, tier, ID)", Gates);
        r.Add("breeding: pair planner blocks wrong sexes and ranges the inherited mutations", Planner);
        r.Add("breeding: supplies are refused where the server would refuse them", Supplies);
        r.Add("breeding: reader JSON round-trips through RynthOracle's view", ReaderJson);
        r.Add("breeding: saved state survives the hub.json source-generated serializer", StateJson);
        r.Add("breeding: RynthILT.Data v2 keeps v1 readers working", DataApiVersion);
    }

    private static IltBreedingRules Rules() => new(new IltBreedingState());

    /// <summary>A healthy adult essence's facts (tier 150, bond 120, appraised).</summary>
    private static BreedEssenceFacts Facts(int id, bool male, uint wcid = 48946) => new()
    {
        Id = id,
        Name = (male ? "Stud " : "Dam ") + id,
        Wcid = wcid,
        HasId = true,
        Bond = 120,
        MaleOverride = male,
    };

    private static void TierTable()
    {
        var seen = new HashSet<uint>();
        foreach (uint[] row in IltBreedingEssence.Families)
        {
            Check.Eq(row.Length, IltBreedingEssence.Tiers.Length, "row " + row[0]);
            foreach (uint w in row) Check.True(seen.Add(w), "WCID " + w + " appears once");
        }
        Check.Eq(IltBreedingEssence.Families.Length, 36, "36 combat pet families");
        Check.Eq(IltBreedingEssence.TierOf(48942), 50, "first fire skeleton is tier 50");
        Check.Eq(IltBreedingEssence.TierOf(49212), 200, "an out-of-run WCID keeps its slot (tier 200)");
        Check.Eq(IltBreedingEssence.TierOf(787801072), 300, "the last tier-300 essence");
        Check.Eq(IltBreedingEssence.TierOf(12345, 125), 125, "unknown WCID: a tier-valued level requirement");
        Check.Eq(IltBreedingEssence.TierOf(12345, 126), 0, "unknown WCID, odd level: unknown tier");
        Check.True(IltBreedingEssence.IsCombatEssence(49551), "range end is included");
    }

    private static void RuleSources()
    {
        IltBreedingRules r = Rules();
        Check.Eq(r.Value(IltBreedingRules.MinTier).Source, BreedRuleSource.AceDefault, "min tier: ACE default");
        Check.Eq(r.GetInt(IltBreedingRules.MinTier), 100, "min tier 100");
        Check.Eq(r.Value(IltBreedingRules.MutationDecay).Source, BreedRuleSource.IltProfile, "decay: ILT profile");
        Check.Near(r.Get(IltBreedingRules.MutationDecay), 0.1, 1e-9, "ILT decay 0.1");
        Check.Near(r.Get(IltBreedingRules.GuardianEnabled), 1, 1e-9, "ILT runs the mating guardian");

        DateTime t = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        Check.True(r.Learn(IltBreedingRules.MaleCharges, 12, t), "a new value is a change");
        Check.False(r.Learn(IltBreedingRules.MaleCharges, 12, t), "the same value again isn't");
        Check.Eq(r.Value(IltBreedingRules.MaleCharges).Source, BreedRuleSource.Server, "learned: seen on server");
        Check.Eq(r.GetInt(IltBreedingRules.MaleCharges), 12, "learned value wins");
        Check.False(r.Learn("not_a_rule", 1, t), "unknown keys are ignored");
        Check.Eq(r.RoomText(), "landblock 0x0106 variant 2", "Seedy Motel room");

        IltBreedingRules frozen = r.Frozen();
        r.Learn(IltBreedingRules.MaleCharges, 8, t);
        Check.Eq(frozen.GetInt(IltBreedingRules.MaleCharges), 12, "a frozen copy doesn't follow later learning");

        Check.Eq(r.StageName(2), "Whelp", "default stage names");
        Check.True(r.LearnStageName(2, "Pup"), "a server's own stage name");
        Check.Eq(r.StageName(2), "Pup", "seen name wins");
        for (int i = 0; i < IltBreedingRules.MaxEvents + 5; i++) r.AddEvent("x", "e" + i, t);
        Check.Eq(r.State.Log.Count, IltBreedingRules.MaxEvents, "event log is capped");
        Check.Eq(r.State.Log[^1].Text, "e" + (IltBreedingRules.MaxEvents + 4), "newest kept last");
    }

    private static void MutationOdds()
    {
        IltBreedingRules r = Rules();
        Check.Near(r.StatMutationChance(0, 0), 0.05, 1e-9, "base 5%");
        Check.Near(r.StatMutationChance(4, 0), 0.05 / 1.4, 1e-9, "decay 0.1 x 4 mutations");
        Check.Near(r.StatMutationChance(0, 0.10), 0.15, 1e-9, "exquisite incense adds 10%");
        Check.Near(r.CombinedIncense(0.4, 0.3), 0.5, 1e-9, "both parents' incense capped at 50%");
        r.Learn(IltBreedingRules.MutationDecay, 10, DateTime.UtcNow);
        Check.Near(r.StatMutationChance(4, 0), 0.02, 1e-9, "never below the 2% floor");
    }

    private static void AppraisalParse()
    {
        double now = 1_800_000_000;
        double lastRefill = now - (20 * 3600 + 50 * 60);
        string desc = "A fire skeleton essence.\nSex: Male (9/12 breeding charges today) - refills in 3h 10m\nBond: Unbound (imprints on first summon)";
        var seen = IltBreedingRules.FromAppraisal(desc, lastRefill, now);
        Check.True(seen.Contains((IltBreedingRules.MaleCharges, 12)), "max charges from the stud line");
        Check.True(seen.Contains((IltBreedingRules.MaleRefillHours, 24)), "refill window from the countdown");

        string growth = "Sex: Female (ready to breed)\nGrowth: Whelp 2/5 - 75 kills to Juvenile, 195 to Adult";
        Check.True(IltBreedingRules.FromAppraisal(growth, 0, now).Contains((IltBreedingRules.MaturityStages, 5)), "stage count");
        Check.True(IltBreedingRules.TryParseGrowth(growth, out string name, out int stage, out int stages, out int toNext, out string next, out int toAdult), "growth line");
        Check.Eq(name, "Whelp", "stage name");
        Check.Eq(stage, 2, "stage");
        Check.Eq(stages, 5, "stages");
        Check.Eq(toNext, 75, "to next");
        Check.Eq(next, "Juvenile", "next stage");
        Check.Eq(toAdult, 195, "to adult");

        Check.True(IltBreedingRules.TryParseGrowth("Growth: Young Adult 5/5 - 60 kills to Adult", out name, out stage, out _, out toNext, out next, out toAdult), "last stage");
        Check.Eq(name, "Young Adult", "two-word stage name");
        Check.Eq(next, "Adult", "last stage leads to Adult");
        Check.Eq(toAdult, 60, "last stage: to adult = to next");
        Check.False(IltBreedingRules.TryParseGrowth("Growth: Adult", out _, out _, out _, out _, out _, out _), "an adult has no countdown");
    }

    private static void ChatParse()
    {
        var a = IltBreedingRules.FromChat("[Breeding] Fire Skeleton Essence spent a breeding charge: 7/12 left today.");
        Check.True(a.Contains((IltBreedingRules.MaleCharges, 12)), "charge line gives the max");
        var g = IltBreedingRules.FromChat("The union stirs something... Mating Spirit rises before A and B! Only the two parents can harm it. They have 120s to bring it down together.");
        Check.True(g.Contains((IltBreedingRules.GuardianEnabled, 1)), "guardian on");
        Check.True(g.Contains((IltBreedingRules.GuardianSeconds, 120)), "guardian timer");
        var d = IltBreedingRules.FromChat("[Breeding] Your pet performs the courtship dance, waiting for a partner... (Your partner must have their pet summoned in the same room and *dance* within 5 seconds).");
        Check.True(d.Contains((IltBreedingRules.DanceWindow, 5)), "dance window");
        Check.Eq(IltBreedingRules.FromChat("You say, \"hello\"").Count, 0, "ordinary chat says nothing");

        Check.True(IltBreedingRules.TryParseGrownInto("Pup has grown into a Whelp! Its body swells with new strength. (2/5)", out int stage, out string name), "growth line");
        Check.Eq(stage, 2, "stage number");
        Check.Eq(name, "Whelp", "stage name");

        Check.Eq(IltPetBreeding.EventKind("The mating ritual has begun between A and B..."), "ritual", "ritual");
        Check.Eq(IltPetBreeding.EventKind("Congratulations! A baby pet has been born: Pup! Placed in Bob's inventory."), "birth", "birth");
        Check.Eq(IltPetBreeding.EventKind("A baby pet has been born: Pup! Bob's packs are full, so the baby has been tucked away"), "birth", "deferred birth");
        Check.Eq(IltPetBreeding.EventKind("Pup has reached adulthood! It stands at its full size"), "adult", "adulthood");
        Check.Eq(IltPetBreeding.EventKind("Pup has imprinted on you. It is now bound to this character"), "imprint", "imprint");
        Check.Eq(IltPetBreeding.EventKind("[Breeding] The spectral guardian dissolves back into the ether..."), "guardian", "guardian lost");
        Check.Eq(IltPetBreeding.EventKind("Bob tells you, \"breed?\""), null, "not a breeding line");
    }

    private const string DebugReply =
        "=== PET BREEDING DIAGNOSTICS ===\n" +
        "Breeding System Enabled: True\n" +
        "Your Location: Landblock=0x0106, Cell=0x01060186, Variant=2\n" +
        "Allowed Target: whole landblock 0x106, Variant=2 => Location Match: VALID (Room OK)\n" +
        "Your Active Pet: Fire Skeleton (WCID 48900) | Device: Fire Skeleton Essence\n" +
        "  Sex: Male (9/10 charges)\n" +
        "Eligible breeding partners nearby: 1\n" +
        "================================\n";

    private static void DebugParse()
    {
        Check.True(IltBreedingRules.IsDebugLine(DebugReply.TrimEnd('\n')), "whole message is a reply line");
        Check.True(IltBreedingRules.IsDebugEnd(DebugReply.TrimEnd('\n')), "whole message ends the reply");
        Check.True(IltBreedingRules.IsDebugEnd("================================"), "rule line alone ends it");
        Check.False(IltBreedingRules.IsDebugEnd("=== PET BREEDING DIAGNOSTICS ==="), "the header doesn't");
        Check.False(IltBreedingRules.IsDebugLine("Bob says, \"hi\""), "chat isn't");

        BreedDebugInfo whole = IltBreedingRules.ParseDebug(new[] { DebugReply });
        BreedDebugInfo split = IltBreedingRules.ParseDebug(DebugReply.Split('\n'));
        foreach (BreedDebugInfo d in new[] { whole, split })
        {
            Check.Eq(d.Enabled, (bool?)true, "enabled");
            Check.Eq(d.AllowedLandblock, 0x106u, "landblock");
            Check.Eq(d.AllowedVariant, 2, "variant");
            Check.Eq(d.AllowedKind, "whole landblock", "kind");
            Check.Eq(d.LocationOk, (bool?)true, "in the room");
            Check.Eq(d.Partners, 1, "partners");
            Check.Eq(d.PetSex, "Male (9/10 charges)", "pet sex line");
        }
        var rules = IltBreedingRules.FromChat(DebugReply);
        Check.True(rules.Contains((IltBreedingRules.Landblock, 0x106)), "room landblock learned");
        Check.True(rules.Contains((IltBreedingRules.Variant, 2)), "room variant learned");
    }

    private static void Gates()
    {
        IltBreedingRules r = Rules();
        double now = 1_800_000_000;

        BreedEssence full = IltBreedingEssence.Evaluate(Facts(1, true), r, now);
        Check.True(full.Ready, "a stud that never bred is full");
        Check.Eq(full.Charges, 10, "10 charges");
        Check.Eq(full.Tier, 125, "48946 is tier 125");

        var spent = Facts(2, true);
        spent.MaleCharges = 0;
        spent.MaleRefreshUnix = now - 2 * 3600;
        BreedEssence noCharge = IltBreedingEssence.Evaluate(spent, r, now);
        Check.Eq(noCharge.Gate, BreedGate.NoCharges, "no charges");
        Check.Near(noCharge.ReadyAtUnix, now + 22 * 3600, 1, "refills 24h after the last refill");

        spent.MaleRefreshUnix = now - 25 * 3600;
        Check.True(IltBreedingEssence.Evaluate(spent, r, now).Ready, "refilled once the window passed (the server refills on read)");

        var dam = Facts(3, false);
        dam.NextBreedUnix = now + 3600 + 300;
        BreedEssence resting = IltBreedingEssence.Evaluate(dam, r, now);
        Check.Eq(resting.Gate, BreedGate.Resting, "resting dam");
        Check.Eq(resting.Status, "Rest 1h 05m", "rest countdown");

        var neut = Facts(4, false);
        neut.Neutered = true;
        Check.Eq(IltBreedingEssence.Evaluate(neut, r, now).Gate, BreedGate.Neutered, "neutered");

        var shiny = Facts(5, true);
        shiny.LongDesc = "Sex: Male (shiny - cannot breed)";
        Check.Eq(IltBreedingEssence.Evaluate(shiny, r, now).Gate, BreedGate.Shiny, "shiny");

        var baby = Facts(6, false);
        baby.JuvenileFlag = true;
        baby.MaturityKills = 130;
        BreedEssence growing = IltBreedingEssence.Evaluate(baby, r, now);
        Check.Eq(growing.Gate, BreedGate.Growing, "juvenile");
        Check.Eq(growing.Stage, 3, "130 kills of 300 over 5 stages: stage 3");
        Check.Eq(growing.StageName, "Juvenile", "stage 3 name");
        Check.Eq(growing.KillsToNext, 50, "50 kills to stage 4");

        var weak = Facts(7, true);
        weak.Bond = 40;
        Check.Eq(IltBreedingEssence.Evaluate(weak, r, now).Gate, BreedGate.LowBond, "bond under 100");

        BreedEssence low = IltBreedingEssence.Evaluate(Facts(8, true, 48944), r, now);
        Check.Eq(low.Gate, BreedGate.LowTier, "tier 80 is under the minimum");

        var unread = Facts(9, true);
        unread.HasId = false;
        Check.Eq(IltBreedingEssence.Evaluate(unread, r, now).Gate, BreedGate.NeedsId, "not appraised yet");

        var boosted = Facts(10, false);
        boosted.Incense = 0.05;
        boosted.Catalyst = true;
        BreedEssence b = IltBreedingEssence.Evaluate(boosted, r, now);
        Check.Eq(string.Join("|", b.Boosters), "Refined incense +5%|Chromatic Catalyst", "boosters listed");
    }

    private static void Planner()
    {
        IltBreedingRules r = Rules();
        double now = 1_800_000_000;
        var sf = Facts(1, true);
        sf.MutDamage = 2;
        sf.MutCrit = 1;
        sf.Incense = 0.10;
        var df = Facts(2, false);
        df.MutDamage = 0;
        df.MutCrit = 3;
        BreedEssence stud = IltBreedingEssence.Evaluate(sf, r, now), dam = IltBreedingEssence.Evaluate(df, r, now);

        BreedPairPlan p = IltBreedingPlanner.Plan(stud, dam, r);
        Check.True(p.CanBreed, "both ready");
        Check.Eq(p.InheritMin, 1, "lower lines: damage 0 + crit 1");
        Check.Eq(p.InheritMax, 5, "higher lines: damage 2 + crit 3");
        Check.Near(p.ChanceBest, 0.15 / 1.1, 1e-9, "best: 1 mutation, incense 10%");
        Check.Near(p.ChanceWorst, 0.15 / 1.5, 1e-9, "worst: 5 mutations");
        Check.Near(p.PotencyChance, 0.03, 1e-9, "potency 3%");

        BreedPairPlan wrong = IltBreedingPlanner.Plan(dam, stud, r);
        Check.False(wrong.CanBreed, "swapped sexes");
        Check.True(wrong.Blockers.Any(b => b.Contains("must be male")), "says the stud must be male");
    }

    private static void Supplies()
    {
        IltBreedingRules r = Rules();
        double now = 1_800_000_000;
        BreedSupplyDef lesser = IltBreedingSupplies.Find(IltBreedingSupplies.LesserIncense)!;
        BreedSupplyDef exquisite = IltBreedingSupplies.Find(IltBreedingSupplies.ExquisiteIncense)!;
        BreedSupplyDef draught = IltBreedingSupplies.Find(IltBreedingSupplies.Draught)!;
        BreedSupplyDef offering = IltBreedingSupplies.Find(IltBreedingSupplies.Offering)!;
        BreedSupplyDef neuter = IltBreedingSupplies.Find(IltBreedingSupplies.NeuteringKit)!;

        var f = Facts(1, false);
        f.Incense = 0.05;
        BreedEssence primed = IltBreedingEssence.Evaluate(f, r, now);
        Check.NotNull(IltBreedingSupplies.RefuseReason(lesser, primed, r), "weaker incense refused");
        Check.Null(IltBreedingSupplies.RefuseReason(exquisite, primed, r), "stronger incense taken");
        Check.True(IltBreedingSupplies.Warning(exquisite, primed).Contains("+5%"), "and warns the +5% is lost");

        var n = Facts(2, false);
        n.Neutered = true;
        Check.NotNull(IltBreedingSupplies.RefuseReason(lesser, IltBreedingEssence.Evaluate(n, r, now), r), "no incense on a neutered essence");

        BreedEssence adult = IltBreedingEssence.Evaluate(Facts(3, true), r, now);
        Check.NotNull(IltBreedingSupplies.RefuseReason(draught, adult, r), "draught: juveniles only");
        var j = Facts(4, true);
        j.JuvenileFlag = true;
        j.MaturityKills = 0;
        Check.Null(IltBreedingSupplies.RefuseReason(draught, IltBreedingEssence.Evaluate(j, r, now), r), "draught on a juvenile");
        j.MaturityXpMult = 2.0;
        Check.NotNull(IltBreedingSupplies.RefuseReason(draught, IltBreedingEssence.Evaluate(j, r, now), r), "not twice");

        Check.Null(IltBreedingSupplies.RefuseReason(offering, adult, r), "offering with the guardian on");
        r.Learn(IltBreedingRules.GuardianEnabled, 0, DateTime.UtcNow);
        Check.NotNull(IltBreedingSupplies.RefuseReason(offering, adult, r), "offering refused with the guardian off");
        Check.NotNull(IltBreedingSupplies.RefuseReason(neuter, adult, r), "the neutering kit is never used from here");
        Check.Eq(IltBreedingSupplies.PriceText(1_250_000), "1.25M", "price text");
    }

    private static void ReaderJson()
    {
        IltBreedingRules r = Rules();
        double now = 1_800_000_000;
        var stud = IltBreedingEssence.Evaluate(Facts(11, true), r, now);
        var dam = IltBreedingEssence.Evaluate(Facts(12, false), r, now);
        var events = new[] { new IltBreedingEvent { Utc = new DateTime(2026, 10, 15, 20, 0, 0, DateTimeKind.Utc), Kind = "birth", Text = "A \"baby\" was born" } };
        var supplies = IltBreedingSupplies.All.Select(s => (s, s.Key == IltBreedingSupplies.RefinedIncense ? 2L : 0L)).ToList();
        var snap = new BreedingSnapshot(new[] { stud, dam }, supplies, r.All(), events,
            IltBreedingRules.ParseDebug(new[] { DebugReply }), DateTime.UtcNow, IltTri.On, IltTri.On, IltTri.On,
            r.RoomText(), 5, "hello", now, true, r.Frozen());

        string json = IltPetBreeding.BuildJson(snap, r, "InfiniteLeaftide");
        using (JsonDocument.Parse(json)) { }   // well-formed
        IltBreedingView v = IltBreedingView.Parse(json);
        Check.True(v.Loaded && v.Available, "loaded");
        Check.Eq(v.Enabled, "on", "enabled");
        Check.Eq(v.Room, "landblock 0x0106 variant 2", "room");
        Check.Eq(v.FreeSlots, 5, "free slots");
        Check.Eq(v.Essences.Count, 2, "two essences");
        Check.True(v.Essences[0].Male && v.Essences[0].Ready, "stud row");
        Check.Eq(v.Pairs.Count, 1, "one ready pairing");
        Check.Near(v.Pairs[0].ChanceBest, 0.05, 1e-6, "pair odds");
        IltBreedSupplyRow refined = v.Supplies.First(s => s.Key == IltBreedingSupplies.RefinedIncense);
        Check.Eq(refined.Count, 2L, "supply count");
        Check.Eq(refined.Targets.Count, 2, "both essences can take it");
        Check.Eq(v.Supplies.First(s => s.Key == IltBreedingSupplies.LesserIncense).Targets.Count, 0, "none carried: no targets");
        Check.Eq(v.Rules.Count, IltBreedingRules.Defs.Length, "every rule");
        Check.Eq(v.Events[0].Text, "A \"baby\" was born", "quotes escaped and read back");
        Check.Eq(v.Debug?.Match, "valid", "diagnostics");
        Check.Eq(IltBreedingView.ApplyVerb(" incense2 ", 12), "apply incense2 12", "apply verb");
    }

    private static void StateJson()
    {
        var state = new IltHubState();
        var rules = new IltBreedingRules(state.Pet.Breeding);
        rules.Learn(IltBreedingRules.MaleCharges, 12, new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc));
        rules.LearnStageName(1, "Hatchling");
        rules.AddEvent("birth", "born", DateTime.UtcNow);
        string json = JsonSerializer.Serialize(state, IltHubJsonContext.Default.IltHubState);
        IltHubState back = JsonSerializer.Deserialize(json, IltHubJsonContext.Default.IltHubState)!;
        var again = new IltBreedingRules(back.Pet.Breeding);
        Check.Eq(again.GetInt(IltBreedingRules.MaleCharges), 12, "learned rule saved");
        Check.Eq(again.StageName(1), "Hatchling", "stage name saved");
        Check.Eq(back.Pet.Breeding.Log.Count, 1, "event saved");

        IltHubState old = JsonSerializer.Deserialize("{\"Pet\":{\"PetLog\":[\"a\"]}}", IltHubJsonContext.Default.IltHubState)!;
        Check.NotNull(old.Pet.Breeding, "a hub.json from before the tracker gets an empty breeding state");
    }

    private static unsafe void DataApiVersion()
    {
        // Version 3 (the Temple guardians) appends after the breeding pair; a v2 reader sees the same breeding layout.
        Check.Eq(RynthIltDataApiV1.InterfaceVersion, 3u, "version 3");
        Check.True(RynthIltDataApiV1.V1Size < RynthIltDataApiV1.V2Size && RynthIltDataApiV1.V2Size < (uint)sizeof(RynthIltDataApiV1),
                   "v2 appends to v1, v3 to v2");
        Check.Eq(RynthIltDataApiV1.V1Size, (uint)(8 + 4 * sizeof(IntPtr)), "v1 = two uints and four function pointers");
        Check.True(DataInterface.Query(RynthIltDataApiV1.InterfaceName, 1) != IntPtr.Zero, "v1 readers still get the table");
        Check.True(DataInterface.Query(RynthIltDataApiV1.InterfaceName, 2) != IntPtr.Zero, "v2 readers too");
        Check.True(DataInterface.Query(RynthIltDataApiV1.InterfaceName, 3) != IntPtr.Zero, "v3 readers too");
        Check.True(DataInterface.Query(RynthIltDataApiV1.InterfaceName, 4) == IntPtr.Zero, "a future version isn't claimed");
        var t = (RynthIltDataApiV1*)DataInterface.Query(RynthIltDataApiV1.InterfaceName, 1);
        Check.True(t->GetPetBreedingJson != null && t->PetBreedingCommand != null, "breeding functions filled");
        Check.Eq(RynthIltHub.SectionPetBreeding, "pets-breeding", "section id");
    }
}
