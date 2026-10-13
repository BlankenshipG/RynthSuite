// RingChainRunner.cs - Crafts Infinite Leaftide's ring inscription chain and applies the scrolls
// (Crafting > Rings).
//
// Pump thread only (the tab's Render reads the volatile snapshots and posts the commands).
// Crafting: the queue from RingChain.Expand, one combine at a time:
//   1. gates: peace mode (unless the server allows combat crafting), not busy, ActionPacer;
//   2. find the tool and the target by name; a later step's ingredients come out of the earlier
//      combine, so a missing one is looked for again for a while (inventory lag) before giving up;
//   3. arm the engine's confirmation answer (Yes: the player queued it) and use tool on target;
//   4. the combine is done when the tool or the target stack shrinks or the output shows up.
//      No sign of it stops the job instead of retrying, so nothing is crafted twice.
// Scrolls: each "Inscription of ..." scroll is used on its target (the item selected in AC, or
// the last Jewelry / weapon scan's items paired the UB-IT way) and is done when it leaves the pack.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RynthCore.Loot;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.MiniRemote.Crafting;

/// <summary>One line of the Rings scan: a recipe header (Found null) or an ingredient.</summary>
/// <param name="State">0 found, 1 comes from an earlier step, 2 missing.</param>
internal sealed record RingScanLine(string Text, string? Found, int State);

internal sealed class RingChainRunner
{
    private const uint ErrorYoureTooBusy = 0x1D;
    private const long StepTimeoutMs = 15_000;
    /// <summary>The player answers AC's own dialog: give them time.</summary>
    private const long ManualTimeoutMs = 60_000;
    private const long StepGapMs = 700;
    private const long RetryDelayMs = 1_500;
    private const int MaxBusyRetries = 3;
    /// <summary>A missing ingredient is looked for this often and this many times (~10 s).</summary>
    private const long LookAgainMs = 250;
    private const int LookTries = 40;

    private static readonly Regex CannotUse = new(@"^The .+ cannot be used on the .+\.$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private enum Mode { Idle, Craft, Apply }

    private readonly MiniRemoteContext _ctx;
    /// <summary>True while another crafting job (the tinker runner) owns the pack.</summary>
    private readonly Func<bool> _blocked;

    // Job (pump thread)
    private Mode _mode;
    private List<RingRecipe> _queue = new();
    private List<(int Scroll, int Target)> _pairs = new();
    private int _index, _crafted, _lookTries, _busyRetries, _confSeq, _errSeq;
    private bool _waiting, _manual;
    private long _stepAt, _nextAt;
    // Counts before the current combine (craft) / the scroll's stack (apply).
    private long _toolBefore, _targetBefore, _outputBefore;
    private int _scrollStackBefore;

    // Render snapshots
    private volatile string _status = "idle";
    private volatile RingScanLine[] _scan = Array.Empty<RingScanLine>();
    private volatile (int Id, string Name)[] _scrolls = Array.Empty<(int, string)>();
    private volatile bool _running;

    public RingChainRunner(MiniRemoteContext ctx, Func<bool> blocked)
    {
        _ctx = ctx;
        _blocked = blocked;
    }

    public bool Running => _running;
    public string Status => _status;
    public RingScanLine[] ScanLines => _scan;
    /// <summary>The inscription scrolls the last scan found.</summary>
    public (int Id, string Name)[] Scrolls => _scrolls;

    private RynthCoreHost Host => _ctx.Host;
    private MiniRemoteInventory Inv => _ctx.Inventory;
    private CraftingTabState C => _ctx.State.Crafting;

    private int Runs(string id)
    {
        var map = C.RingRuns;
        lock (map) return map.TryGetValue(id, out int n) ? n : 0;
    }

    // ── Inventory ───────────────────────────────────────────────────────────

    private IEnumerable<WorldObject> Carried() => Inv.Items().Where(w => !Inv.IsEquipped(w));

    private WorldObject? Find(RingIngredient ing) => Carried().FirstOrDefault(w => RingChain.Matches(w.Name, ing));

    private long Count(RingIngredient ing) => Carried().Where(w => RingChain.Matches(w.Name, ing)).Sum(w => (long)Inv.StackSize(w));

    private long ScrollCount() => Carried().Where(w => RingChain.IsInscriptionScroll(w.Name)).Sum(w => (long)Inv.StackSize(w));

    /// <summary>What a recipe makes, counted: Step 1 the enlightened quill, Step 2 the partition quill, Step 3 a scroll.</summary>
    private long OutputCount(RingRecipe r) => r.Tier switch
    {
        1 => Count(RingChain.Recipes[1].Tool),
        2 => Count(RingChain.Recipes[2].Tool),
        _ => ScrollCount(),
    };

    // ── Scan ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists every queued recipe's ingredients, marking the ones a later step only gets from an
    /// earlier combine (UB-IT's "(after Step 1)"), and the carried inscription scrolls.
    /// </summary>
    public void Scan()
    {
        var lines = new List<RingScanLine>();
        bool Stocked(int tier) => RingChain.Recipes
            .Where(r => r.Tier == tier && Runs(r.Id) > 0)
            .All(r => Find(r.Tool) != null && Find(r.Target) != null);
        bool tier1 = Stocked(1), tier2 = Stocked(2);
        bool any = false, blocking = false;
        foreach (var r in RingChain.Recipes)
        {
            int runs = Runs(r.Id);
            if (runs <= 0) continue;
            any = true;
            lines.Add(new RingScanLine($"{r.Name} x{runs}", null, 0));
            foreach (var ing in new[] { r.Tool, r.Target })
            {
                var found = Find(ing);
                if (found != null) { lines.Add(new RingScanLine(ing.Label, $"{found.Name} ({Count(ing)})", 0)); continue; }
                string? later = r.Tier == 2 && !tier1 ? "(after Step 1)"
                    : r.Tier == 3 && !tier1 ? "(after Step 1)"
                    : r.Tier == 3 && !tier2 ? "(after Step 2)"
                    : null;
                if (later == null) blocking = true;
                lines.Add(new RingScanLine(ing.Label, later ?? "(not found)", later == null ? 2 : 1));
            }
        }
        _scan = lines.ToArray();
        RefreshScrolls();
        _status = !any ? "set how many times to craft at least one recipe"
            : blocking ? "missing ingredients are marked (not found)"
            : "ready: Create runs every Step 1, then Step 2, then Step 3";
    }

    private void RefreshScrolls()
        => _scrolls = Carried().Where(w => RingChain.IsInscriptionScroll(w.Name))
            .Select(w => (w.Id, w.Name ?? string.Empty))
            .OrderBy(s => RingChain.GlyphOf(s.Item2)?.Id ?? "zzz_" + s.Item2, StringComparer.Ordinal)
            .ToArray();

    // ── Job control ─────────────────────────────────────────────────────────

    /// <summary>Crafts the queued recipes (pump thread, after the tab's confirm).</summary>
    public void Create()
    {
        if (_running) return;
        if (_blocked()) { _status = "a tinkering job is running"; return; }
        var queue = RingChain.Expand(Runs);
        if (queue.Count == 0) { _status = "set how many times to craft at least one recipe"; return; }
        Begin(Mode.Craft);
        _queue = queue;
        _status = $"crafting {queue.Count} combine(s)";
        RynthLog.Write(LogCat.Crafting, $"[Crafting] Rings: create, {queue.Count} combine(s)");
    }

    /// <summary>Uses one scroll on the item selected in AC.</summary>
    public void ApplyToSelected(int scrollId)
    {
        uint sel = Host.GetSelectedItemId();
        if (sel == 0) { _status = "select the ring or item in AC first"; return; }
        if (unchecked((int)sel) == scrollId) { _status = "the scroll itself is selected: select the ring or item"; return; }
        ApplyPairs(new List<(int, int)> { (scrollId, unchecked((int)sel)) });
    }

    /// <summary>Pairs the carried scrolls with these items (the last Jewelry / weapon scan) and uses them.</summary>
    public void ApplyToItems(IReadOnlyList<int> targetIds)
    {
        RefreshScrolls();
        if (_scrolls.Length == 0) { _status = "no inscription scroll in the pack (name has \"Inscription of\")"; return; }
        if (targetIds.Count == 0) { _status = "scan the Jewelry or a Weapons tab first: its items are the targets"; return; }
        ApplyPairs(RingChain.PairScrolls(_scrolls, targetIds));
    }

    private void ApplyPairs(List<(int Scroll, int Target)> pairs)
    {
        if (_running) return;
        if (_blocked()) { _status = "a tinkering job is running"; return; }
        if (pairs.Count == 0) { _status = "nothing to apply"; return; }
        Begin(Mode.Apply);
        _pairs = pairs;
        _status = $"applying {pairs.Count} inscription(s)";
        RynthLog.Write(LogCat.Crafting, $"[Crafting] Rings: apply {pairs.Count} inscription(s)");
    }

    private void Begin(Mode mode)
    {
        _mode = mode;
        _index = 0;
        _crafted = 0;
        _lookTries = 0;
        _busyRetries = 0;
        _waiting = false;
        _nextAt = 0;
        _running = true;
    }

    public void Stop(string why)
    {
        if (_waiting && Host.HasConfirmationAnswers) Host.ArmConfirmation(RynthCoreHost.ConfirmationKind.CraftInteraction, 0, 0);
        _mode = Mode.Idle;
        _waiting = false;
        _running = false;
        _queue = new List<RingRecipe>();
        _pairs = new List<(int, int)>();
        _status = why;
    }

    public void OnLogout()
    {
        Stop("idle");
        _scan = Array.Empty<RingScanLine>();
        _scrolls = Array.Empty<(int, string)>();
    }

    // ── Tick (pump thread) ──────────────────────────────────────────────────

    public void Tick(long nowMs)
    {
        if (_mode == Mode.Idle) return;
        if (_waiting) { Wait(nowMs); return; }
        if (nowMs < _nextAt) return;
        if (!C.AllowInCombat && !Inv.InPeaceMode) { _status = "waiting for peace mode"; return; }
        if (Inv.IsBusy) return;
        if (!ActionPacer.MayStart(Host, "Crafting", HealNeed.None, out string why)) { _status = "waiting: " + why; return; }

        if (_mode == Mode.Craft) NextCombine(nowMs);
        else NextScroll(nowMs);
    }

    private void NextCombine(long nowMs)
    {
        if (_index >= _queue.Count)
        {
            Stop($"done - {_crafted} combine(s)");
            RefreshScrolls();
            RynthLog.Write(LogCat.Crafting, $"[Crafting] Rings: done, {_crafted} combine(s)");
            return;
        }
        var r = _queue[_index];
        var tool = Find(r.Tool);
        var target = Find(r.Target);
        if (tool == null || target == null)
        {
            string label = tool == null ? r.Tool.Label : r.Target.Label;
            if (++_lookTries <= LookTries)
            {
                _nextAt = nowMs + LookAgainMs;
                _status = $"{r.Name}: looking for {label}";
                return;
            }
            Stop($"{r.Name}: no {label} in the pack");
            return;
        }
        _lookTries = 0;

        _toolBefore = Count(r.Tool);
        _targetBefore = Count(r.Target);
        _outputBefore = OutputCount(r);
        if (!Use(tool, target, $"Rings: {r.Name} ({_index + 1}/{_queue.Count})"))
        {
            Stop($"{r.Name}: the use request didn't go out");
            return;
        }
        string ask = _manual ? " - click Yes in AC's craft dialog" : "";
        _status = $"{r.Name} ({_index + 1}/{_queue.Count}): {tool.Name} on {target.Name}{ask}";
    }

    private void NextScroll(long nowMs)
    {
        if (_index >= _pairs.Count)
        {
            Stop($"done - {_crafted} inscription(s) applied");
            RefreshScrolls();
            return;
        }
        var (scrollId, targetId) = _pairs[_index];
        var scroll = Inv.Cache?[scrollId];
        var target = Inv.Cache?[targetId];
        if (scroll == null || target == null)
        {
            // UB-IT moves on past a pair whose scroll or item is gone.
            _status = $"inscription {_index + 1}: the scroll or the item is gone - skipped";
            _index++;
            _nextAt = nowMs + StepGapMs;
            return;
        }
        if (Inv.IsEquipped(target)) { Stop($"{target.Name} is equipped: take it off to inscribe it"); return; }
        _scrollStackBefore = Inv.StackSize(scroll);
        if (!Use(scroll, target, $"Rings: {scroll.Name} on {target.Name}"))
        {
            Stop("the use request didn't go out");
            return;
        }
        string ask = _manual ? " - click Yes in AC's dialog" : "";
        _status = $"inscription {_index + 1}/{_pairs.Count}: {scroll.Name} on {target.Name}{ask}";
    }

    /// <summary>Arms the dialog answer (Yes: the player asked for this) and uses source on target.</summary>
    private bool Use(WorldObject source, WorldObject target, string why)
    {
        _manual = !(C.AnswerCraftDialog && Host.HasConfirmationAnswers
                    && Host.ArmConfirmation(RynthCoreHost.ConfirmationKind.CraftInteraction, 0, (int)StepTimeoutMs) == 1);
        _confSeq = Host.TryGetLastConfirmation(out int cs, out _, out _, out _, out _) ? cs : 0;
        _errSeq = Host.TryGetLastWeenieError(out int es, out _, out _, out _) ? es : 0;
        if (!Host.UseOnFor(unchecked((uint)source.Id), unchecked((uint)target.Id), "Crafting", why, UseKind.Asked))
        {
            if (!_manual) Host.ArmConfirmation(RynthCoreHost.ConfirmationKind.CraftInteraction, 0, 0);
            return false;
        }
        _waiting = true;
        _stepAt = Environment.TickCount64;
        return true;
    }

    private void Wait(long nowMs)
    {
        if (Host.TryGetLastConfirmation(out int seq, out uint type, out int pct, out int outcome, out _)
            && seq != _confSeq && type == RynthCoreHost.ConfirmationKind.CraftInteraction)
        {
            _confSeq = seq;
            if (outcome == RynthCoreHost.ConfirmationOutcome.Declined) { Stop("the craft dialog was answered No"); return; }
            if (outcome is RynthCoreHost.ConfirmationOutcome.Shown or RynthCoreHost.ConfirmationOutcome.SendFailed)
            {
                _manual = true;
                _status = $"click Yes in AC's craft dialog ({(pct >= 0 ? pct + "%" : "chance not shown")})";
            }
        }

        if (Host.TryGetLastWeenieError(out int es, out uint error, out _, out _) && es != _errSeq)
        {
            _errSeq = es;
            // Busy: the server did nothing, so the same combine can go again.
            if (error == ErrorYoureTooBusy && ++_busyRetries <= MaxBusyRetries)
            {
                _waiting = false;
                _nextAt = nowMs + RetryDelayMs;
                _status = $"server says busy - trying again ({_busyRetries}/{MaxBusyRetries})";
                return;
            }
            Stop($"the server refused it (error 0x{error:X})");
            return;
        }

        if (Done()) { Next(nowMs); return; }

        if (nowMs - _stepAt > (_manual ? ManualTimeoutMs : StepTimeoutMs))
            Stop(_manual ? "no answer to AC's dialog - stopped" : "no result seen - stopped (check the pack before going on)");
    }

    /// <summary>The current combine or inscription went through.</summary>
    private bool Done()
    {
        if (_mode == Mode.Craft)
        {
            var r = _queue[_index];
            return Count(r.Tool) < _toolBefore || Count(r.Target) < _targetBefore || OutputCount(r) > _outputBefore;
        }
        var scroll = Inv.Cache?[_pairs[_index].Scroll];
        return scroll == null || Inv.StackSize(scroll) < _scrollStackBefore;
    }

    private void Next(long nowMs)
    {
        _waiting = false;
        _busyRetries = 0;
        _crafted++;
        _index++;
        _nextAt = nowMs + StepGapMs;
    }

    /// <summary>"cannot be used on" ends the job: the names didn't match what the server expects.</summary>
    public void OnChat(string text)
    {
        if (!_waiting || string.IsNullOrEmpty(text)) return;
        if (CannotUse.IsMatch(text)) Stop(text);
    }
}
