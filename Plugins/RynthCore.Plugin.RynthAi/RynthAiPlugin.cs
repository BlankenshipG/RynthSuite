using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.Plugin.RynthAi.Vendor;
using RynthCore.PluginCore;
using RynthCore.Loot;
using RynthCore.Loot.VTank;

namespace RynthCore.Plugin.RynthAi;

/// <summary>Full appraisal (Assess/Identify data) for one equipped item, read off the object cache.</summary>
internal sealed class EquipAppraisal
{
    public string Name = "";
    public uint Id;
    public int Slot;
    public int ArmorLevel;
    public double[] Resist = System.Array.Empty<double>(); // 7: slash,pierce,bludgeon,cold,fire,acid,electric (armor only)
    public int Damage; public int DamageType;
    public double WeaponDef, MissileDef, MagicDef, Variance, ElementalMod;
    public int Value, Burden, Workmanship, Material, MaxMana, CurMana;
    public string[] Spells = System.Array.Empty<string>();
    public string LongDesc = "";
}

/// <summary>One item in the read-only remote full-inventory view (P1). Appraisal is cache-hit-only
/// (never triggers a RequestId) so it is null for the un-identified long tail.</summary>
internal sealed class InventoryItemSnapshot
{
    public uint Id;
    public string Name = "";
    public uint Wcid;
    public int ObjectClass;
    public uint ContainerId;          // BFS parent (player/side-pack); 0 = equipped pseudo-container
    public int Location;              // RAW ownership location bits (container-type + slot) — NOT pre-interpreted
    public int Slot = -1;             // 0-based dense index within the container's contents array
    public int StackCount = 1;
    public uint IconDid;              // PWD _iconID (0x06xxxxxx); 0 if bridge/value unavailable
    public bool Equipped;
    public int WieldedLocation;
    public EquipAppraisal? Appraisal; // only when HasAppraisalData(id); null otherwise
}

/// <summary>One container grouping for the remote inventory view (main pack / a side pack / equipped).</summary>
internal sealed class InventoryContainerSnapshot
{
    public uint Id;
    public string Name = "";
    public string Kind = "main";      // "main" | "side" | "equipped"
    public int Capacity;              // ITEMS_CAPACITY if known, else 0
}

public sealed partial class RynthAiPlugin : RynthPluginBase
{
    internal static readonly IntPtr NamePointer = Marshal.StringToHGlobalAnsi("RynthAi");
    internal static readonly IntPtr VersionPointer = Marshal.StringToHGlobalAnsi(PluginModule.HandWrittenVersion);

    /// <summary>
    /// Oldest engine RynthAi runs on. Players get plugin updates automatically but engine
    /// updates only when they click, so this stays at 66 while newer calls are feature-
    /// detected (Host.HasVendorTrade for API v67 vendor trading: AutoVendor reports "needs a
    /// RynthCore update" without it). The SDK default is CurrentApiVersion, which would make
    /// every SDK bump refuse older engines. Raise it only for a call RynthAi can't run without.
    /// </summary>
    public override uint MinimumApiVersion => 66;

    private LegacyDashboardRenderer? _dashboard;

    /// <summary>Snapshot bridge accessor for engine-side panels.</summary>
    internal LegacyDashboardRenderer? DashboardRenderer => _dashboard;
    private NavigationEngine? _navigationEngine;
    private NavMarkerRenderer? _navMarkerRenderer;
    private RadarWallRenderer? _radarWallRenderer;
    private TerrainPassabilityOverlay? _terrainOverlay;
    private MainLogic? _raycast;
    // The session's _raycast once its background init has finished (null until then), and the
    // gate both the init thread and the session setup take to hand it to the CombatManager.
    private MainLogic? _raycastReady;
    private readonly object _raycastWireGate = new();
    private WorldObjectCache? _objectCache;
    private CharacterSkills? _charSkills;
    private SpellManager? _spellManager;
    private BuffManager? _buffManager;
    private CombatManager? _combatManager;
    private WeaponSwapGate? _weaponSwapGate;
    private MissileCraftingManager? _missileCraftingManager;
    private FellowshipTracker? _fellowshipTracker;
    private MetaManager? _metaManager;
    private QuestTracker? _questTracker;
    private InventoryManager? _inventoryManager;
    private SalvageManager? _salvageManager;
    private ScrollLearner? _scrollLearner;   // VTank ReadUnknownScrolls (Loot/ScrollLearner.cs)
    private ManaStoneManager? _manaStoneManager;
    private PetManager? _petManager;
    private Jumper? _jumper;
    private ActivityArbiter? _arbiter; // STEP 1: shadow-mode only (ACTIVITY_ARBITER_PLAN.md)
    private PlayerVitalsCache _vitals = new();
    private uint _playerId;
    private int _vitalsTickCounter;
    private bool _initialized;
    private bool _loginComplete;
    private bool _patrolOnLoginPending;
    // ── Dungeon-patrol hazard tracking ───────────────────────────────────────
    // A dunnav-patrol route is built once from the hazard cells known at that
    // instant — but lava/acid hotspots are usually sighted only as the bot walks
    // up to them, after the route is already running. These fields let OnTick
    // notice a newly-sighted hazard (HazardVersion bumped) and rebuild the patrol
    // around it, so the route treats the hotspot as a wall instead of looping
    // through it. _dunPatrolLandblock guards against rebuilding after the bot has
    // portalled to a different dungeon.
    private bool _dunPatrolActive;
    private uint _dunPatrolLandblock;
    private int  _dunPatrolHazardVersion;
    private DateTime _notInWorldSince = DateTime.MinValue;
    private int _tickDiag;
    private int _currentCombatMode = 1; // 1=noncombat
    private uint _currentTargetId;
    private VTankLootProfile? _loadedLootProfile;
    private string _loadedLootProfilePath = string.Empty;
    private DateTime _loadedLootProfileTime = DateTime.MinValue;

    // Give queue — drained one item per tick with a cooldown (interval comes from settings)
    private readonly Queue<(uint itemId, uint targetId, int stackSize)> _pendingGives = new();
    private DateTime _lastGiveAt = DateTime.MinValue;
    private RynthCore.Loot.LootProfile? _nativeLootProfile;
    private string _nativeLootProfilePath = string.Empty;
    private DateTime _nativeLootProfileTime = DateTime.MinValue;
    private static bool _imguiResolverConfigured;
    private static bool _objectUsedHooked;

    private CreatureData.CreatureProfileStore? _creatureStore;
    internal CreatureData.CreatureProfileStore? CreatureStore => _creatureStore;
    // Per-character learned combat damage (avg damage by wcid/element/tier +
    // learned HP-to-kill), used by CombatManager for kill-shot prediction.
    private CreatureData.MonsterDamageStore? _damageStore;
    // Aelrynth's difficulty tier where the player stands (and which server this is): keys the
    // tier-scaled learned numbers. Inert (tier 0) on every other server.
    private readonly CreatureData.AwakenedTier _tier = new();
    internal CreatureData.AwakenedTier AwakenedTier => _tier;
    // The tier the Damage panel shows: -1 = the current one (the default).
    private volatile int _damageViewTier = -1;
    // wcids appraised this session (AutoId of nearby mobs). Surfaced as bare rows in the
    // Damage table so monsters populate as you encounter them, before you've fought them.
    private readonly HashSet<uint> _seenMonstersThisSession = new();
    private int _creatureSaveTickCounter;
    private int _settingsSaveTickCounter;
    private int _settingsLoadRetryCounter;

    public override int Initialize()
    {
        // RynthAi draws nothing itself: its windows are the engine's ImGui faces,
        // fed through the C exports in PluginExports.cs. The "dashboard" object is
        // the settings store, sub-managers and the bridge those exports call; its
        // constructor does no ImGui work. (RynthAi no longer exports RynthPluginRender
        // and never reads Host.ImGuiContext.)
        ComponentDatabase.SetLog(msg => Log(msg));
        InitLocalDiagnostics();
        // The overlay windows (OnRenderOverlay) must draw through the engine's cimgui module.
        ImGuiNativeBinding.Ensure();
        _dashboard = new LegacyDashboardRenderer(Host);
        // Every use goes through Host.UseFor (Plugins/Shared/UseAudit.cs): one log line
        // each, and no automatic door/corpse use while the macro is off.
        RynthCore.Plugin.Shared.UseAudit.Reset("RynthAi", () => _dashboard?.Settings.IsMacroRunning == true);
        _objectCache = new WorldObjectCache(Host); // must exist before CreateObject events fire during login
        // Items we use (or use something on) get re-identified before a meta reads them.
        // ObjectUsed is a static event and Shutdown never unsubscribes: when the engine
        // reuses this DLL copy for a new instance, subscribe only once (the handler only
        // touches the static tracker, so one copy serves every instance).
        if (!_objectUsedHooked)
        {
            _objectUsedHooked = true;
            RynthCore.PluginSdk.RynthCoreHost.ObjectUsed += (src, tgt) => { ObjectChangeTracker.MarkChanged(src); ObjectChangeTracker.MarkChanged(tgt); };
        }
        _creatureStore = new CreatureData.CreatureProfileStore();
        try { _creatureStore.Load(); } catch { }
        _damageStore = new CreatureData.MonsterDamageStore();
        Func<string, CreatureData.CreatureProfile?> lookup = ruleName =>
        {
            if (_creatureStore == null || string.IsNullOrEmpty(ruleName)) return null;
            return _creatureStore.TryGetByName(ruleName, out var p) ? p : null;
        };
        _dashboard.CreatureLookupForRules = lookup;
        _initialized = true;
        _loginComplete = false;
        // ⚠ Wording matters: this says nothing about Decal. An older "Decal
        // coexistence mode" line here misled two crash investigations (2026-06-11).
        Log("RynthAi: initialized (the engine's panels draw the UI).");
        return 0;
    }

    public override void Shutdown()
    {
        long t0 = Environment.TickCount64;
        try { _dashboard?.SaveSettings(); } catch { }
        long tAfterSettings = Environment.TickCount64;
        TeardownSession();
        long tAfterTeardown = Environment.TickCount64;
        try { _creatureStore?.SaveIfDirty(); } catch { }
        try { _damageStore?.SaveIfDirty(); } catch { }
        long tAfterStore = Environment.TickCount64;
        _creatureStore = null;
        _damageStore = null;
        _objectCache = null;
        _initialized = false;
        _dashboard = null;
        // Don't let the static audit hold this instance once it's gone (a reused module).
        RynthCore.Plugin.Shared.UseAudit.MacroOn = static () => false;

        // Give the heap back. When a changed RynthAi is deployed, the engine loads
        // it as a new copy and this one is abandoned for good (a NativeAOT DLL can't
        // be unloaded), keeping its raycast geometry: 500-800 MB of acclient's 4 GB
        // per RynthAi deploy (2026-09-29). Everything is dropped above, so a full
        // compacting collection lets the GC release those segments. (When this copy
        // is reused instead, the new instance rebuilds what it needs.)
        long tGc = Environment.TickCount64;
        try
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
        catch { }
        long tAfterGc = Environment.TickCount64;
        try { _translator?.Dispose(); } catch { }
        _translator = null;
        _translateUi = null;
        try { RynthLog.Shutdown(); } catch { }
        Log($"RynthAi: Shutdown done — SaveSettings={tAfterSettings - t0} ms, TeardownSession={tAfterTeardown - tAfterSettings} ms, SaveCreatureStore={tAfterStore - tAfterTeardown} ms, GC={tAfterGc - tGc} ms (heap now {GC.GetGCMemoryInfo().HeapSizeBytes / (1024 * 1024)} MB), total={tAfterGc - t0} ms");
    }

    /// <summary>
    /// Called by the engine when the player leaves the world (RecvNotice_Logoff).
    /// Stops every per-session subsystem the way Shutdown does, but leaves
    /// _initialized / _dashboard / _objectCache intact so the next OnLoginComplete
    /// can rebuild a fresh session without going through plugin Init again.
    /// </summary>
    public override void OnLogout()
    {
        Log("RynthAi: logout — tearing down session.");
        try { _dashboard?.SaveSettings(); } catch { }
        TeardownSession();
    }

    /// <summary>
    /// Releases every component that depends on being in-world. Idempotent.
    /// Used by both Shutdown (full plugin unload) and OnLogout (session-only).
    /// </summary>
    /// <summary>
    /// Hands the session's raycast to the CombatManager once both exist: called by the raycast
    /// init thread when it finishes and by the session setup right after it creates the
    /// CombatManager, whichever comes second does it. Only a finished init is handed over
    /// (GeometryLoader reports initialized before its dungeon wall loader exists, and a
    /// landblock loaded in that gap would be cached without walls).
    /// </summary>
    private void WireRaycastIntoCombat()
    {
        lock (_raycastWireGate)
        {
            var ready = _raycastReady;
            if (ready != null && ReferenceEquals(ready, _raycast))
                _combatManager?.SetRaycastSystem(ready);
        }
    }

    private void TeardownSession()
    {
        ObjectChangeTracker.Clear();
        _navigationEngine?.Stop();
        _navigationEngine = null;
        // The dungeon cell graph is a static single-landblock cache; nothing may outlive the session.
        DungeonPathfinder.InvalidateCache();
        _navMarkerRenderer = null;
        long tFlush0 = Environment.TickCount64;
        _radarWallRenderer?.Flush();
        long tFlushMs = Environment.TickCount64 - tFlush0;
        _radarWallRenderer = null;
        _terrainOverlay = null;
        long tRay0 = Environment.TickCount64;
        _raycast?.Dispose();
        long tRayMs = Environment.TickCount64 - tRay0;
        lock (_raycastWireGate)
        {
            _raycast = null;
            _raycastReady = null;
        }
        _combatManager?.Dispose();
        _combatManager = null;
        Log($"RynthAi: TeardownSession — RadarWallFlush={tFlushMs} ms, RaycastDispose={tRayMs} ms");
        _fellowshipTracker?.Dispose();
        _fellowshipTracker = null;
        // Pending pvar/gvar writes are throttled; write them before the manager goes,
        // or 'setpvar[RunDone,1]' then /logout (or a character swap) loses the value.
        try { _metaManager?.FlushPendingVars(); } catch { }
        _metaManager = null;
        TeardownMetaSchedule();
        _questTracker = null;
        _inventoryManager = null;
        _salvageManager = null;
        _scrollLearner?.Reset();
        _scrollLearner = null;
        _manaStoneManager = null;
        _petManager = null;
        _buffManager?.Dispose();
        _buffManager = null;
        _spellManager = null;
        _missileCraftingManager = null;
        _jumper?.Cancel();
        _jumper = null;
        _autoVendor?.Reset();
        _autoVendor = null;
        _autoTrade?.Reset();
        _autoTrade = null;
        TeardownLocalFeatures(disposeTranslator: false);
        _playerId = 0;
        _loginComplete = false;
        _pendingGives.Clear();
        // A /ub delay armed before logout must not fire into the next character's session.
        _delayedCommands.Clear();
        // The engine drops its appraisal cache at logout; ask for worn gear again next login.
        _equipIdRequested.Clear();
        // Callers save first (Shutdown, OnLogout); after this nothing writes to this
        // character's profile until the next login loads one.
        _dashboard?.ResetCharacterSession();
        // The learned-damage store is per character too: write it, then detach it.
        try { _damageStore?.SaveIfDirty(); _damageStore?.SetCharacter(string.Empty); } catch { }
        // The next login may be another server, or a character in real Dereth.
        try { _creatureStore?.SaveIfDirty(); } catch { }
        _tier.Reset();
        _damageViewTier = -1;
        if (_damageStore != null) _damageStore.Difficulty = 0;
    }

    private DateTime _loginCompletedAt = DateTime.MinValue;

    /// <summary>The player id wasn't readable at OnLoginComplete; hand the real one to every manager.</summary>
    private void ApplyLatePlayerId(uint id)
    {
        _playerId = id;
        _objectCache?.SetPlayerId(id);
        _navigationEngine?.SetPlayerId(id);
        _charSkills?.SetPlayerId(id);
        _spellManager?.SetPlayerId(id);
        _combatManager?.SetPlayerId(id);
        _metaManager?.SetPlayerId(id);
        _autoVendor?.SetPlayerId(id);
        _autoTrade?.SetPlayerId(id);
        if (Host.HasQueryHealth) Host.QueryHealth(id);
        Log($"RynthAi: player id 0x{id:X8} read late (it was 0 at login) - combat, looting and inventory now see it.");
    }

    public override void OnLoginComplete()
    {
        if (!_initialized || _dashboard is null)
            return;

        // _loginComplete is set at the top so the wiring below sees it. An exception
        // part-way used to leave a half-built session ticking (every missing manager a
        // silent ?. no-op: no meta, salvage or vendoring) with one line in the log, and
        // the engine doesn't dispatch login again. Tear it down and say so instead.
        try { OnLoginCompleteCore(); }
        catch (Exception ex)
        {
            Log($"RynthAi: login setup failed - {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            try { TeardownSession(); } catch { }
            try { Host.WriteToChat($"[RynthAi] Couldn't start for this character ({ex.GetType().Name}: {ex.Message}). The bot is off - log out and back in.", 2); } catch { }
        }
    }

    private void OnLoginCompleteCore()
    {
        if (_dashboard is null)
            return;

        _loginCompletedAt = DateTime.Now;
        _loginComplete = true;
        _dashboard.OnLoginComplete();
        _dashboard.ChatSubmitHandler = HandleRynthChatSubmit;
        EnsureTranslator();
        EnsureItemInfoUi();
        _navigationEngine = new NavigationEngine(Host, _dashboard.Settings)
        {
            ChatSubmit = HandleRynthChatSubmit,
            // Jump waypoints go through the UB-style jumper (turn, charge, jump, then
            // it hands navigation back). Forward jump; shift = walking jump.
            Jump = (heading, shift, ms) => _jumper?.Start(shift ? "ws" : "w", heading, ms),
            FindObjectByName = name =>
            {
                if (_objectCache == null || string.IsNullOrEmpty(name)) return 0;
                foreach (var wo in _objectCache.GetLandscapeObjects())
                    if (string.Equals(wo.Name, name, StringComparison.OrdinalIgnoreCase)) return unchecked((uint)wo.Id);
                return 0;
            },
            // Route recovery detours: open closed doors on the way back to the route.
            FindClosedDoorOnLeg = FindClosedDoorOnLeg,
            RequestDoorOpen = RequestDoorOpen,
        };
        // Plans the detours: the dungeon cell graph indoors, RynthNav's navmesh outdoors.
        // Reads the raycast geometry and hazards through the fields, so it follows their
        // (re)creation; dropped with the engine at teardown.
        _navigationEngine.SetRecoveryPlanner(new NavRecoveryPlanner(Host, () => _raycast, () => _objectCache?.GetHazardCells()));
        if (_objectCache != null) _navigationEngine.SetWorldObjectCache(_objectCache);
        _navMarkerRenderer = new NavMarkerRenderer(Host, _dashboard.Settings)
        {
            GeometrySource = () => _raycast is { IsInitialized: true } rc ? rc.GeometryLoader : null,
        };
        CreateNavOverlay();
        _radarWallRenderer = new RadarWallRenderer(Host, _dashboard.Settings);
        _terrainOverlay = new TerrainPassabilityOverlay(Host);
        Log($"RynthAi: NavMarkerRenderer created, HasNav3D={Host.HasNav3D}, version={Host.Version}");

        // Init raycast on background thread — .dat parsing takes ~700ms and blocks the client
        _raycast = new MainLogic();
        var raycastRef = _raycast;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                long rayT0 = Environment.TickCount64;
                // We're injected into acclient.exe; its own directory is the AC
                // install with the dats alongside it — correct on any machine.
                // Only pass it if a portal dat is actually present, else null so
                // GeometryLoader.FindACFolder() falls back to path/registry search.
                string? acDir = null;
                try
                {
                    string? exePath = Environment.ProcessPath;
                    string? exeDir = string.IsNullOrEmpty(exePath)
                        ? null : System.IO.Path.GetDirectoryName(exePath);
                    if (!string.IsNullOrEmpty(exeDir) &&
                        (System.IO.File.Exists(System.IO.Path.Combine(exeDir, "client_portal.dat")) ||
                         System.IO.File.Exists(System.IO.Path.Combine(exeDir, "portal.dat"))))
                        acDir = exeDir;
                }
                catch { }
                bool rayOk = raycastRef.Initialize(acDir);
                long rayMs = Environment.TickCount64 - rayT0;
                Log($"RynthAi: raycast init={rayOk} in {rayMs}ms acDir={acDir ?? "(auto)"} status={raycastRef.StatusMessage}");
                if (rayOk)
                {
                    lock (_raycastWireGate)
                    {
                        // An init from a session already torn down: its geometry is disposed.
                        if (!ReferenceEquals(raycastRef, _raycast)) return;
                        _raycastReady = raycastRef;
                    }
                    // The CombatManager is created further down the session setup. With warm
                    // dat files this init finishes first (~450 ms), _combatManager was still
                    // null here, and the new CombatManager never got the raycast: LOS was
                    // never checked, and "raycast warming up" held off the no-progress
                    // blacklist, for the whole session. The setup wires it too (below).
                    WireRaycastIntoCombat();
                    _dashboard?.SetRaycast(raycastRef);
                    _terrainOverlay?.SetRaycast(raycastRef);
                    _radarWallRenderer?.SetRaycast(raycastRef);
                }
            }
            catch (Exception ex)
            {
                Log($"RynthAi: raycast init error: {ex.Message}");
            }
        });

        // Cache player ID so we can filter self out of creature tracking and update vitals
        _playerId = Host.GetPlayerId();
        _objectCache?.SetPlayerId(_playerId);
        _navigationEngine?.SetPlayerId(_playerId);
        if (_objectCache != null) _dashboard.SetWorldFilter(_objectCache);
        if (_objectCache != null) _dashboard.SetWorldObjectCache(_objectCache);

        // Load per-character settings — character name comes from the player's object name
        if (_playerId != 0 && Host.HasGetObjectName && Host.TryGetObjectName(_playerId, out string charName) && !string.IsNullOrWhiteSpace(charName))
        {
            _dashboard.LoadSettings(charName);
            _patrolOnLoginPending = _dashboard.Settings.PatrolOnLogin;
        }

        // Query own health to get the ratio → derive true MaxHealth immediately
        if (_playerId != 0 && Host.HasQueryHealth)
            Host.QueryHealth(_playerId);

        // Wire combat subsystems
        _vitals = new PlayerVitalsCache();
        _charSkills = new CharacterSkills(Host);
        _charSkills.SetPlayerId(_playerId);
        _spellManager = new SpellManager(Host, _dashboard.Settings);
        _spellManager.SetCharacterSkills(_charSkills);
        _spellManager.SetPlayerId(_playerId);
        _spellManager.InitializeNatively();
        // Shared weapon-swap serializer — both managers consult it so a buff
        // wand-equip and a combat weapon-equip can't fire within ~3s of each
        // other (the collision behind "you can only move or use one item at a
        // time" and the object-teardown AV).
        _weaponSwapGate ??= new WeaponSwapGate();

        _buffManager = new BuffManager(Host, _dashboard.Settings, _spellManager, _vitals);
        _buffManager.SetWeaponSwapGate(_weaponSwapGate);
        _buffManager.SetCastResolvedCallback(OnBuffCastResolved);
        _buffManager.SetCharacterSkills(_charSkills);
        if (_objectCache != null) _buffManager.SetWorldObjectCache(_objectCache);
        // Use the same per-character folder as the dashboard settings so all
        // character data lives in one place and the directory is guaranteed to exist.
        if (!string.IsNullOrEmpty(_dashboard.CharFolder))
        {
            _buffManager.SetTimerPath(_dashboard.CharFolder);
            _damageStore?.SetCharacter(_dashboard.CharFolder);
            lock (_seenMonstersThisSession) _seenMonstersThisSession.Clear();
        }

        // Queued, applied on the pump thread by BuffManager.OnHeartbeat: the ImGui FR button
        // fires on the render thread and the exports on their caller's thread.
        _dashboard.OnForceRebuffRequested       = () => _buffManager?.RequestForceFullRebuff();
        _dashboard.OnCancelForceRebuffRequested = () => _buffManager?.RequestCancelBuffing();

        // Override disk timers with live client memory — gets accurate remaining times
        // including login-restored enchantments the event hook missed at startup.
        int liveCount = _buffManager.RefreshFromLiveMemory();
        if (liveCount >= 0)
            Host.WriteToChat($"[RynthAi] Loaded {liveCount} active buff timer(s) from client memory.", 1);

        // Sync the actual current combat mode — _currentCombatMode defaults to NonCombat and
        // OnCombatModeChange doesn't re-fire on hot-reload, so read it directly from AC memory.
        if (Host.HasGetCurrentCombatMode)
            _currentCombatMode = Host.GetCurrentCombatMode();

        _combatManager = new CombatManager(Host, _dashboard.Settings, _objectCache!, _spellManager);
        _combatManager.SetWeaponSwapGate(_weaponSwapGate);
        _combatManager.SetStaleBusyCallback(OnCombatBusyStale);
        if (_buffManager != null)
            _buffManager.OffhandStowed = id => _combatManager?.NoteOffhandStowed(id);
        _combatManager.SetCharacterSkills(_charSkills);
        _combatManager.SetPlayerId(_playerId);
        _combatManager.SetDamageStores(_creatureStore, _damageStore);
        _combatManager.SetAwakenedTier(_tier);
        // The raycast init thread may already have finished (warm dats): hand it over now.
        WireRaycastIntoCombat();
        _navigationEngine?.SetCombatManager(_combatManager);
        // BuffManager.CheckVitals consults CombatManager.HasCloseThreat to pick
        // between in-combat and idle top-off recharge thresholds. Wire here
        // because BuffManager is constructed before CombatManager.
        _buffManager?.SetCombatManager(_combatManager);

        _missileCraftingManager = new MissileCraftingManager(Host, _dashboard.Settings);
        _missileCraftingManager.SetObjectCache(_objectCache!);
        _missileCraftingManager.SetCharacterSkills(_charSkills);
        _missileCraftingManager.HoldWhile = () => _combatManager?.AmmoSwapInProgress == true;
        _dashboard.SetMissileCraftingManager(_missileCraftingManager);

        _fellowshipTracker?.Dispose();
        _fellowshipTracker = new FellowshipTracker();
        _dashboard?.SetFellowshipTracker(_fellowshipTracker);

        _questTracker = new QuestTracker(Host);
        WireMetaSchedule();       // before the refresh: the Meta Manager shows (doesn't eat) that reply
        _questTracker.Refresh(); // auto-populate quest flags on login

        _metaManager = new MetaManager(_dashboard.Settings, Host, _vitals);
        _metaManager.SetPlayerId(_playerId);
        _metaManager.SetMtCommandHandler(HandleMtCommand);
        _metaManager.SetRaCommandHandler(HandleRaCommand);
        if (_objectCache != null) _metaManager.SetObjectCache(_objectCache);
        _metaManager.SetFellowshipTracker(_fellowshipTracker);
        _metaManager.SetQuestTracker(_questTracker);
        if (_buffManager != null) _metaManager.SetBuffManager(_buffManager);
        _metaManager.SetCreatureStore(_creatureStore);
        if (_dashboard != null)
            _dashboard.MetaSnapshotProvider = () => _metaManager?.GetStateSnapshot();

        if (_objectCache != null)
        {
            _inventoryManager  = new InventoryManager(Host, _dashboard.Settings, _objectCache);
            _manaStoneManager  = new ManaStoneManager(Host, _dashboard.Settings, _objectCache);
            _manaStoneManager.IsLootKept = IsKeptByLootProfile;
            _petManager        = new PetManager(Host, _dashboard.Settings, _objectCache, _combatManager, _charSkills);
            _combatManager.SummonOut = () => _petManager?.CurrentSummon();
        }

        CreateIltHub();
        if (_iltHub?.SkipLoginQuestRefresh == true)
            Log("RynthAi: ILT Hub says /myquests is off on this server; login quest refresh already ran.");
        try
        {
            string hubChar = CurrentCharacterName();
            _iltHub?.OnLoginComplete(hubChar, _petManager);
        }
        catch (Exception ex) { RynthLog.Exception(LogCat.IltHub, ex, "login"); }

        _salvageManager = new SalvageManager(Host, _dashboard.Settings, _objectCache);
        // Hand the salvage manager a live accessor for the loot profile's
        // SalvageCombine config so combining respects per-material workmanship
        // bands when the profile defines them. Prefer the VTank profile (loaded
        // from .utl) but fall back to the native JSON LootProfile if that's the
        // active source.
        _salvageManager.CombineConfigProvider = () =>
            _loadedLootProfile?.SalvageCombine ?? _nativeLootProfile?.SalvageCombine;

        _scrollLearner = CreateScrollLearner(_dashboard.Settings);

        _jumper = new Jumper(Host, _dashboard.Settings, s => Host.WriteToChat(s, 1));

        if (_objectCache != null)
        {
            var dashForAv = _dashboard;
            _autoVendor = new AutoVendorManager(Host, _dashboard.Settings, _objectCache, _playerId,
                () => dashForAv?.CharFolder ?? string.Empty);
            var avForUi = _autoVendor;
            _dashboard.SetVendorProfilePathProvider(() => avForUi.OpenVendorProfilePath);

            _autoTrade = new Trade.AutoTradeManager(Host, _dashboard.Settings, _objectCache, _playerId,
                () => dashForAv?.CharFolder ?? string.Empty);
        }

        Log("RynthAi: login complete, legacy ImGui dashboard ready.");
    }

    private bool _combatDbgActive = false;
    private int _combatDbgFrames = 0;
    // STEP 5 (ACTIVITY_ARBITER_PLAN.md): the four hand-managed nav pause flags
    // (_buffingPausedNav / _combatPausedNav / _corpsePausedNav and their reset
    // fan-out) are gone. Nav is stopped by StopNavFor() on the single transition
    // away from Navigating, and the arbiter's decision is the only thing that
    // decides whether nav runs at all. One edge-detect replaces the cascade.
    private BotActivity _activity = BotActivity.Idle;   // last arbiter decision
    private bool _macroWasRunning;                      // edge: release held turns when the macro stops
    private BotActivity _navStoppedFor = BotActivity.Idle; // activity we last issued a nav Stop() for
    private bool _navStopIssued;
    // Buffing-coma watchdog (see the Priority-1 block): how long BotAction has
    // been continuously 'Buffing', and the once-per-interval recovery bypass.
    private DateTime _buffingHeldSince = DateTime.MinValue;
    private DateTime _lastBuffComaBypassAt = DateTime.MinValue;
    private bool _buffComaWarned;
    private const double BuffComaThresholdMs = 5 * 60_000;  // 5 min continuous Buffing = pathological
    private const double BuffComaBypassEveryMs = 10_000;    // let recovery tick through every ~10s
    private const double BuffComaIdleMs = 90_000;           // ...but only when buffing hasn't cast for this long
    // Loot grace. NOT a pause flag: corpse CreateObject events arrive a tick or
    // two after the kill, so between "combat ended" and "corpse exists in the
    // cache" there is a window where nothing wants to loot and nav would walk
    // away from the body. This timestamp feeds the PURE WantLooting input
    // (HasLootWork) rather than gating nav directly, so it is data the decision
    // is computed from, not a lock some subsystem has to remember to release.
    private long _combatEndedAt;
    private const long LootGraceMs = 2000;

    /// <summary>
    /// Stop nav movement once, on the transition into a non-Navigating activity.
    /// Replaces the four _*PausedNav flags: each existed only to answer "have I
    /// already issued the Stop() for this owner?", and each had to be reset from
    /// every other branch of the cascade — that reset fan-out is how a stale flag
    /// stranded the bot. One field, one question, cleared in exactly one place
    /// (the nav-owns-the-tick branch in OnTick).
    /// </summary>
    private void StopNavFor(BotActivity owner)
    {
        if (_navStopIssued && _navStoppedFor == owner)
            return;

        _navStopIssued = true;
        _navStoppedFor = owner;

        _navigationEngine?.Stop();
        if (Host.HasStopCompletely)
            Host.StopCompletely();

        // Combat taking the tick invalidates any in-progress door interaction.
        // The legacy cascade did this on the combat edge only; keep it there.
        if (owner == BotActivity.Combat)
            ResetDoorState();
    }
    /// <summary>
    /// The holds below (AutoVendor, AutoTrade, Buffing, missile crafting) return before
    /// CombatManager.OnHeartbeat, which is what refreshes the target scan. A frozen scan
    /// kept BuffManager's "in combat" test (HasCloseThreat) on its pre-hold answer, so a
    /// mob that walked up during a top-off never switched the vitals to the combat
    /// thresholds. The scan sends no game commands; this is the same call OnHeartbeat makes.
    /// </summary>
    private void KeepThreatScanFresh(LegacyUiSettings s)
    {
        if (!s.IsMacroRunning || !s.EnableCombat || _combatManager == null) return;
        try { _combatManager.ScanNearbyTargets(); }
        catch (Exception ex) { Host.Log($"[RynthAi] ScanNearbyTargets (hold) CRASH: {ex.Message}"); }
    }

    private DateTime _lastFreeSlotsPushAt = DateTime.MinValue; // throttle the status-feed pack-slots compute
    private DateTime _lastCombatTelemetryAt = DateTime.MinValue; // throttle the D2/D6 combat-telemetry push+log
    private int _lastCombatTelemetrySig = int.MinValue;          // change key so a static-mob wedge logs once, not every tick

    // Remote "Hide UI": suppress vanilla radar/chat/powerbar (re-asserted each tick in the OnTick settings
    // push). Set by the forwarded "hideui" command (ApplyRemoteCommand). The phone command-file drain and
    // manual movement moved out to the private RynthRemote plugin (Phase B/D); RynthAi only RECEIVES the
    // non-movement commands RynthRemote forwards (via the engine broker → _forwardedRemoteCommands).
    private bool _hideUi;

    // Remote commands forwarded from the RynthRemote plugin via the engine's SendPluginCommand broker
    // arrive on the broker's calling thread; queue them and apply on OUR pump thread (OnTick) so AC
    // mutation never happens off-thread — the same discipline LegacyDashboardRenderer.DrainMetaCommands uses.
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string action, string value)> _forwardedRemoteCommands = new();

    /// Enqueue a remote command forwarded by the engine broker (the
    /// RynthPluginApplyRemoteCommand export). Thread-safe; the queued command is applied
    /// on the next OnTick on the pump thread via the existing ApplyRemoteCommand switch.
    internal void EnqueueRemoteCommand(string action, string value)
    {
        if (string.IsNullOrEmpty(action)) return;
        _forwardedRemoteCommands.Enqueue((action, value ?? string.Empty));
    }

    // /ra pause <sec> deadline (Environment.TickCount64 ms); 0 = no pause armed.
    private long _macroResumeAt;

    /// Map a remote command to the bot's existing control surface. All targets are SafeInvoke-grade
    /// (managed flags / file I/O) or proven pump-safe (clearbusy = Host.ForceResetBusyCount, same call
    /// the busy watchdog makes from this thread). Idempotent set-semantics for toggles.
    private void ApplyRemoteCommand(string action, string value)
    {
        var dash = _dashboard;
        if (dash == null) return;
        bool on = value is "1" or "true" or "on" or "True" or "TRUE"
                  || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                  || value.Equals("on", StringComparison.OrdinalIgnoreCase);
        switch (action.ToLowerInvariant())
        {
            case "macro":
                if (on && !dash.Settings.IsMacroRunning && RefuseMacroStartForVTank()) break;   // one bot per client
                if (dash.Settings.IsMacroRunning != on) dash.TogglePanelMacro();
                break;
            case "combat":     dash.SetSubsystemEnabled(0, on); break;
            case "buffing":    dash.SetSubsystemEnabled(1, on); break;
            case "navigation": dash.SetSubsystemEnabled(2, on); break;
            case "looting":    dash.SetSubsystemEnabled(3, on); break;
            case "meta":       dash.SetSubsystemEnabled(4, on); break;
            case "navprofile":      if (int.TryParse(value, out int ni)) dash.SelectProfileAtIndex(0, ni); break;
            case "lootprofile":     if (int.TryParse(value, out int li)) dash.SelectProfileAtIndex(1, li); break;
            case "metaprofile":     if (int.TryParse(value, out int mi)) dash.SelectProfileAtIndex(2, mi); break;
            case "settingsprofile": if (int.TryParse(value, out int si)) dash.SelectProfileAtIndex(3, si); break;
            case "forcerebuff":  dash.RequestForceRebuff(); break;
            case "cancelrebuff": dash.RequestCancelForceRebuff(); break;
            case "clearbusy":    HandleClearBusyCommand(); break;
            case "hideui":       _hideUi = on; dash.SetUiHidden(on); break;  // applied each tick in OnTick
            case "sendchat":     if (!string.IsNullOrEmpty(value)) HandleRynthChatSubmit(value); break;
            case "setsetting":   ApplyRemoteSetting(value); break;   // one advanced setting from the phone (clamped + persisted)
            case "hub":
            case "quests":
            case "pets":
            case "guardian":
                // Dashboard Char launcher (hub=show opens the Mini Remote) and its right-click menu
                // (hub=open <section> toggle); value carries the "/ra hub|quests|pets" arguments.
                if (_iltHub == null) { ChatLine("[RynthAi] ILT Hub not ready (log in first)."); break; }
                _iltHub.HandleCommand(action.ToLowerInvariant(), value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                break;
            case "huds":
            case "itemhud":
            case "remote":
            case "miniremote":
                // Dashboard Hub launcher: left-click remote=toggle, right-click huds=show.
                HandleHudCommand(action.ToLowerInvariant(), value);
                break;
            case "itemhudadd":
                // Inventory panel "Add to item count HUD"; value is the item name.
                HandleItemHudAdd(value);
                break;
            case "remoteslot":
                // Inventory panel "Add to Mini Remote"; value is "<slot 1-30|first> <objectId>".
                if (_huds == null) { ChatLine("[RynthAi] HUDs not ready (log in first)."); break; }
                if (!_huds.HandleRemoteSlot(value)) Host.Log($"[RynthAi] bad remoteslot value: {value}");
                break;
            case "prog":
                // Skills panel Progression tab (augmentations / enlightenment edits).
                HandleProgressionRemote(value);
                break;
            case "trace":
            case "logs":
            case "iteminfo":
                // Settings panel buttons (Diagnostics tab, Item Info options); value carries the
                // same arguments as "/ra trace|logs|iteminfo ...".
                {
                    var args = new List<string> { "/ra", action.ToLowerInvariant() };
                    args.AddRange(value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                    string[] parts = args.ToArray();
                    switch (parts[1])
                    {
                        case "trace":    HandleTraceCommand(parts); break;
                        case "logs":     HandleLogsCommand(parts); break;
                        case "iteminfo": HandleItemInfoCommand(parts); break;
                    }
                }
                break;
            // movestart/movestop are applied DIRECTLY by the RynthRemote plugin (pure Host.SetAutoRun/
            // SetMotion + its own dead-man watchdog) and are never forwarded here.
            default:
                Host.Log($"[RynthAi] ignored unknown remote command: {action}={value}");
                return;
        }
        Host.Log($"[RynthAi] applied remote command: {action}={value}");
    }

    // Settings the phone must never write (engine-populated read-only status).
    private static readonly HashSet<string> ReadOnlySettingKeys = new(StringComparer.OrdinalIgnoreCase)
        { "MissileCraftingState", "MissileCraftingActive", "MissileCraftingStatus", "DiagFolder" };

    // Authoritative server-side clamp (min,max) per numeric setting — a bad/garbage phone value can never
    // push a setting out of range and brick a client. Ranges mirror the in-AC SettingsPanel rows. Booleans
    // aren't listed (pass through); enums are clamped to their 0..N index range.
    private static readonly Dictionary<string, (double Min, double Max)> SettingClamp = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TargetFPSFocused"] = (10, 240), ["TargetFPSBackground"] = (5, 60),
        ["BlacklistAttempts"] = (1, 20), ["BlacklistTimeoutSec"] = (5, 120), ["BlacklistCastSettleMs"] = (500, 5000),
        ["TargetNoProgressTimeoutSec"] = (0, 300), ["GiveQueueIntervalMs"] = (50, 2000),
        ["BowArcVelocity"] = (10, 60), ["CrossbowArcVelocity"] = (10, 80), ["AtlatlArcVelocity"] = (10, 60), ["MagicArcVelocity"] = (10, 60), ["MissileArcClearance"] = (0, 3),
        ["HealAt"] = (0, 100), ["EmergencyHealAt"] = (0, 100),
        ["StaminaToHealthAt"] = (0, 100), ["StaminaToHealthMinStamina"] = (0, 100), ["RestamAt"] = (0, 100), ["GetManaAt"] = (0, 100),
        ["TopOffHP"] = (0, 100), ["TopOffStam"] = (0, 100), ["TopOffMana"] = (0, 100),
        ["HealOthersAt"] = (0, 100), ["RestamOthersAt"] = (0, 100), ["InfuseOthersAt"] = (0, 100),
        ["MeleeAttackPower"] = (-1, 100), ["MissileAttackPower"] = (-1, 100),
        ["MeleeAttackHeight"] = (0, 2), ["MissileAttackHeight"] = (0, 2),
        ["PetMinMonsters"] = (1, 20),
        ["SpellCastIntervalMs"] = (100, 1500), ["AttackSpellIntervalMs"] = (250, 5000), ["MinRingTargets"] = (1, 20), ["MinBlastTargets"] = (1, 20), ["BlastRange"] = (0, 100),
        ["MinSkillLevelTier1"] = (1, 500), ["MinSkillLevelTier2"] = (1, 500), ["MinSkillLevelTier3"] = (1, 500), ["MinSkillLevelTier4"] = (1, 500),
        ["MinSkillLevelTier5"] = (1, 500), ["MinSkillLevelTier6"] = (1, 500), ["MinSkillLevelTier7"] = (1, 500), ["MinSkillLevelTier8"] = (1, 500),
        ["MonsterRange"] = (1, 200), ["MonsterDisengageRange"] = (0, 200), ["RingRange"] = (1, 50), ["ApproachRange"] = (1, 50),
        ["CorpseApproachRangeMax"] = (0.5, 50), ["CorpseApproachRangeMin"] = (0.5, 20),
        ["FollowNavMin"] = (0.5, 20), ["NavRingThickness"] = (1, 16), ["NavLineThickness"] = (1, 16),
        ["NavHeightOffset"] = (-5, 5), ["NavSlopeSink"] = (0, 8), ["OpenDoorRange"] = (0.1, 70), ["MovementMode"] = (0, 0),
        ["NavStopTurnAngle"] = (1, 90), ["NavResumeTurnAngle"] = (1, 45), ["NavDeadZone"] = (0.5, 20), ["NavSweepMult"] = (0.5, 10),
        ["NavLookaheadYards"] = (0, 30), ["NavShortcutYards"] = (0, 10), ["NavTurnRateDegPerSec"] = (30, 720), ["NavTier1TurnSpeed"] = (0.5, 15), ["PostPortalDelaySec"] = (0, 30),
        ["NavOffTrackYards"] = (160, 1000), ["NavMaxDetourAttempts"] = (1, 10),
        ["T2Speed"] = (0.1, 5), ["T2WalkWithinYd"] = (1, 50), ["T2DistanceTo"] = (0.1, 10), ["T2ReissueMs"] = (100, 10000),
        ["T2MaxRangeYd"] = (50, 2000), ["T2MaxLandblocks"] = (1, 20),
        ["RebuffSecondsRemaining"] = (30, 1800), ["RebuffTopOffSecondsRemaining"] = (30, 3600),
        ["AutoVendorTries"] = (1, 20), ["AutoVendorTriesTime"] = (500, 30000),
        ["BuffMinSkillLevelTier1"] = (1, 500), ["BuffMinSkillLevelTier2"] = (1, 500), ["BuffMinSkillLevelTier3"] = (1, 500), ["BuffMinSkillLevelTier4"] = (1, 500),
        ["BuffMinSkillLevelTier5"] = (1, 500), ["BuffMinSkillLevelTier6"] = (1, 500), ["BuffMinSkillLevelTier7"] = (1, 500), ["BuffMinSkillLevelTier8"] = (1, 500),
        ["LootJumpHeight"] = (1, 100), ["LootOwnership"] = (0, 2),
        ["LootInterItemDelayMs"] = (0, 5000), ["LootContentSettleMs"] = (0, 5000), ["LootEmptyCorpseMs"] = (0, 5000), ["LootClosingDelayMs"] = (0, 5000),
        ["LootAssessWindowMs"] = (0, 5000), ["LootRetryTimeoutMs"] = (0, 10000), ["LootOpenRetryMs"] = (0, 10000), ["LootCorpseTimeoutMs"] = (0, 60000),
        ["SalvageOpenDelayFirstMs"] = (0, 5000), ["SalvageOpenDelayFastMs"] = (0, 2000), ["SalvageAddDelayFirstMs"] = (0, 5000), ["SalvageAddDelayFastMs"] = (0, 2000),
        ["SalvageSalvageDelayMs"] = (0, 2000), ["SalvageResultDelayFirstMs"] = (0, 5000), ["SalvageResultDelayFastMs"] = (0, 2000),
    };

    // Apply ONE advanced setting sent from the phone as JSON {"key":"HealAt","value":55}. Validates the key
    // against the live settings shape, blocks read-only + buffing-OFF, CLAMPS numerics, then patches the
    // current settings JSON and feeds it through the proven ApplySettingsJson round-trip (which persists via
    // SaveSettings + hot-applies). Pure JSON manipulation (no reflection) — NativeAOT-safe. Never throws.
    private void ApplyRemoteSetting(string valueJson)
    {
        var dash = _dashboard;
        if (dash == null || string.IsNullOrWhiteSpace(valueJson)) return;
        try
        {
            string? key; bool isBool = false, boolVal = false, isInt = false; long longVal = 0; double dblVal = 0;
            using (var doc = System.Text.Json.JsonDocument.Parse(valueJson))
            {
                var root = doc.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return;
                if (!root.TryGetProperty("key", out var ke) || ke.ValueKind != System.Text.Json.JsonValueKind.String) return;
                key = ke.GetString();
                if (string.IsNullOrEmpty(key)) return;
                if (!root.TryGetProperty("value", out var ve)) return;
                switch (ve.ValueKind)
                {
                    case System.Text.Json.JsonValueKind.True:  isBool = true; boolVal = true; break;
                    case System.Text.Json.JsonValueKind.False: isBool = true; boolVal = false; break;
                    case System.Text.Json.JsonValueKind.Number:
                        if (ve.TryGetInt64(out longVal)) { isInt = true; dblVal = longVal; }
                        else dblVal = ve.GetDouble();
                        break;
                    default: return;   // no writable string/null settings
                }
            }
            if (ReadOnlySettingKeys.Contains(key)) { Host.Log($"[RynthAi] setSetting rejected read-only '{key}'"); return; }
            if (key.Equals("EnableBuffing", StringComparison.OrdinalIgnoreCase) && isBool && !boolVal)
            { Host.Log("[RynthAi] setSetting BLOCKED: EnableBuffing OFF from remote (turn off in-game)"); return; }

            var obj = System.Text.Json.Nodes.JsonNode.Parse(dash.BuildSettingsJson())?.AsObject();
            if (obj == null || !obj.ContainsKey(key)) { Host.Log($"[RynthAi] setSetting unknown key '{key}'"); return; }

            object applied;
            if (isBool) { obj[key] = boolVal; applied = boolVal; }
            else
            {
                double v = SettingClamp.TryGetValue(key, out var rng) ? Math.Clamp(dblVal, rng.Min, rng.Max) : dblVal;
                if (isInt) { long lv = (long)Math.Round(v); obj[key] = lv; applied = lv; }   // int field → integer token
                else { obj[key] = v; applied = v; }                                          // float/double field → decimal
            }
            dash.ApplySettingsJson(obj.ToJsonString());
            Host.Log($"[RynthAi] setSetting {key}={applied}");
        }
        catch (Exception ex) { Host.Log($"[RynthAi] setSetting error: {ex.Message}"); }
    }

    // ── Full item appraisal (the Assess/Identify data) for equipped gear ──────────────────────────
    // The bot already auto-identifies objects (engine AutoIdService) and caches the 0xC9 appraisal;
    // every field below is a cache-backed read, safe on the pump thread. Property IDs are ACE STypes.
    private static readonly uint[] ResistStypes = { 13, 14, 15, 16, 17, 18, 19 }; // slash,pierce,bludgeon,cold,fire,acid,electric

    // Equipped item ids we've already asked the server to appraise (once each; gear rarely changes).
    private readonly HashSet<uint> _equipIdRequested = new();

    /// Read the full appraisal for each equipped item (name/id/slot already known) off the object cache.
    /// AutoIdService doesn't identify the player's OWN worn gear, so the appraisal cache is empty for it
    /// until we ask: fire Host.RequestId() for un-appraised items (same marshalled path the loot eval uses
    /// for corpse items). Capped per pass so we never burst the client busy-count.
    private List<EquipAppraisal> AppraiseEquipment(List<(string Name, uint Id, int Slot)> equip)
    {
        var cache = _objectCache;
        var outList = new List<EquipAppraisal>(equip?.Count ?? 0);
        if (cache == null || equip == null) return outList;
        int idRequests = 0;
        foreach (var e in equip)
        {
            int id = unchecked((int)e.Id);
            if (Host.HasRequestId && idRequests < 3
                && (!Host.HasHasAppraisalData || !Host.HasAppraisalData(e.Id))
                && _equipIdRequested.Add(e.Id))
            {
                Host.RequestId(e.Id);   // appraisal 0xC9 lands in the engine cache; read it next pass
                idRequests++;
            }
            var a = new EquipAppraisal { Name = e.Name, Id = e.Id, Slot = e.Slot };
            a.ArmorLevel  = cache.GetIntProperty(id, 28, 0);
            a.Value       = cache.GetIntProperty(id, 19, 0);
            a.Burden      = cache.GetIntProperty(id, 5, 0);
            a.Workmanship = cache.GetIntProperty(id, 105, 0);
            a.Material    = cache.GetIntProperty(id, 131, 0);
            a.MaxMana     = cache.GetIntProperty(id, 108, 0);
            a.CurMana     = cache.GetIntProperty(id, 107, 0);
            a.Damage      = cache.GetIntProperty(id, 44, 0);
            a.DamageType  = cache.GetIntProperty(id, 45, 0);
            a.WeaponDef   = cache.GetDoubleProperty(id, 29, 0);
            a.MissileDef  = cache.GetDoubleProperty(id, 149, 0);
            a.MagicDef    = cache.GetDoubleProperty(id, 150, 0);
            a.Variance    = cache.GetDoubleProperty(id, 22, 0);
            a.ElementalMod = cache.GetDoubleProperty(id, 152, 0);
            if (a.ArmorLevel > 0)
            {
                a.Resist = new double[7];
                for (int i = 0; i < 7; i++) a.Resist[i] = cache.GetDoubleProperty(id, ResistStypes[i], 1.0);
            }
            a.LongDesc = cache.GetStringProperty(id, 16, string.Empty);
            // Item enchantments (banes/Impen/etc.) by name.
            if (Host.HasGetObjectSpellIds)
            {
                uint[] buf = new uint[64];
                int n = Host.GetObjectSpellIds(e.Id, buf, buf.Length);
                if (n > 0)
                {
                    int take = Math.Min(n, buf.Length);
                    var names = new List<string>(take);
                    for (int i = 0; i < take; i++)
                    {
                        var info = SpellTableStub.GetById((int)buf[i]);
                        if (info != null && !string.IsNullOrEmpty(info.Name)) names.Add(info.Name);
                    }
                    a.Spells = names.ToArray();
                }
            }
            outList.Add(a);
        }
        return outList;
    }

    /// Fill the appraisal fields for an inventory item from the object cache. CACHE-HIT-ONLY —
    /// the caller gates on Host.HasAppraisalData; this NEVER fires RequestId (busy-count invariant,
    /// §4.3). Mirrors AppraiseEquipment's field reads (Name/Slot are carried on the item, not here).
    private EquipAppraisal BuildInventoryAppraisal(WorldObjectCache cache, int id)
    {
        var a = new EquipAppraisal { Id = unchecked((uint)id) };
        a.ArmorLevel  = cache.GetIntProperty(id, 28, 0);
        a.Value       = cache.GetIntProperty(id, 19, 0);
        a.Burden      = cache.GetIntProperty(id, 5, 0);
        a.Workmanship = cache.GetIntProperty(id, 105, 0);
        a.Material    = cache.GetIntProperty(id, 131, 0);
        a.MaxMana     = cache.GetIntProperty(id, 108, 0);
        a.CurMana     = cache.GetIntProperty(id, 107, 0);
        a.Damage      = cache.GetIntProperty(id, 44, 0);
        a.DamageType  = cache.GetIntProperty(id, 45, 0);
        a.WeaponDef   = cache.GetDoubleProperty(id, 29, 0);
        a.MissileDef  = cache.GetDoubleProperty(id, 149, 0);
        a.MagicDef    = cache.GetDoubleProperty(id, 150, 0);
        a.Variance    = cache.GetDoubleProperty(id, 22, 0);
        a.ElementalMod = cache.GetDoubleProperty(id, 152, 0);
        if (a.ArmorLevel > 0)
        {
            a.Resist = new double[7];
            for (int i = 0; i < 7; i++) a.Resist[i] = cache.GetDoubleProperty(id, ResistStypes[i], 1.0);
        }
        a.LongDesc = cache.GetStringProperty(id, 16, string.Empty);
        if (Host.HasGetObjectSpellIds)
        {
            uint[] buf = new uint[64];
            int n = Host.GetObjectSpellIds(unchecked((uint)id), buf, buf.Length);
            if (n > 0)
            {
                int take = Math.Min(n, buf.Length);
                var names = new List<string>(take);
                for (int i = 0; i < take; i++)
                {
                    var info = SpellTableStub.GetById((int)buf[i]);
                    if (info != null && !string.IsNullOrEmpty(info.Name)) names.Add(info.Name);
                }
                a.Spells = names.ToArray();
            }
        }
        return a;
    }

    /// Build the read-only full-inventory snapshot for the remote viewer and publish it to the dashboard.
    /// Runs on the SAME ~3s gate as equipment; reuses GetDirectInventory's copy-snapshot (off-thread-safe,
    /// 1s cooldown re-serves the cache if called hot). NEVER fires RequestId — appraisal is cache-hit-only.
    private void ScanInventoryForRemote()
    {
        var cache = _objectCache;
        var dash = _dashboard;
        if (cache == null || dash == null) return;
        uint playerId = _playerId;
        if (playerId == 0) return;

        IReadOnlyList<WorldObject> inv;
        try { inv = cache.GetDirectInventory(); }
        catch { return; }

        bool hasIcon = Host.HasGetObjectDataIdProperty;
        bool hasWcid = Host.HasGetObjectWcid;
        bool hasOwn  = Host.HasGetObjectOwnershipInfo;
        bool hasAppr = Host.HasHasAppraisalData;

        var items = new List<InventoryItemSnapshot>(inv.Count);
        var idToName = new Dictionary<uint, string>(inv.Count);

        foreach (var wo in inv)
        {
            int id = wo.Id;
            uint uid = unchecked((uint)id);
            idToName[uid] = wo.Name ?? string.Empty;

            bool equipped = wo.WieldedLocation > 0;
            // Equipped items merge in without a BFS parent → group them under the "Equipped"
            // pseudo-container (id 0); everything else keeps its authoritative BFS parent.
            uint containerId = equipped ? 0u : unchecked((uint)wo.DirectContainerId);

            int location = 0;
            if (hasOwn && Host.TryGetObjectOwnershipInfo(uid, out _, out _, out uint loc))
                location = unchecked((int)loc);

            uint wcid = 0;
            if (hasWcid) Host.TryGetObjectWcid(uid, out wcid);

            uint icon = 0;
            if (hasIcon) Host.TryGetObjectDataIdProperty(uid, 8u, out icon);   // Icon=8

            int stack = cache.GetIntProperty(id, 12, 1);   // STACK_SIZE; 1 for non-stackables
            if (stack < 1) stack = 1;

            EquipAppraisal? appr = null;
            if (hasAppr && Host.HasAppraisalData(uid))
                appr = BuildInventoryAppraisal(cache, id);

            items.Add(new InventoryItemSnapshot
            {
                Id = uid,
                Name = wo.Name ?? string.Empty,
                Wcid = wcid,
                ObjectClass = (int)wo.ObjectClass,
                ContainerId = containerId,
                Location = location,
                Slot = wo.DirectSlot,
                StackCount = stack,
                IconDid = icon,
                Equipped = equipped,
                WieldedLocation = wo.WieldedLocation,
                Appraisal = appr,
            });
        }

        // Containers: equipped pseudo-container (if any) → main pack → each referenced side pack.
        var containers = new List<InventoryContainerSnapshot>();
        if (items.Exists(it => it.Equipped))
            containers.Add(new InventoryContainerSnapshot { Id = 0, Name = "Equipped", Kind = "equipped", Capacity = 0 });
        containers.Add(new InventoryContainerSnapshot
        {
            Id = playerId,
            Name = "Main Pack",
            Kind = "main",
            Capacity = cache.GetIntProperty(unchecked((int)playerId), 6, 0),   // ITEMS_CAPACITY
        });
        var seen = new HashSet<uint> { 0u, playerId };
        foreach (var it in items)
        {
            uint cid = it.ContainerId;
            if (!seen.Add(cid)) continue;
            idToName.TryGetValue(cid, out string? pname);   // a side pack is itself an item in the list
            containers.Add(new InventoryContainerSnapshot
            {
                Id = cid,
                Name = string.IsNullOrEmpty(pname) ? "Side Pack" : pname,
                Kind = "side",
                Capacity = cache.GetIntProperty(unchecked((int)cid), 6, 0),
            });
        }

        dash.SetInventory(containers, items);
    }

    // Core scarab tiers, seeded so a fully-depleted tier still reports 0 (the "you're OUT" alert the
    // user wants) instead of silently vanishing. Any OTHER "...Scarab" stack (higher tier, Mana Scarab,
    // etc.) is captured dynamically by its real in-game name, so the list is correct by construction.
    private static readonly string[] CoreScarabTiers =
        { "Lead Scarab", "Iron Scarab", "Copper Scarab", "Silver Scarab", "Gold Scarab", "Pyreal Scarab" };

    /// One inventory-cache pass (no live AC read — safe on the off-thread pump) for the status feed:
    ///   freeSlots = empty main-pack slots (same rule as VTankLootEvaluator.GetMainPackEmptySlots:
    ///               direct main-pack items that aren't sub-containers, foci, or equipped; 102 = cap).
    ///   scarabs/tapers = casting-component tallies anywhere in inventory (main + side packs), so the
    ///               phone shows when a bot is about to run out of components and stop casting.
    ///   scarabsByType = per-tier scarab counts (seeded core tiers + any others found), ordered.
    ///   equipment = the gear the character is currently wearing/wielding (name + instance id + slot
    ///               mask), so the phone can show the "suit" and the item ids. EquippedSlots>0 = worn.
    private void ScanInventoryStatus(out int freeSlots, out int scarabs, out int tapers,
                                     out List<KeyValuePair<string, int>> scarabsByType,
                                     out List<(string Name, uint Id, int Slot)> equipment)
    {
        freeSlots = -1; scarabs = -1; tapers = -1; scarabsByType = null; equipment = null;
        var cache = _objectCache;
        if (cache == null || _playerId == 0) return;

        // Ordered seed of the core tiers (0), then dynamic extras appended in first-seen order.
        var byType = new List<KeyValuePair<string, int>>(CoreScarabTiers.Length + 4);
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var tier in CoreScarabTiers) { index[tier] = byType.Count; byType.Add(new(tier, 0)); }
        var equip = new List<(string Name, uint Id, int Slot)>(20);

        int used = 0, scarabN = 0, taperN = 0;
        int playerIdSigned = unchecked((int)_playerId);
        foreach (var wo in cache.GetDirectInventory(forceRefresh: false))
        {
            string name = wo.Name ?? string.Empty;

            // Equipment tally — any item with a wielded location (EquippedSlots>0) is currently worn,
            // anywhere in the tree (armor/clothing/jewelry via container, weapons/foci via wielder).
            int slot = wo.Values(LongValueKey.EquippedSlots, 0);
            if (slot > 0)
                equip.Add((name, unchecked((uint)wo.Id), slot));

            // Component tally — count stacks anywhere in inventory.
            if (name.IndexOf("Scarab", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                int qty = Math.Max(1, wo.Values(LongValueKey.StackCount, 1));
                scarabN += qty;
                if (index.TryGetValue(name, out int at)) byType[at] = new(byType[at].Key, byType[at].Value + qty);
                else { index[name] = byType.Count; byType.Add(new(name, qty)); }
            }
            else if (name.IndexOf("Prismatic Taper", StringComparison.OrdinalIgnoreCase) >= 0)
                taperN += Math.Max(1, wo.Values(LongValueKey.StackCount, 1));

            // Main-pack free-slot count — direct children of the player only.
            if (wo.Container != playerIdSigned) continue;
            if (wo.ObjectClass == AcObjectClass.Container) continue;
            if (wo.ObjectClass == AcObjectClass.Foci) continue;
            if (slot > 0) continue;   // equipped items don't occupy a pack slot
            used++;
        }
        // Stable display order: by slot mask (roughly head->feet->jewelry->weapon) then name.
        equip.Sort((a, b) => a.Slot != b.Slot ? a.Slot.CompareTo(b.Slot)
                                              : string.CompareOrdinal(a.Name, b.Name));
        freeSlots = Math.Max(0, 102 - used);
        scarabs = scarabN;
        tapers = taperN;
        scarabsByType = byType;
        equipment = equip;
    }

    public override void OnTick()
    {
        bool diag = ++_tickDiag <= 3;
        try
        {
            _dashboard?.DrainMetaCommands();   // apply queued meta edits on this (plugin-tick) thread
            TickDelayedCommands();              // /ub delay
            if (_loginComplete) _dashboard?.TickPackSetup(_objectCache, _playerId);   // new/copied profile items

            // /ra pause <sec>: restart the macro once the window elapses. Cleared by
            // an explicit /ra start or /ra stop, so a manual decision always wins.
            if (_macroResumeAt != 0 && Environment.TickCount64 >= _macroResumeAt)
            {
                _macroResumeAt = 0;
                var pauseDash = _dashboard;
                if (pauseDash != null && !pauseDash.Settings.IsMacroRunning && !RefuseMacroStartForVTank())
                {
                    pauseDash.TogglePanelMacro();
                    ChatLine("[RynthAi] Pause elapsed — macro RESUMED.");
                }
            }

            // Remote control: apply any phone-issued commands. ~50ms cadence so the movement d-pad
            // feels responsive (press→move latency); a tiny dir glob is cheap. No-op when empty.
            // Monotonic clock (TickCount64) — a wall-clock step must never stall this safety-critical poll.
            // Apply remote commands forwarded by the RynthRemote plugin (engine SendPluginCommand broker →
            // _forwardedRemoteCommands), on this pump thread. RynthRemote owns the phone command-file drain
            // + manual movement and forwards every non-movement command (chat/clearbusy/hideui/bot-control)
            // here because they touch RynthAi-internal state. Empty in the common case.
            while (_forwardedRemoteCommands.TryDequeue(out var fwd))
            {
                // Chat and busy clears need a character in world: at character select
                // 'sendchat' went raw to AC's chat parser and 'clearbusy' reset the busy
                // count of a torn-down session. Settings toggles still apply.
                if (!_loginComplete && (fwd.action.Equals("sendchat", StringComparison.OrdinalIgnoreCase)
                                        || fwd.action.Equals("clearbusy", StringComparison.OrdinalIgnoreCase)))
                {
                    Host.Log($"[RynthAi] forwarded remote command '{fwd.action}' dropped - not in world.");
                    continue;
                }
                try { ApplyRemoteCommand(fwd.action, fwd.value); }
                catch (Exception ex) { Host.Log($"[RynthAi] forwarded remote command '{fwd.action}' failed: {ex.Message}"); }
            }

            // ── Push settings to engine each tick ──────────────────────
            // RynthAi has no render hook (it draws nothing itself). OnTick runs
            // unconditionally via PluginManager.TickAll, including in
            // Decal-coexistence mode. Push the suppression toggles here so the
            // settings panel controls the radar/powerbar.
            // (Renamed local to `pushSettings` to avoid collision with the
            // `settings` local declared further down in OnTick.)
            var pushSettings = _dashboard?.Settings;
            if (diag)
                Host.Log($"[RynthAi] OnTick: settings push entry — settings null? {pushSettings == null}, HasSetRadarSuppressed={Host.HasSetRadarSuppressed}");
            if (pushSettings != null)
            {
                if (diag)
                    Host.Log($"[RynthAi] OnTick: pushSettings.SuppressRetailRadar={pushSettings.SuppressRetailRadar}, SuppressRetailPowerbar={pushSettings.SuppressRetailPowerbar}");
                Host.SetFpsLimit(pushSettings.EnableFPSLimit, pushSettings.TargetFPSFocused, pushSettings.TargetFPSBackground);
                // "Hide UI" (remote): blank the vanilla AC radar/powerbar too. The RynthAi Avalonia
                // panels are separate windows already excluded by the stream's PrintWindow capture.
                // NOTE: retail chat suppression is intentionally NOT handled here — it is owned by
                // the RynthChat plugin (engine-side ChatHooks, driven from RynthChatPanel's gear menu).
                // RynthAi pushing it every tick clobbered that toggle, so it lives solely in RynthChat now.
                bool hideUi = _hideUi;
                if (Host.HasSetRadarSuppressed)
                    Host.SetRadarSuppressed(hideUi || pushSettings.SuppressRetailRadar);
                if (Host.HasSetPowerbarSuppressed)
                    Host.SetPowerbarSuppressed(hideUi || pushSettings.SuppressRetailPowerbar);

                // Fellowship-follow: publish the leader's object id so the nav
                // engine can steer toward their live position. 0 = idle (not in a
                // fellowship, or we ARE the leader — a leader shouldn't follow itself).
                if (pushSettings.FollowMode && pushSettings.FollowNamedTargetId != 0)
                {
                    pushSettings.FollowTargetId = pushSettings.FollowNamedTargetId;
                }
                else if (pushSettings.FollowMode && _fellowshipTracker != null)
                {
                    int leader = _fellowshipTracker.LeaderId;
                    pushSettings.FollowTargetId =
                        (leader != 0 && !_fellowshipTracker.IsLeader) ? unchecked((uint)leader) : 0u;
                }
                else
                {
                    pushSettings.FollowTargetId = 0;
                }
            }

            _objectCache?.Tick();
            if (diag) Host.Log("[RynthAi] OnTick: after cache tick");

            if (_loginComplete) TickAwakenedTier();

            // Periodically flush the creature profile store (~ every 5 seconds at 60Hz).
            if (++_creatureSaveTickCounter >= 300)
            {
                _creatureSaveTickCounter = 0;
                _creatureStore?.SaveIfDirty();
                _damageStore?.SaveIfDirty();
            }

            // Deferred per-character settings load. OnLoginComplete tries
            // LoadSettings once, gated on Host.GetPlayerId()/TryGetObjectName
            // being readable at that instant. In Decal-coexistence / off-thread
            // pump mode the player object often isn't materialised that early,
            // so that one shot fails, _charFolder/_settingsFilePath stay empty,
            // and EVERY SaveSettings()/CheckAndSave() silently no-ops for the
            // whole session (incl. the Avalonia SettingsPanel write-back path).
            // Retry on the unconditional tick until the name resolves — the
            // player object always comes good once the bot is actually running.
            // The player id is read once in OnLoginComplete and handed to every manager.
            // A 0 read there left combat, looting and the inventory scans off for the
            // whole session (they all return early on id 0), and nothing read it again.
            if (_loginComplete && _playerId == 0 && Host.HasGetPlayerId)
            {
                uint lateId = Host.GetPlayerId();
                if (lateId != 0)
                    ApplyLatePlayerId(lateId);
            }

            if (_loginComplete && _dashboard != null
                && string.IsNullOrEmpty(_dashboard.CharFolder)
                && ++_settingsLoadRetryCounter >= 30)
            {
                _settingsLoadRetryCounter = 0;
                uint pid = _playerId != 0 ? _playerId : Host.GetPlayerId();
                if (pid != 0 && Host.HasGetObjectName
                    && Host.TryGetObjectName(pid, out string lateName)
                    && !string.IsNullOrWhiteSpace(lateName))
                {
                    _dashboard.LoadSettings(lateName);
                    if (!string.IsNullOrEmpty(_dashboard.CharFolder))
                    {
                        _buffManager?.SetTimerPath(_dashboard.CharFolder);
                        _damageStore?.SetCharacter(_dashboard.CharFolder);
                        _patrolOnLoginPending = _dashboard.Settings.PatrolOnLogin;
                        if (_iltHub == null)
                        {
                            CreateIltHub();
                            try { _iltHub?.OnLoginComplete(lateName, _petManager); }
                            catch (Exception ex) { RynthLog.Exception(LogCat.IltHub, ex, "late login"); }
                        }
                        Log($"RynthAi: per-character settings established late for '{lateName}' (early OnLoginComplete read had failed).");
                    }
                }
            }

            // Settings/profile autosave (~every 2s at 60Hz). RynthAi has no render
            // hook, so the dirty-check save runs here on the unconditional
            // tick. TickAutoSave self-throttles by
            // content hash, so this only writes when settings actually changed.
            if (++_settingsSaveTickCounter >= 120)
            {
                _settingsSaveTickCounter = 0;
                _dashboard?.TickAutoSave();
            }
            // Pick up an external Monster Editor save (monsters.json watcher). This used
            // to run only inside the legacy dashboard's Render(), which the engine no
            // longer calls, so edits waited for the next login. A flag check when idle.
            _dashboard?.TickMonsterReload();
            _questTracker?.Tick();
            TickLocalFeatures();
            if (diag) Host.Log("[RynthAi] OnTick: after quest tracker");
            DrainGiveQueue();
            if (diag) Host.Log("[RynthAi] OnTick: after drain give queue");
            _jumper?.Tick();
            if (diag) Host.Log("[RynthAi] OnTick: after jumper tick");

            // ── Affirmative in-world gate ────────────────────────────────
            // _loginComplete is cleared only by the engine's one-shot logout
            // hook (CPlayerSystem::ExecuteLogOff / RecvNotice_Logoff). That
            // hook does not fire for every way an in-world session ends — a
            // server disconnect, link death, or world-server bounce (routine
            // on ACE) drops the client to char-select without it, leaving the
            // bot ticking against torn-down world data. AC's native
            // GetPlayerId returns 0 whenever no player is in world, so a
            // sustained 0 is treated as logout and runs the same teardown the
            // hook would have. Debounced so a one-frame transient 0 (it does
            // not go 0 across portals) can never tear down a healthy session.
            if (_loginComplete && Host.HasGetPlayerId)
            {
                if (Host.GetPlayerId() == 0)
                {
                    if (_notInWorldSince == DateTime.MinValue)
                        _notInWorldSince = DateTime.UtcNow;
                    else if ((DateTime.UtcNow - _notInWorldSince).TotalMilliseconds >= 1500)
                    {
                        _notInWorldSince = DateTime.MinValue;
                        Log("RynthAi: player not in world (GetPlayerId=0 ≥1.5s) — treating as logout.");
                        OnLogout();
                    }
                }
                else
                {
                    _notInWorldSince = DateTime.MinValue;
                }
            }

            if (_loginComplete)
            {
                // try/finally so the Nav3D marker submit runs on every tick the
                // login is complete — even when the Buffing / missile-crafting /
                // BoostNavPriority branches `return` early. The engine clears
                // the Nav3D buffer at the start of each TickAll, so any tick we
                // skip the submit blanks the rings until the next clean tick.
                // That is the cause of markers vanishing while the macro runs.
                try
                {
                if (diag) Host.Log("[RynthAi] OnTick: entering loginComplete block");
                // One bot per client (Decal bridge): VTank's macro started -> ours stops, before
                // anything below can move, attack, cast or buff this tick.
                if (_dashboard?.Settings is { } yieldSettings) TickVTankYield(yieldSettings);
                if (++_vitalsTickCounter >= 30)
                {
                    _vitalsTickCounter = 0;
                    if (Host.HasGetPlayerVitals &&
                        Host.TryGetPlayerVitals(out uint hp, out uint maxHp, out uint st, out uint maxSt, out uint mp, out uint maxMp))
                    {
                        _vitals.CurrentHealth = hp;
                        _vitals.MaxHealth = maxHp;
                        _vitals.CurrentStamina = st;
                        _vitals.MaxStamina = maxSt;
                        _vitals.CurrentMana = mp;
                        _vitals.MaxMana = maxMp;
                    }
                }
                if (_dashboard?.Settings is { } safetySettings) CheckPlayerDeath(safetySettings);
                // Dead or on the way back from a death: do nothing (HoldForDeath). The
                // finally below still submits the nav markers.
                if (HoldForDeath())
                    return;

                if (diag) Host.Log("[RynthAi] OnTick: before CheckBusyTimeout");
                CheckBusyTimeout();
                if (diag) Host.Log("[RynthAi] OnTick: before buffManager");
                _buffManager?.OnHeartbeat();
                if (diag) Host.Log("[RynthAi] OnTick: after buffManager");

                var settings = _dashboard?.Settings;
                if (settings == null)
                    return;
                if (diag) Host.Log($"[RynthAi] OnTick: settings ok, macro={settings.IsMacroRunning} action={settings.BotAction}");

                // Macro switched off: combat stops ticking, so a turn it was holding would
                // stay held and the character spun on its own (2026-09-27). Let go once.
                // Also leave AC able to act: a stop mid-fight or mid-action could leave the
                // server's attack running or AC's busy count up, and the player could click
                // but not use, equip or recall anything until they closed the client (a
                // tester after a 10-hour run, 2026-09-30). The same steps as /ra panic,
                // minus the peace-mode switch.
                if (_macroWasRunning && !settings.IsMacroRunning)
                {
                    _combatManager?.ReleaseHeldTurn();
                    // A door the nav detour asked for (or one half-way through opening) must
                    // not be opened at the next macro start: TickDoorInteraction only runs
                    // while nav owns the tick, so it never got to clear these itself.
                    _doorRequestedId = 0;
                    ResetDoorState();
                    // A corpse left open mid-loot is closed, as a player would close its window.
                    try { CloseOpenCorpseOnMacroStop(); }
                    catch (Exception ex) { Host.Log($"[RynthAi] Macro-stop corpse close failed: {ex.Message}"); }
                    try
                    {
                        int busyBefore = Host.HasGetBusyState ? Host.GetBusyState() : -1;
                        if (Host.HasCancelAttack) Host.CancelAttack();
                        if (Host.HasStopCompletely) Host.StopCompletely();
                        if (busyBefore > 0 && Host.HasForceResetBusyCount) Host.ForceResetBusyCount();
                        _busyCount = 0;
                        _busyCountLastIncrementAt = 0;
                        _busyCountBecamePositiveAt = 0;
                        if (_combatManager != null) _combatManager.BusyCount = 0;
                        if (_buffManager != null) _buffManager.BusyCount = 0;
                        Host.Log($"[RynthAi] Macro stopped: attack cancelled, movement stopped, busy {busyBefore} cleared.");
                    }
                    catch (Exception ex) { Host.Log($"[RynthAi] Macro-stop cleanup failed: {ex.Message}"); }
                }
                // Macro switched on: carry on from the nearest waypoint, not the one it
                // was heading to when stopped (after a recall that can be far away).
                if (!_macroWasRunning && settings.IsMacroRunning)
                    _navigationEngine?.ResumeFromNearestWaypoint();
                _macroWasRunning = settings.IsMacroRunning;

                if (_patrolOnLoginPending && _raycast?.GeometryLoader?.CellDat?.IsLoaded == true)
                {
                    _patrolOnLoginPending = false;
                    HandleDungeonNavPatrol();
                }

                // Reroute the active dungeon patrol around any lava/acid hotspot sighted
                // since the route was built — treats the hotspot as a wall instead of
                // looping through it. Cheap no-op unless a new hazard cell was registered.
                TickDunPatrolHazardReroute();

                // ── Activity arbiter — STEP 5: AUTHORITATIVE, full stop ──
                // The arbiter is the SOLE writer of every BotAction string, and
                // its typed decision (_activity) is what gates the rest of this
                // method. No manager writes the string; the buff and salvage
                // gap-fills are gone; the four nav pause flags are gone. wantCombat
                // uses the tight HasEngageableTarget predicate — NOT HasTargets —
                // so far-off mobs no longer latch the action lock while nav is
                // blocked. See ACTIVITY_ARBITER_PLAN.md.
                try
                {
                    _arbiter ??= new ActivityArbiter(m => Host.Log($"[RynthAi] {m}"));

                    // The three reasons buffing gets to hold the top slot, which is
                    // what BuffManager's seven scattered string writes encoded:
                    //  - a cast awaiting server confirmation (releasing mid-cast let
                    //    CombatManager's peace-mode switch fizzle the spell),
                    //  - a wanted vital recharge (these run even with buffing OFF),
                    //  - buffs actually below threshold.
                    bool wantBuffRaw = _buffManager != null
                                    && (_buffManager.PendingSpellId != 0
                                        || _buffManager.WantsVitalRecharge
                                        || (settings.EnableBuffing && _buffManager.NeedsAnyBuff()));
                    bool wantBuff = wantBuffRaw;

                    // ── Buffing-coma watchdog (STEP 5: decides, no longer falls through) ──
                    // 2026-06-12: a stance deadlock made NeedsAnyBuff() hold Buffing for
                    // 1h54m while BuffManager silently retried mode flips — the bot stood
                    // unbuffed and dormant, and the ONE component with stance recovery
                    // (CombatManager) never got a tick. A normal full rebuff cycle is
                    // 1-2 min; >5 min of CONTINUOUS wanting-to-buff is pathological
                    // regardless of cause.
                    //
                    // This used to sit after the string was written and "fall through" to
                    // the combat heartbeat. That could never work: CombatManager.canRun
                    // rejects BotAction=="Buffing", and stance recovery lives inside
                    // EquipWeaponAndSetStance, downstream of Think(), downstream of
                    // canRun. So the watchdog printed its warning and recovered nothing.
                    // It now suppresses wantBuff for one tick instead — the arbiter picks
                    // Combat, canRun opens, and recovery actually runs. Buffing reclaims
                    // the decision on the very next tick.
                    //
                    // The hold timer keys off wantBuffRaw, NOT off the decision: keying it
                    // off the decision would reset it on the bypass tick itself and the
                    // ~10s retry cadence would silently become "once every 5 minutes".
                    if (!wantBuffRaw)
                    {
                        _buffingHeldSince = DateTime.MinValue;
                        _buffComaWarned = false;
                    }
                    else
                    {
                        if (_buffingHeldSince == DateTime.MinValue)
                            _buffingHeldSince = DateTime.Now;

                        double heldMs = (DateTime.Now - _buffingHeldSince).TotalMilliseconds;
                        // A coma is buffing that has STOPPED casting, not a long cycle: a full
                        // rebuff with Stamina to Mana refills runs past 5 min, and yielding a tick
                        // every 10 s then handed Combat the tick mid-cycle, which drew the weapon
                        // and changed stance between buffs (10-02 18:47, "pulls a weapon out and
                        // fights mid buff cycle"). Only when nothing was cast for BuffComaIdleMs.
                        double idleMs = (DateTime.Now - _buffManager!.LastCastAttemptAt).TotalMilliseconds;
                        if (heldMs > BuffComaThresholdMs && idleMs > BuffComaIdleMs
                            && (DateTime.Now - _lastBuffComaBypassAt).TotalMilliseconds > BuffComaBypassEveryMs)
                        {
                            _lastBuffComaBypassAt = DateTime.Now;
                            wantBuff = false; // yield ONE tick so stance recovery can run
                            if (!_buffComaWarned)
                            {
                                _buffComaWarned = true;
                                Host.Log($"[RynthAi] BUFFING COMA: buffing has wanted the tick continuously for {heldMs / 60000:0.0} min with buffs still needed — yielding one tick per ~10s so combat/stance recovery can run. Check for a stance wedge.");
                                Host.WriteToChat($"[RynthAi] Buffing has been stuck for {heldMs / 60000:0} min (stance wedge?) — engaging recovery. /ra clearbusy or relog if it persists.", 2);
                            }
                        }
                    }

                    bool wantCombat = settings.EnableCombat && _combatManager != null && _combatManager.HasEngageableTarget;
                    bool engaged    = _combatManager?.IsUnderCloseAttack == true;

                    // Loot-grace bookkeeping. Remember the tick combat stopped
                    // wanting to run so HasLootWork can hold Looting across the
                    // gap before the corpse object materialises in the cache.
                    // This is the ONLY surviving piece of the old cascade, and it
                    // survives as an input to a pure predicate, not as a lock.
                    long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (wantCombat)
                        _combatEndedAt = 0;
                    else if (_combatEndedAt == 0 && _activity == BotActivity.Combat)
                        _combatEndedAt = nowMs;

                    bool wantLoot = HasLootWork(settings, nowMs);
                    bool wantSalvage = _salvageManager != null && _salvageManager.IsBusy;
                    bool routeLoaded = settings.CurrentRoute != null && settings.CurrentRoute.Points.Count > 0;

                    // Fellowship-follow steers toward a live leader position and
                    // needs no route, so it counts as nav work in its own right —
                    // without this, turning Follow on with no route loaded would
                    // leave wantNav false and NavigationEngine.Tick would never be
                    // called to do the following.
                    bool followActive = (settings.FollowMode && settings.FollowTargetId != 0)
                        || (settings.CurrentRoute != null && settings.CurrentRoute.RouteType == NavRouteType.Follow
                            && settings.CurrentRoute.FollowTargetName.Length > 0);
                    bool wantNav = settings.IsMacroRunning && settings.EnableNavigation
                                && (routeLoaded || followActive);
                    // Loot starvation: a lootable corpse in range has waited LootStarveMs behind
                    // combat and no monster is within LootStarveCloseYards. A busy spawn always
                    // has a monster inside MonsterRange, so without this corpses waited until
                    // the area emptied and were usually left behind (2026-10-03).
                    bool lootStarved = IsLootStarvedTurn(wantLoot, wantCombat);
                    var inputs = new ArbiterInputs(
                        settings.IsMacroRunning, wantBuff, wantCombat, wantLoot, wantSalvage, wantNav,
                        combatEngaged: engaged,
                        boostNav:      settings.BoostNavPriority,
                        boostLoot:     settings.BoostLootPriority,
                        followActive:  followActive,
                        lootStarved:   lootStarved);
                    _activity = _arbiter.Apply(in inputs, settings);
                    _lootStarveTurn = lootStarved && _activity == BotActivity.Looting;
                }
                catch (Exception ex)
                {
                    // The arbiter must never throw out of the live tick. But a silent catch
                    // left _activity on the last decision (Navigating past monsters, say) for
                    // as long as an input kept throwing. Stand down instead, and say why.
                    _activity = BotActivity.Idle;
                    _lootStarveTurn = false;
                    settings.BotAction = ActivityArbiter.ToBotAction(BotActivity.Idle);
                    if (Environment.TickCount64 - _lastArbiterErrorLogAt > 10_000)
                    {
                        _lastArbiterErrorLogAt = Environment.TickCount64;
                        Host.Log($"[RynthAi] Arbiter inputs threw - standing idle this tick: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                    }
                }

                // ── Meta Manager ─────────────────────────────────────────────────
                // Here, ahead of every early return below (buffing, vendor, crafting),
                // so its timers and polls keep running; it reads _activity to wait for
                // a safe moment before it loads a meta (Docs\META_MANAGER.md).
                try { TickMetaSchedule(settings); }
                catch (Exception ex)
                {
                    if (Environment.TickCount64 - _lastMetaScheduleErrorLogAt > 10_000)
                    {
                        _lastMetaScheduleErrorLogAt = Environment.TickCount64;
                        Host.Log($"[RynthAi] Meta Manager tick threw: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                    }
                }

                // ── AutoVendor (UtilityBelt-style) ───────────────────────────────
                // Runs whether or not the macro is on: it starts when a vendor opens.
                // While it holds the bot (a vendoring session, or /ub vendor open
                // trying to reach a vendor) nothing else moves the character or the
                // inventory — UB takes VTank's Navigation + ItemUse locks the same way.
                // Bounded by AutoVendor's own 60 s bail timer.
                var autoVendor = _autoVendor;
                if (autoVendor != null)
                {
                    autoVendor.Tick(_busyCount);
                    if (autoVendor.HoldsBot)
                    {
                        if (settings.IsMacroRunning)
                            StopNavFor(BotActivity.Idle);
                        KeepThreatScanFresh(settings);
                        FreezeCorpseTimeout(CorpseNowMs);   // a claimed corpse must not time out while held
                        _metaManager?.Think();
                        return;
                    }
                }

                // ── AutoTrade (UtilityBelt-style) ────────────────────────────────
                // Same hold as AutoVendor while it fills a trade window: UB takes VTank's
                // Navigation + ItemUse locks. Bounded by AutoTrade's own 10 s bail timer.
                var autoTrade = _autoTrade;
                if (autoTrade != null)
                {
                    autoTrade.Tick(_busyCount);
                    if (autoTrade.HoldsBot)
                    {
                        if (settings.IsMacroRunning)
                            StopNavFor(BotActivity.Idle);
                        KeepThreatScanFresh(settings);
                        FreezeCorpseTimeout(CorpseNowMs);   // a claimed corpse must not time out while held
                        _metaManager?.Think();
                        return;
                    }
                }

                TickWeaponElements();

                // ── Priority 1: Buffing ───────────────────────────────────────────
                // Blocks combat, looting, and navigation entirely. The coma
                // watchdog that used to live here now runs ahead of the decision
                // (it suppresses wantBuff for a tick), so this is just the hold.
                if (settings.IsMacroRunning && _activity == BotActivity.Buffing)
                {
                    StopNavFor(BotActivity.Buffing);
                    KeepThreatScanFresh(settings);
                    FreezeCorpseTimeout(CorpseNowMs);   // a claimed corpse must not time out while held
                    _metaManager?.Think();
                    return;
                }

                // AutoCram / AutoStack — only while idle (not looting a corpse, not crafting).
                // Gated on busy count inside the manager so it won't move items mid-cast.
                // Skip InventoryManager for a short settle window after login.
                // Its first call does a forced GetDirectInventory which walks every
                // container via native calls — when the cache is still classifying
                // hundreds of pending CreateObjects (RL hot-reload, fresh login),
                // the concurrent native walk has been causing intermittent crashes.
                bool inventorySettled = (DateTime.Now - _loginCompletedAt).TotalMilliseconds > 3000;
                if (inventorySettled
                    && _inventoryManager != null
                    && _openedContainerId == 0
                    && _targetCorpseId == 0)
                {
                    _inventoryManager.OnHeartbeat(_busyCount);
                }

                // Status feed: main-pack empty slots (cache-based, off-thread-safe). Throttled to
                // ~3s — it walks direct inventory, no need every tick. Mirrors VTankLootEvaluator
                // .GetMainPackEmptySlots so the phone shows when a box is about to run out of room.
                if (inventorySettled && _objectCache != null && _playerId != 0
                    && (DateTime.Now - _lastFreeSlotsPushAt).TotalMilliseconds > 3000)
                {
                    _lastFreeSlotsPushAt = DateTime.Now;
                    ScanInventoryStatus(out int freeSlots, out int scarabs, out int tapers, out var scarabsByType, out var equipment);
                    _dashboard?.SetFreeSlots(freeSlots);
                    _dashboard?.SetComponentCounts(scarabs, tapers, scarabsByType);
                    _dashboard?.SetEquipment(AppraiseEquipment(equipment));

                    // Remote full-inventory snapshot (P1) — same 3s gate, reuses the cached walk.
                    ScanInventoryForRemote();
                }

                // D2/D6 combat telemetry: three-tier target counts + offensive attack-cast/kill
                // ratio, pushed to the status feed and (change-gated) logged so a "scanned=0 vs
                // live mobs" or "animates but 0 damage" wedge is diagnosable from the feed/log
                // instead of guesswork. Independent of the inventory gate above — useful pre-settle.
                if (_combatManager != null
                    && (DateTime.Now - _lastCombatTelemetryAt).TotalMilliseconds > 3000)
                {
                    _lastCombatTelemetryAt = DateTime.Now;
                    int sTotal = _combatManager.LastScanTotalMonsters;
                    int sRing = _combatManager.LastScanInRing;
                    int sPoss = _combatManager.LastScanPossible;
                    int sLos = _combatManager.LastScanLosBlocked;
                    int atkCasts = _combatManager.SessionAttackCasts;
                    int sinceKill = _combatManager.CastsSinceLastKill;
                    _dashboard?.SetScanCounts(sTotal, sRing, sPoss, sLos);
                    _dashboard?.SetCastStats(atkCasts, sinceKill);

                    // Log only when there's something to see, and only when the picture changed,
                    // so a healthy idle bot never spams the per-client log. sinceKill is in the key
                    // so active combat shows periodic progress while a stalled-with-mobs wedge logs
                    // its snapshot once and then stays quiet. (-1 = no scan yet → OR is negative → skip.)
                    if ((sTotal | sRing | sPoss | sLos | atkCasts | sinceKill) > 0)
                    {
                        int sig = ((sTotal & 0x3FF) << 20) ^ ((sRing & 0x3FF) << 10) ^ (sPoss & 0x3FF)
                                  ^ (sLos << 6) ^ (sinceKill << 1);
                        if (sig != _lastCombatTelemetrySig)
                        {
                            _lastCombatTelemetrySig = sig;
                            Host.Log($"[ScanTele] targets total={sTotal} ring={sRing} possible={sPoss} " +
                                     $"losBlk={sLos} | atkCasts={atkCasts} sinceKill={sinceKill}");
                        }
                    }
                }

                if (diag) Host.Log("[RynthAi] OnTick: before salvageManager");

                // Combat preempts salvage when a mob is engageable. The old
                // design deliberately let salvage hold BotAction over Combat
                // (finish the queue), but that strands the bot doing inventory
                // combine-sweeps next to a mob attacking it: the legacy
                // 'Salvaging' string blocks CombatManager.canRun and the
                // arbiter doesn't override it (Salvaging isn't arbiter-
                // authoritative yet). HasEngageableTarget = actively engaged OR
                // a scanned mob within MonsterRange — the same predicate the
                // arbiter uses for Combat-vs-Nav (pure, no side effects;
                // reflects last tick's scan, which is fine to yield on).
                bool combatThreat = settings.EnableCombat && _combatManager?.HasEngageableTarget == true;

                // Same settle gate as InventoryManager: BeginCombineSalvage walks
                // GetDirectInventory which races with cache classification during
                // the post-login / hot-reload CreateObject burst. Also pause the
                // combine work while a threat is up so it doesn't contend with
                // combat over SelectItem; it resumes the moment the threat clears.
                if (inventorySettled && !combatThreat)
                    _salvageManager?.OnTick(_busyCount);
                if (diag) Host.Log("[RynthAi] OnTick: before manaStoneManager");

                // STEP 4: the salvage gap-fill that used to pin/release
                // "Salvaging" here is GONE. Its whole content — "salvage wants
                // the tick while the queue is busy, unless combat or buffing
                // outranks it" — is now the WantSalvaging input plus the fixed
                // priority order in ActivityArbiter.Decide. The release branch
                // it needed (hand back the string so CombatManager.canRun could
                // become true) is unnecessary once one writer recomputes the
                // decision from scratch every tick.

                // Mana stone tapping — runs after salvage, independent of looting state.
                _manaStoneManager?.OnHeartbeat(_busyCount);
                _petManager?.OnHeartbeat(_busyCount);
                if (diag) Host.Log("[RynthAi] OnTick: before combatManager");

                // Missile crafting runs before combat — blocks everything while active.
                // Gated on the same settle window as InventoryManager: ProcessCrafting
                // also does a forced GetDirectInventory walk which races with cache
                // classification right after login/RL.
                if (inventorySettled && _missileCraftingManager != null && settings.IsMacroRunning)
                {
                    _missileCraftingManager.ProcessCrafting();
                    if (_missileCraftingManager.IsCrafting)
                    {
                        // Crafting can start in the middle of a nav leg (the ammo check runs
                        // whoever owns the tick). Without a stop, autorun stayed on and the
                        // bot ran blind on its last heading for the whole craft.
                        StopNavFor(BotActivity.Idle);
                        KeepThreatScanFresh(settings);
                        FreezeCorpseTimeout(CorpseNowMs);   // a claimed corpse must not time out while held
                        _metaManager?.Think();
                        return; // Block combat, nav, looting until crafting finishes
                    }
                }

                // Learn unknown spells (VTank ReadUnknownScrolls): reads a scroll only at a safe
                // moment, and holds the bot still while the read animation plays (a few seconds).
                if (inventorySettled && _scrollLearner != null && TickScrollLearner(settings))
                {
                    StopNavFor(BotActivity.Idle);
                    KeepThreatScanFresh(settings);
                    FreezeCorpseTimeout(CorpseNowMs);   // a claimed corpse must not time out while held
                    _metaManager?.Think();
                    return;
                }

                // ── STEP 5: the decision drives who ticks ────────────────────
                // This replaces ~90 lines of legacy cascade: the BoostNav and
                // BoostLoot string-stomps (both are inputs to Decide now), the
                // combatBlocking/corpseBlocking/lootGrace recomputation, and the
                // four hand-managed pause flags. There is exactly one question
                // left — "what did the arbiter decide?" — and one edge-detect for
                // stopping nav movement on the transition away from Navigating.
                bool navOwnsTick = _activity == BotActivity.Navigating;

                // Nav must keep ticking during portal/recall actions so teleport
                // detection works — combat and looting must not suppress it.
                bool navInPortal = _navigationEngine?.IsInPortalAction == true;

                // These two run EVERY tick, whoever won. They self-gate internally
                // — attacks via CombatManager.canRun, corpse claims via
                // CanClaimCorpse, both reading the string the arbiter has already
                // written this tick — so the decision still controls what they
                // DO. The order below is about latency, not permission.
                //
                // Do NOT gate OnHeartbeat on the decision. It is what runs
                // ScanNearbyTargets, which populates _scannedTargets, which is what
                // HasEngageableTarget reads, which is the arbiter's OWN wantCombat
                // input. Gating it is circular and self-latching: nav wins the tick
                // → the scan never refreshes → no targets are ever seen → wantCombat
                // stays false → nav wins forever. That is the 2026-09-04 "ignores
                // all monsters and just navigates" regression. The scan issues no
                // game commands, so running it while another activity owns the tick
                // costs nothing and is what keeps the decision honest.
                if (_activity == BotActivity.Looting)
                {
                    TickCorpseOpening();
                    _combatManager?.OnHeartbeat();
                }
                else
                {
                    _combatManager?.OnHeartbeat();
                    if (diag) Host.Log("[RynthAi] OnTick: after combatManager.OnHeartbeat");
                    TickCorpseOpening();
                }

                if (diag) Host.Log("[RynthAi] OnTick: before nav");

                if (navOwnsTick || navInPortal)
                {
                    _navStopIssued = false;
                    _navStoppedFor = BotActivity.Idle;

                    bool doorBlocking = TickDoorInteraction();
                    if (!doorBlocking)
                        _navigationEngine?.Tick();
                }
                else
                {
                    StopNavFor(_activity);
                }

                TickPendingMtLoot();
                _metaManager?.Think();

                }
                finally
                {
                    // Submit nav-marker 3D geometry here: OnTick runs
                    // unconditionally (RynthAi has no render hook). The engine
                    // clears the Nav3D buffer at the start of each TickAll, so we
                    // just submit our geometry on top of whatever other plugins
                    // have already added this frame. The try/finally above ensures
                    // this fires even when an early return short-circuits the body
                    // (Buffing / missile-crafting / BoostNavPriority branches).
                    if (Host.HasNav3D)
                    {
                        _navMarkerRenderer?.SubmitNav3D();
                        _navOverlay?.SubmitNav3D();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Throttled: an exception early in the tick repeats every tick (~30/s).
            if (Environment.TickCount64 - _lastTickErrorLogAt > 10_000)
            {
                _lastTickErrorLogAt = Environment.TickCount64;
                Host.Log($"[RynthAi] OnTick exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
    private long _lastArbiterErrorLogAt = -100_000;
    private long _lastMetaScheduleErrorLogAt = -100_000;
    private long _lastTickErrorLogAt = -100_000;

    private bool _lootInspectMode = true; // always on; /ra lootcheck off to disable

    public override void OnSelectedTargetChange(uint currentTargetId, uint previousTargetId)
    {
        _currentTargetId = currentTargetId;
        _dashboard?.SetSelectedTarget(currentTargetId);

        // Appraise the target so the server sends back its vitals (including MaxHealth).
        // Follow up with QueryHealth to resolve the ratio → absolute HP values.
        if (currentTargetId != 0)
        {
            if (Host.HasRequestId) Host.RequestId(currentTargetId);
            if (Host.HasQueryHealth) Host.QueryHealth(currentTargetId);
        }

        if (_lootInspectMode && currentTargetId != 0)
        {
            int sid = unchecked((int)currentTargetId);
            WorldObject? obj = _objectCache?[sid];
            // Skip non-items (monsters, players, NPCs, doors, corpses, portals, etc.)
            if (obj != null && IsLootableClass(obj.ObjectClass))
            {
                InspectLootRuleForItem(sid, quiet: true);
                var itemInfo = _dashboard?.Settings.ItemInfoSettings;
                if (itemInfo != null && ItemInfoWantsClass(itemInfo, obj.ObjectClass))
                    QueueAutoItemInfo(sid, requestId: false);
            }
        }
    }

    private static bool IsLootableClass(AcObjectClass cls) => cls is not (
        AcObjectClass.Unknown  or AcObjectClass.Monster   or AcObjectClass.Player  or
        AcObjectClass.Vendor   or AcObjectClass.Door      or AcObjectClass.Corpse  or
        AcObjectClass.Lifestone or AcObjectClass.Portal   or AcObjectClass.Housing or
        AcObjectClass.Npc      or AcObjectClass.CombatPet or AcObjectClass.Sign);

    public override void OnCombatModeChange(int currentCombatMode, int previousCombatMode)
    {
        _currentCombatMode = currentCombatMode;
        // BuffManager and MissileCraftingManager read CurrentCombatMode live from AC,
        // so no push is needed — they always see the truth, hot-reload-safe and event-loss-safe.
    }

    public override void OnChatWindowText(string? text, int chatType, ref int eat)
    {
        if (string.IsNullOrEmpty(text)) return;
        // The Meta Manager eats the reply to its own /myquests poll (only that).
        bool mmEat = false;
        try { mmEat = MetaScheduleChat(text); } catch { }
        if (mmEat) eat = 1;
        else _dashboard?.PushChatLine(text, chatType);
        OnChatLocalFeatures(text, chatType, ref eat);
        _buffManager?.OnChatWindowText(text, chatType);
        _manaStoneManager?.OnChatWindowText(text);
        _petManager?.OnChatWindowText(text);
        _combatManager?.HandleChatForDebuffs(text);
        _combatManager?.HandleChatForDamage(text);
        _missileCraftingManager?.HandleChat(text);
        _scrollLearner?.OnChat(text);
        _metaManager?.HandleChat(text, chatType);
        _questTracker?.OnChatLine(text);
        CheckChatForSafetyStops(text);
    }

    // ACE sends GameEventKillerNotification (0x01AD) to the killer at the
    // lethal hit — earlier than the health=0 / corpse signals combat otherwise
    // waits on. The engine parses the death string out of the packet and hands
    // it here so combat can drop the dead target before burning another cast.
    public override void OnKillNotification(string? deathMessage)
    {
        if (string.IsNullOrEmpty(deathMessage)) return;
        _combatManager?.OnKillNotification(deathMessage);
        _dashboard?.RecordKill();   // feeds the kills/hour session stat
        try { _iltHub?.RecordKill(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.IltHub, ex, "kill"); }
    }

    public override void OnCreateObject(uint objectId)
    {
        try { _objectCache?.OnCreateObject(objectId); }
        catch (Exception ex)
        {
            Host.Log($"[RynthAi] OnCreateObject EXCEPTION on id=0x{objectId:X8}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public override void OnDeleteObject(uint objectId)
    {
        try
        {
            _objectCache?.OnDeleteObject(objectId);
            _combatManager?.OnObjectDeleted(objectId);   // D7/D8: free per-id maps + clear cast-wait if it was our target
            _scrollLearner?.OnObjectDeleted(objectId);   // a read scroll is consumed
            HandleCorpseObjectDeleted(objectId);
        }
        catch (Exception ex)
        {
            Host.Log($"[RynthAi] OnDeleteObject EXCEPTION on id=0x{objectId:X8}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public override void OnUpdateHealth(uint targetId, float healthRatio, uint currentHealth, uint maxHealth)
    {
        float prevRatio = _objectCache?.GetHealthRatio(unchecked((int)targetId)) ?? -1f;
        _objectCache?.OnUpdateHealth(targetId, healthRatio);
        _dashboard?.OnUpdateHealth(targetId, healthRatio, currentHealth, maxHealth);
        if (_loginComplete && targetId == _playerId && maxHealth > 0)
        {
            _vitals.CurrentHealth = currentHealth;
            _vitals.MaxHealth = maxHealth;
        }

        // When a creature's health DROPS, something hit it — reset its miss counter so the
        // blacklist doesn't trigger on valid in-combat targets. Only a drop counts: the
        // client also gets health for a mob the player merely selects (QueryHealth reply),
        // for the fight target combat queries at lock, and on regen, all at unchanged or
        // higher health. Counting those as hits reset the no-damage miss streak,
        // un-blacklisted the mob and gave it the damage-commitment bonus, so selecting a
        // mob could steer combat and a target the arrows weren't hurting could be held far
        // past BlacklistAttempts misses (2026-09-27, Olthoi swarm).
        if (targetId != _playerId && IsHealthDrop(prevRatio, healthRatio))
            _combatManager?.ReportDamageOnTarget((int)targetId);

        // Capture observed creature data into the persistent store. maxHealth>0 means
        // we just got a successful CreatureProfile (Assess succeeded) — the only time
        // we have authoritative max vitals + resists.
        if (targetId != _playerId && maxHealth > 0 && IsFightableCreature(targetId))
            CaptureCreatureSample(targetId, maxHealth);
    }

    /// <summary>
    /// True when a health update means the creature lost health: lower than the last ratio
    /// we knew, or, for the first report of a creature, below full.
    /// </summary>
    private static bool IsHealthDrop(float prevRatio, float newRatio)
    {
        if (float.IsNaN(newRatio)) return false;
        return prevRatio < 0f ? newRatio < 0.999f : newRatio < prevRatio - 0.0005f;
    }

    /// <summary>
    /// Something you can fight — what the Damage tab and creatures.json should learn from.
    /// Appraising an NPC, a vendor, another player or your own pet also returns health, and
    /// each one used to become a Damage-tab row (and a creatures.json entry). The attackable
    /// check also catches NPCs the object cache classifies as monsters.
    /// </summary>
    private bool IsFightableCreature(uint objectId)
    {
        WorldObject? obj = _objectCache?[unchecked((int)objectId)];
        if (obj != null && obj.ObjectClass is AcObjectClass.Npc or AcObjectClass.Vendor
                                             or AcObjectClass.Player or AcObjectClass.CombatPet)
            return false;
        return !Host.HasObjectIsAttackable || Host.ObjectIsAttackable(objectId);
    }

    // Exact per-hit damage from the engine (AttackerNotification 0x01B1). This is
    // the only selection-free source of real damage numbers — combat uses it to
    // learn per-monster damage and predict kill shots. isAttacker==true = our hit.
    public override void OnCombatDamage(uint damage, uint damageType, bool crit, bool isAttacker)
    {
        if (isAttacker)
            _combatManager?.OnCombatDamage((double)damage, damageType, crit, isAttacker);
    }

    /// <summary>Human-readable table of the learned per-monster casts-to-kill data
    /// (joined with creatures.json for names + appraised HP). Polled live by the
    /// engine-side MonsterDamagePanel via RynthPluginGetMonsterDamageText.</summary>
    public string BuildMonsterDamageText()
    {
        var store = _damageStore;
        if (store == null) return "Monster-damage learning not ready.";
        int vt = DamageViewDifficulty;
        var rows = store.Snapshot(vt);
        if (rows.Count == 0)
            return "No kills recorded yet.\n\nFight monsters with magic and this fills in:\n"
                 + "one row per monster type + spell, with the average\n"
                 + "number of casts it takes to kill them.";

        var sb = new System.Text.StringBuilder();
        sb.Append("Monster                      Elem   Tier  Casts/Kill  Kills    HP*\n");
        sb.Append("-----------------------------------------------------------------\n");
        foreach (var r in rows.OrderByDescending(x => x.KillSamples)
                               .ThenBy(x => x.Element, StringComparer.OrdinalIgnoreCase))
        {
            string name = "wcid " + r.Wcid.ToString();
            double hp = 0;
            if (_creatureStore != null && _creatureStore.TryGetByWcid(r.Wcid, out var prof) && prof != null)
            {
                if (!string.IsNullOrEmpty(prof.Name)) name = prof.Name;
                hp = _creatureStore.MaxHealthAt(prof, vt);
            }
            if (name.Length > 28) name = name.Substring(0, 28);
            string hpStr = hp > 0 ? hp.ToString("0") : "?";
            sb.Append(name.PadRight(28)).Append(' ')
              .Append((r.Element ?? "").PadRight(6)).Append(' ')
              .Append(r.Tier.ToString().PadLeft(3)).Append("   ")
              .Append(r.AvgCastsToKill.ToString("0.00").PadLeft(8)).Append("  ")
              .Append(r.KillSamples.ToString().PadLeft(5)).Append("  ")
              .Append(hpStr.PadLeft(5)).Append('\n');
        }
        sb.Append("\n* HP = appraised; skill-gated on ACE, often reads low.\n");
        sb.Append("  Casts/Kill is measured from real kills — that's the reliable number.\n");
        return sb.ToString();
    }

    /// <summary>Structured per-monster learning for the interactive Damage panel.
    /// One JSON object per (monster, weapon, spell) row. Polled live by the engine
    /// MonsterDamagePanel via RynthPluginGetMonsterDamageJson.</summary>
    public string BuildMonsterDamageJson()
    {
        var store = _damageStore;
        if (store == null) return "[]";
        // The Aelrynth difficulty tier shown (0 off Aelrynth): HP, kills, casts to kill, seconds
        // per kill and hit rate are that tier's; damage per cast and every rule are shared.
        int vt = DamageViewDifficulty;
        _jsonViewTier = vt;
        var rows = store.Snapshot(vt);
        _jsonTargetWcid = TargetedWcid();
        _jsonNearby = NearbyMonsters();

        // Per-wcid weapon recommendation is identical for every row of a monster — memoize it.
        var bestCache = new Dictionary<uint, uint>();
        uint BestFor(uint w)
        {
            if (!bestCache.TryGetValue(w, out uint b)) { b = store.GetBestWeapon(w); bestCache[w] = b; }
            return b;
        }
        string NameOf(uint id, string prefix) => id == 0 ? "" : WeaponLabel(id, prefix);

        // Group the per-(weapon,element,tier) stat rows by MONSTER (wcid). The Damage tab now
        // shows ONE collapsed row per monster (latest tier used + total kills); the per-tier
        // breakdown rides along in a nested "tiers" array for the expand drawer.
        var byWcid = new Dictionary<uint, List<CreatureData.MonsterDamageStore.DamageRow>>();
        var order = new List<uint>();
        foreach (var r in rows)
        {
            if (!byWcid.TryGetValue(r.Wcid, out var glist)) { glist = new(); byWcid[r.Wcid] = glist; order.Add(r.Wcid); }
            glist.Add(r);
        }

        string ResolveName(uint wcid, string rowName)
        {
            string name = rowName;
            if (string.IsNullOrEmpty(name) && _creatureStore != null
                && _creatureStore.TryGetByWcid(wcid, out var prof) && prof != null && !string.IsNullOrEmpty(prof.Name))
                name = prof.Name;
            return string.IsNullOrEmpty(name) ? "wcid " + wcid : name;
        }

        var sb = new System.Text.StringBuilder();
        sb.Append('[');
        bool first = true;
        var emittedWcids = new HashSet<uint>();

        // The DEFAULT line (top row). Carries the per-character default weapon; the panel renders
        // it specially (weapon picker + chevron → the Default debuff/shape rule). isDefault=true.
        uint defWeapon = store.GetDefaultWeapon();
        sb.Append('{')
          .Append("\"wcid\":0,\"isDefault\":true,\"name\":\"Default\",")
          .Append("\"wid\":").Append(defWeapon).Append(',')
          .Append("\"weapon\":").Append(JsonStr(NameOf(defWeapon, "Weapon"))).Append(',')
          .Append("\"elem\":\"\",\"tier\":0,\"hp\":0,\"hpManual\":false,")
          .Append("\"crit\":0,\"critN\":0,\"noncrit\":0,\"noncritN\":0,\"casts\":0,\"kills\":0,")
          .Append("\"assignedWid\":").Append(defWeapon).Append(',')
          .Append("\"assignedWeapon\":").Append(JsonStr(NameOf(defWeapon, "Weapon"))).Append(',')
          .Append("\"bestWid\":0,\"bestWeapon\":\"\",\"assignedOff\":0,\"assignedOffName\":\"\",")
          .Append("\"key\":\"__default__\",\"tiers\":[]")
          .Append('}');
        first = false;

        // STABLE order by monster name (then wcid) so rows don't reshuffle as counters tick.
        foreach (uint wcid in order.OrderBy(w => ResolveName(w, byWcid[w].Count > 0 ? byWcid[w][0].Name : ""),
                                            StringComparer.OrdinalIgnoreCase).ThenBy(w => w))
        {
            var glist = byWcid[wcid];
            emittedWcids.Add(wcid);
            string name = ResolveName(wcid, glist.Count > 0 ? glist[0].Name : "");
            var hpAt = HpAt(wcid, vt);
            bool hpManual = hpAt.Manual;
            double hp = hpAt.Hp;

            int latestTier = store.GetLastTier(wcid);          // negative = ring
            int totalKills = 0; foreach (var x in glist) totalKills += x.KillSamples;

            // Representative entry for the collapsed row's crit/casts = the latest-tier one,
            // else the most-killed one.
            CreatureData.MonsterDamageStore.DamageRow m = default; bool haveM = false;
            foreach (var x in glist) if (x.Tier == latestTier) { m = x; haveM = true; break; }
            if (!haveM) { int bk = -1; foreach (var x in glist) if (x.KillSamples > bk) { bk = x.KillSamples; m = x; haveM = true; } }

            uint assignedWid = store.GetManualWeapon(wcid);
            uint bestWid     = BestFor(wcid);
            uint assignedOff = store.GetManualOffhand(wcid);

            if (!first) sb.Append(',');
            first = false;
            sb.Append('{')
              .Append("\"wcid\":").Append(wcid).Append(',')
              .Append("\"name\":").Append(JsonStr(name)).Append(',')
              .Append("\"wid\":").Append(haveM ? m.WeaponId : 0).Append(',')
              .Append("\"weapon\":").Append(JsonStr(haveM ? NameOf(m.WeaponId, "Weapon") : "")).Append(',')
              .Append("\"elem\":").Append(JsonStr(haveM ? (m.Element ?? "") : "")).Append(',')
              .Append("\"tier\":").Append(latestTier).Append(',')
              .Append("\"hp\":").Append((int)Math.Round(hp)).Append(',')
              .Append("\"hpManual\":").Append(hpManual ? "true" : "false").Append(',')
              .Append(hpAt.Estimated ? "\"hpEst\":true," : "")
              .Append("\"crit\":").Append(JsonNum(haveM ? m.AvgCritDamage : 0)).Append(',')
              .Append("\"critN\":").Append(haveM ? m.CritSamples : 0).Append(',')
              .Append("\"noncrit\":").Append(JsonNum(haveM ? m.AvgNonCritDamage : 0)).Append(',')
              .Append("\"noncritN\":").Append(haveM ? m.NonCritSamples : 0).Append(',')
              .Append("\"casts\":").Append(JsonNum(haveM ? m.AvgCastsToKill : 0)).Append(',')
              .Append("\"kills\":").Append(totalKills).Append(',')
              .Append("\"assignedWid\":").Append(assignedWid).Append(',')
              .Append("\"assignedWeapon\":").Append(JsonStr(NameOf(assignedWid, "Weapon"))).Append(',')
              .Append("\"bestWid\":").Append(bestWid).Append(',')
              .Append("\"bestWeapon\":").Append(JsonStr(NameOf(bestWid, "Weapon"))).Append(',')
              .Append("\"assignedOff\":").Append(assignedOff).Append(',')
              .Append("\"assignedOffName\":").Append(JsonStr(NameOf(assignedOff, "Offhand"))).Append(',')
              .Append("\"pet\":").Append(JsonStr(store.GetManualPet(wcid))).Append(',')
              .Append("\"petLabel\":").Append(JsonStr(PetChoiceLabel(store.GetManualPet(wcid)))).Append(',')
              .Append(WeakJson(wcid, name))
              .Append("\"key\":").Append(JsonStr(wcid.ToString())).Append(',')
              .Append("\"tiers\":[");
            bool tf = true;
            foreach (var x in glist.OrderByDescending(z => z.KillSamples).ThenByDescending(z => z.Tier))
            {
                if (!tf) sb.Append(',');
                tf = false;
                sb.Append('{')
                  .Append("\"tier\":").Append(x.Tier).Append(',')
                  .Append("\"elem\":").Append(JsonStr(x.Element ?? "")).Append(',')
                  .Append("\"weapon\":").Append(JsonStr(NameOf(x.WeaponId, "Weapon"))).Append(',')
                  .Append("\"crit\":").Append(JsonNum(x.AvgCritDamage)).Append(',')
                  .Append("\"critN\":").Append(x.CritSamples).Append(',')
                  .Append("\"noncrit\":").Append(JsonNum(x.AvgNonCritDamage)).Append(',')
                  .Append("\"noncritN\":").Append(x.NonCritSamples).Append(',')
                  .Append("\"casts\":").Append(JsonNum(x.AvgCastsToKill)).Append(',')
                  .Append("\"kills\":").Append(x.KillSamples)
                  .Append('}');
            }
            sb.Append("]}");
        }

        // Bare rows for monsters appraised this session but not yet fought (one per wcid), so the
        // table populates as you ID nearby mobs. HP from creatures.json (appraised); empty tiers.
        uint[] seen;
        lock (_seenMonstersThisSession) seen = System.Linq.Enumerable.ToArray(_seenMonstersThisSession);
        foreach (uint wcid in seen.Concat(_jsonNearby.Keys))
        {
            if (emittedWcids.Contains(wcid)) continue;
            emittedWcids.Add(wcid);

            string name = ResolveName(wcid, _jsonNearby.TryGetValue(wcid, out string? nearName) ? nearName : "");
            var hpAt = HpAt(wcid, vt, learned: false);
            bool hpManual = hpAt.Manual;
            double hp = hpAt.Hp;
            uint assignedWid = store.GetManualWeapon(wcid);
            uint bestWid     = BestFor(wcid);
            uint assignedOff = store.GetManualOffhand(wcid);
            int latestTier = store.GetLastTier(wcid);

            if (!first) sb.Append(',');
            first = false;
            sb.Append('{')
              .Append("\"wcid\":").Append(wcid).Append(',')
              .Append("\"name\":").Append(JsonStr(name)).Append(',')
              .Append("\"wid\":0,\"weapon\":\"\",\"elem\":\"\",")
              .Append("\"tier\":").Append(latestTier).Append(',')
              .Append("\"hp\":").Append((int)Math.Round(hp)).Append(',')
              .Append("\"hpManual\":").Append(hpManual ? "true" : "false").Append(',')
              .Append(hpAt.Estimated ? "\"hpEst\":true," : "")
              .Append("\"crit\":0,\"critN\":0,\"noncrit\":0,\"noncritN\":0,\"casts\":0,\"kills\":0,")
              .Append("\"assignedWid\":").Append(assignedWid).Append(',')
              .Append("\"assignedWeapon\":").Append(JsonStr(NameOf(assignedWid, "Weapon"))).Append(',')
              .Append("\"bestWid\":").Append(bestWid).Append(',')
              .Append("\"bestWeapon\":").Append(JsonStr(NameOf(bestWid, "Weapon"))).Append(',')
              .Append("\"assignedOff\":").Append(assignedOff).Append(',')
              .Append("\"assignedOffName\":").Append(JsonStr(NameOf(assignedOff, "Offhand"))).Append(',')
              .Append("\"pet\":").Append(JsonStr(store.GetManualPet(wcid))).Append(',')
              .Append("\"petLabel\":").Append(JsonStr(PetChoiceLabel(store.GetManualPet(wcid)))).Append(',')
              .Append(WeakJson(wcid, name))
              .Append("\"key\":").Append(JsonStr(wcid.ToString())).Append(',')
              .Append("\"tiers\":[]")
              .Append('}');
        }

        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>"weak" (top elements with multipliers) and "weakSrc" fields for a Damage-tab row, each followed by a comma.</summary>
    private uint _jsonTargetWcid;
    private int _jsonViewTier;   // the difficulty tier BuildMonsterDamageJson is writing (WeakJson reads it)
    private Dictionary<uint, string> _jsonNearby = new();

    /// <summary>Fightable monsters in the world cache on the player's landblock, wcid -> name (for the
    /// Damage panel's "nearby" highlight; those not in the list yet are added as rows).</summary>
    private Dictionary<uint, string> NearbyMonsters()
    {
        var map = new Dictionary<uint, string>();
        uint pid = Host.GetPlayerId();
        if (_objectCache == null || pid == 0 || !Host.HasGetObjectWcid) return map;
        if (!Host.TryGetObjectPosition(pid, out uint myCell, out _, out _, out _)) return map;
        uint myBlock = myCell >> 16;
        foreach (var wo in _objectCache.GetLandscapeObjects())
        {
            if (wo.ObjectClass != AcObjectClass.Monster) continue;
            uint uid = unchecked((uint)wo.Id);
            // Attackable only: NPCs the object cache classifies as monsters would otherwise
            // be added as Damage-tab rows here (e52ccc8 closed the appraisal path only).
            if (!IsFightableCreature(uid)) continue;
            if (!Host.TryGetObjectPosition(uid, out uint cell, out _, out _, out _) || (cell >> 16) != myBlock) continue;
            if (Host.TryGetObjectWcid(uid, out uint w) && w != 0 && !map.ContainsKey(w)) map[w] = wo.Name ?? "";
        }
        return map;
    }

    /// <summary>The wcid of the monster being fought, else of the selected object (0 = none).</summary>
    private uint TargetedWcid()
    {
        int id = _combatManager?.activeTargetId ?? 0;
        if (id == 0) id = unchecked((int)_currentTargetId);
        if (id == 0 || !Host.HasGetObjectWcid) return 0;
        return Host.TryGetObjectWcid(unchecked((uint)id), out uint w) ? w : 0;
    }

    private string WeakJson(uint wcid, string name)
    {
        var r = _combatManager?.WeaknessFor(wcid, name);
        var (sec, hit) = _damageStore?.GetSummary(wcid, _jsonViewTier) ?? (-1, -1);
        return "\"weak\":" + JsonStr(r?.Describe() ?? "") + ",\"weakSrc\":" + JsonStr(r?.Source ?? "")
             + ",\"secKill\":" + JsonNum(sec) + ",\"hitRate\":" + JsonNum(hit)
             + ",\"targeted\":" + (wcid != 0 && wcid == _jsonTargetWcid ? "true" : "false")
             + ",\"nearby\":" + (wcid != 0 && _jsonNearby.ContainsKey(wcid) ? "true" : "false") + ",";
    }

    /// <summary>
    /// One monster's detail for the Damage tab's detail panel: all eight weaknesses, every
    /// learned weapon/element/tier row, accuracy and fight length per weapon, kills per
    /// summon element, and the damage it did to us. "{}" when unknown.
    /// </summary>
    public string BuildMonsterDetailJson(uint wcid)
    {
        var store = _damageStore;
        if (store == null || wcid == 0) return "{}";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string NameOf(uint id) => id == 0 ? "" : WeaponLabel(id, "Weapon");

        int vt = DamageViewDifficulty;
        var casts = store.Snapshot(vt).Where(r => r.Wcid == wcid).ToList();
        string name = casts.Count > 0 ? casts[0].Name : "";
        CreatureData.CreatureProfile? prof = null;
        if (_creatureStore != null && _creatureStore.TryGetByWcid(wcid, out var pf)) prof = pf;
        if (string.IsNullOrEmpty(name)) name = prof?.Name ?? "";
        if (string.IsNullOrEmpty(name)) name = "wcid " + wcid;

        var hpAt = HpAt(wcid, vt);
        double hp = hpAt.Hp;
        string hpSrc = hpAt.Source;
        var weak = _combatManager?.WeaknessFor(wcid, name);
        var (weapons, summons, taken) = store.GetDetail(wcid, vt);
        var (secKill, hitRate) = store.GetSummary(wcid, vt);

        var sb = new System.Text.StringBuilder();
        sb.Append('{')
          .Append("\"wcid\":").Append(wcid).Append(',')
          .Append("\"name\":").Append(JsonStr(name)).Append(',')
          .Append("\"hp\":").Append((int)Math.Round(hp)).Append(',')
          .Append("\"hpSrc\":").Append(JsonStr(hpSrc)).Append(',')
          .Append("\"secKill\":").Append(JsonNum(secKill)).Append(',')
          .Append("\"hitRate\":").Append(JsonNum(hitRate)).Append(',')
          .Append("\"weakSrc\":").Append(JsonStr(weak?.Source ?? "")).Append(',')
          .Append("\"weak\":[");
        if (weak != null)
            for (int i = 0; i < weak.Order.Count; i++)
            {
                if (i > 0) sb.Append(',');
                double m = weak.Order[i].Mult;
                sb.Append("{\"e\":").Append(JsonStr(weak.Order[i].Element))
                  .Append(",\"m\":").Append(double.IsNaN(m) ? "-1" : JsonNum(m)).Append('}');
            }
        sb.Append("],\"casts\":[");
        bool f = true;
        foreach (var r in casts.OrderByDescending(x => x.KillSamples).ThenByDescending(x => x.DmgSamples))
        {
            if (!f) sb.Append(','); f = false;
            sb.Append("{\"weapon\":").Append(JsonStr(NameOf(r.WeaponId)))
              .Append(",\"elem\":").Append(JsonStr(r.Element ?? ""))
              .Append(",\"tier\":").Append(r.Tier)
              .Append(",\"hits\":").Append(r.DmgSamples)
              .Append(",\"avg\":").Append(JsonNum(r.AvgDamage))
              .Append(",\"crit\":").Append(JsonNum(r.AvgCritDamage))
              .Append(",\"critN\":").Append(r.CritSamples)
              .Append(",\"noncrit\":").Append(JsonNum(r.AvgNonCritDamage))
              .Append(",\"noncritN\":").Append(r.NonCritSamples)
              .Append(",\"casts\":").Append(JsonNum(r.AvgCastsToKill))
              .Append(",\"kills\":").Append(r.KillSamples).Append('}');
        }
        sb.Append("],\"weapons\":[");
        f = true;
        foreach (var w in weapons.OrderByDescending(x => x.Hits + x.Misses))
        {
            if (!f) sb.Append(','); f = false;
            sb.Append("{\"weapon\":").Append(JsonStr(NameOf(w.WeaponId)))
              .Append(",\"hits\":").Append(w.Hits)
              .Append(",\"misses\":").Append(w.Misses)
              .Append(",\"sec\":").Append(JsonNum(w.SecSamples > 0 ? w.SecAvg : -1))
              .Append(",\"secN\":").Append(w.SecSamples).Append('}');
        }
        sb.Append("],\"summons\":[");
        f = true;
        foreach (var k in summons.OrderByDescending(x => x.Kills))
        {
            if (!f) sb.Append(','); f = false;
            sb.Append("{\"elem\":").Append(JsonStr(k.Element.Length == 0 ? "No summon" : k.Element))
              .Append(",\"kills\":").Append(k.Kills)
              .Append(",\"sec\":").Append(JsonNum(k.SecAvg)).Append('}');
        }
        sb.Append("],\"taken\":[");
        f = true;
        foreach (var t in taken.OrderByDescending(x => x.Avg * x.Hits))
        {
            if (!f) sb.Append(','); f = false;
            sb.Append("{\"elem\":").Append(JsonStr(t.Element))
              .Append(",\"hits\":").Append(t.Hits)
              .Append(",\"avg\":").Append(JsonNum(t.Avg))
              .Append(",\"max\":").Append(JsonNum(t.Max)).Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    /// <summary>Set (or clear, hp&lt;=0) the manual HP override for a wcid, then persist.</summary>
    public void SetMonsterHp(uint wcid, int hp)
    {
        if (_damageStore == null) return;
        // The Damage panel shows (and so edits) the HP of the difficulty tier it shows; the
        // override is kept as the real-Dereth value, which every tier scales by 5% a tier.
        // Off Aelrynth the tier is 0 and this is the value as typed, as before.
        int vt = DamageViewDifficulty;
        double value = hp > 0 && vt > 0 ? hp / CreatureData.AwakenedTier.HealthScale(vt) : hp;
        _damageStore.SetManualHp(wcid, value);
        _damageStore.SaveIfDirty();
    }

    /// <summary>
    /// The Aelrynth difficulty tier the Damage panel shows: the one picked there, else the one
    /// where the player stands. Always 0 off Aelrynth (or before the server has sent a tier).
    /// </summary>
    internal int DamageViewDifficulty
    {
        get
        {
            if (!_tier.Active) return 0;
            int v = _damageViewTier;
            return v >= 0 ? v : _tier.Current;
        }
    }

    /// <summary>The Damage panel's tier picker: -1 follows the current tier.</summary>
    public void SetDamageViewTier(int tier) => _damageViewTier = tier < 0 ? -1 : tier;

    /// <summary>
    /// The tier picker's state for the Damage panel: {"show":false} off Aelrynth or until the
    /// server has sent a tier this login - the panel then shows nothing of it.
    /// </summary>
    public string BuildAwakenedTierJson()
    {
        if (!_tier.Active) return "{\"show\":false}";
        var tiers = new SortedSet<int> { 0, _tier.Current };
        if (_damageStore != null) foreach (int d in _damageStore.KnownDifficulties()) tiers.Add(d);
        int view = DamageViewDifficulty;
        tiers.Add(view);
        var sb = new System.Text.StringBuilder(96);
        sb.Append("{\"show\":true,\"current\":").Append(_tier.Current)
          .Append(",\"view\":").Append(view)
          .Append(",\"following\":").Append(_damageViewTier < 0 ? "true" : "false")
          .Append(",\"percent\":").Append(JsonNum(CreatureData.AwakenedTier.PercentPerTier))
          .Append(",\"tiers\":[").Append(string.Join(",", tiers)).Append("]}");
        return sb.ToString();
    }

    /// <summary>
    /// A monster's HP at a difficulty tier, for the Damage panel: the manual override (a
    /// real-Dereth value, scaled), else that tier's appraisal, else that tier's learned pool
    /// (<paramref name="learned"/>), else - above tier 0 only - tier 0's scaled by 5% a tier,
    /// flagged as an estimate. At tier 0 this is the panel's old order exactly.
    /// </summary>
    private (double Hp, bool Manual, bool Estimated, string Source) HpAt(uint wcid, int difficulty, bool learned = true)
    {
        var store = _damageStore;
        if (store == null || wcid == 0) return (0, false, false, "");
        double scale = CreatureData.AwakenedTier.HealthScale(difficulty);
        double pct = difficulty * CreatureData.AwakenedTier.PercentPerTier;
        double manual = store.GetManualHp(wcid);
        if (manual > 0)
            return (manual * scale, true, false, difficulty > 0 ? $"set by you (real Dereth {manual:0}, +{pct:0}%)" : "set by you");
        CreatureData.CreatureProfile? prof = null;
        if (_creatureStore != null && _creatureStore.TryGetByWcid(wcid, out var pf)) prof = pf;
        uint appraised = _creatureStore?.MaxHealthAt(prof, difficulty) ?? 0;
        if (appraised > 0) return (appraised, false, false, "from the game");
        double pool = learned ? store.GetLearnedHp(wcid, difficulty) : 0;
        if (pool > 0) return (pool, false, false, "learned from kills");
        if (difficulty > 0)
        {
            double baseHp = prof != null && prof.MaxHealth > 0 ? prof.MaxHealth : store.GetLearnedHp(wcid, 0);
            if (baseHp > 0)
                return (baseHp * scale, false, true, $"estimated: real Dereth's {baseHp:0} +{pct:0}%");
        }
        return (0, false, false, "");
    }

    /// <summary>
    /// A weapon's name for the Damage tab: the full retail name with the material ("Silver
    /// Wand"), and the element when the Items list knows it ("Silver Wand (Fire)");
    /// "<paramref name="prefix"/> id" for an object the client no longer has.
    /// </summary>
    private string WeaponLabel(uint id, string prefix)
    {
        string n = WeaponNames.For(Host, _objectCache, unchecked((int)id), "");
        if (string.IsNullOrEmpty(n)) return prefix + " " + id;
        var rules = _dashboard?.Settings?.ItemRules;
        string elem = "";
        if (rules != null)
            foreach (var r in rules)
                if (r.Id == unchecked((int)id)) { elem = r.Element; break; }
        return WeaponNames.WithElement(n, elem);
    }

    private int _weaponElementTick;

    /// <summary>
    /// Twice a second: bring the Items list's weapon elements up to date (properties, the icon's
    /// element glow, a rending imbue, the name) and identify one listed weapon whose DamageType
    /// isn't known yet (paced by WeaponElementTracker; never while busy or in portal space).
    /// Runs whether or not the macro is on, so the Items panel shows the elements.
    /// </summary>
    private void TickWeaponElements()
    {
        if (++_weaponElementTick < 30) return;
        _weaponElementTick = 0;
        var cm = _combatManager;
        var settings = _dashboard?.Settings;
        var cache = _objectCache;
        if (cm == null || settings == null || cache == null) return;
        try
        {
            var rules = settings.ItemRules;
            int changed = WeaponList.RefreshElements(rules, (id, name) => cm.ElementTracker.Read(id, name), id => cache[id]?.Name);
            if (changed > 0)
                foreach (var r in rules)
                    if (r.ElementSource != WeaponList.SourceSet && r.ElementSource.Length > 0 && _loggedWeaponElements.Add((r.Id, r.Element)))
                        Host.Log($"[RynthAi] weapon 0x{(uint)r.Id:X8} '{r.Name}': element {r.Element} (from {r.ElementSource})");
            if ((DateTime.Now - _loginCompletedAt).TotalMilliseconds < 5000) return;
            if (_busyCount > 0 || (Host.HasIsPortaling && Host.IsPortaling())) return;
            var ids = new List<int>(rules.Count);
            foreach (var r in rules) if (r.Id != 0 && !WeaponList.IsShieldRule(r) && cache[r.Id] != null) ids.Add(r.Id);   // shields have no element to identify
            int asked = cm.ElementTracker.Pump(ids);
            if (asked != 0) Host.Log($"[RynthAi] identifying weapon 0x{(uint)asked:X8} '{cache[asked]?.Name}' for its element");
        }
        catch { }
    }

    private readonly HashSet<(int, string)> _loggedWeaponElements = new();

    /// <summary>Set (or clear, wid==0) the per-monster weapon override from the Damage panel, then persist.</summary>
    public void SetMonsterWeapon(uint wcid, uint weaponId)
    {
        if (_damageStore == null) return;
        _damageStore.SetManualWeapon(wcid, weaponId);
        _damageStore.SaveIfDirty();
    }

    /// <summary>Set (or clear, wid==0) the per-character DEFAULT weapon — the sweeping fallback every
    /// monster without its own weapon override uses. From the Damage panel's Default line. Persists.</summary>
    public void SetDefaultWeapon(uint weaponId)
    {
        if (_damageStore == null) return;
        _damageStore.SetDefaultWeapon(weaponId);
        _damageStore.SaveIfDirty();
    }

    /// <summary>Set (or clear, off==0) the per-monster offhand override from the Damage panel, then persist.</summary>
    public void SetMonsterOffhand(uint wcid, uint offhandId)
    {
        if (_damageStore == null) return;
        _damageStore.SetManualOffhand(wcid, offhandId);
        _damageStore.SaveIfDirty();
    }

    /// <summary>Set the per-monster pet choice from the Damage panel ("" = Auto, "E:Fire", "I:&lt;id&gt;"), then persist.</summary>
    public void SetMonsterPet(uint wcid, string choice)
    {
        if (_damageStore == null) return;
        _damageStore.SetManualPet(wcid, choice);
        _damageStore.SaveIfDirty();
    }

    /// <summary>Display text for a pet choice.</summary>
    private string PetChoiceLabel(string choice)
    {
        if (string.IsNullOrEmpty(choice)) return "Auto";
        if (choice.StartsWith("E:", StringComparison.Ordinal)) return choice.Substring(2);
        if (choice.StartsWith("I:", StringComparison.Ordinal) && int.TryParse(choice.AsSpan(2), out int id))
        {
            string name = _objectCache?[id]?.Name ?? "";
            if (name.Length == 0)
                name = _dashboard?.Settings.ConsumableRules.Find(r => r.Id == id)?.Name ?? ("Essence " + id);
            return name;
        }
        return choice;
    }

    /// <summary>
    /// Pet picker entries for the Damage panel, as [{"key":..,"name":..}]: Auto, each
    /// element, then every Pet essence in the Items panel with its element.
    /// </summary>
    public string BuildPetChoicesJson()
    {
        var sb = new System.Text.StringBuilder("[");
        void Add(string key, string name)
        {
            if (sb.Length > 1) sb.Append(',');
            sb.Append("{\"key\":").Append(JsonStr(key)).Append(",\"name\":").Append(JsonStr(name)).Append('}');
        }
        Add("", "Auto (weakest element)");
        foreach (string e in PetChoice.Elements) Add("E:" + e, e);
        var rules = _dashboard?.Settings.ConsumableRules;
        if (rules != null)
        {
            foreach (var r in rules)
            {
                if (!r.Type.Equals("Pet", StringComparison.OrdinalIgnoreCase)) continue;
                string elem = PetChoice.ElementOf(Host, unchecked((uint)r.Id), r.Name);
                Add("I:" + r.Id, elem.Length > 0 ? $"{r.Name} ({elem})" : r.Name);
            }
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>Master reset from the Damage panel: zero all learned stats, keep names + manual overrides. Persists.</summary>
    public void ClearMonsterStats()
    {
        if (_damageStore == null) return;
        _damageStore.ClearAllStats();
        _damageStore.SaveIfDirty();
    }

    /// <summary>Selectable weapons for the Damage-panel weapon/offhand pickers, as a JSON array
    /// [{"id":..,"name":..}], from the same configured-weapons (ItemRules) source the Monsters tab uses.</summary>
    public string BuildCombatWeaponsJson() => DashboardRenderer?.BuildCombatWeaponsJson() ?? "[]";

    /// <summary>Delete one learned row. key = "wcid:weaponId:element:tier" (from the JSON). Persists.</summary>
    public bool DeleteMonsterRow(string key)
    {
        if (_damageStore == null || string.IsNullOrEmpty(key)) return false;
        // A monster row's key is its bare wcid: remove the monster (learned data and its
        // settings), and from this session's seen list so it doesn't come straight back.
        // The ✕ did nothing before, since only "wcid:weapon:element:tier" keys were handled,
        // and monsters from another server couldn't be cleared (2026-09-29).
        if (uint.TryParse(key, out uint onlyWcid))
        {
            bool gone = _damageStore.DeleteWcid(onlyWcid);
            lock (_seenMonstersThisSession) gone |= _seenMonstersThisSession.Remove(onlyWcid);
            _damageStore.SaveIfDirty();
            return gone;
        }
        string[] p = key.Split(':');
        if (p.Length < 4) return false;
        if (!uint.TryParse(p[0], out uint wcid)) return false;
        if (!uint.TryParse(p[1], out uint wid)) return false;
        if (!int.TryParse(p[3], out int tier)) return false;
        bool ok = _damageStore.DeleteRow(wcid, wid, p[2], tier);
        _damageStore.SaveIfDirty();
        return ok;
    }

    private static string JsonNum(double d) => d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static string JsonStr(string? s)
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


    // Stable: PropertyInt indices used by the AC client appraisal table.
    private const uint PROP_INT_CREATURE_TYPE = 2;
    private const uint PROP_INT_ARMOR_LEVEL   = 28;

    // PropertyFloat indices for elemental resistance multipliers (1.0 = neutral).
    // PropertyFloat ResistSlash..ResistElectric (ACE). These were 168-174 (weapon auras), so
    // nothing ever read; ACE doesn't send creature resists on appraisal anyway, which is why
    // CreatureWeakness ships them. Kept for servers that do send them.
    private const uint PROP_FLOAT_RESIST_SLASH    = 64;
    private const uint PROP_FLOAT_RESIST_PIERCE   = 65;
    private const uint PROP_FLOAT_RESIST_BLUDGEON = 66;
    private const uint PROP_FLOAT_RESIST_FIRE     = 67;
    private const uint PROP_FLOAT_RESIST_COLD     = 68;
    private const uint PROP_FLOAT_RESIST_ACID     = 69;
    private const uint PROP_FLOAT_RESIST_ELECTRIC = 70;

    /// <summary>
    /// Once a second (AwakenedTier throttles): which server, and Aelrynth's tier where the player
    /// stands. The creature store follows the server; the damage store records under the tier.
    /// Off Aelrynth the tier stays 0, so nothing below changes there.
    /// </summary>
    private void TickAwakenedTier()
    {
        try
        {
            uint pid = _playerId != 0 ? _playerId : Host.GetPlayerId();
            bool wasActive = _tier.Active;
            int was = _tier.Current;
            _tier.Refresh(Host, pid);
            if (_damageStore != null) _damageStore.Difficulty = _tier.Current;

            if (_creatureStore != null && _tier.ServerKey.Length > 0
                && !string.Equals(_creatureStore.ServerKey, _tier.ServerKey, StringComparison.OrdinalIgnoreCase))
            {
                string? note = _creatureStore.SetServer(_tier.ServerKey);
                Log($"RynthAi: creature data for server '{_tier.ServerKey}'" + (note != null ? $" - {note}" : ""));
            }

            if (_tier.Active && (!wasActive || was != _tier.Current))
                Log($"RynthAi: Aelrynth difficulty tier {_tier.Current} - learned HP, casts to kill, accuracy and fight times are kept for this tier.");
        }
        catch (Exception ex)
        {
            Host.Log($"[RynthAi] TickAwakenedTier: {ex.Message}");
        }
    }

    private void CaptureCreatureSample(uint targetId, uint maxHealth)
    {
        if (_creatureStore == null) return;
        try
        {
            string name = string.Empty;
            if (Host.HasGetObjectName) Host.TryGetObjectName(targetId, out name);
            if (string.IsNullOrEmpty(name)) return;

            uint wcid = 0;
            if (Host.HasGetObjectWcid) Host.TryGetObjectWcid(targetId, out wcid);

            var sample = new CreatureData.CreatureProfile
            {
                Name = name,
                Wcid = wcid,
                MaxHealth = maxHealth,
            };

            if (Host.TryGetTargetVitals(targetId, out _, out _, out _, out uint maxStam, out _, out uint maxMana))
            {
                sample.MaxStamina = maxStam;
                sample.MaxMana = maxMana;
            }

            if (Host.TryGetObjectIntProperty(targetId, PROP_INT_CREATURE_TYPE, out int ctype))
                sample.CreatureType = ctype;
            if (Host.TryGetObjectIntProperty(targetId, PROP_INT_ARMOR_LEVEL, out int armor))
                sample.ArmorLevel = armor;

            if (Host.TryGetObjectDoubleProperty(targetId, PROP_FLOAT_RESIST_SLASH, out double rs))    sample.ResistSlash = rs;
            if (Host.TryGetObjectDoubleProperty(targetId, PROP_FLOAT_RESIST_PIERCE, out double rp))   sample.ResistPierce = rp;
            if (Host.TryGetObjectDoubleProperty(targetId, PROP_FLOAT_RESIST_BLUDGEON, out double rb)) sample.ResistBludgeon = rb;
            if (Host.TryGetObjectDoubleProperty(targetId, PROP_FLOAT_RESIST_FIRE, out double rf))     sample.ResistFire = rf;
            if (Host.TryGetObjectDoubleProperty(targetId, PROP_FLOAT_RESIST_COLD, out double rc))     sample.ResistCold = rc;
            if (Host.TryGetObjectDoubleProperty(targetId, PROP_FLOAT_RESIST_ACID, out double ra))     sample.ResistAcid = ra;
            if (Host.TryGetObjectDoubleProperty(targetId, PROP_FLOAT_RESIST_ELECTRIC, out double re)) sample.ResistElectric = re;

            if (Host.HasGetObjectSpellIds)
            {
                var buf = new uint[64];
                int n = Host.GetObjectSpellIds(targetId, buf, buf.Length);
                for (int i = 0; i < n && i < buf.Length; i++)
                {
                    if (buf[i] != 0) sample.KnownSpellIds.Add(buf[i]);
                }
            }

            TickAwakenedTier();   // the right server's file and tier before the first write (throttled)
            _creatureStore.Upsert(sample, _tier.Current);

            // Remember this monster type so the Damage table shows it (as a bare row with
            // its appraised HP) even before we've fought it — populated as nearby mobs are ID'd.
            if (wcid != 0)
                lock (_seenMonstersThisSession) _seenMonstersThisSession.Add(wcid);
        }
        catch (Exception ex)
        {
            Host.Log($"[RynthAi] CaptureCreatureSample exception: {ex.Message}");
        }
    }

    public override void OnEnchantmentAdded(uint spellId, double durationSeconds)
    {
        _buffManager?.OnEnchantmentAdded(spellId, durationSeconds);
    }

    public override void OnEnchantmentRemoved(uint enchantmentId)
    {
        _buffManager?.OnEnchantmentRemoved(enchantmentId);
    }

    public override void OnChatBarEnter(string? text, ref int eat)
    {
        if (!_initialized || !_loginComplete || string.IsNullOrEmpty(text))
            return;

        string trimmed = text.Trim();
        if (TryEatOutboundTranslation(trimmed))
        {
            eat = 1;
            return;
        }
        NoteChatBarLineForMetaSchedule(trimmed);

        // Mag-Tools /mt command compatibility
        if (trimmed.StartsWith("/mt ", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/mt", StringComparison.OrdinalIgnoreCase))
        {
            eat = 1;
            if (!HandleMtCommand(trimmed))
                ChatLine($"[RynthAi] Unrecognized /mt command. Try /mt opt list");
            return;
        }

        // UtilityBelt /ub command compatibility
        if (trimmed.StartsWith("/ub ", StringComparison.OrdinalIgnoreCase))
        {
            eat = 1;
            if (!HandleUbCommand(trimmed))
                ChatLine("[RynthAi] Unrecognized /ub command.");
            return;
        }

        // VirindiTank /vt command compatibility — option toggles (combat/doors/
        // looting/navboost), meta/nav/loot load, setmetastate. Translated
        // natively to RynthAi settings; no VTank/Decal required. Previously only
        // reachable from meta execution, so /vt chat WAYPOINTS silently no-op'd.
        if (trimmed.StartsWith("/vt ", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/vt", StringComparison.OrdinalIgnoreCase))
        {
            eat = 1;
            if (!(_metaManager?.TryHandleVtCommand(trimmed) ?? false))
                ChatLine("[RynthAi] Unrecognized /vt command.");
            return;
        }

        if (!trimmed.StartsWith("/ra", StringComparison.OrdinalIgnoreCase))
            return;

        string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Bare "/ra" or "/ra help" — show command list
        if (parts.Length < 2 || parts[1].Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            eat = 1;
            HandleHelpCommand();
            return;
        }

        string cmd = parts[1].ToLower();
        eat = 1;
        DispatchRaCommand(cmd, parts, ref eat);
    }

    /// <summary>
    /// Called by RynthChatUi when the user submits a line. Slash commands we
    /// own (/ra, /mt, /ub) are routed through OnChatBarEnter so they run the
    /// same code path as the retail chatbar. Anything else falls through to
    /// Host.InvokeChatParser (say/tell/channels via direct Event_* calls).
    /// </summary>
    private void HandleRynthChatSubmit(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        int eat = 0;
        try { OnChatBarEnter(text, ref eat); }
        catch (Exception ex)
        {
            Host.Log($"[RynthAi] RynthChat OnChatBarEnter threw: {ex.GetType().Name}: {ex.Message}");
        }

        if (eat != 0)
        {
            Host.Log($"[ChatDiag] '{text}' eaten locally (eat={eat})");
            return; // handled locally (/ra, /mt, /ub, etc.)
        }

        if (Host.HasInvokeChatParser)
        {
            bool r = Host.InvokeChatParser(text);   // DIAG: does the engine report the send succeeded?
            Host.Log($"[ChatDiag] InvokeChatParser('{text}') -> {r}");
        }
        else Host.Log($"[ChatDiag] '{text}': Host.HasInvokeChatParser=FALSE (no send path wired)");
    }

    internal void EnqueueGive(uint itemId, uint targetId, int stackSize)
        => _pendingGives.Enqueue((itemId, targetId, stackSize));

    internal void CancelGiveQueue()
    {
        int count = _pendingGives.Count;
        _pendingGives.Clear();
        ChatLine(count > 0
            ? $"[RynthAi] Give queue cancelled ({count} item(s) remaining)."
            : "[RynthAi] Give queue is already empty.");
    }

    private void DrainGiveQueue()
    {
        if (_pendingGives.Count == 0) return;
        int intervalMs = _dashboard?.Settings.GiveQueueIntervalMs ?? 150;
        if ((DateTime.UtcNow - _lastGiveAt).TotalMilliseconds < intervalMs) return;

        var (itemId, targetId, stackSize) = _pendingGives.Dequeue();
        Host.MoveItemExternal(itemId, targetId, stackSize);
        _lastGiveAt = DateTime.UtcNow;

        if (_pendingGives.Count == 0)
            ChatLine("[RynthAi] Give queue complete.");
    }

    /// <summary>
    /// Called by MetaManager for ChatCommand actions that start with /ra,
    /// so meta scripts can dispatch /ra commands without going through InvokeChatParser.
    /// </summary>
    private void HandleRaCommand(string fullCommand)
    {
        if (!_initialized || !_loginComplete) return;

        string[] parts = fullCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return;

        string cmd = parts[1].ToLower();
        int eat = 0; // not used for meta dispatch, but needed for shared helpers
        DispatchRaCommand(cmd, parts, ref eat);
    }

    private void DispatchRaCommand(string cmd, string[] parts, ref int eat)
    {
        // Need trimmed original for commands that use it
        string trimmed = string.Join(" ", parts);
        switch (cmd)
        {
            case "power":        HandlePowerCommand(parts); break;
            case "offhand":      HandleOffhandCommand(parts); break;
            case "raycast":      HandleRaycastCommand(parts); break;
            case "lostest":      HandleLosTestCommand(parts); break;
            case "landtest":     HandleLandTestCommand(); break;
            case "rayland":      HandleRaylandCommand(); break;
            case "cast":         HandleCastCommand(parts); break;
            case "cache":        HandleCacheCommand(); break;
            case "cache2":       HandleCache2Command(); break;
            case "buffs":
                if (_buffManager == null) { ChatLine("[RynthAi] BuffManager not ready (not logged in yet)."); break; }
                if (parts.Length >= 3 && parts[2].Equals("item", StringComparison.OrdinalIgnoreCase))
                    _buffManager.PrintItemBuffDebug();
                else if (parts.Length >= 3 && parts[2].Equals("tiers", StringComparison.OrdinalIgnoreCase))
                    _buffManager.PrintBuffTierDebug();
                else
                    _buffManager.PrintBuffDebug();
                break;
            case "attackable":   HandleAttackableCommand(); break;
            case "mexec":        HandleMexecCommand(parts); break;
            case "listvars":     HandleListVarsCommand(); break;
            case "listpvars":    HandleListPvarsCommand(); break;
            case "listgvars":    HandleListGvarsCommand(); break;
            case "dumpprops":    HandleDumpPropsCommand(parts); break;
            case "version":      HandleVersionCommand(); break;
            case "wielded":      HandleWieldedCommand(); break;
            case "scan":         HandleScanCommand(); break;
            case "buildinfo":    HandleBuildInfoCommand(); break;
            case "navdebug":     HandleNavDebugCommand(); break;
            case "navrec":       HandleNavRecordCommand(parts); break;
            case "navhud":       HandleNavHudCommand(parts); break;
            case "navtrail":     HandleNavTrailCommand(parts); break;
            case "navoverlay":   HandleNavOverlayCommand(parts); break;
            case "debug":        HandleDebugCommand(parts); break;
            case "trace":        HandleTraceCommand(parts); break;
            case "logs":         HandleLogsCommand(parts); break;
            case "hub":
            case "quests":
            case "pets":
            case "guardian":
                if (_iltHub == null) { ChatLine("[RynthAi] ILT Hub not ready (log in first)."); break; }
                _iltHub.HandleCommand(cmd, parts.Length > 2 ? parts[2..] : Array.Empty<string>());
                break;
            case "itemhud" when parts.Length > 2 && parts[2].Equals("add", StringComparison.OrdinalIgnoreCase):
                HandleItemHudAdd(string.Join(" ", parts, 3, parts.Length - 3));
                break;
            case "huds":
            case "itemhud":
            case "remote":
            case "miniremote":   HandleHudCommand(cmd, parts.Length > 2 ? parts[2] : string.Empty); break;
            case "translate":
            case "tr":           HandleTranslateCommand(parts); break;
            case "iteminfo":
            case "ii":           HandleItemInfoCommand(parts); break;
            case "groundloot":   HandleGroundLootCommand(parts); break;
            case "addnavpt":     HandleAddNavPointCommand(); break;
            case "nav":          HandleNavCommand(parts); break;
            case "lua":          ForwardLuaCommand(parts); break;
            case "items":
                if (parts.Length >= 3 && parts[2].Equals("fill", StringComparison.OrdinalIgnoreCase))
                    ChatLine(_dashboard?.FillItemsFromPack(_objectCache, _playerId) ?? "[RynthAi] Settings not ready.");
                else
                    ChatLine("[RynthAi] /ra items fill — add every weapon, healing kit, lockpick and pet essence in your pack that isn't listed yet.");
                break;
            case "follow":       HandleFollowCommand(parts); break;
            case "myquests":
            case "refreshquests": _questTracker?.Refresh(); ChatLine("[RynthAi] Quest flag refresh requested."); break;
            case "metamgr":       QueueMetaMgrCommand(parts); break;   // Meta Manager: applied on the next tick
            case "dunnav":        HandleDungeonNavCommand(parts); break;
            case "dunnav-patrol": HandleDungeonNavPatrolCommand(parts); break;
            case "hazard":        HandleHazardCommand(parts); break;
            case "lootparse":    HandleLootParseCommand(trimmed); break;
            case "lootcheckinv": HandleLootCheckInventoryCommand(trimmed); break;
            case "lootcheck":    HandleLootCheckSelectedCommand(parts); break;
            case "loot":         HandleLootCommand(parts); break;
            case "corpseinfo":   HandleCorpseInfoCommand(); break;
            case "corpsecheck":  HandleCorpseCheckCommand(parts); break;
            case "corpseopen":   HandleCorpseOpenCommand(); break;
            case "fellow":
            case "fellowship":   HandleFellowshipCommand(parts); break;
            case "fellowinfo":   HandleFellowshipInfoCommand(); break;
            case "dumpinv":      HandleDumpInventoryCommand(); break;
            case "combat":       HandleCombatStateCommand(); break;
            case "why":          HandleWhyCommand(); break;
            case "start":
            case "resume":
            case "stop":
            case "pause":        HandleMacroRunCommand(cmd, parts); break;
            case "vtankyield":   HandleVTankYieldCommand(parts); break;
            case "navstate":     HandleNavStateCommand(); break;
            case "salvstate":    HandleSalvageStateCommand(); break;
            case "mapdump":      HandleMapDumpCommand(); break;
            case "clearbusy":    HandleClearBusyCommand(); break;
            case "panic":        HandlePanicCommand(); break;
            case "forcebuff":        HandleForceBuff(); break;
            case "cancelforcebuff":  HandleCancelForceBuff(); break;
            case "bufftest":
                if (_buffManager == null) { ChatLine("[RynthAi] BuffManager not ready (not logged in yet)."); break; }
                _buffManager.EnableCastRegistryDiagnostic = !_buffManager.EnableCastRegistryDiagnostic;
                if (_buffManager.EnableCastRegistryDiagnostic)
                {
                    ChatLine("[RynthAi] BuffTest ON — fires on next item-spell cast.");
                    ChatLine("[RynthAi]   Cast an Impen/Bane (via macro or manually). Diff logs to RynthCore.log with [BuffTest] tag.");
                    ChatLine("[RynthAi]   Auto-disables after one cast/diff.");
                }
                else
                {
                    ChatLine("[RynthAi] BuffTest OFF.");
                }
                break;
            case "settings":     HandleSettingsCommand(parts); break;
            case "autotrade":    HandleAutoTradeRaCommand(parts); break;
            case "trade":        HandleTradeCommand(parts); break;
            case "busyinfo":     HandleBusyInfoCommand(); break;
            // give variants — first-match (with optional count prefix)
            case "give":         HandleGiveCommand(parts, GiveItemMatch.Exact,   partialPlayer: false); break;
            case "givep":        HandleGiveCommand(parts, GiveItemMatch.Partial, partialPlayer: false); break;
            case "givexp":       HandleGiveCommand(parts, GiveItemMatch.Exact,   partialPlayer: true);  break;
            case "givepp":       HandleGiveCommand(parts, GiveItemMatch.Partial, partialPlayer: true);  break;
            case "giver":        HandleGiveCommand(parts, GiveItemMatch.Regex,   partialPlayer: false); break;
            // give-All variants — give every matching stack (sub-command "stop" cancels the queue)
            case "givea":
                if (parts.Length >= 3 && parts[2].Equals("stop", StringComparison.OrdinalIgnoreCase)) CancelGiveQueue();
                else HandleGiveCommand(parts, GiveItemMatch.Exact,   partialPlayer: false, allItems: true);
                break;
            case "giveap":
                if (parts.Length >= 3 && parts[2].Equals("stop", StringComparison.OrdinalIgnoreCase)) CancelGiveQueue();
                else HandleGiveCommand(parts, GiveItemMatch.Partial, partialPlayer: false, allItems: true);
                break;
            case "giveaxp":
                if (parts.Length >= 3 && parts[2].Equals("stop", StringComparison.OrdinalIgnoreCase)) CancelGiveQueue();
                else HandleGiveCommand(parts, GiveItemMatch.Exact,   partialPlayer: true,  allItems: true);
                break;
            case "gap":          // short alias for giveapp (give all, partial item + partial player)
            case "giveapp":
                if (parts.Length >= 3 && parts[2].Equals("stop", StringComparison.OrdinalIgnoreCase)) CancelGiveQueue();
                else HandleGiveCommand(parts, GiveItemMatch.Partial, partialPlayer: true,  allItems: true);
                break;
            case "givear":
                if (parts.Length >= 3 && parts[2].Equals("stop", StringComparison.OrdinalIgnoreCase)) CancelGiveQueue();
                else HandleGiveCommand(parts, GiveItemMatch.Regex,   partialPlayer: false, allItems: true);
                break;
            case "ig":           HandleGiveProfileCommand(parts, partialPlayer: false); break;
            case "igp":          HandleGiveProfileCommand(parts, partialPlayer: true); break;
            // use variants
            case "use":          HandleUseCommand(parts, inv: true,  land: true,  partial: false); break;
            case "usei":         HandleUseCommand(parts, inv: true,  land: false, partial: false); break;
            case "usel":         HandleUseCommand(parts, inv: false, land: true,  partial: false); break;
            case "usepi": case "useip": HandleUseCommand(parts, inv: true,  land: false, partial: true); break;
            case "uselp": case "usepl": HandleUseCommand(parts, inv: false, land: true,  partial: true); break;
            case "usep":         HandleUseCommand(parts, inv: true,  land: true,  partial: true); break;
            // select variants
            case "select":       HandleSelectCommand(parts, inv: true,  land: true,  partial: false); break;
            case "selecti":      HandleSelectCommand(parts, inv: true,  land: false, partial: false); break;
            case "selectl":      HandleSelectCommand(parts, inv: false, land: true,  partial: false); break;
            case "selectpi": case "selectip": HandleSelectCommand(parts, inv: true,  land: false, partial: true); break;
            case "selectlp": case "selectpl": HandleSelectCommand(parts, inv: false, land: true,  partial: true); break;
            case "selectp":      HandleSelectCommand(parts, inv: true,  land: true,  partial: true); break;
            default:
                if (cmd.StartsWith("jump", StringComparison.Ordinal))
                {
                    HandleJumpCommand(cmd, parts);
                    break;
                }
                eat = 0;
                break;
        }
    }
}
