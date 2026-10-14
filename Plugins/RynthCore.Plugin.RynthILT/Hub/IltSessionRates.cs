// IltSessionRates.cs — Session earnings / per-hour rates for Infinite Leaftide: the coins, pyreals and
// item conversions RynthCore's Mini Remote "Session Stats" section shows under "Server: Infinite
// Leaftide" (surface "section:ilt-rates", 2026-10-13; it was the ILT Character tab before).
// Originally SilentKelpie's ILT Hub (RynthAi IltHub/); moved to RynthILT 2026-10-13.
//
// Sources (all passive — nothing is sent to the server except the optional bank refresh
// the Banking tab already does):
//   XP, luminance (total, last 5 min, allegiance pass-up) and kills: RynthAi's Mini Remote
//              session stats (RynthAi.Hub SessionStats) — RynthTracker's SessionStats code, so this
//              table, the Mini Remote and the Tracker panel count them the same way (2026-10-11).
//              Without RynthAi those rows are left out.
//   Coins      positive bank Enlightened Coins deltas + carried coin (WCID 300004) gains.
//   Pyreals    positive bank pyreal deltas + carried pyreal (WCID 273) gains; MMD = 250k.
//   Items      gains of each item in the item→coin conversion table (carried count),
//              converted to potential coins with ItemsPerConversion / CoinsPerConversion.
// A deposit or withdrawal only moves money between pack and bank: while Banking reports one
// (MovingMoney) the coin and pyreal baselines follow along without counting a gain.
using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthILT.Hub;

internal sealed class IltSessionRates : IIltFeature
{
    private const long PyrealsPerMmd = 250_000;
    private const long SampleIntervalMs = 5000;
    private const long RowsIntervalMs = 1000;
    private const string PassUpLabel = "  pass-up";

    private readonly IltHubContext _ctx;
    private DateTime _start = DateTime.UtcNow;

    private long _coins;
    private long _pyreals;

    // Carried-item baselines (last seen counts) for gain tracking.
    private long _lastInvCoins = -1;
    private long _lastInvPyreals = -1;
    private readonly Dictionary<string, long> _lastItemCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _itemGains = new(StringComparer.OrdinalIgnoreCase);
    private long _lastSampleAt;
    private long _lastRowsAt;

    // Render snapshot.
    private volatile RateRow[] _rows = Array.Empty<RateRow>();
    // Shared = one of RynthAi's session stats rows (XP, luminance, kills), which the Session Stats
    // section already shows above this table; the embedded view leaves those out.
    private sealed record RateRow(string Label, string Total, string PerHour, bool Shared = false);

    /// <summary>Coin and pyreal per-hour figures for the Mini Remote HUD (rebuilt every sample).</summary>
    public sealed record RateSummary(double CoinsPerHour, double PyrealsPerHour);
    private static readonly RateSummary EmptySummary = new(0, 0);
    private volatile RateSummary _summary = EmptySummary;

    /// <summary>Latest coin / pyreal summary (safe from the render thread).</summary>
    public RateSummary Summary => _summary;
    /// <summary>When the coin / pyreal session (or the last Reset) started.</summary>
    public DateTime SessionStartUtc => _start;

    public IltSessionRates(IltHubContext ctx) => _ctx = ctx;

    /// <summary>True while money moves between pack and bank (set by the controller from Banking). Pump thread.</summary>
    public Func<bool>? MovingMoney { get; set; }

    private bool Moving => MovingMoney?.Invoke() ?? false;

    /// <summary>Banking hook: bank balance changed (pump thread).</summary>
    public void OnBankBalanceChanged(string key, long oldValue, long newValue)
    {
        RynthLog.Trace(LogCat.IltRates, $"balance {key}: {oldValue} -> {newValue}");
        // The first read after login only establishes the baseline (old value is the stale cache).
        if (_lastSampleAt == 0) return;
        if (Moving) return;   // a deposit, not income
        long delta = newValue - oldValue;
        if (delta <= 0) return; // spending / sending isn't income
        if (key == "EnlightenedCoins") _coins += delta;
        else if (key == "Pyreals") _pyreals += delta;
    }

    /// <summary>Coins, pyreals and item gains start over (login, or with <see cref="ResetAll"/>).</summary>
    public void Reset()
    {
        RynthLog.Trace(LogCat.IltRates, $"Reset()");
        _start = DateTime.UtcNow;
        _coins = _pyreals = 0;
        _lastInvCoins = _lastInvPyreals = -1;
        _lastItemCounts.Clear();
        _itemGains.Clear();
        _lastSampleAt = 0;
        _lastRowsAt = 0;
        _summary = EmptySummary;
    }

    /// <summary>The player's reset (this tab, the Mini Remote): coins here and the shared session stats together. Pump thread.</summary>
    public void ResetAll()
    {
        Reset();
        _ctx.Ai.ResetSessionStats();
    }

    // ── IIltFeature ─────────────────────────────────────────────────────────

    public void Tick(long nowMs)
    {
        // The shared stats move every 0.5 s; their rows refresh each second, coins every 5 s.
        if (nowMs - _lastSampleAt < SampleIntervalMs)
        {
            if (nowMs - _lastRowsAt >= RowsIntervalMs) { _lastRowsAt = nowMs; BuildRows(); }
            return;
        }
        _lastSampleAt = nowMs;
        _lastRowsAt = nowMs;
        var inv = _ctx.Inventory;

        long coinGain = Gain(ref _lastInvCoins, inv.CountByWcid(IltInventory.WcidEnlightenedCoin));
        long pyrealGain = Gain(ref _lastInvPyreals, inv.CountByWcid(IltInventory.WcidPyreal));
        // A withdrawal: the baselines moved, nothing earned.
        if (!Moving)
        {
            _coins += coinGain;
            _pyreals += pyrealGain;
        }

        // ToArray: the render thread edits this table.
        foreach (var conv in _ctx.State.Character.ItemConversions.ToArray())
        {
            if (string.IsNullOrWhiteSpace(conv.ItemName)) continue;
            long count = inv.CountByName(conv.ItemName);
            long last = _lastItemCounts.TryGetValue(conv.ItemName, out long l) ? l : -1;
            long gain = Gain(ref last, count);
            _lastItemCounts[conv.ItemName] = last;
            if (gain > 0) _itemGains[conv.ItemName] = (_itemGains.TryGetValue(conv.ItemName, out long g) ? g : 0) + gain;
        }

        BuildRows();
    }

    /// <summary>Positive change since the last sample; the first sample only sets the baseline.</summary>
    private static long Gain(ref long last, long current)
    {
        if (last < 0) { last = current; return 0; }
        long d = current - last;
        last = current;
        return d > 0 ? d : 0;
    }

    // Luminance chat goes to RynthAi's session stats (its OnChatWindowText).
    public bool OnChat(string text) => false;

    public void OnLogout() => Reset();

    // ── Rows ────────────────────────────────────────────────────────────────

    private void BuildRows()
    {
        double hours = Math.Max((DateTime.UtcNow - _start).TotalHours, 1.0 / 3600);
        _summary = new RateSummary(_coins / hours, _pyreals / hours);
        var rows = new List<RateRow>();
        if (_ctx.Ai.TryGetSessionStats(out SessionStatsV1 s))
        {
            rows.Add(new RateRow("XP", IltParse.Compact(s.Xp), IltParse.Compact(s.XpPerHour), Shared: true));
            rows.Add(new RateRow("Luminance", IltParse.Compact(s.Lum), IltParse.Compact(s.LumPerHour), Shared: true));
            // Per hour at the last five minutes' pace.
            rows.Add(new RateRow("  last 5 min", IltParse.Compact(s.LumLast5Min), IltParse.Compact(s.LumLast5Min * 12.0), Shared: true));
            if (s.PassUpTracked != 0)
                rows.Add(new RateRow(PassUpLabel, IltParse.Compact(s.LumPassUp), IltParse.Compact(s.PassUpPerHour), Shared: true));
            rows.Add(new RateRow("Kills", s.Kills.ToString(), s.KillsPerHour.ToString("0"), Shared: true));
        }
        rows.Add(new RateRow("Enlightened Coins", IltParse.Compact(_coins), IltParse.Compact(_coins / hours)));
        rows.Add(new RateRow("Pyreals", IltParse.Compact(_pyreals), IltParse.Compact(_pyreals / hours)));
        rows.Add(new RateRow("  as MMD", (_pyreals / (double)PyrealsPerMmd).ToString("0.0"), (_pyreals / (double)PyrealsPerMmd / hours).ToString("0.0")));

        double potentialCoins = 0;
        foreach (var conv in _ctx.State.Character.ItemConversions.ToArray())
        {
            if (!_itemGains.TryGetValue(conv.ItemName, out long gained) || gained == 0) continue;
            double coins = conv.ItemsPerConversion > 0 ? gained / (double)conv.ItemsPerConversion * conv.CoinsPerConversion : 0;
            potentialCoins += coins;
            rows.Add(new RateRow("  " + conv.ItemName, IltParse.N0(gained), IltParse.Compact(gained / hours)));
        }
        if (potentialCoins > 0)
            rows.Add(new RateRow("Potential coins (items)", potentialCoins.ToString("0"), (potentialCoins / hours).ToString("0.0")));
        _rows = rows.ToArray();
    }

    /// <summary>What the pass-up figure is (shared with the Mini Remote's Stats).</summary>
    public const string PassUpTooltip =
        "Luminance that reached your bank without a \"You've banked\" line: your vassals' pass-up.\n"
        + "Fellowship and Zone Share splits arrive the same silent way and are included.";

    // ── UI (render thread) ──────────────────────────────────────────────────

    /// <param name="extrasOnly">
    /// True in the Mini Remote's Session Stats section: RynthAi's XP / luminance / kills rows are
    /// drawn there already (one session), so only the ILT coins, pyreals and items are listed.
    /// </param>
    public void Render(bool extrasOnly = false)
    {
        // The session clock is RynthAi's session stats' (it and the coins reset together).
        var start = _ctx.Ai.TryGetSessionStats(out SessionStatsV1 v) && v.SessionSeconds > 0 && v.StartUtcTicks > 0
            ? new DateTime(v.StartUtcTicks, DateTimeKind.Utc) : _start;
        SafeImGui.TextDisabled($"Session {IltParse.Duration(DateTime.UtcNow - start)}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Reset##rates")) _ctx.Post(ResetAll);

        if (ImGui.BeginTable("##iltrates", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("Metric");
            ImGui.TableSetupColumn("Session");
            ImGui.TableSetupColumn("Per hour");
            ImGui.TableHeadersRow();
            foreach (var r in _rows)
            {
                if (extrasOnly && r.Shared) continue;
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Label);
                if (ReferenceEquals(r.Label, PassUpLabel) && ImGui.IsItemHovered()) SafeImGui.SetTooltip(PassUpTooltip);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Total);
                ImGui.TableNextColumn(); SafeImGui.TextColored(IltStyle.ColTeal, r.PerHour);
            }
            ImGui.EndTable();
        }

        if (ImGui.TreeNode("Item -> coin conversions"))
        {
            RenderConversionEditor();
            ImGui.TreePop();
        }
    }

    private string _newItemName = string.Empty;

    private void RenderConversionEditor()
    {
        var list = _ctx.State.Character.ItemConversions;
        int remove = -1;
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            ImGui.PushID(i);
            ImGui.TextUnformatted(c.ItemName);
            ImGui.SameLine(220);
            int items = c.ItemsPerConversion, coins = c.CoinsPerConversion;
            ImGui.SetNextItemWidth(70);
            if (ImGui.InputInt("items", ref items, 0)) c.ItemsPerConversion = Math.Max(1, items);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(70);
            if (ImGui.InputInt("coins", ref coins, 0)) c.CoinsPerConversion = Math.Max(0, coins);
            ImGui.SameLine();
            if (ImGui.SmallButton("x")) remove = i;
            ImGui.PopID();
        }
        if (remove >= 0)
        {
            var victim = list[remove];
            _ctx.Post(() => list.Remove(victim));
        }

        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##newconv", "exact item name", ref _newItemName, 96u);
        ImGui.SameLine();
        if (ImGui.SmallButton("Add") && _newItemName.Trim().Length > 0
            && !list.Any(c => c.ItemName.Equals(_newItemName.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            var added = new IltItemConversion { ItemName = _newItemName.Trim(), ItemsPerConversion = 100, CoinsPerConversion = 0 };
            _ctx.Post(() => list.Add(added));
            _newItemName = string.Empty;
        }
    }
}
