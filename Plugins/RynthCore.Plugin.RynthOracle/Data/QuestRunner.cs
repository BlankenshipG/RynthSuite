using System.Linq;
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
// enter"). Since 2026-11-08 (the Acid Axe Quest, 09:01: it went to Suntik Village and stopped there): a step
// that only travels ("Travel to Suntik Village at ...") is done on arrival; a step goes to the coordinates its
// own words give, not the ones its notes name for another route ("Route 2: ... to 16.9N 3.5E"); a step that
// gets items ("grab the Acid Axe on the ground") asks RynthAi which are carried ("questloot have ...": RynthAi
// picks the rest up off corpses and the ground) and is done when RynthAi says it got them all ("Quest: got X"),
// where the run already is when the step names no place; and entering a building isn't taking a portal.
// Pump thread; no host calls here (the tests drive it with fake senders).
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
    private string _fallback = "";            // coordinates to go to when RynthNav knows none of the step's names
    private uint _dungeon;                    // the dungeon's landblock (OnWhere), 0 outdoors
    private string _wentTo = "";              // the coordinates RynthNav last went to outdoors
    /// <summary>The Town Network's landblock: a portal hub on RynthNav's routes, not a quest dungeon.</summary>
    internal const uint TownNetworkLandblock = 0x0007;
    private bool _dungeonWalk;                // RynthAi walks the dungeon for the run
    private int _askedAt = -1;                // the step RynthAi was asked about its items ("have")
    private bool _portalTried;                // a travel step's place: its portal looked for once
    private readonly HashSet<string> _got = new(StringComparer.OrdinalIgnoreCase);   // quest items RynthAi got this run (bare names)
    private bool _insideAsk;                  // in a dungeon: RynthNav was asked which of the step's people is in it ("inside")
    private uint _insideCell;                 // RynthNav's arrival named the cell the step's person is in; walked to once inside

    /// <summary>RynthAi walks a dungeon for the run (its map: a goto or the patrol).</summary>
    public bool DungeonWalk => _dungeonWalk;
    public bool InDungeon => _inDungeon;

    /// <summary>What the last arrival had RynthAi do: "talk", "handin" or "" (for the view and the tests).</summary>
    public string LastNpc { get; private set; } = "";
    /// <summary>The current step's people and places.</summary>
    public IReadOnlyList<string> Targets => _targets;
    private Func<string, bool> _stampDone = _ => false;
    /// <summary>Player society when the run started (filters CH/EW/RB wiki alternatives).</summary>
    private SocietyStanding _society;

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
    /// <paramref name="society"/> picks Celestial Hand / Eldrytch Web / Radiant Blood NPCs, forts and
    /// stronghold portals when the wiki lists all three (otherwise the run loops on the wrong faction).
    /// </summary>
    public void Start(GuideQuest q, Func<string, bool> stampDone, bool loot, int fromStep = -1,
        SocietyStanding society = default)
    {
        if (Active) Stop("switched quests");
        _society = society;
        if (society.IsMember && SocietyQuestResolve.NeedsFilter(q))
        {
            q = SocietyQuestResolve.ForPlayer(q, society);
            _print($"Society run: using {society.SocietyName} ({SocietyQuestResolve.Code(society)}) portals and NPCs.");
        }
        else if (!society.IsMember && SocietyQuestResolve.NeedsFilter(q))
            _print("Society run: you are not in a society — wiki lists all three factions; join one (or pick the right portal yourself).");
        Quest = q;
        _got.Clear();
        _askedAt = -1;
        _portalTried = false;
        // A run starts at the quest's first part (2026-11-07): stamps already held (an earlier run of a
        // repeatable quest) don't finish its steps; only the ones this run gets do.
        var heldAtStart = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (GuideStep st in q.Steps)
            foreach (string stamp in st.Stamps)
                if (stampDone(stamp)) heldAtStart.Add(GuideStamp.Key(stamp));
        _stampDone = stamp => stampDone(stamp) && !heldAtStart.Contains(GuideStamp.Key(stamp));
        if (loot)
        {
            // The quest's items (its loot, and what the rewards say it hands in), then its loot profile, always:
            // with no items it is base-quest-dynamic alone (2026-11-07: the Female Tusker Tusk run built none).
            foreach (string it in QuestNames.RewardItems(q))
                if (!q.Loot.Exists(x => x.Equals(it, StringComparison.OrdinalIgnoreCase))) q.Loot.Add(it);
            AddLoot(q);
            _profileOn = _ai("profile " + ProfileLine(q));
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
    public bool BuildProfile(GuideQuest q) => _ai("build " + ProfileLine(q));   // no items: base-quest-dynamic alone

    /// <summary>Next: the next place of this step, else the next step not done.</summary>
    public void Next()
    {
        if (Quest == null) return;
        if (Step >= 0 && Step < Quest.Steps.Count && Place + 1 < Places(Quest.Steps[Step]).Count)
        {
            Place++;
            GoPlace();
        }
        else Advance(first: false);
        Changed();
    }

    /// <summary>
    /// The step the player picked (2026-11-08: "click on the step you want to do vs using just next or back"):
    /// done or not, at its first place; RynthNav's trip to the old step stops.
    /// </summary>
    public void GoTo(int step)
    {
        if (Quest == null || step < 0 || step >= Quest.Steps.Count) return;
        if (Travelling) _nav("stop");
        Travelling = false;
        Step = step;
        Place = 0;
        _askedAt = -1;
        _print($"Quest run: step {step + 1}/{Quest.Steps.Count}.");
        GoStep();
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
        if (line.Contains(GotPrefix, StringComparison.Ordinal))
        {
            OnGot(line);
            return;
        }
        // No portal at a travel step's place after all: being there is the step.
        if (_portalTried && line.Contains("[RynthAi] Quest: no portal right here", StringComparison.Ordinal)
            && Quest != null && Step >= 0 && Step < Quest.Steps.Count && !_inDungeon && TravelOnly(Quest, Step))
        {
            _portalTried = false;
            _print($"Step {Step + 1} done: you're there.");
            Advance(first: false);
            Changed();
            return;
        }
        if (_insideAsk && Travelling && line.Contains("[RynthNav] inside:", StringComparison.Ordinal))
        {
            // None of the step's people is in this dungeon (by the Atlas): walk it as before.
            _insideAsk = false;
            Travelling = false;
            GuideStep si = Quest.Steps[Step];
            if (!GoDungeon(Quest, si)) Note = $"Step {Step + 1}: {si.Text} (Next when done)";
            DoNpc();
            Changed();
            return;
        }
        if (Travelling && line.Contains("[RynthNav]", StringComparison.Ordinal) && line.Contains(ArrivedText, StringComparison.OrdinalIgnoreCase))
        {
            Travelling = false;
            _insideAsk = false;
            GuideStep s = Quest.Steps[Step];
            // The step's person is inside a dungeon (RynthNav names the cell): RynthAi walks its map there, and the talk
            // or hand-in waits for them on the way (2026-11-11).
            uint cellIn = InsideCell(line);
            if (cellIn != 0)
            {
                _insideCell = cellIn;
                Note = $"Step {Step + 1}: {s.Text}";
                _print($"Step {Step + 1}/{Quest.Steps.Count}: {s.Text}");
                foreach (string n in s.Notes) _print("  " + n);
                if (_inDungeon) WalkToCell(Quest, s);
                else Note = $"Step {Step + 1}: in; walking to {string.Join(" or ", _targets)} once the dungeon loads";
                AskItems(s);
                Changed();
                return;
            }
            Note = $"Step {Step + 1}: here. {s.Text}";
            _print($"Step {Step + 1}/{Quest.Steps.Count}: {s.Text}");
            foreach (string n in s.Notes) _print("  " + n);
            DoNpc();
            if (_inDungeon)
            {
                // RynthNav's route ended inside (the step's place is in the dungeon): walk it as the step says.
                if (EntersOnly(s)) { _print($"Step {Step + 1} done: you're in."); Advance(first: false); }
                else GoDungeon(Quest, s);
            }
            // "Enter the Hidden Keep at ...", "Take the portal to ...": the portal at the step's place takes you in.
            else if (GoesIn(Quest, Step)) _ai("dungeon enter" + PortalHint(s, _society));
            // The step's place is a dungeon's portal and the next step is inside it (2026-11-10, "the route isn't
            // going in the portal"): a portal right there is taken; the step ends in the dungeon (OnWhere).
            else if (TravelOnly(Quest, Step) && NextInside(Quest, Step) && !_portalTried)
            {
                _portalTried = true;
                _ai("dungeon enter near" + PortalHint(s, _society));
                Changed();
                return;
            }
            // "Travel to Suntik Village at ...": being there is the step (2026-11-08: the run stopped there).
            else if (TravelOnly(Quest, Step))
            {
                if (Place + 1 < Places(s).Count) { Place++; GoPlace(); }
                else { _print($"Step {Step + 1} done: you're there."); Advance(first: false); }
                Changed();
                return;
            }
            if (Quest != null && Step >= 0 && Step < Quest.Steps.Count && Quest.Steps[Step] == s) AskItems(s);
            Changed();
        }
        else if (Travelling && line.Contains("[RynthNav]", StringComparison.Ordinal)
                 && (line.Contains("none of these is a place on the map", StringComparison.OrdinalIgnoreCase)
                     || line.Contains("on the map", StringComparison.OrdinalIgnoreCase) && line.Contains("no place called", StringComparison.OrdinalIgnoreCase)))
        {
            // RynthNav doesn't know where the step's names are: coordinates the quest gives elsewhere, else say so.
            Travelling = false;
            if (_fallback.Length > 0)
            {
                string at = _fallback;
                _fallback = "";
                if (Go(at, $"Step {Step + 1}: RynthNav doesn't know the name; going to {at}")) { Changed(); return; }
            }
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
        _portalTried = false;
        _insideAsk = false;
        _insideCell = 0;
        _targets = QuestNames.TargetsAt(q, Step);
        _fallback = "";
        // In a dungeon, a step that happens outside it (a hand-in, "Return to ...") leaves it: the dungeon walk
        // stops and RynthNav takes the run there (2026-11-10: it kept fighting there after the last item).
        if (_inDungeon && HappensOutside(q, Step))
        {
            if (_dungeonWalk) { _ai("dungeon stop"); _dungeonWalk = false; }
            _print($"Step {Step + 1}: leaving the dungeon for it.");
        }
        else if (_inDungeon && _targets.Count > 0 && Places(s).Count == 0 && (QuestNames.IsHandIn(s) || QuestNames.IsTalk(q, s))
                 && _nav("inside " + string.Join(" || ", _targets)))
        {
            // The step's person may be in this dungeon (the Bur, a dungeon's quest giver, 2026-11-11): RynthNav says
            // which cell, and RynthAi walks there; not here, the dungeon is walked as before (OnChat).
            _insideAsk = true;
            Travelling = true;
            Note = $"Step {Step + 1}: looking for {string.Join(" or ", _targets)} in this dungeon";
            AskItems(s);
            return;
        }
        else if (_inDungeon && GoDungeon(q, s)) { AskItems(s); return; }
        // Items carried already (picked up by hand, an earlier run) finish the step before the trip.
        if (Places(s).Count > 0) { GoPlace(); AskItems(s); return; }
        // Coordinates in the step's own words ("Ollar at 71.4N, 28.6W"): there, before any name.
        // The same in the names it gives (the start place "A Renegade Stronghold at 71.4N, 28.6W").
        string? own0 = CoordsIn(s.Text) ?? s.Notes.Select(CoordsIn).FirstOrDefault(c => c != null)
            ?? QuestNames.Candidates(q, s, Step == 0).Select(CoordsIn).FirstOrDefault(c => c != null);
        if (own0 != null && Go(own0, $"Step {Step + 1}: going to {own0}")) { AskItems(s); return; }
        // "Enter the blacksmith building and grab the Acid Axe on the ground": no place of its own, so it is
        // where the run already is (the step before's place); the items are picked up there.
        if (Step > 0 && IsPickup(s) && !QuestNames.IsHandIn(s))
        {
            Travelling = false;
            Note = $"Step {Step + 1}: picking up {string.Join(", ", s.Items)} here (RynthAi's quest loot) - Next when done";
            _print($"Step {Step + 1}/{q.Steps.Count}: {s.Text}");
            foreach (string n in s.Notes) _print("  " + n);
            AskItems(s);
            return;
        }
        _fallback = FallbackCoords(q, Step) ?? "";
        // No coordinates: the people and places the step names, in order (RynthNav takes the first on its
        // Atlas; the first step adds the quest's start place).
        List<string> own = QuestNames.Candidates(q, s, Step == 0);
        if (own.Count > 0 && Go(string.Join(" || ", own), $"Step {Step + 1}: going to {string.Join(" or ", own)}"))
        {
            AskItems(s);
            return;
        }
        Travelling = false;
        Note = $"Step {Step + 1}: {s.Text} (Next when done)";
        _print($"Step {Step + 1}/{q.Steps.Count}: {s.Text}");
        foreach (string n in s.Notes) _print("  " + n);
        // Naming nobody ("Hand her each note"): the person of the step before, where the run already is.
        if (own.Count == 0) DoNpc();
        AskItems(s);
    }

    /// <summary>RynthAi's line for a quest item got (RynthAiPlugin.QuestNpc.cs QuestGotPrefix).</summary>
    internal const string GotPrefix = "[RynthAi] Quest: got ";

    /// <summary>
    /// A step that gets items and has no stamp to say it's done: RynthAi says which are carried already
    /// ("questloot have ..."); the rest come off corpses and the ground (the quest loot list).
    /// </summary>
    private void AskItems(GuideStep s)
    {
        if (s.Items.Count == 0 || QuestNames.IsHandIn(s) || _askedAt == Step) return;
        _askedAt = Step;   // once a step: RynthAi's quest loot list does the rest
        _ai("have " + string.Join(" | ", s.Items.ConvertAll(i => i.Replace('|', '/'))));
    }

    private bool Got(string item) => _got.Contains(BareItem(item));

    /// <summary>"[RynthAi] Quest: got Acid Axe." / "... (already in your pack).": the step that wanted it may be done.</summary>
    private void OnGot(string line)
    {
        string name = line.Substring(line.IndexOf(GotPrefix, StringComparison.Ordinal) + GotPrefix.Length).Trim();
        if (name.EndsWith("(already in your pack).", StringComparison.OrdinalIgnoreCase))
            name = name.Substring(0, name.Length - "(already in your pack).".Length).Trim();
        name = name.TrimEnd('.').Trim();
        if (name.Length == 0) return;
        _got.Add(BareItem(name));
        if (Quest == null || Step < 0 || Step >= Quest.Steps.Count) return;
        GuideStep s = Quest.Steps[Step];
        // Every item of the step is in the pack: on to the next step, its stamp or not (2026-11-10: "when a quest
        // pickups the last loot item it should move to the next step vs keep fighting").
        if (s.Items.Count == 0 || QuestNames.IsHandIn(s) || !s.Items.TrueForAll(Got)) return;
        if (Travelling) { _nav("stop"); Travelling = false; }
        _print($"Step {Step + 1} done: you have {string.Join(", ", s.Items)}.");
        Advance(first: false);
        Changed();
    }

    /// <summary>An item's name without a note in brackets at the end ("Darling's Collar (Quest Item)").</summary>
    internal static string BareItem(string name)
    {
        name = (name ?? "").Trim();
        int open = name.LastIndexOf(" (", StringComparison.Ordinal);
        return open > 0 && name.EndsWith(")", StringComparison.Ordinal) ? name.Substring(0, open).Trim() : name;
    }

    // "Return to ...", "Bring ... to", a hand-in: done out of the dungeon.
    private static readonly System.Text.RegularExpressions.Regex OutsideRx = new(
        @"\b(?:return|returns|go\s+back|head\s+back|bring|deliver|turn\s+(?:it\s+|them\s+)?in|hand\s+(?:it\s+|them\s+)?in|give)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// The step happens out of the dungeon the run is in: nothing to loot or kill there, nothing to go into, and
    /// it hands in or goes back somewhere ("Return to Xsao", "Bring the jaw to ..."), or only travels.
    /// </summary>
    internal static bool HappensOutside(GuideQuest q, int i)
    {
        if (i < 0 || i >= q.Steps.Count) return false;
        GuideStep s = q.Steps[i];
        if (GoesIn(s) || (s.Items.Count > 0 && !QuestNames.IsHandIn(s)) || QuestNames.KillRx.IsMatch(s.Text)) return false;
        return QuestNames.IsHandIn(s) || OutsideRx.IsMatch(s.Text) || TravelOnly(q, i);
    }

    /// <summary>The step after <paramref name="i"/> is done inside a dungeon (its loot, its kills, further in).</summary>
    internal static bool NextInside(GuideQuest q, int i)
    {
        if (i + 1 >= q.Steps.Count) return false;
        GuideStep n = q.Steps[i + 1];
        // Not into a building ("Enter the blacksmith building and grab ...", the Acid Axe Quest): no portal there.
        return !HappensOutside(q, i + 1) && !IntoBuilding(n.Text)
            && (QuestNames.KillRx.IsMatch(n.Text) || GoesIn(n) || (n.Items.Count > 0 && !QuestNames.IsHandIn(n)));
    }

    /// <summary>A step that picks items up ("grab the Acid Axe on the ground", "pick up the note").</summary>
    internal static bool IsPickup(GuideStep s) =>
        s.Items.Count > 0 && System.Text.RegularExpressions.Regex.IsMatch(s.Text,
            @"\b(?:grab|grabs|pick(?:s|ed)?\s+(?:it\s+|them\s+)?up|pick|collect|gather|on\s+the\s+(?:ground|floor|table|shelf)|lying)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// A step whose job is getting somewhere ("Travel to Suntik Village at 16.2N 4.3E."): no stamp, no item,
    /// nobody to talk to, nothing to go into. Done on arrival.
    /// </summary>
    internal static bool TravelOnly(GuideQuest q, int i)
    {
        if (i < 0 || i >= q.Steps.Count) return false;
        GuideStep s = q.Steps[i];
        return s.Stamps.Count == 0 && s.Items.Count == 0
            && System.Text.RegularExpressions.Regex.IsMatch(s.Text, @"^\s*(?:travel|go|head|run|walk|journey|proceed|make\s+your\s+way)\b",
                   System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            && !GoesIn(q, i) && !QuestNames.IsTalk(q, s) && !QuestNames.IsHandIn(s);
    }

    /// <summary>
    /// The places a step goes to: the coordinates its own words give ("Travel to Suntik Village at 16.2N 4.3E"),
    /// not the ones only its notes give for another way there ("Route 2: ... to 16.9N 3.5E"); all of them when
    /// its words give none.
    /// </summary>
    internal static List<string> Places(GuideStep s)
    {
        var inText = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in CoordsRx.Matches(s.Text ?? ""))
            foreach (string c in s.Coords)
                if (SameCoords(c, m.Value) && !inText.Contains(c)) inText.Add(c);
        return inText.Count > 0 ? inText : s.Coords;
    }

    private static readonly System.Text.RegularExpressions.Regex CoordsRx = new(@"(\d+(?:\.\d+)?)\s*([NnSs])[\s,]+(\d+(?:\.\d+)?)\s*([EeWw])");

    /// <summary>
    /// The character is in a dungeon (<paramref name="inDungeon"/>) or not: the plugin's tick says so. Going in
    /// during the run: a step that only takes you in is done, and the step is walked with the dungeon's map.
    /// Coming out: the dungeon walk ends.
    /// </summary>
    public void OnWhere(bool inDungeon) => OnWhere(inDungeon, inDungeon ? 1u : 0u);

    /// <summary>
    /// As above, with the dungeon's landblock: a portal from one dungeon into another (17:29 on 11-07, into
    /// Abayar's Laboratory) walks the step again in the new one, not on the old one's route.
    /// </summary>
    public void OnWhere(bool inDungeon, uint landblock)
    {
        uint was = _dungeon;
        _dungeon = inDungeon ? landblock : 0;
        if (inDungeon == _inDungeon)
        {
            if (inDungeon && was != 0 && was != landblock && Quest != null && !Travelling && Step >= 0 && Step < Quest.Steps.Count)
            {
                if (_dungeonWalk) { _ai("dungeon stop"); _dungeonWalk = false; }
                GoStep(keepPlace: true);
                Changed();
            }
            return;
        }
        _inDungeon = inDungeon;
        if (Quest == null) return;
        // RynthNav is still on its way (its route may pass through a dungeon): its arrival decides.
        if (Travelling) { Changed(); return; }
        if (!inDungeon)
        {
            if (_dungeonWalk) { _ai("dungeon stop"); _dungeonWalk = false; }
            Changed();
            return;
        }
        Travelling = false;
        GuideQuest q = Quest;
        if (_insideCell != 0 && Step >= 0 && Step < q.Steps.Count && _insideCell >> 16 == landblock)
        {
            WalkToCell(q, q.Steps[Step]);
            Changed();
            return;
        }
        if (Step >= 0 && Step < q.Steps.Count && (EntersOnly(q.Steps[Step]) || (TravelOnly(q, Step) && NextInside(q, Step))))
        {
            _print($"Step {Step + 1} done: you're in.");
            Advance(first: false);
        }
        else if (Step >= 0 && Step < q.Steps.Count) GoStep(keepPlace: true);
        Changed();
    }

    /// <summary>A step whose job is going in ("Enter the Keep", "Take the portal to ..."): no stamp, no item.</summary>
    internal static bool EntersOnly(GuideStep s) =>
        s.Stamps.Count == 0 && s.Items.Count == 0 && !IntoBuilding(s.Text)
        && System.Text.RegularExpressions.Regex.IsMatch(s.Text, @"\b(?:enter|enters|portal|dungeon|go\s+(?:in|inside|down)|head\s+(?:in|inside|down)|teleport)\b",
               System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// A step that goes in by a portal, whether or not it also wants a stamp or an item ("Take the portal to
    /// the Hidden Keep and kill ...", "Enter the dungeon at ..."): at its place the nearest portal is used.
    /// </summary>
    internal static bool GoesIn(GuideStep s) =>
        System.Text.RegularExpressions.Regex.IsMatch(s.Text, @"\b(?:enter|enters|portal|dungeon|go\s+(?:in|inside|down)|head\s+(?:in|inside|down)|teleport)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        && !IntoBuilding(s.Text);

    /// <summary>
    /// "Enter the blacksmith building and ...": walking into a building (its door), not a portal; unless the
    /// step also says portal or dungeon.
    /// </summary>
    internal static bool IntoBuilding(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text ?? "",
            @"\benters?\s+(?:the\s+|a\s+|an\s+)?(?:[\p{L}'’\-]+\s+){0,3}?(?:building|house|shop|hut|inn|tavern|cottage|cabin|store|smithy|forge|home|tent|room|bank|temple|chapel|church|mansion|manor)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        && !System.Text.RegularExpressions.Regex.IsMatch(text ?? "", @"\b(?:portal|dungeon|teleport)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Step <paramref name="i"/> goes in by a portal: it says so, its place is a dungeon by name ("Head to the
    /// Tusker Burrow at ..."), or the next step happens in a dungeon ("... at the end of the dungeon").
    /// </summary>
    internal static bool GoesIn(GuideQuest q, int i)
    {
        if (i < 0 || i >= q.Steps.Count) return false;
        GuideStep s = q.Steps[i];
        if (GoesIn(s) || DungeonPlaceRx.IsMatch(s.Text)) return true;
        return i + 1 < q.Steps.Count && System.Text.RegularExpressions.Regex.IsMatch(q.Steps[i + 1].Text,
            @"\b(?:dungeon|inside|in\s+the\s+(?:burrow|cave|caverns?|crypt|mines?|catacombs?|lair|tomb|sewers?|warren|vault|grotto|barrow|labyrinth))\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static readonly System.Text.RegularExpressions.Regex DungeonPlaceRx = new(
        @"\b(?:Burrow|Cave|Caverns?|Crypt|Mines?|Catacombs?|Lair|Tomb|Sewers?|Warren|Vault|Grotto|Barrow|Labyrinth|Dungeon|Hive|Den|Pit|Halls?)\b",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// " &lt;words&gt;" naming the portal the step means (its capitalised words), for RynthAi to prefer; "" when none.
    /// With a society membership, faction/society stronghold steps prefer that society's stronghold name words
    /// so Town Network's three "Stronghold" portals don't loop on the wrong one.
    /// </summary>
    internal static string PortalHint(GuideStep s, SocietyStanding society = default)
    {
        string societyHint = SocietyQuestResolve.PortalHintExtra(s, society);
        if (societyHint.Length > 0) return societyHint;

        var words = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(s.Text, @"\b[A-Z][a-z'\-]{2,}\b"))
        {
            string w = m.Value;
            if (w is "Enter" or "Take" or "The" or "Use" or "Go" or "Head" or "Portal" or "Dungeon" or "Then" or "Kill" or "Talk" or "Find" or "Return"
                or "Faction" or "Society" or "Your" or "Speak" or "Once" or "Several") continue;
            if (!words.Contains(w)) words.Add(w);
        }
        return words.Count == 0 ? "" : " " + string.Join(" || ", words);
    }

    /// <summary>"71.4N, 28.6W" from text that has coordinates, else null.</summary>
    internal static string? CoordsIn(string? text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text ?? "", @"(\d+(?:\.\d+)?)\s*([NnSs])[\s,]+(\d+(?:\.\d+)?)\s*([EeWw])");
        return m.Success ? $"{m.Groups[1].Value}{char.ToUpperInvariant(m.Groups[2].Value[0])}, {m.Groups[3].Value}{char.ToUpperInvariant(m.Groups[4].Value[0])}" : null;
    }

    /// <summary>
    /// Where to go when RynthNav knows none of a step's names: the quest's start place for the first step,
    /// else the last place of the nearest step before it, else the start place (null: none known).
    /// </summary>
    internal static string? FallbackCoords(GuideQuest q, int step)
    {
        string? start = CoordsIn(q.StartsAt);
        if (step == 0 && start != null) return start;
        for (int i = step - 1; i >= 0; i--)
        {
            GuideStep p = q.Steps[i];
            List<string> pl = Places(p);
            if (pl.Count > 0) return CoordsIn(pl[^1]) ?? pl[^1];
            string? inText = CoordsIn(p.Text);
            if (inText != null) return inText;
        }
        return start;
    }

    /// <summary>
    /// The step inside a dungeon: RynthAi walks its map, to the step's coordinates, else all over it
    /// (fighting, looting, doors). False when RynthAi isn't there.
    /// </summary>
    /// <summary>"... (Drogur is in cell 009C0270)" at the end of RynthNav's arrival line: that cell, else 0.</summary>
    internal static uint InsideCell(string line)
    {
        var m = System.Text.RegularExpressions.Regex.Match(line ?? "", @" is in cell ([0-9A-Fa-f]{8})\)");
        return m.Success ? uint.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>
    /// Inside, to the cell RynthNav named: RynthAi walks the dungeon's map there (fighting, doors, the quest's keys),
    /// and the step's talk or hand-in is sent at once; RynthAi keeps looking for the person while it walks.
    /// </summary>
    private void WalkToCell(GuideQuest q, GuideStep s)
    {
        uint cell = _insideCell;
        _insideCell = 0;
        string keys = string.Join("", QuestKeys(q).ConvertAll(k => " | " + k));
        if (_ai($"dungeon to {cell:X8}{keys}"))
        {
            _dungeonWalk = true;
            Note = $"Step {Step + 1}: walking the dungeon to {string.Join(" or ", _targets)} (cell {cell:X8})";
        }
        DoNpc();
    }

    private bool GoDungeon(GuideQuest q, GuideStep s)
    {
        string keys = string.Join("", QuestKeys(q).ConvertAll(k => " | " + k));
        string cmd;
        string note;
        // The step's own coordinates that RynthNav took it to outdoors are the way in, not a place inside.
        List<string> places = Places(s);
        string here = places.Count > 0 ? places[Math.Clamp(Place, 0, places.Count - 1)] : "";
        bool entrance = here.Length > 0 && SameCoords(here, _wentTo);
        if (!entrance && here.Length > 0 && TrySignedCoords(here, out double ns, out double ew))
        {
            cmd = $"dungeon goto {ns.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} {ew.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}{keys}";
            note = $"Step {Step + 1}: walking the dungeon to {here} (fighting, looting, doors)";
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
        List<string> places = Places(Quest!.Steps[Step]);
        string c = places[Math.Clamp(Place, 0, places.Count - 1)];
        string more = places.Count > 1 ? $" (place {Place + 1} of {places.Count})" : "";
        Go(c, $"Step {Step + 1}: going to {c}{more}");
    }

    /// <summary>Two coordinate texts within 0.5 of each other on both axes.</summary>
    internal static bool SameCoords(string a, string b) =>
        TrySignedCoords(a, out double an, out double ae) && TrySignedCoords(b, out double bn, out double be)
        && Math.Abs(an - bn) <= 0.5 && Math.Abs(ae - be) <= 0.5;

    private bool Go(string target, string note)
    {
        if (!_inDungeon && TrySignedCoords(target, out _, out _)) _wentTo = target;
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
