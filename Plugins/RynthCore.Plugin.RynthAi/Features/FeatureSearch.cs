// ============================================================================
//  RynthAi - Features/FeatureSearch.cs
//  The Find a Feature search (pure; RynthAiHostTests' FeatureSearchTests).
//
//  Every typed word must match somewhere in the entry: its name, keywords,
//  summary, where paths, command / open, group or plugin. Case doesn't matter;
//  a word matches a word of the entry it is the start of ("vend" finds Vendors),
//  after light stemming on both sides (a plural s, -ing, -ed: "dying" stays,
//  "deaths" finds Death, "looting" finds Loot). Ranked by where each word hit:
//  the name over the keywords over the command, group, summary and where. No
//  words: every entry, grouped by its group.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Text;

namespace RynthCore.Plugin.RynthAi.Features;

/// <summary>One result: the entry and its score (higher first).</summary>
internal readonly record struct FeatureHit(FeatureEntry Entry, int Score);

/// <summary>One group of the browse view (no words typed): its name and its entries by name.</summary>
internal sealed record FeatureGroup(string Name, List<FeatureEntry> Entries);

internal static class FeatureSearch
{
    // Weights per field a word hit in (the best one counts for that word).
    public const int NameWeight = 100, KeywordWeight = 60, CommandWeight = 45, GroupWeight = 40,
                     SummaryWeight = 30, WhereWeight = 25, PluginWeight = 20;
    /// <summary>A word that is a whole word of the field (not just its start) scores this much more.</summary>
    public const int WholeWordBonus = 10;

    /// <summary>The schema's groups, in the order the browse view shows them (others after, by name).</summary>
    public static readonly string[] GroupOrder =
    {
        "Help", "Windows", "Combat", "Buffs", "Hunting", "Navigation", "Looting", "Vendors", "Inventory", "Crafting",
        "Metas", "Character", "Quests", "Chat", "HUD", "Death", "Fellowship", "Pets", "Scripts", "Server", "Settings",
    };

    /// <summary>The query's words, lower case, without punctuation ("/ra hunt" is "ra", "hunt").</summary>
    public static List<string> Words(string? text)
    {
        var words = new List<string>();
        if (string.IsNullOrEmpty(text)) return words;
        var sb = new StringBuilder();
        foreach (char ch in text)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (ch == '\'' || ch == '’') continue;   // "don't" is "dont"
            else Flush(sb, words);
        }
        Flush(sb, words);
        return words;
    }

    private static void Flush(StringBuilder sb, List<string> words)
    {
        if (sb.Length == 0) return;
        words.Add(sb.ToString());
        sb.Clear();
    }

    /// <summary>Light stemming: a plural s ("vendors"), -ing ("looting"), -ed ("looted"), a doubled last letter after them ("stopped").</summary>
    public static string Stem(string word)
    {
        string w = word;
        if (w.Length > 5 && w.EndsWith("ing", StringComparison.Ordinal)) w = Undouble(w[..^3]);
        else if (w.Length > 4 && w.EndsWith("ed", StringComparison.Ordinal)) w = Undouble(w[..^2]);
        else if (w.Length > 4 && w.EndsWith("ies", StringComparison.Ordinal)) w = w[..^3] + "y";
        else if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal) && !w.EndsWith("us", StringComparison.Ordinal)) w = w[..^1];
        return w;
    }

    /// <summary>"stopp" is "stop"; "kill", "pass" and "buzz" keep their double letter.</summary>
    private static string Undouble(string w) =>
        w.Length >= 3 && w[^1] == w[^2] && !IsVowel(w[^1]) && w[^1] is not ('l' or 's' or 'z') ? w[..^1] : w;

    private static bool IsVowel(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u';

    /// <summary>
    /// How well the typed word <paramref name="q"/> matches the field word <paramref name="w"/>:
    /// 0 no match, 1 the start of it, 2 the whole word (after stemming).
    /// </summary>
    public static int WordMatch(string q, string w)
    {
        if (q.Length == 0 || w.Length == 0) return 0;
        string sq = Stem(q), sw = Stem(w);
        if (sq == sw || q == w) return 2;
        if (sw.StartsWith(sq, StringComparison.Ordinal) || w.StartsWith(q, StringComparison.Ordinal)) return 1;
        return 0;
    }

    /// <summary>The best match of <paramref name="q"/> against any word of <paramref name="field"/>.</summary>
    private static int FieldMatch(string q, List<string> field)
    {
        int best = 0;
        foreach (string w in field)
        {
            int m = WordMatch(q, w);
            if (m > best) { best = m; if (best == 2) break; }
        }
        return best;
    }

    /// <summary>An entry's words, by field (built per search; the catalog is a few hundred entries).</summary>
    private sealed class Fields
    {
        public readonly List<string> Name, Keywords, Command, Group, Summary, Where, Plugin;

        public Fields(FeatureEntry e)
        {
            Name = Words(e.Name);
            Keywords = new List<string>();
            foreach (string k in e.Keywords) Keywords.AddRange(Words(k));
            Command = Words(e.Command);
            Command.AddRange(Words(e.Open));
            Group = Words(e.Group);
            Summary = Words(e.Summary);
            Where = new List<string>();
            foreach (string p in e.Where) Where.AddRange(Words(p));
            Plugin = Words(e.Plugin);
        }
    }

    /// <summary>The score of <paramref name="e"/> for <paramref name="words"/>, or 0 when a word matches nowhere.</summary>
    public static int Score(FeatureEntry e, IReadOnlyList<string> words)
    {
        var f = new Fields(e);
        int total = 0;
        foreach (string q in words)
        {
            int best = 0;
            best = Math.Max(best, Weigh(FieldMatch(q, f.Name), NameWeight));
            best = Math.Max(best, Weigh(FieldMatch(q, f.Keywords), KeywordWeight));
            best = Math.Max(best, Weigh(FieldMatch(q, f.Command), CommandWeight));
            best = Math.Max(best, Weigh(FieldMatch(q, f.Group), GroupWeight));
            best = Math.Max(best, Weigh(FieldMatch(q, f.Summary), SummaryWeight));
            best = Math.Max(best, Weigh(FieldMatch(q, f.Where), WhereWeight));
            best = Math.Max(best, Weigh(FieldMatch(q, f.Plugin), PluginWeight));
            if (best == 0) return 0;   // every word must match somewhere
            total += best;
        }
        return total;
    }

    private static int Weigh(int match, int weight) => match == 0 ? 0 : match == 2 ? weight + WholeWordBonus : weight;

    /// <summary>
    /// The entries every word of <paramref name="query"/> matches, best first (ties by name). No words:
    /// every entry in browse order (by group, then name), each with score 0.
    /// </summary>
    public static List<FeatureHit> Search(IReadOnlyList<FeatureEntry> all, string? query)
    {
        List<string> words = Words(query);
        var hits = new List<FeatureHit>();
        if (words.Count == 0)
        {
            foreach (FeatureGroup g in Grouped(all))
                foreach (FeatureEntry e in g.Entries) hits.Add(new FeatureHit(e, 0));
            return hits;
        }
        foreach (FeatureEntry e in all)
        {
            int s = Score(e, words);
            if (s > 0) hits.Add(new FeatureHit(e, s));
        }
        hits.Sort((a, b) => a.Score != b.Score ? b.Score.CompareTo(a.Score) : CompareNames(a.Entry, b.Entry));
        return hits;
    }

    /// <summary>Every entry by group (the schema's order, then others by name; no group is "Other"), each group by name.</summary>
    public static List<FeatureGroup> Grouped(IReadOnlyList<FeatureEntry> all)
    {
        var byName = new Dictionary<string, FeatureGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (FeatureEntry e in all)
        {
            string g = e.Group.Trim().Length > 0 ? e.Group.Trim() : "Other";
            if (!byName.TryGetValue(g, out FeatureGroup? group)) byName[g] = group = new FeatureGroup(g, new List<FeatureEntry>());
            group.Entries.Add(e);
        }
        var groups = new List<FeatureGroup>(byName.Values);
        groups.Sort((a, b) =>
        {
            int ia = GroupRank(a.Name), ib = GroupRank(b.Name);
            return ia != ib ? ia.CompareTo(ib) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        foreach (FeatureGroup g in groups) g.Entries.Sort(CompareNames);
        return groups;
    }

    private static int GroupRank(string g)
    {
        int i = Array.FindIndex(GroupOrder, x => x.Equals(g, StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? i : GroupOrder.Length;
    }

    private static int CompareNames(FeatureEntry a, FeatureEntry b)
    {
        int c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        return c != 0 ? c : string.Compare(a.Plugin, b.Plugin, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"12 features match 'death'", "1 feature matches 'death'", "No feature matches 'xyz'", "40 features".</summary>
    public static string CountLine(int count, string? query)
    {
        string q = (query ?? string.Empty).Trim();
        if (Words(q).Count == 0) return count == 1 ? "1 feature" : $"{count} features";
        return count switch
        {
            0 => $"No feature matches '{q}'",
            1 => $"1 feature matches '{q}'",
            _ => $"{count} features match '{q}'",
        };
    }
}
