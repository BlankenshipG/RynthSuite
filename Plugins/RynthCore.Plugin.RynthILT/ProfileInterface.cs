// ProfileInterface.cs - RynthILT's side of "RynthServer.Profile" (Plugins/Shared/RynthServerProfileApi.cs):
// Infinite Leaftide's settings as JSON for other plugins, the same interface and JSON writer the
// server plugins use (Plugins/Shared/ServerPack/ServerPackProfile.cs). RynthCore's GetServerStateJson
// names RynthILT as the owner of "ilt"; a reader (RynthAi's Crafting tab, ...) then asks it for this.
// The profile carries ILT's tinkering salvage (Hub/IltSalvage.cs).
//
// As ServerPackInterface: the table is allocated once per loaded copy of RynthILT and never freed;
// after Release() it reports Version 0 and GetProfileJson answers 0. Before the Hub is up the profile
// still describes the server (active false, no world).
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using RynthCore.Plugin.RynthILT.Hub;
using RynthCore.Plugin.ServerPack;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthILT;

internal static unsafe class ProfileInterface
{
    private static RynthServerProfileApiV1* _table;
    private static volatile bool _released;

    /// <summary>What RynthILT knows about its server (identity from the csproj's manifest properties).</summary>
    internal static readonly ServerPackInfo Info =
        new(RynthPluginInfo.ServerId, RynthPluginInfo.ServerName, RynthPluginInfo.ServerHosts, RynthPluginInfo.ServerWorldNames)
        {
            Emulator = "ACE",
            Salvage = IltSalvage.Rows,
            Source = "RynthILT; salvage from UB-IT's LeaftideImbueHelper (via SilentKelpie)",
        };

    /// <summary>The last JSON handed out, and what it was built for (rebuilt only when that changes).</summary>
    private sealed record Snapshot(bool Active, string World, string Json);
    private static Snapshot? _last;

    /// <summary>The table for (<paramref name="iface"/>, <paramref name="version"/>), or 0 when it isn't ours.</summary>
    public static IntPtr Query(string iface, uint version)
    {
        if (iface != RynthServerProfileApiV1.InterfaceName || version < 1 || version > RynthServerProfileApiV1.InterfaceVersion || _released)
            return IntPtr.Zero;
        if (_table == null)
        {
            var t = (RynthServerProfileApiV1*)NativeMemory.AllocZeroed((nuint)sizeof(RynthServerProfileApiV1));
            t->StructSize = (uint)sizeof(RynthServerProfileApiV1);
            t->GetProfileJson = &GetProfileJson;
            t->Version = RynthServerProfileApiV1.InterfaceVersion;
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

    /// <summary>
    /// The JSON to hand out for RynthILT's verdict (<paramref name="active"/>) and the world name.
    /// Any thread.
    /// </summary>
    internal static string CurrentJson(bool active, string? world)
    {
        world ??= string.Empty;
        Snapshot? last = Volatile.Read(ref _last);
        if (last != null && last.Active == active && last.World == world) return last.Json;
        string json = ServerPackProfile.BuildJson(Info, RynthPluginInfo.Name, RynthPluginInfo.Version, active, world);
        Volatile.Write(ref _last, new Snapshot(active, world, json));
        return json;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetProfileJson(byte* buffer, int capacity)
    {
        try
        {
            if (_released) return 0;
            IltHubController? hub = PluginExports.Runtime.Plugin?.Hub;
            string json = CurrentJson(hub?.Options.IsIltLikeWorld == true, hub?.Options.WorldName);
            return Write(json, buffer, capacity);
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
