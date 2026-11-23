using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

// The ILT Tracker tab's quest bonus and wiki sections (Infinite Leaftide only, 2026-11-21, Silentkelpie: "look at
// acecustom add in itl qb bonus levels and bonus tracker under t11 tab, change t11 tab to itl tracker, add in the itl wiki
// how to to this tab as well"; the levels are ACECustom's QB Deeds: "should be called deeds based on total qb"). The T11
// sections stay in T11Tab.cs. Data: Data/QbDeeds.cs and RynthILT's /deeds read (IltDeedsInfo), Data/QuestBonus.cs
// (ACECustom's QB and /bonus), RynthILT's /qb list (IltQuestBonusList), Data/IltWikiGuide.cs.
internal sealed partial class OracleView
{
    private string _qbFilter = "";
    private bool _qbShowStamps = true;
    private const int QbRowsShown = 200;
    private int _qbAskedSession = -1, _bonusAskedSession = -1;

    // ── QB Deeds and the XP multipliers ──────────────────────────────────

    private int _deedsAskedSession = -1;

    private void DrawIltQuestBonus(UiWindow w)
    {
        IltQuestBonusList? ilt = _c.Ilt.QuestBonus;
        IltDeedsInfo? deeds = _c.Ilt.Deeds;
        QuestBonus qb = _c.QuestBonus;
        long count = qb.BestCount(ilt, out string from, deeds);
        int tier = deeds is { Loaded: true } ? deeds.Unlocked : count >= 0 ? QbDeeds.TierFor(count) : 0;
        string head = count >= 0
            ? $"QB Deeds ({TimeText.Number(count)} QB, tier {tier} of {QbDeeds.MaxTier}"
              + (deeds?.On == false ? ", OFF" : "") + ")"
            : "QB Deeds";
        if (!w.CollapsingHeader(head, "ilt.h.qb", defaultOpen: true)) return;

        // Once a session: /bonus from the server, the /deeds list and the /qb list from RynthILT (the server allows /qb list
        // once a minute; RynthILT reads /deeds quietly).
        if (_c.InWorld && _bonusAskedSession != qb.Session) { _bonusAskedSession = qb.Session; _c.Send("/bonus"); }
        if (_c.InWorld && _deedsAskedSession != qb.Session && deeds != null && !deeds.Loaded && !deeds.Busy && !deeds.Unsupported)
        {
            _deedsAskedSession = qb.Session;
            _c.Send("/ilt deeds refresh");
        }
        if (_c.InWorld && _qbAskedSession != qb.Session && ilt != null && !ilt.Disabled && ilt.Generation == 0)
        {
            _qbAskedSession = qb.Session;
            _c.Send("/ilt qb");
        }

        w.SmallButton(deeds?.Busy == true ? "Reading..." : "Read /deeds", "ilt.dd.read", () =>
            _t11Msg = _c.Send("/ilt deeds refresh") ? "RynthILT is reading your Deeds (/deeds list)." : "Log in first.");
        w.Tooltip("RynthILT reads /deeds list quietly: your QB, Deeds on or off, and every tier as the server tells it.");
        w.SameLine();
        if (deeds?.On == false)
            w.SmallButton("Deeds on", "ilt.dd.on", () => _c.Send("/ilt deeds on"));
        else
            w.SmallButton("Deeds off", "ilt.dd.off", () => _c.Send("/ilt deeds off"));
        w.Tooltip("/deeds on | off: the Deeds bonuses for this character (the server says which in chat).");
        w.SameLine();
        if (tier >= QbDeeds.RebuffTier)
        {
            w.SmallButton("/rebuff", "ilt.dd.rebuff", () => _c.Send("/ilt deeds rebuff"));
            w.Tooltip("Tier 10: casts all your known level 8 buffs at once.");
            w.SameLine();
            w.SmallButton("Writ", "ilt.dd.writ", () => _c.Send("/ilt deeds item"));
            w.Tooltip("/deeds item: a Deedkeeper's Writ that rebuffs you when used.");
            w.SameLine();
        }
        w.SmallButton("Deeds window", "ilt.dd.win", () => _t11Msg = _c.RunCommand("/ilt deeds show") || _c.Send("/ilt deeds show") ? "" : "RynthILT isn't loaded.");
        w.Tooltip("RynthILT's QB Deeds window (/ilt deeds).");

        if (deeds?.Unsupported == true) w.TextDisabled("This server has no /deeds.");
        else if (deeds == null) w.TextDisabled("RynthILT isn't loaded (or is older): the tiers below are ACECustom's table, at the default settings.");
        else if (deeds.Status.Length > 0) w.TextDisabled(deeds.Status);

        if (count < 0)
            w.TextDisabled("No QB yet: press Read /deeds or /bonus.");
        else
        {
            var (next, frac, toNext) = QbDeeds.Progress(count);
            if (next == 0) w.TextColored(UiColors.Green, $"Every Deeds tier is unlocked ({TimeText.Number(count)} QB).");
            else
            {
                long nextQb = deeds is { Loaded: true, NextAt: > 0 } ? deeds.NextAt : (long)next * QbDeeds.QbPerTier;
                w.TextColored(T11Gold, $"Tier {tier}");
                w.SameLine(70f);
                w.ProgressBar(frac, 220f, 0f, $"{TimeText.Number(count)} / {TimeText.Number(nextQb)} QB");
                w.SameLine();
                w.TextDisabled($"{TimeText.Number(Math.Max(0, nextQb - count))} QB to tier {next}");
            }
            w.Tooltip("Deeds: a tier every 1,000 account QB, 15 in all, each adding to the ones before (ACECustom QB Deeds).\n"
                      + $"QB from: {from}. Your QB also gives x{QuestBonus.Multiplier(count):0.000} kill XP (1 + QB x 0.005).");
        }
        DrawDeedTiers(w, deeds, tier);

        BonusReading b = qb.Bonus;
        w.Spacing();
        if (!b.Seen)
        {
            w.TextDisabled("XP multipliers: press /bonus to read them.");
            w.SameLine();
            w.SmallButton("/bonus", "ilt.qb.bonus", () => _t11Msg = _c.Send("/bonus") ? "Asked the server for your XP multipliers (/bonus)." : "Log in first.");
            return;
        }
        w.TextColored(UiColors.Gold, "XP multipliers (/bonus " + b.AtUtc.ToLocalTime().ToString("HH:mm") + ")");
        w.SameLine();
        w.SmallButton("/bonus", "ilt.qb.bonus", () => _t11Msg = _c.Send("/bonus") ? "Asked the server for your XP multipliers (/bonus)." : "Log in first.");
        w.Tooltip("/bonus: the server prints your XP multipliers from quests, equipment and enlightenment, and the total.");
        w.SameLine();
        w.SmallButton("/top qb", "ilt.qb.top", () => _c.Send("/top qb"));
        w.Tooltip("The server's top 25 players by quest bonus, in chat.");
        BonusRow(w, "Quests", b.Quests, "1 + QB x 0.005" + (b.Quests > 0 ? $" (about {TimeText.Number(QuestBonus.QbFromMultiplier(b.Quests))} QB)" : ""));
        BonusRow(w, "Equipment", b.Equipment, "1 + your XP enchantments + 5% for each Bonus XP augmentation");
        BonusRow(w, "Enlightenment", b.Enlightenment, "1 + enlightenments x 0.1"
                 + (b.Enlightenment > 1 ? $" ({Math.Round((b.Enlightenment - 1) / QuestBonus.PerEnlightenment):0} ENL)" : ""));
        BonusRow(w, "Total", b.Total, "Quests x Equipment x Enlightenment (kill XP; the server's own XP rate comes on top)");
    }

    /// <summary>
    /// The 15 Deeds tiers: unlocked ones green, the next one gold, the rest grey. Each tier's text is the server's own (RynthILT's
    /// /deeds list) when it is in, else ACECustom's table at the default settings.
    /// </summary>
    private void DrawDeedTiers(UiWindow w, IltDeedsInfo? deeds, int tier)
    {
        var live = new Dictionary<int, IltDeedTier>();
        if (deeds is { Loaded: true })
            foreach (IltDeedTier t in deeds.Tiers) live[t.Number] = t;
        foreach (QbDeedTier t in QbDeeds.Tiers)
        {
            bool have = live.TryGetValue(t.Number, out IltDeedTier? lt) ? lt.Unlocked : t.Number <= tier;
            bool next = !have && t.Number == tier + 1;
            uint color = have ? UiColors.Green : next ? T11Gold : UiColors.Grey;
            w.TextColored(color, $"T{t.Number}");
            w.SameLine(40f);
            w.TextColored(color, TimeText.Number(t.Qb) + " QB");
            w.SameLine(115f);
            if (lt != null && lt.Rewards.Count > 0)
            {
                string text = string.Join(", ", lt.Rewards);
                w.TextWrapped(text.Length > 260 ? text.Substring(0, 260) + "..." : text);
                if (text.Length > 260) w.Tooltip(string.Join("\n", lt.Rewards));
            }
            else w.TextWrapped(QbDeeds.Line(t));
        }
        if (deeds is not { Loaded: true })
            w.TextDisabled("The tier text is ACECustom's table at the server's default settings; Read /deeds shows the server's own.");
    }

    private static void BonusRow(UiWindow w, string name, double value, string how)
    {
        w.Text(name);
        w.SameLine(120f);
        w.TextColored(name == "Total" ? UiColors.Green : UiFormat.Rgb(230, 230, 230), $"x{value:0.00}");
        w.SameLine(190f);
        w.TextDisabled(how);
    }

    // ── Quest bonus tracker ──────────────────────────────────────────────

    private void DrawIltBonusTracker(UiWindow w)
    {
        IltQuestBonusList? ilt = _c.Ilt.QuestBonus;
        bool loaded = ilt != null && ilt.Generation > 0;
        string head = loaded
            ? $"Quest bonus tracker ({TimeText.Number(ilt!.Completed.Count)} done, {TimeText.Number(ilt.Incomplete.Count)} to finish)"
            : "Quest bonus tracker";
        if (!w.CollapsingHeader(head, "ilt.h.qbt", defaultOpen: true)) return;
        if (ilt == null)
        {
            w.TextDisabled("RynthILT isn't loaded: it reads the /qb list for this tracker.");
            return;
        }
        if (ilt.Disabled)
        {
            w.TextDisabled("/qb is turned off on this server.");
            return;
        }
        if (!loaded)
        {
            w.TextDisabled("The /qb list isn't read this session. " + ilt.Status);
            w.SameLine();
            w.SmallButton("Read /qb list", "ilt.qb.read", () => _t11Msg = _c.Send("/ilt qb") ? "RynthILT is reading your quest bonus list." : "Log in first.");
            w.Tooltip("RynthILT reads /qb list: every quest that counts towards your QB, done or not (the server allows it once a minute).");
            return;
        }

        int doneStamps = ilt.Completed.Count(QuestBonus.IsStamp);
        long qbNow = ilt.Count >= 0 ? ilt.Count : ilt.Completed.Count;
        long qbAll = qbNow + ilt.Incomplete.Count;
        w.TextDisabled($"Per account: a quest is 1 QB once any of your characters starts it and 2 once one finishes it. "
                       + $"{TimeText.Number(doneStamps)} of the done ones are Dynamic stamps.");
        w.SameLine();
        w.SmallButton("Read /qb list", "ilt.qb.read", () => _t11Msg = _c.Send("/ilt qb") ? "RynthILT is reading your quest bonus list." : "Log in first.");
        w.Tooltip("RynthILT reads /qb list again (the server allows it once a minute).");
        if (ilt.Incomplete.Count > 0)
            w.TextColored(T11Gold, $"Finish the {TimeText.Number(ilt.Incomplete.Count)} started ones: {TimeText.Number(qbAll)} QB, "
                                   + $"Deeds tier {QbDeeds.TierFor(qbAll)} of {QbDeeds.MaxTier}.");

        w.InputText("Filter", "ilt.qbt.filter", _qbFilter, 64, s => _qbFilter = s);
        w.SameLine();
        w.Checkbox("Dynamic stamps", "ilt.qbt.stamps", _qbShowStamps, v => _qbShowStamps = v);
        w.Tooltip("List the Dynamic stamp flags too (the Quests tab's Dynamic Stamps rows track them by area).");

        QbList(w, "To finish", "todo", ilt.Incomplete, UiColors.Yellow, defaultOpen: true,
               "Started on this account, not finished yet: finishing each is +1 QB (+0.5% XP, and towards the next Deeds tier).");
        QbList(w, "Done", "done", ilt.Completed, UiColors.Green, defaultOpen: false, "Counted in your QB.");
    }

    private void QbList(UiWindow w, string label, string id, IReadOnlyList<string> flags, uint color, bool defaultOpen, string help)
    {
        string f = _qbFilter.Trim();
        List<string> rows = flags.Where(x => (_qbShowStamps || !QuestBonus.IsStamp(x))
                                             && (f.Length == 0 || x.Contains(f, StringComparison.OrdinalIgnoreCase))).ToList();
        if (!w.CollapsingHeader($"{label} ({TimeText.Number(rows.Count)})", "ilt.qbt." + id, defaultOpen)) return;
        w.TextDisabled(help);
        for (int i = 0; i < rows.Count && i < QbRowsShown; i++)
        {
            string flag = rows[i];
            w.TextColored(QuestBonus.IsStamp(flag) ? UiColors.Grey : color, Cut(flag, 60));
            GuideQuest? q = _c.FindWiki(flag);
            if (q != null)
            {
                w.SameLine();
                w.TextDisabled("(wiki: " + Cut(q.Title, 30) + ")");
            }
            if (flag.Length > 60) w.Tooltip(flag);
        }
        if (rows.Count > QbRowsShown)
            w.TextDisabled($"... and {TimeText.Number(rows.Count - QbRowsShown)} more: narrow them with the filter.");
    }

    // ── The ILT wiki: how to get its login ───────────────────────────────

    private string _wikiGuideMsg = "";

    private void DrawIltWikiGuide(UiWindow w)
    {
        QuestGuideClient g = _c.Guide;
        string state = !g.Available ? "RynthILT isn't serving it"
            : g.Running ? "reading..."
            : g.Guide.Quests.Count > 0 ? $"{TimeText.Number(g.Guide.Quests.Count)} quests read"
            : g.LoginSaved ? "login saved, not read yet" : "no login yet";
        bool open = !g.LoginSaved || g.Guide.Quests.Count == 0;
        if (!w.CollapsingHeader($"ILT wiki: how to get its login ({state})", "ilt.h.wiki", defaultOpen: open)) return;
        w.TextWrapped("Every page of leaftidewiki.com needs a login, so RynthILT reads the wiki with a bot password: a second, "
                      + "read-only password of your own wiki account. RynthOracle never sees it.");
        if (g.Status.Length > 0) w.TextDisabled("Wiki: " + g.Status);
        if (_wikiGuideMsg.Length > 0) w.TextColored(UiColors.Yellow, _wikiGuideMsg);

        for (int i = 0; i < IltWikiGuide.Steps.Length; i++)
        {
            IltWikiGuide.Step st = IltWikiGuide.Steps[i];
            w.TextColored(UiColors.Gold, $"{i + 1}. {st.Title}");
            if (st.Url.Length > 0)
            {
                w.SameLine();
                string url = st.Url;
                string title = st.Title;
                w.SmallButton("Open page", "ilt.wiki.open." + i, () =>
                    _wikiGuideMsg = IltWikiGuide.Open(url) ? "Opened in your browser: " + title + "." : "Couldn't open the browser: " + url);
                w.Tooltip(url);
            }
            w.TextWrapped(st.Text);
        }

        w.Spacing();
        w.SmallButton("RynthILT settings", "ilt.wiki.settings", () =>
            _wikiGuideMsg = g.Command("settings") || _c.Send("/ilt settings") ? "" : "RynthILT isn't loaded.");
        w.Tooltip("Opens /ilt settings, where the login goes (Quest guide).");
        w.SameLine();
        w.SmallButton(g.Running ? "Reading..." : "Read the wiki now", "ilt.wiki.read", () =>
        {
            if (g.Running) return;
            _wikiGuideMsg = !g.LoginSaved ? "Save the login in RynthILT's settings first."
                : g.Command("refresh") ? "RynthILT is reading the wiki." : "RynthILT isn't loaded.";
        });
        w.Tooltip("RynthILT reads the wiki again (only the pages edited since).");
        w.SameLine();
        w.SmallButton("/ilt wiki help", "ilt.wiki.help", () => _c.Send("/ilt wiki help"));
        w.Tooltip("The same steps in chat.");
    }
}
