using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

// The T11 Tracking tab (Infinite Leaftide only, 2026-11-11, Silentkelpie: "add a new tab for ilt in oracle called T11
// tracking, move all tracking of t11 items to that tab include bounties, infusion tracking, charms gems, and school gems").
// Item bounties (the R1 R2 B1 B2 slots and the bounty items carried), the levelled charms and the Charm Infusion Gems, the
// school gems, the lum tokens the bank takes, and the T11 gear the Gear tab's Check read (its T11 modifier lines moved here
// from the Gear tab). The data: Data/T11Tracking.cs (T11Scanner), read by the plugin while this tab is showing.
internal sealed partial class OracleView
{
    public const string TabT11 = "T11 Tracking";
    private static readonly uint T11Gold = UiFormat.Rgb(230, 190, 90);
    private string _t11Msg = "";

    private RefreshResult RefreshT11()
    {
        _c.T11.ForgetAsked();
        _c.RescanT11();
        bool sent = _c.Send("/bounty sync");
        StartGearCheck();
        return new RefreshResult(sent
            ? "Re-read your packs, asked the server for your bounty slots (/bounty sync) and checked your gear."
            : "Re-read your packs and checked your gear.");
    }

    private void DrawT11(UiWindow w)
    {
        T11Scanner t = _c.T11;
        if (_t11Msg.Length > 0) w.TextColored(UiColors.Yellow, _t11Msg);
        DrawT11Bounties(w, t);
        DrawT11Charms(w, t);
        DrawT11SchoolGems(w, t);
        DrawT11Tokens(w, t);
        DrawT11Gear(w);
    }

    // ── Item bounties ────────────────────────────────────────────────────

    private void DrawT11Bounties(UiWindow w, T11Scanner t)
    {
        int active = t.Slots.Count(s => !s.Empty);
        if (!w.CollapsingHeader($"Item bounties ({active} active, {t.Bounties.Count} carried)", "t11.h.bounty", defaultOpen: true)) return;
        w.SmallButton("Sync", "t11.b.sync", () => _t11Msg = _c.Send("/bounty sync") ? "Asked the server for your bounty slots." : "Log in first.");
        w.Tooltip("/bounty sync: the server sends your slots again (no chat).");
        w.SameLine();
        w.SmallButton("/bounty", "t11.b.list", () => _c.Send("/bounty"));
        w.Tooltip("The server's text view: the kill bounties where you stand, then your slots.");
        w.SameLine();
        w.SmallButton("Kill bounties", "t11.b.panel", () => _t11Msg = _c.RunCommand("/rc bounties show") ? "" : "RynthCore's Bounties panel isn't there (it needs RynthILT).");
        w.Tooltip("RynthCore's Bounties panel: every kill bounty on the server and your progress on each.");

        if (t.Slots.Count == 0)
            w.TextDisabled(t.BountyText.Length == 0
                ? "No bounty slots from the server yet (Sync asks for them; the server needs item bounties on)."
                : "The server's bounty format isn't one this RynthOracle reads (update RynthOracle).");
        foreach (ItemBountySlot s in t.Slots)
        {
            w.TextColored(UiColors.Gold, s.Key);
            w.SameLine(40f);
            if (s.Empty)
            {
                w.TextDisabled(s.Key.StartsWith("R", StringComparison.Ordinal) ? "empty: use a bounty on a ring to start it here" : "empty: use a bounty on a bracelet to start it here");
                continue;
            }
            w.Text(Cut(s.Name, 34));
            w.Tooltip(s.Name + "\n" + s.Task + (s.Reward.Length > 0 ? "\nReward: " + s.Reward : "") + (s.RewardUse.Length > 0 ? "\n" + s.RewardUse : ""));
            w.SameLine(300f);
            string count = s.Kills > 0
                ? $"{s.Progress.ToString("0.##", CultureInfo.InvariantCulture)} / {s.Kills:N0}"
                : $"{s.Progress.ToString("0.##", CultureInfo.InvariantCulture)} / ?";
            w.ProgressBar(s.Fraction, 170f, 0f, count);
            w.SameLine();
            w.TextDisabled((s.Species && s.Target.Length > 0 ? s.Target + ", " : "") + (s.PlaceShort.Length > 0 ? s.PlaceShort : "T" + s.Tier));
            if (s.Reward.Length > 0)
            {
                w.Dummy(1f, 0f);
                w.SameLine(40f);
                w.TextDisabled("Reward: " + s.Reward + (s.Kills > 0 && s.Left > 0 ? $"  |  {s.Left.ToString("N0", CultureInfo.InvariantCulture)} kills to go" : ""));
            }
        }
        if (t.Bounties.Count > 0)
        {
            w.Spacing();
            w.TextColored(T11Gold, "Carried, not started (use one on a ring or bracelet):");
            foreach (T11Item b in t.Bounties)
            {
                w.BulletText(b.Stack > 1 ? $"{b.Name} x{b.Stack}" : b.Name);
                w.Tooltip(b.Desc);
            }
        }
    }

    // ── Levelled charms and infusion ─────────────────────────────────────

    private void DrawT11Charms(UiWindow w, T11Scanner t)
    {
        long coins = t.InfusionCoins;
        if (!w.CollapsingHeader($"Levelled charms and infusion ({t.Charms.Count} charms, {coins:N0} coins in gems)", "t11.h.charms", defaultOpen: true)) return;
        w.TextDisabled("Level 1-100, T11+ only (held to the zone's cap). A Charm Infusion Gem used on a charm puts its coins in.");
        if (t.Charms.Count == 0)
            w.TextDisabled("No levelled charm carried (Split Arrow, Chain, Pandemic, Improved Cleave, Armor Breaker, Shield Arc).");
        foreach (LevelledCharmRow c in t.Charms)
        {
            w.TextColored(c.Active ? UiColors.Green : UiColors.Grey, c.Active ? "ON " : "OFF");
            w.SameLine(40f);
            w.Text(c.Name);
            w.SameLine(170f);
            if (!c.Known)
            {
                w.TextDisabled("reading its level...");
                continue;
            }
            float frac = c.Level >= 100 ? 1f : c.PercentToNext / 100f;
            w.ProgressBar(frac, 150f, 0f, c.Level >= 100 ? "Level 100 (max)" : $"Level {c.Level} ({c.PercentToNext}%)");
            w.SameLine();
            w.TextDisabled($"{c.CoinsIn:N0} coins in" + (c.Level < 100 && c.CoinsToNext > 0 ? $", {c.CoinsToNext:N0} to level {c.Level + 1}" : ""));
            if (c.Level < 100 && c.CoinsToNext > 0 && coins > 0)
                w.Tooltip(coins >= c.CoinsToNext
                    ? $"Your gems hold {coins:N0} coins: enough for level {c.Level + 1}."
                    : $"Your gems hold {coins:N0} coins: {c.CoinsToNext - coins:N0} short of level {c.Level + 1}.");
            uint id = c.Id;
            w.SameLine();
            w.SmallButton("Toggle", "t11.c.t." + id, () => _t11Msg = _c.IltData.ToggleCharm(_c.Host, _c.NowMs, id) ? "" : "RynthILT didn't toggle it (update RynthILT, or the charm moved).");
        }
        w.Spacing();
        if (t.Infusion.Count == 0)
        {
            w.TextDisabled("No Charm Infusion Gems carried (Enephior sells them 1:1 for Prestige Coins).");
            return;
        }
        w.TextColored(T11Gold, $"Charm Infusion Gems: {coins:N0} coins");
        w.Text(string.Join("   ", t.Infusion.Select(g =>
            $"({T11Tracking.InfusionGems[g.Wcid]:N0}) x{g.Stack}")));
    }

    // ── School gems ──────────────────────────────────────────────────────

    private void DrawT11SchoolGems(UiWindow w, T11Scanner t)
    {
        if (!w.CollapsingHeader($"School gems ({t.SchoolGems.Sum(g => g.Stack)} carried)", "t11.h.school", defaultOpen: true)) return;
        w.TextDisabled("T15-T25: +1 to a school past the 4,000 cap, up to the tier's ceiling (4,500 at T15 ... 9,500 at T25). Only the earner can use one.");
        foreach (var (wcid, school, kind) in T11Tracking.SchoolGems)
        {
            var mine = t.SchoolGems.Where(g => g.Wcid == wcid).ToList();
            w.TextColored(mine.Count > 0 ? UiColors.Gold : UiColors.Grey, $"{school} ({kind})");
            w.SameLine(220f);
            if (mine.Count == 0) { w.TextDisabled("none"); continue; }
            var byTier = mine.GroupBy(g => g.GemTier).OrderBy(g => g.Key)
                .Select(g => (g.Key > 0 ? "T" + g.Key : "tier ?") + " x" + g.Sum(x => x.Stack));
            w.Text(string.Join("   ", byTier));
            string earned = mine.Select(g => EarnedBy(g.Desc + "\n" + g.Use)).FirstOrDefault(s => s.Length > 0) ?? "";
            if (earned.Length > 0) w.Tooltip(earned);
        }
    }

    /// <summary>"Earned by Silentkelpie at T16" out of a school gem's text, or "".</summary>
    private static string EarnedBy(string text)
    {
        int i = text.IndexOf("Earned by", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "";
        int end = text.IndexOfAny(new[] { '\n', '\r' }, i);
        return (end < 0 ? text.Substring(i) : text.Substring(i, end - i)).Trim();
    }

    // ── Lum tokens ───────────────────────────────────────────────────────

    private void DrawT11Tokens(UiWindow w, T11Scanner t)
    {
        int n = t.LumTokens.Sum(g => g.Stack);
        if (!w.CollapsingHeader($"Lum tokens ({n} carried)", "t11.h.tokens", defaultOpen: n > 0)) return;
        if (n == 0)
        {
            w.TextDisabled("No Bounty Reward Tokens or Event Lum Tokens carried.");
            return;
        }
        foreach (var g in t.LumTokens.GroupBy(x => x.Name).OrderBy(g => g.Key))
            w.BulletText($"{g.Key} x{g.Sum(x => x.Stack)}");
        w.SmallButton("Bank them", "t11.tok.bank", () => _t11Msg = _c.Send("/b lum") ? "Sent /b lum: the tokens go to your banked luminance." : "Log in first.");
        w.Tooltip("/b lum: every luminance token in your packs is banked as luminance (a double click banks one).");
    }

    // ── T11 gear (moved from the Gear tab) ───────────────────────────────

    private void DrawT11Gear(UiWindow w)
    {
        RatingsReport? r = _c.Gear.Ratings;
        var rows = r == null ? new List<RatingRow>() : r.Rows.Where(x => x.T11.Length > 0).ToList();
        if (!w.CollapsingHeader($"T11 gear ({rows.Count} item(s))", "t11.h.gear", defaultOpen: true)) return;
        w.SmallButton(_c.Gear.Busy ? "Checking..." : "Check gear", "t11.g.check", StartGearCheck);
        w.Tooltip("The Gear tab's Check: worn items (and packs, when its option is on) are appraised, then their T11 modifiers listed here.");
        if (r == null)
        {
            w.TextDisabled("Press Check gear to read your T11 gear's modifiers.");
            return;
        }
        if (r.T11Totals.Count > 0)
            w.TextColored(T11Teal, "T11 worn: " + string.Join("  |  ", r.T11Totals.Select(x => x.Name + " " + x.Value)));
        if (rows.Count == 0)
        {
            w.TextDisabled("No T11 gear read (worn" + (r.IncludePack ? " or in your packs" : "; the Gear tab's \"Include pack items\" adds your packs") + ").");
            return;
        }
        foreach (RatingRow row in rows.OrderByDescending(x => x.Piece.Equipped))
        {
            GearPiece p = row.Piece;
            w.TextColored(p.Equipped ? WornGreen : PackGrey, p.Equipped ? "Worn" : "Pack");
            w.SameLine(45f);
            w.TextColored(p.ZoneLocked ? UiColors.Yellow : UiFormat.Rgb(230, 230, 230), Cut(p.Name, 30));
            w.Tooltip(p.Name + (p.ZoneLocked ? "\nZone locked: power reduced outside its zone." : ""));
            w.SameLine(260f);
            w.TextWrapped(row.T11);
        }
    }
}
