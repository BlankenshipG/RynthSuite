using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using RynthCore.PluginSdk;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

internal sealed partial class LegacyDashboardRenderer
{
    internal static readonly Vector4 ColTeal = new(0.15f, 0.85f, 0.90f, 1.00f);
    internal static readonly Vector4 ColAmber = new(0.91f, 0.70f, 0.20f, 1.00f);
    internal static readonly Vector4 ColGreen = new(0.25f, 0.85f, 0.45f, 1.00f);
    internal static readonly Vector4 ColTextDim = new(0.85f, 0.90f, 0.95f, 1.00f);
    internal static readonly Vector4 ColTextMute = new(0.55f, 0.65f, 0.75f, 1.00f);
    internal static readonly Vector4 ColHp = new(0.85f, 0.20f, 0.20f, 1.00f);
    internal static readonly Vector4 ColMana = new(0.15f, 0.55f, 0.95f, 1.00f);
    internal static readonly Vector4 ColBarBg = new(0.08f, 0.12f, 0.16f, 1.00f);
    internal static readonly Vector4 ColPanelBg = new(0.04f, 0.07f, 0.10f, 0.95f);
    internal static readonly Vector4 ColBtnOn = new(0.15f, 0.30f, 0.35f, 1.00f);
    internal static readonly Vector4 ColBtnFill = new(0.06f, 0.12f, 0.18f, 1.00f);
    internal static readonly Vector4 ColBtnHov = new(0.10f, 0.18f, 0.25f, 1.00f);
    internal static readonly Vector4 ColBtnAct = new(0.08f, 0.15f, 0.22f, 1.00f);
    internal static readonly Vector4 ColBtnBord = new(0.15f, 0.25f, 0.35f, 1.00f);

    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings = new();
    public LegacyUiSettings Settings => _settings;

    /// <summary>Set by the plugin so the meta bridge JSON can carry a live
    /// MetaManager snapshot for the panel's debug surface (§3.4).</summary>
    internal Func<RynthCore.Plugin.RynthAi.Meta.MetaSnapshot?>? MetaSnapshotProvider;

    // Meta commands arrive on the Avalonia dispatcher thread; applied on the
    // plugin-tick thread (DrainMetaCommands) so mutation is serialised with
    // MetaManager.Think instead of racing it.
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _metaCmdQueue = new();
    private readonly LegacyAdvancedSettingsUi _advancedSettingsUi;
    private readonly LegacyNavigationUi _navigationUi;
    private readonly LegacyWeaponsUi _weaponsUi;
    private readonly LegacyMetaUi _metaUi;
    private readonly DungeonMapUi _dungeonMapUi;
    private readonly RynthRadarUi _rynthRadarUi;
    private readonly RynthChatUi _rynthChatUi;

    private readonly List<string> _profiles = new();
    private readonly List<string> _navFiles = new();
    private readonly List<string> _lootFiles = new();
    private readonly List<string> _metaFiles = new();
    // Guards the four profile-name lists above against the cross-thread race
    // between BuildSnapshotJson (Avalonia panel poll thread, ~30 Hz) and the
    // Refresh*Files mutators (AC pump thread). Enumerating a List<string> while
    // another thread Clear()s it throws InvalidOperationException; when that
    // escaped the snapshot poll's reverse-P/Invoke boundary it fail-fasted the
    // NativeAOT runtime. Copy-under-lock on read; lock the Clear+AddRange swap.
    private readonly object _profileListsLock = new();
    private readonly string _navFolder = @"C:\Games\RynthSuite\RynthAi\NavProfiles";
    private readonly string _lootFolder = @"C:\Games\RynthSuite\RynthAi\LootProfiles";
    private readonly string _metaFolder = @"C:\Games\RynthSuite\RynthAi\MetaFiles";
    // Profiles are kept per server: SettingsProfiles\<server>\<character> and
    // MonsterProfiles\<server>\<character>.json, the server being the world name
    // the server sends at login. Before 2026-09-28 every server shared
    // SettingsProfiles\ACEmulator, so the same character name on two servers
    // shared one profile; that folder is still used when no name is known.
    private const string ProfilesBase       = @"C:\Games\RynthSuite\RynthAi\SettingsProfiles";
    private const string MonstersBase       = @"C:\Games\RynthSuite\RynthAi\MonsterProfiles";
    private const string LegacyServerFolder = "ACEmulator";
    private string _serverFolder = LegacyServerFolder;
    private string _settingsRoot   => Path.Combine(ProfilesBase, _serverFolder);
    private string _monstersFolder => _serverFolder == LegacyServerFolder ? MonstersBase : Path.Combine(MonstersBase, _serverFolder);

    private int _selectedNavIdx;
    private bool _isMinimized;
    private bool _isLocked;
    private float _bgOpacity = 0.95f;

    /// <summary>Wired by RynthAiPlugin to forward force-rebuff / cancel requests to BuffManager.</summary>
    public Action? OnForceRebuffRequested { get; set; }
    public Action? OnCancelForceRebuffRequested { get; set; }

    // ── Per-character settings persistence ───────────────────────────────────
    private string _charFolder = string.Empty;
    private string _settingsFilePath = string.Empty;
    private string _lastSavedJson = string.Empty;

    private Vector2 _expandedSize = new(430, 452);
    private string _targetLabel = "NO TARGET";
    private float _targetHealthPercent;
    private string _targetHealthDisplay = "0";
    private uint _targetHealth;
    private uint _targetMaxHealth;
    private uint _targetStamina;
    private uint _targetMaxStamina;
    private uint _targetMana;
    private uint _targetMaxMana;
    private uint _currentTargetId;
    private uint _playerHealth;
    private uint _playerMaxHealth;
    private uint _playerStamina;
    private uint _playerMaxStamina;
    private uint _playerMana;
    private uint _playerMaxMana;

    // ── Session kills for the status feed (kills/hour). The other stats (xp/lum/deaths/vitae)
    // are read engine-side (PrefetchPlayerStats) and written to the status file directly — the
    // off-thread plugin pump can't do those main-thread AC reads. ──
    private DateTime _sessionStartUtc = DateTime.UtcNow;
    private long _sessionKills;       // Interlocked: incremented on the pump thread, read in the snapshot
    private double _killsPerHour;
    private long _lastKillTicks;      // DateTime.UtcNow.Ticks of the last kill (0 = none yet this session)
    // volatile: written on the pump thread (SetFreeSlots/SetComponentCounts), read on the snapshot-poll
    // thread (BuildSnapshotJson). x86 int writes are atomic but give no visibility guarantee — without
    // volatile the reader can see a stale value indefinitely. (_sessionKills uses Interlocked for the same reason.)
    private volatile int _freeSlots = -1;      // main-pack empty slots; -1 = unknown. Pushed from the plugin tick.
    private volatile int _scarabs = -1;        // total scarab spell components in inventory; -1 = unknown.
    private volatile int _tapers = -1;         // total prismatic tapers in inventory; -1 = unknown.
    // Per-tier scarab breakdown (name -> count), pushed from the pump thread as a fresh immutable array;
    // volatile ref so the snapshot-poll thread always sees the latest (ref assignment is atomic).
    private volatile KeyValuePair<string, int>[] _scarabsByType = System.Array.Empty<KeyValuePair<string, int>>();
    // Equipped gear with full appraisal, pushed from the pump thread; volatile ref.
    private volatile EquipAppraisal[] _equipment = System.Array.Empty<EquipAppraisal>();

    // D2 three-tier target telemetry + D6 attack-cast/kill ratio, pushed from the pump thread
    // (SetScanCounts/SetCastStats) and read on the snapshot-poll thread — volatile for visibility
    // (same rationale as _freeSlots above). -1 = no scan yet this session.
    private volatile int _scanTotal = -1;      // monsters the client renders this scan
    private volatile int _scanRing = -1;       // of those, within engage range
    private volatile int _scanPossible = -1;   // of those, surviving all combat filters (attack candidates)
    private volatile int _scanLosBlocked = -1; // in-range + attackable but wall-blocked (LOS)
    private volatile int _sessionAttackCasts;  // offensive combat casts issued this session
    private volatile int _castsSinceLastKill;  // offensive casts since the last credited kill (orphan signal)

    // Full read-only inventory for the remote viewer (P1), pushed from the pump thread; volatile refs.
    // Kept OUT of BuildSnapshotJson (the 150ms hot path) — served by its own RynthPluginGetInventoryJson
    // export so 100s of items never ride the status snapshot. _inventoryVersion bumps on each rescan.
    private volatile InventoryItemSnapshot[] _invItems = System.Array.Empty<InventoryItemSnapshot>();
    private volatile InventoryContainerSnapshot[] _invContainers = System.Array.Empty<InventoryContainerSnapshot>();
    private int _inventoryVersion;   // Interlocked: bumped on write, read in BuildInventoryJson

    // ── Monster editor (external process) ────────────────────────────────────
    // Deep-audit finding #10 (2026-06-18): System.Diagnostics.Process is the
    // documented H6 hazard — HasExited/CloseMainWindow/Process.Start's
    // handle-touching accessors silently AV this host under NativeAOT in
    // injected x86 acclient.exe (the engine already abandoned this API for
    // OpenProcess/GetExitCodeProcess in PluginLoader.IsPidAliveWin32 for the
    // identical reason). Runs synchronously on the ImGui/game thread from the
    // "External Editor" button. Replaced with ShellExecuteExW (retaining the
    // process handle via SEE_MASK_NOCLOSEPROCESS) + Win32 liveness/close.
    private FileSystemWatcher? _monsterWatcher;
    private volatile bool _monsterFileChanged;
    private IntPtr _monsterEditorProcessHandle = IntPtr.Zero;
    private int _monsterEditorPid;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFOW
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }

    private const uint SeeMaskNoCloseProcess = 0x00000040;
    private const int SwShowNormal = 1;
    private const uint WaitTimeout = 0x00000102;
    private const uint WmClose = 0x0010;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteExW(ref SHELLEXECUTEINFOW lpExecInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>Finds the first visible top-level window owned by the given PID (mirrors what
    /// Process.CloseMainWindow does internally) so WM_CLOSE can be posted without touching
    /// System.Diagnostics.Process.</summary>
    private static IntPtr FindMainWindowForPid(int pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint wndPid);
            if (wndPid == (uint)pid && IsWindowVisible(hWnd))
            {
                found = hWnd;
                return false; // stop enumerating
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Releases the tracked Monster Editor process handle, if any. Call on plugin
    /// Shutdown so the handle isn't leaked if the editor is still open when RynthAi unloads.</summary>
    public void ReleaseMonsterEditorHandle()
    {
        if (_monsterEditorProcessHandle != IntPtr.Zero)
        {
            CloseHandle(_monsterEditorProcessHandle);
            _monsterEditorProcessHandle = IntPtr.Zero;
            _monsterEditorPid = 0;
        }
    }

    public LegacyDashboardRenderer(RynthCoreHost host)
    {
        _host = host;
        _advancedSettingsUi = new LegacyAdvancedSettingsUi(_settings);
        _navigationUi = new LegacyNavigationUi(_settings, host);
        _weaponsUi = new LegacyWeaponsUi(_settings, host);
        _metaUi = new LegacyMetaUi(_settings, _navFiles);
        _dungeonMapUi = new DungeonMapUi();
        _rynthRadarUi = new RynthRadarUi(host, _settings);
        _rynthRadarUi.SetMapData(_dungeonMapUi);
        _rynthChatUi = new RynthChatUi(host, _settings);
        RefreshAllLists();
    }

    public void OnLoginComplete()
    {
        RefreshAllLists();
        ResetSessionStats();
    }

    /// <summary>
    /// Returns the current per-character folder (set during LoadSettings).
    /// Empty string until a character has logged in.
    /// </summary>
    public string CharFolder => _charFolder;

    public void SetWorldFilter(WorldObjectCache cache)
    {
        _weaponsUi.SetWorldFilter(cache);
        _weaponCache = cache;
    }

    private WorldObjectCache? _weaponCache;

    /// <summary>A listed weapon's full name for the panels: "Silver Wand", and with
    /// <paramref name="withElement"/> "Silver Wand (Fire)". Display only; rules are keyed by id.</summary>
    internal string WeaponDisplayName(ItemRule r, bool withElement)
    {
        string name = WeaponNames.For(_host, _weaponCache, r.Id, r.Name);
        return withElement ? WeaponNames.WithElement(name, r.Element) : name;
    }

    public void SetMissileCraftingManager(MissileCraftingManager mgr) => _advancedSettingsUi.SetMissileCraftingManager(mgr);
    public void SetAutoVendorStatusProvider(Func<string> status) => _advancedSettingsUi.SetAutoVendorStatusProvider(status);
    public void SetAutoTradeStatusProvider(Func<string> status) => _advancedSettingsUi.SetAutoTradeStatusProvider(status);

    // The open vendor's AutoVendor profile path (null when no vendor is open), for the
    // dashboard snapshot's vendorProfilePath.
    private Func<string?>? _vendorProfilePath;
    public void SetVendorProfilePathProvider(Func<string?> path) => _vendorProfilePath = path;

    public void SetRaycast(Raycasting.MainLogic raycast)
    {
        _dungeonMapUi.SetRaycast(raycast);
    }

    /// <summary>Bake the floor plans of a dungeon just entered, with no map panel open
    /// (DrakRemote's /map). Cheap: one pose read; the work runs on a pool thread.</summary>
    public void TickDungeonMapBake() => _dungeonMapUi?.TickBake(_host); // null in tests' bare dashboards

    public void PushChatLine(string? text, int chatType) => _rynthChatUi.Push(text, chatType);

    /// <summary>Set the handler invoked when the user submits a line from the
    /// custom chat widget. Wired by the plugin so slash commands can be routed
    /// through OnChatBarEnter before falling back to InvokeChatParser.</summary>
    public Action<string>? ChatSubmitHandler
    {
        get => _rynthChatUi.OnSubmit;
        set => _rynthChatUi.OnSubmit = value;
    }
    public void SetWorldObjectCache(WorldObjectCache cache)
    {
        _rynthRadarUi.SetWorldObjectCache(cache);
    }

    /// <summary>Fellowship membership for the radar's fellow and your-corpse markers.</summary>
    public void SetFellowshipTracker(FellowshipTracker? tracker)
    {
        _rynthRadarUi.IsFellowId = tracker == null ? null : tracker.IsMember;
        _rynthRadarUi.IsFellowName = tracker == null ? null : tracker.IsMember;
    }

    // ── Settings persistence ─────────────────────────────────────────────────

    public void LoadSettings(string charName)
    {
        if (string.IsNullOrWhiteSpace(charName)) return;

        string safeChar = SanitizeFileName(charName);
        _serverFolder = ResolveServerFolder();
        _charFolder = Path.Combine(_settingsRoot, safeChar);
        MigrateToServerFolder(safeChar);

        // Migrate legacy settings.json → Default.json if needed
        string legacyPath = Path.Combine(_charFolder, "settings.json");
        string defaultProfilePath = GetProfileFilePath("Default");
        if (!File.Exists(defaultProfilePath) && File.Exists(legacyPath))
        {
            try { File.Copy(legacyPath, defaultProfilePath); } catch { }
        }

        // Determine which profile was last active
        string activeProfile = ReadActiveProfile();
        _settingsFilePath = GetProfileFilePath(activeProfile);

        // Fall back to Default if the profile file is missing
        if (!File.Exists(_settingsFilePath))
        {
            activeProfile = "Default";
            _settingsFilePath = GetProfileFilePath(activeProfile);
        }

        // A character with no profile yet starts from defaults, like VTank. The
        // settings object lives for the whole client session, so without this a
        // new character kept the previous character's weapons and consumables,
        // and autosave then wrote them into its Default.json.
        bool freshProfile = !File.Exists(_settingsFilePath);
        if (freshProfile)
        {
            CopySettings(new LegacyUiSettings(), _settings);
            _lastSavedJson = string.Empty;
        }
        else
        {
            try
            {
                string json = File.ReadAllText(_settingsFilePath);
                var loaded = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.LegacyUiSettings);
                if (loaded != null)
                {
                    CopySettings(loaded, _settings);
                    _lastSavedJson = json;
                }
            }
            catch { }
        }
        _packSetup = freshProfile ? PackSetup.FillNew
                   : _settings.ProfileItemsChecked ? PackSetup.None : PackSetup.CheckExisting;
        _packSetupStart = 0;
        _packLastCount = -1;

        _settings.SelectedProfile = activeProfile;
        ApplyUiStateFromSettings();

        _settings.IsMacroRunning = _settings.StartMacroOnLogin;
        _settings.CurrentState   = "Default";
        _settings.BotAction      = "Default";

        // Reload nav route from saved path
        if (!string.IsNullOrEmpty(_settings.CurrentNavPath) && File.Exists(_settings.CurrentNavPath))
        {
            try
            {
                _settings.CurrentRoute = NavRouteParser.Load(_settings.CurrentNavPath);
                _settings.ActiveNavIndex =
                    (_settings.CurrentRoute.RouteType == NavRouteType.Follow ||
                     _settings.CurrentRoute.RouteType == NavRouteType.Once)
                        ? 0
                        : FindNearestWaypoint(_settings.CurrentRoute);
            }
            catch { _settings.CurrentNavPath = string.Empty; }
        }

        // Auto-reload embedded navs from the last loaded meta source so
        // EmbedNav rules resolve after a restart without a manual reload.
        if (_settings.EmbeddedNavs.Count == 0 &&
            !string.IsNullOrEmpty(_settings.CurrentMetaPath) &&
            File.Exists(_settings.CurrentMetaPath))
        {
            try
            {
                string ext = Path.GetExtension(_settings.CurrentMetaPath).ToLowerInvariant();
                LoadedMeta reload = ext == ".met"
                    ? MetFileParser.Load(_settings.CurrentMetaPath)
                    : ext == ".af"
                        ? AfFileParser.Load(_settings.CurrentMetaPath)
                        : new LoadedMeta();
                foreach (var kvp in reload.EmbeddedNavs)
                    _settings.EmbeddedNavs[kvp.Key] = kvp.Value;
            }
            catch { }
        }

        RefreshAllLists();

        // Load MonsterRules from monsters.json (overrides what was in the profile)
        // and migrate existing rules to that file if it doesn't exist yet.
        MigrateMonstersToFile();
        LoadMonstersFromFile();
        // Always re-insert the catch-all "Default" rule at index 0; without it the
        // combat system has no fallback weapon/damage selection for unmatched mobs.
        _settings.EnsureDefaultRule();
        SetupMonsterWatcher();
    }

    /// <summary>
    /// Logout: the settings in memory stop belonging to the character that just left.
    /// Nothing is saved until the next character's LoadSettings, and an empty CharFolder
    /// lets the plugin's late settings retry run if that character's name can't be read
    /// at login. Before, the next character ran (and autosaved over) this one's profile.
    /// </summary>
    public void ResetCharacterSession()
    {
        _charFolder       = string.Empty;
        _settingsFilePath = string.Empty;
        _lastSavedJson    = string.Empty;
        _settings.IsMacroRunning = false;
        _settings.CurrentState   = "Default";
        _settings.BotAction      = "Default";
    }

    public void SaveSettings()
    {
        if (string.IsNullOrEmpty(_settingsFilePath)) return;
        try
        {
            CaptureTransientUiState();
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsFilePath)!);
            string json = JsonSerializer.Serialize(_settings, RynthAiJsonContext.Default.LegacyUiSettings);
            File.WriteAllText(_settingsFilePath, json);
            _lastSavedJson = json;
            WriteActiveProfile(_settings.SelectedProfile);
        }
        catch { }
    }

    public string SaveAsProfile(string name)
    {
        if (string.IsNullOrEmpty(_charFolder)) return "Not logged in.";
        try
        {
            CaptureTransientUiState();
            Directory.CreateDirectory(_charFolder);
            string json = JsonSerializer.Serialize(_settings, RynthAiJsonContext.Default.LegacyUiSettings);
            string path = GetProfileFilePath(name);
            File.WriteAllText(path, json);
            _settings.SelectedProfile = name;
            _settingsFilePath = path;
            _lastSavedJson = json;
            WriteActiveProfile(name);
            RefreshProfilesList();
            return $"Saved profile '{name}'.";
        }
        catch (Exception ex) { return $"Save failed: {ex.Message}"; }
    }

    public string LoadProfile(string name)
    {
        if (string.IsNullOrEmpty(_charFolder)) return "Not logged in.";
        string path = GetProfileFilePath(name);
        if (!File.Exists(path))
            return SaveAsProfile(name);
        SwitchProfile(name);
        return $"Loaded profile '{name}'.";
    }

    /// <summary>
    /// Render-independent autosave entry, driven from RynthAiPlugin.OnTick so
    /// settings + the active-profile marker persist even when the in-AC ImGui
    /// shell is disabled (EnableImGuiShell=false) and Render() never runs.
    /// CheckAndSave self-throttles via a content hash, so this only touches disk
    /// when something actually changed.
    /// </summary>
    public void TickAutoSave() => CheckAndSave();

    private void CheckAndSave()
    {
        if (string.IsNullOrEmpty(_settingsFilePath)) return;
        try
        {
            CaptureTransientUiState();
            string json = JsonSerializer.Serialize(_settings, RynthAiJsonContext.Default.LegacyUiSettings);
            if (json != _lastSavedJson)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_settingsFilePath)!);
                File.WriteAllText(_settingsFilePath, json);
                _lastSavedJson = json;
                WriteActiveProfile(_settings.SelectedProfile);
            }
        }
        catch { }
    }

    // ── Profile items from the pack ──────────────────────────────────────────

    private enum PackSetup { None, FillNew, CheckExisting }
    private PackSetup _packSetup;
    private long _packSetupStart, _packStableSince, _packNextPoll;
    private int _packLastCount = -1;

    /// <summary>
    /// Once per login, after the inventory has stopped arriving: fills a new
    /// profile's weapons and consumables from the pack, or (once per older
    /// profile) removes entries for items this character doesn't have, copied
    /// from another character, filling from the pack if nothing is left.
    /// Driven from the plugin tick.
    /// </summary>
    public void TickPackSetup(WorldObjectCache? cache, uint playerId)
    {
        if (_packSetup == PackSetup.None || cache == null || playerId == 0) return;
        long now = Environment.TickCount64;
        if (now < _packNextPoll) return;
        _packNextPoll = now + 500;
        if (_packSetupStart == 0) _packSetupStart = now;

        // Owned items only: main pack, side packs and wielded gear. The cache's
        // general inventory set also holds ground items and open corpse contents.
        var inv = cache.GetDirectInventory(forceRefresh: true).ToList();
        if (inv.Count != _packLastCount) { _packLastCount = inv.Count; _packStableSince = now; return; }
        bool waited = now - _packSetupStart >= 5000 && now - _packStableSince >= 3000;
        bool unclassified = inv.Any(w => w.ObjectClass == AcObjectClass.Unknown);
        if (!waited || (unclassified && now - _packSetupStart < 30000)) return;
        if (inv.Count == 0 && now - _packSetupStart < 60000) return;

        string who = Path.GetFileName(_charFolder);
        if (_packSetup == PackSetup.FillNew)
        {
            var (w, c) = _weaponsUi.FillFromPack(inv, playerId);
            _host.WriteToChat($"[RynthAi] New {_settings.SelectedProfile} profile for {who}: added {w} weapon(s) and {c} consumable(s) from your pack.", 1);
        }
        else
        {
            var removed = _weaponsUi.PruneNotOwned(inv);
            if (removed.Count > 0)
                _host.WriteToChat($"[RynthAi] Removed {removed.Count} item(s) {who} doesn't have from the {_settings.SelectedProfile} profile (copied from another character): {string.Join(", ", removed)}", 1);
            if (_settings.ItemRules.Count == 0 && _settings.ConsumableRules.Count == 0)
            {
                var (w, c) = _weaponsUi.FillFromPack(inv, playerId);
                if (w + c > 0)
                    _host.WriteToChat($"[RynthAi] Filled the {_settings.SelectedProfile} profile from your pack: {w} weapon(s), {c} consumable(s).", 1);
            }
        }
        _settings.ProfileItemsChecked = true;
        _packSetup = PackSetup.None;
        SaveSettings();
    }

    /// <summary>/ra items fill: add anything in the pack that isn't listed yet.</summary>
    public string FillItemsFromPack(WorldObjectCache? cache, uint playerId)
    {
        if (cache == null || playerId == 0) return "[RynthAi] Not logged in.";
        var (w, c) = _weaponsUi.FillFromPack(cache.GetDirectInventory(forceRefresh: true).ToList(), playerId);
        SaveSettings();
        return $"[RynthAi] Added {w} weapon(s) and {c} consumable(s) from your pack.";
    }

    // ── Per-server profile folders ───────────────────────────────────────────

    private string ResolveServerFolder()
    {
        try
        {
            if (_host.HasGetWorldName && _host.TryGetWorldName(out string world) && !string.IsNullOrWhiteSpace(world))
            {
                string safe = SanitizeFileName(world.Trim());
                if (safe.Length > 0) return safe;
            }
        }
        catch { }
        return LegacyServerFolder;
    }

    /// <summary>
    /// First login of a character on a server after profiles went per server:
    /// copy (not move) its shared ACEmulator profile and monster rules into the
    /// server's folder, so it keeps its settings. A copy, because the same name
    /// on another server may still be using the shared one.
    /// </summary>
    private void MigrateToServerFolder(string safeChar)
    {
        if (_serverFolder == LegacyServerFolder) return;
        try
        {
            string legacyChar = Path.Combine(ProfilesBase, LegacyServerFolder, safeChar);
            if (!Directory.Exists(_charFolder) && Directory.Exists(legacyChar))
            {
                CopyDirectory(legacyChar, _charFolder);
                _host.Log($"[RynthAi] Profiles: copied {safeChar}'s profile from {LegacyServerFolder} to {_serverFolder}.");
                _host.WriteToChat($"[RynthAi] Settings are now kept per server: copied {safeChar}'s existing settings into the {_serverFolder} folder.", 1);
            }

            string legacyMonsters = Path.Combine(MonstersBase, safeChar + ".json");
            string serverMonsters = Path.Combine(_monstersFolder, safeChar + ".json");
            if (!File.Exists(serverMonsters) && File.Exists(legacyMonsters))
            {
                Directory.CreateDirectory(_monstersFolder);
                File.Copy(legacyMonsters, serverMonsters);
            }
        }
        catch (Exception ex)
        {
            _host.Log($"[RynthAi] Profiles: copying {safeChar} into {_serverFolder} failed: {ex.Message}");
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: false);
        foreach (string dir in Directory.GetDirectories(from))
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    // ── monsters.json support ────────────────────────────────────────────────

    private string MonstersFilePath
    {
        get
        {
            if (string.IsNullOrEmpty(_charFolder)) return string.Empty;
            string charKey = Path.GetFileName(_charFolder); // e.g. "Toon Name"
            return Path.Combine(_monstersFolder, charKey + ".json");
        }
    }

    private string MonstersFilePathLegacy => string.IsNullOrEmpty(_charFolder)
        ? string.Empty
        : Path.Combine(_charFolder, "monsters.json");

    /// <summary>
    /// Moves any existing monsters.json from the old per-char settings folder into
    /// the new MonsterProfiles folder, then seeds from in-memory rules if still absent.
    /// </summary>
    private void MigrateMonstersToFile()
    {
        string path = MonstersFilePath;
        if (string.IsNullOrEmpty(path)) return;

        // Move legacy file if present and destination doesn't exist yet
        string legacyPath = MonstersFilePathLegacy;
        if (!string.IsNullOrEmpty(legacyPath) && File.Exists(legacyPath) && !File.Exists(path))
        {
            try
            {
                Directory.CreateDirectory(_monstersFolder);
                File.Move(legacyPath, path);
                return; // moved; no need to seed from memory
            }
            catch { }
        }

        if (File.Exists(path)) return;
        if (_settings.MonsterRules.Count <= 1) return; // only Default, nothing to migrate

        try
        {
            Directory.CreateDirectory(_monstersFolder);
            string json = JsonSerializer.Serialize(_settings.MonsterRules, RynthAiJsonContext.Default.MonsterRuleList);
            File.WriteAllText(path, json);
        }
        catch { }
    }

    /// <summary>Loads MonsterRules from monsters.json, overriding what came from the profile.</summary>
    private void LoadMonstersFromFile()
    {
        string path = MonstersFilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try
        {
            string json  = File.ReadAllText(path);
            var    rules = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.MonsterRuleList);
            if (rules != null && rules.Count > 0)
                _settings.MonsterRules = rules;
        }
        catch { }
    }

    private void SetupMonsterWatcher()
    {
        _monsterWatcher?.Dispose();
        _monsterWatcher = null;

        string path = MonstersFilePath;
        if (string.IsNullOrEmpty(path) || !Directory.Exists(_monstersFolder)) return;

        try
        {
            string charKey = Path.GetFileName(_charFolder);
            _monsterWatcher = new FileSystemWatcher(_monstersFolder, charKey + ".json")
            {
                NotifyFilter       = NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            _monsterWatcher.Changed += (_, _) => _monsterFileChanged = true;
        }
        catch { }
    }

    /// <summary>
    /// Called every plugin tick (RynthAiPlugin.OnTick, pump thread). Hot-reloads MonsterRules when the external editor saves.
    /// </summary>
    public void TickMonsterReload()
    {
        if (!_monsterFileChanged) return;
        _monsterFileChanged = false;
        LoadMonstersFromFile();
    }

    /// <summary>Saves the Monsters rules after a change made outside the panel (a chat command).</summary>
    public void SaveMonsterRules() => SaveMonstersFile();

    /// <summary>Writes the in-memory MonsterRules to monsters.json so the external editor sees them.</summary>
    private void SaveMonstersFile()
    {
        string path = MonstersFilePath;
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            // Suspend the watcher so our own write doesn't cause a redundant reload.
            bool wasEnabled = false;
            if (_monsterWatcher != null)
            {
                wasEnabled = _monsterWatcher.EnableRaisingEvents;
                _monsterWatcher.EnableRaisingEvents = false;
            }

            Directory.CreateDirectory(_monstersFolder);
            string json = JsonSerializer.Serialize(_settings.MonsterRules, RynthAiJsonContext.Default.MonsterRuleList);
            File.WriteAllText(path, json);

            if (_monsterWatcher != null)
                _monsterWatcher.EnableRaisingEvents = wasEnabled;
        }
        catch { }
    }

    // ── Radar bridge (used by the engine-side Avalonia radar panel) ─────────
    /// <summary>
    /// Builds a JSON snapshot for the Avalonia radar. Pass the engine's
    /// currently-cached MapVersion (0 on first call); when it matches the live
    /// landblock, walls/fills are omitted to keep the payload small.
    /// </summary>
    public string BuildRadarJson(uint engineKnownMapVersion)
        => _rynthRadarUi.BuildSnapshotJson(engineKnownMapVersion);

    // ── Monsters bridge (used by the engine-side Avalonia MonstersPanel) ────
    /// <summary>
    /// Builds a JSON payload describing the current MonsterRules + ItemRules
    /// (for weapon/offhand picker) + currently-selected target name (for the
    /// "Add Selected" button). Polled by the Avalonia panel.
    /// </summary>
    public string BuildMonstersJson()
    {
        try
        {
            var payload = new MonstersBridgePayload
            {
                Rules = _settings.MonsterRules ?? new List<MonsterRule>(),
                // Full names with the element ("Silver Wand (Fire)"); saved choices are by id.
                Items = (_settings.ItemRules ?? new List<ItemRule>())
                    .Select(r => new MonsterBridgeItem { Id = r.Id, Name = WeaponDisplayName(r, withElement: true) }).ToList(),
                CurrentTargetName = _currentTargetId != 0 ? (_targetLabel ?? string.Empty) : string.Empty,
            };

            // Annotate each rule with captured creature data when available.
            var store = CreatureLookupForRules;
            if (store != null && payload.Rules.Count > 0)
            {
                foreach (var rule in payload.Rules)
                {
                    if (string.IsNullOrEmpty(rule.Name)) continue;
                    var profile = store(rule.Name);
                    if (profile == null) continue;
                    var (weakType, weakVal) = CreatureData.CreatureProfileStore.GetWeakest(profile);
                    payload.Captured[rule.Name] = new MonsterCapturedInfo
                    {
                        MaxHealth   = profile.MaxHealth,
                        ArmorLevel  = profile.ArmorLevel,
                        WeakestType = weakType,
                        WeakestValue = weakVal,
                        Samples     = profile.Samples,
                    };
                }
            }
            return JsonSerializer.Serialize(payload, RynthAiJsonContext.Default.MonstersBridgePayload);
        }
        catch
        {
            return "{\"rules\":[],\"items\":[],\"currentTargetName\":\"\",\"captured\":{}}";
        }
    }

    /// <summary>Selectable weapons for the Damage-panel weapon/offhand pickers, as a JSON array
    /// [{"id":..,"name":..}] from _settings.ItemRules — the same configured-weapons source the
    /// Monsters tab's picker uses. Manual JSON (NativeAOT-trivial, no extra JsonContext type).</summary>
    public string BuildCombatWeaponsJson()
    {
        try
        {
            var items = _settings.ItemRules ?? new List<ItemRule>();
            var sb = new System.Text.StringBuilder();
            sb.Append('[');
            bool first = true;
            foreach (var it in items)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"id\":").Append(it.Id).Append(",\"name\":").Append(JsonString(WeaponDisplayName(it, withElement: true))).Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }
        catch { return "[]"; }
    }

    private static string JsonString(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "\"\"";
        var sb = new System.Text.StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>Set by RynthAiPlugin so the snapshot can decorate rules with captured profile data.</summary>
    public Func<string, CreatureData.CreatureProfile?>? CreatureLookupForRules { get; set; }

    /// <summary>
    /// Replaces the in-memory MonsterRules from JSON sent by the Avalonia panel
    /// and saves to monsters.json. JSON shape: { "rules": [ MonsterRule, ... ] }.
    /// Default rule (Name="Default") is always preserved at index 0 — if absent,
    /// the existing one is kept; if present, it overrides.
    /// </summary>
    public void ApplyMonstersJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var payload = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.MonstersBridgePayload);
            if (payload?.Rules == null) return;

            // Make sure a Default rule survives — every other code path assumes
            // it exists (CombatManager fallback at LegacyUiSettings:322).
            var incoming = payload.Rules;
            bool hasDefault = incoming.Any(r => r.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));
            if (!hasDefault)
            {
                var existingDefault = _settings.MonsterRules
                    .FirstOrDefault(r => r.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));
                // Synthesize one if neither incoming nor existing has a Default — combat's
                // GetRuleForTarget fallback assumes it always exists, so never leave it absent.
                incoming.Insert(0, existingDefault ?? new MonsterRule { Name = "Default" });
            }

            _settings.MonsterRules = incoming;
            SaveMonstersFile();
        }
        catch { }
    }

    // ── Settings bridge (engine-side Avalonia SettingsPanel) ─────────────────

    public string BuildSettingsJson()
    {
        try
        {
            var s = _settings;
            var payload = new SettingsBridgePayload
            {
                // Display
                ShowTargetStaminaMana      = s.ShowTargetStaminaMana,
                // UI
                SuppressRetailRadar        = s.SuppressRetailRadar,
                ShowRynthRadar             = s.ShowRynthRadar,
                RadarClickThrough          = s.RadarClickThrough,
                ShowRynthChat              = s.ShowRynthChat,
                ChatClickThrough           = s.ChatClickThrough,
                SuppressRetailPowerbar     = s.SuppressRetailPowerbar,
                // Misc
                EnableFPSLimit             = s.EnableFPSLimit,
                TargetFPSFocused           = s.TargetFPSFocused,
                TargetFPSBackground        = s.TargetFPSBackground,
                EnableAutocram             = s.EnableAutocram,
                PeaceModeWhenIdle          = s.PeaceModeWhenIdle,
                StartMacroOnLogin          = s.StartMacroOnLogin,
                PatrolOnLogin              = s.PatrolOnLogin,
                YieldToVTank               = s.YieldToVTank,
                FeatureServerNames         = IltHub.ServerFeatureGate.FormatList(s.FeatureServerNames),
                ForceServerFeatures        = s.ForceServerFeatures,
                ServerFeaturesStatus       = IltHub.ServerFeatureGate.Describe(s),
                EnableRaycasting           = s.EnableRaycasting,
                UseArcs                    = s.UseArcs,
                BowArcVelocity             = s.BowArcVelocity,
                CrossbowArcVelocity        = s.CrossbowArcVelocity,
                AtlatlArcVelocity          = s.AtlatlArcVelocity,
                MagicArcVelocity           = s.MagicArcVelocity,
                MissileArcClearance        = s.MissileArcClearance,
                LosDebugLog                = s.LosDebugLog,
                BlacklistAttempts          = s.BlacklistAttempts,
                BlacklistTimeoutSec        = s.BlacklistTimeoutSec,
                BlacklistCastSettleMs      = s.BlacklistCastSettleMs,
                TargetNoProgressTimeoutSec = s.TargetNoProgressTimeoutSec,
                GiveQueueIntervalMs        = s.GiveQueueIntervalMs,
                // Recharge
                HealAt                     = s.HealAt,
                UsePotions                 = s.UsePotions,
                UseBuffItems               = s.UseBuffItems,
                MakeRationsBelow           = s.MakeRationsBelow,
                UseKitsInMagicMode         = s.UseKitsInMagicMode,
                PeaceModeForKits           = s.PeaceModeForKits,
                KitMinSuccessPct           = s.KitMinSuccessPct,
                EmergencyHealAt            = s.EmergencyHealAt,
                StaminaToHealthAt          = s.StaminaToHealthAt,
                StaminaToHealthMinStamina  = s.StaminaToHealthMinStamina,
                StopMacroOnDeath           = s.StopMacroOnDeath,
                StopMacroOnNoComponents    = s.StopMacroOnNoComponents,
                StopLootingWhenPackFull    = s.StopLootingWhenPackFull,
                StopMacroWhenPackFull      = s.StopMacroWhenPackFull,
                RestamAt                   = s.RestamAt,
                GetManaAt                  = s.GetManaAt,
                TopOffHP                   = s.TopOffHP,
                TopOffStam                 = s.TopOffStam,
                TopOffMana                 = s.TopOffMana,
                HealOthersAt               = s.HealOthersAt,
                RestamOthersAt             = s.RestamOthersAt,
                InfuseOthersAt             = s.InfuseOthersAt,
                // Melee Combat
                UseRecklessness            = s.UseRecklessness,
                MeleeAttackPower           = s.MeleeAttackPower,
                MeleeAttackHeight          = s.MeleeAttackHeight,
                MissileAttackPower         = s.MissileAttackPower,
                MissileAttackHeight        = s.MissileAttackHeight,
                UseNativeAttack            = s.UseNativeAttack,
                SummonPets                 = s.SummonPets,
                PetMinMonsters             = s.PetMinMonsters,
                // Spell Combat
                SpellCastIntervalMs        = s.SpellCastIntervalMs,
                AttackSpellIntervalMs      = s.AttackSpellIntervalMs,
                CastDispelSelf             = s.CastDispelSelf,
                MinRingTargets             = s.MinRingTargets,
                BlastRange                 = s.BlastRange,
                MinBlastTargets            = s.MinBlastTargets,
                MinSkillLevelTier1         = s.MinSkillLevelTier1,
                MinSkillLevelTier2         = s.MinSkillLevelTier2,
                MinSkillLevelTier3         = s.MinSkillLevelTier3,
                MinSkillLevelTier4         = s.MinSkillLevelTier4,
                MinSkillLevelTier5         = s.MinSkillLevelTier5,
                MinSkillLevelTier6         = s.MinSkillLevelTier6,
                MinSkillLevelTier7         = s.MinSkillLevelTier7,
                MinSkillLevelTier8         = s.MinSkillLevelTier8,
                // Ranges
                MonsterRange               = s.MonsterRange,
                MonsterDisengageRange      = s.MonsterDisengageRange,
                RingRange                  = s.RingRange,
                ApproachRange              = s.ApproachRange,
                CorpseApproachRangeMax     = s.CorpseApproachRangeMax,
                CorpseApproachRangeMin     = s.CorpseApproachRangeMin,
                // Navigation
                BoostNavPriority           = s.BoostNavPriority,
                FollowNavMin               = s.FollowNavMin,
                NavRingThickness           = s.NavRingThickness,
                NavLineThickness           = s.NavLineThickness,
                NavHeightOffset            = s.NavHeightOffset,
                NavSlopeSink               = s.NavSlopeSink,
                ShowTerrainPassability     = s.ShowTerrainPassability,
                OpenDoors                  = s.OpenDoors,
                OpenDoorRange              = s.OpenDoorRange,
                AutoUnlockDoors            = s.AutoUnlockDoors,
                MovementMode               = s.MovementMode,
                NavStopTurnAngle           = s.NavStopTurnAngle,
                NavResumeTurnAngle         = s.NavResumeTurnAngle,
                NavDeadZone                = s.NavDeadZone,
                NavSweepMult               = s.NavSweepMult,
                NavLookaheadYards          = s.NavLookaheadYards,
                NavShortcutYards           = s.NavShortcutYards,
                NavTurnRateDegPerSec       = s.NavTurnRateDegPerSec,
                NavTier1TurnSpeed          = s.NavTier1TurnSpeed,
                PostPortalDelaySec         = s.PostPortalDelaySec,
                T2Speed                    = s.T2Speed,
                T2WalkWithinYd             = s.T2WalkWithinYd,
                T2DistanceTo               = s.T2DistanceTo,
                T2ReissueMs                = s.T2ReissueMs,
                T2MaxRangeYd               = s.T2MaxRangeYd,
                T2MaxLandblocks            = s.T2MaxLandblocks,
                // Buffing
                EnableBuffing              = s.EnableBuffing,
                RebuffWhenIdle             = s.RebuffWhenIdle,
                RebuffSecondsRemaining     = s.RebuffSecondsRemaining,
                RebuffTopOffSecondsRemaining = s.RebuffTopOffSecondsRemaining,
                CastBuffsOverItemBuffs     = s.CastBuffsOverItemBuffs,
                BuffMinSkillLevelTier1     = s.BuffMinSkillLevelTier1,
                BuffMinSkillLevelTier2     = s.BuffMinSkillLevelTier2,
                BuffMinSkillLevelTier3     = s.BuffMinSkillLevelTier3,
                BuffMinSkillLevelTier4     = s.BuffMinSkillLevelTier4,
                BuffMinSkillLevelTier5     = s.BuffMinSkillLevelTier5,
                BuffMinSkillLevelTier6     = s.BuffMinSkillLevelTier6,
                BuffMinSkillLevelTier7     = s.BuffMinSkillLevelTier7,
                BuffMinSkillLevelTier8     = s.BuffMinSkillLevelTier8,
                // Crafting
                EnableMissileCrafting      = s.EnableMissileCrafting,
                MissileCraftingState       = _advancedSettingsUi.MissileCraftingState,
                MissileCraftingActive      = _advancedSettingsUi.MissileCraftingActive,
                MissileCraftingStatus      = _advancedSettingsUi.MissileCraftingStatus,
                // Looting
                EnableLooting              = s.EnableLooting,
                BoostLootPriority          = s.BoostLootPriority,
                LootOnlyRareCorpses        = s.LootOnlyRareCorpses,
                LootJumpEnabled            = s.LootJumpEnabled,
                LootJumpHeight             = s.LootJumpHeight,
                LootOwnership              = s.LootOwnership,
                LootOwnCorpse              = s.LootOwnCorpse,
                TravelToOwnCorpse          = s.TravelToOwnCorpse,
                EnableAutostack            = s.EnableAutostack,
                ReadUnknownScrolls         = s.ReadUnknownScrolls,
                EnableCombineSalvage       = s.EnableCombineSalvage,
                CombineBagsDuringSalvage   = s.CombineBagsDuringSalvage,
                LootInterItemDelayMs       = s.LootInterItemDelayMs,
                LootContentSettleMs        = s.LootContentSettleMs,
                LootEmptyCorpseMs          = s.LootEmptyCorpseMs,
                LootClosingDelayMs         = s.LootClosingDelayMs,
                LootAssessWindowMs         = s.LootAssessWindowMs,
                LootRetryTimeoutMs         = s.LootRetryTimeoutMs,
                LootOpenRetryMs            = s.LootOpenRetryMs,
                LootCorpseTimeoutMs        = s.LootCorpseTimeoutMs,
                SalvageOpenDelayFirstMs    = s.SalvageOpenDelayFirstMs,
                SalvageOpenDelayFastMs     = s.SalvageOpenDelayFastMs,
                SalvageAddDelayFirstMs     = s.SalvageAddDelayFirstMs,
                SalvageAddDelayFastMs      = s.SalvageAddDelayFastMs,
                SalvageSalvageDelayMs      = s.SalvageSalvageDelayMs,
                SalvageResultDelayFirstMs  = s.SalvageResultDelayFirstMs,
                SalvageResultDelayFastMs   = s.SalvageResultDelayFastMs,
                // Vendoring (AutoVendor)
                AutoVendorEnabled          = s.AutoVendorEnabled,
                AutoVendorEnableBuying     = s.AutoVendorEnableBuying,
                AutoVendorEnableSelling    = s.AutoVendorEnableSelling,
                AutoVendorTestMode         = s.AutoVendorTestMode,
                AutoVendorThink            = s.AutoVendorThink,
                AutoVendorShowMerchantInfo = s.AutoVendorShowMerchantInfo,
                AutoVendorOnlyFromMainPack = s.AutoVendorOnlyFromMainPack,
                AutoVendorTries            = s.AutoVendorTries,
                AutoVendorTriesTime        = s.AutoVendorTriesTime,
                OffhandDefault             = s.OffhandDefault,
                PreferDualWield            = s.PreferDualWield,
                EnableGroundLoot           = s.EnableGroundLoot,
                ItemInfoOnSelect           = s.ItemInfoSettings.OnSelect,
                // Diagnostics (per PC, RynthLog)
                DiagDebugToChat            = RynthLog.DebugToChat,
                DiagFileLogAll             = RynthLog.FileLogAll,
                DiagCategories             = RynthLog.FormatCategoryLevels(),
                DiagFolder                 = RynthLog.Directory,
            };
            return JsonSerializer.Serialize(payload, RynthAiJsonContext.Default.SettingsBridgePayload);
        }
        catch
        {
            return "{}";
        }
    }

    public void ApplySettingsJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            // Lay the sent fields over the current settings, so a field the sender leaves
            // out keeps its value instead of deserializing to 0 and being applied. The
            // overlay's Settings panel keeps its own copy of this payload; when that copy
            // lacked BlacklistCastSettleMs/MonsterDisengageRange, every click zeroed them.
            var merged = System.Text.Json.Nodes.JsonNode.Parse(BuildSettingsJson(),
                new System.Text.Json.Nodes.JsonNodeOptions { PropertyNameCaseInsensitive = true })?.AsObject();
            if (merged == null) return;
            using (var sent = JsonDocument.Parse(json))
            {
                if (sent.RootElement.ValueKind != JsonValueKind.Object) return;
                foreach (var prop in sent.RootElement.EnumerateObject())
                    merged[prop.Name] = System.Text.Json.Nodes.JsonNode.Parse(prop.Value.GetRawText());
            }
            var p = JsonSerializer.Deserialize(merged.ToJsonString(), RynthAiJsonContext.Default.SettingsBridgePayload);
            if (p == null) return;
            var s = _settings;
            // Display
            s.ShowTargetStaminaMana      = p.ShowTargetStaminaMana;
            // UI
            s.SuppressRetailRadar        = p.SuppressRetailRadar;
            s.ShowRynthRadar             = p.ShowRynthRadar;
            s.RadarClickThrough          = p.RadarClickThrough;
            s.ShowRynthChat              = p.ShowRynthChat;
            s.ChatClickThrough           = p.ChatClickThrough;
            s.SuppressRetailPowerbar     = p.SuppressRetailPowerbar;
            // Misc
            s.EnableFPSLimit             = p.EnableFPSLimit;
            s.TargetFPSFocused           = p.TargetFPSFocused;
            s.TargetFPSBackground        = p.TargetFPSBackground;
            s.EnableAutocram             = p.EnableAutocram;
            s.PeaceModeWhenIdle          = p.PeaceModeWhenIdle;
            s.StartMacroOnLogin          = p.StartMacroOnLogin;
            s.PatrolOnLogin              = p.PatrolOnLogin;
            s.YieldToVTank               = p.YieldToVTank;
            if (p.FeatureServerNames != null)
                s.FeatureServerNames     = IltHub.ServerFeatureGate.ParseList(p.FeatureServerNames);
            s.ForceServerFeatures        = p.ForceServerFeatures;
            s.EnableRaycasting           = p.EnableRaycasting;
            s.UseArcs                    = p.UseArcs;
            s.BowArcVelocity             = p.BowArcVelocity;
            s.CrossbowArcVelocity        = p.CrossbowArcVelocity;
            s.AtlatlArcVelocity          = p.AtlatlArcVelocity;
            if (p.MagicArcVelocity > 0f) s.MagicArcVelocity = p.MagicArcVelocity;
            if (p.MissileArcClearance >= 0f) s.MissileArcClearance = Math.Min(p.MissileArcClearance, 3f);
            s.LosDebugLog                = p.LosDebugLog;
            s.BlacklistAttempts          = p.BlacklistAttempts;
            s.BlacklistTimeoutSec        = p.BlacklistTimeoutSec;
            // -1 = not sent. 0 is never meant (it judges a cast before its damage can land)
            // and is what the bug saved into profiles, so it keeps the default too.
            if (p.BlacklistCastSettleMs > 0) s.BlacklistCastSettleMs = p.BlacklistCastSettleMs;
            s.TargetNoProgressTimeoutSec = p.TargetNoProgressTimeoutSec;
            s.GiveQueueIntervalMs        = p.GiveQueueIntervalMs;
            // Recharge
            s.HealAt                     = p.HealAt;
            s.UsePotions                 = p.UsePotions;
            s.UseBuffItems               = p.UseBuffItems;
            s.MakeRationsBelow           = Math.Clamp(p.MakeRationsBelow, 0, 100);
            s.UseKitsInMagicMode         = p.UseKitsInMagicMode;
            s.PeaceModeForKits           = p.PeaceModeForKits;
            s.KitMinSuccessPct           = Math.Clamp(p.KitMinSuccessPct, 0, 100);
            s.EmergencyHealAt            = Math.Clamp(p.EmergencyHealAt, 0, 100);
            s.StaminaToHealthAt          = Math.Clamp(p.StaminaToHealthAt, 0, 100);
            s.StaminaToHealthMinStamina  = Math.Clamp(p.StaminaToHealthMinStamina, 0, 100);
            s.StopMacroOnDeath           = p.StopMacroOnDeath;
            s.StopMacroOnNoComponents    = p.StopMacroOnNoComponents;
            s.StopLootingWhenPackFull    = p.StopLootingWhenPackFull;
            s.StopMacroWhenPackFull      = p.StopMacroWhenPackFull;
            s.RestamAt                   = p.RestamAt;
            s.GetManaAt                  = p.GetManaAt;
            s.TopOffHP                   = p.TopOffHP;
            s.TopOffStam                 = p.TopOffStam;
            s.TopOffMana                 = p.TopOffMana;
            s.HealOthersAt               = p.HealOthersAt;
            s.RestamOthersAt             = p.RestamOthersAt;
            s.InfuseOthersAt             = p.InfuseOthersAt;
            // Melee Combat
            s.UseRecklessness            = p.UseRecklessness;
            s.MeleeAttackPower           = p.MeleeAttackPower;
            s.MeleeAttackHeight          = p.MeleeAttackHeight;
            s.MissileAttackPower         = p.MissileAttackPower;
            s.MissileAttackHeight        = p.MissileAttackHeight;
            s.UseNativeAttack            = p.UseNativeAttack;
            s.SummonPets                 = p.SummonPets;
            s.PetMinMonsters             = p.PetMinMonsters;
            // Spell Combat
            s.SpellCastIntervalMs        = p.SpellCastIntervalMs;
            s.AttackSpellIntervalMs      = p.AttackSpellIntervalMs;
            s.CastDispelSelf             = p.CastDispelSelf;
            s.MinRingTargets             = p.MinRingTargets;
            s.BlastRange                 = p.BlastRange;
            s.MinBlastTargets            = p.MinBlastTargets;
            s.MinSkillLevelTier1         = p.MinSkillLevelTier1;
            s.MinSkillLevelTier2         = p.MinSkillLevelTier2;
            s.MinSkillLevelTier3         = p.MinSkillLevelTier3;
            s.MinSkillLevelTier4         = p.MinSkillLevelTier4;
            s.MinSkillLevelTier5         = p.MinSkillLevelTier5;
            s.MinSkillLevelTier6         = p.MinSkillLevelTier6;
            s.MinSkillLevelTier7         = p.MinSkillLevelTier7;
            s.MinSkillLevelTier8         = p.MinSkillLevelTier8;
            // Ranges
            s.MonsterRange               = p.MonsterRange;
            if (p.MonsterDisengageRange >= 0) s.MonsterDisengageRange = p.MonsterDisengageRange;   // -1 = not sent
            s.RingRange                  = p.RingRange;
            s.ApproachRange              = p.ApproachRange;
            s.CorpseApproachRangeMax     = p.CorpseApproachRangeMax;
            s.CorpseApproachRangeMin     = p.CorpseApproachRangeMin;
            // Navigation
            s.BoostNavPriority           = p.BoostNavPriority;
            s.FollowNavMin               = p.FollowNavMin;
            s.NavRingThickness           = p.NavRingThickness;
            s.NavLineThickness           = p.NavLineThickness;
            s.NavHeightOffset            = p.NavHeightOffset;
            s.NavSlopeSink               = p.NavSlopeSink;
            s.ShowTerrainPassability     = p.ShowTerrainPassability;
            s.OpenDoors                  = p.OpenDoors;
            s.OpenDoorRange              = p.OpenDoorRange;
            s.AutoUnlockDoors            = p.AutoUnlockDoors;
            s.MovementMode               = p.MovementMode is 1 or 2 ? 0 : p.MovementMode;   // Tier 1/2 off for now
            s.NavStopTurnAngle           = p.NavStopTurnAngle;
            s.NavResumeTurnAngle         = p.NavResumeTurnAngle;
            s.NavDeadZone                = p.NavDeadZone;
            s.NavSweepMult               = p.NavSweepMult;
            s.NavLookaheadYards          = p.NavLookaheadYards;
            s.NavShortcutYards           = p.NavShortcutYards;
            s.NavTurnRateDegPerSec       = p.NavTurnRateDegPerSec;
            s.NavTier1TurnSpeed          = p.NavTier1TurnSpeed;
            s.PostPortalDelaySec         = p.PostPortalDelaySec;
            s.T2Speed                    = p.T2Speed;
            s.T2WalkWithinYd             = p.T2WalkWithinYd;
            s.T2DistanceTo               = p.T2DistanceTo;
            s.T2ReissueMs                = p.T2ReissueMs;
            s.T2MaxRangeYd               = p.T2MaxRangeYd;
            s.T2MaxLandblocks            = p.T2MaxLandblocks;
            // Buffing
            s.EnableBuffing              = p.EnableBuffing;
            s.RebuffWhenIdle             = p.RebuffWhenIdle;
            s.RebuffSecondsRemaining     = p.RebuffSecondsRemaining;
            if (p.RebuffTopOffSecondsRemaining > 0) s.RebuffTopOffSecondsRemaining = p.RebuffTopOffSecondsRemaining;
            if (p.CastBuffsOverItemBuffs is bool castOverItems) s.CastBuffsOverItemBuffs = castOverItems;   // absent = unchanged
            s.BuffMinSkillLevelTier1     = p.BuffMinSkillLevelTier1;
            s.BuffMinSkillLevelTier2     = p.BuffMinSkillLevelTier2;
            s.BuffMinSkillLevelTier3     = p.BuffMinSkillLevelTier3;
            s.BuffMinSkillLevelTier4     = p.BuffMinSkillLevelTier4;
            s.BuffMinSkillLevelTier5     = p.BuffMinSkillLevelTier5;
            s.BuffMinSkillLevelTier6     = p.BuffMinSkillLevelTier6;
            s.BuffMinSkillLevelTier7     = p.BuffMinSkillLevelTier7;
            s.BuffMinSkillLevelTier8     = p.BuffMinSkillLevelTier8;
            // Crafting (EnableMissileCrafting is writable; state fields are read-only)
            s.EnableMissileCrafting      = p.EnableMissileCrafting;
            // Looting
            s.EnableLooting              = p.EnableLooting;
            s.BoostLootPriority          = p.BoostLootPriority;
            s.LootOnlyRareCorpses        = p.LootOnlyRareCorpses;
            s.LootJumpEnabled            = p.LootJumpEnabled;
            s.LootJumpHeight             = p.LootJumpHeight;
            s.LootOwnership              = p.LootOwnership;
            if (p.LootOwnCorpse is bool lootOwn) s.LootOwnCorpse = lootOwn;               // absent = unchanged
            if (p.TravelToOwnCorpse is bool travelOwn) s.TravelToOwnCorpse = travelOwn;   // absent = unchanged
            s.EnableAutostack            = p.EnableAutostack;
            s.ReadUnknownScrolls         = p.ReadUnknownScrolls;
            s.EnableCombineSalvage       = p.EnableCombineSalvage;
            s.CombineBagsDuringSalvage   = p.CombineBagsDuringSalvage;
            s.LootInterItemDelayMs       = p.LootInterItemDelayMs;
            s.LootContentSettleMs        = p.LootContentSettleMs;
            s.LootEmptyCorpseMs          = p.LootEmptyCorpseMs;
            s.LootClosingDelayMs         = p.LootClosingDelayMs;
            s.LootAssessWindowMs         = p.LootAssessWindowMs;
            s.LootRetryTimeoutMs         = p.LootRetryTimeoutMs;
            s.LootOpenRetryMs            = p.LootOpenRetryMs;
            s.LootCorpseTimeoutMs        = p.LootCorpseTimeoutMs;
            s.SalvageOpenDelayFirstMs    = p.SalvageOpenDelayFirstMs;
            s.SalvageOpenDelayFastMs     = p.SalvageOpenDelayFastMs;
            s.SalvageAddDelayFirstMs     = p.SalvageAddDelayFirstMs;
            s.SalvageAddDelayFastMs      = p.SalvageAddDelayFastMs;
            s.SalvageSalvageDelayMs      = p.SalvageSalvageDelayMs;
            s.SalvageResultDelayFirstMs  = p.SalvageResultDelayFirstMs;
            s.SalvageResultDelayFastMs   = p.SalvageResultDelayFastMs;
            // Vendoring: only fields the sender actually included (older panels omit them)
            if (p.AutoVendorEnabled          is bool avOn)    s.AutoVendorEnabled          = avOn;
            if (p.AutoVendorEnableBuying     is bool avBuy)   s.AutoVendorEnableBuying     = avBuy;
            if (p.AutoVendorEnableSelling    is bool avSell)  s.AutoVendorEnableSelling    = avSell;
            if (p.AutoVendorTestMode         is bool avTest)  s.AutoVendorTestMode         = avTest;
            if (p.AutoVendorThink            is bool avThink) s.AutoVendorThink            = avThink;
            if (p.AutoVendorShowMerchantInfo is bool avInfo)  s.AutoVendorShowMerchantInfo = avInfo;
            if (p.AutoVendorOnlyFromMainPack is bool avMain)  s.AutoVendorOnlyFromMainPack = avMain;
            if (p.AutoVendorTries            is int avTries)  s.AutoVendorTries            = Math.Clamp(avTries, 1, 20);
            if (p.AutoVendorTriesTime        is int avTime)   s.AutoVendorTriesTime        = Math.Clamp(avTime, 500, 30000);
            // Off hand: only fields the sender actually included (the engine face doesn't draw them yet)
            if (p.OffhandDefault is string od && OffhandRules.TryParse(od, out var odMode)) s.OffhandDefault = OffhandRules.SettingValue(odMode);
            if (p.PreferDualWield            is bool pdw)     s.PreferDualWield            = pdw;
            s.EnableGroundLoot           = p.EnableGroundLoot;
            s.ItemInfoSettings.OnSelect  = p.ItemInfoOnSelect;
            // Diagnostics: each setter rewrites diagnostics.json, so only touch what changed.
            if (p.DiagDebugToChat != RynthLog.DebugToChat) RynthLog.DebugToChat = p.DiagDebugToChat;
            if (p.DiagFileLogAll  != RynthLog.FileLogAll)  RynthLog.FileLogAll  = p.DiagFileLogAll;
            RynthLog.ApplyCategoryLevels(p.DiagCategories);
            SaveSettings();
        }
        catch { }
    }

    // ── Items bridge (engine-side Avalonia ItemsPanel) ────────────────────────

    public string BuildItemsJson()
    {
        try
        {
            var payload = new ItemsBridgePayload
            {
                // Copies: the full name, and "Unknown" for an element not known yet (ApplyItemsJson maps them back by id).
                Weapons           = WeaponList.ForDisplay(_settings.ItemRules ?? new List<ItemRule>(), r => WeaponDisplayName(r, withElement: false)),
                Consumables       = _settings.ConsumableRules ?? new List<ConsumableRule>(),
                EnableManaTapping = _settings.EnableManaTapping,
                ManaTapMinMana    = _settings.ManaTapMinMana,
                ManaStoneKeepCount = _settings.ManaStoneKeepCount,
                CurrentTargetName = _currentTargetId != 0 ? (_targetLabel ?? string.Empty) : string.Empty,
            };
            return JsonSerializer.Serialize(payload, RynthAiJsonContext.Default.ItemsBridgePayload);
        }
        catch
        {
            return "{}";
        }
    }

    public void ApplyItemsJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var p = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.ItemsBridgePayload);
            if (p == null) return;
            _settings.ItemRules          = WeaponList.MergeEdited(_settings.ItemRules ?? new List<ItemRule>(), p.Weapons ?? new List<ItemRule>());
            _settings.ConsumableRules    = p.Consumables;
            _settings.EnableManaTapping  = p.EnableManaTapping;
            _settings.ManaTapMinMana     = p.ManaTapMinMana;
            _settings.ManaStoneKeepCount = p.ManaStoneKeepCount;
            SaveSettings();
        }
        catch { }
    }

    public void AddSelectedWeapon()     { _weaponsUi.AddSelectedWeapon();     SaveSettings(); }
    public void AddSelectedConsumable() { _weaponsUi.AddSelectedConsumable(); SaveSettings(); }

    /// <summary>Launches the standalone Monster Rules editor for the current char folder.</summary>
    /// <remarks>No button calls this since the plugin-drawn Monsters window went: the
    /// engine's MonstersFace has no "external editor" button yet. Kept as the handler for
    /// one. Saves the editor makes are picked up by TickMonsterReload.</remarks>
    private void LaunchMonsterEditor()
    {
        if (string.IsNullOrEmpty(_charFolder))
        {
            _host.WriteToChat("[RynthAi] No character loaded — cannot open Monster Editor.", 4);
            return;
        }

        // Toggle: if the editor is already running, close it.
        if (_monsterEditorProcessHandle != IntPtr.Zero)
        {
            uint wait = WaitForSingleObject(_monsterEditorProcessHandle, 0);
            if (wait == WaitTimeout) // still running
            {
                IntPtr hwnd = FindMainWindowForPid(_monsterEditorPid);
                if (hwnd != IntPtr.Zero)
                    PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
                else
                    TerminateProcess(_monsterEditorProcessHandle, 0); // no window found — fall back to a hard kill
            }
            ReleaseMonsterEditorHandle();
            return;
        }

        // Editor lives at: <RynthAi root>\MonsterEditor\RynthCore.MonsterEditor.exe
        string rynthAiRoot = Path.GetDirectoryName(Path.GetDirectoryName(_settingsRoot)!)!;
        string editorExe   = Path.Combine(rynthAiRoot, "MonsterEditor", "RynthCore.MonsterEditor.exe");

        if (!File.Exists(editorExe))
        {
            _host.WriteToChat($"[RynthAi] Monster Editor not found: {editorExe}", 4);
            return;
        }

        var info = new SHELLEXECUTEINFOW
        {
            cbSize       = Marshal.SizeOf<SHELLEXECUTEINFOW>(),
            fMask        = SeeMaskNoCloseProcess,   // retain hProcess instead of closing it internally
            lpVerb       = "open",
            lpFile       = editorExe,
            lpParameters = $"\"{_charFolder}\"",
            nShow        = SwShowNormal,
        };

        if (!ShellExecuteExW(ref info) || info.hProcess == IntPtr.Zero)
        {
            _host.WriteToChat("[RynthAi] Failed to launch Monster Editor.", 4);
            return;
        }

        _monsterEditorProcessHandle = info.hProcess;
        _monsterEditorPid = (int)GetProcessId(info.hProcess); // needed to find the main window for WM_CLOSE later
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(IntPtr hProcess);

    private void CaptureTransientUiState()
    {
        _settings.WindowLocked       = _isLocked;
        _settings.DashboardMinimized = _isMinimized;
        _settings.WindowSizeX        = _expandedSize.X;
        _settings.WindowSizeY        = _expandedSize.Y;
        _settings.BgOpacity          = _bgOpacity;
        _settings.DashShowWeapons    = DashWindows.ShowWeapons;
        _settings.DashShowLua        = DashWindows.ShowLua;
        _settings.DashShowNavigation = DashWindows.ShowNavigation;
        _settings.DashShowMacroRules = DashWindows.ShowMacroRules;
        _settings.DashShowMonsters   = DashWindows.ShowMonsters;
        _settings.DashShowDungeonMap = DashWindows.ShowDungeonMap;
    }

    private void ApplyUiStateFromSettings()
    {
        _isLocked     = _settings.WindowLocked;
        _isMinimized  = _settings.DashboardMinimized;
        _expandedSize = new Vector2(_settings.WindowSizeX, _settings.WindowSizeY);
        _bgOpacity    = _settings.BgOpacity;
        DashWindows.ShowWeapons     = _settings.DashShowWeapons;
        DashWindows.ShowLua         = _settings.DashShowLua;
        DashWindows.ShowNavigation  = _settings.DashShowNavigation;
        DashWindows.ShowMacroRules  = _settings.DashShowMacroRules;
        DashWindows.ShowMonsters    = _settings.DashShowMonsters;
        DashWindows.ShowDungeonMap = _settings.DashShowDungeonMap;
    }

    private string GetProfileFilePath(string profileName)
    {
        if (string.IsNullOrEmpty(_charFolder)) return string.Empty;
        return Path.Combine(_charFolder, profileName + ".json");
    }

    private string GetActiveProfileMarkerPath()
    {
        if (string.IsNullOrEmpty(_charFolder)) return string.Empty;
        return Path.Combine(_charFolder, "active_profile.txt");
    }

    private string ReadActiveProfile()
    {
        string markerPath = GetActiveProfileMarkerPath();
        if (!string.IsNullOrEmpty(markerPath) && File.Exists(markerPath))
        {
            try
            {
                string name = File.ReadAllText(markerPath).Trim();
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch { }
        }
        return "Default";
    }

    private void WriteActiveProfile(string profileName)
    {
        string markerPath = GetActiveProfileMarkerPath();
        if (string.IsNullOrEmpty(markerPath)) return;
        try
        {
            Directory.CreateDirectory(_charFolder);
            File.WriteAllText(markerPath, profileName);
        }
        catch { }
    }

    private void SwitchProfile(string newProfile)
    {
        if (string.Equals(newProfile, _settings.SelectedProfile, StringComparison.OrdinalIgnoreCase))
            return;

        // Save current settings to current profile file
        SaveSettings();

        // Switch to the new profile
        _settings.SelectedProfile = newProfile;
        _settingsFilePath = GetProfileFilePath(newProfile);
        WriteActiveProfile(newProfile);

        // Load from new profile file if it exists
        if (File.Exists(_settingsFilePath))
        {
            try
            {
                string json = File.ReadAllText(_settingsFilePath);
                var loaded = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.LegacyUiSettings);
                if (loaded != null)
                {
                    CopySettings(loaded, _settings);
                    _settings.SelectedProfile = newProfile;
                    _lastSavedJson = json;
                }
            }
            catch { }
        }
        else
        {
            // New profile — force save current settings as its initial state
            _lastSavedJson = string.Empty;
        }

        ApplyUiStateFromSettings();

        // Reload nav route for the new profile
        _settings.CurrentRoute = new NavRouteParser();
        if (!string.IsNullOrEmpty(_settings.CurrentNavPath) && File.Exists(_settings.CurrentNavPath))
        {
            try
            {
                _settings.CurrentRoute = NavRouteParser.Load(_settings.CurrentNavPath);
                _settings.ActiveNavIndex =
                    (_settings.CurrentRoute.RouteType == NavRouteType.Follow ||
                     _settings.CurrentRoute.RouteType == NavRouteType.Once)
                        ? 0
                        : FindNearestWaypoint(_settings.CurrentRoute);
            }
            catch { _settings.CurrentNavPath = string.Empty; }
        }

        RefreshAllLists();
    }

    private static string SanitizeFileName(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) || c == '\'' || c == '-' ? c : '_');
        return sb.ToString();
    }

    /// <summary>
    /// Copies all JSON-serializable fields from src into dst without replacing the
    /// object reference (so all existing bindings to _settings remain valid).
    /// </summary>
    private static void CopySettings(LegacyUiSettings src, LegacyUiSettings dst)
    {
        // Re-serialize src, then deserialize over dst's fields via reflection-free copy.
        // Simplest approach: copy field-by-field via JSON round-trip into dst.
        string json = JsonSerializer.Serialize(src, RynthAiJsonContext.Default.LegacyUiSettings);
        var tmp = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.LegacyUiSettings);
        if (tmp == null) return;

        // Copy all serialized fields manually (the source generator guarantees coverage)
        dst.EnableBuffing            = tmp.EnableBuffing;
        dst.EnableCombat             = tmp.EnableCombat;
        dst.EnableNavigation         = tmp.EnableNavigation;
        dst.EnableLooting            = tmp.EnableLooting;
        dst.EnableMeta               = tmp.EnableMeta;
        dst.EnableRaycasting         = tmp.EnableRaycasting;
        // Only Legacy movement works for now: a saved Tier 1 or 2 pick becomes
        // Legacy. Nothing else in the saved settings is changed.
        dst.MovementMode             = tmp.MovementMode is 1 or 2 ? 0 : tmp.MovementMode;
        dst.NavStopTurnAngle         = tmp.NavStopTurnAngle;
        dst.NavResumeTurnAngle       = tmp.NavResumeTurnAngle;
        dst.NavDeadZone              = tmp.NavDeadZone;
        dst.NavSweepMult             = tmp.NavSweepMult;
        dst.NavLookaheadYards        = tmp.NavLookaheadYards;
        dst.NavShortcutYards         = tmp.NavShortcutYards;
        dst.NavTurnRateDegPerSec     = tmp.NavTurnRateDegPerSec;
        dst.NavTier1TurnSpeed        = tmp.NavTier1TurnSpeed;
        dst.PostPortalDelaySec       = tmp.PostPortalDelaySec;
        dst.NavRecoveryEnabled       = tmp.NavRecoveryEnabled;
        dst.NavOffTrackYards         = Math.Max(160f, tmp.NavOffTrackYards);
        dst.NavMaxDetourAttempts     = Math.Clamp(tmp.NavMaxDetourAttempts, 1, 10);
        dst.T2Speed                  = tmp.T2Speed;
        dst.T2DistanceTo             = tmp.T2DistanceTo;
        dst.T2ReissueMs              = tmp.T2ReissueMs;
        dst.T2MaxRangeYd             = tmp.T2MaxRangeYd;
        dst.T2MaxLandblocks          = tmp.T2MaxLandblocks;
        dst.T2WalkWithinYd           = tmp.T2WalkWithinYd;
        dst.CurrentNavPath           = tmp.CurrentNavPath;
        dst.CurrentLootPath          = tmp.CurrentLootPath;
        dst.CurrentMetaPath          = tmp.CurrentMetaPath;
        dst.MacroSettingsIdx         = tmp.MacroSettingsIdx;
        dst.NavProfileIdx            = tmp.NavProfileIdx;
        dst.LootProfileIdx           = tmp.LootProfileIdx;
        dst.MetaProfileIdx           = tmp.MetaProfileIdx;
        dst.EnableAutostack          = tmp.EnableAutostack;
        dst.EnableAutocram           = tmp.EnableAutocram;
        dst.ReadUnknownScrolls       = tmp.ReadUnknownScrolls;
        dst.EnableCombineSalvage     = tmp.EnableCombineSalvage;
        dst.CombineBagsDuringSalvage = tmp.CombineBagsDuringSalvage;
        dst.ShowTargetStaminaMana    = tmp.ShowTargetStaminaMana;
        dst.EnableMissileCrafting    = tmp.EnableMissileCrafting;
        dst.MissileCraftAmmoThreshold= tmp.MissileCraftAmmoThreshold;
        dst.LootInterItemDelayMs     = tmp.LootInterItemDelayMs;
        dst.LootContentSettleMs      = tmp.LootContentSettleMs;
        dst.LootEmptyCorpseMs        = tmp.LootEmptyCorpseMs;
        dst.LootClosingDelayMs       = tmp.LootClosingDelayMs;
        dst.LootAssessWindowMs       = tmp.LootAssessWindowMs;
        dst.LootRetryTimeoutMs       = tmp.LootRetryTimeoutMs;
        dst.LootOpenRetryMs          = tmp.LootOpenRetryMs;
        dst.LootCorpseTimeoutMs      = tmp.LootCorpseTimeoutMs;
        dst.LootJumpEnabled          = tmp.LootJumpEnabled;
        dst.LootJumpHeight           = tmp.LootJumpHeight;
        dst.SalvageOpenDelayFirstMs  = tmp.SalvageOpenDelayFirstMs;
        dst.SalvageOpenDelayFastMs   = tmp.SalvageOpenDelayFastMs;
        dst.SalvageAddDelayFirstMs   = tmp.SalvageAddDelayFirstMs;
        dst.SalvageAddDelayFastMs    = tmp.SalvageAddDelayFastMs;
        dst.SalvageSalvageDelayMs    = tmp.SalvageSalvageDelayMs;
        dst.SalvageResultDelayFirstMs= tmp.SalvageResultDelayFirstMs;
        dst.SalvageResultDelayFastMs = tmp.SalvageResultDelayFastMs;
        dst.UseDispelItems           = tmp.UseDispelItems;
        dst.CastDispelSelf           = tmp.CastDispelSelf;
        dst.AutoFellowMgmt           = tmp.AutoFellowMgmt;
        dst.MChargesWhenOff          = tmp.MChargesWhenOff;
        dst.HealAt                   = tmp.HealAt;
        dst.UsePotions               = tmp.UsePotions;
        dst.UseBuffItems             = tmp.UseBuffItems;
        dst.MakeRationsBelow         = tmp.MakeRationsBelow;
        dst.UseKitsInMagicMode       = tmp.UseKitsInMagicMode;
        dst.PeaceModeForKits         = tmp.PeaceModeForKits;
        dst.KitMinSuccessPct         = tmp.KitMinSuccessPct;
        dst.EmergencyHealAt          = tmp.EmergencyHealAt;
        dst.StaminaToHealthAt        = tmp.StaminaToHealthAt;
        dst.StaminaToHealthMinStamina= tmp.StaminaToHealthMinStamina;
        dst.StopMacroOnDeath         = tmp.StopMacroOnDeath;
        dst.StopMacroOnNoComponents  = tmp.StopMacroOnNoComponents;
        dst.StopLootingWhenPackFull  = tmp.StopLootingWhenPackFull;
        dst.StopMacroWhenPackFull    = tmp.StopMacroWhenPackFull;
        dst.RestamAt                 = tmp.RestamAt;
        dst.GetManaAt                = tmp.GetManaAt;
        dst.TopOffHP                 = tmp.TopOffHP;
        dst.TopOffStam               = tmp.TopOffStam;
        dst.TopOffMana               = tmp.TopOffMana;
        dst.HealOthersAt             = tmp.HealOthersAt;
        dst.RestamOthersAt           = tmp.RestamOthersAt;
        dst.InfuseOthersAt           = tmp.InfuseOthersAt;
        dst.MonsterRange             = tmp.MonsterRange;
        dst.MonsterDisengageRange    = tmp.MonsterDisengageRange;
        dst.RingRange                = tmp.RingRange;
        dst.ApproachRange            = tmp.ApproachRange;
        dst.MinRingTargets           = tmp.MinRingTargets;
        dst.BlastRange               = tmp.BlastRange;
        dst.MinBlastTargets          = tmp.MinBlastTargets;
        dst.FollowNavMin             = tmp.FollowNavMin;
        dst.NavRingThickness         = tmp.NavRingThickness;
        dst.NavLineThickness         = tmp.NavLineThickness;
        dst.NavHeightOffset          = tmp.NavHeightOffset;
        dst.NavSlopeSink             = tmp.NavSlopeSink;
        dst.MaxMonRange              = tmp.MaxMonRange;
        dst.SummonPets               = tmp.SummonPets;
        dst.CustomPetRange           = tmp.CustomPetRange;
        dst.PetMinMonsters           = tmp.PetMinMonsters;
        dst.AdvancedOptions          = tmp.AdvancedOptions;
        dst.MineOnly                 = tmp.MineOnly;
        dst.ShowEditor               = tmp.ShowEditor;
        dst.CorpseApproachRangeMax   = NormalizeCorpseRangeYards(tmp.CorpseApproachRangeMax, 10.0);
        dst.CorpseApproachRangeMin   = NormalizeCorpseRangeYards(tmp.CorpseApproachRangeMin, 2.0);
        dst.BoostNavPriority         = tmp.BoostNavPriority;
        dst.BoostLootPriority        = tmp.BoostLootPriority;
        dst.OpenDoors                = tmp.OpenDoors;
        dst.OpenDoorRange            = tmp.OpenDoorRange;
        dst.AutoUnlockDoors          = tmp.AutoUnlockDoors;
        dst.LootOwnership            = tmp.LootOwnership;
        dst.LootOwnCorpse            = tmp.LootOwnCorpse;
        dst.TravelToOwnCorpse        = tmp.TravelToOwnCorpse;
        dst.LootOnlyRareCorpses      = tmp.LootOnlyRareCorpses;
        dst.PeaceModeWhenIdle        = tmp.PeaceModeWhenIdle;
        dst.RebuffWhenIdle           = tmp.RebuffWhenIdle;
        dst.RebuffSecondsRemaining   = tmp.RebuffSecondsRemaining;
        dst.RebuffTopOffSecondsRemaining = tmp.RebuffTopOffSecondsRemaining;
        dst.CastBuffsOverItemBuffs   = tmp.CastBuffsOverItemBuffs;
        dst.BlacklistAttempts             = tmp.BlacklistAttempts;
        dst.BlacklistTimeoutSec           = tmp.BlacklistTimeoutSec;
        // 0 is never meant (every UI floors it at 250+) and is what the Settings-panel bug
        // saved into profiles: it judges each cast before its damage can land.
        dst.BlacklistCastSettleMs         = tmp.BlacklistCastSettleMs > 0 ? tmp.BlacklistCastSettleMs : 1500;
        dst.TargetNoProgressTimeoutSec    = tmp.TargetNoProgressTimeoutSec;
        dst.MeleeAttackPower         = tmp.MeleeAttackPower;
        dst.MissileAttackPower       = tmp.MissileAttackPower;
        dst.UseRecklessness          = tmp.UseRecklessness;
        dst.UseNativeAttack          = tmp.UseNativeAttack;
        dst.WieldUnlistedWandWhenNoneListed = tmp.WieldUnlistedWandWhenNoneListed;
        dst.OffhandDefault           = OffhandRules.SettingValue(OffhandRules.Parse(tmp.OffhandDefault, OffhandMode.Auto));
        dst.PreferDualWield          = tmp.PreferDualWield;
        dst.MeleeAttackHeight        = tmp.MeleeAttackHeight;
        dst.MissileAttackHeight      = tmp.MissileAttackHeight;
        dst.UseArcs                  = tmp.UseArcs;
        dst.BowArcVelocity           = tmp.BowArcVelocity;
        dst.CrossbowArcVelocity      = tmp.CrossbowArcVelocity;
        dst.AtlatlArcVelocity        = tmp.AtlatlArcVelocity;
        dst.MagicArcVelocity         = LegacyUiSettings.MigrateMagicArcVelocity(tmp.MagicArcVelocity);
        dst.MissileArcClearance      = tmp.MissileArcClearance >= 0f ? Math.Min(tmp.MissileArcClearance, 3f) : 0.5f;
        dst.LosDebugLog              = tmp.LosDebugLog;
        dst.EnableFPSLimit           = tmp.EnableFPSLimit;
        dst.TargetFPSFocused         = tmp.TargetFPSFocused;
        dst.TargetFPSBackground      = tmp.TargetFPSBackground;
        // MonsterRule deep-copy preserves Category + MatchExpression via JSON round-trip
        dst.MonsterRules             = tmp.MonsterRules;
        dst.ItemRules                = tmp.ItemRules;
        dst.ConsumableRules          = tmp.ConsumableRules;
        dst.BuffRules                = tmp.BuffRules;
        dst.MetaRules                = tmp.MetaRules;
        dst.ProfileItemsChecked      = tmp.ProfileItemsChecked;
        dst.SelectedProfile          = tmp.SelectedProfile;
        dst.ActiveNavIndex           = tmp.ActiveNavIndex;
        dst.ShowAdvancedWindow       = tmp.ShowAdvancedWindow;
        dst.SelectedAdvancedTab      = tmp.SelectedAdvancedTab;
        dst.WindowPosX               = tmp.WindowPosX;
        dst.WindowPosY               = tmp.WindowPosY;
        dst.WindowLocked             = tmp.WindowLocked;
        dst.DashboardVisible         = tmp.DashboardVisible;
        dst.DashboardMinimized       = tmp.DashboardMinimized;
        dst.WindowSizeX              = tmp.WindowSizeX;
        dst.WindowSizeY              = tmp.WindowSizeY;
        dst.BgOpacity                = tmp.BgOpacity;
        dst.DashShowWeapons          = tmp.DashShowWeapons;
        dst.DashShowLua              = tmp.DashShowLua;
        dst.DashShowNavigation       = tmp.DashShowNavigation;
        dst.DashShowMacroRules       = tmp.DashShowMacroRules;
        dst.DashShowMonsters         = tmp.DashShowMonsters;
        dst.DashShowDungeonMap       = tmp.DashShowDungeonMap;
        dst.MapShowDoors             = tmp.MapShowDoors;
        dst.MapShowCreatures         = tmp.MapShowCreatures;
        dst.MapShowToolbar           = tmp.MapShowToolbar;
        dst.MapBgOpacity             = tmp.MapBgOpacity;
        dst.MapRotateWithPlayer      = tmp.MapRotateWithPlayer;
        dst.ShowRadarWalls           = tmp.ShowRadarWalls;
        dst.RadarWallWorldRange      = tmp.RadarWallWorldRange;
        dst.MinSkillLevelTier1       = tmp.MinSkillLevelTier1;
        dst.MinSkillLevelTier2       = tmp.MinSkillLevelTier2;
        dst.MinSkillLevelTier3       = tmp.MinSkillLevelTier3;
        dst.MinSkillLevelTier4       = tmp.MinSkillLevelTier4;
        dst.MinSkillLevelTier5       = tmp.MinSkillLevelTier5;
        dst.MinSkillLevelTier6       = tmp.MinSkillLevelTier6;
        dst.MinSkillLevelTier7       = tmp.MinSkillLevelTier7;
        dst.MinSkillLevelTier8       = tmp.MinSkillLevelTier8;
        dst.BuffMinSkillLevelTier1   = tmp.BuffMinSkillLevelTier1;
        dst.BuffMinSkillLevelTier2   = tmp.BuffMinSkillLevelTier2;
        dst.BuffMinSkillLevelTier3   = tmp.BuffMinSkillLevelTier3;
        dst.BuffMinSkillLevelTier4   = tmp.BuffMinSkillLevelTier4;
        dst.BuffMinSkillLevelTier5   = tmp.BuffMinSkillLevelTier5;
        dst.BuffMinSkillLevelTier6   = tmp.BuffMinSkillLevelTier6;
        dst.BuffMinSkillLevelTier7   = tmp.BuffMinSkillLevelTier7;
        dst.BuffMinSkillLevelTier8   = tmp.BuffMinSkillLevelTier8;
        dst.EnableManaTapping        = tmp.EnableManaTapping;
        dst.ManaTapMinMana           = tmp.ManaTapMinMana;
        dst.ManaStoneKeepCount       = tmp.ManaStoneKeepCount;
        dst.AutoVendorEnabled          = tmp.AutoVendorEnabled;
        dst.AutoVendorEnableBuying     = tmp.AutoVendorEnableBuying;
        dst.AutoVendorEnableSelling    = tmp.AutoVendorEnableSelling;
        dst.AutoVendorTestMode         = tmp.AutoVendorTestMode;
        dst.AutoVendorThink            = tmp.AutoVendorThink;
        dst.AutoVendorShowMerchantInfo = tmp.AutoVendorShowMerchantInfo;
        dst.AutoVendorOnlyFromMainPack = tmp.AutoVendorOnlyFromMainPack;
        dst.AutoVendorTries            = tmp.AutoVendorTries;
        dst.AutoVendorTriesTime        = tmp.AutoVendorTriesTime;
        dst.AutoTradeEnabled           = tmp.AutoTradeEnabled;
        dst.AutoTradeTestMode          = tmp.AutoTradeTestMode;
        dst.AutoTradeThink             = tmp.AutoTradeThink;
        dst.AutoTradeOnlyFromMainPack  = tmp.AutoTradeOnlyFromMainPack;
        dst.AutoTradeAutoAccept        = tmp.AutoTradeAutoAccept;
        dst.AutoTradeAutoAcceptChars   = tmp.AutoTradeAutoAcceptChars ?? new();
        dst.MetaDebug                = tmp.MetaDebug;
        dst.StartMacroOnLogin        = tmp.StartMacroOnLogin;
        dst.PatrolOnLogin            = tmp.PatrolOnLogin;
        dst.YieldToVTank             = tmp.YieldToVTank;
        dst.FeatureServerNames       = tmp.FeatureServerNames ?? new(IltHub.ServerFeatureGate.DefaultServerNames);
        dst.ForceServerFeatures      = tmp.ForceServerFeatures;
        dst.ShowTerrainPassability   = tmp.ShowTerrainPassability;
        dst.GiveQueueIntervalMs      = tmp.GiveQueueIntervalMs;
        dst.T11ItemAugsOverride      = tmp.T11ItemAugsOverride;
        dst.SpellCastIntervalMs      = tmp.SpellCastIntervalMs;
        dst.AttackSpellIntervalMs    = tmp.AttackSpellIntervalMs;
        dst.EmbeddedNavs             = tmp.EmbeddedNavs;
        dst.SuppressRetailRadar      = tmp.SuppressRetailRadar;
        dst.ShowRynthRadar           = tmp.ShowRynthRadar;
        dst.RadarRotateWithPlayer    = tmp.RadarRotateWithPlayer;
        dst.RadarOpacity             = tmp.RadarOpacity;
        dst.RadarZoom                = tmp.RadarZoom;
        dst.RadarShowMonsters        = tmp.RadarShowMonsters;
        dst.RadarShowNpcs            = tmp.RadarShowNpcs;
        dst.RadarShowPortals         = tmp.RadarShowPortals;
        dst.RadarShowDoors           = tmp.RadarShowDoors;
        dst.RadarWallPaintRadius     = tmp.RadarWallPaintRadius;
        dst.RadarCircular            = tmp.RadarCircular;
        dst.RadarClickThrough        = tmp.RadarClickThrough;
        dst.SuppressRetailPowerbar   = tmp.SuppressRetailPowerbar;
        dst.ShowRynthChat            = tmp.ShowRynthChat;
        dst.ChatOpacity              = tmp.ChatOpacity;
        dst.ChatMaxLines             = tmp.ChatMaxLines;
        dst.ChatShowTimestamps       = tmp.ChatShowTimestamps;
        dst.ChatClickThrough         = tmp.ChatClickThrough;
    }

    private static double NormalizeCorpseRangeYards(double value, double fallbackYards)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0)
            return fallbackYards;

        // Older settings stored corpse ranges in 240-based world units. Convert those
        // to direct yard values so the UI can use whole numbers like 5 or 6.
        return value <= 1.0 ? value * 240.0 : value;
    }


    public void SetSelectedTarget(uint targetId)
    {
        _currentTargetId = targetId;
        _targetLabel = "NO TARGET";
        if (targetId != 0)
        {
            _targetLabel = _host.HasGetObjectName && _host.TryGetObjectName(targetId, out string name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : $"TARGET 0x{targetId:X8}";
        }
        _targetHealthPercent = 0f;
        _targetHealth = 0;
        _targetMaxHealth = 0;
        _targetStamina = 0;
        _targetMaxStamina = 0;
        _targetMana = 0;
        _targetMaxMana = 0;
        _targetHealthDisplay = targetId == 0 ? "0" : "--";
        if (targetId != 0 && _host.HasQueryHealth) _host.QueryHealth(targetId);
    }

    public void OnUpdateHealth(uint targetId, float healthRatio, uint currentHealth, uint maxHealth)
    {
        if (targetId == 0 || targetId != _currentTargetId)
            return;

        _targetHealthPercent = Math.Clamp(healthRatio, 0f, 1f);
        if (maxHealth > 0)
        {
            _targetHealth = currentHealth;
            _targetMaxHealth = maxHealth;
            _targetHealthDisplay = $"{currentHealth}/{maxHealth}";
        }
        else
        {
            // Try to resolve absolute health from previously identified creature data.
            uint storedMax = 0;
            var lookup = CreatureLookupForRules;
            if (lookup != null && !string.IsNullOrEmpty(_targetLabel))
            {
                var profile = lookup(_targetLabel);
                if (profile != null && profile.MaxHealth > 0)
                    storedMax = profile.MaxHealth;
            }

            if (storedMax > 0)
            {
                _targetMaxHealth = storedMax;
                _targetHealth = (uint)Math.Round(_targetHealthPercent * storedMax);
                _targetHealthDisplay = $"{_targetHealth}/{storedMax}";
            }
            else
            {
                _targetHealth = 0;
                _targetMaxHealth = 0;
                _targetHealthDisplay = $"{(int)(_targetHealthPercent * 100)}%";
            }
        }

        // Refresh stamina/mana from the creature vitals cache
        if (_host.HasGetTargetVitals &&
            _host.TryGetTargetVitals(targetId, out _, out _, out uint st, out uint maxSt, out uint mn, out uint maxMn))
        {
            _targetStamina = st;
            _targetMaxStamina = maxSt;
            _targetMana = mn;
            _targetMaxMana = maxMn;
        }
    }

    private void RefreshPlayerVitals()
    {
        if (!_host.HasGetPlayerVitals)
            return;

        if (_host.TryGetPlayerVitals(
            out uint health,
            out uint maxHealth,
            out uint stamina,
            out uint maxStamina,
            out uint mana,
            out uint maxMana))
        {
            _playerHealth = health;
            _playerMaxHealth = maxHealth != 0 ? maxHealth : _playerMaxHealth;
            _playerStamina = stamina;
            _playerMaxStamina = maxStamina != 0 ? maxStamina : _playerMaxStamina;
            _playerMana = mana;
            _playerMaxMana = maxMana != 0 ? maxMana : _playerMaxMana;
        }
    }

    /// Reset the per-session counters at login so the per-hour rates measure THIS session.
    private void ResetSessionStats()
    {
        _sessionStartUtc = DateTime.UtcNow;
        System.Threading.Interlocked.Exchange(ref _sessionKills, 0);
        System.Threading.Interlocked.Exchange(ref _lastKillTicks, 0);
    }

    /// Called from the plugin's OnKillNotification (pump thread) for each kill — feeds kills/hour
    /// and the "time since last kill" liveness signal.
    public void RecordKill()
    {
        System.Threading.Interlocked.Increment(ref _sessionKills);
        System.Threading.Interlocked.Exchange(ref _lastKillTicks, DateTime.UtcNow.Ticks);
    }

    /// Pushed from the plugin tick (it owns the inventory cache) — main-pack empty slots. -1 = unknown.
    public void SetFreeSlots(int slots) => _freeSlots = slots;

    /// Pushed from the plugin tick — D2 three-tier target counts (-1 = no scan yet).
    public void SetScanCounts(int total, int ring, int possible, int losBlocked)
    {
        _scanTotal = total;
        _scanRing = ring;
        _scanPossible = possible;
        _scanLosBlocked = losBlocked;
    }

    /// Pushed from the plugin tick — D6 offensive attack-cast tallies.
    public void SetCastStats(int sessionAttackCasts, int castsSinceLastKill)
    {
        _sessionAttackCasts = sessionAttackCasts;
        _castsSinceLastKill = castsSinceLastKill;
    }

    private volatile bool _uiHidden;   // remote "Hide UI" state, surfaced to the status feed
    public void SetUiHidden(bool hidden) => _uiHidden = hidden;

    /// Pushed from the plugin tick — casting-component tallies (scarabs / prismatic tapers) and the
    /// per-tier scarab breakdown. -1 = unknown; byType may be null (treated as empty).
    public void SetComponentCounts(int scarabs, int tapers, List<KeyValuePair<string, int>> byType)
    {
        _scarabs = scarabs;
        _tapers = tapers;
        _scarabsByType = byType != null ? byType.ToArray() : System.Array.Empty<KeyValuePair<string, int>>();
    }

    /// Pushed from the plugin tick — the gear the character is wearing/wielding, with full appraisal.
    public void SetEquipment(List<EquipAppraisal> equipment)
        => _equipment = equipment != null ? equipment.ToArray() : System.Array.Empty<EquipAppraisal>();

    /// Pushed from the plugin tick — the full read-only inventory (containers + items) for the remote
    /// viewer. Atomic ref-swap of both arrays + a monotonic version bump (consumers detect change).
    public void SetInventory(List<InventoryContainerSnapshot> containers, List<InventoryItemSnapshot> items)
    {
        _invContainers = containers != null ? containers.ToArray() : System.Array.Empty<InventoryContainerSnapshot>();
        _invItems = items != null ? items.ToArray() : System.Array.Empty<InventoryItemSnapshot>();
        System.Threading.Interlocked.Increment(ref _inventoryVersion);
    }

    /// Serialize the full inventory (schema "rynthcore.inventory/1") for the dedicated
    /// RynthPluginGetInventoryJson export. Own builder + 32KB initial capacity (the hot
    /// BuildSnapshotJson stays lean at 2KB). Reads the volatile arrays once (atomic ref).
    public string BuildInventoryJson()
    {
        var items = _invItems;
        var containers = _invContainers;
        int version = System.Threading.Interlocked.CompareExchange(ref _inventoryVersion, 0, 0);
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder(32768);
        sb.Append('{');
        AppendString(sb, "schema", "rynthcore.inventory/1"); sb.Append(',');
        AppendInt(sb, "version", version); sb.Append(',');
        AppendInt(sb, "itemCount", items.Length); sb.Append(',');
        sb.Append("\"containers\":[");
        for (int i = 0; i < containers.Length; i++)
        {
            var c = containers[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(c.Id);
            sb.Append(",\"name\":\""); AppendEscaped(sb, c.Name ?? string.Empty); sb.Append('"');
            sb.Append(",\"kind\":\""); AppendEscaped(sb, c.Kind ?? string.Empty); sb.Append('"');
            sb.Append(",\"capacity\":").Append(c.Capacity);
            sb.Append('}');
        }
        sb.Append("],\"items\":[");
        for (int i = 0; i < items.Length; i++)
        {
            var it = items[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(it.Id);
            sb.Append(",\"name\":\""); AppendEscaped(sb, it.Name ?? string.Empty); sb.Append('"');
            sb.Append(",\"wcid\":").Append(it.Wcid);
            sb.Append(",\"objectClass\":").Append(it.ObjectClass);
            sb.Append(",\"containerId\":").Append(it.ContainerId);
            sb.Append(",\"location\":").Append(it.Location);
            sb.Append(",\"slot\":").Append(it.Slot);
            sb.Append(",\"stackCount\":").Append(it.StackCount);
            sb.Append(",\"iconDid\":").Append(it.IconDid);
            sb.Append(",\"equipped\":").Append(it.Equipped ? "true" : "false");
            sb.Append(",\"wieldedLocation\":").Append(it.WieldedLocation);
            sb.Append(",\"appraisal\":");
            if (it.Appraisal is { } a) AppendInventoryAppraisal(sb, a, ci);
            else sb.Append("null");
            sb.Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    /// Emit the appraisal sub-object for an inventory item — same field shape/names as the
    /// equipment entry in BuildSnapshotJson (minus name/id/slot, which live on the item), so the
    /// app reuses the existing AcEquipItem appraisal fragment verbatim.
    private static void AppendInventoryAppraisal(System.Text.StringBuilder sb, EquipAppraisal a, System.Globalization.CultureInfo ci)
    {
        sb.Append("{\"armorLevel\":").Append(a.ArmorLevel);
        if (a.Resist is { Length: 7 })
        {
            sb.Append(",\"resist\":[");
            for (int r = 0; r < 7; r++) { if (r > 0) sb.Append(','); sb.Append(a.Resist[r].ToString("0.###", ci)); }
            sb.Append(']');
        }
        sb.Append(",\"value\":").Append(a.Value).Append(",\"burden\":").Append(a.Burden)
          .Append(",\"workmanship\":").Append(a.Workmanship).Append(",\"material\":").Append(a.Material)
          .Append(",\"maxMana\":").Append(a.MaxMana).Append(",\"curMana\":").Append(a.CurMana)
          .Append(",\"damage\":").Append(a.Damage).Append(",\"damageType\":").Append(a.DamageType)
          .Append(",\"weaponDef\":").Append(a.WeaponDef.ToString("0.###", ci))
          .Append(",\"missileDef\":").Append(a.MissileDef.ToString("0.###", ci))
          .Append(",\"magicDef\":").Append(a.MagicDef.ToString("0.###", ci))
          .Append(",\"variance\":").Append(a.Variance.ToString("0.###", ci))
          .Append(",\"elementalMod\":").Append(a.ElementalMod.ToString("0.###", ci));
        sb.Append(",\"spells\":[");
        for (int s = 0; s < a.Spells.Length; s++) { if (s > 0) sb.Append(','); sb.Append('"'); AppendEscaped(sb, a.Spells[s] ?? string.Empty); sb.Append('"'); }
        sb.Append(']');
        sb.Append(",\"longDesc\":\""); AppendEscaped(sb, a.LongDesc ?? string.Empty); sb.Append('"');
        sb.Append('}');
    }

    /// Whole-session kills/hour from the kill counter — a pure counter + clock, safe to compute
    /// on the off-thread snapshot poll (no AC read).
    private void RefreshKillsPerHour()
    {
        double hours = (DateTime.UtcNow - _sessionStartUtc).TotalHours;
        _killsPerHour = hours > 1.0 / 3600.0 ? System.Threading.Interlocked.Read(ref _sessionKills) / hours : 0;
    }

    private void RefreshAllLists()
    {
        RefreshProfilesList();
        RefreshNavFiles();
        RefreshLootFiles();
        RefreshMetaFiles();
    }

    private void RefreshProfilesList()
    {
        var list = new List<string> { "Default" };
        try
        {
            if (!string.IsNullOrEmpty(_charFolder) && Directory.Exists(_charFolder))
            {
                foreach (string file in Directory.GetFiles(_charFolder, "*.json"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (name.Equals("settings", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Equals("Default", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Equals("monsters", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Equals("metamanager", StringComparison.OrdinalIgnoreCase)) continue;   // the Meta Manager's rules
                    if (!list.Contains(name, StringComparer.OrdinalIgnoreCase))
                        list.Add(name);
                }
            }
        }
        catch { }
        // Deep-audit finding #16 (2026-06-18): Contains/[0] used to read
        // _profiles just after releasing the lock — a concurrent
        // SelectProfileAtIndex export call (poll thread) could be mid-index
        // against a list this same read races. Moved inside the lock.
        lock (_profileListsLock)
        {
            _profiles.Clear();
            _profiles.AddRange(list);
            if (!_profiles.Contains(_settings.SelectedProfile, StringComparer.OrdinalIgnoreCase))
                _settings.SelectedProfile = _profiles[0];
        }
    }

    private void RefreshNavFiles()
    {
        var list = new List<string> { "None" };
        int sel = 0;
        if (Directory.Exists(_navFolder))
            foreach (string file in Directory.GetFiles(_navFolder, "*.nav"))
            {
                list.Add(Path.GetFileNameWithoutExtension(file));
                if (file.Equals(_settings.CurrentNavPath, StringComparison.OrdinalIgnoreCase)) sel = list.Count - 1;
            }
        lock (_profileListsLock) { _navFiles.Clear(); _navFiles.AddRange(list); }
        _selectedNavIdx = sel;
    }

    /// <summary>Save button and /ra nav save [name]. Returns the chat line.</summary>
    public string SaveRoute(string? name)
    {
        string msg = _navigationUi.SaveRoute(name);
        RefreshNavFiles();
        SaveSettings();
        return msg;
    }

    /// <summary>Writes an edited route back to its file (or the meta's embedded nav).</summary>
    public void AutoSaveRoute() => _navigationUi.TryAutoSaveNav();

    /// <summary>/ra nav load &lt;name&gt;. Returns the chat line.</summary>
    public string LoadNavByName(string name)
    {
        RefreshNavFiles();
        string want = name.Trim();
        if (want.EndsWith(".nav", StringComparison.OrdinalIgnoreCase)) want = want[..^4].TrimEnd();
        int idx;
        lock (_profileListsLock)
            idx = _navFiles.FindIndex(f => f != "None" && f.Equals(want, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return $"[RynthAi] No nav named '{want}' in NavProfiles. /ra nav list shows them.";
        _selectedNavIdx = idx;
        LoadSelectedNav();
        SaveSettings();
        return $"[RynthAi] Loaded {want} ({_settings.CurrentRoute.Points.Count} waypoints).";
    }

    /// <summary>/ra nav list: the .nav files in NavProfiles.</summary>
    public List<string> NavFileNames()
    {
        RefreshNavFiles();
        lock (_profileListsLock)
            return _navFiles.FindAll(f => f != "None");
    }

    private void RefreshLootFiles()
    {
        var list = new System.Collections.Generic.List<string> { "None" };
        int idx = _settings.LootProfileIdx;
        if (Directory.Exists(_lootFolder))
        {
            var files = new System.Collections.Generic.List<string>();
            files.AddRange(Directory.GetFiles(_lootFolder, "*.utl"));
            files.AddRange(Directory.GetFiles(_lootFolder, "*.json"));
            files.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (string file in files)
            {
                list.Add(Path.GetFileName(file));
                if (file.Equals(_settings.CurrentLootPath, StringComparison.OrdinalIgnoreCase))
                    idx = list.Count - 1;
            }
        }
        lock (_profileListsLock) { _lootFiles.Clear(); _lootFiles.AddRange(list); }
        _settings.LootProfileIdx = idx;
    }

    private void RefreshMetaFiles()
    {
        var list = new List<string> { "None" };
        int idx = 0;
        if (Directory.Exists(_metaFolder))
        {
            var files = new List<string>();
            foreach (string f in Directory.GetFiles(_metaFolder, "*.met"))
            {
                if (Path.GetFileName(f).StartsWith("--")) continue;
                files.Add(f);
            }
            files.AddRange(Directory.GetFiles(_metaFolder, "*.af"));
            files.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (string file in files)
            {
                list.Add(Path.GetFileName(file));
                if (file.Equals(_settings.CurrentMetaPath, StringComparison.OrdinalIgnoreCase))
                    idx = list.Count - 1;
            }
        }
        lock (_profileListsLock) { _metaFiles.Clear(); _metaFiles.AddRange(list); }
        _settings.MetaProfileIdx = idx;
    }

    /// <summary>
    /// The Meta Manager's load (plugin tick): <paramref name="name"/> is a file in MetaFiles
    /// (farm.met / farm.af; without an extension .af is tried, then .met). The same load as
    /// picking it in the Meta panel. Returns null when loaded, else why not.
    /// </summary>
    public string? LoadMetaForSchedule(string name)
    {
        string want = (name ?? string.Empty).Trim();
        if (want.Length == 0) return "no meta named";
        string? path = null;
        try
        {
            string direct = Path.Combine(_metaFolder, Path.GetFileName(want));
            string ext = Path.GetExtension(want);
            if ((ext.Equals(".met", StringComparison.OrdinalIgnoreCase) || ext.Equals(".af", StringComparison.OrdinalIgnoreCase))
                && File.Exists(direct))
                path = direct;
            else
            {
                string stem = ext.Equals(".met", StringComparison.OrdinalIgnoreCase) || ext.Equals(".af", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileNameWithoutExtension(want) : Path.GetFileName(want);
                foreach (string e in new[] { ".af", ".met" })
                {
                    string candidate = Path.Combine(_metaFolder, stem + e);
                    if (File.Exists(candidate)) { path = candidate; break; }
                }
            }
        }
        catch (Exception ex) { return ex.Message; }
        if (path == null) return "not found in MetaFiles";

        if (!_metaUi.LoadMacroFile(path))
            return _metaUi.LastLoadStatus.Length > 0 ? _metaUi.LastLoadStatus : "no rules loaded";
        RefreshMetaFiles();   // MetaProfileIdx follows the loaded file
        SaveSettings();
        return null;
    }

    /// <summary>The Meta Manager's "mm_*" ops from the engine panel, run on the plugin tick (set by the plugin).</summary>
    public Action<MetaCommand>? ScheduleCommandHandler;

    /// <summary>The Meta Manager's snapshot JSON for BuildMetaJson (set by the plugin; built on the tick).</summary>
    public Func<string>? ScheduleJsonProvider;

    private void LoadSelectedNav()
    {
        // Same finding #16 class as SelectProfileAtIndex above — this direct
        // index into _navFiles is also reachable from the Avalonia poll
        // thread (via SelectProfileAtIndex) racing the pump thread's
        // Refresh*Files Clear()/AddRange().
        string? selection;
        lock (_profileListsLock)
        {
            if (_selectedNavIdx < 0 || _selectedNavIdx >= _navFiles.Count) return;
            selection = _navFiles[_selectedNavIdx];
        }
        if (selection == "None")
        {
            _settings.CurrentNavPath = string.Empty;
            _settings.CurrentRoute.Points.Clear();
            _settings.ActiveNavIndex = 0;
            SaveSettings();
            return;
        }
        string filePath = Path.Combine(_navFolder, selection + ".nav");
        _settings.CurrentNavPath = filePath;
        _settings.CurrentRoute = NavRouteParser.Load(filePath);
        if (_settings.CurrentRoute.LoadWarning != null)
        {
            _host.Log(_settings.CurrentRoute.LoadWarning);
            _host.WriteToChat($"[RynthAi] {_settings.CurrentRoute.LoadWarning}", 4);
        }
        // Follow and Once routes start from the top so opening Recall/Portal/Chat
        // actions always fire. Circular and Linear routes jump in at the nearest point.
        _settings.ActiveNavIndex =
            (_settings.CurrentRoute.RouteType == NavRouteType.Follow ||
             _settings.CurrentRoute.RouteType == NavRouteType.Once)
                ? 0
                : FindNearestWaypoint(_settings.CurrentRoute);
        SaveSettings();
    }

    // ── Nav bridge (engine-side Avalonia NavPanel) ────────────────────────────

    public string BuildNavJson()
    {
        try
        {
            var points = new List<NavBridgePoint>(_settings.CurrentRoute.Points.Count);
            for (int i = 0; i < _settings.CurrentRoute.Points.Count; i++)
            {
                var p = _settings.CurrentRoute.Points[i];
                points.Add(new NavBridgePoint
                {
                    Idx  = i,
                    Type = p.Type.ToString(),
                    Desc = p.ToString(),
                    Text = p.Type == NavPointType.Chat ? p.ChatCommand ?? string.Empty : string.Empty,
                    NS   = p.NS,
                    EW   = p.EW,
                    Z    = p.Z,
                });
            }

            int routeTypeInt = _settings.CurrentRoute.RouteType switch
            {
                NavRouteType.Circular => 1,
                NavRouteType.Linear   => 2,
                NavRouteType.Follow   => 3,
                _                     => 4,  // Once
            };

            var payload = new NavBridgePayload
            {
                // No extension: the panel matches this against NavFiles (names
                // without .nav) to show the loaded file in its Nav picker.
                ActiveNavName     = string.IsNullOrEmpty(_settings.CurrentNavPath)
                                        ? "None (Unsaved)"
                                        : _settings.CurrentNavPath.StartsWith("<embedded:", StringComparison.Ordinal)
                                            ? _settings.CurrentNavPath.Substring(10).TrimEnd('>') + " (in meta)"
                                            : Path.GetFileNameWithoutExtension(_settings.CurrentNavPath),
                NavStatusLine     = _settings.NavStatusLine ?? string.Empty,
                NavIsStuck        = _settings.NavIsStuck,
                MacroRunning      = _settings.IsMacroRunning,
                NavigationEnabled = _settings.EnableNavigation,
                RouteType         = routeTypeInt,
                ActiveNavIndex    = _settings.ActiveNavIndex,
                NavFiles          = new List<string>(_navFiles),
                Points            = points,
                TrackBreadcrumbs  = _settings.NavOverlay.TrackBreadcrumbs,
                ShowRouteOverlay  = _settings.NavOverlay.ShowRouteMarkers,
                IsRecording       = _settings.IsRecordingNav,
                TrailPoints       = NavBreadcrumbs?.Trail.Length ?? 0,
                TrailYards        = NavBreadcrumbs?.TrailYards ?? 0,
                EditStatus        = CurrentNavEditStatus(),
            };
            return JsonSerializer.Serialize(payload, RynthAiJsonContext.Default.NavBridgePayload);
        }
        catch { return "{}"; }
    }

    public void HandleNavCommand(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var cmd = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.NavCommand);
            if (cmd == null) return;

            switch (cmd.Cmd)
            {
                case "startNav":
                    _settings.IsMacroRunning  = true;
                    _settings.EnableNavigation = true;
                    if (_settings.BotAction != "Navigating")
                        _settings.BotAction = "Default";
                    SaveSettings();
                    break;

                case "stopNav":
                    _settings.EnableNavigation = false;
                    SaveSettings();
                    break;

                case "setBreadcrumbs":
                    // Nav panel "Breadcrumbs": record (and draw) the walked trail.
                    _settings.NavOverlay.TrackBreadcrumbs = cmd.On;
                    SaveSettings();
                    break;

                case "setRouteOverlay":
                    // Nav panel "Route overlay": route rings / lines, waypoint labels, guide line.
                    _settings.NavOverlay.ShowRouteMarkers = cmd.On;
                    SaveSettings();
                    break;

                case "addWaypoint":
                    _host.SetMotionBy("UI", 0x6500000D, false); // stop TurnRight
                    _host.SetMotionBy("UI", 0x6500000E, false); // stop TurnLeft
                    if (_host.HasGetPlayerPose &&
                        _host.TryGetPlayerPose(out _, out float wx, out float wy, out float wz, out _, out _, out _, out _) &&
                        NavCoordinateHelper.TryGetNavCoords(_host, out double wNS, out double wEW))
                    {
                        InsertNavPoint(new NavPoint { NS = wNS, EW = wEW, Z = wz / 240.0 }, cmd.AddMode, cmd.InsertAt);
                    }
                    break;

                case "addRecall":
                    if (_host.HasGetPlayerPose &&
                        _host.TryGetPlayerPose(out _, out float rx, out float ry, out float rz, out _, out _, out _, out _) &&
                        NavCoordinateHelper.TryGetNavCoords(_host, out double rNS, out double rEW))
                    {
                        InsertNavPoint(new NavPoint { Type = NavPointType.Recall, NS = rNS, EW = rEW, Z = rz / 240.0, SpellId = cmd.SpellId }, cmd.AddMode, cmd.InsertAt);
                    }
                    break;

                case "addChat":
                    if (!string.IsNullOrWhiteSpace(cmd.Text) &&
                        _host.HasGetPlayerPose &&
                        _host.TryGetPlayerPose(out _, out _, out _, out float cz, out _, out _, out _, out _) &&
                        NavCoordinateHelper.TryGetNavCoords(_host, out double cNS, out double cEW))
                    {
                        InsertNavPoint(new NavPoint { Type = NavPointType.Chat, NS = cNS, EW = cEW, Z = cz / 240.0, ChatCommand = cmd.Text.Trim() }, cmd.AddMode, cmd.InsertAt);
                    }
                    break;

                case "editChat":
                    // Change the text of an existing chat waypoint (Nav panel: select it, edit, Update).
                    if (cmd.Index >= 0 && cmd.Index < _settings.CurrentRoute.Points.Count
                        && _settings.CurrentRoute.Points[cmd.Index].Type == NavPointType.Chat
                        && !string.IsNullOrWhiteSpace(cmd.Text))
                    {
                        _settings.CurrentRoute.Points[cmd.Index].ChatCommand = cmd.Text.Trim();
                        _navigationUi.TryAutoSaveNav();
                    }
                    break;

                case "deletePoint":
                    int di = cmd.Index;
                    if (di >= 0 && di < _settings.CurrentRoute.Points.Count)
                    {
                        _settings.CurrentRoute.Points.RemoveAt(di);
                        _settings.ActiveNavIndex = LegacyNavigationUi.IndexAfterDelete(_settings.ActiveNavIndex, di, _settings.CurrentRoute.Points.Count);
                        _navigationUi.TryAutoSaveNav();
                    }
                    break;

                case "clearRoute":
                    _settings.CurrentRoute.Points.Clear();
                    _settings.ActiveNavIndex = 0;
                    _navigationUi.TryAutoSaveNav();
                    break;

                case "saveRoute":
                    _host.WriteToChat(SaveRoute(string.IsNullOrWhiteSpace(cmd.NavName) ? null : cmd.NavName), 1);
                    break;

                case "setRouteType":
                    _settings.CurrentRoute.RouteType = cmd.RouteType switch
                    {
                        1 => NavRouteType.Circular,
                        2 => NavRouteType.Linear,
                        3 => NavRouteType.Follow,
                        _ => NavRouteType.Once,
                    };
                    _navigationUi.TryAutoSaveNav();
                    break;

                case "loadNav":
                    int ni = _navFiles.IndexOf(cmd.NavName);
                    if (ni >= 0) { _selectedNavIdx = ni; LoadSelectedNav(); SaveSettings(); }
                    break;

                default:
                    // Route editor commands (multi-select edits, pause / portal steps, trail -> route).
                    HandleNavEditCommand(cmd);
                    break;
            }
        }
        catch { }
    }

    private void InsertNavPoint(NavPoint pt, int addMode, int insertAt)
    {
        if (addMode == 0 || _settings.CurrentRoute.Points.Count == 0 || insertAt < 0)
            _settings.CurrentRoute.Points.Add(pt);
        else if (addMode == 1)
            _settings.CurrentRoute.Points.Insert(Math.Min(insertAt, _settings.CurrentRoute.Points.Count), pt);
        else
            _settings.CurrentRoute.Points.Insert(Math.Min(insertAt + 1, _settings.CurrentRoute.Points.Count), pt);

        _navigationUi.TryAutoSaveNav();
    }

    private int FindNearestWaypoint(NavRouteParser route)
    {
        if (route.Points.Count == 0 || !NavCoordinateHelper.TryGetNavCoords(_host, out double ns, out double ew)) return 0;
        int best = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < route.Points.Count; i++)
        {
            NavPoint point = route.Points[i];
            if (!NavRouteParser.IsPlainWaypoint(point.Type)) continue;
            double distance = Math.Sqrt(Math.Pow(point.NS - ns, 2) + Math.Pow(point.EW - ew, 2));
            if (distance < bestDistance) { bestDistance = distance; best = i; }
        }
        return best;
    }
    private static string TruncateName(string? value, int max) => string.IsNullOrEmpty(value) ? string.Empty : value.Length > max ? value[..(max - 1)] + "..." : value;

    // ── Snapshot bridge (read by the engine-side Avalonia RynthAi panel) ────
    // The Avalonia panel mirrors this dashboard. It pulls a JSON snapshot via
    // RynthPluginGetSnapshotJson every ~33ms and renders the same fields.

    public string BuildSnapshotJson()
    {
        // Pull a fresh read every snapshot poll. If the cache is cold (right
        // after hot reload), the host's TryGetPlayerVitals will still have
        // returned zero — but the next OnUpdateHealth event or live qualities
        // read will fix that within a tick or two.
        RefreshPlayerVitals();
        RefreshKillsPerHour();   // pure counter/clock — safe off-thread

        var sb = new System.Text.StringBuilder(2048);
        sb.Append('{');
        AppendBool(sb, "macroRunning", _settings.IsMacroRunning); sb.Append(',');
        AppendString(sb, "currentState", _settings.CurrentState ?? string.Empty); sb.Append(',');
        // "botAction" is what the displays show (the dashboard's activity line, and the phone through
        // RynthRemote's status file): the control string, except that the Buffing slot says what it
        // is doing (Healing, Restoring mana, ...). The control string itself is "botActionControl".
        AppendString(sb, "botAction", ActivityArbiter.DisplayLabel(_settings.BotAction, _settings.BuffingLabel)); sb.Append(',');
        AppendString(sb, "botActionControl", _settings.BotAction ?? "Default"); sb.Append(',');
        AppendString(sb, "selectedProfile", _settings.SelectedProfile ?? "Default"); sb.Append(',');
        // Copy the four profile lists under the lock so we never enumerate one
        // while a Refresh*Files mutator Clear()s it on the pump thread — that
        // race threw InvalidOperationException and, escaping this snapshot
        // poll's reverse-P/Invoke boundary, fail-fasted the NativeAOT runtime.
        List<string> profilesCopy, navCopy, lootCopy, metaCopy;
        lock (_profileListsLock)
        {
            profilesCopy = new List<string>(_profiles);
            navCopy      = new List<string>(_navFiles);
            lootCopy     = new List<string>(_lootFiles);
            metaCopy     = new List<string>(_metaFiles);
        }
        AppendStringArray(sb, "profiles", profilesCopy); sb.Append(',');
        AppendStringArray(sb, "navProfiles", navCopy); sb.Append(',');
        AppendStringArray(sb, "lootProfiles", lootCopy); sb.Append(',');
        AppendStringArray(sb, "metaProfiles", metaCopy); sb.Append(',');
        AppendString(sb, "currentNavName",
            string.IsNullOrEmpty(_settings.CurrentNavPath) ? "None" : Path.GetFileNameWithoutExtension(_settings.CurrentNavPath)); sb.Append(',');
        AppendString(sb, "currentLootName",
            string.IsNullOrEmpty(_settings.CurrentLootPath) ? "None" : Path.GetFileNameWithoutExtension(_settings.CurrentLootPath)); sb.Append(',');
        // Full paths for the dashboard's Edit (✎) button, which opens the Loot Editor.
        AppendString(sb, "currentLootPath", _settings.CurrentLootPath ?? string.Empty); sb.Append(',');
        AppendString(sb, "vendorProfilePath", _vendorProfilePath?.Invoke() ?? string.Empty); sb.Append(',');
        AppendString(sb, "currentMetaName",
            string.IsNullOrEmpty(_settings.CurrentMetaPath) ? "None" : Path.GetFileNameWithoutExtension(_settings.CurrentMetaPath)); sb.Append(',');
        AppendInt(sb, "selectedNavIdx", _selectedNavIdx); sb.Append(',');
        AppendInt(sb, "selectedLootIdx", _settings.LootProfileIdx); sb.Append(',');
        AppendInt(sb, "selectedMetaIdx", _settings.MetaProfileIdx); sb.Append(',');
        AppendInt(sb, "selectedProfileIdx", Math.Max(0, profilesCopy.IndexOf(_settings.SelectedProfile ?? string.Empty))); sb.Append(',');
        AppendBool(sb, "combatEnabled", _settings.EnableCombat); sb.Append(',');
        AppendBool(sb, "buffingEnabled", _settings.EnableBuffing); sb.Append(',');
        AppendBool(sb, "navigationEnabled", _settings.EnableNavigation); sb.Append(',');
        AppendBool(sb, "lootingEnabled", _settings.EnableLooting); sb.Append(',');
        AppendBool(sb, "metaEnabled", _settings.EnableMeta); sb.Append(',');
        AppendUInt(sb, "currentTargetId", _currentTargetId); sb.Append(',');
        AppendString(sb, "targetLabel", _targetLabel ?? "NO TARGET"); sb.Append(',');
        AppendFloat(sb, "targetHealthPercent", _targetHealthPercent); sb.Append(',');
        AppendString(sb, "targetHealthDisplay", _targetHealthDisplay ?? "0"); sb.Append(',');
        AppendUInt(sb, "targetHealth", _targetHealth); sb.Append(',');
        AppendUInt(sb, "targetMaxHealth", _targetMaxHealth); sb.Append(',');
        AppendUInt(sb, "targetStamina", _targetStamina); sb.Append(',');
        AppendUInt(sb, "targetMaxStamina", _targetMaxStamina); sb.Append(',');
        AppendUInt(sb, "targetMana", _targetMana); sb.Append(',');
        AppendUInt(sb, "targetMaxMana", _targetMaxMana); sb.Append(',');
        AppendUInt(sb, "playerHealth", _playerHealth); sb.Append(',');
        AppendUInt(sb, "playerMaxHealth", _playerMaxHealth); sb.Append(',');
        AppendUInt(sb, "playerStamina", _playerStamina); sb.Append(',');
        AppendUInt(sb, "playerMaxStamina", _playerMaxStamina); sb.Append(',');
        AppendUInt(sb, "playerMana", _playerMana); sb.Append(',');
        AppendUInt(sb, "playerMaxMana", _playerMaxMana); sb.Append(',');
        AppendBool(sb, "showTargetStaminaMana", _settings.ShowTargetStaminaMana); sb.Append(',');
        AppendBool(sb, "isLocked", _isLocked); sb.Append(',');
        AppendBool(sb, "isMinimized", _isMinimized); sb.Append(',');
        AppendFloat(sb, "bgOpacity", _bgOpacity); sb.Append(',');
        // kills/hour, session kills, last-kill age, and free pack slots are bot-derived
        // (kill counter + inventory cache); the rest of the player stats are engine top-level.
        AppendFloat(sb, "killsPerHour", (float)_killsPerHour); sb.Append(',');
        AppendInt(sb, "sessionKills", (int)System.Threading.Interlocked.Read(ref _sessionKills)); sb.Append(',');
        long lastKillTicks = System.Threading.Interlocked.Read(ref _lastKillTicks);
        int secsSinceLastKill = lastKillTicks == 0
            ? -1
            : (int)Math.Clamp((DateTime.UtcNow - new DateTime(lastKillTicks, DateTimeKind.Utc)).TotalSeconds, 0, int.MaxValue);
        AppendInt(sb, "secsSinceLastKill", secsSinceLastKill); sb.Append(',');
        AppendInt(sb, "freeSlots", _freeSlots); sb.Append(',');
        // D2 three-tier target telemetry + D6 attack-cast/kill ratio (orphan early-warning).
        AppendInt(sb, "scanTotal", _scanTotal); sb.Append(',');
        AppendInt(sb, "scanRing", _scanRing); sb.Append(',');
        AppendInt(sb, "scanPossible", _scanPossible); sb.Append(',');
        AppendInt(sb, "scanLosBlocked", _scanLosBlocked); sb.Append(',');
        AppendInt(sb, "sessionAttackCasts", _sessionAttackCasts); sb.Append(',');
        AppendInt(sb, "castsSinceLastKill", _castsSinceLastKill); sb.Append(',');
        int killsForRatio = (int)System.Threading.Interlocked.Read(ref _sessionKills);
        AppendFloat(sb, "castsPerKill", killsForRatio > 0 ? (float)_sessionAttackCasts / killsForRatio : 0f); sb.Append(',');
        AppendBool(sb, "uiHidden", _uiHidden); sb.Append(',');
        AppendInt(sb, "scarabs", _scarabs); sb.Append(',');
        AppendInt(sb, "tapers", _tapers); sb.Append(',');
        // Per-tier scarab breakdown: [{"name":"Lead Scarab","count":N}, ...]
        var byType = _scarabsByType;
        sb.Append("\"scarabsByType\":[");
        for (int i = 0; i < byType.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"name\":\"");
            AppendEscaped(sb, byType[i].Key ?? string.Empty);
            sb.Append("\",\"count\":").Append(byType[i].Value).Append('}');
        }
        sb.Append("],");
        // Equipped gear with full appraisal: name/id/slot + armor/resist/weapon/value/mana/spells/desc.
        var equip = _equipment;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        sb.Append("\"equipment\":[");
        for (int i = 0; i < equip.Length; i++)
        {
            var a = equip[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"name\":\"");
            AppendEscaped(sb, a.Name ?? string.Empty);
            sb.Append("\",\"id\":").Append(a.Id).Append(",\"slot\":").Append(a.Slot);
            sb.Append(",\"armorLevel\":").Append(a.ArmorLevel);
            if (a.Resist is { Length: 7 })
            {
                sb.Append(",\"resist\":[");
                for (int r = 0; r < 7; r++) { if (r > 0) sb.Append(','); sb.Append(a.Resist[r].ToString("0.###", inv)); }
                sb.Append(']');
            }
            sb.Append(",\"value\":").Append(a.Value).Append(",\"burden\":").Append(a.Burden)
              .Append(",\"workmanship\":").Append(a.Workmanship).Append(",\"material\":").Append(a.Material)
              .Append(",\"maxMana\":").Append(a.MaxMana).Append(",\"curMana\":").Append(a.CurMana)
              .Append(",\"damage\":").Append(a.Damage).Append(",\"damageType\":").Append(a.DamageType)
              .Append(",\"weaponDef\":").Append(a.WeaponDef.ToString("0.###", inv))
              .Append(",\"missileDef\":").Append(a.MissileDef.ToString("0.###", inv))
              .Append(",\"magicDef\":").Append(a.MagicDef.ToString("0.###", inv))
              .Append(",\"variance\":").Append(a.Variance.ToString("0.###", inv))
              .Append(",\"elementalMod\":").Append(a.ElementalMod.ToString("0.###", inv));
            sb.Append(",\"spells\":[");
            for (int s = 0; s < a.Spells.Length; s++) { if (s > 0) sb.Append(','); sb.Append('"'); AppendEscaped(sb, a.Spells[s] ?? string.Empty); sb.Append('"'); }
            sb.Append(']');
            sb.Append(",\"longDesc\":\""); AppendEscaped(sb, a.LongDesc ?? string.Empty); sb.Append('"');
            sb.Append('}');
        }
        sb.Append(']');
        // Recent chat lines for the phone chat view: [{"t":"text","c":<chatType>}, ...] (oldest->newest).
        sb.Append(",\"recentChat\":[");
        var chat = _rynthChatUi.SnapshotRecent(60);
        for (int i = 0; i < chat.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"t\":\"");
            AppendEscaped(sb, chat[i].Text ?? string.Empty);
            sb.Append("\",\"c\":").Append(chat[i].Type).Append('}');
        }
        sb.Append(']');
        sb.Append('}');
        return sb.ToString();
    }

    public void TogglePanelMacro() => _settings.IsMacroRunning = !_settings.IsMacroRunning;

    public void SetSubsystemEnabled(int id, bool enabled)
    {
        switch (id)
        {
            case 0: _settings.EnableCombat = enabled; break;
            case 1: _settings.EnableBuffing = enabled; break;
            case 2: _settings.EnableNavigation = enabled; break;
            case 3: _settings.EnableLooting = enabled; break;
            case 4: _settings.EnableMeta = enabled; break;
        }
        SaveSettings();
    }

    /// <summary>
    /// Profile kinds: 0 = nav, 1 = loot, 2 = meta, 3 = settings profile.
    /// Index is into the matching list returned in BuildSnapshotJson.
    /// </summary>
    public void SelectProfileAtIndex(int kind, int index)
    {
        // Deep-audit finding #16 (2026-06-18): this export (poll thread) used
        // to bounds-check and index _navFiles/_lootFiles/_metaFiles/_profiles
        // with no lock at all, racing the Refresh*Files mutators' Clear()/
        // AddRange() on the pump thread — a torn read or
        // ArgumentOutOfRangeException. Snapshot the one list this call needs
        // under _profileListsLock first, mirroring BuildSnapshotJson.
        string? navFile = null, lootFile = null, metaFile = null, profileName = null;
        lock (_profileListsLock)
        {
            switch (kind)
            {
                case 0: if (index >= 0 && index < _navFiles.Count) navFile = _navFiles[index]; break;
                case 1: if (index >= 0 && index < _lootFiles.Count) lootFile = _lootFiles[index]; break;
                case 2: if (index >= 0 && index < _metaFiles.Count) metaFile = _metaFiles[index]; break;
                case 3: if (index >= 0 && index < _profiles.Count) profileName = _profiles[index]; break;
            }
        }

        switch (kind)
        {
            case 0:
                if (navFile != null) { _selectedNavIdx = index; LoadSelectedNav(); }
                break;
            case 1:
                if (lootFile != null)
                {
                    _settings.LootProfileIdx = index;
                    _settings.CurrentLootPath = index == 0 ? string.Empty : Path.Combine(_lootFolder, lootFile);
                    SaveSettings();
                }
                break;
            case 2:
                if (metaFile != null)
                {
                    _settings.MetaProfileIdx = index;
                    string path = index == 0 ? string.Empty : Path.Combine(_metaFolder, metaFile);
                    _metaUi.LoadMacroFile(path);
                    SaveSettings();
                }
                break;
            case 3:
                if (profileName != null) SwitchProfile(profileName);
                break;
        }
    }

    public void RequestForceRebuff() => OnForceRebuffRequested?.Invoke();
    public void RequestCancelForceRebuff() => OnCancelForceRebuffRequested?.Invoke();
    public void AdjustOpacity(float delta) => _bgOpacity = Math.Clamp(_bgOpacity + delta, 0.1f, 1f);
    public void TogglePanelLock() => _isLocked = !_isLocked;
    public void TogglePanelMinimize() { _isMinimized = !_isMinimized; SaveSettings(); }
    public bool IsPanelLocked => _isLocked;
    public bool IsPanelMinimized => _isMinimized;

    private static void AppendString(System.Text.StringBuilder sb, string key, string value)
    {
        sb.Append('"').Append(key).Append("\":\"");
        AppendEscaped(sb, value);
        sb.Append('"');
    }

    private static void AppendBool(System.Text.StringBuilder sb, string key, bool value)
        => sb.Append('"').Append(key).Append("\":").Append(value ? "true" : "false");

    private static void AppendInt(System.Text.StringBuilder sb, string key, int value)
        => sb.Append('"').Append(key).Append("\":").Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static void AppendUInt(System.Text.StringBuilder sb, string key, uint value)
        => sb.Append('"').Append(key).Append("\":").Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static void AppendFloat(System.Text.StringBuilder sb, string key, float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) value = 0f;
        sb.Append('"').Append(key).Append("\":").Append(value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void AppendStringArray(System.Text.StringBuilder sb, string key, IEnumerable<string> values)
    {
        sb.Append('"').Append(key).Append("\":[");
        bool first = true;
        foreach (string v in values)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"');
            AppendEscaped(sb, v ?? string.Empty);
            sb.Append('"');
        }
        sb.Append(']');
    }

    private static void AppendEscaped(System.Text.StringBuilder sb, string value)
    {
        foreach (char c in value)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("X4"));
                    else sb.Append(c);
                    break;
            }
        }
    }

    // ── Meta bridge ───────────────────────────────────────────────────────────

    private static readonly string MetaFolder = @"C:\Games\RynthSuite\RynthAi\MetaFiles";

    // Result of the last set_source, reported in BuildMetaJson (under MetaRulesLock).
    private long _metaApplySeq;
    private bool _metaApplyOk;
    private string _metaApplyText = "";

    private void ReportMetaApply(bool ok, string text)
    {
        _metaApplySeq++;
        _metaApplyOk = ok;
        _metaApplyText = text;
        _host.WriteToChat("[RynthAi] " + text, 1);
    }

    public string BuildMetaJson()
    {
        // Avalonia dispatcher thread — MetaManager.Think enumerates MetaRules on
        // the plugin-tick thread. Snapshot under the shared lock.
        lock (_settings.MetaRulesLock)
        {
        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        AppendBool(sb, "enableMeta", _settings.EnableMeta); sb.Append(',');
        AppendBool(sb, "metaDebug", _settings.MetaDebug); sb.Append(',');
        AppendString(sb, "currentState", _settings.CurrentState ?? "Default"); sb.Append(',');
        AppendString(sb, "currentMetaPath", _settings.CurrentMetaPath ?? ""); sb.Append(',');

        sb.Append("\"rules\":");
        AppendMetaRuleArray(sb, _settings.MetaRules);
        sb.Append(',');

        var files = BuildMetaFileList();
        sb.Append("\"files\":[");
        for (int i = 0; i < files.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"path\":\""); AppendEscaped(sb, files[i].Item1); sb.Append("\",\"display\":\""); AppendEscaped(sb, files[i].Item2); sb.Append("\"}");
        }
        sb.Append("],");

        var states = _settings.MetaRules.Select(r => r.State).Distinct().ToList();
        if (!states.Contains("Default")) states.Add("Default");
        states = states.OrderBy(s => s, System.StringComparer.OrdinalIgnoreCase).ToList();
        AppendStringArray(sb, "states", states); sb.Append(',');

        AppendStringArray(sb, "navFiles", _navFiles); sb.Append(',');
        AppendStringArray(sb, "embeddedNavKeys", _settings.EmbeddedNavs.Keys); sb.Append(',');

        string sourceText = "";
        try { sourceText = AfFileWriter.SaveToString(_settings.MetaRules, _settings.EmbeddedNavs); } catch { }
        AppendString(sb, "sourceText", sourceText);

        // The last Source "Apply" (set_source): the engine shows this instead
        // of assuming success. Seq 0 = nothing applied yet this session.
        sb.Append(",\"applyResult\":{\"seq\":").Append(_metaApplySeq).Append(',');
        AppendBool(sb, "ok", _metaApplyOk); sb.Append(',');
        AppendString(sb, "text", _metaApplyText);
        sb.Append('}');

        // The Meta Manager's rules and timers (built on the plugin tick; see MetaScheduler).
        string? schedule = ScheduleJsonProvider?.Invoke();
        if (!string.IsNullOrEmpty(schedule))
            sb.Append(",\"schedule\":").Append(schedule);

        var snap = MetaSnapshotProvider?.Invoke();
        if (snap != null)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            sb.Append(",\"snapshot\":{");
            AppendBool(sb, "macroRunning", snap.MacroRunning); sb.Append(',');
            AppendBool(sb, "metaEnabled", snap.MetaEnabled); sb.Append(',');
            AppendString(sb, "currentState", snap.CurrentState); sb.Append(',');
            sb.Append("\"secondsInState\":").Append(snap.SecondsInState.ToString("F1", inv)).Append(',');
            sb.Append("\"stackDepth\":").Append(snap.StackDepth).Append(',');
            sb.Append("\"ruleCount\":").Append(snap.RuleCount).Append(',');
            sb.Append("\"stateCount\":").Append(snap.StateCount).Append(',');
            AppendBool(sb, "watchdogActive", snap.WatchdogActive); sb.Append(',');
            AppendString(sb, "watchdogState", snap.WatchdogState); sb.Append(',');
            sb.Append("\"watchdogSecondsRemaining\":").Append(snap.WatchdogSecondsRemaining.ToString("F1", inv)).Append(',');
            AppendString(sb, "lastFiredState", snap.LastFiredState); sb.Append(',');
            AppendString(sb, "lastFiredCondition", snap.LastFiredCondition); sb.Append(',');
            AppendString(sb, "lastFiredAction", snap.LastFiredAction); sb.Append(',');
            sb.Append("\"lastFiredSecondsAgo\":").Append(snap.LastFiredSecondsAgo.ToString("F1", inv)).Append(',');
            AppendString(sb, "lastExprError", snap.LastExprError); sb.Append(',');
            sb.Append("\"lastExprErrorSecondsAgo\":").Append(snap.LastExprErrorSecondsAgo.ToString("F1", inv));
            sb.Append('}');
        }

        sb.Append('}');
        return sb.ToString();
        }
    }

    private List<(string, string)> BuildMetaFileList()
    {
        var result = new List<(string, string)>();
        result.Add(("", "-- None --"));
        try
        {
            System.IO.Directory.CreateDirectory(MetaFolder);
            var entries = new List<(string, string)>();
            foreach (string f in System.IO.Directory.GetFiles(MetaFolder, "*.met"))
            {
                string name = System.IO.Path.GetFileName(f);
                if (!name.StartsWith("--")) entries.Add((f, $"[met] {name}"));
            }
            foreach (string f in System.IO.Directory.GetFiles(MetaFolder, "*.af"))
                entries.Add((f, $"[af]  {System.IO.Path.GetFileName(f)}"));
            entries.Sort((a, b) => string.Compare(a.Item2, b.Item2, System.StringComparison.OrdinalIgnoreCase));
            result.AddRange(entries);
        }
        catch (System.Exception ex)
        {
            // Was a silent catch — surface it (§2.8 philosophy) but don't spam:
            // this only fires on an actual directory/IO failure, not per refresh.
            _host.Log($"[Meta] BuildMetaFileList FAILED for '{MetaFolder}': {ex.GetType().Name}: {ex.Message}");
        }
        return result;
    }

    private static void AppendMetaRuleArray(System.Text.StringBuilder sb, List<MetaRule> rules)
    {
        sb.Append('[');
        for (int i = 0; i < rules.Count; i++)
        {
            if (i > 0) sb.Append(',');
            AppendMetaRule(sb, rules[i]);
        }
        sb.Append(']');
    }

    private static void AppendMetaRule(System.Text.StringBuilder sb, MetaRule r)
    {
        sb.Append('{');
        sb.Append("\"state\":\""); AppendEscaped(sb, r.State ?? "Default"); sb.Append("\",");
        sb.Append("\"condition\":").Append((int)r.Condition).Append(',');
        sb.Append("\"conditionData\":\""); AppendEscaped(sb, r.ConditionData ?? ""); sb.Append("\",");
        sb.Append("\"action\":").Append((int)r.Action).Append(',');
        sb.Append("\"actionData\":\""); AppendEscaped(sb, r.ActionData ?? ""); sb.Append("\",");
        sb.Append("\"children\":"); AppendMetaRuleArray(sb, r.Children ?? new List<MetaRule>()); sb.Append(',');
        sb.Append("\"actionChildren\":"); AppendMetaRuleArray(sb, r.ActionChildren ?? new List<MetaRule>()); sb.Append(',');
        sb.Append("\"enabled\":").Append(r.Enabled ? "true" : "false").Append(',');
        long ms = r.LastFiredAt == DateTime.MinValue ? 99999L : (long)(DateTime.Now - r.LastFiredAt).TotalMilliseconds;
        sb.Append("\"lastFiredMs\":").Append(ms.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sb.Append('}');
    }

    /// <summary>Called from the Avalonia dispatcher (RynthPluginSendMetaCommand).
    /// Queue only — applied on the plugin-tick thread via DrainMetaCommands so
    /// the mutation is serialised with MetaManager.Think rather than racing it.
    /// (Index-vs-id snapshot staleness still needs an engine-side schema change;
    /// deferred while the engine is in release-hold soak.)</summary>
    public void HandleMetaCommand(string json)
    {
        if (!string.IsNullOrWhiteSpace(json)) _metaCmdQueue.Enqueue(json);
    }

    /// <summary>Drains queued meta commands on the plugin-tick thread.</summary>
    public void DrainMetaCommands()
    {
        while (_metaCmdQueue.TryDequeue(out var j)) ApplyMetaCommand(j);
    }

    private void ApplyMetaCommand(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var cmd = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.MetaCommand);
            if (cmd == null) return;

            // The Meta Manager (schedule) owns its own state; not the meta's rules.
            if (cmd.Op.StartsWith("mm_", System.StringComparison.Ordinal))
            {
                ScheduleCommandHandler?.Invoke(cmd);
                return;
            }

            // Avalonia dispatcher thread — every mutating case races
            // MetaManager.Think on the plugin-tick thread.
            lock (_settings.MetaRulesLock)
            switch (cmd.Op)
            {
                case "set_enabled":
                    _settings.EnableMeta = string.Equals(cmd.Value, "true", System.StringComparison.OrdinalIgnoreCase);
                    SaveSettings();
                    break;

                case "set_debug":
                    _settings.MetaDebug = string.Equals(cmd.Value, "true", System.StringComparison.OrdinalIgnoreCase);
                    SaveSettings();
                    break;

                case "set_state":
                    if (!string.IsNullOrEmpty(cmd.Value))
                    {
                        _settings.CurrentState = cmd.Value;
                        _settings.ForceStateReset = true;
                    }
                    break;

                case "load_file":
                    _metaUi.LoadMacroFile(cmd.Path ?? "");
                    {
                        var w = _metaUi.LastLoadWarnings;
                        _host.WriteToChat(
                            $"[RynthAi] Meta load: {_settings.MetaRules.Count} rules from {System.IO.Path.GetFileName(cmd.Path ?? "")}"
                            + (w.Count > 0 ? $" — {w.Count} warning(s): {w[0]}" : ""), 1);
                    }
                    break;

                case "save_file":
                    if (!string.IsNullOrEmpty(cmd.Path))
                    {
                        AfFileWriter.Save(cmd.Path, _settings.MetaRules, _settings.EmbeddedNavs);
                        _settings.CurrentMetaPath = cmd.Path;
                        SaveSettings();
                    }
                    break;

                case "set_source":
                    if (string.IsNullOrEmpty(cmd.Text))
                    {
                        ReportMetaApply(false, "Source not applied: the source is empty");
                        break;
                    }
                    try
                    {
                        var loaded = AfFileParser.LoadFromText(cmd.Text);
                        string w = loaded.Warnings.Count > 0
                            ? $" — {loaded.Warnings.Count} warning(s): {loaded.Warnings[0]}" : "";
                        if (loaded.Rules.Count > 0)
                        {
                            _settings.MetaRules = loaded.Rules;
                            _settings.EmbeddedNavs.Clear();
                            foreach (var kvp in loaded.EmbeddedNavs) _settings.EmbeddedNavs[kvp.Key] = kvp.Value;
                            _settings.ForceStateReset = true;
                            TryAutoSaveMetaCmd();
                            ReportMetaApply(true, $"Applied source: {loaded.Rules.Count} rules{w}");
                        }
                        else
                        {
                            ReportMetaApply(false, $"Source not applied: 0 rules parsed{(w.Length > 0 ? w : " — check syntax")}");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        ReportMetaApply(false, $"Source apply error: {ex.Message}");
                    }
                    break;

                case "add_rule":
                    if (cmd.Rule != null)
                    {
                        _settings.MetaRules.Add(DtoToMetaRule(cmd.Rule));
                        TryAutoSaveMetaCmd();
                    }
                    break;

                case "update_rule":
                    if (cmd.Rule != null && cmd.Index >= 0 && cmd.Index < _settings.MetaRules.Count)
                    {
                        _settings.MetaRules[cmd.Index] = DtoToMetaRule(cmd.Rule);
                        TryAutoSaveMetaCmd();
                    }
                    break;

                case "delete_rule":
                    if (cmd.Index >= 0 && cmd.Index < _settings.MetaRules.Count)
                    {
                        _settings.MetaRules.RemoveAt(cmd.Index);
                        TryAutoSaveMetaCmd();
                    }
                    break;

                case "move_up":
                case "move_down":
                    // Within the rule's state: the panels list rules grouped by
                    // state, so "up" is the previous rule of the SAME state, not
                    // the previous list entry (which may belong to another state).
                    {
                        int other = SameStateNeighbour(cmd.Index, cmd.Op == "move_up" ? -1 : 1);
                        if (other >= 0)
                        {
                            var tmp = _settings.MetaRules[other];
                            _settings.MetaRules[other] = _settings.MetaRules[cmd.Index];
                            _settings.MetaRules[cmd.Index] = tmp;
                            TryAutoSaveMetaCmd();
                        }
                    }
                    break;

                case "duplicate_rule":
                    if (cmd.Rule != null && cmd.Index >= 0 && cmd.Index < _settings.MetaRules.Count)
                    {
                        _settings.MetaRules.Insert(cmd.Index + 1, DtoToMetaRule(cmd.Rule));
                        TryAutoSaveMetaCmd();
                    }
                    break;

                case "set_rule_enabled":
                    if (cmd.Index >= 0 && cmd.Index < _settings.MetaRules.Count)
                    {
                        _settings.MetaRules[cmd.Index].Enabled =
                            string.Equals(cmd.Value, "true", System.StringComparison.OrdinalIgnoreCase);
                        TryAutoSaveMetaCmd();
                    }
                    break;
            }
        }
        catch { }
    }

    /// <summary>The nearest rule before (-1) or after (+1) <paramref name="index"/> in the same state, or -1.</summary>
    private int SameStateNeighbour(int index, int direction)
    {
        var rules = _settings.MetaRules;
        if (index < 0 || index >= rules.Count) return -1;
        string state = rules[index].State ?? "Default";
        for (int j = index + direction; j >= 0 && j < rules.Count; j += direction)
            if (string.Equals(rules[j].State ?? "Default", state, System.StringComparison.Ordinal))
                return j;
        return -1;
    }

    private void TryAutoSaveMetaCmd()
    {
        _settings.MetaRulesStructuralVersion++;   // editor mutation → rebuild meta index
        if (string.IsNullOrEmpty(_settings.CurrentMetaPath)) return;
        try
        {
            string path = _settings.CurrentMetaPath;
            if (System.IO.Path.GetExtension(path).Equals(".met", System.StringComparison.OrdinalIgnoreCase))
            {
                path = System.IO.Path.ChangeExtension(path, ".af");
                _settings.CurrentMetaPath = path;
                _host.WriteToChat($"[RynthAi] Converted .met → {System.IO.Path.GetFileName(path)} for editing (original .met left untouched).", 1);
            }
            lock (_settings.MetaRulesLock)
                AfFileWriter.Save(path, _settings.MetaRules, _settings.EmbeddedNavs);
        }
        catch { }
    }

    private static MetaRule DtoToMetaRule(MetaRuleDto dto)
    {
        var r = new MetaRule
        {
            State         = dto.State ?? "Default",
            Condition     = (MetaConditionType)dto.Condition,
            ConditionData = dto.ConditionData ?? "",
            Action        = (MetaActionType)dto.Action,
            ActionData    = dto.ActionData ?? "",
            Enabled       = dto.Enabled,
        };
        foreach (var c in dto.Children ?? new List<MetaRuleDto>())
            r.Children.Add(DtoToMetaRule(c));
        foreach (var a in dto.ActionChildren ?? new List<MetaRuleDto>())
            r.ActionChildren.Add(DtoToMetaRule(a));
        return r;
    }
}
