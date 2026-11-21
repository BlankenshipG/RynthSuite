// The quest guide (2026-11-01): Infinite Leaftide's wiki quests (leaftidewiki.com, Category:Quest_Index)
// as RynthILT reads them (WikiQuestParser.cs) and saves them, and as RynthOracle's Quests tab shows
// them: each quest's summary, its walkthrough as steps (coordinates, the QB stamps each step gives,
// the items it names) and its rewards. One JSON file, written by RynthILT and read by RynthOracle
// (this file is linked into RynthILT so both use one reader and writer). No reflection serializer:
// both plugins are NativeAOT.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>One walkthrough step: what to do, where ("28.5S, 90.9W"), the stamps it gives and the items it names.</summary>
internal sealed class GuideStep
{
    public string Text = "";
    /// <summary>The step's sub-lines (tips), cleaned of markup; stamps and empty lines left out.</summary>
    public List<string> Notes = new();
    public List<string> Coords = new();
    /// <summary>The QB stamps (quest flags) this step gives, as the wiki writes them ("Shiny_Red_Stone_Found").</summary>
    public List<string> Stamps = new();
    /// <summary>The {{Itemlink}} items the step names.</summary>
    public List<string> Items = new();
}

/// <summary>One wiki quest.</summary>
internal sealed class GuideQuest
{
    public string Title = "";
    public string Url = "";
    /// <summary>The page's revision id (an unchanged page isn't fetched again).</summary>
    public long Rev;
    public string Level = "";
    public string RecommendedLevel = "";
    public string Type = "";
    public string KillTask = "";
    public string StartsWith = "";
    public string StartsAt = "";
    public string Route = "";
    /// <summary>The repeat timer as the wiki writes it ("20 Hours"); "" = none given.</summary>
    public string Repeat = "";
    /// <summary>The repeat timer in seconds: 0 = one-time (or none given), -1 = given but not understood.</summary>
    public long RepeatSec;
    public string TimeToComplete = "";
    public string Contracts = "";
    public int Qb;
    public List<string> Categories = new();
    public string Overview = "";
    public List<GuideStep> Steps = new();
    /// <summary>QB stamps the page lists that no step could be tied to.</summary>
    public List<string> ExtraStamps = new();
    /// <summary>The items to pick up on the way (named in a step that kills, loots or finds something).</summary>
    public List<string> Loot = new();
    public List<string> Rewards = new();

    /// <summary>Every stamp of the quest, steps first, in order, each once.</summary>
    public IEnumerable<string> AllStamps()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (GuideStep s in Steps)
            foreach (string st in s.Stamps)
                if (seen.Add(GuideStamp.Key(st))) yield return st;
        foreach (string st in ExtraStamps)
            if (seen.Add(GuideStamp.Key(st))) yield return st;
    }

    /// <summary>A daily: repeats within a day (and more than an hour, so a timer typo isn't a daily).</summary>
    public bool IsDaily => RepeatSec > 0 && RepeatSec <= 26 * 3600;
    /// <summary>A weekly: repeats in more than a day, up to about a month.</summary>
    public bool IsWeekly => RepeatSec > 26 * 3600 && RepeatSec <= 31L * 86400;
    /// <summary>A one-time (story) quest: no repeat timer given.</summary>
    public bool IsOneTime => RepeatSec == 0;

    /// <summary>
    /// A kill task (2026-11-21): the Quest Summary box says "Kill Task = Yes" (150 pages), or, when the box
    /// doesn't say, the title does ("... Kill Task", "... Killtask", "Kill: ..."). "Kill Task = False" is not one.
    /// </summary>
    public bool IsKillTask
    {
        get
        {
            string k = KillTask.Trim();
            if (k.Length > 0)
                return k.StartsWith("y", StringComparison.OrdinalIgnoreCase) || k.StartsWith("t", StringComparison.OrdinalIgnoreCase);
            return Title.Contains("Kill Task", StringComparison.OrdinalIgnoreCase)
                   || Title.Contains("Killtask", StringComparison.OrdinalIgnoreCase)
                   || Title.StartsWith("Kill:", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex KillCountRx = new(
        @"\b(?:kill|slay|hunt(?:\s+down)?|defeat|destroy)\s+(?:a\s+total\s+of\s+)?(?<n>\d{1,5})\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>How many kills the walkthrough asks for ("Kill 100 Drudge Lurkers."), from its first step that says; 0 when none does.</summary>
    public int KillTarget
    {
        get
        {
            foreach (GuideStep s in Steps)
            {
                System.Text.RegularExpressions.Match m = KillCountRx.Match(s.Text);
                if (m.Success && int.TryParse(m.Groups["n"].Value, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out int n) && n > 0) return n;
            }
            return 0;
        }
    }

    /// <summary>
    /// The creature a kill task names, as a GuideStamp key ("Drudge Lurker Kill Task" -> "drudgelurker",
    /// "Kill: Rynthid Minions" -> "rynthidminion"): matches the server's kill chat and counter flags.
    /// </summary>
    public string KillCreatureKey
    {
        get
        {
            string t = Title;
            foreach (string cut in new[] { "Kill Task", "Killtask", "Kill:" })
                t = t.Replace(cut, " ", StringComparison.OrdinalIgnoreCase);
            return KillKey(t);
        }
    }

    /// <summary>A creature name as a key, without a plural "s" ("Rynthid Minions" -> "rynthidminion").</summary>
    public static string KillKey(string name)
    {
        string k = GuideStamp.Key(name);
        return k.Length > 4 && k.EndsWith('s') ? k[..^1] : k;
    }
}

/// <summary>Stamp names as the server and the wiki write them, compared alike.</summary>
internal static class GuideStamp
{
    /// <summary>Lower case letters and digits only: "Shiny_Red_Stone_Found", "shinyredstonefound" and "Shiny Red Stone Found" are one stamp.</summary>
    public static string Key(string? stamp)
    {
        if (string.IsNullOrEmpty(stamp)) return "";
        var sb = new StringBuilder(stamp.Length);
        foreach (char ch in stamp)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    private static readonly System.Text.RegularExpressions.Regex Qualifier = new(
        @"^(?<stamp>\S+)\s+(?:(?:on|after|when|at|for|per|if|once|each|every|until|then|from)\b|\().*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// A stamp as the wiki writes it inside {{stamped|...}}, cut to the flag itself (2026-11-21). 125 pages
    /// write "KillTaskDrudgeLurkers_0507 on first completion" and three "You've stamped GleeqeoStart!"; the
    /// words after the flag made the stamp never match /myquests, so every kill task's counter step stayed
    /// unticked. A stamp of plain words with spaces ("Shiny Red Stone Found") is left alone.
    /// </summary>
    public static string Clean(string? stamp)
    {
        if (string.IsNullOrWhiteSpace(stamp)) return "";
        string s = stamp.Trim();
        if (FromChat(s) is string chat) s = chat;
        else if (s.StartsWith("stamped ", StringComparison.OrdinalIgnoreCase)) s = s.Substring(8).Trim();
        System.Text.RegularExpressions.Match m = Qualifier.Match(s);
        if (m.Success) s = m.Groups["stamp"].Value;
        return s.TrimEnd('!', '.', ',', ';', ':', ' ');
    }

    /// <summary>The stamp in the server's "You've stamped X!" line, or null.</summary>
    public static string? FromChat(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        const string Lead = "You've stamped ";
        int i = line.IndexOf(Lead, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        string rest = line.Substring(i + Lead.Length).Trim();
        rest = rest.TrimEnd('!', '.', ' ');
        return rest.Length is > 0 and <= 120 ? rest : null;
    }
}

/// <summary>The whole guide: the quests, where they came from and when.</summary>
internal sealed class QuestGuide
{
    public const int Schema = 1;
    public static readonly QuestGuide Empty = new();

    /// <summary>
    /// The page reader that made the guide (WikiQuestParser.Version; 1 for a guide saved before 2026-11-21).
    /// A guide from an older reader is read from the wiki again whole, not just the pages that changed.
    /// </summary>
    public int Parser = 1;

    /// <summary>"api" (the wiki's API with the player's bot password) or "export" (a Special:Export file).</summary>
    public string Source = "";
    public DateTime UpdatedUtc = DateTime.MinValue;
    public List<GuideQuest> Quests = new();

    public GuideQuest? Find(string title)
    {
        foreach (GuideQuest q in Quests)
            if (string.Equals(q.Title, title, StringComparison.OrdinalIgnoreCase)) return q;
        return null;
    }

    // ── JSON ────────────────────────────────────────────────────────────────

    public string ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", Schema);
            w.WriteNumber("parser", Parser);
            w.WriteString("source", Source);
            w.WriteString("updated", UpdatedUtc == DateTime.MinValue ? "" : UpdatedUtc.ToString("o"));
            w.WriteStartArray("quests");
            foreach (GuideQuest q in Quests)
            {
                w.WriteStartObject();
                w.WriteString("title", q.Title);
                w.WriteString("url", q.Url);
                w.WriteNumber("rev", q.Rev);
                Opt(w, "level", q.Level);
                Opt(w, "recLevel", q.RecommendedLevel);
                Opt(w, "type", q.Type);
                Opt(w, "killTask", q.KillTask);
                Opt(w, "startsWith", q.StartsWith);
                Opt(w, "startsAt", q.StartsAt);
                Opt(w, "route", q.Route);
                Opt(w, "repeat", q.Repeat);
                w.WriteNumber("repeatSec", q.RepeatSec);
                Opt(w, "time", q.TimeToComplete);
                Opt(w, "contracts", q.Contracts);
                w.WriteNumber("qb", q.Qb);
                Arr(w, "categories", q.Categories);
                Opt(w, "overview", q.Overview);
                w.WriteStartArray("steps");
                foreach (GuideStep s in q.Steps)
                {
                    w.WriteStartObject();
                    w.WriteString("text", s.Text);
                    Arr(w, "notes", s.Notes);
                    Arr(w, "coords", s.Coords);
                    Arr(w, "stamps", s.Stamps);
                    Arr(w, "items", s.Items);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                Arr(w, "extraStamps", q.ExtraStamps);
                Arr(w, "loot", q.Loot);
                Arr(w, "rewards", q.Rewards);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void Opt(Utf8JsonWriter w, string name, string value)
    {
        if (!string.IsNullOrEmpty(value)) w.WriteString(name, value);
    }

    private static void Arr(Utf8JsonWriter w, string name, List<string> items)
    {
        if (items.Count == 0) return;
        w.WriteStartArray(name);
        foreach (string s in items) w.WriteStringValue(s);
        w.WriteEndArray();
    }

    /// <summary>Reads a guide; <see cref="Empty"/> when the text isn't one (read leniently: missing fields are empty).</summary>
    public static QuestGuide Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Empty;
            var g = new QuestGuide { Source = Str(root, "source"), Parser = (int)Math.Max(1, Long(root, "parser")) };
            if (DateTime.TryParse(Str(root, "updated"), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out DateTime t))
                g.UpdatedUtc = t.ToUniversalTime();
            if (root.TryGetProperty("quests", out JsonElement qs) && qs.ValueKind == JsonValueKind.Array)
                foreach (JsonElement e in qs.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    var q = new GuideQuest
                    {
                        Title = Str(e, "title"),
                        Url = Str(e, "url"),
                        Rev = Long(e, "rev"),
                        Level = Str(e, "level"),
                        RecommendedLevel = Str(e, "recLevel"),
                        Type = Str(e, "type"),
                        KillTask = Str(e, "killTask"),
                        StartsWith = Str(e, "startsWith"),
                        StartsAt = Str(e, "startsAt"),
                        Route = Str(e, "route"),
                        Repeat = Str(e, "repeat"),
                        RepeatSec = Long(e, "repeatSec"),
                        TimeToComplete = Str(e, "time"),
                        Contracts = Str(e, "contracts"),
                        Qb = (int)Long(e, "qb"),
                        Categories = Strs(e, "categories"),
                        Overview = Str(e, "overview"),
                        ExtraStamps = Stamps(Strs(e, "extraStamps")),
                        Loot = Strs(e, "loot"),
                        Rewards = Strs(e, "rewards"),
                    };
                    if (q.Title.Length == 0) continue;
                    if (e.TryGetProperty("steps", out JsonElement ss) && ss.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement s in ss.EnumerateArray())
                        {
                            if (s.ValueKind != JsonValueKind.Object) continue;
                            q.Steps.Add(new GuideStep
                            {
                                Text = Str(s, "text"),
                                Notes = Strs(s, "notes"),
                                Coords = Strs(s, "coords"),
                                Stamps = Stamps(Strs(s, "stamps")),
                                Items = Strs(s, "items"),
                            });
                        }
                    g.Quests.Add(q);
                }
            return g;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    /// <summary>Stamps cleaned (GuideStamp.Clean) and each once: a guide saved by an older reader reads right at once.</summary>
    private static List<string> Stamps(List<string> raw)
    {
        var list = new List<string>(raw.Count);
        foreach (string r in raw)
        {
            string s = GuideStamp.Clean(r);
            if (s.Length > 0 && !list.Exists(x => GuideStamp.Key(x) == GuideStamp.Key(s))) list.Add(s);
        }
        return list;
    }

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Long(JsonElement o, string name) =>
        o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;

    private static List<string> Strs(JsonElement o, string name)
    {
        var list = new List<string>();
        if (!o.TryGetProperty(name, out JsonElement a) || a.ValueKind != JsonValueKind.Array) return list;
        foreach (JsonElement e in a.EnumerateArray())
            if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s) list.Add(s);
        return list;
    }
}
