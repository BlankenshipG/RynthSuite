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
    public const string Version = "0.2.0";

    private const long QuestReadDelayMs = 6000;   // after login, before the hidden /myquests
    private const long CharacterReadMs = 1000;

    private readonly OracleContext _c = new();
    private readonly UiHost _ui = new();
    private UiWindow? _main, _void;
    private OracleView? _mainView;
    private VoidView? _voidView;

    private readonly ConcurrentQueue<string> _commands = new();
    private long _questReadAtMs = -1;
    private long _loginMs;
    private long _nextCharacterReadMs;
    private bool _voidAutoDecided;
    private bool _dailyCheckStarted;
    private int _seenFlagRevision = -1, _seenCatalogRevision = -1, _seenVoidRevision = -1;
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
        _ui.Log = msg => Host.Log(msg);

        _mainView = new OracleView(_c);
        _voidView = new VoidView(_c);
        _main = _ui.Add(new UiWindow("RynthOracle/Oracle", "Oracle")
        {
            DefaultWidth = 620f, DefaultHeight = 560f, MinWidth = 420f, MinHeight = 260f,
        });
        _void = _ui.Add(new UiWindow("RynthOracle/Void", "Void")
        {
            DefaultWidth = 220f, DefaultHeight = 150f, MinWidth = 160f, MinHeight = 110f,
        });
        _mainView.SetVoidWindow = v => { if (_void != null) { _void.Visible = v; _void.NeedsPass = true; } };
        _mainView.VoidWindowVisible = () => _void?.Visible ?? false;

        Host.Log($"[RynthOracle] {Version} initialized. /ro opens the window. Based on Oracle of Dereth by Advis Eveldan - thank you!");
        return 0;
    }

    // The engine calls this on a real login and on a freshly (re)loaded plugin already in game.
    public override void OnLoginComplete()
    {
        _c.InWorld = true;
        _c.Flags.Clear();
        _c.Void.Clear();
        _c.Server.Reset();
        _c.Server.Refresh(Host, Host.GetPlayerId());
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
        _voidAutoDecided = false;
        _dailyCheckStarted = false;
        if (_main != null) { _main.Visible = _c.Settings.MainWindowOpen; _main.NeedsPass = true; }
        if (_void != null) { _void.Visible = false; _void.NeedsPass = true; }
    }

    public override void OnLogout()
    {
        _c.InWorld = false;
        _questReadAtMs = -1;
        // Closed for character select (the saved "left open" choice is kept for the next login).
        if (_main != null) { _main.Visible = false; _main.NeedsPass = true; }
        if (_void != null) { _void.Visible = false; _void.NeedsPass = true; }
        _ui.SubmitEmpty(Host);
        _c.Flags.Clear();
        _c.Void.Clear();
        _c.Conquest.Clear();
        _c.Boards.Clear();
        _c.Server.Reset();
        _c.TitlesKnown = false;
        _c.EarnedTitles.Clear();
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
            case "quests":
            case "refresh":
                RequestQuests();
                break;
            case "status":
                string pack = _c.Catalog.PackServer.Length > 0 ? $" + {_c.Catalog.ServerCount} {_c.Catalog.PackServer}" : "";
                string titles = _c.TitlesKnown ? $", {_c.EarnedTitles.Count} titles held" : "";
                _c.Print($"Server: {(_c.Server.Tag.Length > 0 ? _c.Server.Tag : "no server-specific features")} ({_c.Server.Source}). "
                         + $"{_c.Flags.Flags.Count} quest flags read, {_c.Catalog.RetailCount} retail quests{pack}, {_c.Void.Dots.Count} Void spells tracked{titles}.");
                break;
            case "about":
                _c.Print("Based on Oracle of Dereth by Advis Eveldan - thank you! github.com/advis61/OracleOfDereth (MIT)");
                break;
            default:
                _c.Print("Commands: /ro (window), /ro void (Void window), /ro quests (read quest flags), /ro status, /ro about");
                break;
        }
        if (arg is "" or "void" && !Host.HasUi && !_noUiLogged)
        {
            _noUiLogged = true;
            _c.Print("This RynthCore engine can't show plugin windows (it needs script windows, API v71, with ImGui on). /ro status still works.");
        }
    }

    private void RequestQuests()
    {
        if (!_c.InWorld) return;
        if (!_c.Flags.Request(cmd => Host.InvokeChatParser(cmd), Environment.TickCount64))
            Host.Log("[RynthOracle] /myquests was sent moments ago; waiting for that reply.");
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
    }

    private void TickData(long now)
    {
        _c.Flags.Tick(now);
        if (_c.Flags.Revision != _seenFlagRevision)
        {
            _seenFlagRevision = _c.Flags.Revision;
            _c.Catalog.SyncDiscoveries(_c.Flags);
            if (_main != null) _main.NeedsPass = true;
        }
        if (_c.Catalog.Revision != _seenCatalogRevision)
        {
            _seenCatalogRevision = _c.Catalog.Revision;
            if (_main != null) _main.NeedsPass = true;
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
            _c.Server.Refresh(Host, _c.Character.PlayerId);
            ApplyServer();
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
