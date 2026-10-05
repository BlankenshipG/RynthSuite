// IltChunkClap.cs — Lugian-hammer chunking and /clap automation (Gear tab).
//
// Chunk: uses a Lugian (Aetheria) Hammer on eligible items, one at a time:
//   Aetheria  — "Aetheria"/"Coalesced Aetheria" gems, not equipped, appraised, not flagged
//               never-chunk (bool 9030), never levelled (ItemTotalXp quad 4 == 0), and
//               max level (int 319) under the configured cap (Coalesced always qualifies).
//   Corrupted — "Corrupted" melee/missile weapons with no tinks (171), no imbue (179),
//               not equipped, not never-chunk, cleave (292) <= 1.
//   Peace mode only unless ChunkInCombat; waits for the result line (10 s max).
// Clap: "/clap all" converts aetheria chunks + trinkets into coins server-side. Needs the
//   AutoCraftingEnabled stamp; 30 min ± 2 min interval, first attempt ~1 min after enable.
using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi.IltHub;

internal sealed class IltChunkClap : IIltFeature
{
    private const long ChunkResultWaitMs = 10_000;
    private const long ClapFirstDelayMs = 60_000;
    private const long ClapJitterMs = 120_000;

    private static readonly string[] ChunkDoneLines =
    {
        "You smash the aetheria with the hammer.", "The two artifacts link together effortlessly.",
        "You successfully chunk the aetheria.", "You fail to chunk the aetheria.",
        "The corrupted weapon is crushed into a fine powder.", "You smash the corrupted weapon with the hammer.",
        "The corrupted weapon shatters into fragments.", "You fail to chunk the corrupted weapon.",
    };
    private static readonly string[] ChunkRefusedLines =
    {
        "The aetheria is too powerful to chunk.", "You cannot chunk this aetheria.",
        "The corrupted weapon is too powerful to chunk.", "You cannot chunk this corrupted weapon.",
    };

    private readonly IltHubContext _ctx;
    private readonly Random _rng = new();

    // Chunk state
    private long _lastChunkAt;
    private int _waitingOn;
    private long _waitStartedAt;
    private readonly HashSet<int> _skip = new();
    private bool _manualPass;
    private volatile string _chunkStatus = "idle";
    private int _chunked;

    // Clap state
    private long _nextClapAt;
    private volatile string _clapStatus = "idle";
    private bool _clapWasEnabled;

    public IltChunkClap(IltHubContext ctx) => _ctx = ctx;

    private IltGearState G => _ctx.State.Gear;

    // ── Chunk ───────────────────────────────────────────────────────────────

    private WorldObject? FindHammer()
        => _ctx.Inventory.FindByName(n => n.Equals("Lugian Aetheria Hammer", StringComparison.OrdinalIgnoreCase)
                                          || n.Equals("Lugian Hammer", StringComparison.OrdinalIgnoreCase)
                                          || n.EndsWith(" Lugian Hammer", StringComparison.OrdinalIgnoreCase));

    /// <summary>True/false when decidable; null when appraisal is still needed.</summary>
    private bool? IsChunkableAetheria(WorldObject wo)
    {
        if (wo.ObjectClass != AcObjectClass.Gem) return false;
        bool coalesced = wo.Name.Equals("Coalesced Aetheria", StringComparison.OrdinalIgnoreCase);
        if (!coalesced && (!wo.Name.Contains("Aetheria", StringComparison.OrdinalIgnoreCase) || wo.Name.Contains("Chunk", StringComparison.OrdinalIgnoreCase)))
            return false;
        var inv = _ctx.Inventory;
        if (inv.IsEquipped(wo)) return false;
        if (!inv.HasAppraisal(wo)) { inv.RequestAppraisal(wo); return null; }
        if (inv.Bool(wo, IltInventory.BoolNeverChunk)) return false;
        if (inv.Quad(wo, IltInventory.QuadItemTotalXp) != 0) return false;
        return coalesced || inv.Int(wo, IltInventory.IntItemMaxLevel) < Math.Max(1, G.ChunkAetheriaUnderLevel);
    }

    private bool? IsChunkableCorrupted(WorldObject wo)
    {
        if (wo.ObjectClass is not (AcObjectClass.MeleeWeapon or AcObjectClass.MissileWeapon)) return false;
        if (!wo.Name.Contains("Corrupted", StringComparison.OrdinalIgnoreCase)) return false;
        var inv = _ctx.Inventory;
        if (inv.IsEquipped(wo)) return false;
        if (!inv.HasAppraisal(wo)) { inv.RequestAppraisal(wo); return null; }
        if (inv.Bool(wo, IltInventory.BoolNeverChunk)) return false;
        if (inv.Int(wo, IltInventory.IntNumTimesTinkered) != 0 || inv.Int(wo, IltInventory.IntImbuedEffect) != 0) return false;
        return inv.Int(wo, IltInventory.IntCleaving) <= 1;
    }

    private WorldObject? NextChunkTarget()
    {
        foreach (var wo in _ctx.Inventory.Items())
        {
            if (_skip.Contains(wo.Id)) continue;
            if ((G.AutoChunkAetheria || _manualPass) && IsChunkableAetheria(wo) == true) return wo;
            if ((G.AutoChunkCorrupted || _manualPass) && IsChunkableCorrupted(wo) == true) return wo;
        }
        return null;
    }

    /// <summary>Runs one chunk pass now (both item kinds) regardless of the auto toggles.</summary>
    public void ChunkNow()
    {
        RynthLog.Trace(LogCat.IltGear, $"ChunkNow()");
        _manualPass = true;
        _lastChunkAt = 0;
        _chunkStatus = "manual pass started";
    }

    private void TickChunk(long now)
    {
        if (!G.AutoChunkAetheria && !G.AutoChunkCorrupted && !_manualPass) return;

        if (_waitingOn != 0)
        {
            if (now - _waitStartedAt < ChunkResultWaitMs) return;
            _skip.Add(_waitingOn); // no result line — don't hammer the same item forever
            _waitingOn = 0;
        }
        if (now - _lastChunkAt < Math.Max(500, G.ChunkIntervalMs)) return;
        _lastChunkAt = now;

        if (!G.ChunkInCombat && !_ctx.Inventory.InPeaceMode) { _chunkStatus = "waiting for peace mode"; return; }
        if (_ctx.Inventory.IsBusy) return;

        var hammer = FindHammer();
        if (hammer == null) { _chunkStatus = "no Lugian Hammer carried"; _manualPass = false; return; }
        var target = NextChunkTarget();
        if (target == null)
        {
            _chunkStatus = _chunked > 0 ? $"nothing left to chunk ({_chunked} this session)" : "nothing eligible";
            _manualPass = false;
            return;
        }
        if (_ctx.Inventory.UseOn(hammer, unchecked((uint)target.Id)))
        {
            _waitingOn = target.Id;
            _waitStartedAt = now;
            _chunkStatus = "chunking " + target.Name;
        }
    }

    // ── Clap ────────────────────────────────────────────────────────────────

    private bool HasClapMaterials()
    {
        var inv = _ctx.Inventory;
        foreach (var wo in inv.Items())
        {
            uint w = inv.Wcid(wo);
            if ((w == IltInventory.WcidRedAetheriaChunk || w == IltInventory.WcidBlueAetheriaChunk)
                && inv.Int(wo, IltInventory.IntItemMaxLevel) <= 3)
                return true;
            string n = wo.Name;
            if (n.Equals("Red Aetheria Chunk", StringComparison.OrdinalIgnoreCase) || n.Equals("Blue Aetheria Chunk", StringComparison.OrdinalIgnoreCase)
                || n.Equals("Ancient Empyrean Trinket", StringComparison.OrdinalIgnoreCase) || n.Equals("Ancient Falatacot Trinket", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Sends "/clap all" through the capture queue (reply stays visible).</summary>
    public void ClapNow(bool manual)
    {
        RynthLog.Trace(LogCat.IltGear, $"ClapNow(manual={manual})");
        if (_ctx.Options.IsOff(IltFeature.Clap)) { _clapStatus = "/clap is not available (server or AutoCraftingEnabled stamp)"; return; }
        if (_ctx.Capture.IsPending("/clap all")) return;
        if (!manual && !HasClapMaterials()) { _clapStatus = "no aetheria materials carried"; return; }
        _clapStatus = "sending /clap all...";
        _ctx.Capture.Enqueue(new IltChatRequest
        {
            Command = "/clap all",
            IsResponseLine = t => t.StartsWith("Deposited ", StringComparison.Ordinal)
                               || t.StartsWith("[Clap]", StringComparison.OrdinalIgnoreCase)
                               || t.Contains("aetheria materials", StringComparison.OrdinalIgnoreCase)
                               || t.Contains("AutoCraftingEnabled", StringComparison.OrdinalIgnoreCase),
            IdleEndMs = 800,
            FirstLineTimeoutMs = 10_000,
            Eat = false,
            OnComplete = r =>
            {
                if (r.UnknownCommand) { _ctx.Options.Set(IltFeature.Clap, IltTri.Off); _clapStatus = "/clap not available"; return; }
                _clapStatus = r.Lines.Count > 0 ? r.Lines[^1] : "no reply";
                if (r.Lines.Any(l => l.StartsWith("Deposited ", StringComparison.Ordinal))) _ctx.Options.Set(IltFeature.Clap, IltTri.On);
            },
        });
    }

    private void TickClap(long now)
    {
        // Automation only runs once the server has positively confirmed /clap (unknown = wait).
        if (!G.AutoClap || !_ctx.Options.IsOn(IltFeature.Clap)) { _clapWasEnabled = false; return; }
        if (!_clapWasEnabled)
        {
            _clapWasEnabled = true;
            _nextClapAt = now + ClapFirstDelayMs;
            _clapStatus = "first auto-clap in ~1 min";
            return;
        }
        if (now < _nextClapAt) return;
        long interval = Math.Max(5, G.ClapIntervalMinutes) * 60_000L;
        _nextClapAt = now + interval + (long)((_rng.NextDouble() * 2 - 1) * ClapJitterMs);
        ClapNow(manual: false);
    }

    // ── IIltFeature ─────────────────────────────────────────────────────────

    public void Tick(long nowMs)
    {
        TickChunk(nowMs);
        TickClap(nowMs);
    }

    public bool OnChat(string text)
    {
        if (_waitingOn == 0) return false;
        if (text.StartsWith("You're too busy", StringComparison.OrdinalIgnoreCase) || text.StartsWith("You are too busy", StringComparison.OrdinalIgnoreCase))
        {
            _waitingOn = 0; // retry the same item next interval
            return false;
        }
        if (ChunkDoneLines.Any(l => text.Contains(l, StringComparison.OrdinalIgnoreCase)))
        {
            _chunked++;
            _waitingOn = 0;
        }
        else if (ChunkRefusedLines.Any(l => text.Contains(l, StringComparison.OrdinalIgnoreCase)))
        {
            _skip.Add(_waitingOn);
            _waitingOn = 0;
        }
        return false;
    }

    public void OnLogout()
    {
        _waitingOn = 0;
        _skip.Clear();
        _manualPass = false;
        _clapWasEnabled = false;
    }

    // ── UI (render thread) ──────────────────────────────────────────────────

    public void Render()
    {
        ImGui.TextColored(LegacyDashboardRenderer.ColTeal, "Chunking (Lugian Hammer)");
        bool a = G.AutoChunkAetheria, c = G.AutoChunkCorrupted, comb = G.ChunkInCombat;
        if (ImGui.Checkbox("Auto-chunk low aetheria", ref a)) G.AutoChunkAetheria = a;
        ImGui.SameLine();
        if (ImGui.Checkbox("Auto-chunk corrupted weapons", ref c)) G.AutoChunkCorrupted = c;
        if (ImGui.Checkbox("Allow while in combat mode", ref comb)) G.ChunkInCombat = comb;
        int under = G.ChunkAetheriaUnderLevel;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Chunk aetheria with max level below", ref under)) G.ChunkAetheriaUnderLevel = Math.Clamp(under, 1, 6);
        if (ImGui.Button("Chunk eligible items now...")) _ctx.Confirm("Chunk items",
            "Run one chunk pass now on every eligible aetheria and corrupted weapon?\nChunked items are destroyed.",
            ChunkNow, "Chunk");
        ImGui.TextDisabled("Status: " + _chunkStatus);

        ImGui.Separator();
        ImGui.TextColored(LegacyDashboardRenderer.ColTeal, "Clap (/clap all)");
        if (_ctx.Options.IsOff(IltFeature.Clap))
        {
            ImGui.TextColored(LegacyDashboardRenderer.ColTextMute, "Not available (server off or AutoCraftingEnabled stamp missing).");
            return;
        }
        bool ac = G.AutoClap;
        if (ImGui.Checkbox("Auto-clap periodically", ref ac)) G.AutoClap = ac;
        int mins = G.ClapIntervalMinutes;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Interval (min)", ref mins)) G.ClapIntervalMinutes = Math.Max(5, mins);
        if (ImGui.SmallButton("Clap now")) _ctx.Post(() => ClapNow(manual: true));
        ImGui.SameLine();
        ImGui.TextDisabled(_clapStatus);
    }
}
