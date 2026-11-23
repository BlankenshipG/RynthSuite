// Who and where a wiki quest step names (2026-11-07). Most steps have no coordinates (5,739 of the
// guide's 7,091): "Talk to Xsao Lann in Fort Tethana.", "Return the collar to Xsao for your rewards.",
// "Take the note to Ahyara and she will sign it and hand you Congratulations (Nasun ibn Tifar & Ahyara)."
// Run this quest goes to the names instead ("/rnav go Xsao Lann || Fort Tethana": RynthNav takes the
// first one on its Atlas as a whole word or more) and, there, has RynthAi talk to the person or hand
// them the quest's items ("questloot talk|handin ..."). Host-free: the tests drive it.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthOracle.Data;

internal static class QuestNames
{
    /// <summary>Names one step offers at most.</summary>
    public const int MaxNames = 6;

    // A name: capitalised words, joined by a space, by "ibn", "of", "the"..., or by "al-"/"el-" ("Mara al-Luq").
    private const string Tok = @"[A-Z][\p{L}'’]*(?:-[A-Z][\p{L}'’]*)*";
    private static readonly Regex NameRx = new(
        Tok + @"(?:(?:\s+(?:ibn|bin|of|the|de|du|da|la|le|von|van)\s+|\s+(?:al|el)-|\s+)" + Tok + @")*",
        RegexOptions.CultureInvariant);

    // A step's first word, when it starts a name ("Talk to ...", "Return ..."): not part of it.
    private static readonly HashSet<string> Lead = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "this", "that", "these", "those", "then", "once", "after", "before", "when", "while", "if",
        "you", "your", "he", "she", "they", "it", "his", "her", "their", "there", "here", "now", "next", "also",
        "talk", "speak", "ask", "tell", "take", "bring", "return", "give", "hand", "deliver", "show", "turn", "go",
        "run", "head", "walk", "travel", "enter", "exit", "leave", "use", "kill", "slay", "defeat", "loot", "find",
        "collect", "gather", "get", "open", "read", "visit", "see", "meet", "wait", "repeat", "continue", "follow",
        "search", "look", "inside", "outside", "at", "on", "in", "to", "from", "into", "with", "by", "near", "for",
        "note", "notes", "route", "step", "tip", "warning", "optional", "quest", "item", "items", "reward", "rewards",
        "portal", "recall", "and", "or", "but", "not", "all", "each", "every", "any", "some", "both", "either", "i",
        "qb", "xp", "level", "lvl", "stamp", "stamps", "flag", "up", "down", "left", "right", "keep", "complete",
        "close", "escape", "opposite", "npc", "npcs", "dereth", "please", "beware", "remember", "however", "otherwise",
        "first", "second", "third", "finally", "last", "again", "back", "around", "through", "across", "inner", "outer",
        "spotted", "seen", "weak", "strong", "immune", "resistant", "you'll", "you’ll", "you're", "you’re", "it's", "it’s",
        "don't", "don’t", "do", "does", "be", "is", "are", "was", "will", "can", "may", "must", "should",
    };

    // Words that are never a place or a person on their own (damage types in "(Weak to Acid)").
    private static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "acid", "fire", "frost", "cold", "lightning", "electric", "pierce", "piercing", "bludgeon", "bludgeoning",
        "slash", "slashing", "nether", "void", "health", "stamina", "mana", "loot", "boss", "bosses", "key", "keys",
        "hour", "hours", "minute", "minutes", "day", "days", "week", "weeks", "team", "group", "fellow", "fellowship",
    };

    // A direction starts a place's name ("North Yaraq Outpost"), so it isn't trimmed off; alone it is no name.
    private static readonly HashSet<string> Directions = new(StringComparer.OrdinalIgnoreCase)
    {
        "north", "south", "east", "west", "northeast", "northwest", "southeast", "southwest", "n", "s", "e", "w",
    };

    // A hand-in: return/bring/deliver/show, give or hand (not "hand you"/"give you"), turn in, take ... to.
    private static readonly Regex HandInRx = new(
        @"\b(?:return|returns|returned|bring|deliver|show)\b"
        + @"|\b(?:hand|give)\b(?!\s+(?:you|it\s+to\s+you|them\s+to\s+you)\b)"
        + @"|\bturn(?:s|ed)?\s+(?:\w+\s+){0,3}?in\b"
        + @"|\btake\b[^.;]*?\bto\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The names <paramref name="s"/> gives, best first: the quest's own people ("starts with") the step
    /// mentions, then the step's names in order, a place it comes "from" last. Item names, the quest's
    /// title and its stamps are left out. For the first step the quest's start place comes last.
    /// </summary>
    public static List<string> Candidates(GuideQuest q, GuideStep s, bool firstStep = false)
    {
        var names = new List<string>();
        var later = new List<string>();
        string text = Strip(q, s.Text);

        foreach (string person in Split(q.StartsWith))
        {
            foreach (string w in person.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (w.Length < 3 || Lead.Contains(w)) continue;
                if (!WholeWord(text, w.Trim('"', '“', '”'))) continue;
                Add(names, person);
                // '"Pistol" Pete': the Atlas may write it without the quotes.
                string plain = person.Replace("\"", "").Replace("“", "").Replace("”", "").Trim();
                if (plain != person) Add(names, plain);
                break;
            }
        }

        foreach (Match m in NameRx.Matches(text))
        {
            string name = TrimLead(m.Value);
            if (name.Length < 3 || Lead.Contains(name) || Directions.Contains(name) || NotNames.Contains(name) || IsExcluded(q, s, name)) continue;
            // One word starting a sentence ("Unlock the door", "Jump down", "Just beyond..."): a name only when
            // the quest knows it (its people, start, title) or the step writes it with a capital elsewhere too.
            int nameAt = m.Index + Math.Max(0, m.Value.IndexOf(name, StringComparison.Ordinal));   // after "Kill ", "Talk to "...
            if (!name.Contains(' ') && SentenceStart(text, nameAt) && !KnownToQuest(q, name) && !CapitalElsewhere(text, name, nameAt))
                continue;
            // "From Yaraq follow the paths...": where it starts from, not where it goes.
            bool from = Regex.IsMatch(text.Substring(0, m.Index), @"\bfrom\s+(?:the\s+)?$", RegexOptions.IgnoreCase);
            // A person already named in full ("Xsao" after "Xsao Lann"): once.
            bool part = names.Exists(n => n.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase) || n.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (part) continue;
            Add(from ? later : names, name);
        }
        foreach (string n in later) Add(names, n);
        if (firstStep)
            foreach (string at in Split(q.StartsAt, people: false)) Add(names, at);
        // A name that is only part of another ("Xsao" of Xsao Lann, "Candeth" of Candeth Keep): the longer one.
        // (Not inside coordinates written with a place: "1.5N, 71.6W in Fort Tethana" keeps Fort Tethana.)
        names.RemoveAll(n => names.Exists(o => !ReferenceEquals(o, n) && !o.Any(char.IsDigit) && WordsOf(o).IsProperSupersetOf(WordsOf(n))));
        if (names.Count > MaxNames) names.RemoveRange(MaxNames, names.Count - MaxNames);
        return names;
    }

    /// <summary>
    /// The names for step <paramref name="i"/>: its own, or, when it names nobody ("Hand her each note"),
    /// the step before's (where the run already is).
    /// </summary>
    public static List<string> TargetsAt(GuideQuest q, int i)
    {
        if (i < 0 || i >= q.Steps.Count) return new List<string>();
        List<string> c = Candidates(q, q.Steps[i], i == 0);
        if (c.Count == 0 && i > 0) c = Candidates(q, q.Steps[i - 1], i - 1 == 0);
        return c;
    }

    // Talking: talk, speak, ask, tell, visit, meet, see, find, greet, report, listen.
    private static readonly Regex TalkRx = new(@"\b(?:talk|talks|speak|speaks|ask|asks|tell|visit|meet|see|find|greet|report|listen|chat)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The step talks to someone: it says so ("Talk to", "find"), or names one of the quest's own people.</summary>
    public static bool IsTalk(GuideQuest q, GuideStep s)
    {
        // A note in brackets ("(see map)") isn't what the step does.
        string text = Regex.Replace(Strip(q, s.Text), @"\([^)]*\)", " ");
        // "dropped by Female Tuskers", "kill the Captain": monsters, nobody to talk to (2026-11-07).
        if (KillRx.IsMatch(text) && !TalkRx.IsMatch(text)) return false;
        if (TalkRx.IsMatch(text)) return true;
        foreach (string person in Split(q.StartsWith))
            if (person.Length >= 3 && WholeWord(text, person.Split(' ')[0])) return true;
        return false;
    }

    internal static readonly Regex KillRx = new(@"\b(?:dropped\s+by|drops?\s+from|kill|kills|slay|slays|defeat|defeats|loot|loots|hunt)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The items the rewards say the quest hands in ("For Hand in Female Tusker Tusk:", "For Vial of The
    /// Architect's Blood:"): the run loots them (2026-11-07, the wiki lists no loot for such quests).
    /// </summary>
    public static List<string> RewardItems(GuideQuest q)
    {
        var list = new List<string>();
        foreach (string r in q.Rewards)
        {
            Match m = RewardForRx.Match(r ?? "");
            if (!m.Success) continue;
            string item = m.Groups["item"].Value.Trim();
            if (item.Length < 3 || Regex.IsMatch(item, @"^(?:completing|completion|finishing|each|the\s+quest|turning)", RegexOptions.IgnoreCase)) continue;
            if (!list.Exists(x => x.Equals(item, StringComparison.OrdinalIgnoreCase))) list.Add(item);
        }
        return list;
    }

    private static readonly Regex RewardForRx = new(@"^For\s+(?:(?:Hand(?:ing)?\s+in|turning\s+in|giving|returning)\s+)?(?:the\s+|a\s+|an\s+)?(?<item>[^:]+?)\s*:\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The step hands something over ("Return the collar to Xsao", "Take the note to Ahyara").</summary>
    public static bool IsHandIn(GuideStep s) => HandInRx.IsMatch(Strip(null, s.Text));

    /// <summary>
    /// What to hand over at step <paramref name="step"/>: the quest's items got before it (its loot and the
    /// items earlier steps name), not the ones this step gives. Those the step's words name ("the collar":
    /// Darling's Collar) when there are any, else all of them.
    /// </summary>
    public static List<string> HandInItems(GuideQuest q, int step)
    {
        // Only what is in hand by now: the items of the steps before this one, and loot no step names.
        var got = new List<string>();
        for (int i = 0; i < step && i < q.Steps.Count; i++)
            foreach (string it in q.Steps[i].Items) Add(got, it);
        foreach (string it in q.Loot)
            if (!q.Steps.Exists(st => st.Items.Exists(x => x.Equals(it, StringComparison.OrdinalIgnoreCase)))) Add(got, it);
        if (step >= 0 && step < q.Steps.Count)
            got.RemoveAll(it => q.Steps[step].Items.Exists(x => x.Equals(it, StringComparison.OrdinalIgnoreCase)));
        if (step < 0 || step >= q.Steps.Count) return got;

        string text = q.Steps[step].Text;
        var named = got.FindAll(it =>
        {
            foreach (string w in Bare(it).Split(new[] { ' ', '\'', '’' }, StringSplitOptions.RemoveEmptyEntries))
                if (w.Length >= 4 && !Lead.Contains(w) && WholeWord(text, w)) return true;
            return false;
        });
        return named.Count > 0 ? named : got;
    }

    private static bool IsExcluded(GuideQuest q, GuideStep s, string name)
    {
        if (name.Equals(q.Title, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (string it in s.Items) if (Bare(it).Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (string it in q.Loot) if (Bare(it).Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (string st in s.Stamps) if (st.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The text without the item names in it (a person named only inside an item's name isn't the step's).</summary>
    private static string Strip(GuideQuest? q, string text)
    {
        text ??= "";
        if (q == null) return text;
        var items = new List<string>(q.Loot);
        foreach (GuideStep st in q.Steps) items.AddRange(st.Items);
        items.Sort((a, b) => b.Length.CompareTo(a.Length));
        foreach (string it in items)
            if (it.Length > 0) text = text.Replace(it, " ", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private static string TrimLead(string name)
    {
        string[] w = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int i = 0;
        // "Using the map", "Killing Burnja", "Eventually you...": a sentence's first word, not a name.
        while (i < w.Length && (Lead.Contains(w[i]) || (i == 0 && IsAdverbOrVerb(w[i])))) i++;
        if (i >= w.Length) return "";
        int j = w.Length - 1;
        while (j > i && Lead.Contains(w[j])) j--;
        string n = string.Join(" ", w, i, j - i + 1).Trim('\'', '’', '-');
        if (n.EndsWith("'s", StringComparison.OrdinalIgnoreCase) || n.EndsWith("’s", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 2);
        return n;
    }

    private static bool IsAdverbOrVerb(string w) =>
        w.Length > 4 && (w.EndsWith("ing", StringComparison.OrdinalIgnoreCase) || w.EndsWith("ly", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// "Nasun ibn Tifar / Ahyara", "Jarvis Hammerstone (Optional)": the separate names, notes in brackets
    /// left off. A place ("1.5N, 71.6W in Fort Tethana") is not split at its comma.
    /// </summary>
    internal static IEnumerable<string> Split(string s, bool people = true)
    {
        string pattern = people ? @"\s*(?:/|;|,|\bor\b|\band\b)\s*" : @"\s*(?:\s/\s|;|\bor\b)\s*";
        foreach (string part in Regex.Split(s ?? "", pattern, RegexOptions.IgnoreCase))
        {
            string p = Regex.Replace(part, @"\s*\([^)]*\)", "").Trim();
            if (p.Length >= 2) yield return p;
        }
    }

    private static bool SentenceStart(string text, int at)
    {
        string before = text.Substring(0, at).TrimEnd();
        return before.Length == 0 || ".!?:;-–(\"".IndexOf(before[^1]) >= 0;
    }

    private static bool KnownToQuest(GuideQuest q, string name) =>
        WholeWord(q.StartsWith, name) || WholeWord(q.StartsAt, name) || WholeWord(q.Title, name);

    private static bool CapitalElsewhere(string text, string name, int at)
    {
        foreach (Match m in Regex.Matches(text, @"(?<![\p{L}\d])" + Regex.Escape(name) + @"(?![\p{L}\d])", RegexOptions.CultureInvariant))
            if (m.Index != at && !SentenceStart(text, m.Index)) return true;
        return false;
    }

    private static HashSet<string> WordsOf(string name) =>
        new(Regex.Split(name, @"[^\p{L}\d'’-]+").Where(w => w.Length > 0), StringComparer.OrdinalIgnoreCase);

    private static string Bare(string item)
    {
        item = (item ?? "").Trim();
        int open = item.LastIndexOf(" (", StringComparison.Ordinal);
        return open > 0 && item.EndsWith(")", StringComparison.Ordinal) ? item.Substring(0, open) : item;
    }

    private static bool WholeWord(string text, string word) =>
        Regex.IsMatch(text, @"(?<![\p{L}\d])" + Regex.Escape(word) + @"(?![\p{L}\d])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static void Add(List<string> list, string s)
    {
        s = s.Trim();
        if (s.Length > 0 && !list.Exists(x => x.Equals(s, StringComparison.OrdinalIgnoreCase))) list.Add(s);
    }
}
