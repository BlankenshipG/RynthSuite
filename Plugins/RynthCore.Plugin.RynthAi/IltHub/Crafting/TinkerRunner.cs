// TinkerRunner.cs - Scans one Crafting tab's items, plans them and applies the salvage.
//
// Pump thread only (the tab's Render reads the volatile snapshots and posts Scan/Start/Stop).
// One step at a time:
//   1. gates: peace mode (unless the server allows combat crafting), not busy, ActionPacer;
//   2. re-plan the item from fresh facts (TinkerPlanner) minus what this run already tried;
//   3. pick the bag (TinkerPlanner.SelectBag over TinkerMath's retail chance, Foolproof = 100%);
//   4. arm the engine's confirmation answer (v81: Yes only at the tab's minimum percent) and
//      UseObjectOn(bag, item);
//   5. wait for the result: the craft chat line, the bag leaving the pack, a declined
//      confirmation, "cannot be used on", or a WeenieError (YoureTooBusy retries).
// Without the v81 engine (or with dialog answering off) AC shows its own dialog and the player
// clicks it; the runner waits longer for that.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RynthCore.Loot;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.IltHub.Crafting;

/// <summary>One scanned item as the tab shows it.</summary>
internal sealed class TinkerRow
{
    public int Id;
    public string Name = string.Empty;
    public int Tinks;
    public double Workmanship;
    public bool Imbued;
    public string Plan = "-";
    /// <summary>Updated by the runner as it works the item.</summary>
    public volatile string Status = "planned";
}

internal sealed class TinkerRunner
{
    private const uint IntWorkmanship = 105;
    private const uint IntNumItemsInMaterial = 170;
    private const uint IntDamageType = 45;
    private const uint IntItemMaxMana = 108;
    private const uint BoolRetained = 91;
    private const uint ErrorYoureTooBusy = 0x1D;
    private const long StepTimeoutMs = 15_000;
    /// <summary>The player answers AC's own dialog: give them time.</summary>
    private const long ManualTimeoutMs = 60_000;
    private const long RetryDelayMs = 1_500;
    private const long StepGapMs = 600;
    private const int MaxRetries = 3;
    /// <summary>Appraisal requests one scan may send (the Items tab sees the whole pack).</summary>
    private const int MaxAppraisalsPerScan = 25;

    private static readonly Regex Applied = new(
        @"^(?<character>[\w\s]+) successfully applies the (?<salvage>[\w\s\-]+?)(\sSalvage.*)(\s?\(100\))?\s\(workmanship (?<workmanship>\d+\.\d+)\) to the (?<item>[\w\s\'\-]+)\.$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Failed = new(
        @"^(?<character>[\w\s]+) fails to apply the (?<salvage>[\w\s\-]+?)(\sSalvage.*)(\s?\(100\))?\s\(workmanship (?<workmanship>\d+\.\d+)\) to the (?<item>[\w\s\'\-]+)\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Foolproof = new(@"^You apply the (?<salvage>.*)\.$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CannotUse = new(@"^The .+ cannot be used on the .+\.$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IltHubContext _ctx;
    private readonly Func<TinkerBench, IReadOnlyList<TinkerMaterial>> _rows;
    private readonly Func<TinkerBench, IltTinkerBenchSettings> _settings;

    // Job (pump thread)
    private TinkerBench _bench;
    private readonly Queue<int> _queue = new();
    private int _itemId;
    private readonly Dictionary<string, int> _attempted = new();
    private readonly HashSet<string> _skipped = new();
    private bool _waiting;
    private TinkerStep _step;
    private int _bagId;
    private string _bagName = string.Empty;
    private int _confSeq, _errSeq, _retries, _stepPercent;
    private bool _manual;
    private long _stepAt, _nextAt;
    private Dictionary<int, TinkerRow> _byId = new();

    // Render snapshots
    private volatile TinkerRow[] _scan = Array.Empty<TinkerRow>();
    private volatile string _status = "idle";
    private volatile int _scannedBench = -1;

    public TinkerRunner(IltHubContext ctx, Func<TinkerBench, IReadOnlyList<TinkerMaterial>> rows,
                        Func<TinkerBench, IltTinkerBenchSettings> settings)
    {
        _ctx = ctx;
        _rows = rows;
        _settings = settings;
    }

    public bool Running => _itemId != 0 || _queue.Count > 0;
    public TinkerBench Bench => _bench;
    public string Status => _status;
    /// <summary>The last scan's rows (for <see cref="ScannedBench"/>).</summary>
    public TinkerRow[] Rows => _scan;
    public int ScannedBench => _scannedBench;

    private RynthCoreHost Host => _ctx.Host;
    private IltInventory Inv => _ctx.Inventory;
    private IltCraftingState C => _ctx.State.Crafting;

    // ── Scan ────────────────────────────────────────────────────────────────

    /// <summary>Reads what the planner needs from an item.</summary>
    public TinkerItemFacts Facts(WorldObject wo) => new()
    {
        Id = wo.Id,
        Name = wo.Name ?? string.Empty,
        Class = wo.ObjectClass,
        Workmanship = Inv.Int(wo, IntWorkmanship),
        Tinks = Inv.Int(wo, IltInventory.IntNumTimesTinkered),
        Imbued = Inv.Int(wo, IltInventory.IntImbuedEffect) != 0,
        DamageType = Inv.Int(wo, IntDamageType),
        Retained = Inv.Bool(wo, BoolRetained),
        MaxMana = Inv.Int(wo, IntItemMaxMana),
        Equipped = Inv.IsEquipped(wo),
        FlagInt = k => Inv.Int(wo, unchecked((uint)k)),
    };

    /// <summary>A thread-safe copy of a bench's settings (the tab edits Counts under its lock).</summary>
    public static IltTinkerBenchSettings Snapshot(IltTinkerBenchSettings s)
    {
        lock (s.Counts)
        {
            return new IltTinkerBenchSettings
            {
                Counts = new Dictionary<string, int>(s.Counts),
                MinChancePercent = s.MinChancePercent,
                MaxTinks = s.MaxTinks,
                MinFreeTinks = s.MinFreeTinks,
                SkipImbued = s.SkipImbued,
                PierceForCombo = s.PierceForCombo,
                RendLast = s.RendLast,
            };
        }
    }

    /// <summary>Scans the pack for a tab: up to BatchSize items that have something to apply.</summary>
    public void Scan(TinkerBench bench)
    {
        if (Running) { _status = "stop the running job before scanning"; return; }
        var s = Snapshot(_settings(bench));
        var rows = _rows(bench);
        int batch = Math.Clamp(C.BatchSize, 1, 100);
        var list = new List<TinkerRow>();
        int appraising = 0;
        foreach (var wo in Inv.Items())
        {
            if (!TinkerCatalog.Targets(bench, wo.ObjectClass)) continue;
            if (bench == TinkerBench.Jewelry && TinkerCatalog.IsTrinket(wo.Name)) continue;
            if (!Inv.HasAppraisal(wo))
            {
                if (appraising < MaxAppraisalsPerScan && Inv.RequestAppraisal(wo)) appraising++;
                continue;
            }
            var f = Facts(wo);
            if (TinkerPlanner.Excluded(f, s).Length > 0) continue;
            var plan = TinkerPlanner.Plan(bench, f, s, rows);
            if (plan.Count == 0) continue;
            list.Add(new TinkerRow
            {
                Id = f.Id, Name = f.Name, Tinks = f.Tinks, Workmanship = f.Workmanship, Imbued = f.Imbued,
                Plan = TinkerPlanner.Summary(plan),
            });
            if (list.Count >= batch) break;
        }
        _scan = list.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        _scannedBench = (int)bench;
        _status = list.Count == 0
            ? (appraising > 0 ? $"nothing to do yet - appraising {appraising} item(s), scan again in a moment" : "nothing to do: no item matches the ticked salvage")
            : $"{list.Count} item(s) planned" + (appraising > 0 ? $" ({appraising} still being appraised)" : "");
    }

    // ── Job control ─────────────────────────────────────────────────────────

    /// <summary>Starts on the last scan's rows (pump thread, after the tab's confirm).</summary>
    public void Start(TinkerBench bench)
    {
        if (Running) return;
        if (_scannedBench != (int)bench || _scan.Length == 0) { _status = "scan first"; return; }
        _bench = bench;
        _queue.Clear();
        _byId = _scan.ToDictionary(r => r.Id);
        foreach (var r in _scan) { _queue.Enqueue(r.Id); r.Status = "queued"; }
        _itemId = 0;
        _waiting = false;
        _nextAt = 0;
        _status = $"working {_queue.Count} item(s)";
        RynthLog.Write(LogCat.IltGear, $"[Crafting] {TinkerCatalog.Label(bench)}: start, {_queue.Count} item(s)");
    }

    public void Stop(string why)
    {
        if (_waiting && Host.HasConfirmationAnswers) Host.ArmConfirmation(RynthCoreHost.ConfirmationKind.CraftInteraction, 0, 0);
        if (_itemId != 0) SetRow(_itemId, "stopped");
        while (_queue.Count > 0) SetRow(_queue.Dequeue(), "not started");
        _itemId = 0;
        _waiting = false;
        _status = why;
    }

    public void OnLogout()
    {
        Stop("idle");
        _scan = Array.Empty<TinkerRow>();
        _scannedBench = -1;
    }

    private void SetRow(int id, string status)
    {
        if (_byId.TryGetValue(id, out var r)) r.Status = status;
    }

    private string AttemptKey(string rowKey) => _itemId + ":" + rowKey;

    private void NextItem(string doneStatus)
    {
        if (_itemId != 0) SetRow(_itemId, doneStatus);
        _itemId = 0;
        _attempted.Clear();
        _skipped.Clear();
    }

    // ── Tick (pump thread) ──────────────────────────────────────────────────

    public void Tick(long nowMs)
    {
        if (!Running) return;
        if (_waiting) { Wait(nowMs); return; }
        if (nowMs < _nextAt) return;

        if (!C.AllowInCombat && !Inv.InPeaceMode) { _status = "waiting for peace mode"; return; }
        if (Inv.IsBusy) return;
        if (!ActionPacer.MayStart(Host, "Crafting", HealNeed.None, out string why)) { _status = "waiting: " + why; return; }

        if (_itemId == 0)
        {
            if (_queue.Count == 0) { Stop("done"); return; }
            _itemId = _queue.Dequeue();
            _retries = 0;
        }

        var wo = Inv.Cache?[_itemId];
        if (wo == null) { NextItem("gone from the pack"); return; }
        if (Inv.IsEquipped(wo)) { NextItem("equipped - skipped"); return; }

        var s = Snapshot(_settings(_bench));
        var facts = Facts(wo);
        var plan = TinkerPlanner.Plan(_bench, facts, s, _rows(_bench),
                k => _attempted.TryGetValue(k, out int n) ? n : 0)
            .Where(st => !_skipped.Contains(TinkerCatalog.Key(st.Row)))
            .ToList();
        if (plan.Count == 0) { NextItem($"done ({facts.Tinks} tinks)"); return; }

        var step = plan[0];
        string key = TinkerCatalog.Key(step.Row);
        string matName = TinkerCatalog.MaterialName(step.Material);

        var bags = Inv.Items()
            .Where(b => b.ObjectClass == AcObjectClass.Salvage
                        && TinkerPlanner.BagMatches(step, b.Name, Inv.Int(b, IltInventory.IntMaterialType), Inv.Uses(b)))
            .ToList();
        if (bags.Count == 0) { Skip(key, $"no full bag of {matName}"); return; }

        int skill = Skill(step.Row.Skill > 0 ? step.Row.Skill : TinkerCatalog.DefaultSkill(_bench));
        var choices = bags.Select(b =>
        {
            double bagWork = TinkerMath.BagWorkmanship(Inv.Int(b, IntWorkmanship), Inv.Int(b, IntNumItemsInMaterial, 1));
            bool fool = TinkerPlanner.IsFoolproof(b.Name);
            double? chance = fool ? 1.0
                : step.Row.Origin == TinkerOrigin.Retail && skill > 0
                    ? TinkerMath.Chance(step.Material, skill, facts.Workmanship, bagWork, facts.Tinks,
                                        step.Row.Kind == TinkerKind.Imbue, false)
                    : null;
            return new TinkerBagChoice(b.Id, bagWork, chance);
        }).ToList();

        int min = Math.Clamp(s.MinChancePercent, 0, 100);
        int pick = TinkerPlanner.SelectBag(choices, min);
        if (pick < 0)
        {
            int best = choices.Max(ch => TinkerMath.Percent(ch.Chance ?? 0));
            Skip(key, $"{matName}: best chance {best}% is under {min}%");
            return;
        }

        var bag = bags[pick];
        _stepPercent = choices[pick].Chance is double pc ? TinkerMath.Percent(pc) : -1;
        _manual = !(C.AnswerCraftDialog && Host.HasConfirmationAnswers
                    && Host.ArmConfirmation(RynthCoreHost.ConfirmationKind.CraftInteraction, min, (int)StepTimeoutMs) == 1);
        _confSeq = Host.TryGetLastConfirmation(out int cs, out _, out _, out _, out _) ? cs : 0;
        _errSeq = Host.TryGetLastWeenieError(out int es, out _, out _, out _) ? es : 0;

        string pctText = _stepPercent >= 0 ? $" ~{_stepPercent}%" : "";
        if (!Host.UseOnFor(unchecked((uint)bag.Id), unchecked((uint)_itemId), "Crafting",
                $"{TinkerCatalog.Label(_bench)}: {matName} on {facts.Name}{pctText}", UseKind.Asked))
        {
            if (!_manual) Host.ArmConfirmation(RynthCoreHost.ConfirmationKind.CraftInteraction, 0, 0);
            Retry(nowMs, "the use request didn't go out");
            return;
        }

        _step = step;
        _bagId = bag.Id;
        _bagName = bag.Name ?? matName;
        _waiting = true;
        _stepAt = nowMs;
        _attempted[key] = (_attempted.TryGetValue(key, out int n0) ? n0 : 0) + 1;
        string ask = _manual ? " - click Yes in AC's craft dialog" : "";
        SetRow(_itemId, $"applying {matName}{pctText}{ask}");
        _status = $"{facts.Name}: applying {_bagName}{pctText}{ask}";
    }

    /// <summary>Buffed tinkering skill (0 when the engine can't read it: the server's percent decides).</summary>
    private int Skill(int skillId)
        => Host.HasGetObjectSkill && Host.TryGetObjectSkill(Inv.PlayerId, unchecked((uint)skillId), out int buffed, out _) ? buffed : 0;

    private void Skip(string rowKey, string why)
    {
        _skipped.Add(rowKey);
        SetRow(_itemId, why);
        _status = why;
    }

    private void Retry(long nowMs, string why)
    {
        _waiting = false;
        string key = TinkerCatalog.Key(_step.Row);
        if (_attempted.TryGetValue(key, out int n) && n > 0) _attempted[key] = n - 1;
        if (++_retries > MaxRetries) { NextItem("gave up: " + why); _status = why; return; }
        _nextAt = nowMs + RetryDelayMs;
        _status = $"{why} - retrying ({_retries}/{MaxRetries})";
    }

    private void Finish(string outcome)
    {
        _waiting = false;
        _retries = 0;
        _nextAt = Environment.TickCount64 + StepGapMs;
        SetRow(_itemId, outcome);
        _status = outcome;
    }

    private void Wait(long nowMs)
    {
        // Engine answer (v81): a No ends the step without using the bag.
        if (Host.TryGetLastConfirmation(out int seq, out uint type, out int pct, out int outcome, out _)
            && seq != _confSeq && type == RynthCoreHost.ConfirmationKind.CraftInteraction)
        {
            _confSeq = seq;
            if (outcome == RynthCoreHost.ConfirmationOutcome.Declined)
            {
                _waiting = false;
                Skip(TinkerCatalog.Key(_step.Row), $"{TinkerCatalog.MaterialName(_step.Material)}: server chance {pct}% is under the minimum");
                return;
            }
            if (outcome is RynthCoreHost.ConfirmationOutcome.Shown or RynthCoreHost.ConfirmationOutcome.SendFailed)
            {
                _manual = true;
                _status = $"click Yes in AC's craft dialog ({(pct >= 0 ? pct + "%" : "chance not shown")})";
            }
        }

        if (Host.TryGetLastWeenieError(out int es, out uint error, out _, out _) && es != _errSeq)
        {
            _errSeq = es;
            if (error == ErrorYoureTooBusy) { Retry(nowMs, "server says busy"); return; }
            _waiting = false;
            Skip(TinkerCatalog.Key(_step.Row), $"server refused {TinkerCatalog.MaterialName(_step.Material)} (error 0x{error:X})");
            return;
        }

        // Every tinker consumes the bag, success or failure: covers server recipes with their own text.
        if (Inv.Cache?[_bagId] == null) { Finish($"{_bagName} used"); return; }

        if (nowMs - _stepAt > (_manual ? ManualTimeoutMs : StepTimeoutMs))
            Retry(nowMs, _manual ? "no answer to AC's craft dialog" : "no result from the server");
    }

    /// <summary>The craft result lines (never hidden).</summary>
    public void OnChat(string text)
    {
        if (!_waiting || string.IsNullOrEmpty(text)) return;
        string mat = TinkerCatalog.MaterialName(_step.Material);
        if (Applied.IsMatch(text)) Finish($"{mat} applied");
        else if (Failed.IsMatch(text)) Finish($"{mat} failed");
        else if (Foolproof.IsMatch(text)) Finish($"{mat} applied (foolproof)");
        else if (CannotUse.IsMatch(text))
        {
            _waiting = false;
            Skip(TinkerCatalog.Key(_step.Row), $"{mat} can't be used on this item");
        }
    }
}
