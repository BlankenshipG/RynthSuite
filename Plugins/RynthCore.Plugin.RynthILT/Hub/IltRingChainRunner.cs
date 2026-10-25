// IltRingChainRunner.cs - Crafts Infinite Leaftide's ring inscription chain and applies the scrolls
// (Gear tab > Rings). SilentKelpie's RynthAi RingChainRunner (MiniRemote/Crafting/), moved to
// RynthILT 2026-10-15 and run on the ILT Hub's context; its settings are the Gear state's
// (IltGearState.RingRuns, RingAnswerCraftDialog, RingAllowInCombat), saved with the Hub.
//
// Tick runs on the pump thread; Render on the render thread reads the volatile snapshots and
// posts the commands (IltHubContext.Post / Confirm).
// Crafting: the queue from IltRingChain.Expand, one combine at a time:
//   1. gates: peace mode (unless allowed in combat for servers with combat crafting), not busy,
//      no other ILT pack job (the split-arrow imbuer);
//   2. find the tool and the target by name; a later step's ingredients come out of the earlier
//      combine, so a missing one is looked for again for a while (inventory lag) before giving up;
//   3. arm the engine's confirmation answer (API v85; Yes: the player queued it) and use tool on
//      target; without v85 (or with the answer off) the player clicks Yes in AC's dialog;
//   4. the combine is done when the tool or the target stack shrinks or the output shows up.
//      No sign of it stops the job instead of retrying, so nothing is crafted twice.
// Scrolls: each "Inscription of ..." scroll is used on its target (the item selected in AC, or
// the carried jewelry paired the UB-IT way) and is done when it leaves the pack.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ImGuiNET;
using RynthCore.Loot;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthILT.Hub;

/// <summary>One line of the Rings scan: a recipe header (Found null) or an ingredient.</summary>
/// <param name="State">0 found, 1 comes from an earlier step, 2 missing.</param>
internal sealed record IltRingScanLine(string Text, string? Found, int State);

internal sealed class IltRingChainRunner : IIltFeature
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
    private const long RunsViewMs = 1000;

    private static readonly Regex CannotUse = new(@"^The .+ cannot be used on the .+\.$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private enum Mode { Idle, Craft, Apply }

    private readonly IltHubContext _ctx;
    /// <summary>True while another ILT job (the split-arrow imbuer) is using the pack.</summary>
    private readonly Func<bool> _blocked;

    // Job (pump thread)
    private Mode _mode;
    private List<IltRingRecipe> _queue = new();
    private List<(int Scroll, int Target)> _pairs = new();
    private int _index, _crafted, _lookTries, _busyRetries, _confSeq, _errSeq;
    private bool _waiting, _manual;
    private long _stepAt, _nextAt, _runsViewAt;
    // Counts before the current combine (craft) / the scroll's stack (apply).
    private long _toolBefore, _targetBefore, _outputBefore;
    private int _scrollStackBefore;

    // Render snapshots
    private volatile string _status = "idle";
    private volatile IltRingScanLine[] _scan = Array.Empty<IltRingScanLine>();
    private volatile (int Id, string Name)[] _scrolls = Array.Empty<(int, string)>();
    private volatile (int Id, string Name)[] _jewelry = Array.Empty<(int, string)>();
    // Runs per recipe (IltRingChain.Recipes order), copied from the Gear state on the pump.
    private volatile int[] _runsView = new int[IltRingChain.Recipes.Length];
    private volatile bool _running;

    public IltRingChainRunner(IltHubContext ctx, Func<bool> blocked)
    {
        _ctx = ctx;
        _blocked = blocked;
    }

    public bool Running => _running;
    public string Status => _status;
    public IltRingScanLine[] ScanLines => _scan;
    /// <summary>The inscription scrolls the last scan found.</summary>
    public (int Id, string Name)[] Scrolls => _scrolls;

    private RynthCoreHost Host => _ctx.Host;
    private IltInventory Inv => _ctx.Inventory;
    private IltGearState G => _ctx.State.Gear;

    /// <summary>Pump thread: the saved run count for a recipe.</summary>
    private int Runs(string id) => G.RingRuns.TryGetValue(id, out int n) ? n : 0;

    /// <summary>Pump thread: sets a recipe's run count (0 removes it) and the render copy.</summary>
    public void SetRuns(string id, int runs)
    {
        runs = Math.Clamp(runs, 0, IltRingChain.MaxRuns);
        if (runs == 0) G.RingRuns.Remove(id);
        else G.RingRuns[id] = runs;
        PublishRuns();
    }

    private void PublishRuns()
    {
        var view = new int[IltRingChain.Recipes.Length];
        for (int i = 0; i < view.Length; i++) view[i] = Runs(IltRingChain.Recipes[i].Id);
        _runsView = view;
    }

    // ── Inventory ───────────────────────────────────────────────────────────

    private IEnumerable<WorldObject> Carried() => Inv.Items().Where(w => !Inv.IsEquipped(w));

    private WorldObject? Find(IltRingIngredient ing) => Carried().FirstOrDefault(w => IltRingChain.Matches(w.Name, ing));

    private long Count(IltRingIngredient ing) => Carried().Where(w => IltRingChain.Matches(w.Name, ing)).Sum(w => (long)Inv.StackSize(w));

    private long ScrollCount() => Carried().Where(w => IltRingChain.IsInscriptionScroll(w.Name)).Sum(w => (long)Inv.StackSize(w));

    /// <summary>What a recipe makes, counted: Step 1 the enlightened quill, Step 2 the partition quill, Step 3 a scroll.</summary>
    private long OutputCount(IltRingRecipe r) => r.Tier switch
    {
        1 => Count(IltRingChain.Recipes[1].Tool),
        2 => Count(IltRingChain.Recipes[2].Tool),
        _ => ScrollCount(),
    };

    // ── Scan ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists every queued recipe's ingredients, marking the ones a later step only gets from an
    /// earlier combine (UB-IT's "(after Step 1)"), the carried inscription scrolls and the carried
    /// jewelry they can go on. Pump thread.
    /// </summary>
    public void Scan()
    {
        var lines = new List<IltRingScanLine>();
        bool Stocked(int tier) => IltRingChain.Recipes
            .Where(r => r.Tier == tier && Runs(r.Id) > 0)
            .All(r => Find(r.Tool) != null && Find(r.Target) != null);
        bool tier1 = Stocked(1), tier2 = Stocked(2);
        bool any = false, blocking = false;
        foreach (var r in IltRingChain.Recipes)
        {
            int runs = Runs(r.Id);
            if (runs <= 0) continue;
            any = true;
            lines.Add(new IltRingScanLine($"{r.Name} x{runs}", null, 0));
            foreach (var ing in new[] { r.Tool, r.Target })
            {
                var found = Find(ing);
                if (found != null) { lines.Add(new IltRingScanLine(ing.Label, $"{found.Name} ({Count(ing)})", 0)); continue; }
                string? later = r.Tier == 2 && !tier1 ? "(after Step 1)"
                    : r.Tier == 3 && !tier1 ? "(after Step 1)"
                    : r.Tier == 3 && !tier2 ? "(after Step 2)"
                    : null;
                if (later == null) blocking = true;
                lines.Add(new IltRingScanLine(ing.Label, later ?? "(not found)", later == null ? 2 : 1));
            }
        }
        _scan = lines.ToArray();
        RefreshScrolls();
        _status = !any ? "set how many times to craft at least one recipe"
            : blocking ? "missing ingredients are marked (not found)"
            : "ready: Create runs every Step 1, then Step 2, then Step 3";
    }

    private void RefreshScrolls()
    {
        _scrolls = Carried().Where(w => IltRingChain.IsInscriptionScroll(w.Name))
            .Select(w => (w.Id, w.Name ?? string.Empty))
            .OrderBy(s => IltRingChain.ScrollOrderKey(s.Item2), StringComparer.Ordinal)
            .ToArray();
        _jewelry = Carried().Where(w => w.ObjectClass == AcObjectClass.Jewelry)
            .Select(w => (w.Id, w.Name ?? string.Empty))
            .OrderBy(j => j.Id)
            .ToArray();
    }

    // ── Job control (pump thread) ───────────────────────────────────────────

    /// <summary>Crafts the queued recipes (after the tab's confirm).</summary>
    public void Create()
    {
        if (_running) return;
        if (_blocked()) { _status = "the split-arrow imbuer is running"; return; }
        var queue = IltRingChain.Expand(Runs);
        if (queue.Count == 0) { _status = "set how many times to craft at least one recipe"; return; }
        Begin(Mode.Craft);
        _queue = queue;
        _status = $"crafting {queue.Count} combine(s)";
        RynthLog.Write(LogCat.IltGear, $"[Rings] create, {queue.Count} combine(s)");
    }

    /// <summary>Uses one scroll on the item selected in AC.</summary>
    public void ApplyToSelected(int scrollId)
    {
        uint sel = Host.GetSelectedItemId();
        if (sel == 0) { _status = "select the ring or item in AC first"; return; }
        if (unchecked((int)sel) == scrollId) { _status = "the scroll itself is selected: select the ring or item"; return; }
        ApplyPairs(new List<(int, int)> { (scrollId, unchecked((int)sel)) });
    }

    /// <summary>Pairs the carried scrolls with these items (the carried jewelry) and uses them.</summary>
    public void ApplyToItems(IReadOnlyList<int> targetIds)
    {
        RefreshScrolls();
        if (_scrolls.Length == 0) { _status = "no inscription scroll in the pack (name has \"Inscription of\")"; return; }
        if (targetIds.Count == 0) { _status = "no jewelry carried (worn items aren't inscribed)"; return; }
        ApplyPairs(IltRingChain.PairScrolls(_scrolls, targetIds));
    }

    private void ApplyPairs(List<(int Scroll, int Target)> pairs)
    {
        if (_running) return;
        if (_blocked()) { _status = "the split-arrow imbuer is running"; return; }
        if (pairs.Count == 0) { _status = "nothing to apply"; return; }
        Begin(Mode.Apply);
        _pairs = pairs;
        _status = $"applying {pairs.Count} inscription(s)";
        RynthLog.Write(LogCat.IltGear, $"[Rings] apply {pairs.Count} inscription(s)");
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
        _queue = new List<IltRingRecipe>();
        _pairs = new List<(int, int)>();
        _status = why;
    }

    // ── IIltFeature ─────────────────────────────────────────────────────────

    public void OnLogout()
    {
        Stop("idle");
        _scan = Array.Empty<IltRingScanLine>();
        _scrolls = Array.Empty<(int, string)>();
        _jewelry = Array.Empty<(int, string)>();
    }

    public void Tick(long nowMs)
    {
        if (nowMs - _runsViewAt >= RunsViewMs)
        {
            // A profile load replaces the Gear state: follow it.
            _runsViewAt = nowMs;
            PublishRuns();
        }
        if (_mode == Mode.Idle) return;
        if (_waiting) { Wait(nowMs); return; }
        if (nowMs < _nextAt) return;
        if (!G.RingAllowInCombat && !Inv.InPeaceMode) { _status = "waiting for peace mode"; return; }
        if (Inv.IsBusy) return;

        if (_mode == Mode.Craft) NextCombine(nowMs);
        else NextScroll(nowMs);
    }

    private void NextCombine(long nowMs)
    {
        if (_index >= _queue.Count)
        {
            Stop($"done - {_crafted} combine(s)");
            RefreshScrolls();
            RynthLog.Write(LogCat.IltGear, $"[Rings] done, {_crafted} combine(s)");
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
        _manual = !(G.RingAnswerCraftDialog && Host.HasConfirmationAnswers
                    && Host.ArmConfirmation(RynthCoreHost.ConfirmationKind.CraftInteraction, 0, (int)StepTimeoutMs) == 1);
        _confSeq = Host.TryGetLastConfirmation(out int cs, out _, out _, out _, out _) ? cs : 0;
        _errSeq = Host.TryGetLastWeenieError(out int es, out _, out _, out _) ? es : 0;
        if (!Host.UseOnFor(unchecked((uint)source.Id), unchecked((uint)target.Id), "IltRings", why, UseKind.Asked))
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

    /// <summary>"cannot be used on" ends the job: the names didn't match what the server expects. Never eaten.</summary>
    public bool OnChat(string text)
    {
        if (!_waiting || string.IsNullOrEmpty(text)) return false;
        if (CannotUse.IsMatch(text)) Stop(text);
        return false;
    }

    // ── UI (render thread) ──────────────────────────────────────────────────

    /// <summary>The ring inscription chain: runs per recipe, Scan / Create / Stop, then the scrolls.</summary>
    public void Render()
    {
        ImGui.TextDisabled("Quill of Infliction -> Enlightened Quill -> partition quill -> glyph -> Inscription scroll -> ring.");
        int[] runsView = _runsView;
        if (ImGui.BeginTable("##ringrecipes", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Runs", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Recipe");
            for (int i = 0; i < IltRingChain.Recipes.Length; i++)
            {
                var r = IltRingChain.Recipes[i];
                ImGui.PushID(r.Id);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                int n = i < runsView.Length ? runsView[i] : 0;
                ImGui.SetNextItemWidth(90);
                if (ImGui.InputInt("##runs", ref n, 1))
                {
                    string id = r.Id;
                    int runs = Math.Clamp(n, 0, IltRingChain.MaxRuns);
                    var shown = (int[])runsView.Clone();
                    shown[i] = runs;
                    _runsView = shown;   // shown at once; the pump saves it and republishes
                    _ctx.Post(() => SetRuns(id, runs));
                }
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(r.Name);
                if (ImGui.IsItemHovered()) SafeImGui.SetTooltip(r.Notes);
                ImGui.PopID();
            }
            ImGui.EndTable();
        }

        bool answer = G.RingAnswerCraftDialog, combat = G.RingAllowInCombat;
        if (ImGui.Checkbox("Answer the craft dialog##rings", ref answer)) G.RingAnswerCraftDialog = answer;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Answers AC's craft confirmation Yes for each combine you queued (needs RynthCore plugin API v85).\nOff, or on an older RynthCore: click Yes in AC's dialog yourself.");
        ImGui.SameLine();
        if (ImGui.Checkbox("Allow in combat mode##rings", ref combat)) G.RingAllowInCombat = combat;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Only for servers that allow crafting in combat mode.");

        bool running = Running;
        bool blocked = _blocked();
        ImGui.BeginDisabled(running);
        if (ImGui.Button("Scan##rings")) _ctx.Post(Scan);
        ImGui.SameLine();
        ImGui.BeginDisabled(blocked);
        if (ImGui.Button("Create##rings"))
        {
            int[] view = _runsView;
            int total = IltRingChain.Expand(id =>
            {
                int k = Array.FindIndex(IltRingChain.Recipes, x => x.Id == id);
                return k >= 0 && k < view.Length ? view[k] : 0;
            }).Count;
            _ctx.Confirm("Rings chain", $"Craft {total} combine(s)?\n\nEvery Step 1 run goes first, then Step 2, then Step 3.\nThe ingredients are used up.",
                Create, "Create");
        }
        ImGui.EndDisabled();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!running);
        if (ImGui.Button("Stop##rings")) _ctx.Post(() => Stop("stopped by player"));
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (blocked && !running) ImGui.TextColored(IltStyle.ColAmber, "the split-arrow imbuer is running");
        else SafeImGui.TextDisabled(Status);

        var lines = ScanLines;
        if (lines.Length > 0 && ImGui.BeginTable("##ringscan", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
        {
            foreach (var l in lines)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (l.Found == null) { SafeImGui.TextColored(IltStyle.ColTeal, l.Text); continue; }
                ImGui.TextUnformatted("  " + l.Text);
                ImGui.TableNextColumn();
                if (l.State == 0) ImGui.TextUnformatted(l.Found);
                else if (l.State == 1) SafeImGui.TextDisabled(l.Found);
                else SafeImGui.TextColored(IltStyle.ColAmber, l.Found);
            }
            ImGui.EndTable();
        }

        ImGui.Separator();
        RenderScrolls(running || blocked);
    }

    /// <summary>The carried inscription scrolls: one to the selected item, or all to the carried jewelry.</summary>
    private void RenderScrolls(bool busy)
    {
        ImGui.TextColored(IltStyle.ColTeal, "Inscription scrolls");
        var scrolls = Scrolls;
        if (scrolls.Length == 0) { ImGui.TextDisabled("None carried (Scan to look again)."); return; }

        ImGui.BeginDisabled(busy);
        foreach (var (id, name) in scrolls)
        {
            ImGui.PushID(id);
            if (ImGui.SmallButton("Apply to selected"))
            {
                int scrollId = id;
                string scrollName = name;
                _ctx.Confirm("Ring inscription", $"Use {scrollName} on the item selected in AC?",
                    () => ApplyToSelected(scrollId), "Apply");
            }
            ImGui.SameLine();
            ImGui.TextUnformatted(name);
            ImGui.PopID();
        }

        // UB-IT pairs the scrolls with a scan's items; here the carried (not worn) jewelry.
        var jewelry = _jewelry;
        var targets = jewelry.Select(j => j.Id).ToList();
        ImGui.BeginDisabled(targets.Count == 0);
        if (ImGui.Button($"Apply all to the carried jewelry ({targets.Count})"))
        {
            int pairs = Math.Min(targets.Count, scrolls.Length);
            _ctx.Confirm("Ring inscriptions", $"Use {pairs} inscription scroll(s) on the carried jewelry, one each?",
                () => ApplyToItems(targets), "Apply");
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            SafeImGui.SetTooltip("Scrolls in glyph order go on the jewelry in your pack (not worn) in id order (UB-IT's pairing).\n"
                                 + (jewelry.Length == 0 ? "No jewelry carried (Scan to look again)."
                                    : string.Join("\n", jewelry.Select(j => j.Name))));
        ImGui.EndDisabled();
    }
}
