// IltProgression.cs — Augmentation and Enlightenment planners, drawn by the engine's
// Skills panel (Progression tab) from AppendSnapshotJson; edits arrive via HandleRemote.
//
// Augs  : "/aug" levels + the per-server cost rules in IltAugCosts (UB's ILT model by default:
//         level i costs base + (i-1)*(base*LP/100), times 1/4/8/16/24 from S1..S4), coins per level.
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
    // ── Augmentation cost rules ─────────────────────────────────────────────

    /// <summary>Per-server aug cost rules (UB's ILT model unless this world was changed).</summary>
    public IltAugCosts AugCosts { get; } = new();

    /// <summary>The rules for the world we're on.</summary>
    private IltAugCostProfile AugProfile => AugCosts.For(_ctx.Options.WorldName);

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
    // Opening the Progression tab pulls "/aug" once per login when nothing has loaded the levels yet.
    private bool _augAutoTried;

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
        _augAutoTried = false;
        _augStatus = "not loaded";
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
        // The snapshot is only polled while the Progression tab is open, so this is "on first view".
        EnsureAugs();
        Attributes.EnsureCosts();
        AppendAugJson(sb);
        sb.Append(',');
        AppendEnlJson(sb);
        sb.Append(',');
        Attributes.AppendJson(sb);
    }

    /// <summary>Pulls "/aug" once per login when the levels haven't been loaded (login probe or by hand).</summary>
    private void EnsureAugs()
    {
        if (_augAutoTried || _ctx.Options.IsOff(IltFeature.Aug) || !_ctx.Host.HasInvokeChatParser) return;
        bool loaded;
        lock (_augLevels) loaded = _augLevels.Count > 0;
        _augAutoTried = true;
        if (!loaded) RequestAugs();
    }

    /// <summary>One aug's plan line: current level, clamped target, luminance and coins to get there.</summary>
    private readonly record struct AugPlan(IltAugCostRow Def, string Label, int Cur, int Tgt, int Ceiling, decimal Lum, long Coins)
    {
        public int Inc => Tgt - Cur;
    }

    private List<AugPlan> BuildAugPlan()
    {
        Dictionary<string, int> levels;
        lock (_augLevels) levels = new Dictionary<string, int>(_augLevels, StringComparer.OrdinalIgnoreCase);
        var profile = AugProfile;
        var plan = new List<AugPlan>(profile.Augs.Count);
        foreach (var a in profile.Augs)
        {
            int cur = levels.TryGetValue(a.Key, out int c) ? c : 0;
            int ceiling = IltAugCosts.Ceiling(a, cur);
            int tgt = Math.Clamp(C.AugTargets.TryGetValue(a.Key, out int t) ? t : cur, cur, ceiling);
            plan.Add(new AugPlan(a, IltAugCosts.Label(a.Key), cur, tgt, ceiling,
                IltAugCosts.LumCost(a, profile.Multipliers, cur, tgt), (long)(tgt - cur) * a.CoinsPerLevel));
        }
        return plan;
    }

    private void AppendAugJson(StringBuilder sb)
    {
        var plan = BuildAugPlan();

        sb.Append("\"aug\":{\"off\":").Append(Bool(_ctx.Options.IsOff(IltFeature.Aug)));
        Str(sb, "status", _augStatus);
        sb.Append(",\"lumPerCoin\":").Append(C.LumPerEnlightenedCoin).Append(",\"rows\":[");
        for (int i = 0; i < plan.Count; i++)
        {
            var p = plan[i];
            if (i > 0) sb.Append(',');
            sb.Append('{');
            Str(sb, "key", p.Def.Key, first: true);
            Str(sb, "label", p.Label);
            sb.Append(",\"cur\":").Append(p.Cur).Append(",\"tgt\":").Append(p.Tgt).Append(",\"cap\":").Append(p.Ceiling)
              .Append(",\"inc\":").Append(p.Inc);
            Str(sb, "lum", p.Lum > 0 ? IltParse.Compact((double)p.Lum) : "-");
            sb.Append(",\"coins\":").Append(p.Coins).Append('}');
        }
        sb.Append(']');

        decimal totalLum = plan.Sum(p => p.Lum);
        long totalCoins = plan.Sum(p => p.Coins);
        long bankCoins = _ctx.State.Bank.EnlightenedCoins;
        long lumPerCoin = C.LumPerEnlightenedCoin;
        long coinsShort = Math.Max(0, totalCoins - bankCoins);

        // Totals row (UB's TOTALS line) and the e-coin summary under the table.
        sb.Append(",\"totCur\":").Append(plan.Sum(p => p.Cur))
          .Append(",\"totTgt\":").Append(plan.Sum(p => p.Tgt))
          .Append(",\"totInc\":").Append(plan.Sum(p => p.Inc))
          .Append(",\"totCoins\":").Append(totalCoins);
        Str(sb, "totLum", totalLum > 0 ? IltParse.Compact((double)totalLum) : "-");
        Str(sb, "coinsLine", $"E-coins needed: {IltParse.N0(totalCoins)}   banked: {IltParse.N0(bankCoins)}   short: {IltParse.N0(coinsShort)}");
        Str(sb, "lumToBuy", coinsShort == 0 ? "Lum to buy e-coins: none needed"
                            : lumPerCoin > 0 ? $"Lum to buy e-coins: ~{IltParse.Compact((double)coinsShort * lumPerCoin)}"
                            : "Lum to buy e-coins: set Lum per coin");
        // Older engine builds read these three.
        Str(sb, "total", $"Total: {IltParse.Compact((double)totalLum)} luminance, {IltParse.N0(totalCoins)} coins");
        Str(sb, "short", coinsShort > 0
            ? $"Coins short: {IltParse.N0(coinsShort)}" + (lumPerCoin > 0 ? $" (~{IltParse.Compact((double)coinsShort * lumPerCoin)} luminance to buy)" : "")
            : string.Empty);
        Str(sb, "banked", $"Banked luminance: {IltParse.Compact(_ctx.State.Bank.Luminance)}");
        Str(sb, "copyDiscord", AugDiscordText(plan));
        Str(sb, "copyIngame", AugIngameText(plan));
        AppendAugCostJson(sb);
        sb.Append('}');
    }

    /// <summary><c>"cfg":{...}</c>: this world's cost rules for the "Cost settings" editor.</summary>
    private void AppendAugCostJson(StringBuilder sb)
    {
        string world = _ctx.Options.WorldName;
        var p = AugProfile;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        sb.Append(",\"cfg\":{");
        Str(sb, "world", world.Length > 0 ? world : "unknown world", first: true);
        sb.Append(",\"custom\":").Append(Bool(AugCosts.IsCustom(world))).Append(",\"mults\":[");
        for (int i = 0; i < p.Multipliers.Length; i++)
            sb.Append(i > 0 ? "," : "").Append(p.Multipliers[i].ToString("R", inv));
        sb.Append("],\"rows\":[");
        for (int i = 0; i < p.Augs.Count; i++)
        {
            var r = p.Augs[i];
            if (i > 0) sb.Append(',');
            sb.Append('{');
            Str(sb, "key", r.Key, first: true);
            Str(sb, "label", IltAugCosts.Label(r.Key));
            sb.Append(",\"base\":").Append(r.Base)
              .Append(",\"pct\":").Append(r.LinearPercent.ToString("R", inv))
              .Append(",\"coins\":").Append(r.CoinsPerLevel)
              .Append(",\"s1\":").Append(r.S1).Append(",\"s2\":").Append(r.S2)
              .Append(",\"s3\":").Append(r.S3).Append(",\"s4\":").Append(r.S4)
              .Append(",\"cap\":").Append(r.Cap).Append('}');
        }
        sb.Append("]}");
    }

    /// <summary>"augcfg &lt;aug&gt; &lt;field&gt; &lt;value&gt;": one cost-rule edit for this world.</summary>
    private void EditAugRule(string key, string field, string value)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        bool isInt = int.TryParse(value, System.Globalization.NumberStyles.Integer, inv, out int iv);
        bool isNum = double.TryParse(value, System.Globalization.NumberStyles.Float, inv, out double dv);
        long amount = 0;
        bool isAmount = field == "base" && IltParse.TryParseAmount(value, out amount);
        if (!isInt && !isNum && !isAmount) return;
        AugCosts.Edit(_ctx.Options.WorldName, p =>
        {
            var r = p.Augs.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (r == null) return;
            switch (field)
            {
                case "base": if (isAmount) r.Base = amount; break;
                case "pct": if (isNum) r.LinearPercent = dv; break;
                case "coins": if (isInt) r.CoinsPerLevel = iv; break;
                case "s1": if (isInt) r.S1 = iv; break;
                case "s2": if (isInt) r.S2 = iv; break;
                case "s3": if (isInt) r.S3 = iv; break;
                case "s4": if (isInt) r.S4 = iv; break;
                case "cap": if (isInt) r.Cap = iv; break;
            }
        });
    }

    /// <summary>
    /// UB's "Copy Discord" table in a code block. ASCII only: the engine reads the snapshot as ANSI.
    /// </summary>
    private string AugDiscordText(List<AugPlan> plan)
    {
        string name = string.IsNullOrEmpty(_ctx.CharName) ? "character" : _ctx.CharName;
        string rule = new('-', 56);
        var t = new StringBuilder();
        t.Append("```\n").Append("Augmentations - ").Append(name).Append('\n').Append(rule).Append('\n');
        t.Append($"{"Type",-15} {"Current",-8} {"Target",-8} {"Inc",-5} {"Lum Cost",-10} {"Coins",-6}\n").Append(rule).Append('\n');
        foreach (var p in plan)
        {
            if (p.Cur <= 0 && p.Tgt <= 0) continue;
            string lum = p.Lum > 0 ? IltParse.Compact((double)p.Lum) : "-";
            t.Append($"{p.Label,-15} {p.Cur,-8} {p.Tgt,-8} {p.Inc,-5} {lum,-10} {p.Coins,-6}\n");
        }
        decimal totalLum = plan.Sum(p => p.Lum);
        t.Append(rule).Append('\n');
        t.Append($"{"TOTALS:",-15} {plan.Sum(p => p.Cur),-8} {plan.Sum(p => p.Tgt),-8} {plan.Sum(p => p.Inc),-5} "
                 + $"{(totalLum > 0 ? IltParse.Compact((double)totalLum) : "-"),-10} {plan.Sum(p => p.Coins),-6}\n");
        t.Append("```");
        return t.ToString();
    }

    /// <summary>UB's "Copy In-Game": one line of current levels with short names, for pasting into chat.</summary>
    private string AugIngameText(List<AugPlan> plan)
    {
        string name = string.IsNullOrEmpty(_ctx.CharName) ? "character" : _ctx.CharName;
        var parts = plan.Where(p => p.Cur > 0).Select(p => $"{AugShortName(p.Def.Key)}: {IltParse.N0(p.Cur)}").ToList();
        return parts.Count == 0
            ? $"Augmentation Levels - {name}: none loaded"
            : $"Augmentation Levels - {name} ({IltParse.N0(plan.Sum(p => p.Cur))} total): {string.Join(", ", parts)}";
    }

    private static string AugShortName(string key) => key switch
    {
        "creature" => "Crit",
        "duration" => "Dur",
        "specialization" => "Spec",
        "summon" => "Sum",
        "melee" => "Mel",
        "missile" => "Mis",
        _ => char.ToUpperInvariant(key[0]) + key.Substring(1),
    };

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
            case "augreset":
                // UB's "Reset Target": every target back to the current level.
                lock (_augLevels)
                    foreach (var (key, _) in IltAugCosts.Labels)
                        C.AugTargets[key] = _augLevels.TryGetValue(key, out int c) ? c : 0;
                break;
            case "augtarget" when a.Length > 2:
                foreach (var def in AugProfile.Augs)
                {
                    if (!def.Key.Equals(arg1, StringComparison.OrdinalIgnoreCase)) continue;
                    int cur;
                    lock (_augLevels) cur = _augLevels.TryGetValue(def.Key, out int c) ? c : 0;
                    C.AugTargets[def.Key] = Math.Clamp(n, 0, IltAugCosts.Ceiling(def, cur));
                    break;
                }
                break;
            case "augmult" when a.Length > 2:
                // "augmult <tier 0-4> <multiplier>": tier 0 is below S1, tier 4 is from S4 on.
                if (int.TryParse(arg1, out int tier) && tier is >= 0 and <= 4
                    && double.TryParse(a[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double mult))
                    AugCosts.Edit(_ctx.Options.WorldName, p => p.Multipliers[tier] = mult);
                break;
            case "augcfg" when a.Length > 3:
                EditAugRule(arg1, a[2].ToLowerInvariant(), a[3]);
                break;
            case "augcfgreset":
                AugCosts.Reset(_ctx.Options.WorldName);
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
