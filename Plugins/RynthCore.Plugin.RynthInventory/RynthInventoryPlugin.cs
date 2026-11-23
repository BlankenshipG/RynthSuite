using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using RynthCore.Plugin.RynthInventory.Data;
using RynthCore.Plugin.RynthInventory.Scanning;
using RynthCore.Plugin.RynthInventory.Ui;
using RynthCore.Plugin.RynthInventory.Views;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthInventory;

/// <summary>
/// RynthInventory: a searchable inventory across all of a player's characters on every server
/// (like Virindi Global Inventory). Each client scans the character it is logged into (a full
/// scan after login, then whenever the inventory changes, debounced) and writes one file per
/// character per server; storage the character opens (house storage, banks, vaults) is
/// recorded when it is opened, and storage another plugin reads for it (a server plugin's
/// server-side store, through Plugins/Shared/StorageFeed.cs) when that arrives. /ginv opens a
/// window that merges every file.
///
/// Everything runs on the plugin pump (OnTick and the events the engine drains into it)
/// except OnChatBarEnter, which the engine may call on AC's thread: it only queues the
/// command. File reads for the merged view and file writes run on worker tasks; they touch
/// no game state.
/// </summary>
public sealed class RynthInventoryPlugin : RynthPluginBase
{
    // Previous release: 0.1.6. Keep in step with PluginExports.GetVersion.
    public const string Version = "0.1.7";

    private const long IdentityRetryMs = 1000;
    private const long AccountWaitMs = 15_000;
    private const long FileCheckMs = 5000;
    private const long StorageFirstScanMs = 700;
    private const long StorageRescanMs = 3000;
    private const long StorageSettleScans = 3;
    /// <summary>An online character's file is rewritten at least this often, so its "scanned" time stays honest.</summary>
    private const long RefreshWriteMs = 10 * 60_000;
    /// <summary>Within this long after login an empty scan means "not loaded yet", never "owns nothing".</summary>
    private const long EmptyScanGraceMs = 90_000;

    private readonly InventoryContext _c = new();
    private readonly InventoryScanner _scanner = new();
    private readonly ScanScheduler _scheduler = new();
    private readonly UiHost _ui = new();
    private UiWindow? _window;
    private InventoryView? _view;
    private UiWindow? _tradeWindow;
    private TradeView? _tradeView;
    private readonly ConcurrentQueue<string> _commands = new();

    // Storage other plugins read for us ("storage" plugin commands, StorageFeed.cs): queued by
    // ApplyRemoteCommand (any thread), filed on the tick once the character is known. Aelrynth's
    // account vault comes this way from RynthAelrynth (it was read here until 2026-10-20).
    private readonly ConcurrentQueue<string> _storagePushes = new();
    private const int MaxQueuedPushes = 8;
    private string _lastPush = string.Empty;

    // RynthILT's bank JSON (Infinite Leaftide only): every BankTabReadMs on the Banking tab, every
    // BankIdleReadMs with the window on another tab, never with it closed or on another server.
    private const long BankTabReadMs = 2000, BankIdleReadMs = 10_000;
    private long _nextBankReadMs;
    private string _bankJson = string.Empty;
    // RynthCore's server list, read for the Infinite Leaftide gate every ServerStateReadMs.
    private const long ServerStateReadMs = 2000;
    private long _nextServerStateMs;
    private string _serverStateJson = string.Empty;

    private bool _identityKnown;
    private long _loginMs;
    private long _nextIdentityTryMs;
    private bool _oldAccountChecked;
    private bool _forceWrite;

    // Write in flight (one at a time).
    private Task? _writeTask;
    private long _lastWriteMs;

    // Storage being watched.
    private uint _lastViewed;
    private uint _openStorage;
    private string _openStorageName = string.Empty;
    private string _openStorageLabel = string.Empty;
    private uint _openStorageCell;
    private long _nextStorageScanMs;
    private int _storageScans;

    // Merged view: files parsed once and re-read only when they change.
    private Dictionary<string, (InventoryFileStamp Stamp, CharacterSnapshot? Snapshot)> _files = new(StringComparer.OrdinalIgnoreCase);
    private Task<(Dictionary<string, (InventoryFileStamp, CharacterSnapshot?)> Files, int Broken)>? _loadTask;
    private long _nextFileCheckMs;
    private bool _indexDirty = true;
    private bool _reloadWanted = true;

    public override int Initialize()
    {
        _c.Host = Host;
        _c.Reply = new CommandReply("[RynthInventory]", (t, c) => Host.WriteToChat(t, c), t => Host.Log(t));
        _c.Settings.Load();
        _c.RequestRescan = () => { _scheduler.RequestFullScan(); _forceWrite = true; };
        _c.RequestReload = () => { _reloadWanted = true; _files.Clear(); };
        _c.Forget = ForgetCharacter;
        _c.BankCommand = SendBankVerb;
        _ui.Log = msg => Host.Log(msg);

        _view = new InventoryView(_c);
        // No bar button of its own: opened from the engine Inventory window's GInv button, the
        // Scripts menu or /ginv.
        _window = _ui.Add(new UiWindow("RynthInventory/Main", "Global Inventory")
        {
            DefaultWidth = 640f, DefaultHeight = 600f, MinWidth = 420f, MinHeight = 300f,
            BarButton = false,
        });

        _tradeView = new TradeView(_c);
        _tradeWindow = _ui.Add(new UiWindow("RynthInventory/Trade", "Trade List")
        {
            DefaultWidth = 400f, DefaultHeight = 500f, MinWidth = 300f, MinHeight = 200f,
            BarButton = false,
        });
        _c.TradeCommand = RunTradeCommand;

        Host.Log($"[RynthInventory] {Version} initialized. /ginv opens the window. Files: {_c.Store.Root}");
        return 0;
    }

    // The engine calls this on a real login and on a freshly (re)loaded plugin already in game.
    public override void OnLoginComplete()
    {
        _c.InWorld = true;
        _c.Settings.Load();
        _identityKnown = false;
        _oldAccountChecked = false;
        _loginMs = Environment.TickCount64;
        _nextIdentityTryMs = _loginMs;
        _c.Live = null;
        _c.LastSavedUtc = default;
        _c.LastSaveError = string.Empty;
        _scanner.Reset();
        CloseStorage();
        _lastViewed = 0;
        _lastWriteMs = 0;
        _carriedIds.Clear();
        _carriedPacks.Clear();
        RefreshIconSupport();
        ClearBank();
        _nextServerStateMs = 0;
        if (_window != null) { _window.Visible = _c.Settings.MainWindowOpen; _window.NeedsPass = true; }
        // Trade: this character's part of a running trade is picked up by TickTrade.
        if (_tradeWindow != null) _tradeWindow.NeedsPass = true;
    }

    /// <summary>Forgets the last bank read (login, logout, off Infinite Leaftide): the next comes fresh.</summary>
    private void ClearBank()
    {
        _c.Bank = IltBankView.None;
        _bankJson = string.Empty;
        _nextBankReadMs = 0;
    }

    /// <summary>
    /// Whether RynthCore's active server is Infinite Leaftide (the Banking page's gate), from the
    /// engine's server list. Leaving it drops the bank and moves the window off the Banking tab.
    /// </summary>
    private void TickServer(long now)
    {
        if (now < _nextServerStateMs) return;
        _nextServerStateMs = now + ServerStateReadMs;
        string json = _c.InWorld && Host.HasGetServerStateJson && Host.TryGetServerStateJson(out string j) ? j : string.Empty;
        if (string.Equals(json, _serverStateJson, StringComparison.Ordinal)) return;
        _serverStateJson = json;
        bool ilt = EngineServerState.Parse(json)?.IsActive(EngineServerState.IltId) == true;
        if (ilt == _c.Ilt) return;
        _c.Ilt = ilt;
        _scanner.Ilt = ilt;   // T11 attributes are read only on Leaftide (2026-11-07)
        if (!ilt)
        {
            ClearBank();
            _view?.LeaveBanking();
        }
        if (_window != null) _window.NeedsPass = true;
    }

    /// <summary>The Banking tab's actions: a verb line for RynthILT, then a quick re-read for its reply.</summary>
    private bool SendBankVerb(string verb)
    {
        long now = Environment.TickCount64;
        bool ok = _c.InWorld && _c.Ilt && _c.IltData.BankCommand(Host, now, verb);
        _nextBankReadMs = now + 500;
        if (_window != null) _window.NeedsPass = true;
        return ok;
    }

    /// <summary>
    /// On Infinite Leaftide: reads RynthILT's bank JSON while the window is open (more often on the
    /// Banking tab) and redraws when it changed. Reading is what tells RynthILT someone is looking.
    /// </summary>
    private void TickBank(long now)
    {
        if (!_c.InWorld || !_c.Ilt || _window == null || _view == null || !_window.Visible) return;
        if (now < _nextBankReadMs) return;
        _nextBankReadMs = now + (_view.Tab == InventoryView.TabBanking ? BankTabReadMs : BankIdleReadMs);
        string json = _c.IltData.ReadBank(Host, now);
        if (string.Equals(json, _bankJson, StringComparison.Ordinal)) return;
        _bankJson = json;
        _c.Bank = IltBankView.Parse(json);
        _window.NeedsPass = true;
    }

    public override void OnLogout()
    {
        // Last word for this character: write what it has now (also refreshes its "scanned" time).
        FlushNow(final: true);
        _c.InWorld = false;
        _identityKnown = false;
        _scheduler.Logout();
        CloseStorage();
        if (_window != null) { _window.Visible = false; _window.NeedsPass = true; }
        // Trade window stays visible across logout so the player can see what's left.
        if (_tradeWindow != null) _tradeWindow.NeedsPass = true;
        _ui.SubmitEmpty(Host);
        _c.Live = null;
        _indexDirty = true;
        while (_storagePushes.TryDequeue(out _)) { }   // the next character's come fresh
        ClearBank();
        _c.Ilt = false;
        _serverStateJson = string.Empty;
        _view?.LeaveBanking();
        _lastPush = string.Empty;
    }

    public override void Shutdown()
    {
        FlushNow(final: true);
        _ui.SubmitEmpty(Host);
        try { _loadTask?.Wait(2000); } catch { }
    }

    // ── Events (pump thread) ────────────────────────────────────────────────

    public override void OnCreateObject(uint objectId)
    {
        if (!_identityKnown || _c.Player == 0) return;
        // Only objects that landed in the character's inventory matter (most creates are the landscape).
        if (Host.TryGetObjectOwnershipInfo(objectId, out uint container, out uint wielder, out _)
            && (container == _c.Player || wielder == _c.Player || IsCarriedPack(container)))
            _scheduler.InventoryEvent(Environment.TickCount64);
    }

    public override void OnDeleteObject(uint objectId)
    {
        if (!_identityKnown) return;
        if (_carriedIds.Contains(objectId)) _scheduler.InventoryEvent(Environment.TickCount64);
    }

    public override void OnUpdateObjectInventory(uint objectId)
    {
        if (!_identityKnown) return;
        long now = Environment.TickCount64;
        if (objectId == _openStorage && _openStorage != 0)
            _nextStorageScanMs = Math.Min(_nextStorageScanMs, now + 300);
        else if (objectId == _c.Player || IsCarriedPack(objectId))
            _scheduler.InventoryEvent(now);
    }

    public override void OnViewObjectContents(uint objectId)
    {
        if (!_identityKnown || objectId == 0) return;
        _lastViewed = objectId;
        bool kept = _c.Settings.IsKept(_c.Server, objectId);
        if (!InventoryScanner.IsStorage(Host, objectId, _c.Player, _c.Settings.StorageWordList(), kept, out string name, out string why))
        {
            if (why == "carried") _scheduler.InventoryEvent(Environment.TickCount64);
            return;
        }
        BeginStorage(objectId, name, why);
    }

    public override void OnStopViewingObjectContents(uint objectId)
    {
        if (objectId != 0 && objectId == _openStorage)
        {
            ScanStorage(Environment.TickCount64, final: true);
            CloseStorage();
        }
    }

    // ── Chat ────────────────────────────────────────────────────────────────

    // May run on AC's thread: classify and queue only.
    public override void OnChatBarEnter(string? text, ref int eat)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        string t = text.Trim();
        if (!t.StartsWith("/ginv", StringComparison.OrdinalIgnoreCase) || (t.Length > 5 && !char.IsWhiteSpace(t[5])))
            return;
        eat = 1;
        _commands.Enqueue(t.Length > 5 ? t.Substring(5).Trim() : string.Empty);
    }

    private void RunCommand(string arg)
    {
        // "Find like this in Global Inventory" (the Inventory panel's right-click): the rule as a spec.
        if (arg.StartsWith("like ", StringComparison.OrdinalIgnoreCase) || arg.Equals("like", StringComparison.OrdinalIgnoreCase))
        {
            string spec = arg.Length > 4 ? arg.Substring(4).Trim() : string.Empty;
            if (spec.Length == 0 || spec.Equals("clear", StringComparison.OrdinalIgnoreCase) || spec.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                _view?.SetLike(null);
                return;
            }
            Views.LikeFilter? like = Views.LikeFilter.Parse(spec, out string error);
            if (like == null)
            {
                _c.Print(error + " (Right-click an item in the Inventory panel: Find like this in Global Inventory.)");
                return;
            }
            _view?.SetLike(like);
            if (_window != null) { _window.Visible = true; _window.NeedsPass = true; }
            return;
        }
        // Every /ginv answers in chat (2026-10-28, InventoryCommand.cs; Plugins/Shared/CommandReply.cs).
        // RynthInventory can't tell a typed /ginv from a meta's, so each one does.
        InventoryCommand c = InventoryCommand.Parse(arg);
        switch (c.Verb)
        {
            case InventoryVerb.Window:
                if (_window != null)
                {
                    _window.Visible = !_window.Visible; _window.NeedsPass = true;
                    if (Host.HasUi) _c.Print(InventoryCommand.Window(_window.Visible));
                }
                break;
            case InventoryVerb.Scan:
                _scheduler.RequestFullScan();
                _forceWrite = true;
                _c.Print("Rescanning this character; it saves within a few seconds.");
                break;
            case InventoryVerb.Keep:
                KeepOpenContainer();
                break;
            case InventoryVerb.Reload:
                _reloadWanted = true;
                _files.Clear();
                _c.Print("Reading the inventory files again.");
                break;
            case InventoryVerb.Status:
                PrintStatus();
                break;
            case InventoryVerb.Bank:
                // The Banking tab: every character's Infinite Leaftide bank (RynthILT), Infinite Leaftide only.
                if (!_c.Ilt)
                {
                    _c.Print("The bank is an Infinite Leaftide feature; the active server isn't Infinite Leaftide.");
                    break;
                }
                _view?.ShowBanking();
                _nextBankReadMs = 0;
                if (_window != null) { _window.Visible = true; _window.NeedsPass = true; }
                break;
            case InventoryVerb.Trade:
                // Toggle the trade list window and start a new trade if none is active.
                if (_c.Trade == null)
                    _c.Trade = new TradeList();
                if (_tradeWindow != null) { _tradeWindow.Visible = !_tradeWindow.Visible; _tradeWindow.NeedsPass = true; }
                break;
            case InventoryVerb.Help:
                _c.Print(InventoryCommand.Usage);
                break;
            default:
                _view?.SetSearch(c.Text);
                if (_window != null) { _window.Visible = true; _window.NeedsPass = true; }
                if (Host.HasUi) _c.Print(InventoryCommand.Searching(c.Text));
                break;
        }
        // Without plugin windows the window verbs say why nothing shows (every time).
        if (_window != null && _window.Visible && !Host.HasUi && c.Verb is InventoryVerb.Window or InventoryVerb.Search)
            _c.PrintFail(InventoryCommand.NoWindows);
    }

    private void PrintStatus()
    {
        string who = _identityKnown ? $"{_c.Character} on {_c.Server} (account {_c.Account})" : "no character yet";
        int items = _c.Live?.Items.Count ?? 0, storage = _c.Live?.Storage.Count ?? 0;
        string saved = _c.LastSavedUtc == default ? "not saved yet" : "saved " + InventoryIndex.Age(_c.LastSavedUtc, DateTime.UtcNow);
        _c.Print($"{who}: {items} items carried, {storage} storage container(s), {saved}. {_c.Index?.Characters.Count ?? 0} characters in {_c.Store.Root}.");
        if (_lastPush.Length > 0) _c.Print("From other plugins: " + _lastPush + ".");
        if (_c.LastSaveError.Length > 0) _c.Print("Last save failed: " + _c.LastSaveError);
    }

    private void KeepOpenContainer()
    {
        if (!_identityKnown || _lastViewed == 0)
        {
            _c.PrintFail("Open the container first, then type /ginv keep while it is open.");
            return;
        }
        string name = Host.TryGetObjectName(_lastViewed, out string n) ? n : "container";
        if (!InventoryScanner.IsStorage(Host, _lastViewed, _c.Player, Array.Empty<string>(), kept: true, out name, out string why))
        {
            _c.PrintFail($"{name} can't be kept as storage ({why}).");
            return;
        }
        string key = InventorySettings.KeepKey(_c.Server, _lastViewed);
        if (!_c.Settings.KeptStorage.Contains(key))
        {
            _c.Settings.KeptStorage.Add(key);
            _c.Settings.Save();
        }
        _c.Print($"{name} will be recorded as storage whenever it is opened.");
        BeginStorage(_lastViewed, name, why);
    }

    // ── Tick ────────────────────────────────────────────────────────────────

    public override void OnTick()
    {
        long now = Environment.TickCount64;
        _c.UtcNow = DateTime.UtcNow;

        while (_commands.TryDequeue(out string? cmd)) RunCommand(cmd);
        _c.Reply?.Flush();
        _ui.Poll(Host);

        if (_c.InWorld)
        {
            try { TickScan(now); }
            catch (Exception ex)
            {
                Host.Log($"[RynthInventory] scan failed: {ex.GetType().Name}: {ex.Message}");
                _scheduler.FullScanDone(now, _c.Live?.ContentHash() ?? 0, 0);   // don't retry every tick
            }
        }
        TickWrite(now);
        TickIndex(now);
        TickServer(now);
        TickBank(now);
        TickTrade(now);

        if (!Host.HasUi || _window == null || _view == null) return;
        Record(_window, now, 2000, _view.Draw);
        if (_tradeWindow != null && _tradeView != null && _tradeWindow.Visible)
            Record(_tradeWindow, now, 1000, _tradeView.Draw);
        _ui.Submit(Host, now);

        if (_c.InWorld && _window.Visible != _c.Settings.MainWindowOpen)
        {
            _c.Settings.MainWindowOpen = _window.Visible;
            _c.Settings.Save();
        }
    }

    private void TickScan(long now)
    {
        if (!_identityKnown)
        {
            if (now < _nextIdentityTryMs) return;
            _nextIdentityTryMs = now + IdentityRetryMs;
            if (!InventoryScanner.TryReadIdentity(Host, out uint player, out string server, out string account, out string character))
                return;
            // The engine learns the account name on AC's thread during login; give it a moment
            // so the file lands in the account's folder rather than "unknown".
            if (account == "unknown" && now - _loginMs < AccountWaitMs)
                return;
            StartCharacter(now, player, server, account, character);
        }

        _scanner.WarmUp(Host, _c.Player);
        if (_scheduler.ShouldFullScan(now))
            FullScan(now);
        else if (_scheduler.ShouldPoll(now))
            _scheduler.PollResult(now, _scanner.Fingerprint(Host, _c.Player));

        if (_openStorage != 0 && now >= _nextStorageScanMs)
            ScanStorage(now, final: false);

        TickStoragePushes(now);
    }

    // ── Storage from other plugins ──────────────────────────────────────────

    /// <summary>
    /// RynthPluginApplyRemoteCommand: "storage" (StorageFeed.Action) carries a whole store another
    /// plugin read. Any thread: the payload is only queued (the newest few).
    /// </summary>
    internal void ApplyRemoteCommand(string action, string value)
    {
        if (!action.Equals(StorageFeed.Action, StringComparison.OrdinalIgnoreCase) || value.Length == 0 || value.Length > StorageFeed.MaxJsonLength) return;
        _storagePushes.Enqueue(value);
        while (_storagePushes.Count > MaxQueuedPushes && _storagePushes.TryDequeue(out _)) { }
    }

    /// <summary>Files what other plugins sent, once the character (and so its storage) is known. Plugin tick.</summary>
    private void TickStoragePushes(long now)
    {
        if (_c.Live == null) return;
        while (_storagePushes.TryDequeue(out string? json))
        {
            StorageFeedRecord? feed = StorageFeed.Parse(json, out string error);
            if (feed == null)
            {
                Host.Log("[RynthInventory] storage from another plugin refused: " + error);
                continue;
            }
            StorageRecord record = ExternalStorage.ToRecord(feed, DateTime.UtcNow);
            CharacterSnapshot snap = NewSnapshot(_c.Live.Items, ExternalStorage.FileInto(_c.Live.Storage, record));
            snap.ScannedUtc = _c.Live.ScannedUtc;
            _c.Live = snap;
            _indexDirty = true;
            _scheduler.ContentChanged(now, snap.ContentHash());
            _lastPush = $"{record.Label} ({record.Items.Count} item(s)) at {DateTime.Now:HH:mm}";
        }
    }

    private void StartCharacter(long now, uint player, string server, string account, string character)
    {
        _identityKnown = true;
        _c.Player = player;
        _c.Server = server;
        _c.Account = account;
        _c.Character = character;
        _scheduler.Login(now);

        // Start from this character's saved file: it keeps the storage seen on earlier visits.
        string path = _c.Store.PathFor(server, account, character);
        CharacterSnapshot? saved = InventoryStore.Load(path, out _);
        if (saved == null && account != "unknown")
            saved = InventoryStore.Load(_c.Store.PathFor(server, "unknown", character), out _);
        if (saved != null && InventoryIndex.SameName(saved.Character, character))
        {
            saved.Account = account;
            saved.CharacterId = player;
            _c.Live = saved;
            if (saved.FilePath == path) _scheduler.KnownSaved(saved.ContentHash());
        }
        _indexDirty = true;
        Host.Log($"[RynthInventory] tracking {character} on {server} (account {account}); file {path}");
    }

    private readonly HashSet<uint> _carriedIds = new();
    private readonly HashSet<uint> _carriedPacks = new();

    private bool IsCarriedPack(uint id) => id != 0 && _carriedPacks.Contains(id);

    private void FullScan(long now)
    {
        List<ItemRecord> items = _scanner.ScanCarried(Host, _c.Player);
        ulong fingerprint = _scanner.Fingerprint(Host, _c.Player);
        if (items.Count == 0 && now - _loginMs < EmptyScanGraceMs)
        {
            // The client hasn't got the inventory yet; never save "owns nothing" for that.
            _scheduler.FullScanDone(now, _c.Live?.ContentHash() ?? 0, fingerprint);
            return;
        }

        _carriedIds.Clear();
        _carriedPacks.Clear();
        foreach (ItemRecord it in items)
        {
            _carriedIds.Add(it.Id);
            if (it.Place == ItemPlace.Pack && it.Path.Length == 1 && it.Category == ItemCategory.Container) _carriedPacks.Add(it.Id);
        }

        var snap = NewSnapshot(items, _c.Live?.Storage ?? new List<StorageRecord>());
        snap.ScannedUtc = DateTime.UtcNow;
        _c.Live = snap;
        _indexDirty = true;
        _scheduler.FullScanDone(now, snap.ContentHash(), fingerprint);
    }

    private CharacterSnapshot NewSnapshot(List<ItemRecord> items, List<StorageRecord> storage) => new()
    {
        Server = _c.Server,
        Account = _c.Account,
        Character = _c.Character,
        CharacterId = _c.Player,
        ScannedUtc = _c.Live?.ScannedUtc ?? default,
        PluginVersion = Version,
        Items = items,
        Storage = new List<StorageRecord>(storage),
    };

    // ── Storage ─────────────────────────────────────────────────────────────

    private void BeginStorage(uint id, string name, string why)
    {
        _openStorage = id;
        _openStorageName = name;
        _openStorageLabel = InventoryScanner.StorageLabel(Host, id, name, out _openStorageCell);
        _storageScans = 0;
        _nextStorageScanMs = Environment.TickCount64 + StorageFirstScanMs;
        _c.OpenStorage = id;
        _c.OpenStorageLabel = _openStorageLabel;
        Host.Log($"[RynthInventory] recording {_openStorageLabel} (0x{id:X8}): {why}");
    }

    private void CloseStorage()
    {
        _openStorage = 0;
        _c.OpenStorage = 0;
        _c.OpenStorageLabel = string.Empty;
    }

    private void ScanStorage(long now, bool final)
    {
        if (_openStorage == 0 || _c.Live == null) { _nextStorageScanMs = now + StorageRescanMs; return; }
        List<ItemRecord> items = _scanner.ScanContainer(Host, _openStorage);
        _storageScans++;
        _nextStorageScanMs = now + (_storageScans < StorageSettleScans ? 1500 : StorageRescanMs);
        // Contents arrive a moment after the container opens, and are gone right after it
        // closes: an empty read is only believed once the container has been open a while.
        if (items.Count == 0 && (final || _storageScans < StorageSettleScans)) return;

        var record = new StorageRecord
        {
            Id = _openStorage,
            Name = _openStorageName,
            Label = _openStorageLabel,
            Cell = _openStorageCell,
            SeenUtc = DateTime.UtcNow,
            Items = items,
        };
        var storage = new List<StorageRecord>(_c.Live.Storage);
        int i = storage.FindIndex(s => s.Id == record.Id);
        if (i >= 0) storage[i] = record; else storage.Add(record);
        var snap = NewSnapshot(_c.Live.Items, storage);
        _c.Live = snap;
        _indexDirty = true;
        _scheduler.ContentChanged(now, snap.ContentHash());
    }

    // ── Writing ─────────────────────────────────────────────────────────────

    private void TickWrite(long now)
    {
        if (_writeTask != null)
        {
            if (!_writeTask.IsCompleted) return;
            _writeTask = null;
        }
        if (!_identityKnown || _c.Live == null || !_scheduler.Active) return;
        // Rewrite now and then even when nothing changed, so other clients see this character
        // as current (once a minute after login, then every RefreshWriteMs).
        bool refresh = _lastWriteMs == 0 ? now - _loginMs >= 60_000 : now - _lastWriteMs >= RefreshWriteMs;
        if (!_forceWrite && !refresh && !_scheduler.ShouldWrite(now)) return;
        if (_forceWrite && _scheduler.ShouldFullScan(now)) return;   // /ginv scan: write after the scan
        _forceWrite = false;
        StartWrite(now, _c.Live);
    }

    private void StartWrite(long now, CharacterSnapshot snap)
    {
        // The worker only reads the snapshot; published snapshots are never changed (a change makes a new one).
        if (snap.ScannedUtc == default) snap.ScannedUtc = DateTime.UtcNow;
        ulong hash = snap.ContentHash();
        _lastWriteMs = now;
        InventoryStore store = _c.Store;
        bool checkOld = !_oldAccountChecked && snap.Account != "unknown";
        _oldAccountChecked = true;
        string server = snap.Server, character = snap.Character;
        _writeTask = Task.Run(() =>
        {
            try
            {
                store.Save(snap);
                if (checkOld)
                {
                    // The same character saved earlier without an account name: that file is superseded.
                    string old = store.PathFor(server, "unknown", character);
                    if (File.Exists(old)) store.Forget(old, out _);
                }
                return string.Empty;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }).ContinueWith(t =>
        {
            string error = t.IsFaulted ? (t.Exception?.GetBaseException().Message ?? "failed") : t.Result;
            _writeResult = new WriteResult(hash, error);
        }, TaskScheduler.Default);
    }

    private sealed record WriteResult(ulong Hash, string Error);
    private volatile WriteResult? _writeResult;

    private void TakeWriteResult(long now)
    {
        WriteResult? r = _writeResult;
        if (r == null) return;
        _writeResult = null;
        if (r.Error.Length == 0)
        {
            _scheduler.Written(r.Hash);
            _c.LastSavedUtc = DateTime.UtcNow;
            _c.LastSaveError = string.Empty;
            _reloadWanted = true;   // other views pick the new file up; ours already has it live
        }
        else
        {
            _scheduler.WriteFailed(now);
            _c.LastSaveError = r.Error;
            Host.Log("[RynthInventory] save failed: " + r.Error);
        }
    }

    /// <summary>Logout and shutdown: write now, on this thread, if anything is unsaved (or always at logout).</summary>
    private void FlushNow(bool final)
    {
        try
        {
            try { _writeTask?.Wait(3000); } catch { }
            _writeTask = null;
            TakeWriteResult(Environment.TickCount64);
            if (!_identityKnown || _c.Live == null) return;
            if (!final && !_scheduler.Dirty) return;
            CharacterSnapshot snap = _c.Live;
            if (snap.ScannedUtc == default) snap.ScannedUtc = DateTime.UtcNow;
            _c.Store.Save(snap);
            _scheduler.Written(snap.ContentHash());
            _c.LastSavedUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            try { Host.Log("[RynthInventory] final save failed: " + ex.Message); } catch { }
        }
    }

    private string ForgetCharacter(CharacterInfo ci)
    {
        int moved = 0;
        string? error = null;
        var paths = new List<string>(ci.ShadowedFiles);
        if (!string.IsNullOrEmpty(ci.Snapshot.FilePath)) paths.Add(ci.Snapshot.FilePath);
        else paths.Add(_c.Store.PathFor(ci.Server, ci.Account, ci.Name));   // the live one: its own file
        foreach (string p in paths)
        {
            if (_c.Store.Forget(p, out string? e)) moved++;
            else if (e != "already gone") error = e;
        }
        _reloadWanted = true;
        _files.Clear();
        if (ci.IsCurrentCharacter)
        {
            // Back at the next write; start from a clean slate so the old storage isn't re-saved.
            if (_c.Live != null) _c.Live = NewSnapshot(_c.Live.Items, new List<StorageRecord>());
            _scheduler.ContentChanged(Environment.TickCount64, 1);
        }
        _indexDirty = true;
        if (moved > 0) return $"Forgot {ci.Name} on {ci.Server} (moved to {InventoryStore.ForgottenFolder}).";
        return error != null ? $"Couldn't forget {ci.Name}: {error}" : $"{ci.Name} had no file to move.";
    }

    // ── Merged view ─────────────────────────────────────────────────────────

    private void TickIndex(long now)
    {
        TakeWriteResult(now);

        if (_loadTask != null && _loadTask.IsCompleted)
        {
            try
            {
                var (files, broken) = _loadTask.Result;
                _files = new Dictionary<string, (InventoryFileStamp, CharacterSnapshot?)>(files, StringComparer.OrdinalIgnoreCase);
                _c.BrokenFiles = broken;
            }
            catch (Exception ex)
            {
                Host.Log("[RynthInventory] reading files failed: " + ex.Message);
            }
            _loadTask = null;
            _c.Loading = false;
            _indexDirty = true;
        }

        // Files are only listed and read while the window is open (and once at start).
        bool visible = _window?.Visible ?? false;
        if (_loadTask == null && (visible || _c.Index == null) && (_reloadWanted || now >= _nextFileCheckMs))
        {
            _nextFileCheckMs = now + FileCheckMs;
            _reloadWanted = false;
            StartLoad();
        }

        if (_indexDirty && (visible || _c.Index == null) && _loadTask == null)
        {
            _indexDirty = false;
            var snaps = new List<CharacterSnapshot>();
            foreach (var (_, snap) in _files.Values) if (snap != null) snaps.Add(snap);
            _c.Index = InventoryIndex.Build(snaps, _c.InWorld ? _c.Live : null, _c.Server, _c.Character);
            _c.IndexRevision++;
            if (_window != null) _window.NeedsPass = true;
        }
    }

    /// <summary>Lists the files and re-reads the ones that changed, on a worker.</summary>
    private void StartLoad()
    {
        InventoryStore store = _c.Store;
        var known = new Dictionary<string, (InventoryFileStamp Stamp, CharacterSnapshot? Snapshot)>(_files, StringComparer.OrdinalIgnoreCase);
        _c.Loading = _c.Index == null;
        _loadTask = Task.Run(() =>
        {
            var result = new Dictionary<string, (InventoryFileStamp, CharacterSnapshot?)>(StringComparer.OrdinalIgnoreCase);
            int broken = 0;
            foreach (InventoryFileStamp f in store.ListFiles())
            {
                if (known.TryGetValue(f.Path, out var have) && have.Stamp == f)
                {
                    result[f.Path] = (f, have.Snapshot);
                    if (have.Snapshot == null) broken++;
                    continue;
                }
                CharacterSnapshot? s = InventoryStore.Load(f.Path, out _);
                if (s == null) broken++;
                result[f.Path] = (f, s);
            }
            return (result, broken);
        });
    }

    private void RefreshIconSupport()
    {
        _c.IconsAvailable = Host.HasUi && Host.TryGetUiInfo(out UiInfoNative info) && info.OpLevel >= 2;
    }

    /// <summary>A pass when one is wanted, or on the refresh interval while visible (ages tick on).</summary>
    private void Record(UiWindow w, long now, long interval, Action<UiWindow> draw)
    {
        if (_c.Index != null && _view != null && w.Visible && _c.IndexRevision != _lastDrawnRevision)
        {
            _lastDrawnRevision = _c.IndexRevision;
            w.NeedsPass = true;
        }
        bool due = w.NeedsPass || (w.Visible && now >= w.NextPassMs);
        if (!due) return;
        if (!_c.IconsAvailable && Host.HasUi) RefreshIconSupport();
        w.NeedsPass = false;
        w.NextPassMs = now + interval;
        w.Begin();
        try
        {
            draw(w);
        }
        catch (Exception ex)
        {
            w.TextWrapped($"Something went wrong drawing this window: {ex.GetType().Name}: {ex.Message}");
            Host.Log($"[RynthInventory] {w.Key} pass failed: {ex}");
        }
        w.End();
    }

    private int _lastDrawnRevision = -1;

    // ── Trade execution ────────────────────────────────────────────────
    // A started trade is a shared job (Data/TradeJobs.cs), because every account plays in its
    // own client: the client that built the list writes the job; every client (that one too)
    // gives the items of the account it is logged into, logging that account onto the
    // character that holds them (/rc switchchar, RynthCore 2026.10.25+), and writes how each
    // went. The client that started the trade reads every account's status back into its list.

    private const long TradeTickMs = 1000;
    private const long TradeGiveIntervalMs = 1500;
    /// <summary>After a login the pack scan has to land before items can be found.</summary>
    private const long TradeLoginSettleMs = 5000;
    private const long TradeRecipientRetryMs = 5000;
    /// <summary>A switch that didn't happen (old engine, char-select trouble) is asked for again after this.</summary>
    private const long TradeSwitchRetryMs = 60_000;
    private const long TradeHeartbeatMs = 10_000;
    private static readonly TimeSpan TradeStaleStatus = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TradeNoClientGrace = TimeSpan.FromSeconds(15);

    private long _nextTradeTickMs;
    private long _nextTradeGiveMs;
    private long _lastSwitchRequestMs;
    private string _lastSwitchTarget = string.Empty;
    private long _lastTradeStatusWriteMs;
    // The job this client is giving for and its own report on it (this client is its only writer).
    private string _givingJobId = string.Empty;
    private TradeAccountStatus? _givingStatus;

    private string TradeFolder => TradeJobs.Folder(_c.Store.Root);

    private void RunTradeCommand(string cmd)
    {
        switch (cmd)
        {
            case "start":
                StartTrade();
                break;
            case "cancel":
                CancelTrade();
                break;
            case "clear":
            case "new":
                if (_c.Trade != null && _c.Trade.JobId.Length > 0 && _c.Trade.Phase != TradePhase.Executing)
                    TradeJobs.Delete(TradeFolder, _c.Trade.JobId);
                _c.Trade = new TradeList();
                if (_tradeWindow != null) _tradeWindow.NeedsPass = true;
                break;
            case "toggle":
                if (_c.Trade == null)
                    _c.Trade = new TradeList();
                if (_tradeWindow != null) { _tradeWindow.Visible = !_tradeWindow.Visible; _tradeWindow.NeedsPass = true; }
                break;
        }
    }

    private void StartTrade()
    {
        TradeList? trade = _c.Trade;
        if (trade == null || trade.Items.Count == 0) return;
        string recipient = trade.Recipient.Trim();
        if (recipient.Length == 0)
        {
            trade.Message = "Enter a player name to send items to.";
            if (_tradeWindow != null) _tradeWindow.NeedsPass = true;
            return;
        }
        if (!_c.Host.HasGiveObjectTo)
        {
            trade.Message = "This engine doesn't support GiveObjectTo (needs API v62+).";
            if (_tradeWindow != null) _tradeWindow.NeedsPass = true;
            return;
        }

        var job = new TradeJob
        {
            Id = TradeJobs.NewId(),
            Server = trade.Server,
            Recipient = recipient,
            StartedBy = _c.Character.Length > 0 ? $"{_c.Character} ({_c.Account})" : _c.Account,
            StartedUtc = DateTime.UtcNow,
        };
        foreach (TradeItem item in trade.Items)
        {
            item.Status = TradeItemStatus.Pending;
            item.StatusText = string.Empty;
            job.Items.Add(item);
        }
        try { TradeJobs.WriteJob(TradeFolder, job); }
        catch (Exception ex)
        {
            trade.Message = "Couldn't write the trade file: " + ex.Message;
            if (_tradeWindow != null) _tradeWindow.NeedsPass = true;
            return;
        }

        trade.JobId = job.Id;
        trade.StartedUtc = job.StartedUtc;
        trade.Phase = TradePhase.Executing;
        trade.Message = $"Trade started. Each account's client logs onto the character holding the items and gives them to {recipient}: {recipient} has to stand next to where each of those characters logs in.";
        _nextTradeTickMs = 0;
        if (_tradeWindow != null) { _tradeWindow.Visible = true; _tradeWindow.NeedsPass = true; }
        _c.Print($"Trade started: {trade.Items.Count} item(s) to {recipient}.");
    }

    private void CancelTrade()
    {
        TradeList? trade = _c.Trade;
        if (trade == null) return;
        if (trade.JobId.Length > 0)
        {
            TradeJob? job = TradeJobs.ReadJob(TradeFolder, trade.JobId);
            if (job != null && !job.Cancelled)
            {
                job.Cancelled = true;
                try { TradeJobs.WriteJob(TradeFolder, job); } catch { }
            }
        }
        trade.Phase = TradePhase.Cancelled;
        trade.Message = "Trade cancelled.";
        if (_tradeWindow != null) _tradeWindow.NeedsPass = true;
    }

    private void TickTrade(long now)
    {
        if (now < _nextTradeTickMs) return;
        _nextTradeTickMs = now + TradeTickMs;
        try { TickTradeGiver(now); }
        catch (Exception ex) { Host.Log($"[RynthInventory] trade giver failed: {ex.GetType().Name}: {ex.Message}"); }
        try { MergeTradeStatus(); }
        catch (Exception ex) { Host.Log($"[RynthInventory] trade status read failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>The client that started the trade: every account's status file into its list.</summary>
    private void MergeTradeStatus()
    {
        TradeList? trade = _c.Trade;
        if (trade == null || trade.JobId.Length == 0 || trade.Phase == TradePhase.Building) return;

        List<TradeAccountStatus> statuses = TradeJobs.ReadStatuses(TradeFolder, trade.JobId);
        for (int i = 0; i < trade.Items.Count; i++)
        {
            TradeItem item = trade.Items[i];
            foreach (TradeAccountStatus st in statuses)
            {
                if (!TradeJobs.SameAccount(st.Account, item.Account)) continue;
                if (st.Items.TryGetValue(i, out var v)) { item.Status = v.Status; item.StatusText = v.Text; }
                break;
            }
        }

        trade.AccountLines.Clear();
        var accounts = new List<string>();
        foreach (TradeItem item in trade.Items)
            if (!accounts.Exists(a => TradeJobs.SameAccount(a, item.Account))) accounts.Add(item.Account);
        foreach (string account in accounts)
        {
            bool accountDone = trade.Items.TrueForAll(it => !TradeJobs.SameAccount(it.Account, account)
                || it.Status is not (TradeItemStatus.Pending or TradeItemStatus.Giving));
            TradeAccountStatus? st = statuses.Find(s => TradeJobs.SameAccount(s.Account, account));
            string line;
            if (st == null)
                line = DateTime.UtcNow - trade.StartedUtc > TradeNoClientGrace
                    ? $"{account}: no client logged into this account has picked it up (is one running with RynthInventory?)"
                    : $"{account}: waiting for its client...";
            else
            {
                line = $"{account}: " + (st.Character.Length > 0 ? st.Character + " - " : "") + st.Message;
                if (!accountDone && DateTime.UtcNow - st.UpdatedUtc > TradeStaleStatus)
                    line += " (its client has gone quiet - logging in, or closed?)";
            }
            trade.AccountLines.Add(line);
        }

        if (trade.Phase == TradePhase.Executing && trade.AllDone())
        {
            trade.Phase = TradePhase.Complete;
            var (given, notFound, failed, _) = trade.Counts();
            trade.Message = $"Trade complete: {given} given" +
                            (notFound > 0 ? $", {notFound} not found" : "") +
                            (failed > 0 ? $", {failed} failed" : "") + ".";
            _c.Print(trade.Message);
        }
        if (_tradeWindow != null) _tradeWindow.NeedsPass = true;
    }

    /// <summary>Every client: gives the items of the account it is logged into.</summary>
    private void TickTradeGiver(long now)
    {
        if (!_c.InWorld || !_identityKnown || _c.Account.Length == 0 || _c.Account == "unknown") return;
        if (now - _loginMs < TradeLoginSettleMs) return;

        List<TradeJob> jobs = TradeJobs.ReadJobs(TradeFolder, _c.Server);
        foreach (TradeJob job in jobs)
        {
            var mine = new List<int>();
            for (int i = 0; i < job.Items.Count; i++)
                if (TradeJobs.SameAccount(job.Items[i].Account, _c.Account)) mine.Add(i);
            if (mine.Count == 0) continue;

            TradeAccountStatus st = GivingStatusFor(job.Id);
            var pending = mine.FindAll(i => !st.Items.TryGetValue(i, out var v) || v.Status is TradeItemStatus.Pending or TradeItemStatus.Giving);
            if (pending.Count == 0)
            {
                int given = mine.FindAll(i => st.Items[i].Status == TradeItemStatus.Given).Count;
                string done = $"done: {given} of {mine.Count} given.";
                if (!string.Equals(st.Message, done, StringComparison.Ordinal)) SetTradeStatus(job.Id, st, done, now, force: true);
                continue;
            }
            if (job.Cancelled)
            {
                if (!string.Equals(st.Message, "trade cancelled.", StringComparison.Ordinal)) SetTradeStatus(job.Id, st, "trade cancelled.", now, force: true);
                continue;
            }
            GiveForJob(job, st, pending, now);
            return;   // one job at a time, oldest first
        }
    }

    private TradeAccountStatus GivingStatusFor(string jobId)
    {
        if (_givingStatus != null && string.Equals(_givingJobId, jobId, StringComparison.Ordinal))
            return _givingStatus;
        _givingJobId = jobId;
        _givingStatus = TradeJobs.ReadStatus(TradeFolder, jobId, _c.Account) ?? new TradeAccountStatus { Account = _c.Account };
        _givingStatus.Account = _c.Account;
        _lastTradeStatusWriteMs = 0;
        return _givingStatus;
    }

    private void GiveForJob(TradeJob job, TradeAccountStatus st, List<int> pending, long now)
    {
        // This account's only character online can't be someone else at the same time: when it is
        // the recipient, the items on its other characters can't be given from here.
        if (InventoryIndex.SameName(job.Recipient, _c.Character))
        {
            foreach (int i in pending)
                st.Items[i] = (TradeItemStatus.Failed, $"on {job.Items[i].CharacterName}, the recipient's own account (only one of its characters can be online)");
            SetTradeStatus(job.Id, st, "the recipient's account: these items can't be given while it is logged in.", now, force: true);
            return;
        }

        string myKey = CharacterSnapshot.MakeKey(_c.Server, _c.Character);
        List<int> here = pending.FindAll(i => string.Equals(job.Items[i].CharacterKey, myKey, StringComparison.Ordinal));
        if (here.Count > 0)
        {
            if (now < _nextTradeGiveMs) { SetTradeStatus(job.Id, st, st.Message, now, force: false); return; }

            uint target = ResolvePlayerByName(job.Recipient);
            if (target == 0)
            {
                _nextTradeGiveMs = now + TradeRecipientRetryMs;
                SetTradeStatus(job.Id, st, $"waiting for {job.Recipient} to come near {_c.Character} ({here.Count} item(s) to give).", now, force: false);
                return;
            }

            int idx = here[0];
            TradeItem item = job.Items[idx];
            uint objId = FindItemOnCharacter(item);
            if (objId == 0)
                st.Items[idx] = (TradeItemStatus.NotFound, $"not found on {_c.Character}");
            else if (_c.Host.GiveObjectTo(objId, target, item.Quantity))
            {
                st.Items[idx] = (TradeItemStatus.Given, "given");
                _c.Print($"Trade: gave {item.ItemName}{(item.Quantity > 0 ? " x" + item.Quantity.ToString(CultureInfo.InvariantCulture) : "")} to {job.Recipient}.");
            }
            else
                st.Items[idx] = (TradeItemStatus.Failed, "the give was refused");
            _nextTradeGiveMs = now + TradeGiveIntervalMs;
            SetTradeStatus(job.Id, st, $"giving to {job.Recipient} ({here.Count - 1} left on {_c.Character}).", now, force: true);
            return;
        }

        // The rest are on another character of this account: log onto it.
        TradeItem next = job.Items[pending[0]];
        bool sameTarget = InventoryIndex.SameName(_lastSwitchTarget, next.CharacterName);
        if (sameTarget && now - _lastSwitchRequestMs < TradeSwitchRetryMs)
        {
            SetTradeStatus(job.Id, st, st.Message, now, force: false);
            return;
        }
        if (sameTarget && _lastSwitchRequestMs != 0)
            Host.Log($"[RynthInventory] trade: still not on {next.CharacterName} {TradeSwitchRetryMs / 1000}s after asking; asking again.");
        _lastSwitchTarget = next.CharacterName;
        _lastSwitchRequestMs = now;
        SetTradeStatus(job.Id, st, $"logging onto {next.CharacterName} to give its items.", now, force: true);
        _c.Print($"Trade: logging {_c.Account} onto {next.CharacterName} to give its items to {job.Recipient}.");
        if (_c.Host.HasInvokeChatParser)
            _c.Host.InvokeChatParser("/rc switchchar " + next.CharacterName);
    }

    /// <summary>Writes this account's status file when something changed (or as a heartbeat).</summary>
    private void SetTradeStatus(string jobId, TradeAccountStatus st, string message, long now, bool force)
    {
        bool changed = force || !string.Equals(st.Message, message, StringComparison.Ordinal)
                       || !string.Equals(st.Character, _c.Character, StringComparison.Ordinal);
        if (!changed && now - _lastTradeStatusWriteMs < TradeHeartbeatMs) return;
        st.Message = message;
        st.Character = _c.Character;
        st.UpdatedUtc = DateTime.UtcNow;
        try
        {
            TradeJobs.WriteStatus(TradeFolder, jobId, st);
            _lastTradeStatusWriteMs = now;
        }
        catch (Exception ex)
        {
            Host.Log($"[RynthInventory] trade status write failed: {ex.Message}");
            _lastTradeStatusWriteMs = now;   // try again at the next heartbeat, not every tick
        }
    }

    private const uint BitfieldPlayer = 0x00000008;

    /// <summary>
    /// Resolves a player name to a nearby object id. Searches the live objects for a player
    /// whose name matches (case-insensitive).
    /// </summary>
    private uint ResolvePlayerByName(string name)
    {
        if (!Host.HasGetLiveObjectIds) return 0;
        uint[] ids = Host.GetLiveObjectIds();
        string trimmed = name.Trim();
        foreach (uint id in ids)
        {
            if (id == _c.Player) continue;
            if (!Host.TryGetObjectBitfield(id, out uint bf) || (bf & BitfieldPlayer) == 0) continue;
            if (Host.TryGetObjectName(id, out string objName)
                && string.Equals(objName, trimmed, StringComparison.OrdinalIgnoreCase))
                return id;
        }
        return 0;
    }

    /// <summary>
    /// Finds an item on the current character matching a trade item (by object id first, then by
    /// wcid + name). Returns 0 if not found.
    /// </summary>
    private uint FindItemOnCharacter(TradeItem item)
    {
        if (_c.Player == 0) return 0;
        // First, try the exact object id.
        if (item.ItemId != 0 && Host.TryGetObjectName(item.ItemId, out _))
        {
            if (Host.TryGetObjectOwnershipInfo(item.ItemId, out uint container, out uint wielder, out _)
                && (container == _c.Player || wielder == _c.Player || IsCarriedPack(container)))
                return item.ItemId;
        }
        // Fall back to scanning inventory for matching wcid + name.
        if (item.Wcid == 0) return 0;
        if (!Host.HasGetContainerContents) return 0;
        uint found = FindItemInContainer(_c.Player, item);
        if (found != 0) return found;
        // Check carried containers (packs).
        foreach (uint packId in _carriedPacks)
        {
            found = FindItemInContainer(packId, item);
            if (found != 0) return found;
        }
        return 0;
    }

    private uint FindItemInContainer(uint containerId, TradeItem item)
    {
        uint[] buf = new uint[256];
        int count = Host.GetContainerContents(containerId, buf);
        for (int i = 0; i < count; i++)
        {
            uint id = buf[i];
            if (Host.TryGetObjectWcid(id, out uint wcid) && wcid == item.Wcid
                && Host.TryGetObjectName(id, out string name)
                && string.Equals(name, item.ItemName, StringComparison.OrdinalIgnoreCase))
                return id;
        }
        return 0;
    }
}
