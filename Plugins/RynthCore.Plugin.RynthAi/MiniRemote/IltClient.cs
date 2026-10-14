// IltClient.cs - RynthAi's side of the "RynthILT.Hub" interface (Plugins/Shared/RynthIltHubApi.cs):
// the Infinite Leaftide tabs and sections the Mini Remote shows, the server's ILT feature bits
// (the Pets tab's refill / mastery charms, the Quests tab's /myquests bit), the ILT session rates
// and the ILT "/ra" verbs, all from the RynthILT plugin.
//
// Without RynthILT (not installed, too old, not loaded yet) every call answers "not ILT": no ILT
// tab, no ILT section, unknown feature bits, and the ILT verbs say RynthILT is needed. The table is
// looked up on the pump thread (Tick) at most every RecheckMs and kept in a volatile field for the
// render thread; RynthILT never frees it (Version goes to 0 on its shutdown), so reading a stale
// pointer is safe and a Version of 0 means "look again".
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.MiniRemote;

/// <summary>One ILT tab as RynthILT describes it (render thread copy).</summary>
internal readonly record struct IltTabInfo(string Id, string Label, float Width, float Height, bool Shown, bool Available);

internal sealed unsafe class IltClient
{
    public const string NotLoaded = "That's an Infinite Leaftide feature: it comes with the RynthILT plugin, which isn't loaded.";
    private const long RecheckMs = 3000;
    private const int MaxTabs = 16;

    private readonly RynthCoreHost _host;
    private IntPtr _table;          // volatile reads/writes: the render thread uses what the pump found
    private long _checkedAt;
    private bool _checkedOnce;      // false: look now (a sentinel time would overflow "now - _checkedAt")
    private readonly IltTabV1[] _tabBuf = new IltTabV1[MaxTabs];
    private readonly List<IltTabInfo> _tabs = new();

    public IltClient(RynthCoreHost host) => _host = host;

    /// <summary>Pump thread: look the table up again when it's due, missing or shut down.</summary>
    public void Tick()
    {
        long now = Environment.TickCount64;
        var t = (RynthIltHubApiV1*)System.Threading.Volatile.Read(ref _table);
        if (!LookupDue(_checkedOnce, _checkedAt, now, haveTable: t != null, tableLive: t != null && t->Version >= 1)) return;
        _checkedOnce = true;
        _checkedAt = now;
        IntPtr found = IntPtr.Zero;
        if (_host.HasGetPluginInterface)
        {
            var p = (RynthIltHubApiV1*)_host.GetPluginInterface(RynthIltHub.PluginName, RynthIltHubApiV1.InterfaceName, RynthIltHubApiV1.InterfaceVersion);
            if (p != null && p->Version >= 1 && p->StructSize >= (uint)sizeof(RynthIltHubApiV1)) found = (IntPtr)p;
        }
        System.Threading.Volatile.Write(ref _table, found);
    }

    /// <summary>Forget the table (login, logout); the next Tick looks again.</summary>
    public void Reset()
    {
        System.Threading.Volatile.Write(ref _table, IntPtr.Zero);
        _checkedOnce = false;
    }

    /// <summary>
    /// Whether Tick looks the table up again. Pure. Always on the first call (and after Reset), at
    /// once when the table RynthILT gave is shut down (Version 0), else every <see cref="RecheckMs"/>.
    /// </summary>
    internal static bool LookupDue(bool checkedOnce, long checkedAt, long now, bool haveTable, bool tableLive)
    {
        if (!checkedOnce) return true;
        if (haveTable && !tableLive) return true;
        return now - checkedAt >= RecheckMs;
    }

    private RynthIltHubApiV1* T
    {
        get
        {
            var t = (RynthIltHubApiV1*)System.Threading.Volatile.Read(ref _table);
            return t != null && t->Version >= 1 ? t : null;
        }
    }

    /// <summary>RynthILT answers (any thread).</summary>
    public bool Loaded => T != null;

    /// <summary>This world is Infinite Leaftide / ACECustom (or forced), as RynthILT sees it. Any thread.</summary>
    public bool IsIltWorld
    {
        get
        {
            var t = T;
            return t != null && t->IsIltWorld != null && t->IsIltWorld() != 0;
        }
    }

    /// <summary>The server's bit for a feature: -1 unknown (and always off ILT / without RynthILT), 0 off, 1 on. Any thread.</summary>
    public int Feature(string key)
    {
        var t = T;
        if (t == null || t->GetFeature == null) return -1;
        using var k = new Utf8(key);
        return t->GetFeature(k.P);
    }

    /// <summary>The server positively reported the feature off.</summary>
    public bool IsOff(string key) => Feature(key) == 0;

    /// <summary>The server positively reported the feature on.</summary>
    public bool IsOn(string key) => Feature(key) == 1;

    /// <summary>ILT coin / pyreal rates (false off ILT or without RynthILT). Any thread.</summary>
    public bool TryGetRates(out double coinsPerHour, out double pyrealsPerHour)
    {
        coinsPerHour = pyrealsPerHour = 0;
        var t = T;
        if (t == null || t->GetRates == null) return false;
        double c = 0, p = 0;
        if (t->GetRates(&c, &p) == 0) return false;
        coinsPerHour = c;
        pyrealsPerHour = p;
        return true;
    }

    /// <summary>This character's "Treat this world as ILT" (RynthILT's). Any thread.</summary>
    public bool ForceIlt
    {
        get
        {
            var t = T;
            return t != null && t->GetForceIlt != null && t->GetForceIlt() != 0;
        }
        set
        {
            var t = T;
            if (t != null && t->SetForceIlt != null) t->SetForceIlt(value ? 1 : 0);
        }
    }

    /// <summary>Render thread: RynthILT's tabs with their flags (empty without RynthILT). The list is reused.</summary>
    public IReadOnlyList<IltTabInfo> Tabs()
    {
        _tabs.Clear();
        var t = T;
        if (t == null || t->GetTabs == null) return _tabs;
        int n;
        fixed (IltTabV1* buf = _tabBuf) n = t->GetTabs(buf, MaxTabs);
        n = Math.Min(n, MaxTabs);
        for (int i = 0; i < n; i++)
        {
            ref IltTabV1 tab = ref _tabBuf[i];
            string id, label;
            fixed (byte* pid = tab.Id) id = Marshal.PtrToStringUTF8((IntPtr)pid) ?? string.Empty;
            fixed (byte* plabel = tab.Label) label = Marshal.PtrToStringUTF8((IntPtr)plabel) ?? string.Empty;
            if (id.Length == 0) continue;
            _tabs.Add(new IltTabInfo(id, label.Length > 0 ? label : id, tab.Width, tab.Height,
                (tab.Flags & RynthIltHub.TabShown) != 0, (tab.Flags & RynthIltHub.TabAvailable) != 0));
        }
        return _tabs;
    }

    /// <summary>Render thread: one ILT tab's body into the current child window; false = nothing drawn.</summary>
    public bool RenderTab(string id)
    {
        var t = T;
        if (t == null || t->RenderTab == null) return false;
        using var s = new Utf8(id);
        return t->RenderTab(s.P) != 0;
    }

    /// <summary>Render thread: an ILT section (RynthIltHub.Section*); false = nothing drawn (not ILT, or no RynthILT).</summary>
    public bool RenderSection(string id)
    {
        var t = T;
        if (t == null || t->RenderSection == null) return false;
        using var s = new Utf8(id);
        return t->RenderSection(s.P) != 0;
    }

    /// <summary>An ILT "/ra" verb ("hub bank", "guardian handin", ...); false when RynthILT doesn't take it.</summary>
    public bool Command(string text)
    {
        var t = T;
        if (t == null || t->Command == null) return false;
        using var s = new Utf8(text);
        return t->Command(s.P) != 0;
    }

    /// <summary>A NUL-terminated UTF-8 copy of a string for one call.</summary>
    private readonly struct Utf8 : IDisposable
    {
        public readonly byte* P;

        public Utf8(string? s)
        {
            s ??= string.Empty;
            int n = Encoding.UTF8.GetByteCount(s);
            P = (byte*)NativeMemory.Alloc((nuint)(n + 1));
            fixed (char* c = s) Encoding.UTF8.GetBytes(c, s.Length, P, n);
            P[n] = 0;
        }

        public void Dispose() => NativeMemory.Free(P);
    }
}
