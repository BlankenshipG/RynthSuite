using System;
using System.Runtime.InteropServices;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// RynthAi's "RynthAi.Script" interface (Shared/RynthAiScriptApi.cs), resolved through the
/// host (API v68) and wrapped as plain C# calls. Everything here runs on the plugin pump
/// thread. The table is looked up lazily, and RynthLuaPlugin.OnTick resets it every tick:
/// RynthAi may load after RynthLua, be reloaded (its code unloaded), or not be installed at all (then every call reports
/// "RynthAi isn't loaded" and scripts that only use chat, timers and the game keep working).
/// </summary>
internal sealed unsafe class RynthAiBridge
{
    public const string NotLoaded = "RynthAi isn't loaded (needs RynthAi and RynthCore 2026.9.29 or newer)";

    private readonly RynthCoreHost _host;
    private RynthAiScriptApiV1* _t;
    private bool _checked;
    private long _checkedAt;
    private const long RecheckMs = 3000;

    public RynthAiBridge(RynthCoreHost host) => _host = host;

    /// <summary>True when RynthAi's interface is there right now.</summary>
    public bool Available => Table != null;

    private RynthAiScriptApiV1* Table
    {
        get
        {
            long now = Environment.TickCount64;
            if (_checked && now - _checkedAt < RecheckMs) return _t;
            _checked = true;
            _checkedAt = now;
            IntPtr p = _host.GetPluginInterface("RynthAi", RynthAiScriptApiV1.InterfaceName, RynthAiScriptApiV1.InterfaceVersion);
            var t = (RynthAiScriptApiV1*)p;
            _t = t != null && t->Version >= 1 && t->StructSize >= (uint)sizeof(RynthAiScriptApiV1) ? t : null;
            return _t;
        }
    }

    /// <summary>Forget the table (logout, RynthAi reload); the next call looks it up again.</summary>
    public void Reset()
    {
        _t = null;
        _checked = false;
    }

    private static string Copy(byte* s) => s == null ? string.Empty : Marshal.PtrToStringUTF8((IntPtr)s) ?? string.Empty;

    /// <summary>A UTF-8 copy of <paramref name="s"/> for the duration of one call.</summary>
    private readonly ref struct Utf8
    {
        public readonly IntPtr Ptr;
        public Utf8(string s) => Ptr = Marshal.StringToCoTaskMemUTF8(s ?? string.Empty);
        public byte* P => (byte*)Ptr;
        public void Dispose() => Marshal.FreeCoTaskMem(Ptr);
    }

    public bool Evaluate(string expression, out string result)
    {
        var t = Table;
        if (t == null) { result = NotLoaded; return false; }
        using var e = new Utf8(expression);
        byte* r = null;
        int ok = t->Evaluate(e.P, &r);
        result = Copy(r);
        return ok != 0;
    }

    public string? GetSetting(string name)
    {
        var t = Table;
        if (t == null) return null;
        using var n = new Utf8(name);
        byte* r = null;
        return t->GetSetting(n.P, &r) != 0 ? Copy(r) : null;
    }

    public bool SetSetting(string name, string value)
    {
        var t = Table;
        if (t == null) return false;
        using var n = new Utf8(name);
        using var v = new Utf8(value);
        return t->SetSetting(n.P, v.P) != 0;
    }

    public string[] SettingNames()
    {
        var t = Table;
        if (t == null) return Array.Empty<string>();
        byte* r = null;
        return t->GetSettingNames(&r) != 0 ? Copy(r).Split('\n', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
    }

    public bool MacroRunning
    {
        get { var t = Table; return t != null && t->IsMacroRunning() != 0; }
        set { var t = Table; if (t != null) t->SetMacroRunning(value ? 1 : 0); }
    }

    public bool GoTo(double ns, double ew)
    {
        var t = Table;
        return t != null && t->NavGoTo(ns, ew) != 0;
    }

    public void Stop()
    {
        var t = Table;
        if (t != null) t->NavStop();
    }

    public bool FindNearest(string name, bool portalOrNpcOnly, out uint id, out double distance)
    {
        id = 0; distance = -1;
        var t = Table;
        if (t == null) return false;
        using var n = new Utf8(name);
        uint found = 0; double d = -1;
        if (t->FindNearest(n.P, portalOrNpcOnly ? 1 : 0, &found, &d) == 0) return false;
        id = found; distance = d;
        return true;
    }

    public double DistanceTo(uint id)
    {
        var t = Table;
        return t == null ? -1 : t->DistanceTo(id);
    }

    public bool IsPathClear(uint id)
    {
        var t = Table;
        return t == null || t->IsPathClear(id) != 0;
    }

    /// <summary>A chat line as if typed. Without RynthAi, falls back to AC's own parser.</summary>
    public void SubmitCommand(string text)
    {
        var t = Table;
        if (t != null)
        {
            using var c = new Utf8(text);
            if (t->SubmitCommand(c.P) != 0) return;
        }
        if (_host.HasInvokeChatParser) _host.InvokeChatParser(text);
    }
}
