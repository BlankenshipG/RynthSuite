// IltAttributeRaiser.cs — Infinite attributes: spend unassigned XP with the server's /attr.
//
// ACECustom (Infinite Leaftide) lets attributes and vitals go past the retail cap, so the
// client's XP tables stop applying. The server is the only source of truth:
//   "/xp all"         → "[XP] Your XP cost for next 1 Strength level is: 1,234" per stat
//   "/attr <abbr> <n>" → "[ATTR] Strength attribute raised by 1, costing 1,234!"
// (Same commands UB's Leaftide XP Calculator drives; it raises one level at a time and
// re-reads the costs after each confirmation, which is what this does too.)
//
// Raise now / auto-raise run one loop: read costs → pick a stat → /attr <abbr> 1 → wait for
// the [ATTR] reply → read costs again → ... until nothing ticked is affordable, a reply is
// not a success, or MaxRaisesPerRun. Only the ticked stats are ever spent on, never below
// the reserve (AttrKeepXp). Which stat comes next depends on the raise mode:
//   Priority    the first ticked stat in the order that is affordable (fills the top stat)
//   RoundRobin  one level per ticked stat in order, then around again
//   Cheapest    the ticked stat with the lowest next-level cost
// Manual +N buttons send "/attr <abbr> N" straight to the server (its reply shows in chat).
//
// Gated on ServerFeatureGate (via IltServerOptions.IsIltLikeWorld) and on the server not
// reporting /xp off. Everything here runs on the plugin pump thread.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthAi.IltHub;

internal sealed class IltAttributeRaiser
{
    /// <summary>Raise modes (IltCharacterState.AttrRaiseMode).</summary>
    public enum Mode { Priority = 0, RoundRobin = 1, Cheapest = 2 }

    /// <summary>/attr abbreviation, display name, vital?</summary>
    public sealed record Stat(string Abbr, string Name, bool Vital);

    public static readonly Stat[] Stats =
    {
        new("str", "Strength", false), new("end", "Endurance", false), new("coo", "Coordination", false),
        new("qui", "Quickness", false), new("foc", "Focus", false), new("sel", "Self", false),
        new("hea", "Health", true), new("sta", "Stamina", true), new("man", "Mana", true),
    };

    /// <summary>UB's default priority (Coordination, Quickness, Strength, Endurance, Focus, Self, vitals).</summary>
    public static readonly string[] DefaultOrder = { "coo", "qui", "str", "end", "foc", "sel", "hea", "sta", "man" };

    public const int MaxRaisesPerRun = 200;
    private const long FirstAutoDelayMs = 30_000;   // after login / arming, before the first auto run
    private const int ReplyTimeoutMs = 8000;
    /// <summary>A run with no reply/step for this long is ended (a lost callback must not wedge it).</summary>
    private const long StallMs = 45_000;

    /// <summary>"Your base Strength is now 250!" / "Your base Maximum Health is now 900!" (older servers / UB wording).</summary>
    private static readonly Regex BaseNow = new(@"Your base (?:Maximum )?(\w+) is now (\d+)!", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IltHubContext _ctx;
    // Last known costs (display, and the passive chat reader). A run decides only from _runCosts:
    // the costs in the "/xp all" reply read right before that step, never an older value.
    private readonly Dictionary<string, long> _cost = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, long> _runCosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _base = new(StringComparer.OrdinalIgnoreCase);
    private long _costsAtMs;

    // Run state. _runId guards against a capture that completes after Stop / a newer run.
    private enum Phase { Idle, ReadingCosts, Raising }
    private Phase _phase;
    private int _runId;
    private bool _runAuto;
    private int _runRaised;
    private long _runSpent;
    private readonly Dictionary<string, int> _runGains = new(StringComparer.OrdinalIgnoreCase);
    private int _rrNext;
    private string _raisingAbbr = string.Empty;
    private long _raisingCost;

    private long _lastProgressMs;
    private long _nextAutoAtMs;
    private volatile string _status = "Idle.";
    private volatile string _lastRun = string.Empty;

    public IltAttributeRaiser(IltHubContext ctx) => _ctx = ctx;

    /// <summary>
    /// True while another XP spender runs (IltProgression's spend-before-/enl loop sends /attr
    /// outside the capture queue). Nothing here starts, by hand or on the timer, while it is.
    /// </summary>
    public Func<bool>? HoldOff { get; set; }

    private bool HeldOff
    {
        get { try { return HoldOff?.Invoke() == true; } catch { return false; } }
    }

    private IltCharacterState C => _ctx.State.Character;

    public bool Running => _phase != Phase.Idle;

    // ── Order / selection helpers (also used by IltHubStore.Normalize) ─────────

    public static Stat? Find(string keyOrName)
    {
        if (string.IsNullOrWhiteSpace(keyOrName)) return null;
        string k = keyOrName.Trim();
        foreach (var s in Stats)
            if (s.Abbr.Equals(k, StringComparison.OrdinalIgnoreCase) || s.Name.Equals(k, StringComparison.OrdinalIgnoreCase))
                return s;
        // Cost lines may say "MaxHealth" / "Maximum Health"; the reply may abbreviate.
        string lower = k.ToLowerInvariant().Replace(" ", "");
        if (lower.StartsWith("maximum")) lower = lower.Substring(7);
        else if (lower.StartsWith("max")) lower = lower.Substring(3);
        foreach (var s in Stats)
            if (s.Name.Equals(lower, StringComparison.OrdinalIgnoreCase) || lower.StartsWith(s.Abbr, StringComparison.Ordinal))
                return s;
        return null;
    }

    /// <summary>All nine abbreviations exactly once: saved order first, missing ones appended, junk dropped.</summary>
    public static List<string> RepairOrder(List<string>? order)
    {
        var result = new List<string>();
        foreach (string k in order ?? new List<string>())
        {
            Stat? s = Find(k);
            if (s != null && !result.Contains(s.Abbr)) result.Add(s.Abbr);
        }
        foreach (string k in DefaultOrder)
            if (!result.Contains(k)) result.Add(k);
        return result;
    }

    public static List<string> RepairSelection(List<string>? selection)
    {
        var result = new List<string>();
        foreach (string k in selection ?? new List<string>())
        {
            Stat? s = Find(k);
            if (s != null && !result.Contains(s.Abbr)) result.Add(s.Abbr);
        }
        return result;
    }

    /// <summary>
    /// Picks the next stat to raise. Pure (unit-testable): <paramref name="order"/> is the
    /// priority order, <paramref name="ticked"/> the stats allowed, <paramref name="cost"/> the
    /// known next-level costs, <paramref name="budget"/> what may be spent. Round robin starts at
    /// <paramref name="rrNext"/> and returns the index after the pick there.
    /// </summary>
    public static string? Pick(Mode mode, IReadOnlyList<string> order, ICollection<string> ticked,
                               IReadOnlyDictionary<string, long> cost, long budget, ref int rrNext)
    {
        if (budget <= 0 || order.Count == 0) return null;
        bool Affordable(string k) => ticked.Contains(k) && cost.TryGetValue(k, out long c) && c > 0 && c <= budget;

        switch (mode)
        {
            case Mode.RoundRobin:
                for (int step = 0; step < order.Count; step++)
                {
                    int i = ((rrNext % order.Count) + step) % order.Count;
                    if (!Affordable(order[i])) continue;
                    rrNext = i + 1;
                    return order[i];
                }
                return null;
            case Mode.Cheapest:
                string? best = null;
                long bestCost = long.MaxValue;
                foreach (string k in order)   // ties go to the higher-priority stat
                {
                    if (!Affordable(k)) continue;
                    long c = cost[k];
                    if (c < bestCost) { best = k; bestCost = c; }
                }
                return best;
            default:
                foreach (string k in order)
                    if (Affordable(k)) return k;
                return null;
        }
    }

    // ── Gate ──────────────────────────────────────────────────────────────────

    /// <summary>Why the raiser can't run here, or null when it can.</summary>
    public string? Blocker()
    {
        if (!_ctx.Options.IsIltLikeWorld)
            return "Server features are off for this world (Settings > Misc > Server Features).";
        if (_ctx.Options.IsOff(IltFeature.Xp)) return "This server reports /xp as unavailable.";
        if (!_ctx.Host.HasInvokeChatParser) return "This engine build can't send chat commands.";
        return null;
    }

    // ── Pump-thread lifecycle ─────────────────────────────────────────────────

    /// <summary>Tick. <paramref name="holdOff"/>: another XP spender (auto-enlighten's loop) is running.</summary>
    public void Tick(long nowMs, bool holdOff)
    {
        if (Running && nowMs - _lastProgressMs > StallMs) { Finish($"Stopped: no reply for {StallMs / 1000} s."); return; }
        if (!C.AttrAutoRaise || Running || holdOff || HeldOff) return;
        if (_nextAutoAtMs == 0) { _nextAutoAtMs = nowMs + FirstAutoDelayMs; return; }
        if (nowMs < _nextAutoAtMs) return;
        _nextAutoAtMs = nowMs + Math.Clamp(C.AttrAutoRaiseMinutes, 1, 1440) * 60_000L;
        if (Blocker() != null || C.AttrRaiseStats.Count == 0) return;
        if (_ctx.Inventory.PlayerQuad(IltInventory.QuadAvailableXp) <= Math.Max(0, C.AttrKeepXp)) return;
        Start(auto: true);
    }

    /// <summary>Passive: keeps costs / base values current when the player runs the commands by hand.</summary>
    public void OnChat(string text)
    {
        var cost = IltParse.XpCost.Match(text);
        if (cost.Success)
        {
            Stat? s = Find(cost.Groups[1].Value);
            if (s != null) { _cost[s.Abbr] = IltParse.ParseLeadingLong(cost.Groups[2].Value); _costsAtMs = IltHubContext.NowMs; }
            return;
        }
        var now = BaseNow.Match(text);
        if (now.Success)
        {
            Stat? s = Find(now.Groups[1].Value);
            if (s != null && int.TryParse(now.Groups[2].Value, out int v)) _base[s.Abbr] = v;
        }
    }

    public void OnLogout()
    {
        _runId++;
        _phase = Phase.Idle;
        _cost.Clear();
        _runCosts.Clear();
        _base.Clear();
        _costsAtMs = 0;
        _nextAutoAtMs = 0;
        _status = "Idle.";
    }

    // ── Runs ──────────────────────────────────────────────────────────────────

    /// <summary>Starts a spend run (manual "Raise now" or the auto timer).</summary>
    public void Start(bool auto)
    {
        if (Running) return;
        string? blocked = Blocker();
        if (blocked != null) { _status = blocked; return; }
        if (HeldOff) { _status = "Auto-enlighten is spending XP right now; try again when it finishes."; return; }
        if (C.AttrRaiseStats.Count == 0) { _status = "Tick at least one attribute or vital to raise."; return; }

        _runId++;
        _runAuto = auto;
        _runRaised = 0;
        _runSpent = 0;
        _runGains.Clear();
        _rrNext = 0;
        _runCosts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        _lastProgressMs = IltHubContext.NowMs;
        RynthLog.Trace(LogCat.IltProgression, $"attr raise run {_runId} start (auto={auto}, mode={(Mode)C.AttrRaiseMode}, keep={C.AttrKeepXp}, stats={string.Join(",", C.AttrRaiseStats)})");
        ReadCosts(_runId, thenStep: true);
    }

    public void Stop(string why = "Stopped.")
    {
        if (!Running) return;
        Finish(why);
    }

    /// <summary>Re-reads the costs only (the panel's Refresh button).</summary>
    public void RefreshCosts()
    {
        if (Running || Blocker() != null) return;
        ReadCosts(_runId, thenStep: false);
    }

    /// <summary>"+N" button: sends "/attr abbr N" as-is; the server's reply shows in chat.</summary>
    public void RaiseManual(string key, int levels)
    {
        Stat? s = Find(key);
        if (s == null || levels <= 0) return;
        if (Running) { _status = "Wait for the current raise run to finish (or Stop it)."; return; }
        string? blocked = Blocker();
        if (blocked != null) { _status = blocked; return; }
        if (HeldOff) { _status = "Auto-enlighten is spending XP right now; try again when it finishes."; return; }
        levels = Math.Min(levels, 1000);
        RynthLog.Trace(LogCat.IltProgression, $"attr manual raise {s.Abbr} +{levels}");
        int run = _runId;
        _ctx.Capture.Enqueue(new IltChatRequest
        {
            Command = $"/attr {s.Abbr} {levels}",
            IsResponseLine = IsAttrReply,
            IsTerminator = IsAttrReply,
            IdleEndMs = 600,
            FirstLineTimeoutMs = ReplyTimeoutMs,
            Eat = false,
            OnComplete = r =>
            {
                string reply = r.TerminatorLine ?? r.Lines.FirstOrDefault() ?? (r.TimedOut ? "no reply" : "");
                _status = $"{s.Name} +{levels}: {reply}";
                if (run == _runId && !Running) ReadCosts(_runId, thenStep: false);
            },
        });
        _status = $"Sent /attr {s.Abbr} {levels}...";
    }

    private static bool IsAttrReply(string t)
        => t.StartsWith("[ATTR]", StringComparison.OrdinalIgnoreCase) || t.StartsWith("Your base", StringComparison.OrdinalIgnoreCase);

    private void ReadCosts(int run, bool thenStep)
    {
        // A plain refresh doesn't stack on one already queued; a run always gets its own reply.
        if (!thenStep && _ctx.Capture.IsPending("/xp all")) return;
        if (thenStep)
        {
            _phase = Phase.ReadingCosts;
            if (_runRaised == 0) _status = "Reading XP costs (/xp all)...";
        }
        _ctx.Capture.Enqueue(new IltChatRequest
        {
            Command = "/xp all",
            IsResponseLine = t => t.StartsWith("[XP]", StringComparison.OrdinalIgnoreCase),
            IdleEndMs = 900,
            FirstLineTimeoutMs = ReplyTimeoutMs,
            Eat = true,
            OnComplete = r => Guard(run, () =>
            {
                if (r.UnknownCommand) { _ctx.Options.Set(IltFeature.Xp, IltTri.Off); if (run == _runId && thenStep) Finish("This server has no /xp command."); return; }
                var fresh = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in r.Lines)
                {
                    var m = IltParse.XpCost.Match(line);
                    if (!m.Success) continue;
                    Stat? s = Find(m.Groups[1].Value);
                    if (s == null) continue;
                    fresh[s.Abbr] = IltParse.ParseLeadingLong(m.Groups[2].Value);
                }
                foreach (var kv in fresh) _cost[kv.Key] = kv.Value;
                if (fresh.Count > 0) _costsAtMs = IltHubContext.NowMs;
                RynthLog.Trace(LogCat.IltProgression, $"attr costs: {fresh.Count} line(s) (timedOut={r.TimedOut})");
                if (!thenStep || run != _runId || !Running) return;
                if (fresh.Count == 0) { Finish(r.TimedOut ? "No reply to /xp all." : "No cost lines in the /xp all reply."); return; }
                _runCosts = fresh;   // a stat missing from this reply is not raised this step
                _lastProgressMs = IltHubContext.NowMs;
                Step(run);
            }),
        });
    }

    private void Step(int run)
    {
        if (run != _runId || !Running) return;
        // The world can leave Server Features (or /xp go off) mid-run, and auto-enlighten can start.
        if (Blocker() is string blocked) { Finish("Stopped: " + blocked); return; }
        if (HeldOff) { Finish("Stopped: auto-enlighten started spending XP."); return; }
        if (_runRaised >= MaxRaisesPerRun) { Finish($"Stopped after {MaxRaisesPerRun} raises (runs again later)."); return; }

        long have = _ctx.Inventory.PlayerQuad(IltInventory.QuadAvailableXp);
        long budget = have - Math.Max(0, C.AttrKeepXp);
        var ticked = new HashSet<string>(C.AttrRaiseStats, StringComparer.OrdinalIgnoreCase);
        string? pick = Pick((Mode)Math.Clamp(C.AttrRaiseMode, 0, 2), C.AttrOrder, ticked, _runCosts, budget, ref _rrNext);
        if (pick == null)
        {
            Finish(_runRaised == 0
                ? (budget <= 0 ? "Nothing to spend (unassigned XP is at or below the reserve)."
                               : "Nothing affordable among the ticked stats.")
                : string.Empty);
            return;
        }

        Stat s = Find(pick)!;
        _phase = Phase.Raising;
        _raisingAbbr = s.Abbr;
        _raisingCost = _runCosts.TryGetValue(s.Abbr, out long c) ? c : 0;
        _status = $"Raising {s.Name} +1 (cost {IltParse.Compact(_raisingCost)})...";
        _ctx.Capture.Enqueue(new IltChatRequest
        {
            Command = $"/attr {s.Abbr} 1",
            IsResponseLine = IsAttrReply,
            IsTerminator = IsAttrReply,
            IdleEndMs = 600,
            FirstLineTimeoutMs = ReplyTimeoutMs,
            Eat = true,
            OnComplete = r => Guard(run, () => OnRaiseReply(run, s, r)),
        });
    }

    private void OnRaiseReply(int run, Stat s, IltChatResult r)
    {
        if (run != _runId || !Running) return;
        string reply = r.TerminatorLine ?? r.Lines.FirstOrDefault() ?? string.Empty;
        var ok = IltParse.AttrRaised.Match(reply);
        var baseNow = BaseNow.Match(reply);
        // The reply must be about the stat we raised (another /attr sender could answer first).
        if (ok.Success && Find(ok.Groups[1].Value) != s) ok = Match.Empty;
        if (baseNow.Success && Find(baseNow.Groups[1].Value) != s) baseNow = Match.Empty;
        if (!ok.Success && !baseNow.Success)
        {
            RynthLog.Trace(LogCat.IltProgression, $"attr raise {s.Abbr}: not a success ('{reply}', timedOut={r.TimedOut}, unknown={r.UnknownCommand})");
            Finish(r.UnknownCommand ? "This server has no /attr command."
                 : reply.Length > 0 ? $"Server: {reply}" : "No reply to /attr.");
            return;
        }

        long spent = ok.Success ? IltParse.ParseLeadingLong(ok.Groups[3].Value) : _raisingCost;
        int levels = ok.Success && int.TryParse(ok.Groups[2].Value, out int n) ? n : 1;
        if (baseNow.Success && int.TryParse(baseNow.Groups[2].Value, out int v)) _base[s.Abbr] = v;
        else if (_base.TryGetValue(s.Abbr, out int b)) _base[s.Abbr] = b + levels;
        _runGains[s.Abbr] = (_runGains.TryGetValue(s.Abbr, out int g) ? g : 0) + levels;
        _runRaised += levels;
        _runSpent += spent;
        _cost.Remove(s.Abbr);   // its next level costs more; re-read before deciding again
        _runCosts.Clear();
        _lastProgressMs = IltHubContext.NowMs;
        _status = $"Raised {s.Name} (+{_runRaised} this run, {IltParse.Compact(_runSpent)} XP)...";
        ReadCosts(run, thenStep: true);
    }

    /// <summary>Runs a capture callback; an exception ends the run instead of leaving it Running.</summary>
    private void Guard(int run, Action body)
    {
        try { body(); }
        catch (Exception ex)
        {
            RynthLog.Exception(LogCat.IltProgression, ex, "attr raise callback");
            if (run == _runId && Running) Finish("Stopped: internal error (see the RynthAi log).");
        }
    }

    private void Finish(string reason)
    {
        _phase = Phase.Idle;
        _runId++;   // late callbacks from this run are ignored
        string summary = _runRaised > 0
            ? "Raised " + string.Join(", ", C.AttrOrder.Where(_runGains.ContainsKey).Select(k => $"{Find(k)!.Name} +{_runGains[k]}"))
              + $" ({IltParse.Compact(_runSpent)} XP)."
            : string.Empty;
        string text = (summary + (reason.Length > 0 ? (summary.Length > 0 ? " " : "") + reason : "")).Trim();
        if (text.Length == 0) text = "Done.";
        _status = text;
        _lastRun = $"{DateTime.Now:t} {(_runAuto ? "auto" : "manual")}: {text}";
        RynthLog.Trace(LogCat.IltProgression, $"attr raise run finished: {text}");
        if (_runRaised > 0) _ctx.Chat($"[ILT Hub] Attribute raise ({(_runAuto ? "auto" : "manual")}): {text}");
    }

    // ── Engine Skills panel bridge ────────────────────────────────────────────

    /// <summary>Appends <c>"attr":{...}</c> for the Progression tab.</summary>
    public void AppendJson(StringBuilder sb)
    {
        string? blocked = Blocker();
        long have = _ctx.Inventory.PlayerQuad(IltInventory.QuadAvailableXp);
        long budget = have - Math.Max(0, C.AttrKeepXp);
        long now = IltHubContext.NowMs;

        sb.Append("\"attr\":{\"off\":").Append(blocked != null ? "true" : "false");
        Str(sb, "reason", blocked ?? string.Empty);
        sb.Append(",\"auto\":").Append(C.AttrAutoRaise ? "true" : "false")
          .Append(",\"every\":").Append(Math.Clamp(C.AttrAutoRaiseMinutes, 1, 1440))
          .Append(",\"mode\":").Append(Math.Clamp(C.AttrRaiseMode, 0, 2))
          .Append(",\"running\":").Append(Running ? "true" : "false");
        Str(sb, "keep", C.AttrKeepXp > 0 ? IltParse.Compact(C.AttrKeepXp) : "0");
        // The reserve box edits the exact number (the compact form above is lossy).
        Str(sb, "keepFull", IltParse.N0(Math.Max(0, C.AttrKeepXp)));
        Str(sb, "status", _status);
        Str(sb, "lastRun", _lastRun);
        Str(sb, "unassigned", $"Unassigned XP {IltParse.Compact(have)}"
                              + (C.AttrKeepXp > 0 ? $" (spendable {IltParse.Compact(Math.Max(0, budget))})" : ""));
        string next = !C.AttrAutoRaise ? string.Empty
                    : Running ? "running"
                    : _nextAutoAtMs <= 0 ? "soon"
                    : IltParse.Duration(TimeSpan.FromMilliseconds(Math.Max(0, _nextAutoAtMs - now)));
        Str(sb, "next", next);
        Str(sb, "costsAge", _costsAtMs == 0 ? "costs not read yet" : $"costs read {Ago(now - _costsAtMs)} ago");

        var ticked = new HashSet<string>(C.AttrRaiseStats, StringComparer.OrdinalIgnoreCase);
        sb.Append(",\"rows\":[");
        bool first = true;
        foreach (string k in C.AttrOrder)
        {
            Stat? s = Find(k);
            if (s == null) continue;
            if (!first) sb.Append(',');
            first = false;
            bool known = _cost.TryGetValue(s.Abbr, out long c) && c > 0;
            sb.Append('{');
            Str(sb, "key", s.Abbr, first: true);
            Str(sb, "label", s.Name);
            sb.Append(",\"vital\":").Append(s.Vital ? "true" : "false")
              .Append(",\"on\":").Append(ticked.Contains(s.Abbr) ? "true" : "false")
              .Append(",\"base\":").Append(_base.TryGetValue(s.Abbr, out int b) ? b : -1)
              .Append(",\"afford\":").Append(known && c <= budget ? "true" : "false");
            Str(sb, "cost", known ? IltParse.Compact(c) : "?");
            sb.Append('}');
        }
        sb.Append("]}");
    }

    private static string Ago(long ms)
    {
        long sec = Math.Max(0, ms / 1000);
        return sec < 60 ? $"{sec}s" : sec < 3600 ? $"{sec / 60}m" : $"{sec / 3600}h {sec % 3600 / 60}m";
    }

    private static void Str(StringBuilder sb, string name, string value, bool first = false)
    {
        if (!first) sb.Append(',');
        sb.Append('"').Append(name).Append("\":\"").Append(RynthAiPlugin.JsonEscape(value ?? string.Empty)).Append('"');
    }

    /// <summary>"prog attr…" commands from the Skills panel. Returns false when the verb isn't ours.</summary>
    public bool HandleRemote(string verb, string[] a)
    {
        string arg1 = a.Length > 1 ? a[1] : string.Empty;
        string arg2 = a.Length > 2 ? a[2] : string.Empty;
        switch (verb)
        {
            case "attrauto":
                C.AttrAutoRaise = arg1.Equals("on", StringComparison.OrdinalIgnoreCase);
                _nextAutoAtMs = C.AttrAutoRaise ? IltHubContext.NowMs + FirstAutoDelayMs : 0;
                if (!C.AttrAutoRaise && Running && _runAuto) Stop("Auto-raise turned off.");
                return true;
            case "attrevery":
                if (int.TryParse(arg1, out int min))
                {
                    C.AttrAutoRaiseMinutes = Math.Clamp(min, 1, 1440);
                    if (C.AttrAutoRaise) _nextAutoAtMs = IltHubContext.NowMs + C.AttrAutoRaiseMinutes * 60_000L;
                }
                return true;
            case "attrmode":
                if (int.TryParse(arg1, out int mode)) C.AttrRaiseMode = Math.Clamp(mode, 0, 2);
                return true;
            case "attrkeep":
                if (arg1.Length == 0 || arg1 == "0") C.AttrKeepXp = 0;
                else if (IltParse.TryParseAmount(arg1, out long keep)) C.AttrKeepXp = Math.Max(0, keep);
                return true;
            case "attron":
            {
                Stat? s = Find(arg1);
                if (s == null) return true;
                bool on = arg2.Equals("on", StringComparison.OrdinalIgnoreCase);
                C.AttrRaiseStats.RemoveAll(x => x.Equals(s.Abbr, StringComparison.OrdinalIgnoreCase));
                if (on) C.AttrRaiseStats.Add(s.Abbr);
                return true;
            }
            case "attrmove":
            {
                Stat? s = Find(arg1);
                if (s == null) return true;
                int i = C.AttrOrder.FindIndex(x => x.Equals(s.Abbr, StringComparison.OrdinalIgnoreCase));
                int j = arg2.Equals("up", StringComparison.OrdinalIgnoreCase) ? i - 1
                      : arg2.Equals("top", StringComparison.OrdinalIgnoreCase) ? 0 : i + 1;
                if (i < 0 || j < 0 || j >= C.AttrOrder.Count || i == j) return true;
                string item = C.AttrOrder[i];
                C.AttrOrder.RemoveAt(i);
                C.AttrOrder.Insert(j, item);
                return true;
            }
            case "attrorderreset":
                C.AttrOrder = new List<string>(DefaultOrder);
                return true;
            case "attrraise":
                if (int.TryParse(arg2, out int levels)) RaiseManual(arg1, levels);
                return true;
            case "attrrun": Start(auto: false); return true;
            case "attrstop": Stop(); return true;
            case "attrcosts": RefreshCosts(); return true;
        }
        return false;
    }
}
