using System;
using System.Collections.Concurrent;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;
using RynthCore.Plugin.RynthOracle.Views;
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
    // Previous release: 0.2.5.
    // Previous release: 0.3.4.
    public const string Version = "0.5.1";

    private const long QuestReadDelayMs = 6000;   // after login, before the hidden /myquests
    private const long CharacterReadMs = 1000;
    // The Character tab's charms (RynthILT) and session (RynthAi): read only while that tab is on screen.
    private const long CharacterExtrasReadMs = 2000;
    private const long BookWorldWaitMs = 15_000;  // how long the quest book waits for the world name

    private readonly OracleContext _c = new();
    private readonly UiHost _ui = new();
    private UiWindow? _main, _void, _timers;
    private OracleView? _mainView;
    private VoidView? _voidView;
    private QuestTimersView? _timersView;
    private int _seenBookRevision = -1;

    private readonly ConcurrentQueue<string> _commands = new();
    private long _questReadAtMs = -1;
    private long _loginMs;
    private long _nextCharacterReadMs;
    private long _nextCharacterExtrasMs;
    private string _charmsJson = "";
    private long _nextBreedingMs;
    private string _breedingJson = "";
    private bool _voidAutoDecided;
    private bool _dailyCheckStarted;
    private int _seenFlagRevision = -1, _seenCatalogRevision = -1, _seenVoidRevision = -1, _seenIltRevision = -1;
    // RynthCore's server list (ServerStateClient.Revision) last applied to the server identity.
    private int _seenServerStateRevision = -1;
    private bool _noUiLogged;
    private string? _catalogTag;          // the server tag the catalog and server data were loaded for
    private bool _conquestLoginRead;
    private long _lastDepositBlock = -1;
    private int _titleCount = -2;
    private uint _titleCurrent;

    public override int Initialize()
    {
        _c.Host = Host;
        _c.Settings.Load();
        _c.RequestQuests = RequestQuests;
        _c.Send = cmd => _c.InWorld && Host.InvokeChatParser(cmd);
        _c.RunCommand = cmd => _c.InWorld && Host.InvokeChatParser(cmd);
        // SkillsFace embed: engine /rc oracle-skills bridge (works logged out too for tab prep).
        _c.SkillsEmbedChanged = tab =>
            Host.InvokeChatParser(tab < 0 ? "/rc oracle-skills off" : $"/rc oracle-skills {tab}");
        _c.ToggleCharm = ToggleCharm;
        _c.BreedingCommand = BreedingCommand;
        _ui.Log = msg => Host.Log(msg);

        _mainView = new OracleView(_c);
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
        _c.Server.Reset();
        _c.Ilt.Clear();
        _c.Ilt.Poll(Host, Environment.TickCount64);
        _c.ServerState.Clear();
        _c.ServerState.Poll(Host, Environment.TickCount64, force: true);
        _seenServerStateRevision = _c.ServerState.Revision;
        _c.Server.Refresh(Host, Host.GetPlayerId(), _c.Ilt.Profile.Ilt, _c.ServerState.State);
        _catalogTag = null;
        ApplyServer();
        _conquestLoginRead = false;
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
        _dailyCheckStarted = false;
        if (_main != null) { _main.Visible = _c.Settings.MainWindowOpen; _main.NeedsPass = true; }
        if (_void != null) { _void.Visible = false; _void.NeedsPass = true; }
        // The quest book loads once the character's name (and world) are known (TickData).
        _c.Book.Clear();
        if (_timers != null) { _timers.Visible = _c.Settings.QuestTimersOpen; _timers.NeedsPass = true; }
    }

    public override void OnLogout()
    {
        _c.InWorld = false;
        _questReadAtMs = -1;
        // Closed for character select (the saved "left open" choice is kept for the next login).
        if (_main != null) { _main.Visible = false; _main.NeedsPass = true; }
        if (_void != null) { _void.Visible = false; _void.NeedsPass = true; }
        if (_timers != null) { _timers.Visible = false; _timers.NeedsPass = true; }
        _ui.SubmitEmpty(Host);
        _c.Book.Clear();   // saved as it changed
        _c.Flags.Clear();
        _c.Void.Clear();
        _c.Conquest.Clear();
        _c.Boards.Clear();
        _c.Server.Reset();
        _c.Ilt.Clear();
        _c.ServerState.Clear();
        _c.TitlesKnown = false;
        _c.EarnedTitles.Clear();
        ClearCharacterExtras();
    }

    /// <summary>Forgets the Character tab's charms and session (login, logout): the next character's come fresh.</summary>
    private void ClearCharacterExtras()
    {
        _c.Charms = Shared.IltCharmsView.None;
        _charmsJson = "";
        _c.CharmMessage = "";
        _c.Breeding = Shared.IltBreedingView.None;
        _breedingJson = "";
        _c.BreedingMessage = "";
        _c.Session = default;
        _c.SessionKnown = false;
        _c.AiSession.Reset();
    }

    /// <summary>The Character tab's Toggle: RynthILT checks the id is one of the player's charms and uses it.</summary>
    private bool ToggleCharm(uint id)
    {
        bool ok = _c.InWorld && _c.IltData.ToggleCharm(Host, Environment.TickCount64, id);
        _c.CharmMessage = ok ? "Toggled; the status updates in a moment." : "RynthILT didn't take that (not carried any more, or RynthILT isn't loaded).";
        _nextCharacterExtrasMs = Environment.TickCount64 + 1000;   // re-read soon so the new state shows
        if (_main != null) _main.NeedsPass = true;
        return ok;
    }

    /// <summary>
    /// While the Character tab is on screen: RynthILT's charms JSON and RynthAi's session, every
    /// <see cref="CharacterExtrasReadMs"/>. Nothing is read with the window closed or on another tab.
    /// </summary>
    private void ReadCharacterExtras(long now)
    {
        if (_main == null || _mainView == null || !_main.Visible || _mainView.Tab != OracleView.TabCharacter) return;
        if (now < _nextCharacterExtrasMs) return;
        _nextCharacterExtrasMs = now + CharacterExtrasReadMs;

        string json = _c.IltData.ReadCharms(Host, now);
        if (!string.Equals(json, _charmsJson, StringComparison.Ordinal))
        {
            _charmsJson = json;
            _c.Charms = Shared.IltCharmsView.Parse(json);
            _main.NeedsPass = true;
        }
        bool known = _c.AiSession.TryGet(Host, now, out Shared.SessionStatsV1 s);
        if (known != _c.SessionKnown || known && (s.Xp != _c.Session.Xp || s.Lum != _c.Session.Lum || s.Kills != _c.Session.Kills))
            _main.NeedsPass = true;
        _c.SessionKnown = known;
        _c.Session = known ? s : default;
    }

    /// <summary>The Pet Breeding tab's buttons: a verb for RynthILT's tracker, then a quick re-read.</summary>
    private bool BreedingCommand(string verb)
    {
        long now = Environment.TickCount64;
        bool ok = _c.InWorld && _c.IltData.PetBreedingCommand(Host, now, verb);
        _c.BreedingMessage = ok ? "Sent to RynthILT; the tab updates in a moment." : "RynthILT didn't take that (not loaded, or older than 0.2.7).";
        _nextBreedingMs = now + 800;
        if (_main != null) _main.NeedsPass = true;
        return ok;
    }

    /// <summary>
    /// While the Pet Breeding tab is on screen: RynthILT's breeding JSON every <see cref="CharacterExtrasReadMs"/>
    /// (reading it keeps RynthILT's tracker scanning). Nothing is read with the window closed or on another tab.
    /// </summary>
    private void ReadBreeding(long now)
    {
        if (_main == null || _mainView == null || !_main.Visible || _mainView.Tab != OracleView.TabBreeding) return;
        if (now < _nextBreedingMs) return;
        _nextBreedingMs = now + CharacterExtrasReadMs;
        string json = _c.IltData.ReadPetBreeding(Host, now);
        if (string.Equals(json, _breedingJson, StringComparison.Ordinal)) return;
        _breedingJson = json;
        _c.Breeding = Shared.IltBreedingView.Parse(json);
        _main.NeedsPass = true;
    }

    /// <summary>Shows the main window on a tab (another plugin's link, or a /ro tab command).</summary>
    private void ShowTab(string tab)
    {
        if (_mainView != null) _mainView.Tab = tab;
        if (_main != null) { _main.Visible = true; _main.NeedsPass = true; }
        if (tab == OracleView.TabCharacter) _nextCharacterExtrasMs = 0;   // read at once
        if (tab == OracleView.TabBreeding) _nextBreedingMs = 0;
    }

    /// <summary>
    /// Loads what depends on the server: the quest catalog with the server's pack, and fresh
    /// server-specific data. Runs at login and again whenever the server tag changes (it can be
    /// known only a moment after login).
    /// </summary>
    private void ApplyServer()
    {
        string tag = _c.Server.Tag;
        if (_catalogTag == tag) return;
        _catalogTag = tag;
        try { _c.Catalog.Load(tag, msg => Host.Log(msg)); }
        catch (Exception ex) { Host.Log($"[RynthOracle] Couldn't load the quest list: {ex.Message}"); }
        _c.Catalog.SyncDiscoveries(_c.Flags);
        _c.Conquest.Clear();
        _c.Boards.Clear();
        string pack = _c.Catalog.PackServer.Length > 0 ? $" + {_c.Catalog.ServerCount} {_c.Catalog.PackServer}" : "";
        Host.Log($"[RynthOracle] Server: {(tag.Length > 0 ? tag : "no server-specific features")} ({_c.Server.Source}); {_c.Catalog.RetailCount} retail quests{pack}.");
        if (_main != null) _main.NeedsPass = true;
    }

    public override void Shutdown()
    {
        _ui.SubmitEmpty(Host);
        _c.Updater.Shutdown();
        _c.Settings.Save();
    }

    // ── Chat ────────────────────────────────────────────────────────────────

    public override void OnChatWindowText(string? text, int chatType, ref int eat)
    {
        if (string.IsNullOrEmpty(text)) return;
        long now = Environment.TickCount64;
        string line = text.TrimEnd('\r', '\n');

        MyQuestsLine kind = _c.Flags.OnChatLine(line, now);
        if (kind != MyQuestsLine.None)
        {
            // Only the reply to RynthOracle's own request, and only when the setting says so.
            if (_c.Flags.AwaitingOurs && _c.Settings.HideOwnQuestReplies && kind is MyQuestsLine.Quest or MyQuestsLine.Empty)
                eat = 1;
            return;
        }
        if (!_c.InWorld) return;

        // Server-specific replies: each parser only runs on its own server.
        bool ours;
        if (_c.Server.Is(ServerTags.Conquest) && _c.Conquest.OnChatLine(line, now, out ours))
        {
            if (ours && _c.Settings.HideOwnQuestReplies) eat = 1;
            if (_main != null) _main.NeedsPass = true;
            return;
        }
        if (_c.Server.Is(ServerTags.Aelrynth) && _c.Boards.OnChatLine(line, now, out ours))
        {
            if (ours && _c.Settings.HideOwnQuestReplies) eat = 1;
            if (_main != null) _main.NeedsPass = true;
            return;
        }

        double scale = _c.Server.Is(ServerTags.Conquest)
            ? _c.Conquest.VoidDurationMultiplier(_c.Character.Int(Host, IntArchmagesEndurance))
            : 1.0;
        _c.Void.OnChatLine(line.Trim(), now, ResolveTarget, () => _c.Character.HasEnchantment(SpellIds.SurgeOfDestruction), scale);
    }

    /// <summary>PropertyInt AugmentationIncreasedSpellDuration (Archmage's Endurance), sent at login.</summary>
    private const uint IntArchmagesEndurance = 238;

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
        switch (arg)
        {
            case "":
                if (_main != null) { _main.Visible = !_main.Visible; _main.NeedsPass = true; }
                break;
            case "void":
                if (_void != null) { _void.Visible = !_void.Visible; _void.NeedsPass = true; }
                break;
            case "timers":
            case "timer":
                if (_timers != null) SetQuestTimers(!_timers.Visible);
                break;
            case "quests":
            case "refresh":
                RequestQuests();
                break;
            case "fellowship":
            case "fellow":
            case "fship":
                // The main window on its Fellowship tab (RynthCore's Char menu and RynthAi's Mini Remote use this).
                ShowTab(OracleView.TabFellowship);
                break;
            case "character":
            case "char":
            case "charms":
            case "charm":
                // The Character tab: where the Mini Remote's Character and Charms tabs moved (2026-10-13).
                // RynthAi, RynthILT and RynthCore's menus link here; it shows, never toggles off.
                ShowTab(OracleView.TabCharacter);
                break;
            case "skills":
            case "skill":
                // Engine SkillsFace body (raise/train) embedded under Oracle.
                ShowTab(OracleView.TabSkills);
                break;
            case "attrs":
            case "attributes":
            case "vitals":
                ShowTab(OracleView.TabAttributes);
                break;
            case "progress":
            case "progression":
                // ILT progression planners (was Skills → Progression).
                ShowTab(OracleView.TabProgress);
                break;
            case "legendary":
            case "legend":
            case "john":
                // The Legendary tab: John's 30 Legendary Key quests, have / missing and repeat timers.
                ShowTab(OracleView.TabLegendary);
                break;
            case "dynamic":
            case "dyn":
            case "dyno":
            case "stamps":
                // The Dynamic Stamps tab (Infinite Leaftide): NPC hand-in stamps, have / missing.
                if (!_c.Server.Is(ServerTags.InfiniteLeaftide))
                    _c.Print("Dynamic stamps are an Infinite Leaftide quest line; this server isn't recognised as Infinite Leaftide.");
                ShowTab(OracleView.TabDynamic);
                break;
            case "breeding":
            case "breed":
            case "pets":
                // The Pet Breeding tab (Infinite Leaftide): RynthAi's Character menu and its Pets > Breeding tab link here.
                if (!_c.Server.Is(ServerTags.InfiniteLeaftide))
                    _c.Print("Pet breeding is an Infinite Leaftide feature; this server isn't recognised as Infinite Leaftide.");
                ShowTab(OracleView.TabBreeding);
                break;
            case "status":
                string pack = _c.Catalog.PackServer.Length > 0 ? $" + {_c.Catalog.ServerCount} {_c.Catalog.PackServer}" : "";
                string titles = _c.TitlesKnown ? $", {_c.EarnedTitles.Count} titles held" : "";
                _c.Print($"Server: {(_c.Server.Tag.Length > 0 ? _c.Server.Tag : "no server-specific features")} ({_c.Server.Source}). "
                         + $"{_c.Flags.Flags.Count} quest flags read, {_c.Catalog.RetailCount} retail quests{pack}, {_c.Void.Dots.Count} Void spells tracked{titles}.");
                break;
            case "about":
            case "features":
                // The About tab (its "Server features" list is RynthILT's; RynthILT's settings link here).
                _c.Print("Based on Oracle of Dereth by Advis Eveldan - thank you! github.com/advis61/OracleOfDereth (MIT)");
                ShowTab(OracleView.TabAbout);
                break;
            default:
                _c.Print("Commands: /ro (window), /ro character (Character tab: status, session, charms), "
                         + "/ro skills|attrs|progress (raise/train and ILT planners), /ro void (Void window), "
                         + "/ro timers (Quest Timers window), /ro fellowship (Fellowship tab), /ro breeding (Pet Breeding tab, Infinite Leaftide), "
                         + "/ro legendary (Legendary tab: John's 30 key quests, missing and repeat timers), "
                         + "/ro dynamic (Dynamic Stamps tab, Infinite Leaftide: NPC stamps you have and are missing), "
                         + "/ro quests (read quest flags), /ro status, "
                         + "/ro about (About tab: settings, server features)");
                break;
        }
        if (arg is "" or "void" or "timers" or "timer" or "fellowship" or "fellow" or "fship" or "character" or "char" or "charms" or "charm"
                or "skills" or "skill" or "attrs" or "attributes" or "vitals" or "progress" or "progression"
                or "breeding" or "breed" or "pets" or "legendary" or "legend" or "john"
                or "dynamic" or "dyn" or "dyno" or "stamps" or "about" or "features" && !Host.HasUi && !_noUiLogged)
        {
            _noUiLogged = true;
            _c.Print("This RynthCore engine can't show plugin windows (it needs script windows, API v71, with ImGui on). /ro status still works.");
        }
    }

    private void RequestQuests()
    {
        if (!_c.InWorld) return;
        long now = Environment.TickCount64;
        if (!_c.Flags.Request(cmd => Host.InvokeChatParser(cmd), now))
        {
            int wait = _c.Flags.ThrottleSecondsLeft(now);
            Host.Log(wait > 0
                ? $"[RynthOracle] The server answers one /myquests a minute; try again in {wait}s (the flags shown are kept)."
                : "[RynthOracle] /myquests was sent moments ago; waiting for that reply.");
        }
        if (_main != null) _main.NeedsPass = true;
    }

    // ── Tick ────────────────────────────────────────────────────────────────

    public override void OnTick()
    {
        long now = Environment.TickCount64;
        _c.NowMs = now;
        _c.UtcNow = DateTime.UtcNow;

        while (_commands.TryDequeue(out string? cmd)) RunCommand(cmd);

        _ui.Poll(Host);
        TickData(now);

        if (!Host.HasUi) return;
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
        if (_c.Flags.LastPartialReply is { } partial)
        {
            _c.Flags.LastPartialReply = null;
            Host.Log($"[RynthOracle] /myquests reply looked cut short ({partial.Got} lines, {partial.Had} flags held): merged it instead of replacing the list.");
        }
        if (_c.Flags.Revision != _seenFlagRevision)
        {
            _seenFlagRevision = _c.Flags.Revision;
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

        string? result = _c.Updater.TakeResult(out bool updated);
        if (result != null)
        {
            _c.UpdaterMessage = result;
            Host.Log("[RynthOracle] " + result);
            if (updated)
            {
                _c.Catalog.Load(_c.Server.Tag, msg => Host.Log(msg));
                _c.Catalog.SyncDiscoveries(_c.Flags);
            }
            if (_main != null) _main.NeedsPass = true;
        }

        if (!_c.InWorld) return;

        // RynthILT's profile (every 2 s): the Fellowship tab's size and Leaftide fellowship features.
        _c.Ilt.Poll(Host, now);
        if (_c.Ilt.Revision != _seenIltRevision)
        {
            _seenIltRevision = _c.Ilt.Revision;
            if (_main != null) _main.NeedsPass = true;
        }

        ReadCharacterExtras(now);
        ReadBreeding(now);

        // RynthCore's server list (every 2 s): a new pick or override applies now, not at the next character read.
        _c.ServerState.Poll(Host, now);
        if (_c.ServerState.Revision != _seenServerStateRevision)
        {
            _seenServerStateRevision = _c.ServerState.Revision;
            _c.Server.Refresh(Host, _c.Character.PlayerId != 0 ? _c.Character.PlayerId : Host.GetPlayerId(), _c.Ilt.Profile.Ilt, _c.ServerState.State);
            ApplyServer();
            if (_main != null) _main.NeedsPass = true;
        }

        if (_questReadAtMs >= 0 && now >= _questReadAtMs)
        {
            _questReadAtMs = -1;
            // Another plugin (RynthAi's meta manager) or the player may already have asked.
            if (_c.Flags.LastReadUtc == DateTime.MinValue && !_c.Flags.Loading) RequestQuests();
        }

        if (!_dailyCheckStarted && _c.Settings.CheckQuestListDaily
            && _c.UtcNow - _c.Settings.LastQuestListCheckUtc >= QuestCatalogUpdater.Interval)
        {
            _dailyCheckStarted = true;
            _c.Settings.LastQuestListCheckUtc = _c.UtcNow;
            _c.Settings.Save();
            _c.Updater.Start();
        }

        if (now >= _nextCharacterReadMs)
        {
            _nextCharacterReadMs = now + CharacterReadMs;
            _c.Character.Read(Host);
            _c.Void.RemoveExpired(now);
            _c.Server.Refresh(Host, _c.Character.PlayerId, _c.Ilt.Profile.Ilt, _c.ServerState.State);
            ApplyServer();
            LoadQuestBook(now);
            ReadTitles();
            TickConquest(now);
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

    /// <summary>ConquestAC only: read the advanced augs once after login (void DoT timing uses them), and the auto-deposit.</summary>
    private void TickConquest(long now)
    {
        if (!_c.Server.Is(ServerTags.Conquest)) return;
        if (!_conquestLoginRead && now - _loginMs > QuestReadDelayMs + 4000)
        {
            _conquestLoginRead = true;
            Host.InvokeChatParser(_c.Conquest.RequestAugs(now));
        }
        if (_c.Settings.ConquestAutoDeposit)
        {
            DateTime local = DateTime.Now;
            long block = local.Ticks / TimeSpan.TicksPerMinute / 10;
            if (local.Minute % 10 == 0 && block != _lastDepositBlock)
            {
                _lastDepositBlock = block;
                Host.InvokeChatParser(_c.Conquest.AutoDeposit(now));
                _c.Print("Auto-deposited to the bank.");
            }
        }
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
