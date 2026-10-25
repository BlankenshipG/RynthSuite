// ServerPackInterface.cs - A server plugin's side of "RynthServer.Profile"
// (Plugins/Shared/RynthServerProfileApi.cs): the server's settings as JSON (ServerPackProfile) for
// other plugins, answered through RynthPluginQueryInterface (ServerPackExports).
//
// The table is allocated once per loaded copy of the plugin and never freed (a plugin DLL is never
// unloaded); after Release() it reports Version 0 and GetProfileJson answers 0. Before the plugin is
// up, or between characters, the profile still describes the server (active false, no world).
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.ServerPack;

internal static unsafe class ServerPackInterface
{
    private static RynthServerProfileApiV1* _table;
    private static volatile bool _released;

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

    /// <summary>The JSON to hand out now. Any thread: the plugin's state is read through volatile fields.</summary>
    internal static string CurrentJson(ServerPackPlugin? plugin)
    {
        ServerPackInfo info = plugin?.Info ?? ServerPackPlugin.StaticInfo;
        bool active = plugin?.Active ?? false;
        string world = plugin?.World ?? string.Empty;
        Snapshot? last = Volatile.Read(ref _last);
        if (last != null && last.Active == active && last.World == world) return last.Json;
        string json = ServerPackProfile.BuildJson(info, RynthPluginInfo.Name, RynthPluginInfo.Version, active, world);
        Volatile.Write(ref _last, new Snapshot(active, world, json));
        return json;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetProfileJson(byte* buffer, int capacity)
    {
        try
        {
            if (_released) return 0;
            return Write(CurrentJson(ServerPackExports.Plugin), buffer, capacity);
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
