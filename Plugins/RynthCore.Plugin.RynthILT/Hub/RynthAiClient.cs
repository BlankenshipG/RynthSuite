// RynthAiClient.cs - RynthILT's side of the "RynthAi.Hub" interface (Plugins/Shared/RynthAiHubApi.cs):
// the Mini Remote that hosts the ILT tabs, the /myquests tracker, spell names, VTank loot-profile
// matching, the .usd importer and the Mini Remote's session stats.
//
// The table is looked up through the host (API v68) and kept for RecheckMs; Reset() (login, logout,
// shutdown) and a table whose Version went to 0 (RynthAi shut down) force a fresh lookup, so a
// RynthAi that loads after RynthILT, is reloaded or isn't installed is handled: every call then
// reports "no RynthAi" and the ILT features that need it say so. The pointer is kept in a volatile
// field so the render thread (RenderSection) can use what the pump looked up; the table is never
// freed by RynthAi (its Version is zeroed instead), so a stale pointer is safe to read.
using System;
using System.Runtime.InteropServices;
using System.Text;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthILT.Hub;

/// <summary>One /myquests flag as RynthAi has it (QuestRecord's fields the Temple tracker reads).</summary>
internal sealed class QuestRecord
{
    public string Key = string.Empty;
    public int Solves;
    public int MaxSolves;
    public DateTime CompletedOn = DateTime.MinValue;
    public TimeSpan RepeatTime = TimeSpan.Zero;

    /// <summary>RynthAi's QuestRecord.IsReady (UB's): the timer ran out and it isn't a once-only flag.</summary>
    public bool IsReady()
    {
        if ((CompletedOn + RepeatTime) > DateTime.UtcNow) return false;
        return !(MaxSolves == 1 && Solves <= 1);
    }

    /// <summary>Time until the flag can be solved again (zero when ready).</summary>
    public TimeSpan TimeUntilReady()
    {
        var left = (CompletedOn + RepeatTime) - DateTime.UtcNow;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }
}

internal sealed unsafe class RynthAiClient
{
    public const string NotLoaded = "needs RynthAi (not loaded)";
    private const long RecheckMs = 3000;

    private readonly RynthCoreHost _host;
    private RynthAiHubApiV1* _t;          // pump-thread copy
    private IntPtr _renderTable;          // what the render thread uses (volatile)
    private bool _checked;
    private long _checkedAt;

    public RynthAiClient(RynthCoreHost host) => _host = host;

    /// <summary>Forget the table; the next call looks it up again (login, logout, RynthAi reload).</summary>
    public void Reset()
    {
        _t = null;
        _checked = false;
        System.Threading.Volatile.Write(ref _renderTable, IntPtr.Zero);
    }

    /// <summary>The table, looked up at most every RecheckMs (pump thread).</summary>
    private RynthAiHubApiV1* Table
    {
        get
        {
            long now = Environment.TickCount64;
            if (_checked && now - _checkedAt < RecheckMs && (_t == null || _t->Version >= 1)) return _t;
            _checked = true;
            _checkedAt = now;
            _t = null;
            if (_host.HasGetPluginInterface)
            {
                var t = (RynthAiHubApiV1*)_host.GetPluginInterface(RynthAiHub.PluginName, RynthAiHubApiV1.InterfaceName, RynthAiHubApiV1.InterfaceVersion);
                if (t != null && t->Version >= 1 && t->StructSize >= RynthAiHubApiV1.BaseStructSize) _t = t;
            }
            System.Threading.Volatile.Write(ref _renderTable, (IntPtr)_t);
            return _t;
        }
    }

    /// <summary>Pump thread: refreshes the lookup (the render thread only reads what this found).</summary>
    public void Tick() => _ = Table;

    /// <summary>True when RynthAi's interface is there (pump thread).</summary>
    public bool Available => Table != null;

    /// <summary>Any thread: the table the pump last found, if RynthAi hasn't shut down since.</summary>
    private RynthAiHubApiV1* AnyThreadTable
    {
        get
        {
            var t = (RynthAiHubApiV1*)System.Threading.Volatile.Read(ref _renderTable);
            return t != null && t->Version >= 1 ? t : null;
        }
    }

    // ── Mini Remote ─────────────────────────────────────────────────────────

    /// <summary>Puts the Mini Remote on a tab (any thread). The reply, or why it couldn't.</summary>
    public string MiniRemoteTab(string tabId, string mode)
    {
        var t = AnyThreadTable;
        if (t == null || t->MiniRemoteTab == null) return "The Mini Remote is RynthAi's; RynthAi isn't loaded.";
        byte* reply = stackalloc byte[256];
        reply[0] = 0;
        using var id = new Utf8(tabId);
        using var m = new Utf8(mode);
        int ok = t->MiniRemoteTab(id.P, m.P, reply, 256);
        string text = Marshal.PtrToStringUTF8((IntPtr)reply) ?? string.Empty;
        return ok != 0 ? text : (text.Length > 0 ? text : "The Mini Remote is not available yet.");
    }

    /// <summary>True while the Mini Remote is on screen (any thread).</summary>
    public bool MiniRemoteVisible
    {
        get
        {
            var t = AnyThreadTable;
            return t != null && t->MiniRemoteVisible != null && t->MiniRemoteVisible() != 0;
        }
    }

    // ── Quests (/myquests) ──────────────────────────────────────────────────

    /// <summary>A /myquests flag from RynthAi's tracker (pump thread).</summary>
    public bool TryGetQuestFlag(string key, out QuestRecord rec)
    {
        rec = null!;
        var t = Table;
        if (t == null || t->QuestFlag == null || string.IsNullOrWhiteSpace(key)) return false;
        QuestFlagV1 f = default;
        using var k = new Utf8(key);
        if (t->QuestFlag(k.P, &f) == 0) return false;
        rec = new QuestRecord
        {
            Key = key.ToLowerInvariant(),
            Solves = f.Solves,
            MaxSolves = f.MaxSolves,
            CompletedOn = new DateTime(Math.Max(0, f.CompletedOnUtcTicks), DateTimeKind.Utc),
            RepeatTime = TimeSpan.FromSeconds(Math.Max(0, f.RepeatSeconds)),
        };
        return true;
    }

    /// <summary>The tracker's state (pump thread); false without RynthAi or before login.</summary>
    public bool TryGetQuestStatus(out QuestStatusV1 status)
    {
        status = default;
        var t = Table;
        if (t == null || t->QuestStatus == null) return false;
        QuestStatusV1 s = default;
        if (t->QuestStatus(&s) == 0) return false;
        status = s;
        return true;
    }

    /// <summary>A quiet /myquests refresh through RynthAi's Quests tab (its cooldown applies). Pump thread.</summary>
    public string RefreshQuests()
    {
        var t = Table;
        if (t == null || t->QuestRefresh == null) return "The quest tracker " + NotLoaded + ".";
        byte* reply = stackalloc byte[256];
        reply[0] = 0;
        t->QuestRefresh(reply, 256);
        return Marshal.PtrToStringUTF8((IntPtr)reply) ?? string.Empty;
    }

    // ── Spells, loot profiles ───────────────────────────────────────────────

    /// <summary>A spell's name from RynthAi's spell table ("" when unknown or no RynthAi). Pump thread.</summary>
    public string SpellName(int spellId)
    {
        var t = Table;
        if (t == null || t->SpellName == null || spellId <= 0) return string.Empty;
        byte* buf = stackalloc byte[160];
        int n = t->SpellName(unchecked((uint)spellId), buf, 160);
        return n <= 0 ? string.Empty : Marshal.PtrToStringUTF8((IntPtr)buf) ?? string.Empty;
    }

    /// <summary>
    /// Evaluates a VTank .utl profile on <paramref name="ids"/>: keep[i] is true when that item's
    /// first matching rule is Keep. False (with the reason) when it couldn't. Pump thread.
    /// </summary>
    public bool LootKeeps(string utlPath, uint[] ids, out bool[] keep, out string error)
    {
        keep = new bool[ids.Length];
        error = string.Empty;
        var t = Table;
        if (t == null || t->LootKeeps == null) { error = "equipment suits " + NotLoaded; return false; }
        byte[] flags = new byte[ids.Length];
        byte* err = stackalloc byte[256];
        err[0] = 0;
        int ok;
        using (var p = new Utf8(utlPath))
        fixed (uint* pids = ids)
        fixed (byte* pf = flags)
            ok = t->LootKeeps(p.P, pids, ids.Length, pf, err, 256);
        if (ok == 0) { error = Marshal.PtrToStringUTF8((IntPtr)err) ?? "could not read profile"; return false; }
        for (int i = 0; i < ids.Length; i++) keep[i] = flags[i] != 0;
        return true;
    }

    // ── Session stats ───────────────────────────────────────────────────────

    /// <summary>The Mini Remote's session (any thread); false without RynthAi or before login.</summary>
    public bool TryGetSessionStats(out SessionStatsV1 stats)
    {
        stats = default;
        var t = AnyThreadTable;
        if (t == null || t->SessionStats == null) return false;
        SessionStatsV1 s = default;
        if (t->SessionStats(&s) == 0) return false;
        stats = s;
        return true;
    }

    /// <summary>Starts the Mini Remote's session over (any thread).</summary>
    public void ResetSessionStats()
    {
        var t = AnyThreadTable;
        if (t != null && t->SessionReset != null) t->SessionReset();
    }

    // ── RynthAi's sections drawn in RynthILT's tabs ─────────────────────────

    /// <summary>Render thread: draws one of RynthAi's sections; false (nothing drawn) without RynthAi.</summary>
    public bool RenderSection(string id)
    {
        var t = AnyThreadTable;
        if (t == null || t->RenderSection == null) return false;
        using var s = new Utf8(id);
        return t->RenderSection(s.P) != 0;
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
