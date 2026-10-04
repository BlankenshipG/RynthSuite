// IltProgression.cs — Augmentation, Enlightenment and XP planners (ILT Hub Character tab).
//
// Augs  : "/aug" levels + the SERVER cost formula (per level n: base + n*(base*LP/100),
//         times a soft-cap multiplier 1/4/8/16/24 at thresholds S1..S4), coins per level.
// Enl   : tier material/luminance table, requirement checklist, "/enl" on confirm. The
//         server pops its own Yes/No dialog — the host cannot (and must not) click it.
//         Optional auto-enlighten is disarmed every session and needs a hard confirm.
// XP    : "/xp all" next-level costs, multi-level estimates, "/attr" raises on confirm.
using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;

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
    private readonly Dictionary<string, long> _xpCosts = new(StringComparer.OrdinalIgnoreCase);
    private volatile string _xpStatus = "not loaded";

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

    // Render-thread inputs.
    private int _xpLevels = 10;

    public IltProgression(IltHubContext ctx) => _ctx = ctx;

    private IltCharacterState C => _ctx.State.Character;

    // ── Commands ────────────────────────────────────────────────────────────

    public void RequestAugs()
    {
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

    public void RequestXpCosts()
    {
        if (_ctx.Options.IsOff(IltFeature.Xp) || _ctx.Capture.IsPending("/xp all")) return;
        _xpStatus = "loading...";
        _ctx.Capture.Enqueue(new IltChatRequest
        {
            Command = "/xp all",
            IsResponseLine = t => t.StartsWith("[XP]", StringComparison.OrdinalIgnoreCase),
            IdleEndMs = 1500,
            FirstLineTimeoutMs = 5000,
            Eat = true,
            OnComplete = r =>
            {
                if (r.UnknownCommand) { _ctx.Options.Set(IltFeature.Xp, IltTri.Off); _xpStatus = "/xp not available"; return; }
                int n = 0;
                foreach (string l in r.Lines)
                {
                    var m = IltParse.XpCost.Match(l);
                    if (!m.Success) continue;
                    lock (_xpCosts) _xpCosts[m.Groups[1].Value] = IltParse.ParseLeadingLong(m.Groups[2].Value);
                    n++;
                }
                _xpStatus = n > 0 ? $"{n} costs @ {DateTime.Now:t}" : "no cost lines in reply";
            },
        });
    }

    /// <summary>Sends "/attr abbr n" (n clamped 1..10). Pump thread, after the UI confirm.</summary>
    public void RaiseStat(string abbr, int levels)
    {
        levels = Math.Clamp(levels, 1, 10);
        if (_ctx.Host.HasInvokeChatParser) _ctx.Host.InvokeChatParser($"/attr {abbr} {levels}");
        // Costs change after a raise — re-read shortly.
        RequestXpCosts();
    }

    /// <summary>Sends "/enl" (server shows the confirmation dialog). Pump thread.</summary>
    public void SendEnlighten(string why)
    {
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

        if (_autoArmed && C.AutoEnlightenEnabled && !_spending
            && nowMs - _lastAutoCheck >= Math.Max(10, C.AutoEnlightenCheckSeconds) * 1000L)
        {
            _lastAutoCheck = nowMs;
            TryAutoEnlighten(nowMs);
        }
    }

    public bool OnChat(string text)
    {
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
        lock (_augLevels) _augLevels.Clear();
        lock (_xpCosts) _xpCosts.Clear();
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

    // ── UI (render thread) ──────────────────────────────────────────────────

    public void RenderAugs()
    {
        if (_ctx.Options.IsOff(IltFeature.Aug)) { ImGui.TextColored(LegacyDashboardRenderer.ColTextMute, "/aug is not available on this server."); return; }
        if (ImGui.SmallButton("Load /aug")) _ctx.Post(RequestAugs);
        ImGui.SameLine();
        ImGui.TextDisabled(_augStatus);

        long lumPerCoin = C.LumPerEnlightenedCoin;
        ImGui.SetNextItemWidth(160);
        string lpc = lumPerCoin.ToString();
        if (ImGui.InputTextWithHint("Lum per Enlightened Coin", "0 = don't price coins", ref lpc, 24u)
            && IltParse.TryParseAmount(lpc, out long parsed))
            C.LumPerEnlightenedCoin = parsed;

        Dictionary<string, int> levels;
        lock (_augLevels) levels = new Dictionary<string, int>(_augLevels, StringComparer.OrdinalIgnoreCase);

        decimal totalLum = 0;
        long totalCoins = 0;
        if (ImGui.BeginTable("##iltaugs", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("Aug");
            ImGui.TableSetupColumn("Current", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Luminance");
            ImGui.TableSetupColumn("Coins", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableHeadersRow();
            foreach (var a in Augs)
            {
                int cur = levels.TryGetValue(a.Key, out int c) ? c : 0;
                int tgt = C.AugTargets.TryGetValue(a.Key, out int t) ? t : cur;
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(a.Label);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(cur.ToString());
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(80);
                if (ImGui.InputInt($"##tgt{a.Key}", ref tgt, 0)) C.AugTargets[a.Key] = Math.Clamp(tgt, 0, a.Cap);
                tgt = Math.Clamp(tgt, cur, a.Cap);
                decimal lum = AugLumCost(a, cur, tgt);
                long coins = (long)(tgt - cur) * a.CoinsPerLevel;
                totalLum += lum;
                totalCoins += coins;
                ImGui.TableNextColumn(); ImGui.TextUnformatted(lum > 0 ? IltParse.Compact((double)lum) : "-");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(coins > 0 ? coins.ToString() : "-");
            }
            ImGui.EndTable();
        }
        ImGui.TextColored(LegacyDashboardRenderer.ColAmber, $"Total: {IltParse.Compact((double)totalLum)} luminance, {IltParse.N0(totalCoins)} coins");
        long bankCoins = _ctx.State.Bank.EnlightenedCoins;
        if (totalCoins > bankCoins)
        {
            long missing = totalCoins - bankCoins;
            ImGui.TextUnformatted($"Coins short: {IltParse.N0(missing)}"
                + (lumPerCoin > 0 ? $" (~{IltParse.Compact((double)missing * lumPerCoin)} luminance to buy)" : ""));
        }
        ImGui.TextDisabled($"Banked luminance: {IltParse.Compact(_ctx.State.Bank.Luminance)}");
    }

    public void RenderEnlightenment()
    {
        if (_ctx.Options.IsOff(IltFeature.Enl)) { ImGui.TextColored(LegacyDashboardRenderer.ColTextMute, "/enl is not available on this server."); return; }
        int enl = _enl;
        ImGui.TextUnformatted($"Enlightenment {enl}   Level {_level}   Unassigned XP {IltParse.Compact(_unassignedXp)}");

        // Next step
        var next = EnlStepCost(enl + 1);
        ImGui.TextColored(LegacyDashboardRenderer.ColTeal, $"Next ({enl + 1}):");
        ImGui.SameLine();
        ImGui.TextUnformatted(next.Count > 0
            ? $"{next.Count} {next.Item} (have {HaveFor(next.Wcid)})" + (next.Lum > 0 ? $" + {IltParse.Compact((double)next.Lum)} lum" : "")
            : "no materials");

        // Planner to a target
        int target = C.EnlTargetLevel <= enl ? enl + 5 : C.EnlTargetLevel;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Plan to level", ref target)) C.EnlTargetLevel = Math.Max(enl + 1, target);
        var totals = new Dictionary<string, long>();
        decimal lumTotal = 0;
        for (int t = enl + 1; t <= Math.Max(enl + 1, target) && t <= enl + 500; t++)
        {
            var s = EnlStepCost(t);
            if (s.Count > 0) totals[s.Item] = (totals.TryGetValue(s.Item, out long v) ? v : 0) + s.Count;
            lumTotal += s.Lum;
        }
        foreach (var kv in totals) ImGui.BulletText($"{IltParse.N0(kv.Value)} {kv.Key}");
        if (lumTotal > 0) ImGui.BulletText($"{IltParse.Compact((double)lumTotal)} luminance (banked {IltParse.Compact(_ctx.State.Bank.Luminance)})");
        int coinsPerToken = C.EnlightenedCoinsPerToken;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Coins per token (shop price)", ref coinsPerToken)) C.EnlightenedCoinsPerToken = Math.Max(0, coinsPerToken);
        if (totals.TryGetValue("Enlightenment Tokens", out long tokNeed) && coinsPerToken > 0)
        {
            long shortTok = Math.Max(0, tokNeed - _enlHave[0]);
            if (shortTok > 0) ImGui.TextDisabled($"Tokens short: {shortTok} (~{IltParse.N0(shortTok * coinsPerToken)} coins)");
        }

        // Requirements
        ImGui.Separator();
        var blockers = NextEnlBlockers();
        if (blockers.Count == 0) ImGui.TextColored(LegacyDashboardRenderer.ColGreen, "Client-side checks pass.");
        foreach (string b in blockers) ImGui.TextColored(LegacyDashboardRenderer.ColAmber, b);
        ImGui.TextDisabled($"Server also checks: not in a dungeon, free pack slots ({(_freeSlots >= 0 ? _freeSlots.ToString() : "?")} free), "
                           + "all luminance augs (past 10), society master (past 30).");

        if (ImGui.Button("Enlighten now..."))
            _ctx.Confirm("Enlighten",
                "Send /enl now?\n\nEnlightening resets your level and wipes unassigned XP.\nThe server will show its own Yes/No dialog - you must click Yes there.",
                () => SendEnlighten("manual"), "Send /enl");

        // Auto-enlighten
        ImGui.Separator();
        bool auto = C.AutoEnlightenEnabled;
        if (ImGui.Checkbox("Auto-enlighten when ready", ref auto))
        {
            if (auto)
                _ctx.Confirm("Arm auto-enlighten",
                    "Auto-enlighten will send /enl whenever the checks pass (peace mode, level, materials).\n"
                    + "You still have to click Yes in the AC dialog each time.\n\nArm it for this session?",
                    () => { C.AutoEnlightenEnabled = true; _autoArmed = true; _ctx.Chat("[ILT Hub] Auto-enlighten armed for this session."); },
                    "Arm");
            else { C.AutoEnlightenEnabled = false; _autoArmed = false; }
        }
        if (C.AutoEnlightenEnabled && !_autoArmed)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Arm for this session..."))
                _ctx.Confirm("Arm auto-enlighten", "Arm auto-enlighten for this session?", () => _autoArmed = true, "Arm");
        }
        bool spend = C.AutoEnlightenSpendXpFirst;
        if (ImGui.Checkbox("Spend unassigned XP on attributes first", ref spend)) C.AutoEnlightenSpendXpFirst = spend;
        int every = C.AutoEnlightenCheckSeconds;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Check every (s)", ref every)) C.AutoEnlightenCheckSeconds = Math.Max(10, every);
        ImGui.TextDisabled(_autoArmed ? "Armed." : "Disarmed.");
        if (_enlStatus.Length > 0) ImGui.TextWrapped(_enlStatus);
    }

    public void RenderXp()
    {
        if (_ctx.Options.IsOff(IltFeature.Xp)) { ImGui.TextColored(LegacyDashboardRenderer.ColTextMute, "/xp is not available on this server."); return; }
        if (ImGui.SmallButton("Load /xp all")) _ctx.Post(RequestXpCosts);
        ImGui.SameLine();
        ImGui.TextDisabled(_xpStatus);
        ImGui.TextUnformatted($"Unassigned XP: {IltParse.N0(_unassignedXp)}");
        ImGui.SetNextItemWidth(120);
        ImGui.SliderInt("Levels to estimate", ref _xpLevels, 1, 50);

        Dictionary<string, long> costs;
        lock (_xpCosts) costs = new Dictionary<string, long>(_xpCosts, StringComparer.OrdinalIgnoreCase);

        if (ImGui.BeginTable("##iltxp", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("Stat");
            ImGui.TableSetupColumn("Next level");
            ImGui.TableSetupColumn($"Next {_xpLevels}");
            ImGui.TableSetupColumn("Raise", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableHeadersRow();
            foreach (var s in Stats)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(s.Name);
                bool known = costs.TryGetValue(s.Name, out long next);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(known ? IltParse.Compact(next) : "-");
                ImGui.TableNextColumn();
                if (known)
                {
                    double growth = s.Attr != 0 ? 1.077 : 1.075;
                    double total = 0, c = next;
                    for (int i = 0; i < _xpLevels; i++) { total += c; c *= growth; }
                    ImGui.TextColored(total <= _unassignedXp ? LegacyDashboardRenderer.ColGreen : LegacyDashboardRenderer.ColTextDim,
                        "~" + IltParse.Compact(total));
                }
                else ImGui.TextUnformatted("-");
                ImGui.TableNextColumn();
                var stat = s;
                if (ImGui.SmallButton($"+1##{s.Abbr}"))
                    _ctx.Confirm("Raise " + s.Name, $"Spend XP to raise {s.Name} by 1?", () => RaiseStat(stat.Abbr, 1), "Raise");
                ImGui.SameLine();
                if (ImGui.SmallButton($"+10##{s.Abbr}"))
                    _ctx.Confirm("Raise " + s.Name, $"Spend XP to raise {s.Name} by 10?", () => RaiseStat(stat.Abbr, 10), "Raise");
            }
            ImGui.EndTable();
        }
        ImGui.TextDisabled("Estimates grow each level by 7.7% (attributes) / 7.5% (vitals); the server's numbers win.");
    }
}
