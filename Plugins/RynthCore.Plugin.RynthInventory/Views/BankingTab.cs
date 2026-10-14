using System;
using System.Collections.Generic;
using System.Globalization;
using RynthCore.Plugin.RynthInventory.Data;
using RynthCore.Plugin.RynthInventory.Ui;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthInventory.Views;

/// <summary>
/// Banking: the Infinite Leaftide bank of every character on this server (the Mini Remote's Banking
/// tab moved here 2026-10-13). Balances, form choices and the last reply come from RynthILT's
/// "RynthILT.Data" bank JSON; the character list adds the inventory's characters on the same server
/// (BankRoster). Deposits, withdrawals, transfers and balance reads go back to RynthILT as verb lines;
/// a transfer is confirmed here first (RynthILT doesn't ask again).
/// </summary>
internal sealed partial class InventoryView
{
    private const int MaxTransactionsShown = 20;
    private const float ColPyreals = 150f, ColLum = 270f, ColKeys = 380f, ColCoins = 450f, ColUpdated = 560f;

    private int _depositIndex, _withdrawIndex, _withdrawNote, _transferIndex, _transferNote;
    private string _withdrawAmount = string.Empty, _transferAmount = string.Empty, _transferTo = string.Empty;
    /// <summary>The transfer waiting for "Yes, send": its verb line and the sentence shown.</summary>
    private (string Verb, string Text)? _confirmTransfer;
    private string _bankMessage = string.Empty;

    /// <summary>
    /// The Banking tab button shows when RynthILT answered on an ILT world, or when the tab was asked
    /// for (/ginv bank) so it can say what is missing.
    /// </summary>
    private bool BankingShown => Tab == TabBanking || (_c.Bank.Loaded && _c.Bank.Available);

    /// <summary>Opens on the Banking tab ("/ginv bank", RynthAi's and RynthILT's bank links).</summary>
    public void ShowBanking()
    {
        Tab = TabBanking;
        _confirmTransfer = null;
    }

    private void DrawBanking(UiWindow w)
    {
        IltBankView b = _c.Bank;
        if (!b.Loaded)
        {
            w.TextDisabled("The bank comes from RynthILT (Infinite Leaftide). Load RynthILT to see every character's bank here.");
            return;
        }
        if (!b.Available)
        {
            w.TextDisabled("This isn't an Infinite Leaftide world, so there is no bank to show.");
            return;
        }

        w.Text($"Bank on {(b.World.Length > 0 ? b.World : "this server")}");
        w.SameLine();
        w.SmallButton("Refresh", "b.refresh", () => SendBank(IltBankView.RefreshVerb(), "Reading the balance..."));
        w.Tooltip("A quiet /bank balance read for the character you're on (other characters update when they log in).");
        if (b.Enabled == 0) w.TextColored(UiColors.Red, "This server has no /bank command.");
        else if (b.Enabled < 0) w.TextDisabled("Whether this server has /bank isn't known yet.");
        if (b.Result.Length > 0) w.TextColored(UiColors.Gold, b.Result);
        if (_bankMessage.Length > 0) w.TextColored(UiColors.Yellow, _bankMessage);

        List<BankRosterEntry> roster = BankRoster.Build(b, _c.Index);
        DrawBankTable(w, roster);
        if (b.Enabled != 0)
        {
            DrawDeposit(w, b);
            DrawWithdraw(w, b);
            DrawTransfer(w, b, roster);
        }
        DrawTransactions(w, b);
    }

    private void DrawBankTable(UiWindow w, List<BankRosterEntry> roster)
    {
        w.SeparatorText($"Characters ({roster.Count})");
        w.TextDisabled("Character");
        w.SameLine(ColPyreals); w.TextDisabled("Pyreals");
        w.SameLine(ColLum); w.TextDisabled("Luminance");
        w.SameLine(ColKeys); w.TextDisabled("Keys L/M");
        w.SameLine(ColCoins); w.TextDisabled("Coins E/W/P");
        w.SameLine(ColUpdated); w.TextDisabled("Updated");
        foreach (BankRosterEntry e in roster)
        {
            string name = e.Current ? e.Name + " (you)" : e.Name;
            if (e.Bank is { } r)
            {
                w.Text(name);
                if (e.Account.Length > 0) w.Tooltip("Account: " + e.Account);
                BankCells(w, r);
                w.SameLine(ColUpdated);
                w.TextDisabled(r.UpdatedUtc == DateTime.MinValue ? "-" : InventoryIndex.Age(r.UpdatedUtc, _c.UtcNow));
            }
            else
            {
                w.TextDisabled(name);
                if (e.Account.Length > 0) w.Tooltip("Account: " + e.Account);
                w.SameLine(ColPyreals);
                w.TextDisabled("not read yet: log in with RynthILT loaded");
            }
        }
        if (roster.Count == 0) w.TextDisabled("No characters on this server yet.");
        else
        {
            w.TextColored(UiColors.Gold, "Total");
            BankCells(w, BankRoster.Totals(roster));
        }
    }

    /// <summary>The balance columns of one row (Pyreals .. Coins).</summary>
    private static void BankCells(UiWindow w, IltBankRow r)
    {
        w.SameLine(ColPyreals); w.Text(N(r.Pyreals));
        w.SameLine(ColLum); w.Text(N(r.Luminance));
        w.SameLine(ColKeys); w.Text($"{N(r.LegendaryKeys)}/{N(r.MythicalKeys)}");
        w.SameLine(ColCoins); w.Text($"{N(r.EnlightenedCoins)}/{N(r.WeaklyEnlightenedCoins)}/{N(r.PrestigeCoins)}");
    }

    private void DrawDeposit(UiWindow w, IltBankView b)
    {
        if (b.Deposits.Count == 0) return;
        w.SeparatorText("Deposit");
        _depositIndex = Math.Clamp(_depositIndex, 0, b.Deposits.Count - 1);
        w.Combo("What", "b.dep.what", _depositIndex, b.Deposits, i => _depositIndex = i);
        w.SameLine();
        int di = _depositIndex;
        string label = b.Deposits[di];
        w.SmallButton("Deposit", "b.dep.go", () => SendBank(IltBankView.DepositVerb(di), "Depositing " + label + "..."));
    }

    private void DrawWithdraw(UiWindow w, IltBankView b)
    {
        if (b.Withdrawals.Count == 0) return;
        w.SeparatorText("Withdraw");
        _withdrawIndex = Math.Clamp(_withdrawIndex, 0, b.Withdrawals.Count - 1);
        w.Combo("Currency", "b.wd.what", _withdrawIndex, Labels(b.Withdrawals), i => _withdrawIndex = i);
        IltBankChoice choice = b.Withdrawals[_withdrawIndex];
        w.InputText(choice.Notes ? "Notes" : "Amount", "b.wd.amt", _withdrawAmount, 20, v => _withdrawAmount = v);
        int note = NoteCombo(w, b, choice, "b.wd.note", _withdrawNote, i => _withdrawNote = i);
        w.SameLine();
        int wi = _withdrawIndex;
        w.SmallButton("Withdraw", "b.wd.go", () =>
        {
            long amount = BankRoster.ParseAmount(_withdrawAmount);
            if (amount <= 0) { _bankMessage = "Type a whole number to withdraw."; return; }
            SendBank(IltBankView.WithdrawVerb(wi, amount, note), $"Withdrawing {N(amount)} {choice.Label}...");
        });
    }

    private void DrawTransfer(UiWindow w, IltBankView b, List<BankRosterEntry> roster)
    {
        if (b.Transfers.Count == 0) return;
        w.SeparatorText("Transfer");
        _transferIndex = Math.Clamp(_transferIndex, 0, b.Transfers.Count - 1);
        w.Combo("Currency", "b.tx.what", _transferIndex, Labels(b.Transfers), i => { _transferIndex = i; _confirmTransfer = null; });
        IltBankChoice choice = b.Transfers[_transferIndex];
        w.InputText(choice.Notes ? "Notes" : "Amount", "b.tx.amt", _transferAmount, 20, v => { _transferAmount = v; _confirmTransfer = null; });
        int note = NoteCombo(w, b, choice, "b.tx.note", _transferNote, i => { _transferNote = i; _confirmTransfer = null; });
        w.InputText("To", "b.tx.to", _transferTo, 40, v => { _transferTo = v; _confirmTransfer = null; });

        // The other characters on this server, to fill "To" with one click.
        var others = new List<string> { "Pick a character..." };
        foreach (BankRosterEntry e in roster) if (!e.Current) others.Add(e.Name);
        if (others.Count > 1)
        {
            w.SameLine();
            w.Combo("##pick", "b.tx.pick", 0, others, i => { if (i > 0 && i < others.Count) { _transferTo = others[i]; _confirmTransfer = null; } });
        }

        if (_confirmTransfer is { } pending)
        {
            w.TextColored(UiColors.Red, pending.Text);
            w.SmallButton("Yes, send", "b.tx.yes", () =>
            {
                _confirmTransfer = null;
                SendBank(pending.Verb, "Transfer sent...");
            });
            w.SameLine();
            w.SmallButton("Cancel", "b.tx.no", () => _confirmTransfer = null);
            return;
        }
        int ti = _transferIndex;
        w.SmallButton("Transfer...", "b.tx.go", () =>
        {
            long amount = BankRoster.ParseAmount(_transferAmount);
            string to = (_transferTo ?? string.Empty).Trim();
            if (amount <= 0) { _bankMessage = "Type a whole number to transfer."; return; }
            if (to.Length == 0) { _bankMessage = "Type (or pick) who gets the transfer."; return; }
            string what = choice.Notes && note < b.Notes.Count ? $"{N(amount)} x {b.Notes[note].Label}" : $"{N(amount)} {choice.Label}";
            _confirmTransfer = (IltBankView.TransferVerb(ti, amount, note, to), $"Send {what} to {to}? This can't be undone.");
            _bankMessage = string.Empty;
        });
    }

    /// <summary>
    /// The trade-note combo for a choice that counts notes; returns the note index in use (0 when
    /// the choice isn't notes). A pick arrives as an event and lands in <paramref name="set"/>.
    /// </summary>
    private static int NoteCombo(UiWindow w, IltBankView b, IltBankChoice choice, string id, int index, Action<int> set)
    {
        if (!choice.Notes || b.Notes.Count == 0) return 0;
        var labels = new List<string>(b.Notes.Count);
        foreach (IltBankNote n in b.Notes) labels.Add($"{n.Label} ({N(n.Value)} pyreals)");
        index = Math.Clamp(index, 0, b.Notes.Count - 1);
        w.Combo("Note", id, index, labels, set);
        return index;
    }

    private void DrawTransactions(UiWindow w, IltBankView b)
    {
        if (b.Transactions.Count == 0) return;
        if (!w.CollapsingHeader($"Recent transfers ({b.Transactions.Count})", "b.txlog")) return;
        int shown = 0;
        foreach (IltBankTx t in b.Transactions)
        {
            if (shown++ >= MaxTransactionsShown) { w.TextDisabled($"...and {b.Transactions.Count - MaxTransactionsShown} more."); break; }
            string dir = t.Type.Length > 0 ? t.Type : "?";
            w.Text($"{t.When}  {dir} {N(t.Amount)} {t.Currency}{(t.Player.Length > 0 ? " - " + t.Player : "")}");
        }
    }

    /// <summary>Hands a verb to RynthILT; says so when it was refused.</summary>
    private void SendBank(string verb, string sending)
    {
        _bankMessage = _c.BankCommand(verb) ? sending : "RynthILT didn't take that (is it loaded, and are you logged in?).";
    }

    private static List<string> Labels(IReadOnlyList<IltBankChoice> choices)
    {
        var list = new List<string>(choices.Count);
        foreach (IltBankChoice c in choices) list.Add(c.Label);
        return list;
    }

    private static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
}
