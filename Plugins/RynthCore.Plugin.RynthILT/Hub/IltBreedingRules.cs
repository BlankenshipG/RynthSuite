// IltBreedingRules.cs - Infinite Leaftide's pet breeding rules as RynthILT knows them.
//
// ACECustom's breeding (Source/ACE.Server/WorldObjects/PetDevice_Breeding.cs, docs/PET_BREEDING_*.md)
// is driven by ServerConfig values the client is never sent. Each rule here therefore has three
// possible sources, best last:
//   * ACE default   - the ServerConfig default in ACECustom;
//   * ILT profile   - what Infinite Leaftide runs (deploy/PROD_DEPLOY_PLAN.md: decay 0.1, the
//                     mating guardian on, pet bond on, the Seedy Motel room);
//   * seen on server - read back from this server's own text: the appraisal breeding block
//                     ("Sex: Male (9/10 breeding charges today) - refills in 3h 10m", "Growth: ..."),
//                     "@breed-debug" ("Allowed Target: whole landblock 0x106, Variant=2"), and
//                     breeding chat ("spent a breeding charge: 9/10 left today", "They have 90s ...").
// A value seen on the server is saved with the character (IltPetState.Breeding) and wins from then on.
//
// Pure: no host calls, so RynthILT.Tests checks every parser offline.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthILT.Hub;

/// <summary>Where a breeding rule's value came from (later = more trusted).</summary>
internal enum BreedRuleSource { AceDefault = 0, IltProfile = 1, Server = 2 }

/// <summary>One rule: stable key, label, unit, the ACE default and the ILT profile value (null = same as ACE).</summary>
internal sealed record BreedRuleDef(string Key, string Label, string Unit, double AceDefault, double? IltProfile, string Help);

/// <summary>A rule's effective value and where it came from.</summary>
internal readonly record struct BreedRuleValue(BreedRuleDef Def, double Value, BreedRuleSource Source, DateTime SeenUtc);

/// <summary>A value this server showed, saved with the character.</summary>
public sealed class IltLearnedRule
{
    public string Key = string.Empty;
    public double Value;
    public DateTime SeenUtc;
}

/// <summary>One breeding event from chat (ritual, birth, growth, ...), newest last.</summary>
public sealed class IltBreedingEvent
{
    public DateTime Utc;
    /// <summary>ritual, charge, birth, mutation, guardian, growth, adult, imprint, dance, debug.</summary>
    public string Kind = string.Empty;
    public string Text = string.Empty;
}

/// <summary>The breeding tracker's saved part (IltPetState.Breeding).</summary>
public sealed class IltBreedingState
{
    /// <summary>Rules read back from this server (see IltBreedingRules).</summary>
    public List<IltLearnedRule> Rules = new();
    /// <summary>Growth stage names this server uses, by stage (index 0 = stage 1); "" = not seen yet.</summary>
    public List<string> StageNames = new();
    /// <summary>Recent breeding events (capped at IltBreedingRules.MaxEvents).</summary>
    public List<IltBreedingEvent> Log = new();
    /// <summary>The last "@breed-debug" reply's lines and when it came.</summary>
    public List<string> LastDebug = new();
    public DateTime LastDebugUtc;
}

/// <summary>What one "@breed-debug" reply said.</summary>
internal sealed record BreedDebugInfo(bool? Enabled, string Location, uint AllowedLandblock, int AllowedVariant, string AllowedKind,
                                      bool? LocationOk, string ActivePet, string PetSex, int Partners);

internal sealed class IltBreedingRules
{
    // ── Rule keys ───────────────────────────────────────────────────────────
    public const string Landblock = "landblock";
    public const string Variant = "variant";
    public const string DanceWindow = "dance_window_s";
    public const string MinTier = "min_tier";
    public const string MinBond = "min_bond";
    public const string MaleCharges = "male_charges";
    public const string MaleRefillHours = "male_refill_h";
    public const string FemaleRestHours = "female_rest_h";
    public const string BaseMutation = "base_mutation";
    public const string MutationDecay = "mutation_decay";
    public const string MutationFloor = "mutation_floor";
    public const string PotencyMutation = "potency_mutation";
    public const string IncenseCap = "incense_cap";
    public const string Inheritance = "inheritance";
    public const string GuardianEnabled = "guardian_enabled";
    public const string GuardianSeconds = "guardian_s";
    public const string MaturityKills = "maturity_kills";
    public const string MaturityStages = "maturity_stages";
    public const string MaturityShare = "maturity_share";

    /// <summary>Breeding events kept with the character.</summary>
    public const int MaxEvents = 60;

    /// <summary>ACECustom's default growth stage names (pet_maturity_stage_names).</summary>
    public static readonly string[] DefaultStageNames = { "Newborn", "Whelp", "Juvenile", "Adolescent", "Young Adult" };

    /// <summary>Every rule shown, in display order. Values copied from ACECustom ServerConfig and the ILT deploy plan.</summary>
    public static readonly BreedRuleDef[] Defs =
    {
        new(Landblock, "Breeding room landblock", "hex", 0x0106, null,
            "pet_breeding_allowed_landblock: the Seedy Motel (Ruggan's Annex, portal by Prof. Ruggan in Lin 54.7S 73.5E)."),
        new(Variant, "Breeding room variant", "", 2, null, "pet_breeding_allowed_variant: the landblock variant the room is in."),
        new(DanceWindow, "Dance window", "s", 5, null, "Both owners must *dance* (or @dance) within this many seconds."),
        new(MinTier, "Minimum essence tier", "", 100, null, "Both parents must be at least this tier (50, 80, 100, ... 300)."),
        new(MinBond, "Minimum bond", "", 100, null, "Both parents need this bond level (bond grows from kills; pet_bond must be on)."),
        new(MaleCharges, "Stud charges", "", 10, null, "pet_breeding_male_max_charges: breeds a male can sire per refill."),
        new(MaleRefillHours, "Stud refill", "h", 24, null, "pet_breeding_male_charge_reset_hours: charges refill this long after the last refill."),
        new(FemaleRestHours, "Dam rest", "h", 4, null, "A female rests this long after each breed before she can breed again."),
        new(BaseMutation, "Stat mutation chance", "%", 0.05, null, "pet_breeding_mutation_chance: the base chance of a stat mutation."),
        new(MutationDecay, "Mutation decay", "", 0.0, 0.1,
            "pet_breeding_mutation_decay: each stat mutation the baby inherits divides the chance by (1 + decay x mutations)."),
        new(MutationFloor, "Mutation floor", "%", 0.02, null, "The stat mutation chance never drops below this."),
        new(PotencyMutation, "Potency mutation chance", "%", 0.03, null, "An independent roll for a potency mutation."),
        new(IncenseCap, "Incense cap", "%", 0.50, null, "Both parents' incense together adds at most this much."),
        new(Inheritance, "Dominant line weight", "%", 0.55, null, "Each stat line comes from one parent, 55 / 45."),
        new(GuardianEnabled, "Mating guardian", "on/off", 0, 1,
            "pet_breeding_guardian_enabled: a spirit rises after the ritual; killing it in time grants an Awakened Blessing mutation."),
        new(GuardianSeconds, "Guardian timer", "s", 90, null, "How long the two parents have to kill the guardian."),
        new(MaturityKills, "Kills to adulthood", "", 300, null, "pet_maturity_kills_required: a bred baby grows up with this many credited kills."),
        new(MaturityStages, "Growth stages", "", 5, null, "pet_maturity_stages: the growth stages before Adult."),
        new(MaturityShare, "Kill credit share", "%", 0.10, null,
            "A kill counts when the pet dealt this share of the victim's health and the victim's level is at least the essence tier."),
    };

    private static readonly Dictionary<string, BreedRuleDef> DefByKey = BuildIndex();

    private static Dictionary<string, BreedRuleDef> BuildIndex()
    {
        var d = new Dictionary<string, BreedRuleDef>(StringComparer.Ordinal);
        foreach (BreedRuleDef def in Defs) d[def.Key] = def;
        return d;
    }

    private readonly IltBreedingState _state;

    /// <param name="state">The saved breeding state; learned values are read from and written to it.</param>
    public IltBreedingRules(IltBreedingState state) => _state = state;

    /// <summary>The saved state these rules read and update.</summary>
    public IltBreedingState State => _state;

    /// <summary>A copy that never changes (the render thread reads it while the pump learns).</summary>
    public IltBreedingRules Frozen()
    {
        var copy = new IltBreedingState { StageNames = new List<string>(_state.StageNames) };
        foreach (IltLearnedRule l in _state.Rules)
            copy.Rules.Add(new IltLearnedRule { Key = l.Key, Value = l.Value, SeenUtc = l.SeenUtc });
        return new IltBreedingRules(copy);
    }

    /// <summary>The effective value of rule <paramref name="key"/> (0 for an unknown key).</summary>
    public double Get(string key) => Value(key).Value;

    /// <summary>A whole-number rule (charges, kills, tiers).</summary>
    public int GetInt(string key) => (int)Math.Round(Get(key));

    /// <summary>The effective value and its source.</summary>
    public BreedRuleValue Value(string key)
    {
        if (!DefByKey.TryGetValue(key, out BreedRuleDef? def))
            return new BreedRuleValue(new BreedRuleDef(key, key, "", 0, null, ""), 0, BreedRuleSource.AceDefault, default);
        foreach (IltLearnedRule l in _state.Rules)
            if (l.Key == key) return new BreedRuleValue(def, l.Value, BreedRuleSource.Server, l.SeenUtc);
        return def.IltProfile.HasValue
            ? new BreedRuleValue(def, def.IltProfile.Value, BreedRuleSource.IltProfile, default)
            : new BreedRuleValue(def, def.AceDefault, BreedRuleSource.AceDefault, default);
    }

    /// <summary>Every rule's effective value, in display order.</summary>
    public List<BreedRuleValue> All()
    {
        var list = new List<BreedRuleValue>(Defs.Length);
        foreach (BreedRuleDef d in Defs) list.Add(Value(d.Key));
        return list;
    }

    /// <summary>Records a value the server showed. True when it changed what RynthILT knew.</summary>
    public bool Learn(string key, double value, DateTime utc)
    {
        if (!DefByKey.ContainsKey(key) || double.IsNaN(value) || double.IsInfinity(value)) return false;
        foreach (IltLearnedRule l in _state.Rules)
        {
            if (l.Key != key) continue;
            bool changed = Math.Abs(l.Value - value) > 1e-9;
            l.Value = value;
            l.SeenUtc = utc;
            return changed;
        }
        _state.Rules.Add(new IltLearnedRule { Key = key, Value = value, SeenUtc = utc });
        return true;
    }

    /// <summary>Applies every (key, value) an observer returned. True when anything changed.</summary>
    public bool LearnAll(IEnumerable<(string Key, double Value)> seen, DateTime utc)
    {
        bool changed = false;
        foreach ((string k, double v) in seen) changed |= Learn(k, v, utc);
        return changed;
    }

    /// <summary>Records a growth stage's name (1-based) as the server spelled it.</summary>
    public bool LearnStageName(int stage, string name)
    {
        name = (name ?? string.Empty).Trim();
        if (stage < 1 || stage > 50 || name.Length == 0 || name.Length > 40) return false;
        while (_state.StageNames.Count < stage) _state.StageNames.Add(string.Empty);
        if (_state.StageNames[stage - 1] == name) return false;
        _state.StageNames[stage - 1] = name;
        return true;
    }

    /// <summary>The name of growth stage <paramref name="stage"/> (1-based): seen on the server, else ACE's default, else "Stage N".</summary>
    public string StageName(int stage)
    {
        if (stage >= 1 && stage <= _state.StageNames.Count && _state.StageNames[stage - 1].Length > 0)
            return _state.StageNames[stage - 1];
        if (stage >= 1 && stage <= DefaultStageNames.Length && GetInt(MaturityStages) == DefaultStageNames.Length)
            return DefaultStageNames[stage - 1];
        return "Stage " + stage.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Appends an event, dropping the oldest past <see cref="MaxEvents"/>.</summary>
    public void AddEvent(string kind, string text, DateTime utc)
    {
        _state.Log.Add(new IltBreedingEvent { Utc = utc, Kind = kind, Text = text });
        if (_state.Log.Count > MaxEvents) _state.Log.RemoveRange(0, _state.Log.Count - MaxEvents);
    }

    // ── Mutation odds (ACE PetDevice_Breeding) ──────────────────────────────

    /// <summary>
    /// The chance of a stat mutation for a baby that inherits <paramref name="babyStatMutations"/>
    /// stat mutations, with <paramref name="incense"/> from both parents (already capped):
    /// clamp(max(floor, (base + incense) / (1 + decay x mutations)), 0, 1).
    /// </summary>
    public double StatMutationChance(int babyStatMutations, double incense)
    {
        double decay = Math.Max(0, Get(MutationDecay));
        double raw = (Get(BaseMutation) + Math.Max(0, incense)) / (1.0 + decay * Math.Max(0, babyStatMutations));
        return Math.Clamp(Math.Max(Get(MutationFloor), raw), 0.0, 1.0);
    }

    /// <summary>Both parents' incense together, capped at the incense cap.</summary>
    public double CombinedIncense(double a, double b) => Math.Clamp(Math.Max(0, a) + Math.Max(0, b), 0.0, Math.Max(0, Get(IncenseCap)));

    // ── Observers: what the server's own text says about its rules ──────────

    private const RegexOptions Rx = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    private static readonly Regex MaleLine = new(@"Sex:\s*Male\s*\((\d+)\s*/\s*(\d+) breeding charges today\)(?:\s*-\s*refills in (?:(\d+)h\s*)?(?:(\d+)m)?)?", Rx);
    private static readonly Regex GrowthLine = new(@"Growth:\s*(.+?)\s+(\d+)\s*/\s*(\d+)\s*-\s*(\d+) kills to (.+?)(?:,\s*(\d+) to Adult)?\s*$", Rx | RegexOptions.Multiline);
    private static readonly Regex ChargeSpent = new(@"spent a breeding charge:\s*(\d+)\s*/\s*(\d+) left today", Rx);
    private static readonly Regex GuardianRise = new(@"They have (\d+)s to bring it down together", Rx);
    private static readonly Regex GrownInto = new(@"has grown into an? (.+?)!.*\((\d+)\s*/\s*(\d+)\)", Rx);
    private static readonly Regex DanceWindowLine = new(@"\*dance\* within (\d+(?:\.\d+)?) seconds", Rx);
    private static readonly Regex DebugEnabled = new(@"Breeding System Enabled:\s*(True|False)", Rx);
    private static readonly Regex DebugLocation = new(@"Your Location:\s*(.+)$", Rx);
    private static readonly Regex DebugAllowed = new(@"Allowed Target:\s*(anywhere|whole landblock|exact cell)\s*0x([0-9A-F]+),\s*Variant=(-?\d+)\s*=>\s*Location Match:\s*(VALID|INVALID)", Rx);
    private static readonly Regex DebugPet = new(@"Your Active Pet:\s*(.+)$", Rx);
    private static readonly Regex DebugSex = new(@"^\s*Sex:\s*(.+)$", Rx);
    private static readonly Regex DebugPartners = new(@"(?:Eligible breeding partners nearby|Valid Partner Candidates within 30m):\s*(\d+)", Rx);

    /// <summary>
    /// What an essence's appraisal text says: the stud's charge max and refill window (from the
    /// "refills in" countdown against the essence's last refill time), and the growth stage count.
    /// </summary>
    /// <param name="longDesc">The appraisal LongDesc (string 16).</param>
    /// <param name="maleRefreshUnix">The essence's PetMaleChargesRefreshTime (0 = not sent).</param>
    /// <param name="nowUnix">Now, unix seconds.</param>
    public static List<(string Key, double Value)> FromAppraisal(string longDesc, double maleRefreshUnix, double nowUnix)
    {
        var seen = new List<(string, double)>();
        if (string.IsNullOrEmpty(longDesc)) return seen;

        Match m = MaleLine.Match(longDesc);
        if (m.Success)
        {
            if (int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int max) && max > 0 && max < 1000)
                seen.Add((MaleCharges, max));
            if ((m.Groups[3].Success || m.Groups[4].Success) && maleRefreshUnix > 0)
            {
                int h = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
                int min = m.Groups[4].Success ? int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 0;
                double refillAt = nowUnix + h * 3600.0 + min * 60.0;
                double hours = Math.Round((refillAt - maleRefreshUnix) / 3600.0);
                // The countdown is minute-rounded; only a whole-hour window that makes sense is taken.
                if (hours >= 1 && hours <= 24 * 14) seen.Add((MaleRefillHours, hours));
            }
        }

        Match g = GrowthLine.Match(longDesc);
        if (g.Success && int.TryParse(g.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int stages) && stages >= 1 && stages <= 50)
            seen.Add((MaturityStages, stages));
        return seen;
    }

    /// <summary>
    /// The growth line of an appraisal: current stage name, stage, stages, kills to the next stage
    /// and to adulthood (-1 when the line has no "to Adult" part, i.e. the last stage).
    /// </summary>
    public static bool TryParseGrowth(string longDesc, out string stageName, out int stage, out int stages, out int toNext, out string nextName, out int toAdult)
    {
        stageName = nextName = string.Empty;
        stage = stages = toNext = toAdult = 0;
        if (string.IsNullOrEmpty(longDesc)) return false;
        Match g = GrowthLine.Match(longDesc);
        if (!g.Success) return false;
        stageName = g.Groups[1].Value.Trim();
        int.TryParse(g.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out stage);
        int.TryParse(g.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out stages);
        int.TryParse(g.Groups[4].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out toNext);
        nextName = g.Groups[5].Value.Trim();
        if (g.Groups[6].Success) int.TryParse(g.Groups[6].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out toAdult);
        else { toAdult = toNext; nextName = "Adult"; }
        return true;
    }

    /// <summary>What one chat line says about the rules (a line can say nothing).</summary>
    public static List<(string Key, double Value)> FromChat(string line)
    {
        var seen = new List<(string, double)>();
        if (string.IsNullOrEmpty(line)) return seen;

        Match m = ChargeSpent.Match(line);
        if (m.Success && int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int max) && max > 0 && max < 1000)
            seen.Add((MaleCharges, max));

        m = GuardianRise.Match(line);
        if (m.Success)
        {
            seen.Add((GuardianEnabled, 1));
            if (int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) && s > 0 && s < 3600)
                seen.Add((GuardianSeconds, s));
        }
        else if (line.Contains("The union stirs something", StringComparison.OrdinalIgnoreCase))
            seen.Add((GuardianEnabled, 1));

        m = GrownInto.Match(line);
        if (m.Success && int.TryParse(m.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int stages) && stages >= 1 && stages <= 50)
            seen.Add((MaturityStages, stages));

        m = DanceWindowLine.Match(line);
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double w) && w > 0 && w < 600)
            seen.Add((DanceWindow, w));

        foreach (string part in line.Split('\n'))
        {
            m = DebugAllowed.Match(part);
            if (!m.Success) continue;
            if (uint.TryParse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint lb))
                seen.Add((Landblock, lb));
            if (int.TryParse(m.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                seen.Add((Variant, v));
        }
        return seen;
    }

    /// <summary>The stage name a "has grown into a Whelp! ... (2/5)" line gives, with its stage number; false for any other line.</summary>
    public static bool TryParseGrownInto(string line, out int stage, out string name)
    {
        stage = 0;
        name = string.Empty;
        Match m = GrownInto.Match(line ?? string.Empty);
        if (!m.Success || !int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out stage)) return false;
        name = m.Groups[1].Value.Trim();
        return name.Length > 0;
    }

    /// <summary>True for any line of an "@breed-debug" reply (one multi-line message, or its lines one by one).</summary>
    public static bool IsDebugLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.Contains("PET BREEDING DIAGNOSTICS", StringComparison.OrdinalIgnoreCase)
            || DebugEnabled.IsMatch(text) || DebugLocation.IsMatch(text) || DebugAllowed.IsMatch(text)
            || DebugPet.IsMatch(text) || DebugPartners.IsMatch(text)
            || text.TrimStart().StartsWith("Sex:", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Ensure your partner has their pet summoned", StringComparison.OrdinalIgnoreCase)
            || IsDebugEnd(text);
    }

    /// <summary>The reply's closing rule of '=' (alone, or ending the whole message).</summary>
    public static bool IsDebugEnd(string text)
        => !string.IsNullOrEmpty(text) && text.TrimEnd().EndsWith("================================", StringComparison.Ordinal);

    /// <summary>Parses an "@breed-debug" reply (any mix of whole messages and single lines).</summary>
    public static BreedDebugInfo ParseDebug(IEnumerable<string> lines)
    {
        bool? enabled = null, ok = null;
        string location = string.Empty, kind = string.Empty, pet = string.Empty, sex = string.Empty;
        uint lb = 0;
        int variant = -1, partners = -1;
        foreach (string text in lines)
        {
            foreach (string raw in (text ?? string.Empty).Split('\n'))
            {
                string l = raw.TrimEnd('\r');
                Match m;
                if ((m = DebugEnabled.Match(l)).Success) enabled = m.Groups[1].Value.Equals("True", StringComparison.OrdinalIgnoreCase);
                else if ((m = DebugAllowed.Match(l)).Success)
                {
                    kind = m.Groups[1].Value.ToLowerInvariant();
                    uint.TryParse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out lb);
                    int.TryParse(m.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out variant);
                    ok = m.Groups[4].Value.Equals("VALID", StringComparison.OrdinalIgnoreCase);
                }
                else if ((m = DebugLocation.Match(l)).Success) location = m.Groups[1].Value.Trim();
                else if ((m = DebugPet.Match(l)).Success) pet = m.Groups[1].Value.Trim();
                else if ((m = DebugPartners.Match(l)).Success) int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out partners);
                else if ((m = DebugSex.Match(l)).Success) sex = m.Groups[1].Value.Trim();
            }
        }
        return new BreedDebugInfo(enabled, location, lb, variant, kind, ok, pet, sex, partners);
    }

    /// <summary>"0x0106 variant 2" (or "anywhere") for the rules list.</summary>
    public string RoomText()
    {
        uint lb = (uint)Math.Max(0, Get(Landblock));
        int variant = GetInt(Variant);
        if (lb == 0) return "anywhere";
        string place = lb > 0xFFFF ? "cell 0x" + lb.ToString("X8", CultureInfo.InvariantCulture) : "landblock 0x" + lb.ToString("X4", CultureInfo.InvariantCulture);
        return variant >= 0 ? place + " variant " + variant.ToString(CultureInfo.InvariantCulture) : place;
    }

    /// <summary>A rule value for display: "5%", "24h", "0x0106", "on", "10".</summary>
    public static string Format(BreedRuleValue v)
    {
        switch (v.Def.Unit)
        {
            case "%": return (v.Value * 100.0).ToString("0.##", CultureInfo.InvariantCulture) + "%";
            case "h": return v.Value.ToString("0.#", CultureInfo.InvariantCulture) + "h";
            case "s": return v.Value.ToString("0.#", CultureInfo.InvariantCulture) + "s";
            case "hex": return "0x" + ((uint)Math.Max(0, v.Value)).ToString("X4", CultureInfo.InvariantCulture);
            case "on/off": return v.Value >= 0.5 ? "on" : "off";
            default: return v.Value.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>"ACE default", "ILT profile" or "seen on server".</summary>
    public static string SourceText(BreedRuleSource s) => s switch
    {
        BreedRuleSource.Server => "seen on server",
        BreedRuleSource.IltProfile => "ILT profile",
        _ => "ACE default",
    };
}
