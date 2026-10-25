using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RynthCore.Plugin.RynthThreads.Threads;

/// <summary>
/// One modifier's rate in a set of presses (a component's, a combination's, ...), with its rate
/// in the other presses for comparison.
/// </summary>
internal sealed class Odds
{
    /// <summary>The modifier's name, or <see cref="PressInference.AffinityGroup"/> for any "&lt;Material&gt; Affinity".</summary>
    public string Modifier = string.Empty;
    /// <summary>Presses in the set that had it, of <see cref="N"/>.</summary>
    public int Hits, N;
    /// <summary>False for the All presses row: nothing to compare with.</summary>
    public bool Compared;
    /// <summary>The other presses (without the component, ...) that had it, of <see cref="OtherN"/>; 0 = no data.</summary>
    public int OtherHits, OtherN;
    /// <summary>One-sided Fisher exact p: chance of a rate this much higher here if the set made no difference.</summary>
    public double P = 1;
    /// <summary>Clearly higher here than in the other presses (Fisher p &lt; 0.05 or the 95% intervals apart).</summary>
    public bool MoreLikely;
    /// <summary>Its numbers in these presses ("x1.38 to x1.52").</summary>
    public string Values = string.Empty;
    /// <summary>For the Affinity group: presses per material, most first.</summary>
    public List<(string Material, int Hits)> Split = new();

    public double Rate => N == 0 ? 0 : (double)Hits / N;
    public double? OtherRate => OtherN == 0 ? null : (double)OtherHits / OtherN;
    public bool IsGroup => Modifier == PressInference.AffinityGroup;
}

internal enum GroupKind { All, Component, Combination, NoComponents }

/// <summary>(fragment level, result level, entries) and how many presses gave it.</summary>
internal readonly record struct ResultCount(int? FragmentLevel, int? ResultLevel, int? Entries, int Count);

/// <summary>A set of presses: All presses, one component's, one exact combination's, or those with no component.</summary>
internal sealed class GroupReport
{
    public string Name = string.Empty;
    public GroupKind Kind;
    /// <summary>The components (one for a component row, several for a combination, none otherwise).</summary>
    public List<string> Members = new();
    public int Presses;
    public double AvgModifiers;
    public int MinModifiers, MaxModifiers;
    /// <summary>Most common first.</summary>
    public List<ResultCount> Results = new();
    /// <summary>Every modifier seen in these presses, likeliest first.</summary>
    public List<Odds> Odds = new();

    /// <summary>What the comparison column is: "without", "other presses", "with components", or "".</summary>
    public string OtherLabel => Kind switch
    {
        GroupKind.Component => "without",
        GroupKind.Combination => "other presses",
        GroupKind.NoComponents => "with components",
        _ => string.Empty,
    };

    public Odds? Find(string modifier) =>
        Odds.FirstOrDefault(o => o.Modifier.Equals(modifier, StringComparison.OrdinalIgnoreCase));
}

internal sealed class ModifierReport
{
    /// <summary>The modifier's name, or <see cref="PressInference.AffinityGroup"/>.</summary>
    public string Name = string.Empty;
    public bool IsGroup;
    public string Example = string.Empty;
    /// <summary>"reward" (loot, salvage), "harder" (monster health, damage, numbers) or "" when the text doesn't say plainly.</summary>
    public string Tag = string.Empty;
    /// <summary>Its rate over all usable presses.</summary>
    public Odds Overall = new();
    /// <summary>Its rate in presses with no components (against presses with some); null when there are none.</summary>
    public Odds? NoComponents;
    /// <summary>Its rate with each component and combination (0% included), likeliest first.</summary>
    public List<(GroupReport Group, Odds Odds)> ByGroup = new();
    public string Values = string.Empty;
    /// <summary>(fragment level, presses, values), lowest level first; level null = unknown.</summary>
    public List<(int? Level, int Count, string Values)> ByLevel = new();

    public int Seen => Overall.Hits;
}

internal sealed class ThreadsReport
{
    public int Total, Usable, Incomplete, Characters;
    /// <summary>Every usable press (the baseline); null when there are none.</summary>
    public GroupReport? All;
    public List<GroupReport> Components = new();
    /// <summary>Exact combinations of two or more components.</summary>
    public List<GroupReport> Combinations = new();
    /// <summary>Presses with no components (the plain random draw); null when there are none.</summary>
    public GroupReport? NoComponents;
    public List<ModifierReport> Modifiers = new();

    public GroupReport? Component(string name) =>
        Components.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public GroupReport? Combination(string name) =>
        Combinations.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public ModifierReport? Modifier(string name) =>
        Modifiers.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The odds of each modifier from the recorded presses. On DreamWeave a component gives a chance of
/// each modifier, not a fixed one, so for every set of presses (All presses, each component, each
/// exact combination of components, no components) this counts how often each modifier came:
/// hits/N, with a 95% Wilson interval, against its rate in the presses outside the set.
///
/// Only finished presses with their gem block count (Press.Usable); a finished press without a
/// "Components selected" line counts as one with no components. Every gem carries one
/// "&lt;Material&gt; Affinity": those are counted together as the Affinity group, with the split by
/// material.
///
/// "More likely with X": the rate with X is above the rate without and either the one-sided Fisher
/// exact p is under 0.05 or the two 95% intervals don't overlap. "Always ... so far" only for 100%
/// over at least 10 presses.
/// </summary>
internal static class PressInference
{
    public const string AffinityGroup = "Affinity";
    private const string AffinitySuffix = " Affinity";
    private const double Z = 1.96;
    /// <summary>Intervals are shown below this many presses.</summary>
    public const int IntervalBelow = 50;
    public const int AlwaysAtLeast = 10;

    public static ThreadsReport Build(IReadOnlyList<Press> all)
    {
        var report = new ThreadsReport { Total = all.Count };
        var used = new List<Press>();
        var characters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Press p in all)
        {
            if (p.Character.Length > 0) characters.Add(p.Character);
            if (!p.Complete) report.Incomplete++;
            if (p.Usable) used.Add(p);
        }
        report.Usable = used.Count;
        report.Characters = characters.Count;
        if (used.Count == 0) return report;

        List<string> components = Distinct(used.SelectMany(p => p.Components));
        List<string> keys = Distinct(used.SelectMany(Keys));

        report.All = Group("All presses", GroupKind.All, new List<string>(), used, used);

        foreach (string c in components)
            report.Components.Add(Group(c, GroupKind.Component, new List<string> { c }, used, used.Where(p => p.HasComponent(c)).ToList()));
        SortGroups(report.Components);

        foreach (var g in used.Where(p => p.Components.Count > 1).GroupBy(ComboKey, StringComparer.OrdinalIgnoreCase))
        {
            List<string> members = g.First().Components.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            report.Combinations.Add(Group(string.Join(" + ", members), GroupKind.Combination, members, used, g.ToList()));
        }
        SortGroups(report.Combinations);

        List<Press> plain = used.Where(p => p.Components.Count == 0).ToList();
        if (plain.Count > 0)
            report.NoComponents = Group("No components", GroupKind.NoComponents, new List<string>(), used, plain);

        foreach (string k in keys)
        {
            List<Press> with = used.Where(p => Has(p, k)).ToList();
            var mr = new ModifierReport
            {
                Name = k,
                IsGroup = k == AffinityGroup,
                Example = Find(with[^1], k)!.Description,
                Overall = Count(k, used, null),
                Values = Values(with, k),
            };
            mr.Tag = Tag(mr.Example);
            if (report.NoComponents != null) mr.NoComponents = Count(k, plain, used.Where(p => p.Components.Count > 0).ToList());
            foreach (GroupReport gr in report.Components.Concat(report.Combinations))
                mr.ByGroup.Add((gr, gr.Find(k) ?? Count(k, Members(used, gr), Others(used, gr))));
            mr.ByGroup.Sort((a, b) =>
            {
                int byOdds = CompareOdds(a.Odds, b.Odds);
                return byOdds != 0 ? byOdds : string.Compare(a.Group.Name, b.Group.Name, StringComparison.OrdinalIgnoreCase);
            });
            foreach (var g in with.GroupBy(p => p.FragmentLevel).OrderBy(g => g.Key ?? int.MaxValue))
                mr.ByLevel.Add((g.Key, g.Count(), Values(g, k)));
            report.Modifiers.Add(mr);
        }
        report.Modifiers.Sort((a, b) => b.Seen != a.Seen ? b.Seen.CompareTo(a.Seen) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return report;
    }

    // ── Groups ──────────────────────────────────────────────────────────────

    private static GroupReport Group(string name, GroupKind kind, List<string> members, List<Press> used, List<Press> set)
    {
        var g = new GroupReport { Name = name, Kind = kind, Members = members, Presses = set.Count };
        if (set.Count == 0) return g;
        g.AvgModifiers = set.Average(p => (double)p.Modifiers.Count);
        g.MinModifiers = set.Min(p => p.Modifiers.Count);
        g.MaxModifiers = set.Max(p => p.Modifiers.Count);
        g.Results = set.GroupBy(p => (p.FragmentLevel, p.ResultLevel, p.Entries))
            .Select(x => new ResultCount(x.Key.FragmentLevel, x.Key.ResultLevel, x.Key.Entries, x.Count()))
            .OrderByDescending(r => r.Count).ThenBy(r => r.ResultLevel ?? int.MaxValue).ThenBy(r => r.Entries ?? int.MaxValue)
            .ToList();
        List<Press>? others = kind == GroupKind.All ? null : Others(used, g, set);
        foreach (string k in Distinct(set.SelectMany(Keys)))
            g.Odds.Add(Count(k, set, others));
        g.Odds.Sort(CompareOdds);
        return g;
    }

    private static void SortGroups(List<GroupReport> groups) =>
        groups.Sort((a, b) => b.Presses != a.Presses ? b.Presses.CompareTo(a.Presses) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The presses a component or combination row covers.</summary>
    private static List<Press> Members(List<Press> used, GroupReport g) => g.Kind switch
    {
        GroupKind.Component => used.Where(p => p.HasComponent(g.Members[0])).ToList(),
        GroupKind.Combination => used.Where(p => ComboKey(p).Equals(ComboKey(g.Members), StringComparison.OrdinalIgnoreCase)).ToList(),
        GroupKind.NoComponents => used.Where(p => p.Components.Count == 0).ToList(),
        _ => used,
    };

    /// <summary>The presses a row is compared with: without the component, not this combination, with components.</summary>
    private static List<Press> Others(List<Press> used, GroupReport g, List<Press>? set = null)
    {
        var inSet = new HashSet<Press>(set ?? Members(used, g));
        return used.Where(p => !inSet.Contains(p)).ToList();
    }

    private static string ComboKey(Press p) => ComboKey(p.Components);

    private static string ComboKey(IEnumerable<string> components) =>
        string.Join("\n", components.Select(c => c.ToLowerInvariant()).OrderBy(c => c, StringComparer.Ordinal));

    /// <summary>Likeliest first; then more presses; then by name.</summary>
    private static int CompareOdds(Odds a, Odds b)
    {
        if (a.Rate != b.Rate) return b.Rate.CompareTo(a.Rate);
        if (a.Hits != b.Hits) return b.Hits.CompareTo(a.Hits);
        return string.Compare(a.Modifier, b.Modifier, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Modifier <paramref name="k"/> in <paramref name="set"/>, against <paramref name="others"/> (null = no comparison).</summary>
    internal static Odds Count(string k, List<Press> set, List<Press>? others)
    {
        var o = new Odds { Modifier = k, N = set.Count, Compared = others != null };
        foreach (Press p in set)
            if (Has(p, k)) o.Hits++;
        if (others != null)
        {
            o.OtherN = others.Count;
            foreach (Press p in others)
                if (Has(p, k)) o.OtherHits++;
            if (o.OtherN > 0 && o.N > 0)
            {
                o.P = FisherGreater(o.Hits, o.N, o.OtherHits, o.OtherN);
                bool apart = Wilson(o.Hits, o.N).Low > Wilson(o.OtherHits, o.OtherN).High;
                o.MoreLikely = o.Rate > o.OtherRate!.Value && (o.P < 0.05 || apart);
            }
        }
        o.Values = Values(set, k);
        if (k == AffinityGroup)
        {
            var split = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            foreach (Press p in set)
                foreach (string mat in p.Modifiers.Select(m => Material(m.Name)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!split.ContainsKey(mat)) { split[mat] = 0; names.Add(mat); }
                    split[mat]++;
                }
            o.Split = names.Select(n => (n, split[n]))
                .OrderByDescending(x => x.Item2).ThenBy(x => x.n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        return o;
    }

    // ── Modifier keys (the Affinity group) ──────────────────────────────────

    /// <summary>"Amethyst" for "Amethyst Affinity", else null.</summary>
    internal static string? Material(string modifier) =>
        modifier.Length > AffinitySuffix.Length && modifier.EndsWith(AffinitySuffix, StringComparison.OrdinalIgnoreCase)
            ? modifier.Substring(0, modifier.Length - AffinitySuffix.Length).Trim()
            : null;

    /// <summary>The name the counts use: <see cref="AffinityGroup"/> for any affinity, else the modifier's own.</summary>
    internal static string Key(string modifier) => Material(modifier) != null ? AffinityGroup : modifier;

    private static IEnumerable<string> Keys(Press p) => p.Modifiers.Select(m => Key(m.Name));

    private static bool Has(Press p, string k) => Find(p, k) != null;

    private static GemModifier? Find(Press p, string k)
    {
        if (k != AffinityGroup) return p.FindModifier(k);
        foreach (GemModifier m in p.Modifiers)
            if (Material(m.Name) != null) return m;
        return null;
    }

    /// <summary>"Affinity (any)" for the group, else the name.</summary>
    internal static string Display(string k) => k == AffinityGroup ? "Affinity (any)" : k;

    // ── Statistics ──────────────────────────────────────────────────────────

    /// <summary>95% Wilson score interval for <paramref name="hits"/> of <paramref name="n"/>.</summary>
    internal static (double Low, double High) Wilson(int hits, int n)
    {
        if (n <= 0) return (0, 1);
        double p = (double)hits / n, z2 = Z * Z;
        double denom = 1 + z2 / n;
        double centre = (p + z2 / (2 * n)) / denom;
        double half = Z * Math.Sqrt(p * (1 - p) / n + z2 / (4.0 * n * n)) / denom;
        return (Math.Max(0, centre - half), Math.Min(1, centre + half));
    }

    /// <summary>
    /// One-sided Fisher exact test: the chance of <paramref name="a"/> or more hits in a set of
    /// <paramref name="n"/> when <paramref name="a"/> + <paramref name="c"/> hits are spread at random
    /// over the <paramref name="n"/> + <paramref name="m"/> presses.
    /// </summary>
    internal static double FisherGreater(int a, int n, int c, int m)
    {
        int total = n + m, hits = a + c;
        if (total == 0) return 1;
        double logAll = LogChoose(total, n), sum = 0;
        for (int x = a; x <= Math.Min(n, hits); x++)
        {
            if (hits - x > m) continue;
            sum += Math.Exp(LogChoose(hits, x) + LogChoose(total - hits, n - x) - logAll);
        }
        return Math.Min(1, sum);
    }

    private static double LogChoose(int n, int k)
    {
        if (k < 0 || k > n) return double.NegativeInfinity;
        double r = 0;
        for (int i = 1; i <= k; i++) r += Math.Log(n - k + i) - Math.Log(i);
        return r;
    }

    // ── Text ────────────────────────────────────────────────────────────────

    private static string Pct(double v) => Math.Round(v * 100).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>"75% (3/4, 30-95%)"; the interval only below <see cref="IntervalBelow"/> presses.</summary>
    internal static string OddsText(Odds o)
    {
        if (o.N == 0) return "no data";
        if (o.N >= IntervalBelow) return $"{Pct(o.Rate)}% ({o.Hits}/{o.N})";
        var (lo, hi) = Wilson(o.Hits, o.N);
        return $"{Pct(o.Rate)}% ({o.Hits}/{o.N}, {Pct(lo)}-{Pct(hi)}%)";
    }

    /// <summary>"without: 20% (1/5)", "without: no data", or "" when not compared.</summary>
    internal static string OtherText(Odds o, string label)
    {
        if (!o.Compared || label.Length == 0) return string.Empty;
        return o.OtherN == 0 ? label + ": no data" : $"{label}: {Pct(o.OtherRate!.Value)}% ({o.OtherHits}/{o.OtherN})";
    }

    /// <summary>"more likely with Lead Scarab", "always with Lead Scarab so far" (100% over 10+), or "".</summary>
    internal static string LikelyText(Odds o, GroupReport g)
    {
        if (!o.MoreLikely) return string.Empty;
        string who = g.Kind == GroupKind.NoComponents ? "no components" : g.Name;
        return o.Hits == o.N && o.N >= AlwaysAtLeast ? $"always with {who} so far" : $"more likely with {who}";
    }

    /// <summary>"Amethyst 1, Iron 1" for the Affinity group's split, else "".</summary>
    internal static string SplitText(Odds o) =>
        string.Join(", ", o.Split.Select(s => $"{s.Material} {s.Hits}"));

    /// <summary>"5.25 modifiers per press (5-6)".</summary>
    internal static string ModifiersText(GroupReport g)
    {
        string avg = g.AvgModifiers.ToString("0.##", CultureInfo.InvariantCulture);
        string range = g.MinModifiers == g.MaxModifiers ? "" : $" ({g.MinModifiers}-{g.MaxModifiers})";
        return $"{avg} modifiers per press{range}";
    }

    /// <summary>"L185 -> 175, 3 entries: 4" per result, most common first.</summary>
    internal static string ResultsText(GroupReport g) =>
        string.Join("; ", g.Results.Select(r =>
            $"L{N(r.FragmentLevel)} -> {N(r.ResultLevel)}, {N(r.Entries)} entries: {r.Count}"));

    private static string N(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "?";

    private static List<string> Distinct(IEnumerable<string> names)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string n in names)
            if (n.Length > 0 && seen.Add(n)) list.Add(n);
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>
    /// The modifier's numbers across presses, position by position: "x1.38 to x1.52, 12%". Empty
    /// when it has none.
    /// </summary>
    internal static string Values(IEnumerable<Press> presses, string k)
    {
        var ranges = new List<(string Kind, double Min, double Max)>();
        foreach (Press p in presses)
        {
            GemModifier? gm = Find(p, k);
            if (gm == null) continue;
            for (int i = 0; i < gm.Numbers.Count; i++)
            {
                ModNumber n = gm.Numbers[i];
                if (i >= ranges.Count) ranges.Add((n.Kind, n.Value, n.Value));
                else ranges[i] = (ranges[i].Kind, Math.Min(ranges[i].Min, n.Value), Math.Max(ranges[i].Max, n.Value));
            }
        }
        var sb = new StringBuilder();
        foreach (var (kind, min, max) in ranges)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(Format(kind, min));
            if (max != min) sb.Append(" to ").Append(Format(kind, max));
        }
        return sb.ToString();
    }

    internal static string Format(string kind, double v)
    {
        string s = v.ToString("0.##", CultureInfo.InvariantCulture);
        return kind switch
        {
            ModNumber.Mult => "x" + s,
            ModNumber.Pct => s + "%",
            ModNumber.Plus => v >= 0 ? "+" + s : s,
            _ => s,
        };
    }

    /// <summary>"reward" or "harder" when the description says so plainly, else "".</summary>
    internal static string Tag(string description)
    {
        string d = description.ToLowerInvariant();
        bool reward = d.Contains("loot") || d.Contains("salvage") || d.Contains("drop");
        bool harder = d.Contains("monsters have") || d.Contains("more monsters") || d.Contains("monsters attack")
                      || d.Contains("boss attacks") || d.Contains("hitting harder");
        return reward == harder ? string.Empty : reward ? "reward" : "harder";
    }
}
