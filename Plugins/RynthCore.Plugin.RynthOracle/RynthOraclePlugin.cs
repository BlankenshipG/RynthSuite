using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;
using RynthCore.Plugin.RynthOracle.Views;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;

namespace RynthCore.Plugin.RynthOracle;

/// <summary>
/// RynthOracle: quest flags against a master quest list, character status and progression,
/// augmentations and luminance, society standing, titles, Void timers and fellowship info, in
/// the engine's script windows (ImGui). A RynthCore port of the Decal plugin Oracle of Dereth
/// by Advis Eveldan (advis61), MIT; see THIRD_PARTY_NOTICES.md.
///
/// Everything runs on the plugin pump (OnTick and the events it drains) except OnChatBarEnter,
/// which the engine may call on AC's thread: it only classifies the line and queues work.
/// </summary>
public sealed class RynthOraclePlugin : RynthPluginBase
{
    // The landblock the character was last outdoors in (a building's rooms belong to it; 0: not seen yet).
    private uint _lastOutdoorLandblock;

    // Previous release: 0.7.2.
    public const string Version = "0.7.3";

    private const long QuestReadDelayMs = 6000;   // after login, before the hidden /myquests
    private const long CharacterReadMs = 1000;
    // The Character tab's session (RynthAi): read only while that tab is on screen.
    private const long CharacterExtrasReadMs = 2000;
    private const long BookWorldWaitMs = 15_000;  // how long the quest book waits for the world name
    // Re-reading /myquests during a session (2026-11-13, R-20261110-155059-e947): at most one of
    // RynthOracle's own re-reads a minute (the throttling servers' limit, and a long list is a big
    // chat burst), and a timer alert holds this long for the fresh reading before printing anyway.
    private const long AutoReadGapMs = 60_000;
    private const long AlertConfirmWaitMs = 120_000;
    // A timer alert from flags read longer ago than this is checked against a fresh read first.
    private static readonly TimeSpan AlertConfirmAge = TimeSpan.FromMinutes(2);
    // How far the server's "again in" wait may sit from a held flag's timer and still be that quest.
    private static readonly TimeSpan ServerWaitTolerance = TimeSpan.FromMinutes(2);

    private readonly OracleContext _c = new();
    private readonly UiHost _ui = new();
    private UiWindow? _main, _void, _timers, _offerWin;
    private OracleView? _mainView;
    private VoidView? _voidView;
    private QuestTimersView? _timersView;
    private SkillsEmbed? _skills;
    private int _seenBookRevision = -1;

    private readonly ConcurrentQueue<string> _commands = new();
    private long _questReadAtMs = -1;
    private long _loginMs;
    private long _nextCharacterReadMs;
    private long _nextCharacterExtrasMs;
    private bool _voidAutoDecided;
    // Auto refresh (Settings QuestRefreshMinutes): the later of the login and RynthOracle's own last
    // automatic /myquests; the flags' last reply counts too (TickData).
    private DateTime _autoQuestSinceUtc = DateTime.MinValue;
    private int _seenFlagRevision = -1, _seenCatalogRevision = -1, _seenVoidRevision = -1, _seenIltRevision = -1;
    private int _seenGuideRevision = -1, _seenRunnerRevision = -1;
    // Infinite Leaftide (ServerIdentity.IsIlt): RynthILT's Temple guardian JSON last read, and when next.
    private long _nextGuardianMs;
    private string _guardianJson = "";
    // The Dynamic stamp sources last logged (LogStampSources), and the revisions they were read at.
    private (int InMyQuests, int InQb, int Matched) _loggedStamps = (-1, -1, -1);
    private int _stampFlagRevision = -1, _stampIltRevision = -1;
    // RynthCore's server list (ServerStateClient.Revision) last applied to the server identity.
    private int _seenServerStateRevision = -1;
    private string? _catalogKey;          // what the catalog was loaded for (QuestPackSource.Key; "" = retail only)
    private int _seenPackRevision = -1;   // the active server plugin's quest pack (ServerQuestPackClient.Revision)
    private int _titleCount = -2;
    private uint _titleCurrent;
    private long _nextAutoReadMs;
    private int _seenServerGuideRevision = -1;
    // A kill line came while the Kill Tasks tab was open: read /myquests again on the next allowed minute.
    private bool _killReadWanted;
    private DateTime _seenReadUtc = DateTime.MinValue;
    // The server's "again in" wait not yet checked against a read made after it (the log line).
    private (TimeSpan Left, DateTime AtUtc)? _uncheckedServerWait;
    // Timer alerts held for a fresh read (flags read too long ago to trust a "ready").
    private readonly System.Collections.Generic.List<Quest> _heldAlerts = new();
    private long _heldAlertsSinceMs;
    private DateTime _heldAlertsReadUtc;

    public override int Initialize()
    {
        _c.Host = Host;
        _c.Reply = new CommandReply("[RynthOracle]", (t, c) => Host.WriteToChat(t, c), t => Host.Log(t));
        _c.Settings.Load();
        _c.RequestQuests = RequestQuests;
        _c.RereadCharacter = RereadCharacter;
        _c.RereadTitles = RereadTitles;
        _c.RescanT11 = () => { _nextT11Ms = 0; ReadT11(Environment.TickCount64); };
        _c.ReloadQuestList = ReloadQuestList;
        _c.Send = cmd => _c.InWorld && Host.InvokeChatParser(cmd);
        _c.RunCommand = cmd => _c.InWorld && Host.InvokeChatParser(cmd);
        _c.GuardianCommand = GuardianCommand;
        _c.SendNav = line => Host.HasSendPluginCommand && Host.SendPluginCommand("RynthNav", "rnav", line);
        _c.SendAi = (action, value) => Host.HasSendPluginCommand && Host.SendPluginCommand("RynthAi", action, value);
        // Run this quest (the wiki quest guide): RynthNav walks the steps, RynthAi picks up the quest's items.
        _c.Runner = new QuestRunner(line => _c.SendNav(line), line => _c.SendAi("questloot", line), t => _c.Print(t));
        _ui.Log = msg => Host.Log(msg);

        _mainView = new OracleView(_c);
        // The Skills / Attributes / Progress tabs: RynthCore draws its Skills panel body in the window.
        _skills = new SkillsEmbed(cmd => Host.InvokeChatParser(cmd));
        _voidView = new VoidView(_c);
        _timersView = new QuestTimersView(_c);
        _main = _ui.Add(new UiWindow("RynthOracle/Oracle", "Oracle")
        {
            DefaultWidth = 700f, DefaultHeight = 600f, MinWidth = 420f, MinHeight = 260f,
        });
        // The Void window is opened from the Oracle window: no bar button (and no Scripts menu
        // line, which would bring back the Scripts button just for it).
        _void = _ui.Add(new UiWindow("RynthOracle/Void", "Void")
        {
            DefaultWidth = 220f, DefaultHeight = 150f, MinWidth = 160f, MinHeight = 110f,
            ShowInBar = false,
        });
        _mainView.SetVoidWindow = v => { if (_void != null) { _void.Visible = v; _void.NeedsPass = true; } };
        _mainView.VoidWindowVisible = () => _void?.Visible ?? false;
        // The Quest Timers window (tracked quests): opened from the Quests tab, /ro timers or the
        // engine's Character menu; like the Void window it has no bar button of its own.
        _timers = _ui.Add(new UiWindow("RynthOracle/QuestTimers", "Quest Timers")
        {
            DefaultWidth = 310f, DefaultHeight = 220f, MinWidth = 220f, MinHeight = 90f,
            ShowInBar = false,
        });
        _c.SetQuestTimersWindow = SetQuestTimers;
        // The quest offer (2026-11-08): an NPC who starts a guide quest talks to you; asks whether to run it.
        _offerWin = _ui.Add(new UiWindow("RynthOracle/QuestOffer", "Quest offer")
        {
            DefaultWidth = 360f, DefaultHeight = 200f, MinWidth = 240f, MinHeight = 100f,
            ShowInBar = false,
        });
        _mainView.SetOfferWindow = v => { if (_offerWin != null) { _offerWin.Visible = v; _offerWin.NeedsPass = true; } };
        _mainView.ShowMainWindow = () => { if (_main != null) { _main.Visible = true; _main.NeedsPass = true; } };
        _c.QuestTimersWindowVisible = () => _timers?.Visible ?? false;

        Host.Log($"[RynthOracle] {Version} initialized. /ro opens the window. Based on Oracle of Dereth by Advis Eveldan - thank you!");
        return 0;
    }

    /// <summary>Shows or hides the Quest Timers window and remembers the choice for the next login.</summary>
    private void SetQuestTimers(bool visible)
    {
        if (_timers == null) return;
        _timers.Visible = visible;
        _timers.NeedsPass = true;
        if (_c.Settings.QuestTimersOpen != visible)
        {
            _c.Settings.QuestTimersOpen = visible;
            _c.Settings.Save();
        }
        if (_main != null) _main.NeedsPass = true;
    }

    // The engine calls this on a real login and on a freshly (re)loaded plugin already in game.
    public override void OnLoginComplete()
    {
        _c.InWorld = true;
        _c.Flags.Clear();
        _c.Void.Clear();
        _c.Gear.Clear();
        _c.Server.Reset();
        _c.ServerState.Clear();
        _c.ServerState.Poll(Host, Environment.TickCount64, force: true);
        _seenServerStateRevision = _c.ServerState.Revision;
        _c.VoidDotScale = VoidScaleFrom(_c.ServerState.ProfileJson);
        _c.Server.Refresh(Host, _c.ServerState.State);
        _c.QuestPacks.Clear();
        _c.QuestPacks.Poll(Host, Shared.ServerQuestPackClient.OwnerOf(_c.ServerState.State), Environment.TickCount64, force: true);
        _seenPackRevision = _c.QuestPacks.Revision;
        _catalogKey = null;
        ApplyServer();
        _titleCount = -2;
        _c.TitlesKnown = false;
        _c.EarnedTitles.Clear();

        long now = Environment.TickCount64;
        _loginMs = now;
        _questReadAtMs = _c.Settings.ReadQuestsAtLogin ? now + QuestReadDelayMs : -1;
        _nextCharacterReadMs = now;
        _nextCharacterExtrasMs = now;
        ClearCharacterExtras();
        _voidAutoDecided = false;
        _autoQuestSinceUtc = DateTime.UtcNow;
        if (_main != null) { _main.Visible = _c.Settings.MainWindowOpen; _main.NeedsPass = true; }
        if (_void != null) { _void.Visible = false; _void.NeedsPass = true; }
        // The quest book loads once the character's name (and world) are known (TickData).
        _c.Book.Clear();
        _c.Timers.Reset();
        ResetRereads();
        if (_timers != null) { _timers.Visible = _c.Settings.QuestTimersOpen; _timers.NeedsPass = true; }
    }

    private void ResetRereads()
    {
        _nextAutoReadMs = 0;
        _seenReadUtc = DateTime.MinValue;
        _uncheckedServerWait = null;
        _heldAlerts.Clear();
    }

    public override void OnLogout()
    {
        _c.InWorld = false;
        _questReadAtMs = -1;
        // Closed for character select (the saved "left open" choice is kept for the next login).
        if (_main != null) { _main.Visible = false; _main.NeedsPass = true; }
        if (_void != null) { _void.Visible = false; _void.NeedsPass = true; }
        if (_timers != null) { _timers.Visible = false; _timers.NeedsPass = true; }
        if (_offerWin != null) { _offerWin.Visible = false; _offerWin.NeedsPass = true; }
        _skills?.Update(string.Empty, false, false, Environment.TickCount64);
        _ui.SubmitEmpty(Host);
        _c.Book.Clear();   // saved as it changed
        _c.Timers.Reset();
        ResetRereads();
        _c.Flags.Clear();
        _c.Void.Clear();
        _c.Server.Reset();
        _c.VoidDotScale = 1.0;
        _c.ServerState.Clear();
        _c.QuestPacks.Clear();
        _c.TitlesKnown = false;
        _c.EarnedTitles.Clear();
        _c.Gear.Clear();
        _c.T11.Clear();
        ClearCharacterExtras();
        _c.Runner?.Stop("ended at logout");
        _c.SessionStamps.Clear();
        _c.Kills.Clear();
        ClearIlt();
    }

    /// <summary>Forgets RynthILT's profile and guardians (logout, or the server stopped being Infinite Leaftide).</summary>
    private void ClearIlt()
    {
        _c.Ilt.Clear();
        _c.Guide.Clear();
        _c.ServerGuide.Clear();
        _guardianJson = "";
        _nextGuardianMs = 0;
        if (!ReferenceEquals(_c.Guardian, Shared.IltGuardianView.None))
        {
            _c.Guardian = Shared.IltGuardianView.None;
            _c.GuardianRevision++;
        }
        _c.GuardianMessage = "";
        _loggedStamps = (-1, -1, -1);
        _stampFlagRevision = _stampIltRevision = -1;
    }

    /// <summary>
    /// Infinite Leaftide: logs where the Dynamic stamps come from whenever the flags or the /qb list
    /// change and the counts move (Dynamic flags in /myquests, in the /qb list, and the listed
    /// stamps matched), so a stamp shown as not done can be told apart from one never read. Names
    /// the flags no listed stamp matched (up to 10), so a sheet row that doesn't fit is visible.
    /// Nothing on any other server.
    /// </summary>
    private void LogStampSources()
    {
        if (!_c.Server.IsIlt) return;
        if (_c.Flags.Revision == _stampFlagRevision && _c.Ilt.Revision == _stampIltRevision) return;
        _stampFlagRevision = _c.Flags.Revision;
        _stampIltRevision = _c.Ilt.Revision;
        if (_c.Flags.Flags.Count == 0) return;
        IReadOnlySet<string> qbStamps = _c.Ilt.QbStampSet;
        var counts = DynamicStampList.Counts(_c.Flags, qbStamps);
        if (counts == _loggedStamps) return;
        _loggedStamps = counts;
        List<string> unlisted = DynamicStampList.Unlisted(_c.Flags, qbStamps);
        Host.Log($"[RynthOracle] Dynamic stamps: {counts.InMyQuests} Dynamic flag(s) in /myquests, {counts.InQb} in the /qb list"
                 + $" ({_c.Ilt.QbCompletedSet.Count} completed flags read); {counts.Matched} of {DynamicStampList.All.Count} listed stamps done"
                 + (unlisted.Count == 0 ? "." : $"; not in the list: {string.Join(" ", unlisted.Take(10))}{(unlisted.Count > 10 ? " ..." : "")}."));
    }

    /// <summary>The Quests tab's guardian controls: a verb for RynthILT's guardians, then a quick re-read.</summary>
    private bool GuardianCommand(string verb)
    {
        long now = Environment.TickCount64;
        bool ok = _c.InWorld && _c.Server.IsIlt && _c.IltData.GuardianCommand(Host, now, verb);
        _c.GuardianMessage = ok ? "" : "RynthILT didn't take that (not loaded, or older than 0.2.20).";
        _nextGuardianMs = now + 400;
        if (_main != null) _main.NeedsPass = true;
        return ok;
    }

    /// <summary>
    /// While the Quests tab is on screen on Infinite Leaftide: RynthILT's Temple guardian JSON every
    /// <see cref="CharacterExtrasReadMs"/> (the guardian rows, switches and last answer). Nothing is
    /// read with the window closed, on another tab, or on another server.
    /// </summary>
    private void ReadGuardian(long now)
    {
        if (_main == null || _mainView == null || !_main.Visible || _mainView.Tab != OracleView.TabQuests) return;
        if (!_c.Server.IsIlt) return;
        if (now < _nextGuardianMs) return;
        _nextGuardianMs = now + CharacterExtrasReadMs;
        string json = _c.IltData.ReadGuardian(Host, now);
        if (string.Equals(json, _guardianJson, StringComparison.Ordinal)) return;
        _guardianJson = json;
        _c.Guardian = Shared.IltGuardianView.Parse(json);
        _c.GuardianRevision++;
        _main.NeedsPass = true;
    }

    /// <summary>Forgets the Character tab's session (login, logout): the next character's comes fresh.</summary>
    private long _nextT11Ms;
    /// <summary>The T11 Tracking tab reads the packs and the bounty slots this often while it shows.</summary>
    private const long T11ReadMs = 2000;

    /// <summary>
    /// The T11 Tracking tab's data (Infinite Leaftide only): the packs and the bounty slots every <see cref="T11ReadMs"/>
    /// while that tab shows, and one queued appraisal at a time for the items it needs read. Nothing with the window
    /// closed, on another tab, or on another server.
    /// </summary>
    private void ReadT11(long now)
    {
        if (_main == null || _mainView == null || !_main.Visible || _mainView.Tab != OracleView.TabT11) return;
        if (!_c.Server.IsIlt || !_c.InWorld) return;
        _c.T11.Tick(Host, now);
        if (now < _nextT11Ms) return;
        _nextT11Ms = now + T11ReadMs;
        if (_c.T11.Scan(Host, now)) _main.NeedsPass = true;
    }

    private void ClearCharacterExtras()
    {
        _c.Session = default;
        _c.SessionKnown = false;
        _c.AiSession.Reset();
    }

    /// <summary>
    /// While the Character tab is on screen: RynthAi's session, every <see cref="CharacterExtrasReadMs"/>.
    /// Nothing is read with the window closed or on another tab.
    /// </summary>
    private void ReadCharacterExtras(long now)
    {
        if (_main == null || _mainView == null || !_main.Visible || _mainView.Tab != OracleView.TabCharacter) return;
        if (now < _nextCharacterExtrasMs) return;
        _nextCharacterExtrasMs = now + CharacterExtrasReadMs;

        bool known = _c.AiSession.TryGet(Host, now, out Shared.SessionStatsV1 s);
        if (known != _c.SessionKnown || known && (s.Xp != _c.Session.Xp || s.Lum != _c.Session.Lum || s.Kills != _c.Session.Kills))
            _main.NeedsPass = true;
        _c.SessionKnown = known;
        _c.Session = known ? s : default;
    }

    /// <summary>Opens the Quests tab on the trackers: one kind (/ro legendary, /ro markers) or all of them (null).</summary>
    private void ShowTrackers(QuestKind? kind)
    {
        _mainView?.ShowTrackers(kind);
        if (_main != null) { _main.Visible = true; _main.NeedsPass = true; }
        _nextGuardianMs = 0;   // read at once
    }

    /// <summary>Shows the main window on a tab (another plugin's link, or a /ro tab command).</summary>
    private void ShowTab(string tab)
    {
        if (_mainView != null) _mainView.Tab = tab;
        if (_main != null) { _main.Visible = true; _main.NeedsPass = true; }
        if (tab == OracleView.TabCharacter) _nextCharacterExtrasMs = 0;   // read at once
    }

    /// <summary>
    /// Loads what depends on the server: the quest catalog with the active server's pack (the one its
    /// plugin offers through "RynthServer.QuestPack"; none on a server without one). Runs at login and
    /// again whenever the server or its pack changes (either can arrive a moment after login).
    /// </summary>
    private void ApplyServer()
    {
        Shared.ServerQuestPack? pack = Shared.ServerQuestPackClient.ForActive(_c.ServerState.State, _c.QuestPacks.Pack);
        QuestPackSource? source = QuestCatalog.SourceFor(_c.Server, pack);
        // The pack client's revision goes up whenever the offered pack changes (same size or not).
        string key = source == null ? "" : source.Key + "|" + _c.QuestPacks.Revision;
        if (_catalogKey == key) return;
        _catalogKey = key;
        try { _c.Catalog.Load(source, msg => Host.Log(msg)); }
        catch (Exception ex) { Host.Log($"[RynthOracle] Couldn't load the quest list: {ex.Message}"); }
        _c.Catalog.SyncDiscoveries(_c.Flags);
        Host.Log($"[RynthOracle] Server: {_c.Server.DisplayName} ({_c.Server.Source}); {_c.Catalog.RetailCount} retail quests{PackText()}.");
        if (_main != null) _main.NeedsPass = true;
    }

    /// <summary>" + 546 Conquest (from RynthConquest)", or "" without a pack.</summary>
    private string PackText() => _c.Catalog.PackServer.Length > 0
        ? $" + {_c.Catalog.ServerCount} {_c.Catalog.PackServer} ({_c.Catalog.PackSource})"
        : "";

    public override void Shutdown()
    {
        _skills?.Shutdown();
        _ui.SubmitEmpty(Host);
        _c.Updater.Shutdown();
        _c.Fandom.Shutdown();
        _c.Settings.Save();
    }

    // ── Chat ────────────────────────────────────────────────────────────────

    public override void OnChatWindowText(string? text, int chatType, ref int eat)
    {
        if (string.IsNullOrEmpty(text)) return;
        long now = Environment.TickCount64;
        string line = text.TrimEnd('\r', '\n');

        MyQuestsLine kind = _c.Flags.OnChatLine(line, now, DateTime.UtcNow);
        if (kind == MyQuestsLine.TooRecent)
        {
            // Shown as the server sent it; the store now wants a fresh read (TickData sends it).
            if (_c.Flags.LastServerWait is { } wait && wait.AtUtc != _uncheckedServerWait?.AtUtc)
            {
                _uncheckedServerWait = wait;
                Host.Log("[RynthOracle] The server turned a quest down: " + DescribeServerWait(wait, "the flags held"));
            }
            return;
        }
        if (kind != MyQuestsLine.None)
        {
            // Only the reply to RynthOracle's own request, and only when the setting says so.
            if (_c.Flags.AwaitingOurs && _c.Settings.HideOwnQuestReplies && kind is MyQuestsLine.Quest or MyQuestsLine.Empty)
                eat = 1;
            return;
        }
        if (!_c.InWorld) return;

        // The quest guide: a stamp counts as done at once (before /myquests is read again), and Run this quest
        // follows the stamps and RynthNav's arrival.
        if (GuideStamp.FromChat(line) is string stamp && _c.SessionStamps.Add(GuideStamp.Key(stamp)))
        {
            _c.SessionStampRevision++;
            if (_main != null) _main.NeedsPass = true;
        }
        // A kill task's kill line: the Kill Tasks tab shows the count at once; with the tab open, /myquests is
        // read again (at most once a minute) so the counter flags follow (2026-11-21).
        if (_c.Kills.OnChatLine(line, _c.UtcNow))
        {
            if (KillTasksOnScreen()) _killReadWanted = true;
            if (_main != null) _main.NeedsPass = true;
        }
        _c.Runner?.OnChat(line);
        _mainView?.OnDynChat(line);   // Dynamic Stamps' Go and hand in (2026-11-08)

        // An NPC who starts quests in the guide talks to you: ask whether to run one (not during a run).
        if (_c.Runner?.Active != true && _mainView != null && QuestOffers.TryNpcLine(line, out string npc, out string said)
            && _mainView.OfferFor(npc, said, now) && _offerWin != null)
        {
            _offerWin.Visible = true;
            _offerWin.NeedsPass = true;
        }

        // A server's own command replies (Conquest's /b, Aelrynth's /top, ...) are its server plugin's.
        // A server's own Void DoT duration rule comes from its plugin's profile (RynthConquest's for
        // Conquest); 1 everywhere else (VoidScaleFrom).
        _c.Void.OnChatLine(line.Trim(), now, ResolveTarget, () => _c.Character.HasEnchantment(SpellIds.SurgeOfDestruction), _c.VoidDotScale);
    }

    /// <summary>The server profile value RynthOracle's Void timers apply ("RynthServer.Profile" values).</summary>
    internal const string VoidDotScaleKey = "voidDotDurationScale";

    /// <summary>
    /// The active server's Void DoT duration multiplier, from its plugin's profile: a number from 1 to
    /// 10 (anything else, or none, is 1). Re-read whenever RynthCore's server state or the profile changes.
    /// </summary>
    internal static double VoidScaleFrom(string? profileJson)
    {
        double v = RynthCore.Plugin.Shared.ServerProfileView.Value(profileJson, VoidDotScaleKey, 1.0);
        return v >= 1.0 && v <= 10.0 ? v : 1.0;
    }

    /// <summary>The selected target's id when its name matches (RynthCore has no spell-cast event for plugins).</summary>
    private uint ResolveTarget(string name)
    {
        uint selected = Host.GetSelectedItemId();
        return selected != 0 && Host.TryGetObjectName(selected, out string n) && n == name ? selected : 0;
    }

    // May run on AC's thread: classify and queue only.
    public override void OnChatBarEnter(string? text, ref int eat)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        string t = text.Trim();
        if (t.Equals("/myquests", StringComparison.OrdinalIgnoreCase))
        {
            _c.Flags.NoteTypedCommand(); // not eaten: the server answers it
            return;
        }
        if (!t.StartsWith("/ro", StringComparison.OrdinalIgnoreCase) || (t.Length > 3 && !char.IsWhiteSpace(t[3])))
            return;
        eat = 1;
        _commands.Enqueue(t.Length > 3 ? t.Substring(3).Trim().ToLowerInvariant() : "");
    }

    private void RunCommand(string arg)
    {
        // Every /ro answers in chat (2026-10-28, OracleCommand.cs; Plugins/Shared/CommandReply.cs). RynthOracle
        // can't tell a typed /ro from a meta's or another plugin's menu (they all come through chat), so
        // each one does.
        OracleCommand c = OracleCommand.Parse(arg);
        // A window shown: says so, or (an engine without plugin windows) why nothing shows.
        void Shown(string text)
        {
            if (Host.HasUi) _c.Print(text);
            else _c.PrintFail(OracleCommand.NoWindows);
        }
        switch (c.Verb)
        {
            case OracleVerb.Window:
                if (_main != null) { _main.Visible = !_main.Visible; _main.NeedsPass = true; Shown(OracleCommand.Shown("Oracle window", _main.Visible)); }
                break;
            case OracleVerb.Void:
                if (_void != null) { _void.Visible = !_void.Visible; _void.NeedsPass = true; Shown(OracleCommand.Shown("Void window", _void.Visible)); }
                break;
            case OracleVerb.Timers:
                if (_timers != null) { SetQuestTimers(!_timers.Visible); Shown(OracleCommand.Shown("Quest Timers window", _timers.Visible)); }
                break;
            case OracleVerb.Quests:
            {
                bool sent = RequestQuests();
                var (ok, text) = OracleCommand.Quests(_c.InWorld, sent, _c.Flags.ThrottleSecondsLeft(Environment.TickCount64));
                if (ok) _c.Print(text); else _c.PrintFail(text);
                break;
            }
            case OracleVerb.Tab:
                // RynthAi, the server plugins and RynthCore's menus link to these; they show, never toggle off.
                ShowTab(c.Tab);
                Shown(OracleCommand.OnTab(c.Tab));
                break;
            case OracleVerb.Trackers:
                // A server's own trackers and tabs are its server plugin's pages (since 2026-10-21).
                if (c.Trackers is QuestKind.Dynamic or QuestKind.Guardian && !_c.Server.IsIlt)
                {
                    // Infinite Leaftide's trackers (the Dynamic Stamps, RynthILT's Temple guardians).
                    _c.PrintFail((c.Trackers == QuestKind.Guardian ? "The Temple guardians are" : "Dynamic stamps are")
                                 + " an Infinite Leaftide quest line; the active server isn't Infinite Leaftide.");
                    ShowTrackers(null);
                    break;
                }
                ShowTrackers(c.Trackers);
                Shown(OracleCommand.OnTrackers(c.Trackers));
                break;
            case OracleVerb.Status:
                string titles = _c.TitlesKnown ? $", {_c.EarnedTitles.Count} titles held" : "";
                _c.Print($"Server: {_c.Server.DisplayName} ({_c.Server.Source}). "
                         + $"{_c.Flags.Flags.Count} quest flags read, {_c.Catalog.RetailCount} retail quests{PackText()}, {_c.Void.Dots.Count} Void spells tracked{titles}.");
                break;
            case OracleVerb.About:
                // The About tab (settings, the server RynthCore says this is, the quest list).
                _c.Print("Based on Oracle of Dereth by Advis Eveldan - thank you! github.com/advis61/OracleOfDereth (MIT)");
                ShowTab(c.Tab);
                if (!Host.HasUi) _c.PrintFail(OracleCommand.NoWindows);
                break;
            case OracleVerb.Help:
                _c.Print(OracleCommand.Usage);
                break;
            case OracleVerb.LootProfiles:
                BuildLootProfiles(c.Word);
                break;
            default:
                _c.PrintFail(OracleCommand.Unknown(c.Word));
                break;
        }
    }

    /// <summary>
    /// "/ro lootprofiles [quest]": RynthAi writes each wiki quest's loot profile (LootProfiles\Quests\&lt;quest&gt;.utl,
    /// base-quest-dynamic.utl and the quest's items; RynthAiPlugin.QuestLootProfiles.cs). A quest without loot has none.
    /// </summary>
    private void BuildLootProfiles(string quest)
    {
        QuestGuide g = _c.Wiki;
        if (g.Quests.Count == 0) { _c.PrintFail("No wiki quests yet: the AC Community Wiki is still being read (Quests > Guide shows it)" + (_c.Server.IsIlt ? ", or RynthILT's (/ilt wiki)." : ".")); return; }
        var list = new System.Collections.Generic.List<GuideQuest>();
        if (quest.Length > 0)
        {
            GuideQuest? q = g.Find(quest);
            if (q == null) { _c.PrintFail($"No wiki quest called '{quest}' (the name as the guide shows it)."); return; }
            if (q.Loot.Count == 0) { _c.PrintFail($"{q.Title} has no loot on its wiki page, so no loot profile."); return; }
            list.Add(q);
        }
        else
            foreach (GuideQuest q in g.Quests) if (q.Loot.Count > 0) list.Add(q);
        int sent = 0;
        foreach (GuideQuest q in list)
        {
            if (_c.Runner == null || !_c.Runner.BuildProfile(q)) break;
            sent++;
        }
        if (sent == 0) { _c.PrintFail("RynthAi isn't loaded: it writes the loot profiles."); return; }
        _c.Print(list.Count == 1
            ? $"RynthAi wrote the loot profile for {list[0].Title}: LootProfiles\\Quests\\ (base-quest-dynamic and its {list[0].Loot.Count} item(s))."
            : $"RynthAi wrote {sent} quest loot profiles to LootProfiles\\Quests\\ (base-quest-dynamic and each quest's items; {g.Quests.Count - list.Count} quests have no loot). Run this quest uses them.");
    }

    /// <summary>Sends /myquests; false when nothing went out (not in the world, or one was sent moments ago).</summary>
    private bool RequestQuests()
    {
        if (!_c.InWorld) return false;
        long now = Environment.TickCount64;
        bool sent = _c.Flags.Request(cmd => Host.InvokeChatParser(cmd), now);
        if (!sent)
        {
            int wait = _c.Flags.ThrottleSecondsLeft(now);
            Host.Log(wait > 0
                ? $"[RynthOracle] The server answers one /myquests a minute; try again in {wait}s (the flags shown are kept)."
                : "[RynthOracle] /myquests was sent moments ago; waiting for that reply.");
        }
        if (_main != null) _main.NeedsPass = true;
        return sent;
    }

    // ── Keeping the flags current (2026-11-13, R-20261110-155059-e947) ──────
    // The report: every daily quest showed "ready" hours early, and the server turned one down with
    // "again in 2h 50m 42s". /myquests was read once at login (34 hours before, in that log), so a
    // quest done since kept the stamp from before it and its old timer had long run out. Nothing in
    // the log said when the flags were read, so reads, re-reads and the server's turn-downs log now.

    /// <summary>
    /// RynthOracle may send /myquests on its own during a session: only with the login read on and
    /// its replies hidden (a re-read must never print the whole list into chat).
    /// </summary>
    private bool AutoRereads() => _c.Settings.ReadQuestsAtLogin && _c.Settings.HideOwnQuestReplies;

    /// <summary>Something on screen shows quest timers: a quest page of the Oracle window, or the Quest Timers window.</summary>
    private bool TimersOnScreen()
    {
        if (_timers?.Visible == true) return true;
        if (_main?.Visible != true || _mainView == null) return false;
        string tab = _mainView.Tab;
        return tab == OracleView.TabQuests || tab == OracleView.TabAugs || tab == OracleView.TabSociety;
    }

    /// <summary>The Oracle window shows the Quests tab's Kill Tasks list.</summary>
    private bool KillTasksOnScreen() => _main?.Visible == true && _mainView != null && _mainView.KillTasksShowing;

    /// <summary>Re-reads /myquests when the flags are known stale, or old and on screen; at most once a minute.</summary>
    private void RereadIfStale(long now)
    {
        if (now < _nextAutoReadMs || !AutoRereads()) return;
        bool wanted = _c.Flags.RereadWanted;
        bool kills = _killReadWanted && KillTasksOnScreen() && _c.Flags.LastReadUtc != DateTime.MinValue && !_c.Flags.Loading && !_c.Flags.ServerDisabled;
        if (!kills && !_c.Flags.NeedsReread(_c.UtcNow, TimersOnScreen())) return;
        _nextAutoReadMs = now + AutoReadGapMs;
        _killReadWanted = false;
        string why = kills && !wanted ? "a kill task's kill came in with the Kill Tasks tab open" : wanted
            ? "the server turned a quest down, or a timer alert wants a fresh reading"
            : $"the quest timers are on screen and the flags were read {TimeText.Friendly(_c.UtcNow - _c.Flags.LastReadUtc)} ago";
        if (RequestQuests()) Host.Log($"[RynthOracle] Re-reading /myquests: {why}.");
    }

    /// <summary>Logs each /myquests reply that landed (whoever asked) and checks the server's last "again in" against it.</summary>
    private void NoteRead()
    {
        DateTime read = _c.Flags.LastReadUtc;
        if (read == _seenReadUtc || read == DateTime.MinValue) return;
        DateTime previous = _seenReadUtc;
        _seenReadUtc = read;
        Host.Log($"[RynthOracle] Read {_c.Flags.Flags.Count} quest flags"
                 + (previous == DateTime.MinValue ? " (the first read since login)." : $" (the read before was {TimeText.Friendly(read - previous)} earlier)."));
        if (_uncheckedServerWait is { } w && read > w.AtUtc)
        {
            _uncheckedServerWait = null;
            Host.Log("[RynthOracle] After the server turned a quest down: " + DescribeServerWait(w, "the flags just read"));
        }
    }

    /// <summary>
    /// "again in 2h 50m 42s; the flags held (read 07:12:05 UTC, 2h 33m before it) have aaaflag at 2h 50m",
    /// or that none has about that long to go: which quest it was, and whether Oracle's timer agrees.
    /// </summary>
    private string DescribeServerWait((TimeSpan Left, DateTime AtUtc) w, string which)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("again in ").Append(TimeText.Friendly(w.Left)).Append("; ").Append(which);
        if (_c.Flags.LastReadUtc == DateTime.MinValue) return sb.Append(" (none read yet)").ToString();
        TimeSpan gap = w.AtUtc - _c.Flags.LastReadUtc;
        sb.Append($" (read {_c.Flags.LastReadUtc:HH:mm:ss} UTC, ")
          .Append(gap >= TimeSpan.Zero ? TimeText.Friendly(gap) + " before it)" : TimeText.Friendly(-gap) + " after it)");
        var near = _c.Flags.WaitingAbout(w.Left, w.AtUtc, ServerWaitTolerance);
        if (near.Count == 0)
            return sb.Append(" have no quest with about that long to go: out of date, or the server's timer isn't the one /myquests gives.").ToString();
        sb.Append(" have ");
        for (int i = 0; i < near.Count && i < 3; i++)
        {
            if (i > 0) sb.Append(", ");
            TimeSpan held = near[i].Flag.NextAvailableTime(w.AtUtc);
            sb.Append(near[i].Flag.Key).Append(" at ").Append(TimeText.Friendly(held));
        }
        return sb.Append('.').ToString();
    }

    // ── The Oracle window's Refresh buttons (the data side; Views\RefreshBar.cs has the rest) ──

    /// <summary>Reads the character's stats, buffs and (on the Character tab) RynthAi's session now, not at the next timer.</summary>
    private void RereadCharacter()
    {
        if (!_c.InWorld) return;
        long now = Environment.TickCount64;
        _c.Character.Read(Host);
        _c.Void.RemoveExpired(now);
        _nextCharacterReadMs = now + CharacterReadMs;
        _nextCharacterExtrasMs = 0;
        ReadCharacterExtras(now);
        if (_main != null) _main.NeedsPass = true;
        if (_void != null) _void.NeedsPass = true;
    }

    /// <summary>Asks the engine for the character's titles again (it keeps them from the login events).</summary>
    private void RereadTitles()
    {
        _titleCount = -2;
        ReadTitles();
        if (_main != null) _main.NeedsPass = true;
    }

    /// <summary>
    /// Reloads the quest list and the server's pack (a downloaded list, a pack dropped into
    /// %APPDATA%\RynthCore\RynthOracle\packs, the server plugin's pack asked for again) and asks
    /// RynthCore which server this is.
    /// </summary>
    private void ReloadQuestList()
    {
        long now = Environment.TickCount64;
        _c.ServerState.Poll(Host, now, force: true);
        _seenServerStateRevision = _c.ServerState.Revision;
        _c.VoidDotScale = VoidScaleFrom(_c.ServerState.ProfileJson);
        _c.Server.Refresh(Host, _c.ServerState.State);
        _c.QuestPacks.Poll(Host, Shared.ServerQuestPackClient.OwnerOf(_c.ServerState.State), now, force: true);
        _seenPackRevision = _c.QuestPacks.Revision;
        _catalogKey = null;   // the files may have changed: load again even for the same pack
        ApplyServer();
        if (_main != null) _main.NeedsPass = true;
    }

    // ── Tick ────────────────────────────────────────────────────────────────

    public override void OnTick()
    {
        long now = Environment.TickCount64;
        _c.NowMs = now;
        _c.UtcNow = DateTime.UtcNow;

        while (_commands.TryDequeue(out string? cmd)) RunCommand(cmd);
        _c.Reply?.Flush();

        _ui.Poll(Host);
        TickData(now);
        // Run this quest inside a dungeon (2026-11-07): the run hears when the character goes in or out.
        if (Host.HasGetPlayerPose && Host.TryGetPlayerPose(out uint cell, out _, out _, out _, out _, out _, out _, out _) && cell != 0)
        {
            // The Town Network (landblock 0x0007) is a portal hub RynthNav routes through, never a quest's dungeon.
            // A building's rooms are indoor cells of the landblock you walked in from; a dungeon is reached by a
            // portal, so it is another landblock (2026-11-07: the run took a building by the Tusker Burrow for it).
            uint lb = cell >> 16;
            bool indoor = (cell & 0xFFFF) >= 0x0100;
            if (!indoor) _lastOutdoorLandblock = lb;
            if (_c.Runner is { Active: true } run)
                run.OnWhere(indoor && lb != QuestRunner.TownNetworkLandblock && lb != _lastOutdoorLandblock, lb);
        }

        if (!Host.HasUi) return;
        // After the poll, so a tab clicked this tick is already selected.
        if (_skills != null && _main != null && _mainView != null)
            _skills.Update(_mainView.Tab, _main.Visible, _c.InWorld, now);
        RecordWindows(now);
        _ui.Submit(Host, now);

        // Remember whether the main window was left open (the player's X or the bar menu).
        if (_c.InWorld && _main != null && _main.Visible != _c.Settings.MainWindowOpen)
        {
            _c.Settings.MainWindowOpen = _main.Visible;
            _c.Settings.Save();
        }
        // The same for the Quest Timers window (closed with its X or opened from the engine's menu).
        if (_c.InWorld && _timers != null && _timers.Visible != _c.Settings.QuestTimersOpen)
        {
            _c.Settings.QuestTimersOpen = _timers.Visible;
            _c.Settings.Save();
            if (_main != null) _main.NeedsPass = true;
        }
    }

    private void TickData(long now)
    {
        _c.Flags.Tick(now);
        _mainView?.TickDyn();   // Dynamic Stamps: the hand-in after the arrival settles
        if (_c.Flags.LastPartialReply is { } partial)
        {
            _c.Flags.LastPartialReply = null;
            Host.Log($"[RynthOracle] /myquests reply looked cut short ({partial.Got} lines, {partial.Had} flags held): merged it instead of replacing the list.");
        }
        LogStampSources();
        NoteRead();
        if (_c.Flags.Revision != _seenFlagRevision)
        {
            _seenFlagRevision = _c.Flags.Revision;
            _c.Runner?.OnFlagsChanged();
            _c.Catalog.SyncDiscoveries(_c.Flags);
            if (_main != null) _main.NeedsPass = true;
            if (_timers != null) _timers.NeedsPass = true;
        }
        if (_c.Catalog.Revision != _seenCatalogRevision)
        {
            _seenCatalogRevision = _c.Catalog.Revision;
            if (_main != null) _main.NeedsPass = true;
            if (_timers != null) _timers.NeedsPass = true;
        }
        if (_c.Book.Revision != _seenBookRevision)
        {
            _seenBookRevision = _c.Book.Revision;
            if (_main != null) _main.NeedsPass = true;
            if (_timers != null) _timers.NeedsPass = true;
        }

        // The AC Community Wiki's guide (2026-11-21): a finished read is taken here.
        if (_c.Fandom.TakeResult())
        {
            Host.Log("[RynthOracle] AC Community Wiki: " + _c.Fandom.Status);
            if (_main != null) _main.NeedsPass = true;
        }

        string? result = _c.Updater.TakeResult(out bool updated);
        if (result != null)
        {
            _c.UpdaterMessage = result;
            Host.Log("[RynthOracle] " + result);
            if (updated)
            {
                _c.Catalog.Reload(msg => Host.Log(msg));
                _c.Catalog.SyncDiscoveries(_c.Flags);
            }
            if (_main != null) _main.NeedsPass = true;
        }

        if (!_c.InWorld) return;

        ReadCharacterExtras(now);
        // Infinite Leaftide only: RynthILT's profile (every 2 s; the /qb list for the stamps) and the guardians.
        if (_c.Server.IsIlt)
        {
            _c.Ilt.Poll(Host, now);
            if (_c.Ilt.Revision != _seenIltRevision)
            {
                _seenIltRevision = _c.Ilt.Revision;
                if (_main != null) _main.NeedsPass = true;
            }
            ReadGuardian(now);
            ReadT11(now);
            _c.Guide.Poll(Host, now);
            if (_c.Guide.Revision != _seenGuideRevision)
            {
                _seenGuideRevision = _c.Guide.Revision;
                if (_main != null) _main.NeedsPass = true;
            }
        }
        else
        {
            // Any other server: its plugin's own wiki quest guide, when it has one (RynthConquest's, 2026-11-21).
            _c.ServerGuide.Poll(Host, Shared.ServerQuestPackClient.OwnerOf(_c.ServerState.State), now);
            if (_c.ServerGuide.Client.Revision != _seenServerGuideRevision)
            {
                _seenServerGuideRevision = _c.ServerGuide.Client.Revision;
                if (_main != null) _main.NeedsPass = true;
            }
        }
        if (_c.Runner != null && _c.Runner.Revision != _seenRunnerRevision)
        {
            _seenRunnerRevision = _c.Runner.Revision;
            if (_main != null) _main.NeedsPass = true;
        }
        // The Gear tab's Check runs on (appraisals spaced out) whichever tab is showing.
        if (_c.Gear.Tick(Host, now, _c.Character.Name) && _main != null) _main.NeedsPass = true;

        // RynthCore's server list (every 2 s): a new pick or override applies now, not at the next character read.
        _c.ServerState.Poll(Host, now);
        bool serverChanged = _c.ServerState.Revision != _seenServerStateRevision;
        if (serverChanged)
        {
            _seenServerStateRevision = _c.ServerState.Revision;
            _c.VoidDotScale = VoidScaleFrom(_c.ServerState.ProfileJson);
            bool wasIlt = _c.Server.IsIlt;
            _c.Server.Refresh(Host, _c.ServerState.State);
            if (wasIlt && !_c.Server.IsIlt) ClearIlt();
        }
        // The active server plugin's quest pack (every 2 s; the CSV is read only when it changed).
        _c.QuestPacks.Poll(Host, Shared.ServerQuestPackClient.OwnerOf(_c.ServerState.State), now, force: serverChanged);
        if (serverChanged || _c.QuestPacks.Revision != _seenPackRevision)
        {
            _seenPackRevision = _c.QuestPacks.Revision;
            ApplyServer();
            if (_main != null) _main.NeedsPass = true;
        }

        if (_questReadAtMs >= 0 && now >= _questReadAtMs)
        {
            _questReadAtMs = -1;
            // Another plugin (RynthAi's meta manager) or the player may already have asked.
            if (_c.Flags.LastReadUtc == DateTime.MinValue && !_c.Flags.Loading) RequestQuests();
        }
        RereadIfStale(now);

        // Keep the flags fresh on a long session: /myquests again once nothing has read it for the
        // chosen minutes (the login read first; any reply, RynthAi's meta polls too, resets it).
        if (_questReadAtMs < 0)
        {
            DateTime since = _c.Flags.LastReadUtc > _autoQuestSinceUtc ? _c.Flags.LastReadUtc : _autoQuestSinceUtc;
            if (QuestFlagStore.AutoRefreshDue(_c.Settings.QuestRefreshMinutes, since, _c.UtcNow, _c.Flags.Loading,
                    _c.Flags.ServerDisabled, _c.Flags.ThrottleSecondsLeft(now)))
            {
                _autoQuestSinceUtc = _c.UtcNow;   // a lost reply tries again after the same wait, not every tick
                if (_c.Flags.Request(cmd => Host.InvokeChatParser(cmd), now))
                    Host.Log($"[RynthOracle] Quest flags are {(int)(_c.UtcNow - since).TotalMinutes} min old: reading /myquests again.");
            }
        }

        // The AC Community Wiki's quests, kill tasks, society quests and contracts: read at login when a day old.
        _c.Fandom.Tick(_c.UtcNow);

        // Once a day, however long the session (it used to run once per login only).
        if (_c.Settings.CheckQuestListDaily && !_c.Updater.Running
            && _c.UtcNow - _c.Settings.LastQuestListCheckUtc >= QuestCatalogUpdater.Interval)
        {
            _c.Settings.LastQuestListCheckUtc = _c.UtcNow;
            _c.Settings.Save();
            _c.Updater.Start();
        }

        if (now >= _nextCharacterReadMs)
        {
            _nextCharacterReadMs = now + CharacterReadMs;
            _c.Character.Read(Host);
            _c.Void.RemoveExpired(now);
            _c.Server.Refresh(Host, _c.ServerState.State);
            ApplyServer();
            LoadQuestBook(now);
            AnnounceTimers();
            ReadTitles();
            if (!_voidAutoDecided && _c.Character.Valid)
            {
                _voidAutoDecided = true;
                if (_void != null && _c.Settings.AutoShowVoidWindow && _c.Character.VoidMagic.Known)
                {
                    _void.Visible = true;
                    _void.NeedsPass = true;
                }
            }
        }

        if (_c.Void.Revision != _seenVoidRevision)
        {
            _seenVoidRevision = _c.Void.Revision;
            if (_void != null) _void.NeedsPass = true;
            if (_main != null && _mainView != null && _mainView.Tab == OracleView.TabVoid) _main.NeedsPass = true;
        }
    }

    /// <summary>Says in chat which watched quest timers came up: tracked quests, or every held quest if so set.</summary>
    private void AnnounceTimers()
    {
        if (!_c.Settings.AlertTimers || !_c.Book.Loaded) { _c.Timers.Reset(); _heldAlerts.Clear(); return; }
        long now = _c.NowMs;
        var ready = _c.Timers.Check(_c.Flags, _c.Catalog, _c.Settings.AlertAllTimers ? null : _c.Book.Tracked, _c.UtcNow);
        // A timer that ran out on flags read a while ago may be a quest done again since (R-20261110-155059-e947):
        // hold the alert for a fresh read rather than call it ready (2026-11-13).
        bool confirm = ready.Count > 0 && AutoRereads() && !_c.Flags.ServerDisabled
                       && _c.UtcNow - _c.Flags.LastReadUtc > AlertConfirmAge;
        foreach (Quest q in ready)
        {
            if (!confirm) { _c.Print($"Quest ready again: {q.Label}"); continue; }
            if (_heldAlerts.Count == 0) { _heldAlertsSinceMs = now; _heldAlertsReadUtc = _c.Flags.LastReadUtc; }
            if (!_heldAlerts.Exists(h => string.Equals(h.Flag, q.Flag, StringComparison.OrdinalIgnoreCase))) _heldAlerts.Add(q);
        }
        if (confirm) _c.Flags.WantReread();
        ReleaseHeldAlerts(now);
    }

    /// <summary>
    /// Held timer alerts once a fresh read is in: still ready, they print; on a timer again (the quest
    /// was done since the old read) they're dropped and the watcher alerts when that timer ends. With
    /// no read in <see cref="AlertConfirmWaitMs"/> (throttled, no reply) they print as before.
    /// </summary>
    private void ReleaseHeldAlerts(long now)
    {
        if (_heldAlerts.Count == 0) return;
        bool fresh = _c.Flags.LastReadUtc > _heldAlertsReadUtc;
        if (!fresh && now - _heldAlertsSinceMs < AlertConfirmWaitMs) return;
        foreach (Quest q in _heldAlerts)
        {
            if (!fresh || QuestTimer.State(q, _c.Flags, _c.UtcNow, out _) == QuestTimerState.Ready)
                _c.Print($"Quest ready again: {q.Label}");
            else
                Host.Log($"[RynthOracle] No \"ready again\" for {q.Flag}: the fresh read has it on a timer (done again since the last read).");
        }
        _heldAlerts.Clear();
    }

    /// <summary>
    /// Loads this character's quest book (tracked quests, rewards) once the name is known and the
    /// world has been announced, or after <see cref="BookWorldWaitMs"/> without one (then filed
    /// under "default"), so the file isn't picked before the world name arrives.
    /// </summary>
    private void LoadQuestBook(long now)
    {
        if (_c.Book.Loaded || !_c.Character.Valid || _c.Character.Name.Length == 0) return;
        if (_c.Server.WorldName.Length == 0 && now - _loginMs < BookWorldWaitMs) return;
        _c.Book.Load(_c.Server.WorldName, _c.Character.Name);
        if (_c.Book.LoadNote.Length > 0) _c.Print(_c.Book.LoadNote);
    }

    /// <summary>The titles the character holds (engine API v75), re-read each second (cheap; changes are rare).</summary>
    private void ReadTitles()
    {
        if (!Host.TryGetCharacterTitles(out uint[] ids, out uint current))
        {
            if (_c.TitlesKnown) { _c.TitlesKnown = false; _c.EarnedTitles.Clear(); }
            return;
        }
        if (ids.Length == _titleCount && current == _titleCurrent && _c.TitlesKnown) return;
        _titleCount = ids.Length;
        _titleCurrent = current;
        _c.EarnedTitles.Clear();
        foreach (uint id in ids) _c.EarnedTitles.Add((int)id);
        _c.CurrentTitle = current;
        _c.TitlesKnown = true;
        if (_main != null) _main.NeedsPass = true;
    }

    private void RecordWindows(long now)
    {
        if (_main != null && _mainView != null)
        {
            long interval = _mainView.TabHasLiveTimers ? 1000 : 3000;
            Record(_main, now, interval, _mainView.Draw);
        }
        if (_void != null && _voidView != null)
            Record(_void, now, 500, _voidView.Draw);
        if (_timers != null && _timersView != null)
            Record(_timers, now, 1000, _timersView.Draw);
        if (_offerWin != null && _mainView != null)
            Record(_offerWin, now, 2000, _mainView.DrawOffer);
    }

    /// <summary>
    /// A pass when one is wanted, or on the refresh interval while visible. A hidden window is
    /// still recorded when asked (so the bar menu shows it with content); it costs nothing more.
    /// </summary>
    private void Record(UiWindow w, long now, long interval, Action<UiWindow> draw)
    {
        bool due = w.NeedsPass || (w.Visible && now >= w.NextPassMs);
        if (!due) return;
        w.NeedsPass = false;
        w.NextPassMs = now + interval;
        w.Begin();
        try
        {
            draw(w);
        }
        catch (Exception ex)
        {
            w.TextWrapped($"Something went wrong drawing this page: {ex.GetType().Name}: {ex.Message}");
            Host.Log($"[RynthOracle] {w.Key} pass failed: {ex}");
        }
        w.End();
    }
}
