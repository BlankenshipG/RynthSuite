// HubInterface.cs - RynthILT's side of the "RynthILT.Hub" interface (Plugins/Shared/RynthIltHubApi.cs):
// the table RynthPluginQueryInterface hands RynthAi, and the functions behind it.
//
// The table is allocated once per loaded copy of RynthILT and never freed (a plugin DLL is never
// unloaded, so its functions stay callable): RynthAi's render thread may still hold the pointer
// while plugins shut down, and every function here returns "nothing" once Release() has run
// (Version 0, no plugin). Threads as the interface says: the render-thread functions draw into
// the caller's current ImGui window; the pump-thread Command runs the ILT verb at once.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using RynthCore.Plugin.RynthILT.Hub;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthILT;

internal static unsafe class HubInterface
{
    private static RynthIltHubApiV1* _table;
    private static volatile bool _released;

    /// <summary>The live controller, or null (before login, after logout, after shutdown).</summary>
    private static IltHubController? Hub => _released ? null : PluginExports.Runtime.Plugin?.Hub;

    /// <summary>The table for (<paramref name="iface"/>, <paramref name="version"/>), or 0 when it isn't ours.</summary>
    public static IntPtr Query(string iface, uint version)
    {
        if (iface != RynthIltHubApiV1.InterfaceName || version < 1 || version > RynthIltHubApiV1.InterfaceVersion || _released)
            return IntPtr.Zero;
        if (_table == null)
        {
            var t = (RynthIltHubApiV1*)NativeMemory.AllocZeroed((nuint)sizeof(RynthIltHubApiV1));
            t->StructSize = (uint)sizeof(RynthIltHubApiV1);
            t->IsIltWorld = &IsIltWorld;
            t->GetFeature = &GetFeature;
            t->GetRates = &GetRates;
            t->GetForceIlt = &GetForceIlt;
            t->SetForceIlt = &SetForceIlt;
            t->GetTabs = &GetTabs;
            t->RenderTab = &RenderTab;
            t->RenderSection = &RenderSection;
            t->Command = &Command;
            t->Version = RynthIltHubApiV1.InterfaceVersion;
            _table = t;
        }
        return (IntPtr)_table;
    }

    /// <summary>Plugin shutdown: callers see Version 0 and re-query; the functions answer "nothing".</summary>
    public static void Release()
    {
        _released = true;
        if (_table != null) _table->Version = 0;
    }

    private static string? Utf8(byte* p) => p == null ? null : Marshal.PtrToStringUTF8((IntPtr)p);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int IsIltWorld()
    {
        try { return Hub?.Options.IsIltLikeWorld == true ? 1 : 0; }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetFeature(byte* key)
    {
        try
        {
            var hub = Hub;
            string? k = Utf8(key);
            if (hub == null || string.IsNullOrEmpty(k) || !hub.Options.IsIltLikeWorld) return -1;
            return (int)hub.Options.Get(k);
        }
        catch { return -1; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetRates(double* coinsPerHour, double* pyrealsPerHour)
    {
        try
        {
            var hub = Hub;
            if (hub == null || !hub.Options.IsIltLikeWorld) return 0;
            var r = hub.Rates.Summary;
            if (coinsPerHour != null) *coinsPerHour = r.CoinsPerHour;
            if (pyrealsPerHour != null) *pyrealsPerHour = r.PyrealsPerHour;
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetForceIlt()
    {
        try { return Hub?.ForceIltWorld == true ? 1 : 0; }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void SetForceIlt(int on)
    {
        try
        {
            var hub = Hub;
            if (hub != null) hub.ForceIltWorld = on != 0;
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetTabs(IltTabV1* tabs, int capacity)
    {
        try
        {
            var hub = Hub;
            if (hub == null) return 0;
            var all = IltSections.Tabs;
            if (tabs != null)
            {
                for (int i = 0; i < all.Length && i < capacity; i++)
                {
                    IltSection s = all[i];
                    IltTabV1* t = &tabs[i];
                    WriteUtf8(IltSections.TabId(s), t->Id, 24);
                    WriteUtf8(IltSections.Label(s), t->Label, 32);
                    var size = IltSections.TabSize(s);
                    t->Width = size.X;
                    t->Height = size.Y;
                    uint flags = 0;
                    if (hub.TabShown(s)) flags |= RynthIltHub.TabShown;
                    if (hub.SectionAvailable(s)) flags |= RynthIltHub.TabAvailable;
                    t->Flags = flags;
                }
            }
            return all.Length;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int RenderTab(byte* id)
    {
        try
        {
            var hub = Hub;
            if (hub == null || !IltSections.TryParse(Utf8(id) ?? string.Empty, out IltSection s) || !IltSections.IsTab(s)) return 0;
            hub.RenderTab(s);
            return 1;
        }
        catch (Exception ex)
        {
            RynthLog.Exception(LogCat.IltHub, ex, "RenderTab");
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int RenderSection(byte* id)
    {
        try
        {
            var hub = Hub;
            string? s = Utf8(id);
            return hub != null && s != null && hub.RenderSection(s) ? 1 : 0;
        }
        catch (Exception ex)
        {
            RynthLog.Exception(LogCat.IltHub, ex, "RenderSection");
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Command(byte* text)
    {
        try
        {
            var plugin = _released ? null : PluginExports.Runtime.Plugin;
            string? line = Utf8(text);
            if (plugin == null || string.IsNullOrWhiteSpace(line) || line.Length > 512) return 0;
            return plugin.RunForwardedCommand(line) ? 1 : 0;
        }
        catch { return 0; }
    }

    /// <summary>Writes <paramref name="s"/> as NUL-terminated UTF-8 into a fixed buffer, truncated to fit.</summary>
    private static void WriteUtf8(string s, byte* dest, int capacity)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        int n = Math.Min(bytes.Length, capacity - 1);
        for (int i = 0; i < n; i++) dest[i] = bytes[i];
        dest[n] = 0;
    }
}
