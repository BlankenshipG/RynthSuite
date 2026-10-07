// IltProgression.cs — Augmentation and Enlightenment planners, drawn by the engine's
// Skills panel (Progression tab) from AppendSnapshotJson; edits arrive via HandleRemote.
//
// Augs  : "/aug" levels + the SERVER cost formula (per level n: base + n*(base*LP/100),
//         times a soft-cap multiplier 1/4/8/16/24 at thresholds S1..S4), coins per level.
// Enl   : tier material/luminance table, requirement checklist, "/enl" on confirm. The
//         server pops its own Yes/No dialog — the host cannot (and must not) click it.
//         Optional auto-enlighten is disarmed every session and needs a hard confirm.
// XP    : the Skills panel plans attribute/vital raises from the client's exact XP tables;
//         this module spends unassigned XP ("/attr") before an auto-enlighten, and owns the
//         infinite-attribute raiser (IltAttributeRaiser: server costs from "/xp all", manual
//         or timed raises in a chosen order) drawn in the same tab.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RynthCore.Plugin.RynthAi.IltHub;

internal sealed class IltProgression : IIltFeature
{
    // ── Augmentation table (server values) ──────────────────────────────────

    private sealed record AugDef(string Key, string Label, long Base, int LinearPercent, int CoinsPerLevel,
                                 int S1, int S2, int S3, int S4, int Cap);

    private static readonly AugDef[] Augs =
    {
        new("creature",       "Creature",       750_000,   130, 3,  2750, 4000, 4750, 5250, 6000),
        new("item",           "Item",           1_000_000, 165, 18, 1250, 2000, 3000, 3500, 4000),
        new("life",           "Life",           975_000,   195, 12, 1000, 2000, 3000, 3750, 4000),
        new("war",            "War",            750_000,   140, 5,  1750, 2500, 3000, 3750, 4000),
        new("void",           "Void",           800_000,   160, 5,  1750, 2500, 3000, 3750, 4000),
        new("duration",       "Duration",       400_000,   120, 5,  1000, 2000, 2500, 3000, 4000),
        new("melee",          "Melee",          750_000,   140, 5,  1750, 2500, 3000, 3750, 4000),
        new("missile",        "Missile",        750_000,   140, 5,  1750, 2500, 3000, 3750, 4000),
        new("specialization", "Specialization", 3_000_000, 200, 50, 1750, 2000, 2250, 2750, 266),
        new("summon",         "Summon",         500_000,   125, 36, 1250, 2000, 3000, 3500, 4000),
    };

    /// <summary>Luminance for raising one aug from <paramref name="cur"/> to <paramref name="tgt"/>.</summary>
    private static decimal AugLumCost(AugDef a, int cur, int tgt)
    {
        decimal total = 0;
        decimal step = a.Base * (decimal)a.LinearPercent / 100m;
        for (int n = Math.Max(0, cur); n < tgt; n++)
        {
            int mult = n >= a.S4 ? 24 : n >= a.S3 ? 16 : n >= a.S2 ? 8 : n >= a.S1 ? 4 : 1;
            total += (a.Base + n * step) * mult;
        }
        return total;
    }

    // ── Enlightenment ───────────────────────────────────────────────────────

    private const int BaseLevelForEnl = 275;
    private const long AutoEnlCooldownMs = 60_000;
    private const uint SpellVitae = 666;

    /// <summary>Materials for reaching enlightenment level <paramref name="t"/> (one step).</summary>
    public static (string Item, uint Wcid, int Count, decimal Lum) EnlStepCost(int t)
    {
        if (t <= 5) return ("none", 0, 0, 0);
        int count = t - 5;
        if (t <= 50) return ("Enlightenment Tokens", IltInventory.WcidEnlToken, count, 0);
        if (t <= 150) return ("Enlightenment Tokens", IltInventory.WcidEnlToken, count, t * 100_000_000m);
        if (t <= 300) return ("Enlightenment Medallions", IltInventory.WcidEnlMedallion, count, t * 1_000_000_000m);
        decimal mult = 1m + 0.5m * ((t - 300 - 1) / 50);
        decimal lum = Math.Ceiling(t * 2_000_000_000m * mult);
        return t <= 325 ? ("Enlightenment Sigils", IltInventory.WcidEnlSigil, count, lum)
                        : ("Enlightenment Crests", IltInventory.WcidEnlCrest, count, lum);
    }

    // ── XP ──────────────────────────────────────────────────────────────────

    /// <summary>/attr abbreviation, display name, attribute stype (0 = vital), vital stype.</summary>
    private static readonly (string Abbr, string Name, uint Attr, uint Vital)[] Stats =
    {
        ("str", "Strength", 1, 0), ("end", "Endurance", 2, 0), ("coo", "Coordination", 4, 0),
        ("qui", "Quickness", 3, 0), ("foc", "Focus", 5, 0), ("sel", "Self", 6, 0),
        ("hea", "Health", 0, 1), ("sta", "Stamina", 0, 3), ("man", "Mana", 0, 5),
    };

    private readonly IltHubContext _ctx;
    private readonly Dictionary<string, int> _augLevels = new(StringComparer.OrdinalIgnoreCase);
    private volatile string _augStatus = "not loaded";

    // Cached player facts for the render thread (refreshed in Tick).
    private volatile int _enl, _level, _freeSlots;
    private long _unassignedXp;
    private volatile bool _peace, _hasVitae;
    private readonly long[] _enlHave = new long[4]; // tokens, medallions, sigils, crests
    private long _lastFactsAt;
    private readonly uint[] _enchIds = new uint[256];
    private readonly double[] _enchExp = new double[256];

    // Auto-enlighten (armed only after the in-session confirm).
    private bool _autoArmed;
    private long _lastAutoCheck;
    private long _lastEnlSentAt = -AutoEnlCooldownMs;
    private volatile string _enlStatus = string.Empty;

    // XP spend-before-enlighten loop.
    private bool _spending;
    private int _spendIdx, _spendSent, _spendNoProgress;
    private long _spendLastAt, _spendLastXp;
    private Action? _afterSpend;

    /// <summary>Infinite-attribute raiser (Progression tab "Attribute raiser" section).</summary>
    public IltAttributeRaiser Attributes { get; }

    public IltProgression(IltHubContext ctx)
    {
        _ctx = ctx;
        // Never two /attr spenders at once: the raiser waits while the spend-before-/enl loop runs.
        Attributes = new IltAttributeRaiser(ctx) { HoldOff = () => _spending };
    }

    private IltCharacterState C => _ctx.State.Character;

    // ── Commands ────────────────────────────────────────────────────────────

    public void RequestAugs()
    {
        RynthLog.Trace(LogCat.IltProgression, $"RequestAugs()");
        if (_ctx.Options.IsOff(IltFeature.Aug) || _ctx.Capture.IsPending("/aug")) return;
        _augStatus = "loading...";
        _ctx.Capture.Enqueue(new IltChatRequest
        {
            Command = "/aug",
            IsResponseLine = t => t.StartsWith("---") || t.Contains("Augmentation Levels", StringComparison.OrdinalIgnoreCase) || IltParse.AugLine.IsMatch(t),
            IdleEndMs = 2000,
            FirstLineTimeoutMs = 15000,
            Eat = true,
            OnComplete = r =>
            {
                if (r.UnknownCommand) { _ctx.Options.Set(IltFeature.Aug, IltTri.Off); _augStatus = "/aug not available"; return; }
                ApplyAugLines(r.Lines);
            },
        });
    }

    /// <summary>Parses "/aug" output (also fed from the login probe).</summary>
    public void ApplyAugLines(List<string> lines)
    {
        RynthLog.Trace(LogCat.IltProgression, $"ApplyAugLines(lines={lines.Count})");
        int n = 0;
        foreach (string l in lines)
        {
            var m = IltParse.AugLine.Match(l.Trim());
            if (!m.Success) continue;
            string key = m.Groups[1].Value.ToLowerInvariant();
            if (key == "specialize") key = "specialization";
            lock (_augLevels) _augLevels[key] = (int)IltParse.ParseLeadingLong(m.Groups[2].Value);
            n++;
        }
        _augStatus = n > 0 ? $"{n} aug types @ {DateTime.Now:t}" : "no aug lines in reply";
    }

    /// <summary>Sends "/enl" (server shows the confirmation dialog). Pump thread.</summary>
    public void SendEnlighten(string why)
    {
        RynthLog.Trace(LogCat.IltProgression, $"SendEnlighten(why='{why}')");
        if (!_ctx.Host.HasInvokeChatParser) return;
        _lastEnlSentAt = IltHubContext.NowMs;
        _ctx.Host.InvokeChatParser("/enl");
        _enlStatus = $"/enl sent ({why}) - click Yes in the AC dialog to confirm.";
        _ctx.Chat("[ILT Hub] " + _enlStatus);
    }

    // ── IIltFeature ─────────────────────────────────────────────────────────

    public void Tick(long nowMs)
    {
        if (nowMs - _lastFactsAt >= 2000)
        {
            _lastFactsAt = nowMs;
            RefreshFacts();
        }
        TickSpend(nowMs);
        Attributes.Tick(nowMs, holdOff: _spending);

        if (_autoArmed && C.AutoEnlightenEnabled && !_spending
            && nowMs - _lastAutoCheck >= Math.Max(10, C.AutoEnlightenCheckSeconds) * 1000L)
        {
            _lastAutoCheck = nowMs;
            TryAutoEnlighten(nowMs);
        }
    }

    public bool OnChat(string text)
    {
        Attributes.OnChat(text);
        if (text.Contains("You have become enlightened", StringComparison.OrdinalIgnoreCase)
            || text.Contains("You have risen to a higher tier of enlightenment", StringComparison.OrdinalIgnoreCase))
        {
            _enlStatus = "Enlightened! " + DateTime.Now.ToString("t");
            _lastFactsAt = 0;
        }
        return false;
    }

    public void OnLogout()
    {
        _autoArmed = false;
        _spending = false;
        Attributes.OnLogout();
        lock (_augLevels) _augLevels.Clear();
    }

    // ── Facts / auto ────────────────────────────────────────────────────────

    private void RefreshFacts()
    {
        var inv = _ctx.Inventory;
        _enl = inv.PlayerInt(IltInventory.IntEnlightenment);
        _level = inv.PlayerInt(IltInventory.IntLevel);
        _unassignedXp = inv.PlayerQuad(IltInventory.QuadAvailableXp);
        _peace = inv.InPeaceMode;
        _freeSlots = inv.FreeMainPackSlots();
        _enlHave[0] = inv.CountByWcid(IltInventory.WcidEnlToken);
        _enlHave[1] = inv.CountByWcid(IltInventory.WcidEnlMedallion);
        _enlHave[2] = inv.CountByWcid(IltInventory.WcidEnlSigil);
        _enlHave[3] = inv.CountByWcid(IltInventory.WcidEnlCrest);

        _hasVitae = false;
        if (_ctx.Host.HasReadPlayerEnchantments)
        {
            int n = _ctx.Host.ReadPlayerEnchantments(_enchIds, _enchExp, _enchIds.Length);
            for (int i = 0; i < Math.Min(n, _enchIds.Length); i++)
                if (_enchIds[i] == SpellVitae) { _hasVitae = true; break; }
        }
    }

    private long HaveFor(uint wcid) => wcid switch
    {
        IltInventory.WcidEnlToken => _enlHave[0],
        IltInventory.WcidEnlMedallion => _enlHave[1],
        IltInventory.WcidEnlSigil => _enlHave[2],
        IltInventory.WcidEnlCrest => _enlHave[3],
        _ => 0,
    };

    /// <summary>Checks the client-visible requirements for the next enlightenment. Empty = ready.</summary>
    private List<string> NextEnlBlockers()
    {
        var list = new List<string>();
        int next = _enl + 1;
        if (_level < BaseLevelForEnl + _enl) list.Add($"Need character level {BaseLevelForEnl + _enl} (have {_level}).");
        if (!_peace) list.Add("Must be in peace mode.");
        if (_hasVitae) list.Add("Must not have vitae.");
        var cost = EnlStepCost(next);
        if (cost.Count > 0 && HaveFor(cost.Wcid) < cost.Count) list.Add($"Need {cost.Count} {cost.Item} (have {HaveFor(cost.Wcid)}).");
        if (cost.Lum > 0 && _ctx.State.Bank.Luminance < cost.Lum) list.Add($"Need {IltParse.Compact((double)cost.Lum)} banked luminance.");
        return list;
    }

    private void TryAutoEnlighten(long now)
    {
        if (_ctx.Options.IsOff(IltFeature.Enl)) return;
        if (Attributes.Running) return;   // the raiser is spending XP; check again next interval
        if (now - _lastEnlSentAt < AutoEnlCooldownMs) return;
        if (NextEnlBlockers().Count > 0) return;
        if (C.AutoEnlightenSpendXpFirst && _unassignedXp > 0)
            StartSpend(() => SendEnlighten("auto"));
        else
            SendEnlighten("auto");
    }

    // ── XP spend loop: cycles /attr <abbr> 1 until unassigned XP stops dropping ──

    private void StartSpend(Action then)
    {
        _spending = true;
        _spendIdx = 0;
        _spendSent = 0;
        _spendNoProgress = 0;
        _spendLastAt = 0;
        _spendLastXp = _ctx.Inventory.PlayerQuad(IltInventory.QuadAvailableXp);
        _afterSpend = then;
        _enlStatus = "Spending unassigned XP before enlightening...";
    }

    private void TickSpend(long now)
    {
        if (!_spending || now - _spendLastAt < 300) return;
        _spendLastAt = now;

        long xp = _ctx.Inventory.PlayerQuad(IltInventory.QuadAvailableXp);
        if (xp < _spendLastXp) _spendNoProgress = 0; else if (_spendSent > 0) _spendNoProgress++;
        _spendLastXp = xp;

        // Stop after a full stat cycle with no XP drop, when XP is gone, or at the safety cap.
        if (xp <= 0 || _spendNoProgress >= Stats.Length || _spendSent >= 300)
        {
            _spending = false;
            var next = _afterSpend;
            _afterSpend = null;
            next?.Invoke();
            return;
        }
        if (_ctx.Inventory.IsBusy) return;
        _ctx.Host.InvokeChatParser($"/attr {Stats[_spendIdx].Abbr} 1");
        _spendIdx = (_spendIdx + 1) % Stats.Length;
        _spendSent++;
    }

    // ── Engine Skills panel bridge (pump thread) ────────────────────────────
    // The engine's Skills panel draws Augmentations and Enlightenment from this snapshot and
    // sends edits back as "prog ..." remote commands (HandleRemote). Planner math stays here so
    // the Hub and the Skills panel can never disagree. Numbers are preformatted for display.

    /// <summary>Appends <c>"aug":{...},"enl":{...},"attr":{...}</c> to <paramref name="sb"/>. Pump thread.</summary>
    public void AppendSnapshotJson(StringBuilder sb)
    {
        AppendAugJson(sb);
        sb.Append(',');
        AppendEnlJson(sb);
        sb.Append(',');
        Attributes.AppendJson(sb);
    }

    private void AppendAugJson(StringBuilder sb)
    {
        Dictionary<string, int> levels;
        lock (_augLevels) levels = new Dictionary<string, int>(_augLevels, StringComparer.OrdinalIgnoreCase);

        sb.Append("\"aug\":{\"off\":").Append(Bool(_ctx.Options.IsOff(IltFeature.Aug)));
        Str(sb, "status", _augStatus);
        sb.Append(",\"lumPerCoin\":").Append(C.LumPerEnlightenedCoin).Append(",\"rows\":[");
        decimal totalLum = 0;
        long totalCoins = 0;
        for (int i = 0; i < Augs.Length; i++)
        {
            var a = Augs[i];
            int cur = levels.TryGetValue(a.Key, out int c) ? c : 0;
            int ceiling = Math.Max(cur, a.Cap);
            int tgt = Math.Clamp(C.AugTargets.TryGetValue(a.Key, out int t) ? t : cur, cur, ceiling);
            decimal lum = AugLumCost(a, cur, tgt);
            long coins = (long)(tgt - cur) * a.CoinsPerLevel;
            totalLum += lum;
            totalCoins += coins;
            if (i > 0) sb.Append(',');
            sb.Append('{');
            Str(sb, "key", a.Key, first: true);
            Str(sb, "label", a.Label);
            sb.Append(",\"cur\":").Append(cur).Append(",\"tgt\":").Append(tgt).Append(",\"cap\":").Append(ceiling);
            Str(sb, "lum", lum > 0 ? IltParse.Compact((double)lum) : "-");
            sb.Append(",\"coins\":").Append(coins).Append('}');
        }
        sb.Append(']');
        Str(sb, "total", $"Total: {IltParse.Compact((double)totalLum)} luminance, {IltParse.N0(totalCoins)} coins");
        long bankCoins = _ctx.State.Bank.EnlightenedCoins;
        long lumPerCoin = C.LumPerEnlightenedCoin;
        string shortText = totalCoins > bankCoins
            ? $"Coins short: {IltParse.N0(totalCoins - bankCoins)}"
              + (lumPerCoin > 0 ? $" (~{IltParse.Compact((double)(totalCoins - bankCoins) * lumPerCoin)} luminance to buy)" : "")
            : string.Empty;
        Str(sb, "short", shortText);
        Str(sb, "banked", $"Banked luminance: {IltParse.Compact(_ctx.State.Bank.Luminance)}");
        sb.Append('}');
    }

    private void AppendEnlJson(StringBuilder sb)
    {
        int enl = _enl;
        sb.Append("\"enl\":{\"off\":").Append(Bool(_ctx.Options.IsOff(IltFeature.Enl)));
        Str(sb, "header", $"Enlightenment {enl}   Level {_level}   Unassigned XP {IltParse.Compact(_unassignedXp)}");
        var next = EnlStepCost(enl + 1);
        Str(sb, "next", $"Next ({enl + 1}): " + (next.Count > 0
            ? $"{next.Count} {next.Item} (have {HaveFor(next.Wcid)})" + (next.Lum > 0 ? $" + {IltParse.Compact((double)next.Lum)} lum" : "")
            : "no materials"));

        int target = C.EnlTargetLevel <= enl ? enl + 5 : C.EnlTargetLevel;
        sb.Append(",\"enl\":").Append(enl).Append(",\"target\":").Append(target).Append(",\"plan\":[");
        var totals = new Dictionary<string, long>();
        decimal lumTotal = 0;
        for (int t = enl + 1; t <= Math.Max(enl + 1, target) && t <= enl + 500; t++)
        {
            var s = EnlStepCost(t);
            if (s.Count > 0) totals[s.Item] = (totals.TryGetValue(s.Item, out long v) ? v : 0) + s.Count;
            lumTotal += s.Lum;
        }
        bool firstLine = true;
        foreach (var kv in totals) { StrItem(sb, $"{IltParse.N0(kv.Value)} {kv.Key}", ref firstLine); }
        if (lumTotal > 0)
            StrItem(sb, $"{IltParse.Compact((double)lumTotal)} luminance (banked {IltParse.Compact(_ctx.State.Bank.Luminance)})", ref firstLine);
        sb.Append(']');

        int coinsPerToken = C.EnlightenedCoinsPerToken;
        sb.Append(",\"coinsPerToken\":").Append(coinsPerToken);
        string tokensShort = string.Empty;
        if (totals.TryGetValue("Enlightenment Tokens", out long tokNeed) && coinsPerToken > 0)
        {
            long shortTok = Math.Max(0, tokNeed - _enlHave[0]);
            if (shortTok > 0) tokensShort = $"Tokens short: {shortTok} (~{IltParse.N0(shortTok * coinsPerToken)} coins)";
        }
        Str(sb, "tokensShort", tokensShort);

        var blockers = NextEnlBlockers();
        sb.Append(",\"blockers\":[");
        bool firstBlocker = true;
        foreach (string b in blockers) StrItem(sb, b, ref firstBlocker);
        sb.Append(']');
        Str(sb, "serverChecks", $"Server also checks: not in a dungeon, free pack slots ({(_freeSlots >= 0 ? _freeSlots.ToString() : "?")} free), "
                                + "all luminance augs (past 10), society master (past 30).");
        sb.Append(",\"auto\":").Append(Bool(C.AutoEnlightenEnabled))
          .Append(",\"armed\":").Append(Bool(_autoArmed))
          .Append(",\"spendFirst\":").Append(Bool(C.AutoEnlightenSpendXpFirst))
          .Append(",\"checkEvery\":").Append(C.AutoEnlightenCheckSeconds);
        Str(sb, "status", _enlStatus);
        sb.Append('}');
    }

    private static string Bool(bool b) => b ? "true" : "false";

    private static void Str(StringBuilder sb, string name, string value, bool first = false)
    {
        if (!first) sb.Append(',');
        sb.Append('"').Append(name).Append("\":\"").Append(RynthAiPlugin.JsonEscape(value ?? string.Empty)).Append('"');
    }

    private static void StrItem(StringBuilder sb, string value, ref bool first)
    {
        if (!first) sb.Append(',');
        first = false;
        sb.Append('"').Append(RynthAiPlugin.JsonEscape(value)).Append('"');
    }

    /// <summary>
    /// "prog" remote command from the engine Skills panel. Pump thread. The panel asks its own
    /// confirmation before "enlighten" and "autoenl on|arm", so none is asked here.
    /// </summary>
    public void HandleRemote(string[] a)
    {
        if (a.Length == 0) return;
        RynthLog.Trace(LogCat.IltProgression, $"HandleRemote({string.Join(" ", a)})");
        string arg1 = a.Length > 1 ? a[1] : string.Empty;
        int.TryParse(a.Length > 2 ? a[2] : arg1, out int n);
        string verb = a[0].ToLowerInvariant();
        if (verb.StartsWith("attr", StringComparison.Ordinal) && Attributes.HandleRemote(verb, a)) return;
        switch (verb)
        {
            case "augload": RequestAugs(); break;
            case "augtarget" when a.Length > 2:
                foreach (var def in Augs)
                {
                    if (!def.Key.Equals(arg1, StringComparison.OrdinalIgnoreCase)) continue;
                    int cur;
                    lock (_augLevels) cur = _augLevels.TryGetValue(def.Key, out int c) ? c : 0;
                    C.AugTargets[def.Key] = Math.Clamp(n, 0, Math.Max(cur, def.Cap));
                    break;
                }
                break;
            case "lumpercoin":
                if (IltParse.TryParseAmount(arg1, out long lpc)) C.LumPerEnlightenedCoin = Math.Max(0, lpc);
                break;
            case "enltarget": C.EnlTargetLevel = Math.Max(_enl + 1, n); break;
            case "coinspertoken": C.EnlightenedCoinsPerToken = Math.Max(0, n); break;
            case "enlighten": SendEnlighten("manual, Skills panel"); break;
            case "autoenl":
                switch (arg1.ToLowerInvariant())
                {
                    case "on":
                        C.AutoEnlightenEnabled = true;
                        _autoArmed = true;
                        _ctx.Chat("[ILT Hub] Auto-enlighten armed for this session.");
                        break;
                    case "arm": if (C.AutoEnlightenEnabled) _autoArmed = true; break;
                    default: C.AutoEnlightenEnabled = false; _autoArmed = false; break;
                }
                break;
            case "spendfirst": C.AutoEnlightenSpendXpFirst = arg1.Equals("on", StringComparison.OrdinalIgnoreCase); break;
            case "checkevery": C.AutoEnlightenCheckSeconds = Math.Max(10, n); break;
        }
    }
}
