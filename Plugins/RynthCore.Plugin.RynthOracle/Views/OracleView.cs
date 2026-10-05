using System;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>
/// The main RynthOracle window: a row of tabs and the selected tab's page. Each tab is a
/// partial of this class (Views\*Tab.cs). Recorded on the pump thread, replayed by the engine.
/// </summary>
internal sealed partial class OracleView
{
    public const string TabQuests = "Quests", TabCharacter = "Character", TabAugs = "Augs", TabSociety = "Society",
        TabMarkers = "Markers", TabTitles = "Titles", TabVoid = "Void", TabFellowship = "Fellowship",
        TabBoards = "Leaderboards", TabConquest = "Conquest", TabAbout = "About";

    /// <summary>A tab: shown where any of its server tags matches the server; no tags = everywhere (retail).</summary>
    private sealed record TabDef(string Name, string[] Servers, Action<UiWindow> Draw, bool LiveTimers);

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
            new(TabQuests, any, DrawQuests, true),
            new(TabCharacter, any, DrawCharacter, true),
            new(TabAugs, any, DrawAugs, true),
            new(TabSociety, any, DrawSociety, true),
            new(TabMarkers, any, DrawMarkers, false),
            new(TabTitles, any, DrawTitles, false),
            new(TabVoid, any, DrawVoid, true),
            new(TabFellowship, any, DrawFellowship, false),
            // Server-specific: each parser and its commands only exist on its own server.
            new(TabBoards, new[] { Data.ServerTags.Conquest, Data.ServerTags.Aelrynth }, DrawBoards, false),
            new(TabConquest, new[] { Data.ServerTags.Conquest }, DrawConquest, true),
            new(TabAbout, any, DrawAbout, false),
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

        w.BeginChild("page." + tab, 0f, 0f, border: false);
        (current?.Draw ?? DrawAbout)(w);
        w.EndChild();
    }

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

    /// <summary>Prints a quest's name, status, notes and wiki link in chat (upstream "thinks" them).</summary>
    private void PrintEntry(string name, string flag, string status, string details, string url)
    {
        _c.Print(flag.Length > 0 ? $"{name} ({flag}){(status.Length > 0 ? " - " + status : "")}" : name);
        if (details.Length > 0) _c.Print(details);
        if (url.Length > 0) _c.Print(url);
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
