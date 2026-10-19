// ServerInterface.cs - RynthILT's side of the "RynthILT.Server" interface (Plugins/Shared/RynthIltServerApi.cs):
// the profile JSON other plugins read (RynthOracle's Fellowship / Quests qb list / feature list):
// the Leaftide feature list with the server's state for each, and the server's fellowship rules.
// Version 2 (RynthILT 0.2.22) moves the full /qb lists out of the profile into GetQuestBonusJson,
// because a 15,000-25,000 flag list overran the 512 KB profile readers and blanked the whole profile.
//
// Restored 2026-10-16: this export was dropped when "RynthServer.Profile" (salvage / identity) was
// added, so Oracle still detected ILT via RynthCore but never received feature / qb / fellowship
// data from RynthILT. Hub + Data stay separate; this table is the feature-profile channel.
//
// The table is allocated once per loaded copy of RynthILT and never freed (a plugin DLL is never
// unloaded); after Release() it reports Version 0 and GetProfileJson answers 0. Before login there
// is no controller, so the profile is the not-ILT default (every feature unknown, retail fellowships).
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using RynthCore.Plugin.RynthILT.Hub;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthILT;

internal static unsafe class ServerInterface
{
    private static RynthIltServerApiV1* _table;
    private static volatile bool _released;

    /// <summary>The profile before any login: not an ILT world, nothing known (built once).</summary>
    private static readonly string NotLoggedInJson =
        IltFeatureCatalog.BuildProfileJson(false, string.Empty, false, string.Empty, DateTime.MinValue, _ => IltTri.Unknown);

    /// <summary>The table for (<paramref name="iface"/>, <paramref name="version"/>), or 0 when it isn't ours.</summary>
    public static IntPtr Query(string iface, uint version)
    {
        if (iface != RynthIltServerApiV1.InterfaceName || version < 1 || version > RynthIltServerApiV1.InterfaceVersion || _released)
            return IntPtr.Zero;
        if (_table == null)
        {
            var t = (RynthIltServerApiV1*)NativeMemory.AllocZeroed((nuint)sizeof(RynthIltServerApiV1));
            t->StructSize = (uint)sizeof(RynthIltServerApiV1);
            t->GetProfileJson = &GetProfileJson;
            t->GetQuestBonusJson = &GetQuestBonusJson;
            t->Version = RynthIltServerApiV1.InterfaceVersion;
            _table = t;
        }
        return (IntPtr)_table;
    }

    /// <summary>Plugin shutdown: readers see Version 0 and re-query; GetProfileJson answers 0.</summary>
    public static void Release()
    {
        _released = true;
        if (_table != null) _table->Version = 0;
    }

    /// <summary>The JSON to hand out now: the live controller's snapshot, or the not-logged-in default.</summary>
    internal static string CurrentJson()
        => (_released ? null : PluginExports.Runtime.Plugin?.Hub?.ProfileJson) ?? NotLoggedInJson;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetProfileJson(byte* buffer, int capacity)
    {
        try
        {
            if (_released) return 0;
            return Write(CurrentJson(), buffer, capacity);
        }
        catch { return 0; }
    }

    /// <summary>The "/qb list" JSON now: the live controller's, or an empty list before login.</summary>
    internal static string CurrentQuestBonusJson()
        => (_released ? null : PluginExports.Runtime.Plugin?.Hub?.QuestBonus.ListJson) ?? IltQuestBonus.EmptyListJson;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetQuestBonusJson(byte* buffer, int capacity)
    {
        try
        {
            if (_released) return 0;
            return Write(CurrentQuestBonusJson(), buffer, capacity);
        }
        catch { return 0; }
    }

    /// <summary>
    /// Copies <paramref name="json"/> as NUL-terminated UTF-8 into the caller's buffer when it fits,
    /// and returns its byte length either way (length >= capacity: too small, nothing written).
    /// </summary>
    internal static int Write(string json, byte* buffer, int capacity)
    {
        int length = Encoding.UTF8.GetByteCount(json);
        if (buffer == null || capacity <= length) return length;
        fixed (char* src = json)
            Encoding.UTF8.GetBytes(src, json.Length, buffer, capacity);
        buffer[length] = 0;
        return length;
    }
}
