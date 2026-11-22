using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

// The ILT Tracker tab's quest bonus and wiki sections (Infinite Leaftide only, 2026-11-21, Silentkelpie: "look at
// acecustom add in itl qb bonus levels and bonus tracker under t11 tab, change t11 tab to itl tracker, add in the itl wiki
// how to to this tab as well"). The T11 sections stay in T11Tab.cs. Data: Data/QuestBonus.cs (ACECustom's QB and /bonus),
// RynthILT's /qb list (IltQuestBonusList), Data/IltWikiGuide.cs.
internal sealed partial class OracleView
{
    private string _qbFilter = "";
    private bool _qbShowStamps = true;
    private const int QbRowsShown = 200;
    private int _qbAskedSession = -1, _bonusAskedSession = -1;

    // ── Quest bonus levels ───────────────────────────────────────────────

    private void DrawIltQuestBonus(UiWindow w)
    {
        IltQuestBonusList? ilt = _c.Ilt.QuestBonus;
        QuestBonus qb = _c.QuestBonus;
        long count = qb.BestCount(ilt, out string from);
        string head = count >= 0
            ? $"Quest bonus levels ({TimeText.Number(count)} QB, x{QuestBonus.Multiplier(count):0.000} XP from quests)"
            : "Quest bonus levels";
        if (!w.CollapsingHeader(head, "ilt.h.qb", defaultOpen: true)) return;

        // Once a session, ask the server for /bonus and RynthILT for the /qb list (the server allows /qb list once a minute).
        if (_c.InWorld && _bonusAskedSession != qb.Session) { _bonusAskedSession = qb.Session; _c.Send("/bonus"); }
        if (_c.InWorld && _qbAskedSession != qb.Session && ilt != null && !ilt.Disabled && ilt.Generation == 0)
        {
            _qbAskedSession = qb.Session;
            _c.Send("/ilt qb");
        }

        w.SmallButton("/bonus", "ilt.qb.bonus", () => _t11Msg = _c.Send("/bonus") ? "Asked the server for your XP multipliers (/bonus)." : "Log in first.");
        w.Tooltip("/bonus: the server prints your XP multipliers from quests, equipment and enlightenment, and the total.\nIt also brings your QB up to date on the server.");
        w.SameLine();
        w.SmallButton("Read /qb list", "ilt.qb.read", () => _t11Msg = _c.Send("/ilt qb") ? "RynthILT is reading your quest bonus list." : "Log in first.");
        w.Tooltip("RynthILT reads /qb list: every quest that counts towards your QB, done or not (the server allows it once a minute).");
        w.SameLine();
        w.SmallButton("/top qb", "ilt.qb.top", () => _c.Send("/top qb"));
        w.Tooltip("The server's top 25 players by quest bonus, in chat.");

        if (count < 0)
        {
            w.TextDisabled(ilt == null
                ? "No QB yet: RynthILT isn't loaded, so press /bonus (or type /qb) to read it from the server."
                : "No QB yet: reading /bonus and the /qb list...");
        }
        else
        {
            var (cur, next, frac, toNext) = QuestBonus.Levels(count);
            w.TextColored(T11Gold, $"Bonus level {cur.Level}");
            w.SameLine(110f);
            w.ProgressBar(frac, 220f, 0f, $"{TimeText.Number(count)} / {TimeText.Number(next[0].Qb)} QB");
            w.SameLine();
            w.TextDisabled($"{TimeText.Number(toNext)} QB to level {next[0].Level} (x{next[0].Multiplier:0.00})");
            w.Tooltip("Each QB is +0.5% kill XP (ACECustom: 1 + QB x 0.005). A bonus level is every x0.25 of it, "
                      + $"{QuestBonus.QbPerLevel} QB.\nQB from: {from}.");
            w.TextDisabled("Next levels: " + string.Join("   ", next.Select(l => $"L{l.Level} x{l.Multiplier:0.00} at {TimeText.Number(l.Qb)}")));
        }

        BonusReading b = qb.Bonus;
        if (!b.Seen)
        {
            w.TextDisabled("XP multipliers: press /bonus to read them.");
            return;
        }
        w.Spacing();
        w.TextColored(UiColors.Gold, "XP multipliers (/bonus " + b.AtUtc.ToLocalTime().ToString("HH:mm") + ")");
        BonusRow(w, "Quests", b.Quests, "1 + QB x 0.005" + (b.Quests > 0 ? $" (about {TimeText.Number(QuestBonus.QbFromMultiplier(b.Quests))} QB)" : ""));
        BonusRow(w, "Equipment", b.Equipment, "1 + your XP enchantments + 5% for each Bonus XP augmentation");
        BonusRow(w, "Enlightenment", b.Enlightenment, "1 + enlightenments x 0.1"
                 + (b.Enlightenment > 1 ? $" ({Math.Round((b.Enlightenment - 1) / QuestBonus.PerEnlightenment):0} ENL)" : ""));
        BonusRow(w, "Total", b.Total, "Quests x Equipment x Enlightenment (kill XP; the server's own XP rate comes on top)");
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
            w.TextDisabled("The /qb list isn't read this session: press Read /qb list above. " + ilt.Status);
            return;
        }

        int doneStamps = ilt.Completed.Count(QuestBonus.IsStamp);
        long qbNow = ilt.Count >= 0 ? ilt.Count : ilt.Completed.Count;
        long qbAll = qbNow + ilt.Incomplete.Count;
        w.TextDisabled($"Each quest counts once per account, the first time any of your characters finishes it. "
                       + $"{TimeText.Number(doneStamps)} of the done ones are Dynamic stamps.");
        if (ilt.Incomplete.Count > 0)
            w.TextColored(T11Gold, $"Finish the {TimeText.Number(ilt.Incomplete.Count)} started ones: {TimeText.Number(qbAll)} QB, "
                                   + $"x{QuestBonus.Multiplier(qbAll):0.000} (bonus level {QuestBonus.LevelOf(qbAll)}).");

        w.InputText("Filter", "ilt.qbt.filter", _qbFilter, 64, s => _qbFilter = s);
        w.SameLine();
        w.Checkbox("Dynamic stamps", "ilt.qbt.stamps", _qbShowStamps, v => _qbShowStamps = v);
        w.Tooltip("List the Dynamic stamp flags too (the Quests tab's Dynamic Stamps rows track them by area).");

        QbList(w, "To finish", "todo", ilt.Incomplete, UiColors.Yellow, defaultOpen: true,
               "Started on this account (a row with no completion yet): finishing each is +1 QB, +0.5% XP.");
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
