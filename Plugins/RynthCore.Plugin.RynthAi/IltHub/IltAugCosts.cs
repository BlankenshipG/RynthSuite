// IltAugCosts.cs — luminance augmentation cost rules, per server (world name).
//
// Defaults are UB's Leaftide Augs tab (correct for InfiniteLeaftide):
//   level i (1-based) costs  (Base + (i-1) * Base * LinearPercent/100) * multiplier(i)
//   multiplier(i) = M0 below S1, M1 from S1, M2 from S2, M3 from S3, M4 from S4
//   (UB: 1 / 4 / 8 / 16 / 24; a tier starts AT its S level).
// Another server can change the multipliers and every aug's base, growth, S1..S4, target cap
// and coins per level from the Progression tab; those edits are saved for that world only in
// one shared file, so every character on the world prices augs the same way.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RynthCore.Plugin.RynthAi.IltHub;

/// <summary>The saved file: one cost profile per world that was changed from the defaults.</summary>
public sealed class IltAugCostFile
{
    public Dictionary<string, IltAugCostProfile> Servers = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A world's cost rules: the five tier multipliers and one row per aug.</summary>
public sealed class IltAugCostProfile
{
    public double[] Multipliers = (double[])IltAugCosts.DefaultMultipliers.Clone();
    public List<IltAugCostRow> Augs = new();
}

/// <summary>One aug's cost rule. Thresholds are 1-based levels; Cap 0 = no target limit.</summary>
public sealed class IltAugCostRow
{
    public string Key = string.Empty;
    public long Base;
    public double LinearPercent;
    public int CoinsPerLevel;
    public int S1, S2, S3, S4;
    public int Cap;

    public IltAugCostRow Clone() => (IltAugCostRow)MemberwiseClone();
}

internal sealed class IltAugCosts
{
    /// <summary>UB's tier multipliers: below S1, from S1, from S2, from S3, from S4.</summary>
    public static readonly double[] DefaultMultipliers = { 1, 4, 8, 16, 24 };

    /// <summary>Highest target the planner accepts when an aug has no cap (keeps the plan sane).</summary>
    public const int NoCapLimit = 100_000;

    /// <summary>Display order and labels; also the only keys a profile may hold.</summary>
    public static readonly (string Key, string Label)[] Labels =
    {
        ("creature", "Creature"), ("item", "Item"), ("life", "Life"), ("war", "War"), ("void", "Void"),
        ("duration", "Duration"), ("melee", "Melee"), ("missile", "Missile"),
        ("specialization", "Specialization"), ("summon", "Summon"),
    };

    /// <summary>UB Augs.cs (ILT): cost models, coins per level and soft caps S1..S4; no target cap.</summary>
    public static IltAugCostRow[] DefaultRows() => new[]
    {
        Row("creature",       750_000,   130, 3,  2750, 4000, 4750, 5250),
        Row("item",           1_000_000, 165, 18, 1250, 2000, 3000, 3500),
        Row("life",           975_000,   195, 12, 1000, 2000, 3000, 3750),
        Row("war",            750_000,   140, 5,  1750, 2500, 3000, 3750),
        Row("void",           800_000,   160, 5,  1750, 2500, 3000, 3750),
        Row("duration",       400_000,   120, 5,  1000, 2000, 2500, 3000),
        Row("melee",          750_000,   140, 5,  1750, 2500, 3000, 3750),
        Row("missile",        750_000,   140, 5,  1750, 2500, 3000, 3750),
        Row("specialization", 3_000_000, 200, 50, 1750, 2500, 3000, 3750),
        Row("summon",         500_000,   125, 36, 1750, 2500, 3000, 3750),
    };

    private static IltAugCostRow Row(string key, long b, double lp, int coins, int s1, int s2, int s3, int s4)
        => new() { Key = key, Base = b, LinearPercent = lp, CoinsPerLevel = coins, S1 = s1, S2 = s2, S3 = s3, S4 = s4, Cap = 0 };

    /// <summary>The shared settings file. Tests point it at a scratch folder before first use.</summary>
    public static string FilePath { get; set; } = Path.Combine(IltHubStore.SharedRoot, "ilt-aug-costs.json");

    private IltAugCostFile? _file;
    private string _lastJson = string.Empty;
    private static readonly IltAugCostProfile Defaults = NewDefaultProfile();

    public static string Label(string key)
    {
        foreach (var l in Labels) if (l.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return l.Label;
        return key;
    }

    private static IltAugCostProfile NewDefaultProfile()
        => new() { Multipliers = (double[])DefaultMultipliers.Clone(), Augs = DefaultRows().ToList() };

    private IltAugCostFile File_
    {
        get
        {
            if (_file != null) return _file;
            _file = new IltAugCostFile();
            try
            {
                if (File.Exists(FilePath))
                {
                    string json = File.ReadAllText(FilePath);
                    _file = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.IltAugCostFile) ?? new IltAugCostFile();
                    _lastJson = json;
                }
            }
            catch (Exception ex) { RynthLog.Exception(LogCat.IltStore, ex, $"load {FilePath}"); _file = new IltAugCostFile(); }
            var fixedUp = new Dictionary<string, IltAugCostProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _file.Servers ?? new())
                if (kv.Value != null) fixedUp[kv.Key ?? string.Empty] = Normalize(kv.Value);
            _file.Servers = fixedUp;
            return _file;
        }
    }

    /// <summary>
    /// Repairs a loaded or edited profile: five multipliers, exactly one row per known aug (missing
    /// ones from the defaults, unknown ones dropped), non-negative values and S1 &lt;= S2 &lt;= S3 &lt;= S4.
    /// </summary>
    public static IltAugCostProfile Normalize(IltAugCostProfile p)
    {
        var m = new double[5];
        for (int i = 0; i < 5; i++)
        {
            double v = p.Multipliers != null && i < p.Multipliers.Length ? p.Multipliers[i] : DefaultMultipliers[i];
            m[i] = double.IsFinite(v) ? Math.Clamp(v, 0, 1000) : DefaultMultipliers[i];
        }
        p.Multipliers = m;

        var byKey = new Dictionary<string, IltAugCostRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in p.Augs ?? new()) if (r != null && !string.IsNullOrEmpty(r.Key)) byKey[r.Key] = r;
        var rows = new List<IltAugCostRow>(Labels.Length);
        foreach (var def in DefaultRows())
        {
            var r = byKey.TryGetValue(def.Key, out var have) ? have : def;
            r.Key = def.Key;
            r.Base = Math.Max(0, r.Base);
            r.LinearPercent = double.IsFinite(r.LinearPercent) ? Math.Clamp(r.LinearPercent, 0, 100_000) : def.LinearPercent;
            r.CoinsPerLevel = Math.Max(0, r.CoinsPerLevel);
            r.S1 = Math.Max(0, r.S1);
            r.S2 = Math.Max(r.S1, r.S2);
            r.S3 = Math.Max(r.S2, r.S3);
            r.S4 = Math.Max(r.S3, r.S4);
            r.Cap = Math.Max(0, r.Cap);
            rows.Add(r);
        }
        p.Augs = rows;
        return p;
    }

    /// <summary>The rules for <paramref name="world"/>: its saved profile, or the UB defaults.</summary>
    public IltAugCostProfile For(string world)
        => File_.Servers.TryGetValue(world ?? string.Empty, out var p) ? p : Defaults;

    /// <summary>True when <paramref name="world"/> has its own saved rules.</summary>
    public bool IsCustom(string world) => File_.Servers.ContainsKey(world ?? string.Empty);

    /// <summary>Applies an edit to <paramref name="world"/>'s rules (copying the defaults first) and saves.</summary>
    public void Edit(string world, Action<IltAugCostProfile> change)
    {
        string key = world ?? string.Empty;
        if (!File_.Servers.TryGetValue(key, out var p))
        {
            p = NewDefaultProfile();
            File_.Servers[key] = p;
        }
        change(p);
        Normalize(p);
        Save();
    }

    /// <summary>Drops <paramref name="world"/>'s saved rules: it goes back to the UB defaults.</summary>
    public void Reset(string world)
    {
        if (File_.Servers.Remove(world ?? string.Empty)) Save();
    }

    private void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(File_, RynthAiJsonContext.Default.IltAugCostFile);
            if (json == _lastJson) return;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
            _lastJson = json;
        }
        catch (Exception ex) { RynthLog.Exception(LogCat.IltStore, ex, $"save {FilePath}"); }
    }

    /// <summary>Highest target the planner allows for <paramref name="r"/> at current level <paramref name="cur"/>.</summary>
    public static int Ceiling(IltAugCostRow r, int cur) => Math.Max(cur, r.Cap > 0 ? r.Cap : NoCapLimit);

    /// <summary>
    /// Luminance for levels cur+1 .. tgt. Same result as UB's per-level loop, summed per tier in
    /// closed form: within a tier the per-level base grows linearly, so each tier is an arithmetic series.
    /// </summary>
    public static decimal LumCost(IltAugCostRow r, double[] mult, int cur, int tgt)
    {
        int first = Math.Max(1, cur + 1);
        if (tgt < first) return 0;
        decimal b = r.Base;
        decimal step = b * (decimal)r.LinearPercent / 100m;
        // Tier t covers levels [lo[t], hi[t]) (1-based); the last tier has no end.
        long[] lo = { 1, r.S1, r.S2, r.S3, r.S4 };
        long[] hi = { r.S1, r.S2, r.S3, r.S4, long.MaxValue };
        decimal total = 0;
        for (int t = 0; t < 5; t++)
        {
            long a = Math.Max(first, lo[t]);
            long z = Math.Min(tgt, hi[t] - 1);
            if (z < a) continue;
            decimal n = z - a + 1;
            // sum over i=a..z of (b + (i-1)*step) = n*b + step * sum(i-1) = n*b + step * n*(a-1 + z-1)/2
            decimal levels = n * b + step * (n * ((a - 1) + (z - 1)) / 2m);
            total += levels * (decimal)mult[t];
        }
        return total;
    }
}
