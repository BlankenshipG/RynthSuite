// Reads an Infinite Leaftide wiki quest page (MediaWiki wikitext, leaftidewiki.com) into a GuideQuest
// (QuestGuide.cs), 2026-11-01. Pure text work: RynthILT fetches the pages (its WikiFeed) and the offline
// tests feed it the sample pages. Linked into RynthILT.
//
// The pages look like this (the two samples the player sent, "Pistol" Pete Trophy Hunts and
// Mistreated Virindi):
//   {{Leaftide Content}}
//   {{Quest Summary | Level = 180+ | Recommended Level = 275+ | Quest Type = Solo/Fellowship
//     | Starts With = [[Mistreated Virindi]] | Starts At = [[Zaikhal]] | Repeat Timer = 20 Hours | QB = ... }}
//   == Overview ==               (also "== Discord Description ==")
//   == Walkthrough & Notes ==    either numbered steps:  # Talk to ...   with   #* {{stamped|Sneaky_Virindi}}
//                                or plain paragraphs, one per task, with the stamps listed apart:
//   == QB Stamps ==              {{stamped|NephriteTooth}} ...
//   == Rewards ==                {{Rewards Table | Quest Task = {{Itemlink|X}} | Item Reward = * {{Icon Link|Y}} (x80) }}
//   == Images == / == Dialog == / == Update History ==
//   [[Category:Enlightened Coin Quests]]
// Coordinates are written every which way ("28.5S, 90.9W", "62.7s 66.6W", "85.8s,78.8w"): NavTargets reads them.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthOracle.Data;

internal static class WikiQuestParser
{
    public const string WikiBase = "https://www.leaftidewiki.com/";

    /// <summary>
    /// This reader's version, saved in the guide (QuestGuide.Parser). Raise it when the reader changes what
    /// it takes from a page: RynthILT then reads every page again, not only the ones edited since.
    /// 2: stamps cut to the flag ("X on first completion"), kill tasks, the Kill Tasks category and the
    /// Dynamic Stamps page (2026-11-21).
    /// </summary>
    public const int Version = 2;

    /// <summary>The wiki's page of Dynamic Stamps (2026-11-21).</summary>
    public const string DynamicStampsPage = "Dynamic Stamps";
    /// <summary>The wiki's list of kill tasks (2026-11-21; the page "Kill Tasks" redirects here).</summary>
    public const string KillTasksCategory = "Category:Kill Tasks";

    private static readonly Regex DynStampRx = new(@"\{\{\s*stamped\s*\|\s*(Dynamic_[^}|]+?)\s*\}\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Heading3Rx = new(@"^\s*===\s*([^=].*?)\s*===\s*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The Dynamic Stamps page (2026-11-21): "== Network ==" headings, "=== [[Town]] ===" under them, each with
    /// a table "Name | Coordinates | Stamp | Item Reward | Notes" (cells one per line, or on one line split by
    /// "||"). "==Miscellaneous stamps==" has one table whose Notes say where. Rows without a Dynamic_ stamp
    /// ("NO STAMP", "?") are left out, and so is "==Original Unsorted List==" (stamps without an NPC). Each
    /// stamp once, first row wins. Area is the town (the heading's shown text), or "Miscellaneous".
    /// </summary>
    public static List<GuideDynStamp> ParseDynamicStamps(string? wikitext)
    {
        var list = new List<GuideDynStamp>();
        if (string.IsNullOrEmpty(wikitext)) return list;
        string text = CommentRx.Replace(wikitext, "").Replace("\r\n", "\n").Replace('\r', '\n');
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string network = "", town = "";
        bool inTable = false, unsorted = false;
        var cells = new List<string>();

        void Row()
        {
            if (cells.Count == 0 || unsorted) { cells.Clear(); return; }
            string stampCell = cells.Count > 2 ? cells[2] : string.Join(" ", cells);
            Match m = DynStampRx.Match(stampCell);
            if (!m.Success) m = DynStampRx.Match(string.Join(" ", cells));
            if (m.Success)
            {
                string stamp = m.Groups[1].Value.Trim();
                if (seen.Add(stamp.ToLowerInvariant()))
                {
                    bool misc = network.StartsWith("Misc", StringComparison.OrdinalIgnoreCase);
                    string notes = cells.Count > 4 ? Clean(cells[4]) : "";
                    string area = misc ? "Miscellaneous" : town.Length > 0 ? town : network;
                    list.Add(new GuideDynStamp
                    {
                        Area = area,
                        Place = misc ? notes : area,
                        Npc = cells.Count > 0 ? Clean(cells[0]) : "",
                        Coords = cells.Count > 1 ? NavTargets.FirstCoords(Clean(cells[1])) ?? Clean(cells[1]) : "",
                        Stamp = stamp,
                        Reward = cells.Count > 3 ? Clean(cells[3]) : "",
                        Note = misc ? "" : notes,
                    });
                }
            }
            cells.Clear();
        }

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd();
            Match h3 = Heading3Rx.Match(line);
            if (h3.Success) { Row(); town = Clean(h3.Groups[1].Value); continue; }
            Match h2 = HeadingRx.Match(line);
            if (h2.Success)
            {
                Row();
                network = Clean(h2.Groups[1].Value);
                town = "";
                unsorted = network.Contains("Unsorted", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            string t = line.TrimStart();
            if (t.StartsWith("{|", StringComparison.Ordinal)) { inTable = true; cells.Clear(); continue; }
            if (!inTable) continue;
            if (t.StartsWith("|}", StringComparison.Ordinal)) { Row(); inTable = false; continue; }
            if (t.StartsWith("|-", StringComparison.Ordinal)) { Row(); continue; }
            if (t.StartsWith('!')) continue;
            if (t.StartsWith('|'))
                foreach (string c in t.Substring(1).Split("||")) cells.Add(c.Trim());
            else if (cells.Count > 0)
                cells[^1] += " " + t;
        }
        Row();
        return list;
    }

    /// <summary>The page is a quest page (it has the Quest Summary box).</summary>
    public static bool IsQuestPage(string? wikitext) =>
        !string.IsNullOrEmpty(wikitext) && wikitext.IndexOf("{{Quest Summary", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>The page's address on the wiki.</summary>
    public static string UrlOf(string title) => WikiBase + Uri.EscapeDataString(title.Replace(' ', '_')).Replace("%2F", "/");

    private static readonly Regex CommentRx = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex RefRx = new(@"<ref[^>/]*/>|<ref[^>]*>.*?</ref>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex HeadingRx = new(@"^\s*==\s*([^=].*?)\s*==\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex StampRx = new(@"\{\{\s*stamped\s*\|\s*([^}|]+?)\s*\}\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ItemRx = new(@"\{\{\s*Item\s*link\s*\|\s*([^}|]+?)\s*(?:\|[^}]*)?\}\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CategoryRx = new(@"\[\[\s*Category\s*:\s*([^\]|]+?)\s*(?:\|[^\]]*)?\]\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Reads a quest page. <paramref name="title"/> is the page's title; <paramref name="rev"/> its revision id (0 unknown).</summary>
    public static GuideQuest Parse(string title, string wikitext, long rev = 0)
    {
        var q = new GuideQuest { Title = title.Trim(), Url = UrlOf(title.Trim()), Rev = rev };
        string text = RefRx.Replace(CommentRx.Replace(wikitext ?? "", ""), "").Replace("\r\n", "\n").Replace('\r', '\n');

        foreach (Match m in CategoryRx.Matches(text))
        {
            string c = m.Groups[1].Value.Trim();
            if (c.Length > 0 && !q.Categories.Contains(c)) q.Categories.Add(c);
        }

        Dictionary<string, string> box = TemplateParams(text, "Quest Summary");
        q.Level = Clean(Param(box, "Level"));
        q.RecommendedLevel = Clean(Param(box, "Recommended Level", "Recommended Aug Level", "Recommended"));
        q.Type = Clean(Param(box, "Quest Type", "Type"));
        q.KillTask = Clean(Param(box, "Kill Task"));
        q.StartsWith = Clean(Param(box, "Starts With", "NPC"));
        q.StartsAt = Clean(Param(box, "Starts At", "Location"));
        q.Route = Clean(Param(box, "Route"));
        q.Repeat = Clean(Param(box, "Repeat Timer", "Timer", "Repeat"));
        q.RepeatSec = RepeatSeconds(q.Repeat);
        q.TimeToComplete = Clean(Param(box, "Time to Complete"));
        q.Contracts = Clean(Param(box, "Contracts"));
        q.Qb = FirstInt(Clean(Param(box, "QB", "Quest Bonus")));

        List<(string Heading, List<string> Lines)> sections = Sections(text);
        string overview = "";
        List<string>? walk = null;
        var looseStamps = new List<string>();
        foreach (var (heading, lines) in sections)
        {
            string h = heading.ToLowerInvariant();
            if (h.StartsWith("overview") || h.Contains("description"))
            {
                // The Overview wins over the Discord Description when a page has both.
                string o = Clean(string.Join(" ", lines));
                if (o.Length > 0 && (h.StartsWith("overview") || overview.Length == 0)) overview = o;
            }
            else if (h.StartsWith("walkthrough") || h == "steps" || h.StartsWith("quest steps") || h == "notes" && walk == null)
                walk = lines;
            else if (h.Contains("stamp"))
                foreach (string l in lines)
                    foreach (Match m in StampRx.Matches(l))
                        if (GuideStamp.Clean(m.Groups[1].Value) is { Length: > 0 } ls) looseStamps.Add(ls);
            else if (h.StartsWith("reward"))
                q.Rewards = Rewards(string.Join("\n", lines));
        }
        q.Overview = overview.Length > 700 ? overview.Substring(0, 700).TrimEnd() + "..." : overview;
        if (walk != null) q.Steps = Steps(walk);

        // Stamps listed outside the steps: tied to a step by name, else (as many as there are stampless
        // steps) in order, else kept apart.
        var have = new HashSet<string>(StringComparer.Ordinal);
        foreach (GuideStep s in q.Steps) foreach (string st in s.Stamps) have.Add(GuideStamp.Key(st));
        var left = new List<string>();
        foreach (string st in looseStamps)
        {
            if (!have.Add(GuideStamp.Key(st))) continue;
            GuideStep? byName = StepNamed(q.Steps, st);
            if (byName != null) byName.Stamps.Add(st);
            else left.Add(st);
        }
        var bare = q.Steps.FindAll(s => s.Stamps.Count == 0);
        if (left.Count > 0 && left.Count == bare.Count)
        {
            for (int i = 0; i < left.Count; i++) bare[i].Stamps.Add(left[i]);
            left.Clear();
        }
        q.ExtraStamps = left;
        q.Loot = LootItems(q.Steps);
        return q;
    }

    // ── Sections and steps ──────────────────────────────────────────────────

    /// <summary>The page's "== X ==" sections with their lines (the text before the first heading is "").</summary>
    internal static List<(string Heading, List<string> Lines)> Sections(string text)
    {
        var list = new List<(string, List<string>)>();
        string heading = "";
        var lines = new List<string>();
        foreach (string raw in text.Split('\n'))
        {
            Match m = HeadingRx.Match(raw);
            if (m.Success)
            {
                list.Add((heading, lines));
                heading = m.Groups[1].Value.Trim();
                lines = new List<string>();
                continue;
            }
            lines.Add(raw);
        }
        list.Add((heading, lines));
        return list;
    }

    /// <summary>
    /// The walkthrough's steps: its numbered "# " lines with their "#*" / "#:" / "##" lines under them;
    /// or, with no numbered lines, its top-level bullets or its paragraphs (one step each). Sub-sections
    /// ("=== Part 2 ===") only break paragraphs.
    /// </summary>
    internal static List<GuideStep> Steps(List<string> lines)
    {
        var steps = new List<GuideStep>();
        bool numbered = lines.Exists(l => IsTopLevel(l, '#'));
        bool bullets = !numbered && lines.Exists(l => IsTopLevel(l, '*'));
        char lead = numbered ? '#' : '*';

        if (numbered || bullets)
        {
            GuideStep? cur = null;
            foreach (string raw in lines)
            {
                string l = raw.TrimEnd();
                if (l.Length == 0) continue;
                if (IsTopLevel(l, lead))
                {
                    cur = NewStep(l.Substring(1));
                    steps.Add(cur);
                }
                else if (cur != null && l[0] == lead)
                    AddSub(cur, l.TrimStart('#', '*', ':', ';'));
                else if (cur != null && (l[0] == '*' || l[0] == ':'))
                    AddSub(cur, l.TrimStart('#', '*', ':', ';'));
                // Anything else between steps (a paragraph, a table) is left out.
            }
        }
        else
        {
            var para = new StringBuilder();
            void Flush()
            {
                string p = para.ToString().Trim();
                para.Clear();
                if (p.Length == 0) return;
                GuideStep s = NewStep(p);
                if (s.Text.Length > 0 || s.Stamps.Count > 0) steps.Add(s);
            }
            foreach (string raw in lines)
            {
                string l = raw.Trim();
                if (l.Length == 0 || l.StartsWith("===") || l.StartsWith("{|") || l.StartsWith("|}") || l.StartsWith("[[File:", StringComparison.OrdinalIgnoreCase))
                {
                    Flush();
                    continue;
                }
                if (para.Length > 0) para.Append(' ');
                para.Append(l);
            }
            Flush();
        }
        steps.RemoveAll(s => s.Text.Length == 0 && s.Stamps.Count == 0 && s.Coords.Count == 0);
        return steps;
    }

    private static bool IsTopLevel(string l, char lead) =>
        l.Length > 1 && l[0] == lead && l[1] != '*' && l[1] != '#' && l[1] != ':' && l[1] != ';';

    private static GuideStep NewStep(string raw)
    {
        var s = new GuideStep();
        Collect(s, raw);
        s.Text = Clean(raw);
        return s;
    }

    private static void AddSub(GuideStep s, string raw)
    {
        Collect(s, raw);
        string note = Clean(raw);
        if (note.Length > 0) s.Notes.Add(note);
    }

    /// <summary>The stamps, items and coordinates in a line of the step.</summary>
    private static void Collect(GuideStep s, string raw)
    {
        foreach (Match m in StampRx.Matches(raw))
        {
            string st = GuideStamp.Clean(m.Groups[1].Value);
            if (st.Length > 0 && !s.Stamps.Exists(x => GuideStamp.Key(x) == GuideStamp.Key(st))) s.Stamps.Add(st);
        }
        foreach (Match m in ItemRx.Matches(raw))
        {
            string it = m.Groups[1].Value.Trim();
            if (it.Length > 0 && !s.Items.Contains(it, StringComparer.OrdinalIgnoreCase)) s.Items.Add(it);
        }
        foreach (string c in NavTargets.AllCoords(Clean(raw)))
            if (!s.Coords.Contains(c)) s.Coords.Add(c);
    }

    private static readonly Regex LootVerbRx = new(
        @"\b(kill|kills|killing|loot|loots|looted|drop|drops|dropped|collect|collects|gather|find|finds|obtain|obtains|pick\s+up|grab|hunt|hunts|for\s+(a|an|the|their|its|his|her))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The items worth picking up: those named in a step that kills, loots, drops or finds something
    /// (not one only handed in or upgraded), each once, in order.
    /// </summary>
    internal static List<string> LootItems(List<GuideStep> steps)
    {
        var loot = new List<string>();
        foreach (GuideStep s in steps)
        {
            if (s.Items.Count == 0) continue;
            string all = s.Text + " " + string.Join(" ", s.Notes);
            if (!LootVerbRx.IsMatch(all)) continue;
            if (Regex.IsMatch(s.Text, @"^\s*(instead|return|turn\s+in|hand\s+in|give|bring|upgrade)\b", RegexOptions.IgnoreCase)) continue;
            foreach (string it in s.Items)
                if (!loot.Contains(it, StringComparer.OrdinalIgnoreCase)) loot.Add(it);
        }
        return loot;
    }

    /// <summary>
    /// The step a stamp listed apart belongs to, by its name: "NephriteTooth" goes with the step that
    /// talks about the Nephrite, "WinstonsLeg" with Winston's. The single best step, or null.
    /// </summary>
    internal static GuideStep? StepNamed(List<GuideStep> steps, string stamp)
    {
        List<string> tokens = StampTokens(stamp);
        if (tokens.Count == 0) return null;
        GuideStep? best = null;
        int bestScore = 0;
        bool tie = false;
        foreach (GuideStep s in steps)
        {
            var words = new HashSet<string>(Words(s.Text + " " + string.Join(" ", s.Notes) + " " + string.Join(" ", s.Items)), StringComparer.Ordinal);
            int score = 0;
            foreach (string t in tokens)
                foreach (string w in words)
                    if (w.Length >= 4 && (w == t || w.StartsWith(t, StringComparison.Ordinal) || t.StartsWith(w, StringComparison.Ordinal))) { score++; break; }
            if (score > bestScore) { best = s; bestScore = score; tie = false; }
            else if (score == bestScore && score > 0) tie = true;
        }
        return tie ? null : best;
    }

    private static readonly HashSet<string> StampNoise = new(StringComparer.Ordinal)
    { "quest", "stamp", "found", "turnin", "turned", "done", "complete", "completed", "kill", "killed", "the", "obtained", "timer", "flag", "started", "start" };

    /// <summary>"Shiny_Red_Stone_Found" → shiny, red, stone; "WinstonsLeg" → winston, leg (words of 3+ letters, an 's dropped).</summary>
    internal static List<string> StampTokens(string stamp)
    {
        string spaced = Regex.Replace(stamp, @"(?<=[a-z0-9])(?=[A-Z])", " ");
        var list = new List<string>();
        foreach (string w in Words(spaced))
        {
            string t = w.Length > 4 && w.EndsWith("s", StringComparison.Ordinal) ? w.Substring(0, w.Length - 1) : w;
            if (t.Length >= 3 && !StampNoise.Contains(t) && !list.Contains(t)) list.Add(t);
        }
        return list;
    }

    private static IEnumerable<string> Words(string text)
    {
        foreach (Match m in Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9]+"))
        {
            string w = m.Value;
            // "Winston's" reads as "winston" + "s".
            yield return w;
        }
    }

    // ── Rewards ─────────────────────────────────────────────────────────────

    /// <summary>The rewards section as short lines: "For Vial of The Architect's Blood:", then "Enlightened Coin (x80)" ...</summary>
    internal static List<string> Rewards(string section)
    {
        var lines = new List<string>();
        int at = 0;
        bool anyTable = false;
        while (true)
        {
            int i = section.IndexOf("{{Rewards Table", at, StringComparison.OrdinalIgnoreCase);
            if (i < 0) break;
            int end = TemplateEnd(section, i);
            if (end < 0) break;
            anyTable = true;
            Dictionary<string, string> p = SplitParams(section.Substring(i + 2, end - i - 4));
            string task = Clean(Param(p, "Quest Task", "Task"));
            if (task.Length > 0) lines.Add("For " + task + ":");
            foreach (string key in new[] { "Item Reward", "Items", "XP Reward", "Experience", "Luminance Reward", "Luminance", "Other Reward", "Other" })
            {
                string v = Param(p, key);
                if (v.Length == 0) continue;
                foreach (string part in v.Split('\n'))
                {
                    string c = Clean(part.TrimStart('*', ' ', '#'));
                    if (c.Length == 0) continue;
                    lines.Add(key.StartsWith("XP") || key.StartsWith("Exp") ? "XP: " + c : key.StartsWith("Lum") ? "Luminance: " + c : c);
                }
            }
            at = end;
        }
        if (!anyTable)
            foreach (string l in section.Split('\n'))
            {
                string c = Clean(l.TrimStart('*', '#', ':', ' '));
                if (c.Length > 0) lines.Add(c);
            }
        if (lines.Count > 30) lines.RemoveRange(30, lines.Count - 30);
        return lines;
    }

    // ── Templates ───────────────────────────────────────────────────────────

    /// <summary>The first {{<paramref name="name"/> ...}} template's named parameters (keys trimmed, any case).</summary>
    internal static Dictionary<string, string> TemplateParams(string text, string name)
    {
        int i = text.IndexOf("{{" + name, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int end = TemplateEnd(text, i);
        if (end < 0) end = text.Length;
        int bodyEnd = Math.Max(i + 2, end - 2);
        return SplitParams(text.Substring(i + 2, bodyEnd - (i + 2)));
    }

    /// <summary>Where the template starting at <paramref name="start"/> ("{{") ends (just past its "}}"), or -1.</summary>
    private static int TemplateEnd(string text, int start)
    {
        int depth = 0;
        for (int i = start; i < text.Length - 1; i++)
        {
            if (text[i] == '{' && text[i + 1] == '{') { depth++; i++; }
            else if (text[i] == '}' && text[i + 1] == '}') { depth--; i++; if (depth == 0) return i + 1; }
        }
        return -1;
    }

    /// <summary>A template body's "| key = value" parameters, split at the top level only (links and inner templates kept whole).</summary>
    private static Dictionary<string, string> SplitParams(string body)
    {
        var parts = new List<string>();
        int depthT = 0, depthL = 0, from = 0;
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            char n = i + 1 < body.Length ? body[i + 1] : '\0';
            if (c == '{' && n == '{') { depthT++; i++; }
            else if (c == '}' && n == '}') { depthT--; i++; }
            else if (c == '[' && n == '[') { depthL++; i++; }
            else if (c == ']' && n == ']') { depthL--; i++; }
            else if (c == '|' && depthT == 0 && depthL == 0)
            {
                parts.Add(body.Substring(from, i - from));
                from = i + 1;
            }
        }
        parts.Add(body.Substring(from));
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int k = 1; k < parts.Count; k++)
        {
            string p = parts[k];
            int eq = p.IndexOf('=');
            if (eq <= 0) continue;
            string key = Regex.Replace(p.Substring(0, eq), @"\s+", " ").Trim();
            if (key.Length == 0 || map.ContainsKey(key)) continue;
            map[key] = p.Substring(eq + 1).Trim();
        }
        return map;
    }

    private static string Param(Dictionary<string, string> p, params string[] names)
    {
        foreach (string n in names)
            if (p.TryGetValue(n, out string? v) && v.Trim().Length > 0) return v.Trim();
        return "";
    }

    // ── Text ────────────────────────────────────────────────────────────────

    private static readonly Regex FileRx = new(@"\[\[\s*(File|Image)\s*:[^\[\]]*(\[\[[^\]]*\]\][^\[\]]*)*\]\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LinkRx = new(@"\[\[([^\[\]|]*)\|([^\[\]]*)\]\]", RegexOptions.CultureInvariant);
    private static readonly Regex PlainLinkRx = new(@"\[\[([^\[\]|]*)\]\]", RegexOptions.CultureInvariant);
    private static readonly Regex ExtLinkRx = new(@"\[(https?://[^\s\]]+)(?:\s+([^\]]*))?\]", RegexOptions.CultureInvariant);
    private static readonly Regex NamedTemplateRx = new(@"\{\{\s*(?:Item\s*link|Icon\s*Link|Creature\s*link|NPC\s*link|Link)\s*\|\s*([^}|]+?)\s*(?:\|[^{}]*)?\}\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AnyTemplateRx = new(@"\{\{[^{}]*\}\}", RegexOptions.CultureInvariant);
    private static readonly Regex TagRx = new(@"<[^>]+>", RegexOptions.CultureInvariant);
    private static readonly Regex SpaceRx = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>Wikitext as plain text: links as their words, item templates as the item, other templates, files, bold and tags dropped.</summary>
    public static string Clean(string? wikitext)
    {
        if (string.IsNullOrEmpty(wikitext)) return "";
        string s = wikitext;
        s = StampRx.Replace(s, "");
        s = FileRx.Replace(s, "");
        s = CategoryRx.Replace(s, "");
        s = LinkRx.Replace(s, "$2");
        s = PlainLinkRx.Replace(s, m => m.Groups[1].Value.TrimStart(':'));
        s = ExtLinkRx.Replace(s, m => m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : m.Groups[1].Value);
        s = NamedTemplateRx.Replace(s, "$1");
        for (int pass = 0; pass < 4 && s.Contains("{{"); pass++) s = AnyTemplateRx.Replace(s, "");
        s = Regex.Replace(s, @"<br\s*/?>", " ", RegexOptions.IgnoreCase);
        s = TagRx.Replace(s, "");
        s = s.Replace("'''", "").Replace("''", "");
        s = s.Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"");
        s = SpaceRx.Replace(s, " ").Trim();
        // A "[[A]]s" plural leaves "Tortured Raveners" whole; a list leftover "* " at the start goes.
        return s.TrimStart('*', '#', ':', ' ').Trim();
    }

    private static int FirstInt(string s)
    {
        Match m = Regex.Match(s, @"\d+");
        return m.Success && int.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
    }

    /// <summary>
    /// "20 Hours", "7 days", "1 Week", "20h", "3d 12h", "Daily" → seconds; "", "None", "N/A", "Once",
    /// "One time" → 0; anything else -1.
    /// </summary>
    public static long RepeatSeconds(string? repeat)
    {
        if (string.IsNullOrWhiteSpace(repeat)) return 0;
        string r = repeat.Trim().ToLowerInvariant();
        if (r is "none" or "n/a" or "na" or "-" or "once" or "no" or "never" || r.StartsWith("one time") || r.StartsWith("one-time") || r.StartsWith("not repeatable")) return 0;
        if (r.StartsWith("daily")) return 86400;
        if (r.StartsWith("weekly")) return 7 * 86400;
        long total = 0;
        bool any = false;
        foreach (Match m in Regex.Matches(r, @"(\d+(?:\.\d+)?)\s*(weeks?|w|days?|d|hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)\b"))
        {
            double n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            string u = m.Groups[2].Value;
            double mult = u[0] switch { 'w' => 7 * 86400, 'd' => 86400, 'h' => 3600, 'm' => 60, _ => 1 };
            total += (long)Math.Round(n * mult);
            any = true;
        }
        return any && total > 0 ? total : -1;
    }

    // ── Special:Export ──────────────────────────────────────────────────────

    /// <summary>
    /// The pages in a Special:Export XML file: (title, revision id, wikitext), main namespace only.
    /// </summary>
    public static List<(string Title, long Rev, string Text)> ReadExport(System.IO.TextReader xml)
    {
        var pages = new List<(string, long, string)>();
        var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null, IgnoreWhitespace = true };
        using var r = System.Xml.XmlReader.Create(xml, settings);
        string title = "", text = "";
        long rev = 0;
        int ns = 0;
        bool inRevision = false;
        bool advance = true;
        while (true)
        {
            // ReadElementContentAsString leaves the reader on the next node already: no Read then.
            if (advance && !r.Read()) break;
            advance = true;
            if (r.EOF) break;
            if (r.NodeType == System.Xml.XmlNodeType.Element)
            {
                switch (r.LocalName)
                {
                    case "page": title = ""; text = ""; rev = 0; ns = 0; inRevision = false; break;
                    case "title": title = r.ReadElementContentAsString(); advance = false; break;
                    case "ns":
                        int.TryParse(r.ReadElementContentAsString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ns);
                        advance = false;
                        break;
                    case "revision": inRevision = true; break;
                    case "id" when inRevision && rev == 0:
                        long.TryParse(r.ReadElementContentAsString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out rev);
                        advance = false;
                        break;
                    case "text" when inRevision: text = r.ReadElementContentAsString(); advance = false; break;
                }
            }
            else if (r.NodeType == System.Xml.XmlNodeType.EndElement && r.LocalName == "page")
            {
                if (ns == 0 && title.Length > 0) pages.Add((title, rev, text));
            }
        }
        return pages;
    }
}
