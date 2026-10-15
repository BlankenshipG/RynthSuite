// IltBreedingEssence.cs - One carried combat pet essence, as the breeding tracker sees it.
//
// The facts come from the essence's appraisal (ACECustom [AssessmentProperty] qualities: bond 9053,
// male charges 9057, next breeding time float 9056, incense float 9058, neutered bool 9051, the
// male override 50053, juvenile 50054, Chromatic Catalyst 50055, the guardian's Offering 50056,
// maturity kills 9077, the mutation lines 9070..9075) and its LongDesc breeding block ("Sex: ...",
// "Growth: ...", "(shiny - cannot breed)"). Evaluate turns them into what the player needs to know:
// stud or dam, every breeding gate that fails, when it clears, growth, and the boosters applied.
// The gates are ACE PetDevice_Breeding's, with the thresholds from IltBreedingRules.
//
// Pure: no host calls (IltPetBreeding reads the host), so RynthILT.Tests checks it offline.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RynthCore.Plugin.RynthILT.Hub;

/// <summary>A gate's outcome for one essence.</summary>
internal enum BreedGate { Ready, Resting, NoCharges, Growing, Neutered, Shiny, LowBond, LowTier, NeedsId }

/// <summary>What was read for one essence (host values; pump thread fills it).</summary>
internal sealed class BreedEssenceFacts
{
    public int Id;
    public string Name = string.Empty;
    public uint Wcid;
    public bool HasId;
    /// <summary>The essence's level requirement (a tier fallback for WCIDs outside the table).</summary>
    public int UseLevel;
    public int Bond;
    /// <summary>The server's explicit sex (Tailoring Kit), or null to derive it from the object id.</summary>
    public bool? MaleOverride;
    public bool Neutered;
    public bool JuvenileFlag;
    /// <summary>PetMaturityKills: -1 when absent (the essence was never bred).</summary>
    public int MaturityKills = -1;
    /// <summary>PetMaleBreedingCharges: -1 when absent (a full stud).</summary>
    public int MaleCharges = -1;
    public double MaleRefreshUnix;
    public double NextBreedUnix;
    public double Incense;
    public bool Catalyst;
    public bool Offering;
    public double MaturityXpMult;
    public int MutDamage, MutDamageResist, MutCrit, MutVitality, MutPotency, MutationCount;
    public int PotencyStored;
    public bool Attuned;
    public string LongDesc = string.Empty;
}

/// <summary>One essence evaluated for breeding. Immutable; the UI and the reader JSON read it.</summary>
internal sealed record BreedEssence(
    int Id, string Name, uint Wcid, bool HasId, int Tier, int Bond, bool Male, BreedGate Gate, string Status,
    IReadOnlyList<string> Problems, double ReadyAtUnix, int Charges, int ChargesMax, double RefillAtUnix,
    bool Bred, bool Juvenile, int Stage, int Stages, string StageName, int Kills, int KillsRequired, int KillsToNext,
    double Incense, bool Catalyst, bool Offering, bool Draught, IReadOnlyList<string> Boosters,
    int MutD, int MutDR, int MutC, int MutV, int MutP, bool Shiny, bool Unbound, int Potency)
{
    /// <summary>Every gate passes now.</summary>
    public bool Ready => Gate == BreedGate.Ready;
    /// <summary>The stat mutation lines (damage, damage resist, crit, vitality) summed.</summary>
    public int StatMutations => MutD + MutDR + MutC + MutV;
    /// <summary>"Stud" or "Dam".</summary>
    public string Role => Male ? "Stud" : "Dam";
}

internal static class IltBreedingEssence
{
    /// <summary>Essence tiers, in the order of each family row below (ACE PetDeviceWcids petLevelIndexes).</summary>
    public static readonly int[] Tiers = { 50, 80, 100, 125, 150, 180, 200, 250, 300 };

    // ACECustom PetDeviceWcids: one row per combat pet family, tiers 50 .. 300 in order. "a-b" is a
    // run of consecutive WCIDs. Rows copied from Source/ACE.Server/Factories/Tables/Wcids/PetDeviceWcids.cs.
    private static readonly string[] FamilyRows =
    {
        "48942,48944,48945,48946,48947,48948,48956,787801001,787801037",
        "49213-49219,787801002,787801038",
        "49220-49226,787801003,787801039",
        "49227-49232,49212,787801004,787801040",
        "48972,49234-49239,787801005,787801041",
        "49240-49246,787801006,787801042",
        "49247-49253,787801007,787801043",
        "49254-49259,49233,787801008,787801044",
        "49421-49427,787801009,787801045",
        "49428-49434,787801010,787801046",
        "49435-49441,787801011,787801047",
        "49442-49448,787801012,787801048",
        "48959,48961,48963,48965,48967,48969,48957,787801013,787801049",
        "49261-49267,787801014,787801050",
        "49268-49274,787801015,787801051",
        "49275-49280,49260,787801016,787801052",
        "49282-49287,49281,787801017,787801053",
        "49289-49294,49288,787801018,787801054",
        "49296-49301,49295,787801019,787801055",
        "49303-49308,49302,787801020,787801056",
        "49310-49316,787801021,787801057",
        "49317-49323,787801022,787801058",
        "49324-49330,787801023,787801059",
        "49331-49336,49309,787801024,787801060",
        "49338-49344,787801025,787801061",
        "49345-49351,787801026,787801062",
        "49352-49358,787801027,787801063",
        "49359-49364,49337,787801028,787801064",
        "49366-49372,787801029,787801065",
        "49373-49379,787801030,787801066",
        "49380-49386,787801031,787801067",
        "49387-49392,49365,787801032,787801068",
        "49524-49530,787801033,787801069",
        "49531-49537,787801034,787801070",
        "49538-49544,787801035,787801071",
        "49545-49551,787801036,787801072",
    };

    /// <summary>Each family row expanded (tests check every row has one WCID per tier).</summary>
    internal static readonly uint[][] Families = Array.ConvertAll(FamilyRows, ExpandRow);

    private static readonly Dictionary<uint, int> TierByWcid = BuildTiers();

    private static uint[] ExpandRow(string row)
    {
        var list = new List<uint>(9);
        foreach (string token in row.Split(','))
        {
            int dash = token.IndexOf('-');
            if (dash < 0) { list.Add(uint.Parse(token, CultureInfo.InvariantCulture)); continue; }
            uint a = uint.Parse(token.AsSpan(0, dash), NumberStyles.Integer, CultureInfo.InvariantCulture);
            uint b = uint.Parse(token.AsSpan(dash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture);
            for (uint w = a; w <= b; w++) list.Add(w);
        }
        return list.ToArray();
    }

    private static Dictionary<uint, int> BuildTiers()
    {
        var d = new Dictionary<uint, int>();
        foreach (uint[] row in Families)
            for (int i = 0; i < row.Length && i < Tiers.Length; i++)
                d[row[i]] = Tiers[i];
        return d;
    }

    /// <summary>True for a combat pet essence WCID (the only kind that breeds).</summary>
    public static bool IsCombatEssence(uint wcid) => TierByWcid.ContainsKey(wcid);

    /// <summary>The essence tier for a WCID, else the level requirement when it is a tier, else 0.</summary>
    public static int TierOf(uint wcid, int useLevel = 0)
    {
        if (TierByWcid.TryGetValue(wcid, out int t)) return t;
        return Array.IndexOf(Tiers, useLevel) >= 0 ? useLevel : 0;
    }

    /// <summary>ACE PetDevice.IsMale without an override: murmur3 fmix32 on the object id, even = male.</summary>
    public static bool DeriveIsMale(int objectId)
    {
        uint h = unchecked((uint)objectId);
        h ^= h >> 16;
        h = unchecked(h * 0x7feb352d);
        h ^= h >> 15;
        h = unchecked(h * 0x846ca68b);
        h ^= h >> 16;
        return (h & 1) == 0;
    }

    /// <summary>"3h 05m", "42m", "20s" (0 or less = "now").</summary>
    public static string Duration(double seconds)
    {
        if (seconds <= 0) return "now";
        long s = (long)Math.Ceiling(seconds);
        if (s >= 3600) return $"{s / 3600}h {s % 3600 / 60:00}m";
        if (s >= 60) return $"{s / 60}m";
        return $"{s}s";
    }

    /// <summary>
    /// Evaluates one essence: role, every failing gate (the first is the headline), the ready time,
    /// growth and boosters. <paramref name="nowUnix"/> is unix seconds.
    /// </summary>
    public static BreedEssence Evaluate(BreedEssenceFacts f, IltBreedingRules r, double nowUnix)
    {
        string desc = f.LongDesc ?? string.Empty;
        bool male = f.MaleOverride ?? DeriveIsMale(f.Id);
        int tier = TierOf(f.Wcid, f.UseLevel);
        int bond = Math.Max(1, f.Bond);
        bool shiny = desc.Contains("(shiny - cannot breed)", StringComparison.OrdinalIgnoreCase);
        bool bred = f.MaturityKills >= 0;
        bool juvenile = f.JuvenileFlag || desc.Contains("cannot breed yet", StringComparison.OrdinalIgnoreCase);

        // Stud charges refill lazily on the server: full again once the refill time has passed.
        int max = Math.Max(0, r.GetInt(IltBreedingRules.MaleCharges));
        int charges = f.MaleCharges < 0 ? max : Math.Min(f.MaleCharges, max);
        double refillAt = 0;
        double refillH = r.Get(IltBreedingRules.MaleRefillHours);
        if (male && f.MaleRefreshUnix > 0 && refillH > 0)
        {
            refillAt = f.MaleRefreshUnix + refillH * 3600.0;
            if (nowUnix >= refillAt) { charges = max; refillAt = 0; }
        }
        if (male && charges >= max) refillAt = 0;

        // Growth (bred essences only): stage from credited kills, as ACE PetDevice_Maturity.
        int required = Math.Max(1, r.GetInt(IltBreedingRules.MaturityKills));
        int stages = Math.Max(1, r.GetInt(IltBreedingRules.MaturityStages));
        int kills = Math.Max(0, f.MaturityKills);
        int perStage = Math.Max(1, (int)Math.Ceiling(required / (double)stages));
        int stage = juvenile ? Math.Min(stages, kills / perStage + 1) : stages + 1;
        int toNext = juvenile ? Math.Max(0, Math.Min(required, stage * perStage) - kills) : 0;
        string stageName = juvenile ? r.StageName(stage) : bred ? "Adult" : string.Empty;
        if (juvenile && IltBreedingRules.TryParseGrowth(desc, out string seenName, out int seenStage, out _, out int seenNext, out _, out int seenAdult))
        {
            // The appraisal line is the server's own math: prefer it when it is there.
            stage = seenStage;
            stageName = seenName;
            toNext = seenNext;
            kills = Math.Max(kills, required - seenAdult);
        }

        // Every failing gate, in the order the server checks them.
        var problems = new List<string>();
        BreedGate gate = BreedGate.Ready;
        void Fail(BreedGate g, string text)
        {
            if (gate == BreedGate.Ready) gate = g;
            problems.Add(text);
        }
        double readyAt = 0;
        if (!f.HasId) Fail(BreedGate.NeedsId, "not appraised yet");
        if (f.Neutered) Fail(BreedGate.Neutered, "neutered (permanent)");
        if (shiny) Fail(BreedGate.Shiny, "shiny essences can't breed");
        if (juvenile) Fail(BreedGate.Growing, $"still growing: {stageName} ({Math.Max(0, required - kills)} kills to Adult)");
        int minTier = r.GetInt(IltBreedingRules.MinTier);
        if (tier > 0 && tier < minTier) Fail(BreedGate.LowTier, $"tier {tier} (needs {minTier}+)");
        int minBond = r.GetInt(IltBreedingRules.MinBond);
        if (f.HasId && bond < minBond) Fail(BreedGate.LowBond, $"bond {bond}/{minBond}");
        if (male && charges <= 0)
        {
            Fail(BreedGate.NoCharges, refillAt > 0 ? $"no charges (refill in {Duration(refillAt - nowUnix)})" : "no charges left");
            readyAt = Math.Max(readyAt, refillAt);
        }
        if (!male && f.NextBreedUnix > nowUnix)
        {
            Fail(BreedGate.Resting, $"resting ({Duration(f.NextBreedUnix - nowUnix)})");
            readyAt = Math.Max(readyAt, f.NextBreedUnix);
        }

        string status = gate switch
        {
            BreedGate.Ready => male ? $"Ready ({charges}/{max})" : "Ready",
            BreedGate.Resting => "Rest " + Duration(f.NextBreedUnix - nowUnix),
            BreedGate.NoCharges => refillAt > 0 ? "0/" + max + " refill " + Duration(refillAt - nowUnix) : "0/" + max,
            BreedGate.Growing => $"{stageName} {Math.Min(stage, stages)}/{stages}",
            BreedGate.Neutered => "Neutered",
            BreedGate.Shiny => "Shiny",
            BreedGate.LowBond => $"Bond {bond}/{minBond}",
            BreedGate.LowTier => $"Tier {tier}",
            _ => "ID needed",
        };

        bool draught = f.MaturityXpMult > 1.0001;
        var boosters = new List<string>(4);
        if (f.Incense > 0) boosters.Add(IltBreedingSupplies.IncenseName(f.Incense));
        if (f.Catalyst) boosters.Add("Chromatic Catalyst");
        if (f.Offering) boosters.Add("Offering of Subjugation");
        if (draught) boosters.Add("Nurturing Draught x" + f.MaturityXpMult.ToString("0.#", CultureInfo.InvariantCulture));

        return new BreedEssence(
            f.Id, f.Name, f.Wcid, f.HasId, tier, bond, male, gate, status, problems, readyAt,
            male ? charges : 0, male ? max : 0, male ? refillAt : 0,
            bred, juvenile, juvenile ? stage : 0, stages, stageName, kills, required, toNext,
            Math.Max(0, f.Incense), f.Catalyst, f.Offering, draught, boosters,
            Math.Max(0, f.MutDamage), Math.Max(0, f.MutDamageResist), Math.Max(0, f.MutCrit), Math.Max(0, f.MutVitality),
            Math.Max(0, f.MutPotency), shiny, bred && !f.Attuned && desc.Contains("Bond: Unbound", StringComparison.OrdinalIgnoreCase),
            Math.Max(0, f.PotencyStored));
    }
}

/// <summary>What a stud + dam pairing would do: the gates both must pass, and the baby's odds.</summary>
internal sealed record BreedPairPlan(bool CanBreed, IReadOnlyList<string> Blockers, double Incense, double ChanceBest, double ChanceWorst,
                                     double PotencyChance, int InheritMin, int InheritMax, IReadOnlyList<string> Notes);

internal static class IltBreedingPlanner
{
    /// <summary>
    /// Plans one pairing. Each stat line comes from one parent, so the baby inherits between the
    /// sum of the lower lines and the sum of the higher lines; more inherited mutations lower the
    /// stat mutation chance (the decay), so the best case is the fewest.
    /// </summary>
    public static BreedPairPlan Plan(BreedEssence stud, BreedEssence dam, IltBreedingRules r)
    {
        var blockers = new List<string>();
        if (!stud.Male) blockers.Add($"{stud.Name} is female: the stud must be male.");
        if (dam.Male) blockers.Add($"{dam.Name} is male: the dam must be female.");
        if (stud.Id == dam.Id) blockers.Add("Pick two different essences.");
        foreach (string p in stud.Problems) blockers.Add($"{stud.Name}: {p}");
        foreach (string p in dam.Problems) blockers.Add($"{dam.Name}: {p}");

        int lo = Math.Min(stud.MutD, dam.MutD) + Math.Min(stud.MutDR, dam.MutDR) + Math.Min(stud.MutC, dam.MutC) + Math.Min(stud.MutV, dam.MutV);
        int hi = Math.Max(stud.MutD, dam.MutD) + Math.Max(stud.MutDR, dam.MutDR) + Math.Max(stud.MutC, dam.MutC) + Math.Max(stud.MutV, dam.MutV);
        double incense = r.CombinedIncense(stud.Incense, dam.Incense);

        var notes = new List<string>
        {
            "The baby goes to the dam's owner: they need a free main-pack slot and burden room.",
            $"Both pets in the same room of the breeding area ({r.RoomText()}); both owners *dance* within {r.Get(IltBreedingRules.DanceWindow):0.#}s.",
        };
        if (stud.Catalyst || dam.Catalyst) notes.Add("Chromatic Catalyst: the baby rolls a fresh colour.");
        if (r.Get(IltBreedingRules.GuardianEnabled) >= 0.5)
            notes.Add(stud.Offering || dam.Offering
                ? "Offering of Subjugation applied: the mating guardian comes weakened."
                : $"A mating guardian rises: kill it within {r.Get(IltBreedingRules.GuardianSeconds):0}s for an Awakened Blessing mutation.");
        if (incense > 0) notes.Add($"Incense +{incense * 100:0.#}% is used up by this breed.");

        return new BreedPairPlan(blockers.Count == 0, blockers, incense,
            r.StatMutationChance(lo, incense), r.StatMutationChance(hi, incense),
            Math.Clamp(r.Get(IltBreedingRules.PotencyMutation), 0, 1), lo, hi, notes);
    }
}
