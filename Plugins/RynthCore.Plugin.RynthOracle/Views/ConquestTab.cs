// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using System;
using System.Linq;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>
/// ConquestAC (tag Conquest; the tab only exists there): bank balances and bank commands, advanced
/// and enlightenment augmentations, levels past 275 and the XP bonus breakdown, fellowships looking
/// for members, and the server's custom quest worklist. Upstream's Bank, Conquest Augs, Conquest XP,
/// Fship and Custom tabs.
/// </summary>
internal sealed partial class OracleView
{
    private static readonly string[] ConquestViews = { "Bank", "Augs", "Enlightenment", "Experience", "Fellowships", "Custom quests" };
    private int _conquestView;
    private int _withdrawIndex = ConquestData.DefaultWithdraw, _transferIndex = ConquestData.DefaultTransfer;
    private string _withdrawAmount = "", _transferAmount = "", _transferTarget = "";
    private int _fshipSort;

    private void DrawConquest(UiWindow w)
    {
        for (int i = 0; i < ConquestViews.Length; i++)
        {
            int index = i;
            if (i > 0) w.SameLine();
            w.ToggleButton(ConquestViews[i], "cq.v." + i, i == _conquestView, () => _conquestView = index, small: true);
        }
        switch (_conquestView)
        {
            case 0: DrawConquestBank(w); break;
            case 1: DrawConquestAugs(w); break;
            case 2: DrawConquestEnl(w); break;
            case 3: DrawConquestXp(w); break;
            case 4: DrawConquestFships(w); break;
            default: DrawConquestCustom(w); break;
        }
    }

    private static string ReadAt(DateTime utc) => utc == DateTime.MinValue ? "not read yet" : $"read at {utc.ToLocalTime():HH:mm}";

    private void DrawConquestBank(UiWindow w)
    {
        ConquestData cq = _c.Conquest;
        w.Button("Refresh", "cq.bank.refresh", () => _c.Send(cq.RequestBank(_c.NowMs)));
        w.Tooltip("Sends /b and reads your balances.");
        w.SameLine();
        w.TextDisabled(ReadAt(cq.BankUtc));
        foreach (NamedValue b in cq.Bank.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
        {
            w.TextDisabled(Cut(b.Name, 34));
            w.SameLine(260f);
            w.Text(b.Value);
        }
        if (cq.Bank.Count == 0) w.TextDisabled("No balances read yet.");

        w.SeparatorText("Deposit");
        w.Button("Deposit everything", "cq.bank.deposit", () => _c.Send("/bank deposit"));
        w.Tooltip("Sends /bank deposit.");
        w.Checkbox("Deposit by itself every ten minutes", "cq.bank.auto", _c.Settings.ConquestAutoDeposit,
            v => { _c.Settings.ConquestAutoDeposit = v; _c.Settings.Save(); });
        w.Tooltip("At :00, :10, :20 ... while you're logged in on ConquestAC. Its reply is hidden.");

        w.SeparatorText("Withdraw");
        string[] wl = ConquestData.Withdrawable.Select(c => c.Label).ToArray();
        w.Combo("Currency", "cq.bank.wcur", _withdrawIndex, wl, i => _withdrawIndex = Math.Clamp(i, 0, wl.Length - 1));
        w.InputText("Amount", "cq.bank.wamt", _withdrawAmount, 16, s => _withdrawAmount = s);
        w.Button("Withdraw", "cq.bank.withdraw", () =>
        {
            string? cmd = ConquestData.WithdrawCommand(ConquestData.Withdrawable[_withdrawIndex], _withdrawAmount);
            if (cmd == null) _c.Print("Withdraw: type a whole number above 0.");
            else _c.Send(cmd);
        });

        w.SeparatorText("Transfer to a character");
        string[] tl = ConquestData.Transferable.Select(c => c.Label).ToArray();
        w.Combo("Currency##t", "cq.bank.tcur", _transferIndex, tl, i => _transferIndex = Math.Clamp(i, 0, tl.Length - 1));
        w.InputText("Amount##t", "cq.bank.tamt", _transferAmount, 16, s => _transferAmount = s);
        w.InputText("Character", "cq.bank.tgt", _transferTarget, 40, s => _transferTarget = s);
        w.Button("Transfer", "cq.bank.transfer", () =>
        {
            string? cmd = ConquestData.TransferCommand(ConquestData.Transferable[_transferIndex], _transferAmount, _transferTarget);
            if (cmd == null) _c.Print("Transfer: type a whole number above 0 (MMD notes: at most 1,000) and a character name.");
            else _c.Send(cmd);
        });
    }

    private void DrawConquestAugs(UiWindow w)
    {
        ConquestData cq = _c.Conquest;
        w.Button("Refresh", "cq.augs.refresh", () => _c.Send(cq.RequestAugs(_c.NowMs)));
        w.Tooltip("Sends /augs.");
        w.SameLine();
        w.TextDisabled($"{ReadAt(cq.AugsUtc)}, {cq.AugTotal} in total");
        w.TextDisabled("Aug"); w.SameLine(120f); w.TextDisabled("Have"); w.SameLine(170f); w.TextDisabled("Next costs"); w.SameLine(330f); w.TextDisabled("Effect now");
        foreach (ConquestAug a in cq.Augs)
        {
            w.Text(a.Name);
            w.SameLine(120f);
            w.Text(a.Count.ToString());
            w.SameLine(170f);
            w.Text(a.NextCostText());
            w.SameLine(330f);
            w.TextDisabled(Cut(a.Effect(), 48));
            w.Tooltip(a.Effect());
        }
    }

    private void DrawConquestEnl(UiWindow w)
    {
        ConquestData cq = _c.Conquest;
        w.Button("Refresh", "cq.enl.refresh", () => _c.Send(cq.RequestEnl(_c.NowMs)));
        w.Tooltip("Sends /enl augs.");
        w.SameLine();
        w.TextDisabled(ReadAt(cq.EnlUtc));
        foreach (NamedValue a in cq.EnlAugs)
        {
            w.Text(Cut(a.Name, 26));
            w.SameLine(200f);
            w.Text(a.Value);
            w.SameLine(360f);
            w.TextDisabled(ConquestData.EnlCost(a.Name));
        }
        if (cq.EnlAugs.Count == 0) w.TextDisabled("Nothing read yet. Press Refresh.");
    }

    private void DrawConquestXp(UiWindow w)
    {
        ConquestData cq = _c.Conquest;
        CharacterSnapshot ch = _c.Character;
        int level = ch.Level;
        w.SeparatorText($"Level {level}");
        if (level >= ConquestData.MaxLevel) w.Text("Maximum level.");
        else
        {
            long start = level >= 276 ? ConquestData.XpForLevel(level) : 0;
            long next = level >= 275 ? ConquestData.XpForLevel(level + 1) : 0;
            if (next > 0)
            {
                long toNext = Math.Max(0, next - ch.TotalXp);
                long begin = level == 275 ? 191_226_310_247 : start;
                float frac = next > begin ? (float)Math.Clamp((double)(ch.TotalXp - begin) / (next - begin), 0, 1) : 1f;
                w.ProgressBar(frac, -1f, 0f, $"{TimeText.Number(toNext)} to level {level + 1}");
            }
            else
            {
                (long toNext, float frac) = ch.LevelProgress();
                w.ProgressBar(frac, -1f, 0f, $"{TimeText.Number(toNext)} to level {level + 1}");
            }
            long toMax = Math.Max(0, ConquestData.XpForLevel(ConquestData.MaxLevel) - ch.TotalXp);
            w.TextDisabled($"{TimeText.Number(toMax)} to level {ConquestData.MaxLevel}");
        }
        int target = ch.Enlightenment + 1;
        w.SeparatorText($"Enlightenment {ch.Enlightenment}");
        w.Text($"Next costs {TimeText.Number(ConquestData.EnlCoinCost(target))} coins and {(ConquestData.EnlLuminanceCost(target) / 1_000_000.0):0.##}M luminance");

        w.SeparatorText("Experience bonuses");
        w.Button("Refresh", "cq.bonus.refresh", () => _c.Send(cq.RequestBonus(_c.NowMs)));
        w.Tooltip("Sends /bonus.");
        w.SameLine();
        w.TextDisabled(ReadAt(cq.BonusUtc));
        foreach (NamedValue b in cq.Bonuses)
        {
            w.TextDisabled(b.Name);
            w.SameLine(200f);
            w.Text(b.Value.Length > 0 ? b.Value : "-");
        }
    }

    private void DrawConquestFships(UiWindow w)
    {
        ConquestData cq = _c.Conquest;
        w.Button("Refresh", "cq.fship.refresh", () => _c.Send(cq.RequestFships(_c.NowMs)));
        w.Tooltip("Sends /fship list.");
        w.SameLine();
        w.TextDisabled($"{ReadAt(cq.FshipUtc)}, {cq.Fellowships.Count} looking for members");
        w.Combo("Sort", "cq.fship.sort", _fshipSort, new[] { "Name", "Leader", "Most members", "Location" }, i => _fshipSort = i);
        var list = _fshipSort switch
        {
            1 => cq.Fellowships.OrderBy(f => f.Leader, StringComparer.OrdinalIgnoreCase),
            2 => cq.Fellowships.OrderByDescending(f => f.MemberCount),
            3 => cq.Fellowships.OrderBy(f => f.Location, StringComparer.OrdinalIgnoreCase),
            _ => cq.Fellowships.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
        };
        w.BeginChild("cq.fship.list", 0f, 0f, border: true);
        foreach (ConquestFellowship f in list)
        {
            w.Text(Cut(f.Name, 26));
            w.SameLine(200f);
            w.Text(Cut(f.Leader, 20));
            w.SameLine(350f);
            w.Text(f.Members);
            if (f.Location.Length > 0) { w.SameLine(410f); w.TextDisabled(Cut(f.Location, 26)); }
        }
        if (cq.Fellowships.Count == 0) w.TextDisabled("None read yet. Press Refresh.");
        w.EndChild();
    }

    private void DrawConquestCustom(UiWindow w)
    {
        DateTime now = _c.UtcNow;
        bool any = false;
        foreach (CustomQuest q in CustomQuestList.For(_c.Server))
        {
            any = true;
            DoneMark(w, q.IsComplete(_c.Flags, now));
            w.SameLine(50f);
            CustomQuest row = q;
            string status = q.Status(_c.Flags, now);
            w.Selectable(Cut(q.Name, 34), "cq.cust." + q.Flag, false, () => PrintEntry(row.Name, row.Flag, status, row.Hint, row.Url), 280f);
            w.SameLine(340f);
            w.Text(status);
        }
        if (!any) w.TextDisabled("No custom quests listed for this server.");
        if (_c.Flags.LastReadUtc == DateTime.MinValue) w.TextDisabled("Read your quest flags (Quests tab) for their status.");
    }
}
