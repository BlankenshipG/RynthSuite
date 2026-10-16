// IltChunkClap.cs — Lugian-hammer chunking and /clap automation (Gear tab).
// Originally SilentKelpie's ILT Hub (RynthAi IltHub/); moved to RynthILT 2026-10-13.
//
// Chunk: uses the Lugian Aetheria Hammer on eligible items, one at a time:
//   Aetheria  — "Aetheria"/"Coalesced Aetheria" gems, not equipped, appraised, not flagged
//               never-chunk (bool 9030), never levelled (ItemTotalXp quad 4 == 0), no kept
//               C / CD / CDR rating (gear ints 372 / 374 / 375; on by default), and max level
//               (int 319, up to 10 on ILT) under the configured cap. Coalesced Aetheria has its
//               level from loot too, so the cap applies to it (it used to skip the cap and could
//               chunk a level 10 piece).
//   Corrupted — "Corrupted" melee/missile weapons with no tinks (171), no imbue (179),
//               not equipped, not never-chunk, cleave (292) <= 1.
//   Peace mode only unless ChunkInCombat; waits for the result line (10 s max).
//   The tool is the "Lugian Aetheria Hammer" only. The Lugian Hammer weapons (Flaming, Acid, ...)
//   are loot and chunk nothing on ILT: the server answers "done" with no result line. Matching
//   them by name ending used the Flaming Lugian Hammer on Silentkelpie's 9 Coalesced Aetheria,
//   every one timed out and was skipped, and the status sat on "nothing eligible" (2026-10-15).
// Clap: "/clap all" converts aetheria + trinkets into coins server-side: red (Coalesced level 1-3,
//   chunks, powder) with Empyrean Trinkets, blue with Falatacot Trinkets. Auto-clap sends it only
//   when a pair is carried. Needs the AutoCraftingEnabled stamp; 30 min ± 2 min interval, first
//   attempt ~1 min after enable.
using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using RynthCore.Loot;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthILT.Hub;

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

    /// <summary>Infinite Leaftide's chunking tool.</summary>
    internal const string HammerName = "Lugian Aetheria Hammer";

    /// <summary>True for the chunking tool only, never a Lugian Hammer weapon. Pure: the tests drive it.</summary>
    internal static bool IsChunkHammer(string name) => name.Equals(HammerName, StringComparison.OrdinalIgnoreCase);

    private WorldObject? FindHammer()
        => _ctx.Inventory.Items().FirstOrDefault(wo => IsChunkHammer(wo.Name) && !_ctx.Inventory.IsEquipped(wo));

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
        if (KeptForRatings(G, inv.Int(wo, IltInventory.IntGearCrit), inv.Int(wo, IltInventory.IntGearCritDamage),
                           inv.Int(wo, IltInventory.IntGearCritDamageResist)))
            return false;
        return UnderChunkCap(inv.Int(wo, IltInventory.IntItemMaxLevel), G.ChunkAetheriaUnderLevel);
    }

    /// <summary>The chunk cap in range: 1 (chunk nothing by level) to one past the ILT maximum (every level).</summary>
    internal static int ClampUnderLevel(int under) => Math.Clamp(under, 1, IltInventory.AetheriaMaxLevel + 1);

    /// <summary>
    /// True when an aetheria's max level (int 319) is under the cap. Coalesced Aetheria has its level
    /// from loot (ACECustom rolls 1-10; level 6+ can carry a rating), so it gets the same cap.
    /// 0 = no level read: never chunked. Pure: the tests drive it.
    /// </summary>
    internal static bool UnderChunkCap(int maxLevel, int under) => maxLevel > 0 && maxLevel < ClampUnderLevel(under);

    /// <summary>
    /// True when an aetheria's C / CD / CDR rating is one the settings keep (any rating above 0 with
    /// its keep switch on). Pure: the tests drive it.
    /// </summary>
    internal static bool KeptForRatings(IltGearState g, int crit, int critDamage, int critDamageResist)
        => (g.KeepAetheriaWithCrit && crit > 0)
           || (g.KeepAetheriaWithCritDamage && critDamage > 0)
           || (g.KeepAetheriaWithCritDamageResist && critDamageResist > 0);

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
        _skip.Clear();   // a manual pass gives the items that got no answer another try
        _chunkStatus = "manual pass started";
    }

    private void TickChunk(long now)
    {
        if (!G.AutoChunkAetheria && !G.AutoChunkCorrupted && !_manualPass) return;

        if (_waitingOn != 0)
        {
            if (now - _waitStartedAt < ChunkResultWaitMs) return;
            _skip.Add(_waitingOn); // no result line — don't hammer the same item forever
            RynthLog.Write(LogCat.IltGear, $"chunk: no result line for 0x{(uint)_waitingOn:X8} in {ChunkResultWaitMs / 1000} s, skipped until a manual pass");
            _waitingOn = 0;
        }
        if (now - _lastChunkAt < Math.Max(500, G.ChunkIntervalMs)) return;
        _lastChunkAt = now;

        if (!G.ChunkInCombat && !_ctx.Inventory.InPeaceMode) { _chunkStatus = "waiting for peace mode"; return; }
        if (_ctx.Inventory.IsBusy) return;

        var hammer = FindHammer();
        if (hammer == null) { _chunkStatus = $"no {HammerName} carried"; _manualPass = false; return; }
        var target = NextChunkTarget();
        if (target == null)
        {
            _chunkStatus = (_chunked > 0 ? $"nothing left to chunk ({_chunked} this session)" : "nothing eligible")
                         + (_skip.Count > 0 ? $"; {_skip.Count} skipped with no answer (Chunk now retries them)" : "");
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

    internal enum ClapPart { None, Red, Blue, Empyrean, Falatacot }

    /// <summary>
    /// Which side of /clap an item feeds (ACECustom PlayerCommands.HandleClap): red material (Red
    /// Coalesced Aetheria, chunks, powder) pairs with Ancient Empyrean Trinkets, blue with Ancient
    /// Falatacot Trinkets. Coalesced counts only unrevealed and at level 1-3; level 0 (not read yet)
    /// counts, and the server has the last word. With no WCID yet, the chunk and trinket names
    /// decide. Pure: the tests drive it.
    /// </summary>
    internal static ClapPart ClapPartOf(uint wcid, string name, int maxLevel)
    {
        bool lowCoalesced = name.Equals("Coalesced Aetheria", StringComparison.OrdinalIgnoreCase) && maxLevel <= 3;
        switch (wcid)
        {
            case IltInventory.WcidRedCoalescedAetheria:  return lowCoalesced ? ClapPart.Red : ClapPart.None;
            case IltInventory.WcidBlueCoalescedAetheria: return lowCoalesced ? ClapPart.Blue : ClapPart.None;
            case IltInventory.WcidRedAetheriaChunk:
            case IltInventory.WcidRedAetheriaPowder:     return ClapPart.Red;
            case IltInventory.WcidBlueAetheriaChunk:
            case IltInventory.WcidBlueAetheriaPowder:    return ClapPart.Blue;
            case IltInventory.WcidEmpyreanTrinket:       return ClapPart.Empyrean;
            case IltInventory.WcidFalatacotTrinket:      return ClapPart.Falatacot;
            case 0: break;
            default: return ClapPart.None;
        }
        if (name.Equals("Red Aetheria Chunk", StringComparison.OrdinalIgnoreCase)) return ClapPart.Red;
        if (name.Equals("Blue Aetheria Chunk", StringComparison.OrdinalIgnoreCase)) return ClapPart.Blue;
        if (name.Equals("Ancient Empyrean Trinket", StringComparison.OrdinalIgnoreCase)) return ClapPart.Empyrean;
        if (name.Equals("Ancient Falatacot Trinket", StringComparison.OrdinalIgnoreCase)) return ClapPart.Falatacot;
        return ClapPart.None;
    }

    /// <summary>
    /// True when /clap has a pair to work with: red material and an Empyrean Trinket, or blue
    /// material and a Falatacot Trinket. One side alone gets "You don't have any aetheria materials".
    /// Pure: the tests drive it.
    /// </summary>
    internal static bool CanClap(IEnumerable<ClapPart> parts)
    {
        bool red = false, blue = false, empyrean = false, falatacot = false;
        foreach (var part in parts)
        {
            switch (part)
            {
                case ClapPart.Red: red = true; break;
                case ClapPart.Blue: blue = true; break;
                case ClapPart.Empyrean: empyrean = true; break;
                case ClapPart.Falatacot: falatacot = true; break;
            }
        }
        return (red && empyrean) || (blue && falatacot);
    }

    private bool HasClapMaterials()
    {
        var inv = _ctx.Inventory;
        return CanClap(inv.Items().Where(wo => !inv.IsEquipped(wo))
                                  .Select(wo => ClapPartOf(inv.Wcid(wo), wo.Name, inv.Int(wo, IltInventory.IntItemMaxLevel))));
    }

    /// <summary>Sends "/clap all" through the capture queue (reply stays visible).</summary>
    public void ClapNow(bool manual)
    {
        RynthLog.Trace(LogCat.IltGear, $"ClapNow(manual={manual})");
        if (_ctx.Options.IsOff(IltFeature.Clap)) { _clapStatus = "/clap is not available (server or AutoCraftingEnabled stamp)"; return; }
        if (_ctx.Capture.IsPending("/clap all")) return;
        if (!manual && !HasClapMaterials()) { _clapStatus = "no aetheria and trinket pair carried (red + Empyrean, blue + Falatacot)"; return; }
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

    internal enum ChunkResult { None, Busy, Done, Refused }

    /// <summary>
    /// What a chat line says about the chunk in flight. ILT names the item and the tool ("You
    /// smash the Corrupted Sword with the Lugian Aetheria Hammer."), so a line naming a corrupted
    /// item with the hammer, powder or shatter is a result too (combat's "You smash Foo for 147"
    /// names neither). Pure: the tests drive it.
    /// </summary>
    internal static ChunkResult ClassifyChat(string text)
    {
        if (text.StartsWith("You're too busy", StringComparison.OrdinalIgnoreCase) || text.StartsWith("You are too busy", StringComparison.OrdinalIgnoreCase))
            return ChunkResult.Busy;
        if (ChunkRefusedLines.Any(l => text.Contains(l, StringComparison.OrdinalIgnoreCase))) return ChunkResult.Refused;
        if (ChunkDoneLines.Any(l => text.Contains(l, StringComparison.OrdinalIgnoreCase))) return ChunkResult.Done;
        if (text.Contains("corrupted", StringComparison.OrdinalIgnoreCase)
            && (text.Contains("hammer", StringComparison.OrdinalIgnoreCase) || text.Contains("powder", StringComparison.OrdinalIgnoreCase)
                || text.Contains("shatter", StringComparison.OrdinalIgnoreCase)))
            return ChunkResult.Done;
        return ChunkResult.None;
    }

    public bool OnChat(string text)
    {
        if (_waitingOn == 0) return false;
        switch (ClassifyChat(text))
        {
            case ChunkResult.Busy:
                _waitingOn = 0; // retry the same item next interval
                break;
            case ChunkResult.Done:
                _chunked++;
                _waitingOn = 0;
                break;
            case ChunkResult.Refused:
                _skip.Add(_waitingOn);
                _waitingOn = 0;
                break;
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
        ImGui.TextColored(IltStyle.ColTeal, $"Chunking ({HammerName})");
        bool a = G.AutoChunkAetheria, c = G.AutoChunkCorrupted, comb = G.ChunkInCombat;
        if (ImGui.Checkbox("Auto-chunk low aetheria", ref a)) G.AutoChunkAetheria = a;
        ImGui.SameLine();
        if (ImGui.Checkbox("Auto-chunk corrupted weapons", ref c)) G.AutoChunkCorrupted = c;
        if (ImGui.Checkbox("Allow while in combat mode", ref comb)) G.ChunkInCombat = comb;
        int under = G.ChunkAetheriaUnderLevel;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Chunk aetheria with max level below", ref under)) G.ChunkAetheriaUnderLevel = ClampUnderLevel(under);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Infinite Leaftide aetheria goes up to level {IltInventory.AetheriaMaxLevel}; {IltInventory.AetheriaMaxLevel + 1} chunks every level.\nApplies to Coalesced Aetheria too: it has its level before it is revealed.");
        bool kc = G.KeepAetheriaWithCrit, kcd = G.KeepAetheriaWithCritDamage, kcdr = G.KeepAetheriaWithCritDamageResist;
        ImGui.TextUnformatted("Never chunk aetheria with:");
        ImGui.SameLine();
        if (ImGui.Checkbox("C##keepc", ref kc)) G.KeepAetheriaWithCrit = kc;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("A Crit rating (GearCrit).");
        ImGui.SameLine();
        if (ImGui.Checkbox("CD##keepcd", ref kcd)) G.KeepAetheriaWithCritDamage = kcd;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("A Crit Damage rating (GearCritDamage).");
        ImGui.SameLine();
        if (ImGui.Checkbox("CDR##keepcdr", ref kcdr)) G.KeepAetheriaWithCritDamageResist = kcdr;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("A Crit Damage Resist rating (GearCritDamageResist).");
        if (ImGui.Button("Chunk eligible items now...")) _ctx.Confirm("Chunk items",
            "Run one chunk pass now on every eligible aetheria and corrupted weapon?\nChunked items are destroyed.",
            ChunkNow, "Chunk");
        SafeImGui.TextDisabled("Status: " + _chunkStatus);

        ImGui.Separator();
        ImGui.TextColored(IltStyle.ColTeal, "Clap (/clap all)");
        if (_ctx.Options.IsOff(IltFeature.Clap))
        {
            ImGui.TextColored(IltStyle.ColTextMute, "Not available (server off or AutoCraftingEnabled stamp missing).");
            return;
        }
        bool ac = G.AutoClap;
        if (ImGui.Checkbox("Auto-clap periodically", ref ac)) G.AutoClap = ac;
        int mins = G.ClapIntervalMinutes;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Interval (min)", ref mins)) G.ClapIntervalMinutes = Math.Max(5, mins);
        if (ImGui.SmallButton("Clap now")) _ctx.Post(() => ClapNow(manual: true));
        ImGui.SameLine();
        SafeImGui.TextDisabled(_clapStatus);
    }
}
