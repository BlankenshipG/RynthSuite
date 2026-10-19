using System;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>
/// The main RynthOracle window: a row of tabs and the selected tab's page. Each tab is a
/// partial of this class (Views\*Tab.cs). Recorded on the pump thread, replayed by the engine.
/// </summary>
internal sealed partial class OracleView
{
    public const string TabQuests = "Quests", TabCharacter = "Character",
        TabSkills = "Skills", TabAttributes = "Attributes", TabProgress = "Progress",
        TabAugs = "Augs", TabSociety = "Society",
        TabTitles = "Titles", TabVoid = "Void", TabFellowship = "Fellowship",
        TabBoards = "Leaderboards", TabConquest = "Conquest", TabBreeding = "Pet Breeding",
        TabAbout = "About";
    // The Markers, Legendary and Dynamic Stamps tabs (and RynthILT's Temple guardians) are rows of
    // the Quests tab since 2026-10-18 (QuestsTab.cs, ShowTrackers); a saved tab name of theirs falls
    // back to Quests.

    /// <summary>
    /// A tab: shown where any of its server tags matches the server; no tags = everywhere (retail).
    /// Refresh is the page's Refresh button (Views\RefreshBar.cs); Help its tooltip.
    /// </summary>
    private sealed record TabDef(string Name, string[] Servers, Action<UiWindow> Draw, bool LiveTimers,
                                 Func<RefreshResult> Refresh, Func<string> Help);

    private readonly OracleContext _c;
    private readonly TabDef[] _tabs;
    /// <summary>Asks the plugin to show or hide the Void window (the Void tab's button).</summary>
    public Action<bool>? SetVoidWindow;
    public Func<bool>? VoidWindowVisible;

    public OracleView(OracleContext c)
    {
        _c = c;
        string[] any = Array.Empty<string>();
        _tabs = new TabDef[]
        {
            new(TabQuests, any, DrawQuests, true, () => RefreshQuests(),
                () => "Sends /myquests to the server and reads the reply (every quest, Legendary quest, marker and stamp)."),
            new(TabCharacter, any, DrawCharacter, true, RefreshCharacter,
                () => "Re-reads your level, experience, buffs and skills from the game now (and RynthAi's session)."),
            // Skills / Attributes / Progress: RynthCore draws its Skills panel body under the tab
            // strip (SkillsEmbed). The engine's panel reads the game itself, so there is no Refresh row.
            new(TabSkills, any, DrawEmbedded, false, RefreshCharacter,
                () => "RynthCore's skills panel: raise and train skills."),
            new(TabAttributes, any, DrawEmbedded, false, RefreshCharacter,
                () => "RynthCore's skills panel: attributes and vitals."),
            new(TabProgress, any, DrawEmbedded, false, RefreshCharacter,
                () => "RynthCore's skills panel: progression planners (Infinite Leaftide)."),
            new(TabAugs, any, DrawAugs, true, RefreshCharacterAndQuests,
                () => "Re-reads your character from the game and sends /myquests for the gem timers."),
            new(TabSociety, any, DrawSociety, true, RefreshCharacterAndQuests,
                () => "Re-reads your society standing from the game and sends /myquests for today's ribbons and quest timers."),
            new(TabTitles, any, DrawTitles, false, RefreshTitles,
                () => "Re-reads the titles you hold from the game."),
            new(TabVoid, any, DrawVoid, true, RefreshVoid,
                () => "Re-reads your buffs from the game and drops the spell timers that ran out."),
            new(TabFellowship, any, DrawFellowship, false, RefreshFellowship,
                () => "Re-reads the fellowship and its members from the game."),
            // Server-specific: each parser and its commands only exist on its own server.
            new(TabBoards, new[] { Data.ServerTags.Conquest, Data.ServerTags.Aelrynth }, DrawBoards, false, RefreshBoards, BoardsHelp),
            new(TabConquest, new[] { Data.ServerTags.Conquest }, DrawConquest, true, RefreshConquest, ConquestHelp),
            new(TabBreeding, new[] { Data.ServerTags.InfiniteLeaftide }, DrawBreeding, false, RefreshBreeding,
                () => "The pet breeding list comes from RynthILT, read again every few seconds while this tab is open."),
            new(TabAbout, any, DrawAbout, false, RefreshAbout,
                () => "Loads the quest list and server pack from their files again and asks RynthCore which server this is."),
        };
    }

    private bool Visible(TabDef t)
    {
        if (t.Servers.Length == 0) return true;
        foreach (string s in t.Servers) if (_c.Server.Is(s)) return true;
        return false;
    }

    /// <summary>The selected tab's name; a tab not available on this server falls back to Quests.</summary>
    public string Tab
    {
        get
        {
            foreach (TabDef t in _tabs)
                if (t.Name == _c.Settings.TabName && Visible(t)) return t.Name;
            return TabQuests;
        }
        set
        {
            _c.Settings.TabName = value;
            _c.Settings.Save();
        }
    }

    /// <summary>Tabs whose content changes by the second (timers), so the window re-records often.</summary>
    public bool TabHasLiveTimers
    {
        get
        {
            string tab = Tab;
            foreach (TabDef t in _tabs) if (t.Name == tab) return t.LiveTimers;
            return false;
        }
    }

    /// <summary>For tests: the names of the tabs this server shows.</summary>
    internal string[] VisibleTabs() => Array.ConvertAll(Array.FindAll(_tabs, Visible), t => t.Name);

    public void Draw(UiWindow w)
    {
        string tab = Tab;
        bool first = true;
        TabDef? current = null;
        foreach (TabDef t in _tabs)
        {
            if (!Visible(t)) continue;
            if (!first) w.SameLine();
            first = false;
            string name = t.Name;
            w.ToggleButton(name, "tab." + name, tab == name, () => Tab = name, small: true);
            if (tab == name) current = t;
        }
        w.Separator();

        if (!_c.InWorld && tab != TabAbout)
        {
            w.TextDisabled("Log in to a character to see this page.");
            return;
        }

        // Skills / Attributes / Progress: nothing more is recorded. The rest of the window is
        // the region RynthCore fills with its Skills panel body (see SkillsEmbed), so the page
        // has no Refresh row and no child of its own to cover it.
        if (SkillsEmbed.IsEmbedTab(tab)) return;

        if (current != null) DrawRefreshRow(w, current);
        w.BeginChild("page." + tab, 0f, 0f, border: false);
        (current?.Draw ?? DrawAbout)(w);
        w.EndChild();
    }

    /// <summary>The Skills-family tabs record nothing: <see cref="Draw"/> returns before it would call this.</summary>
    private void DrawEmbedded(UiWindow w) { }

    // ── Shared helpers ──────────────────────────────────────────────────────

    /// <summary>"Done" in green or "--" in grey, the status column most lists start with.</summary>
    private static void DoneMark(UiWindow w, bool done) =>
        w.TextColored(done ? UiColors.Green : UiColors.Grey, done ? "done" : "--");

    private static string Cut(string s, int max) => s.Length <= max ? s : s.Substring(0, Math.Max(0, max - 3)) + "...";

    /// <summary>"Label  value" with the value at a fixed column.</summary>
    private static void Field(UiWindow w, string label, string value, float x, float valueX)
    {
        if (x > 0f) w.SameLine(x);
        w.TextDisabled(label);
        w.SameLine(valueX);
        w.Text(value);
    }

    /// <summary>Prints an entry's name, status, notes and wiki link in chat (upstream "thinks" them). Quests print more: <see cref="OracleContext.PrintQuest"/>.</summary>
    private void PrintEntry(string name, string flag, string status, string details, string url)
    {
        _c.Print(flag.Length > 0 ? $"{name} ({flag}){(status.Length > 0 ? " - " + status : "")}" : name);
        if (details.Length > 0) _c.Print(details);
        if (url.Length > 0) _c.Print(url);
    }

    /// <summary>
    /// The quest trackers' refresh row (Society, Augs, Conquest):
    /// a small Refresh (the same /myquests read as the Quests tab's) and how fresh the flags are,
    /// or why they can't be read. <paramref name="what"/> names what the flags show ("stamps").
    /// </summary>
    private void QuestFlagsBar(UiWindow w, string id, string what)
    {
        QuestFlagStore flags = _c.Flags;
        w.SmallButton("Refresh", id + ".refresh", () => _c.RequestQuests());
        w.Tooltip("Reads your quest flags again (/myquests), as the Quests tab's Refresh does.");
        w.SameLine();
        int wait = flags.ThrottleSecondsLeft(_c.NowMs);
        if (flags.ServerDisabled)
            w.TextColored(UiColors.Red, "This server has /myquests turned off, so " + what + " can't be read.");
        else if (flags.Loading)
            w.TextColored(UiColors.Yellow, "Reading your quest flags...");
        else if (flags.LastReadUtc == DateTime.MinValue)
            w.TextDisabled("Quest flags not read yet: Refresh to see your " + what + ".");
        else
            w.TextDisabled($"Flags read at {flags.LastReadUtc.ToLocalTime():HH:mm}" + (wait > 0 ? $" (Refresh again in {wait}s)" : ""));
    }

    /// <summary>Previous / page n of m / next, with the page kept in range.</summary>
    private static int Pager(UiWindow w, string id, int page, int count, int pageSize, Action<int> setPage)
    {
        int pages = Math.Max(1, (count + pageSize - 1) / pageSize);
        page = Math.Clamp(page, 0, pages - 1);
        int p = page;
        w.SmallButton("<", id + ".prev", () => setPage(Math.Max(0, p - 1)));
        w.SameLine();
        w.Text($"Page {page + 1} of {pages}");
        w.SameLine();
        w.SmallButton(">", id + ".next", () => setPage(Math.Min(pages - 1, p + 1)));
        return page;
    }
}
