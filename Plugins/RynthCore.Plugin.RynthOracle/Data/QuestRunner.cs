// "Run this quest" (2026-11-01): walks a wiki quest's steps (QuestGuide.cs) with RynthNav. Each step that
// isn't done yet (its stamps not all held) is visited in order: RynthNav goes to its coordinates ("/rnav go
// 28.5S, 90.9W"; a step with several places goes to each in turn), and when RynthNav says it arrived the
// step's instructions go to chat. The step is done when the server stamps it ("You've stamped X!") or its
// flags show up, or with Next. A step without coordinates is shown and waits for its stamp or Next. The
// quest's loot (the items its steps kill or loot for) goes on RynthAi's quest loot list while it runs
// ("questloot" broker action, "add <name>") and comes off again when it ends. Since 2026-11-05 the run also makes the
// quest's own loot profile the one in use: RynthAi builds it from LootProfiles\base-quest-dynamic.utl and the
// quest's items ("questloot profile <quest>|<item>..."), and goes back to the profile before when the run ends
// ("questloot profile off"). Since 2026-11-07 a step without coordinates goes to the people and places it
// names (QuestNames: "Talk to Xsao Lann in Fort Tethana" is "/rnav go Xsao Lann || Fort Tethana"), and at
// a step's place RynthAi talks to the person it names ("questloot talk ...") or, on a hand-in ("Return the
// collar to Xsao"), gives them the quest's items got so far ("questloot handin ..."). Inside a dungeon
// (2026-11-07) RynthAi walks the dungeon's map instead of RynthNav: to a step's coordinates ("questloot
// dungeon goto"), or all over it for a step that names none ("dungeon patrol"), fighting, looting the quest's
// items and opening doors (the quest's keys first on a locked one); a step that only takes you in ("Enter
// the Keep") is done once you are in, and "dungeon stop" ends the walk when you come out or the run ends.
// A step's place reached outdoors whose text enters something has RynthAi use the portal there ("dungeon
// enter"). Pump thread; no host calls here (the tests drive it with fake senders).
using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthOracle.Data;

internal sealed class QuestRunner
{
    /// <summary>RynthNav's "goto: arrived" line (RynthNavPlugin FinishGoto).</summary>
    internal const string ArrivedText = "goto: arrived";

    private readonly Func<string, bool> _nav;
    private readonly Func<string, bool> _ai;
    private readonly Action<string> _print;
    private readonly List<string> _lootAdded = new();
    private bool _profileOn;   // RynthAi switched to this quest's loot profile
    private List<string> _targets = new();   // the step's people and places (QuestNames)
    private bool _inDungeon;                  // the character is in a dungeon (OnWhere)
    private bool _dungeonWalk;                // RynthAi walks the dungeon for the run

    /// <summary>RynthAi walks a dungeon for the run (its map: a goto or the patrol).</summary>
    public bool DungeonWalk => _dungeonWalk;
    public bool InDungeon => _inDungeon;

    /// <summary>What the last arrival had RynthAi do: "talk", "handin" or "" (for the view and the tests).</summary>
    public string LastNpc { get; private set; } = "";
    /// <summary>The current step's people and places.</summary>
    public IReadOnlyList<string> Targets => _targets;
    private Func<string, bool> _stampDone = _ => false;

    public GuideQuest? Quest { get; private set; }
    /// <summary>The step being done (index into Quest.Steps), -1 before the first.</summary>
    public int Step { get; private set; } = -1;
    /// <summary>Which of the step's places RynthNav is going to (0-based).</summary>
    public int Place { get; private set; }
    public bool Active => Quest != null;
    public bool Travelling { get; private set; }
    /// <summary>What the runner is doing, for the view.</summary>
    public string Note { get; private set; } = "";
    public int Revision { get; private set; }
    public IReadOnlyList<string> LootAdded => _lootAdded;

    /// <param name="nav">Sends a RynthNav command ("go 28.5S, 90.9W", "stop"); false when RynthNav isn't there.</param>
    /// <param name="ai">Sends a RynthAi command ("questloot add X"); false when RynthAi isn't there.</param>
    /// <param name="print">A line to chat.</param>
    public QuestRunner(Func<string, bool> nav, Func<string, bool> ai, Action<string> print)
    {
        _nav = nav;
        _ai = ai;
        _print = print;
    }

    /// <summary>A step is done when it has stamps and every one is held.</summary>
    public bool StepDone(GuideStep s)
    {
        if (s.Stamps.Count == 0) return false;
        foreach (string st in s.Stamps)
            if (!_stampDone(st)) return false;
        return true;
    }

    /// <summary>
    /// Starts <paramref name="q"/> at its first step not done (from <paramref name="fromStep"/> when given).
    /// <paramref name="stampDone"/> says whether the character holds a stamp. With <paramref name="loot"/>, the
    /// quest's loot goes on RynthAi's quest loot list.
    /// </summary>
    public void Start(GuideQuest q, Func<string, bool> stampDone, bool loot, int fromStep = -1)
    {
        if (Active) Stop("switched quests");
        Quest = q;
        _stampDone = stampDone;
        if (loot)
        {
            AddLoot(q);
            if (q.Loot.Count > 0) _profileOn = _ai("profile " + ProfileLine(q));
        }
        Step = -1;
        if (fromStep >= 0 && fromStep < q.Steps.Count) { Step = fromStep; Place = 0; GoStep(); }
        else Advance(first: true);
        Changed();
    }

    /// <summary>The quest's loot onto RynthAi's quest loot list (what was added comes off at the end).</summary>
    public int AddLoot(GuideQuest q)
    {
        int n = 0;
        foreach (string item in q.Loot)
        {
            if (_lootAdded.Contains(item)) continue;
            if (_ai("add " + item)) { _lootAdded.Add(item); n++; }
        }
        return n;
    }

    private void RemoveLoot()
    {
        if (_dungeonWalk) _ai("dungeon stop");
        _dungeonWalk = false;
        foreach (string item in _lootAdded) _ai("remove " + item);
        _lootAdded.Clear();
        if (_profileOn) _ai("profile off");
        _profileOn = false;
    }

    /// <summary>
    /// "&lt;quest&gt;|&lt;item&gt;|&lt;item&gt;...": the quest and its loot for RynthAi's quest loot profile
    /// ("questloot build ..." / "questloot profile ..."). A '|' in a name becomes '/'.
    /// </summary>
    public static string ProfileLine(GuideQuest q)
    {
        var parts = new List<string> { q.Title.Replace('|', '/') };
        foreach (string item in q.Loot) parts.Add(item.Replace('|', '/'));
        return string.Join("|", parts);
    }

    /// <summary>Has RynthAi write the quest's loot profile (LootProfiles\Quests); false without RynthAi or loot.</summary>
    public bool BuildProfile(GuideQuest q) => q.Loot.Count > 0 && _ai("build " + ProfileLine(q));

    /// <summary>Next: the next place of this step, else the next step not done.</summary>
    public void Next()
    {
        if (Quest == null) return;
        if (Step >= 0 && Step < Quest.Steps.Count && Place + 1 < Quest.Steps[Step].Coords.Count)
        {
            Place++;
            GoPlace();
        }
        else Advance(first: false);
        Changed();
    }

    /// <summary>Back one step (done or not), at its first place.</summary>
    public void Back()
    {
        if (Quest == null || Step <= 0) return;
        Step--;
        Place = 0;
        GoStep();
        Changed();
    }

    /// <summary>Goes to the current step's current place again (RynthNav stopped, the player wandered off).</summary>
    public void Again()
    {
        if (Quest == null) return;
        GoStep(keepPlace: true);
        Changed();
    }

    public void Stop(string why = "stopped")
    {
        if (Quest == null) return;
        if (Travelling) _nav("stop");
        _print($"Quest run {why}: {Quest.Title}.");
        Travelling = false;
        RemoveLoot();
        Quest = null;
        Step = -1;
        Place = 0;
        Note = "";
        Changed();
    }

    /// <summary>A chat line: a stamp (the step may be done) or RynthNav's arrival.</summary>
    public void OnChat(string line)
    {
        if (Quest == null) return;
        string? stamp = GuideStamp.FromChat(line);
        if (stamp != null)
        {
            CheckStep(GuideStamp.Key(stamp));
            return;
        }
        if (Travelling && line.Contains("[RynthNav]", StringComparison.Ordinal) && line.Contains(ArrivedText, StringComparison.OrdinalIgnoreCase))
        {
            Travelling = false;
            GuideStep s = Quest.Steps[Step];
            Note = $"Step {Step + 1}: here. {s.Text}";
            _print($"Step {Step + 1}/{Quest.Steps.Count}: {s.Text}");
            foreach (string n in s.Notes) _print("  " + n);
            DoNpc();
            // "Enter the Hidden Keep at ...": the portal at the step's place takes you in.
            if (!_inDungeon && EntersOnly(s)) _ai("dungeon enter");
            Changed();
        }
        else if (Travelling && line.Contains("[RynthNav]", StringComparison.Ordinal)
                 && (line.Contains("none of these is a place on the map", StringComparison.OrdinalIgnoreCase)
                     || line.Contains("on the map", StringComparison.OrdinalIgnoreCase) && line.Contains("no place called", StringComparison.OrdinalIgnoreCase)))
        {
            // RynthNav doesn't know where the step's names are: say so, and talk to them if they're here.
            Travelling = false;
            string who = _targets.Count > 0 ? string.Join(" or ", _targets) : "it";
            Note = $"Step {Step + 1}: RynthNav doesn't know where {who} is - go there yourself, then Again (to talk) or Next.";
            _print($"Step {Step + 1}/{Quest.Steps.Count}: {Quest.Steps[Step].Text}");
            DoNpc();
            Changed();
        }
        else if (Travelling && line.Contains("[RynthNav] goto:", StringComparison.OrdinalIgnoreCase))
        {
            // RynthNav gave up or was stopped: say so; Again tries once more.
            Travelling = false;
            Note = $"Step {Step + 1}: RynthNav {line.Substring(line.IndexOf("goto:", StringComparison.OrdinalIgnoreCase))} (Again tries once more).";
            Changed();
        }
    }

    /// <summary>The flags changed (a /myquests read): the current step may be done.</summary>
    public void OnFlagsChanged() => CheckStep(null);

    private void CheckStep(string? stampKey)
    {
        if (Quest == null || Step < 0 || Step >= Quest.Steps.Count) return;
        GuideStep s = Quest.Steps[Step];
        if (s.Stamps.Count == 0) return;
        bool mine = stampKey == null || s.Stamps.Exists(x => GuideStamp.Key(x) == stampKey);
        if (!mine) return;
        // The chat line comes before the flag list knows it: the stamp just seen counts.
        bool done = true;
        foreach (string st in s.Stamps)
            if (GuideStamp.Key(st) != stampKey && !_stampDone(st)) { done = false; break; }
        if (!done) return;
        _print($"Step {Step + 1} done.");
        Advance(first: false);
        Changed();
    }

    private void Advance(bool first)
    {
        GuideQuest q = Quest!;
        int i = Step + 1;
        while (i < q.Steps.Count && StepDone(q.Steps[i])) i++;
        if (i >= q.Steps.Count)
        {
            if (first && q.Steps.Count > 0) _print($"Every step of {q.Title} is stamped already.");
            Finish();
            return;
        }
        Step = i;
        Place = 0;
        GoStep();
    }

    private void Finish()
    {
        GuideQuest q = Quest!;
        _print($"Quest run done: {q.Title}." + (q.ExtraStamps.Count > 0 ? " (The wiki lists more stamps: " + string.Join(", ", q.ExtraStamps) + ".)" : ""));
        Travelling = false;
        RemoveLoot();
        Quest = null;
        Step = -1;
        Note = "";
    }

    private void GoStep(bool keepPlace = false)
    {
        GuideQuest q = Quest!;
        GuideStep s = q.Steps[Step];
        if (!keepPlace) Place = 0;
        _targets = QuestNames.TargetsAt(q, Step);
        if (_inDungeon && GoDungeon(q, s)) return;
        if (s.Coords.Count > 0) { GoPlace(); return; }
        // No coordinates: the people and places the step names, in order (RynthNav takes the first on its
        // Atlas; the first step adds the quest's start place).
        List<string> own = QuestNames.Candidates(q, s, Step == 0);
        if (own.Count > 0 && Go(string.Join(" || ", own), $"Step {Step + 1}: going to {string.Join(" or ", own)}"))
            return;
        Travelling = false;
        Note = $"Step {Step + 1}: {s.Text} (Next when done)";
        _print($"Step {Step + 1}/{q.Steps.Count}: {s.Text}");
        foreach (string n in s.Notes) _print("  " + n);
        // Naming nobody ("Hand her each note"): the person of the step before, where the run already is.
        if (own.Count == 0) DoNpc();
    }

    /// <summary>
    /// The character is in a dungeon (<paramref name="inDungeon"/>) or not: the plugin's tick says so. Going in
    /// during the run: a step that only takes you in is done, and the step is walked with the dungeon's map.
    /// Coming out: the dungeon walk ends.
    /// </summary>
    public void OnWhere(bool inDungeon)
    {
        if (inDungeon == _inDungeon) return;
        _inDungeon = inDungeon;
        if (Quest == null) return;
        if (!inDungeon)
        {
            if (_dungeonWalk) { _ai("dungeon stop"); _dungeonWalk = false; }
            Changed();
            return;
        }
        Travelling = false;
        GuideQuest q = Quest;
        if (Step >= 0 && Step < q.Steps.Count && EntersOnly(q.Steps[Step]))
        {
            _print($"Step {Step + 1} done: you're in.");
            Advance(first: false);
        }
        else if (Step >= 0 && Step < q.Steps.Count) GoStep(keepPlace: true);
        Changed();
    }

    /// <summary>A step whose job is going in ("Enter the Keep", "Take the portal to ..."): no stamp, no item.</summary>
    internal static bool EntersOnly(GuideStep s) =>
        s.Stamps.Count == 0 && s.Items.Count == 0
        && System.Text.RegularExpressions.Regex.IsMatch(s.Text, @"\b(?:enter|enters|portal|dungeon|go\s+(?:in|inside|down)|head\s+(?:in|inside|down)|teleport)\b",
               System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// The step inside a dungeon: RynthAi walks its map, to the step's coordinates, else all over it
    /// (fighting, looting, doors). False when RynthAi isn't there.
    /// </summary>
    private bool GoDungeon(GuideQuest q, GuideStep s)
    {
        string keys = string.Join("", QuestKeys(q).ConvertAll(k => " | " + k));
        string cmd;
        string note;
        if (s.Coords.Count > 0 && TrySignedCoords(s.Coords[Math.Clamp(Place, 0, s.Coords.Count - 1)], out double ns, out double ew))
        {
            cmd = $"dungeon goto {ns.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} {ew.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}{keys}";
            note = $"Step {Step + 1}: walking the dungeon to {s.Coords[Math.Clamp(Place, 0, s.Coords.Count - 1)]} (fighting, looting, doors)";
        }
        else
        {
            cmd = "dungeon patrol" + keys;
            note = $"Step {Step + 1}: patrolling the dungeon (fighting, looting the quest's items, doors) - the stamp or Next moves on";
        }
        if (!_ai(cmd)) return false;
        _dungeonWalk = true;
        Travelling = false;
        Note = note;
        _print($"Step {Step + 1}/{q.Steps.Count}: {s.Text}");
        foreach (string n in s.Notes) _print("  " + n);
        return true;
    }

    /// <summary>The quest's keys (its items named key or keyring), for locked doors.</summary>
    internal static List<string> QuestKeys(GuideQuest q)
    {
        var keys = new List<string>();
        void Add(string it)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(it, @"(?<![\p{L}])(?:key|keys|keyring)(?![\p{L}])", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                && !keys.Exists(k => k.Equals(it, StringComparison.OrdinalIgnoreCase))) keys.Add(it.Replace('|', '/'));
        }
        foreach (string it in q.Loot) Add(it);
        foreach (GuideStep st in q.Steps) foreach (string it in st.Items) Add(it);
        return keys;
    }

    /// <summary>"28.5S, 90.9W" as signed numbers: north and east positive.</summary>
    internal static bool TrySignedCoords(string text, out double ns, out double ew)
    {
        ns = ew = 0;
        var m = System.Text.RegularExpressions.Regex.Match(text ?? "",
            @"(\d+(?:\.\d+)?)\s*([NnSs])[\s,]+(\d+(?:\.\d+)?)\s*([EeWw])");
        if (!m.Success) return false;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        ns = double.Parse(m.Groups[1].Value, inv) * (char.ToUpperInvariant(m.Groups[2].Value[0]) == 'S' ? -1 : 1);
        ew = double.Parse(m.Groups[3].Value, inv) * (char.ToUpperInvariant(m.Groups[4].Value[0]) == 'W' ? -1 : 1);
        return true;
    }

    /// <summary>
    /// At the step's place: a hand-in step gives the quest's items got so far to the person it names, a step
    /// that talks ("Talk to", "find", the quest's own people) talks to them (RynthAi, "questloot talk|handin").
    /// </summary>
    private void DoNpc()
    {
        GuideQuest q = Quest!;
        if (Step < 0 || Step >= q.Steps.Count || _targets.Count == 0) return;
        GuideStep s = q.Steps[Step];
        string names = string.Join(" || ", _targets);
        if (QuestNames.IsHandIn(s))
        {
            List<string> items = QuestNames.HandInItems(q, Step);
            _ai("handin " + names + (items.Count > 0 ? " | " + string.Join(" | ", items) : ""));
            LastNpc = "handin";
        }
        else if (QuestNames.IsTalk(q, s))
        {
            _ai("talk " + names);
            LastNpc = "talk";
        }
        else LastNpc = "";
    }

    private void GoPlace()
    {
        GuideStep s = Quest!.Steps[Step];
        string c = s.Coords[Math.Clamp(Place, 0, s.Coords.Count - 1)];
        string more = s.Coords.Count > 1 ? $" (place {Place + 1} of {s.Coords.Count})" : "";
        Go(c, $"Step {Step + 1}: going to {c}{more}");
    }

    private bool Go(string target, string note)
    {
        if (_nav("go " + target))
        {
            Travelling = true;
            Note = note;
            return true;
        }
        Travelling = false;
        Note = note + " - RynthNav isn't loaded: walk there yourself, then Next.";
        return false;
    }

    private void Changed() => Revision++;
}
