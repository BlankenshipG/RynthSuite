using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.PluginSdk;

namespace RynthCore.RynthAiHostTests.Fakes;

/// <summary>
/// An in-process stand-in for the engine. The real RynthCoreHost is a struct of native function
/// pointers; here each pointer is an [UnmanagedCallersOnly] static method that answers from the
/// static state below, so tested code gets the answers a test sets up and nothing reaches a game.
///
/// Deliberately NOT faked:
///   * GetPlayerId, unless a test asks for it with Create(playerId): ExpressionEngine's getgame*
///     functions read raw client memory (0x008379A8) once GetPlayerId returns non-zero, which
///     would crash this process. With no GetPlayerId they return the calendar for a zero clock,
///     which the tests pin. Buff tests (no expressions) use the opt-in.
///   * GetContainerContents: WorldObjectCache then builds its inventory from objects it already
///     knows (see <see cref="MakeCache"/>).
///   * Most action calls (combat mode, ...) stay no-ops. The nav, buff and weapon calls are
///     recorded: SetAutoRun, TurnToHeading (the fake turns instantly), JumpNonAutonomous,
///     CastSpell, UseObject and RequestId.
///
/// State is static because function pointers cannot capture; call <see cref="Reset"/> first.
/// </summary>
internal static unsafe class FakeHost
{
    // ── State a test sets up ──────────────────────────────────────────────────
    public static readonly Dictionary<uint, string> Names = new();
    public static readonly Dictionary<uint, (uint Cell, float X, float Y, float Z)> Positions = new();
    public static readonly Dictionary<uint, uint> ItemTypes = new();
    public static readonly Dictionary<(uint Id, uint Key), int> Ints = new();
    public static readonly Dictionary<(uint Id, uint Key), double> Doubles = new();
    public static readonly Dictionary<(uint Id, uint Key), long> Quads = new();
    public static readonly Dictionary<(uint Id, uint Key), bool> Bools = new();
    public static readonly Dictionary<(uint Id, uint Key), string> Strings = new();
    public static readonly Dictionary<(uint Id, uint Skill), (int Buffed, int Training)> Skills = new();
    public static readonly Dictionary<(uint Id, uint Skill, int Raw), int> SkillLevels = new();
    public static readonly Dictionary<(uint Id, uint Attr, int Raw), uint> Attributes = new();
    public static readonly Dictionary<uint, uint[]> SpellIds = new();
    /// <summary>Data id properties (STypeDID): (object, key) -> value, e.g. a scroll's Spell 28.</summary>
    public static readonly Dictionary<(uint Id, uint Key), uint> DataIds = new();
    public static readonly Dictionary<uint, uint[]> Palettes = new();
    public static readonly HashSet<uint> KnownSpells = new();
    public static readonly HashSet<uint> NotAttackable = new();
    public static readonly List<(uint SpellId, double Expiry)> Enchantments = new();
    /// <summary>Item id -> container id. When it has entries at Create() time the host also gets
    /// GetObjectOwnershipInfo (opt-in, so tests written without it see no ownership, as before).</summary>
    public static readonly Dictionary<uint, uint> Containers = new();
    /// <summary>Object id -> PublicWeenieDesc bitfield (0x2000 corpse, 0x8 player, ...). When it has
    /// entries at Create() time the host also gets GetObjectBitfield (opt-in, as Containers).</summary>
    public static readonly Dictionary<uint, uint> Bitfields = new();
    /// <summary>The api struct the last Create() built (a test attaching a plugin needs it).</summary>
    public static RynthCoreApiNative LastApi;

    public static double ServerTime;
    public static float Vitae = 1.0f;
    public static bool Portaling;
    public static (uint Cell, float X, float Y, float Z) PlayerPose;
    public static bool HasPose = true;
    public static (uint Hp, uint MaxHp, uint Stam, uint MaxStam, uint Mana, uint MaxMana) Vitals;
    public static (uint Hp, uint Stam, uint Mana) BaseVitals;
    public static string WorldName = "";
    public static string AccountName = "";
    /// <summary>The player's facing, 0 = north, clockwise (read back through the pose quaternion).</summary>
    public static double HeadingDeg;
    public static readonly Dictionary<uint, uint> Wcids = new();
    /// <summary>The character's spellbook as ReadKnownSpells returns it (empty = snapshot cold).</summary>
    public static readonly List<uint> Spellbook = new();
    /// <summary>Only used when Create(playerId: ...) installs GetPlayerId (see the class note).</summary>
    public static uint PlayerIdValue;

    // ── What tested code sent to the engine ───────────────────────────────────
    public static readonly List<string> Logs = new();
    public static readonly List<string> Chat = new();
    public static readonly List<string> ChatCommands = new();
    /// <summary>Every SetAutoRun call, in order; AutoRun is the last value sent.</summary>
    public static readonly List<bool> AutoRunCalls = new();
    public static bool AutoRun;
    public static int Jumps;
    public static int Turns;
    public static readonly List<(uint Target, int SpellId)> Casts = new();
    /// <summary>Opt-in: wires GiveObjectTo (AC's give action); every call lands in <see cref="Gives"/>.</summary>
    public static bool GiveCalls;
    /// <summary>Every GiveObjectTo (item, target, amount; 0 = the whole object), in order.</summary>
    public static readonly List<(uint Item, uint Target, int Amount)> Gives = new();
    /// <summary>Opt-in: wires StopCompletely (off by default so no other test sees it).</summary>
    public static bool StopCalls;
    /// <summary>StopCompletely calls (opt-in with <see cref="StopCalls"/>).</summary>
    public static int Stops;
    /// <summary>Every UseObject (a wield, a use) and RequestId (an identify), in order.</summary>
    public static readonly List<uint> Uses = new();
    public static readonly List<uint> IdRequests = new();
    /// <summary>Every MoveItemInternal (an item into a pack), in order (opt-in with <see cref="WeaponCalls"/>).</summary>
    public static readonly List<(uint Id, uint Pack, int Amount)> Moves = new();
    /// <summary>Objects the fake says have appraisal data (HasAppraisalData).</summary>
    public static readonly HashSet<uint> Appraised = new();
    /// <summary>Opt-in (set before Create): UseObject, RequestId, HasAppraisalData and
    /// GetCurrentCombatMode (answering <see cref="CombatModeValue"/>). Tests written without
    /// them keep seeing those calls as absent.</summary>
    public static bool WeaponCalls;
    /// <summary>The engine's VTank signal (API v74 GetVTankState flags: 1 = watching, 2 = running);
    /// -1 = not installed (an engine before v74). Opt-in: set before Create().</summary>
    public static int VTankFlags = -1;
    /// <summary>Opt-in (set before Create): the server gates combat paces casts on, GetUseDoneSeq
    /// (answering <see cref="UseDoneSeq"/>) and GetCastBusyState (answering <see cref="CastBusy"/>).</summary>
    public static bool ServerGates;
    /// <summary>The count of server UseDone (0x01C7) events the fake reports.</summary>
    public static int UseDoneSeq;
    /// <summary>The error code of the last UseDone (GetLastUseDone, v70; 0 = completed, 0x1D = too busy).
    /// Opt-in with <see cref="LastUseDoneCalls"/> (set before Create, together with ServerGates).</summary>
    public static uint LastUseDoneError;
    public static bool LastUseDoneCalls;
    /// <summary>One more UseDone with <paramref name="error"/> (0 = the action completed).</summary>
    public static void UseDone(uint error) { UseDoneSeq++; LastUseDoneError = error; }
    /// <summary>1 while a cast gesture animates (GetCastBusyState), else 0.</summary>
    public static int CastBusy;
    public static int VTankSeq;
    public static int CombatModeValue = 1;
    /// <summary>Opt-in (set before Create): WieldItem (a wield into a named slot, API v70+). The
    /// host then reports API version 73, so only tests that ask for it see the newer calls.</summary>
    public static bool WieldCalls;
    /// <summary>Every WieldItem (item, EquipMask slot), in order (opt-in with <see cref="WieldCalls"/>).</summary>
    public static readonly List<(uint Id, uint Mask)> Wields = new();
    /// <summary>Opt-in (set before Create): UseObject, MoveItemExternal, the salvage panel
    /// (open / add / execute) and GetObjectWielderInfo (answering from <see cref="Wielded"/>).</summary>
    public static bool SalvageCalls;
    /// <summary>Every MoveItemExternal (item, target container, amount), in order (opt-in with <see cref="SalvageCalls"/>).</summary>
    public static readonly List<(uint Id, uint Target, int Amount)> ExternalMoves = new();
    /// <summary>Every SalvagePanelAddItem, in order.</summary>
    public static readonly List<uint> SalvageAdds = new();
    /// <summary>The items added since the last execute, each SalvagePanelExecute, in order.</summary>
    public static readonly List<List<uint>> SalvageExecutes = new();
    /// <summary>Items SalvagePanelAddItem turns down (the panel refuses them).</summary>
    public static readonly HashSet<uint> RefuseSalvageAdd = new();
    /// <summary>Called on SalvagePanelExecute with what was added since the last one (the test
    /// removes the salvaged items from its world here).</summary>
    public static Action<List<uint>>? OnSalvageExecute;
    /// <summary>Item id -> wielded location: GetObjectWielderInfo says the player wields it there.</summary>
    public static readonly Dictionary<uint, uint> Wielded = new();
    private static readonly List<uint> _panelItems = new();
    /// <summary>Opt-in (set before Create): CloseContainer (API v76; the host then reports at
    /// least v76) and GetGroundContainerId (answering <see cref="GroundContainer"/>).</summary>
    public static bool CloseContainerCalls;
    /// <summary>Every CloseContainer (container id), in order (opt-in with <see cref="CloseContainerCalls"/>).</summary>
    public static readonly List<uint> Closes = new();
    /// <summary>The container the client has open (GetGroundContainerId), 0 = none.</summary>
    public static uint GroundContainer;
    /// <summary>The item selected in the game (GetSelectedItemId); 0 = none.</summary>
    public static uint SelectedItem;
    /// <summary>Opt-in: install SendPluginCommand (the engine's plugin-command broker, API v64).</summary>
    public static bool PluginCommandCalls;
    /// <summary>What SendPluginCommand answers: true = delivered (the target plugin is loaded).</summary>
    public static bool PluginCommandDelivered = true;
    /// <summary>Every SendPluginCommand (plugin, action, value), in order.</summary>
    public static readonly List<(string Plugin, string Action, string Value)> PluginCommands = new();

    // ANSI strings handed back to tested code stay alive for the whole run.
    private static readonly Dictionary<string, IntPtr> _ansi = new();

    public static void Reset()
    {
        Names.Clear(); Positions.Clear(); ItemTypes.Clear();
        Ints.Clear(); Doubles.Clear(); Quads.Clear(); Bools.Clear(); Strings.Clear();
        Skills.Clear(); SkillLevels.Clear(); Attributes.Clear();
        SpellIds.Clear(); DataIds.Clear(); Palettes.Clear(); KnownSpells.Clear(); NotAttackable.Clear(); Enchantments.Clear();
        Containers.Clear(); Bitfields.Clear();
        ServerTime = 0; Vitae = 1.0f; Portaling = false; PlayerPose = default; HasPose = true;
        Vitals = default; BaseVitals = default; WorldName = ""; AccountName = "";
        HeadingDeg = 0; Wcids.Clear(); Spellbook.Clear(); PlayerIdValue = 0;
        Logs.Clear(); Chat.Clear(); ChatCommands.Clear();
        AutoRunCalls.Clear(); AutoRun = false; Jumps = 0; Turns = 0; Casts.Clear(); StopCalls = false; Stops = 0; GiveCalls = false; Gives.Clear();
        Uses.Clear(); IdRequests.Clear(); Moves.Clear(); Appraised.Clear(); WeaponCalls = false; CombatModeValue = 1;
        VTankFlags = -1; VTankSeq = 0; LogCostMicros = 0;
        ServerGates = false; UseDoneSeq = 0; CastBusy = 0; LastUseDoneError = 0; LastUseDoneCalls = false;
        WieldCalls = false; Wields.Clear();
        SalvageCalls = false; ExternalMoves.Clear(); SalvageAdds.Clear(); SalvageExecutes.Clear();
        RefuseSalvageAdd.Clear(); OnSalvageExecute = null; Wielded.Clear(); _panelItems.Clear();
        CloseContainerCalls = false; Closes.Clear(); GroundContainer = 0;
        SelectedItem = 0;
        PluginCommandCalls = false; PluginCommandDelivered = true; PluginCommands.Clear();
    }

    /// <summary>
    /// Puts the player at nav coordinates (ns, ew) on the landscape: the landblock cell and
    /// local x/y that NavCoordinateHelper converts back to the same coordinates.
    /// </summary>
    public static void SetNavPosition(double ns, double ew, float z = 0f)
    {
        PlayerPose = (NavCell(ns, ew, out float x, out float y), x, y, z);
    }

    /// <summary>The outdoor cell id holding (ns, ew), and the local x/y in it.</summary>
    public static uint NavCell(double ns, double ew, out float x, out float y)
    {
        double gx = (ew * 10.0 + 1019.5) * 24.0, gy = (ns * 10.0 + 1019.5) * 24.0;
        uint lbX = (uint)(gx / 192.0), lbY = (uint)(gy / 192.0);
        x = (float)(gx - lbX * 192.0);
        y = (float)(gy - lbY * 192.0);
        uint cell = (uint)(x / 24f) * 8 + (uint)(y / 24f) + 1;   // outdoor cells 0x0001-0x0040
        return (lbX << 24) | (lbY << 16) | cell;
    }

    /// <summary>
    /// A host whose faked calls answer from the state above. <paramref name="playerId"/> non-zero
    /// also installs GetPlayerId; never do that for a test that runs expressions (see the class note).
    /// </summary>
    public static RynthCoreHost Create(uint playerId = 0)
    {
        var api = new RynthCoreApiNative { Version = RynthCoreHost.CurrentApiVersion };
        PlayerIdValue = playerId;
        if (playerId != 0)
            api.GetPlayerIdFn       = (IntPtr)(delegate* unmanaged[Cdecl]<uint>)&GetPlayerId;
        if (Containers.Count > 0)
            api.GetObjectOwnershipInfoFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, uint*, uint*, int>)&GetObjectOwnershipInfo;
        if (Bitfields.Count > 0)
            api.GetObjectBitfieldFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint>)&GetObjectBitfield;
        api.SetAutoRunFn            = (IntPtr)(delegate* unmanaged[Cdecl]<int, int>)&SetAutoRun;
        api.TurnToHeadingFn         = (IntPtr)(delegate* unmanaged[Cdecl]<float, int>)&TurnToHeading;
        api.JumpNonAutonomousFn     = (IntPtr)(delegate* unmanaged[Cdecl]<float, int>)&JumpNonAutonomous;
        api.CastSpellFn             = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int, int>)&CastSpell;
        if (StopCalls)
            api.StopCompletelyFn    = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&StopCompletely;
        if (GiveCalls)
            api.GiveObjectToFn      = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int, int>)&GiveObjectTo;
        if (WeaponCalls)
        {
            api.UseObjectFn            = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&UseObject;
            api.RequestIdFn            = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&RequestId;
            api.HasAppraisalDataFn     = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&HasAppraisalData;
            api.GetCurrentCombatModeFn = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&GetCurrentCombatMode;
            api.MoveItemInternalFn     = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int, int, int>)&MoveItemInternal;
        }
        if (SalvageCalls)
        {
            api.UseObjectFn            = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&UseObject;
            api.MoveItemExternalFn     = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int, int>)&MoveItemExternal;
            api.SalvagePanelOpenFn     = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&SalvagePanelOpen;
            api.SalvagePanelAddItemFn  = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&SalvagePanelAddItem;
            api.SalvagePanelExecuteFn  = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&SalvagePanelExecute;
            api.GetObjectWielderInfoFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, uint*, int>)&GetObjectWielderInfo;
        }
        if (WieldCalls)
        {
            api.Version     = 73;
            api.WieldItemFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int>)&WieldItem;
        }
        api.GetObjectWcidFn         = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint>)&GetObjectWcid;
        api.GetSelectedItemIdFn     = (IntPtr)(delegate* unmanaged[Cdecl]<uint>)&GetSelectedItemId;
        api.ReadKnownSpellsFn       = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, int, int>)&ReadKnownSpells;
        api.LogFn                   = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&Log;
        api.WriteToChatFn           = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, int>)&WriteToChat;
        api.InvokeChatParserFn      = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int>)&InvokeChatParser;
        api.GetObjectNameFn         = (IntPtr)(delegate* unmanaged[Cdecl]<uint, IntPtr>)&GetObjectName;
        api.GetObjectPositionFn     = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, float*, float*, float*, int>)&GetObjectPosition;
        api.GetItemTypeFn           = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, int>)&GetItemType;
        api.ObjectIsAttackableFn    = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&ObjectIsAttackable;
        api.GetObjectIntPropertyFn  = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int*, int>)&GetObjectIntProperty;
        api.GetObjectDoublePropertyFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, double*, int>)&GetObjectDoubleProperty;
        api.GetObjectQuadPropertyFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, long*, int>)&GetObjectQuadProperty;
        api.GetObjectBoolPropertyFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int*, int>)&GetObjectBoolProperty;
        api.GetObjectStringPropertyFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, IntPtr>)&GetObjectStringProperty;
        api.GetObjectSkillFn        = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int*, int*, int>)&GetObjectSkill;
        api.GetObjectSkillBuffedFn  = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int, int*, int>)&GetObjectSkillLevel;
        api.GetObjectAttributeFn    = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int, uint*, int>)&GetObjectAttribute;
        api.GetObjectSpellIdsFn     = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, int, int>)&GetObjectSpellIds;
        api.GetObjectDataIdPropertyFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, uint*, int>)&GetObjectDataIdProperty;
        api.GetObjectPalettesFn     = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, uint*, int, int>)&GetObjectPalettes;
        api.IsSpellKnownFn          = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int>)&IsSpellKnown;
        api.ReadPlayerEnchantmentsFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, double*, int, int>)&ReadPlayerEnchantments;
        api.GetServerTimeFn         = (IntPtr)(delegate* unmanaged[Cdecl]<double>)&GetServerTime;
        api.GetVitaeFn              = (IntPtr)(delegate* unmanaged[Cdecl]<uint, float>)&GetVitae;
        api.IsPortalingFn           = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&IsPortaling;
        api.GetPlayerPoseFn         = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, float*, float*, float*, float*, float*, float*, float*, int>)&GetPlayerPose;
        api.GetPlayerVitalsFn       = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, uint*, uint*, uint*, uint*, uint*, int>)&GetPlayerVitals;
        api.GetPlayerBaseVitalsFn   = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, uint*, uint*, int>)&GetPlayerBaseVitals;
        api.GetWorldNameFn          = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr>)&GetWorldName;
        api.GetAccountNameFn        = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr>)&GetAccountName;
        if (ServerGates)
        {
            api.GetUseDoneSeqFn    = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&GetUseDoneSeq;
            api.GetCastBusyStateFn = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&GetCastBusyState;
            if (LastUseDoneCalls)
                api.GetLastUseDoneFn = (IntPtr)(delegate* unmanaged[Cdecl]<int*, uint*, int>)&GetLastUseDone;
        }
        if (VTankFlags >= 0)
        {
            api.Version = Math.Max(api.Version, 74u);
            api.GetVTankStateFn = (IntPtr)(delegate* unmanaged[Cdecl]<int*, int>)&GetVTankState;
        }
        if (CloseContainerCalls)
        {
            api.Version = Math.Max(api.Version, 76u);
            api.CloseContainerFn       = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&CloseContainer;
            api.GetGroundContainerIdFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint>)&GetGroundContainerId;
        }
        if (PluginCommandCalls)
        {
            api.Version = Math.Max(api.Version, 64u);
            api.SendPluginCommandFn = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int>)&SendPluginCommand;
        }
        LastApi = api;
        return new RynthCoreHost(api);
    }

    /// <summary>
    /// A WorldObjectCache that knows the given objects: each id must have a name in
    /// <see cref="Names"/>. Ids with a <see cref="Positions"/> entry land on the landscape
    /// (a creature when <see cref="ItemTypes"/> has the creature flag 0x10), the rest in the
    /// inventory. Health ratios make creatures count as alive for the meta monster conditions.
    /// </summary>
    public static RynthCore.Plugin.RynthAi.WorldObjectCache MakeCache(RynthCoreHost host, uint playerId,
        IEnumerable<uint> ids, IReadOnlyDictionary<uint, float>? health = null)
    {
        var cache = new RynthCore.Plugin.RynthAi.WorldObjectCache(host);
        cache.SetPlayerId(playerId);
        foreach (uint id in ids)
        {
            _ = cache[unchecked((int)id)];   // the indexer's lazy lookup classifies and files it
            if (health != null && health.TryGetValue(id, out float hr))
                cache.OnUpdateHealth(id, hr);
        }
        return cache;
    }

    private static IntPtr Ansi(string s)
    {
        if (!_ansi.TryGetValue(s, out IntPtr p))
            _ansi[s] = p = Marshal.StringToHGlobalAnsi(s);
        return p;
    }

    // ── The fakes ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Microseconds each Log call costs, spun on the calling thread (0 = free). The engine's real
    /// log sink opens, appends to and closes the log file for every line, on the caller's thread:
    /// about 350 us a line measured on the PC the clients run on (2026-10-02), so the flood
    /// benchmark sets this to make plugin log volume cost what it costs in game.
    /// </summary>
    public static int LogCostMicros;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void Log(IntPtr msg)
    {
        Logs.Add(Marshal.PtrToStringAnsi(msg) ?? "");
        if (LogCostMicros > 0)
        {
            long until = System.Diagnostics.Stopwatch.GetTimestamp()
                         + LogCostMicros * System.Diagnostics.Stopwatch.Frequency / 1_000_000;
            while (System.Diagnostics.Stopwatch.GetTimestamp() < until) { }
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetUseDoneSeq() => UseDoneSeq;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetCastBusyState() => CastBusy;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetLastUseDone(int* seq, uint* error)
    {
        *seq = UseDoneSeq;
        *error = UseDoneSeq == 0 ? 0 : LastUseDoneError;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int WriteToChat(IntPtr text, int type) { Chat.Add(Marshal.PtrToStringUni(text) ?? ""); return 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SendPluginCommand(IntPtr plugin, IntPtr action, IntPtr value)
    {
        PluginCommands.Add((Marshal.PtrToStringAnsi(plugin) ?? "", Marshal.PtrToStringAnsi(action) ?? "", Marshal.PtrToStringAnsi(value) ?? ""));
        return PluginCommandDelivered ? 1 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int InvokeChatParser(IntPtr text) { ChatCommands.Add(Marshal.PtrToStringUni(text) ?? ""); return 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr GetObjectName(uint id) => Names.TryGetValue(id, out string? n) ? Ansi(n) : IntPtr.Zero;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectPosition(uint id, uint* cell, float* x, float* y, float* z)
    {
        if (!Positions.TryGetValue(id, out var p)) return 0;
        *cell = p.Cell; *x = p.X; *y = p.Y; *z = p.Z;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetItemType(uint id, uint* flags)
    {
        if (!ItemTypes.TryGetValue(id, out uint f)) return 0;
        *flags = f;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectOwnershipInfo(uint id, uint* container, uint* wielder, uint* location)
    {
        if (!Containers.TryGetValue(id, out uint c)) return 0;
        *container = c;
        *wielder = 0;
        *location = 0;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint GetObjectBitfield(uint id) => Bitfields.TryGetValue(id, out uint b) ? b : 0u;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int MoveItemInternal(uint id, uint pack, int slot, int amount) { Moves.Add((id, pack, amount)); return 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjectIsAttackable(uint id) => NotAttackable.Contains(id) ? 0 : 1;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectIntProperty(uint id, uint key, int* v)
    {
        if (!Ints.TryGetValue((id, key), out int x)) return 0;
        *v = x;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectDoubleProperty(uint id, uint key, double* v)
    {
        if (!Doubles.TryGetValue((id, key), out double x)) return 0;
        *v = x;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectQuadProperty(uint id, uint key, long* v)
    {
        if (!Quads.TryGetValue((id, key), out long x)) return 0;
        *v = x;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectBoolProperty(uint id, uint key, int* v)
    {
        if (!Bools.TryGetValue((id, key), out bool x)) return 0;
        *v = x ? 1 : 0;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr GetObjectStringProperty(uint id, uint key)
        => Strings.TryGetValue((id, key), out string? s) ? Ansi(s) : IntPtr.Zero;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectSkill(uint id, uint skill, int* buffed, int* training)
    {
        if (!Skills.TryGetValue((id, skill), out var s)) return 0;
        *buffed = s.Buffed; *training = s.Training;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectSkillLevel(uint id, uint skill, int raw, int* level)
    {
        if (!SkillLevels.TryGetValue((id, skill, raw), out int l)) return 0;
        *level = l;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectAttribute(uint id, uint attr, int raw, uint* value)
    {
        if (!Attributes.TryGetValue((id, attr, raw), out uint v)) return 0;
        *value = v;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectDataIdProperty(uint id, uint key, uint* v)
    {
        if (!DataIds.TryGetValue((id, key), out uint x)) return 0;
        *v = x;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectSpellIds(uint id, uint* buf, int max)
    {
        if (!SpellIds.TryGetValue(id, out uint[]? ids)) return -1;
        for (int i = 0; i < ids.Length && i < max; i++) buf[i] = ids[i];
        return ids.Length;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectPalettes(uint id, uint* subIds, uint* offsets, int max)
    {
        if (!Palettes.TryGetValue(id, out uint[]? p)) return -1;
        for (int i = 0; i < p.Length && i < max; i++) { subIds[i] = p[i]; offsets[i] = 0; }
        return p.Length;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int IsSpellKnown(uint id, uint spell) => KnownSpells.Contains(spell) ? 1 : 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ReadPlayerEnchantments(uint* ids, double* expiry, int max)
    {
        int n = Math.Min(Enchantments.Count, max);
        for (int i = 0; i < n; i++) { ids[i] = Enchantments[i].SpellId; expiry[i] = Enchantments[i].Expiry; }
        return Enchantments.Count;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static double GetServerTime() => ServerTime;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static float GetVitae(uint id) => Vitae;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int IsPortaling() => Portaling ? 1 : 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetVTankState(int* sequence)
    {
        if (sequence != null) *sequence = VTankSeq;
        return VTankFlags < 0 ? 0 : VTankFlags;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetPlayerPose(uint* cell, float* x, float* y, float* z, float* qw, float* qx, float* qy, float* qz)
    {
        if (!HasPose) return 0;
        *cell = PlayerPose.Cell; *x = PlayerPose.X; *y = PlayerPose.Y; *z = PlayerPose.Z;
        // Heading h (0 = north, clockwise) is a physics yaw of -h about z.
        double half = -HeadingDeg * Math.PI / 360.0;
        *qw = (float)Math.Cos(half); *qx = 0; *qy = 0; *qz = (float)Math.Sin(half);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint GetPlayerId() => PlayerIdValue;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint GetSelectedItemId() => SelectedItem;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SetAutoRun(int on) { AutoRun = on != 0; AutoRunCalls.Add(AutoRun); return 1; }

    // The fake character turns instantly.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int TurnToHeading(float deg) { HeadingDeg = deg; Turns++; return 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int JumpNonAutonomous(float extent) { Jumps++; return 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int CastSpell(uint target, int spellId) { Casts.Add((target, spellId)); return 1; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int StopCompletely() { Stops++; return 1; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GiveObjectTo(uint item, uint target, int amount) { Gives.Add((item, target, amount)); return 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetCurrentCombatMode() => CombatModeValue;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int UseObject(uint id) { Uses.Add(id); return 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int WieldItem(uint id, uint mask) { Wields.Add((id, mask)); return 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int CloseContainer(uint id) { Closes.Add(id); return 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint GetGroundContainerId() => GroundContainer;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int MoveItemExternal(uint id, uint target, int amount) { ExternalMoves.Add((id, target, amount)); return 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SalvagePanelOpen(uint tool) => 1;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SalvagePanelAddItem(uint id)
    {
        SalvageAdds.Add(id);
        if (RefuseSalvageAdd.Contains(id)) return 0;
        _panelItems.Add(id);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SalvagePanelExecute()
    {
        var items = new List<uint>(_panelItems);
        _panelItems.Clear();
        SalvageExecutes.Add(items);
        OnSalvageExecute?.Invoke(items);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectWielderInfo(uint id, uint* wielder, uint* location)
    {
        if (!Wielded.TryGetValue(id, out uint loc)) return 0;
        *wielder = PlayerIdValue;
        *location = loc;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int RequestId(uint id) { IdRequests.Add(id); return 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int HasAppraisalData(uint id) => Appraised.Contains(id) ? 1 : 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint GetObjectWcid(uint id) => Wcids.TryGetValue(id, out uint w) ? w : 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ReadKnownSpells(uint* buf, int max)
    {
        for (int i = 0; i < Spellbook.Count && i < max; i++) buf[i] = Spellbook[i];
        return Spellbook.Count;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetPlayerVitals(uint* hp, uint* maxHp, uint* stam, uint* maxStam, uint* mana, uint* maxMana)
    {
        *hp = Vitals.Hp; *maxHp = Vitals.MaxHp; *stam = Vitals.Stam; *maxStam = Vitals.MaxStam;
        *mana = Vitals.Mana; *maxMana = Vitals.MaxMana;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetPlayerBaseVitals(uint* hp, uint* stam, uint* mana)
    {
        *hp = BaseVitals.Hp; *stam = BaseVitals.Stam; *mana = BaseVitals.Mana;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr GetWorldName() => WorldName.Length > 0 ? Ansi(WorldName) : IntPtr.Zero;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr GetAccountName() => AccountName.Length > 0 ? Ansi(AccountName) : IntPtr.Zero;
}
