// IltSessionRates.cs — Session earnings / per-hour rates for the ILT Hub Character tab.
//
// Sources (all passive — nothing is sent to the server except the optional bank refresh
// the Banking tab already does):
//   XP         player quad 1 (TotalExperience) delta since session start.
//   Luminance  "You've banked N Luminance" chat lines (ILT overflow-to-bank message).
//   Coins      positive bank Enlightened Coins deltas + carried coin (WCID 300004) gains.
//   Pyreals    positive bank pyreal deltas + carried pyreal (WCID 273) gains; MMD = 250k.
//   Items      gains of each item in the item→coin conversion table (carried count),
//              converted to potential coins with ItemsPerConversion / CoinsPerConversion.
//   Kills      RynthAiPlugin.OnKillNotification.
using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi.IltHub;

internal sealed class IltSessionRates : IIltFeature
{
    private const long PyrealsPerMmd = 250_000;
    private const long SampleIntervalMs = 5000;

    private readonly IltHubContext _ctx;
    private DateTime _start = DateTime.UtcNow;

    private long _startXp = -1;
    private long _xp;
    private long _lum;
    private long _coins;
    private long _pyreals;
    private int _kills;

    // Carried-item baselines (last seen counts) for gain tracking.
    private long _lastInvCoins = -1;
    private long _lastInvPyreals = -1;
    private readonly Dictionary<string, long> _lastItemCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _itemGains = new(StringComparer.OrdinalIgnoreCase);
    private long _lastSampleAt;

    // Render snapshot.
    private volatile RateRow[] _rows = Array.Empty<RateRow>();
    private sealed record RateRow(string Label, string Total, string PerHour);

    /// <summary>Per-hour figures for the Mini Remote HUD (rebuilt every sample).</summary>
    public sealed record RateSummary(double XpPerHour, double LumPerHour, double KillsPerHour,
                                     double CoinsPerHour, double PyrealsPerHour, long Xp, int Kills);
    private static readonly RateSummary EmptySummary = new(0, 0, 0, 0, 0, 0, 0);
    private volatile RateSummary _summary = EmptySummary;

    /// <summary>Latest per-hour summary (safe from the render thread).</summary>
    public RateSummary Summary => _summary;
    /// <summary>When the current session (or the last Reset) started.</summary>
    public DateTime SessionStartUtc => _start;

    public IltSessionRates(IltHubContext ctx) => _ctx = ctx;

    /// <summary>Banking hook: bank balance changed (pump thread).</summary>
    public void OnBankBalanceChanged(string key, long oldValue, long newValue)
    {
        RynthLog.Trace(LogCat.IltRates, $"balance {key}: {oldValue} -> {newValue}");
        // The first read after login only establishes the baseline (old value is the stale cache).
        if (_lastSampleAt == 0) return;
        long delta = newValue - oldValue;
        if (delta <= 0) return; // spending / sending isn't income
        if (key == "EnlightenedCoins") _coins += delta;
        else if (key == "Pyreals") _pyreals += delta;
    }

    public void RecordKill() => _kills++;

    public void Reset()
    {
        RynthLog.Trace(LogCat.IltRates, $"Reset()");
        _start = DateTime.UtcNow;
        _startXp = -1;
        _xp = _lum = _coins = _pyreals = 0;
        _kills = 0;
        _lastInvCoins = _lastInvPyreals = -1;
        _lastItemCounts.Clear();
        _itemGains.Clear();
        _lastSampleAt = 0;
        _summary = EmptySummary;
    }

    // ── IIltFeature ─────────────────────────────────────────────────────────

    public void Tick(long nowMs)
    {
        if (nowMs - _lastSampleAt < SampleIntervalMs) return;
        _lastSampleAt = nowMs;
        var inv = _ctx.Inventory;

        long totalXp = inv.PlayerQuad(IltInventory.QuadTotalXp);
        if (totalXp > 0)
        {
            if (_startXp < 0) _startXp = totalXp;
            _xp = Math.Max(0, totalXp - _startXp);
        }

        _coins += Gain(ref _lastInvCoins, inv.CountByWcid(IltInventory.WcidEnlightenedCoin));
        _pyreals += Gain(ref _lastInvPyreals, inv.CountByWcid(IltInventory.WcidPyreal));

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

    public bool OnChat(string text)
    {
        var m = IltParse.LumBanked.Match(text);
        if (m.Success) _lum += IltParse.ParseLeadingLong(m.Groups["amt"].Value);
        return false;
    }

    public void OnLogout() => Reset();

    // ── Rows ────────────────────────────────────────────────────────────────

    private void BuildRows()
    {
        double hours = Math.Max((DateTime.UtcNow - _start).TotalHours, 1.0 / 3600);
        _summary = new RateSummary(_xp / hours, _lum / hours, _kills / hours, _coins / hours, _pyreals / hours, _xp, _kills);
        var rows = new List<RateRow>
        {
            Row("XP", _xp, hours),
            Row("Luminance banked", _lum, hours),
            Row("Enlightened Coins", _coins, hours),
            Row("Pyreals", _pyreals, hours),
            new("  as MMD", (_pyreals / (double)PyrealsPerMmd).ToString("0.0"), (_pyreals / (double)PyrealsPerMmd / hours).ToString("0.0")),
            new("Kills", _kills.ToString(), (_kills / hours).ToString("0")),
        };

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

    private static RateRow Row(string label, long total, double hours)
        => new(label, IltParse.Compact(total), IltParse.Compact(total / hours));

    // ── UI (render thread) ──────────────────────────────────────────────────

    public void Render()
    {
        var elapsed = DateTime.UtcNow - _start;
        ImGui.TextDisabled($"Session {IltParse.Duration(elapsed)}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Reset##rates")) _ctx.Post(Reset);

        if (ImGui.BeginTable("##iltrates", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("Metric");
            ImGui.TableSetupColumn("Session");
            ImGui.TableSetupColumn("Per hour");
            ImGui.TableHeadersRow();
            foreach (var r in _rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Label);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Total);
                ImGui.TableNextColumn(); ImGui.TextColored(LegacyDashboardRenderer.ColTeal, r.PerHour);
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
