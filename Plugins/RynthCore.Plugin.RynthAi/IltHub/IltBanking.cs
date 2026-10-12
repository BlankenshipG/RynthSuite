// IltBanking.cs — ILT Hub "Banking" tab: /bank balances, deposits, withdrawals, confirmed
// transfers, transaction log.
//
// Server contract (ACECustom PlayerCommands bank handler, upstream 2026-10-11):
//   /bank                  → "[BANK] Your balances are:" then "[BANK] Pyreals: N", "Luminance:",
//                            "Legendary Keys:", "Mythical Keys:", "Enlightened Coins:",
//                            "Weakly Enlightened Coins:", "Prestige Coins: N (bound to you ...)".
//   /b d, /b lum, /b w     → deposits and withdrawals (IltBankCommands has the commands and replies).
//   /bank t <c> <n> <who>  → "Transferred {N0} {Currency} to {Name}[ (offline)]" or an error;
//                            the recipient sees "Received {N0} {currency} from {Name}".
//   Codes: p pyreals, l luminance, k legendary keys, mk mythical keys, e enl coins, we weak coins.
//   Prestige Coins (pc) are bound: deposit and withdraw only.
// Deposit, withdraw and transfer share the server's bank_command_limit throttle; a balance
// refresh doesn't, so it has its own (client-side) spacing.
// Balance lines are parsed whenever they appear (player-typed /bank too) but only EATEN
// when the Hub asked. Transfers always need a confirmation (UB had none).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi.IltHub;

internal sealed class IltBanking : IIltFeature
{
    /// <summary>Transfer currency table: display name, /bank t code.</summary>
    public static readonly (string Name, string Code)[] Currencies =
    {
        ("Pyreals", "p"),
        ("Luminance", "l"),
        ("Legendary Keys", "k"),
        ("Mythical Keys", "mk"),
        ("Enlightened Coins", "e"),
        ("Weakly Enlightened Coins", "we"),
    };
    private static readonly string[] CurrencyNames = Currencies.Select(c => c.Name).ToArray();
    private static readonly string[] DepositNames = IltBankCommands.Deposits.Select(d => d.Label).ToArray();
    private static readonly string[] WithdrawNames = IltBankCommands.Withdrawals.Select(w => w.Label).ToArray();
    private static readonly string[] NoteNames = IltBankCommands.NoteDenominations.Select(n => n.Label).ToArray();

    /// <summary>Server throttle for deposit / withdraw / transfer (bank_command_limit).</summary>
    private const long BankCommandSpacingMs = 5000;
    /// <summary>Client spacing for balance refreshes (the server doesn't throttle a plain /bank).</summary>
    private const long BalanceSpacingMs = 5000;
    /// <summary>After a deposit or withdrawal, the balances are re-read this long after the reply.</summary>
    private const long RefreshAfterActionMs = 1500;

    private readonly IltHubContext _ctx;
    private readonly object _txLock = new();
    private readonly List<IltBankTransaction> _transactions;
    private IltBankTransaction[] _txSnapshot = Array.Empty<IltBankTransaction>();
    private bool _txDirty;

    private long _lastBankCommandAt;
    private long _lastBalanceAt;
    private long _lastAutoRefreshAt;
    /// <summary>When a quiet balance refresh is due after an action (0 = none). Pump thread.</summary>
    private long _refreshDueAt;
    /// <summary>Until then, money is moving between pack and bank (MovingMoney). Pump thread.</summary>
    private long _moveUntil;
    /// <summary>How long after a deposit / withdrawal reply the moved amounts are not income (covers the balance re-read).</summary>
    private const long MoveWindowMs = 12_000;

    // Render-thread form state.
    private string _amountText = string.Empty;
    private string _withdrawText = string.Empty;
    private string _txFilter = string.Empty;
    private string _lastResult = string.Empty;
    private volatile string _actionResult = string.Empty;

    /// <summary>Raised (pump thread) after a balance line updates the state; Character rates use the deltas.</summary>
    public event Action<string, long, long>? BalanceChanged;

    public IltBanking(IltHubContext ctx)
    {
        _ctx = ctx;
        _transactions = ctx.Store.LoadTransactions();
        PublishSnapshot();
    }

    private IltBankState Bank => _ctx.State.Bank;
    public bool Enabled => _ctx.Options.IsOn(IltFeature.Bank);

    /// <summary>
    /// True while a deposit or withdrawal is moving money between the pack and the bank (sent from
    /// this tab or typed): session rates then re-baseline instead of counting it as income. Pump thread.
    /// </summary>
    public bool MovingMoney => IltHubContext.NowMs < _moveUntil;

    /// <summary>Exposed for the one-time UB import (mutated under the lock).</summary>
    public void ImportUb(Func<List<IltBankTransaction>, string> importer)
    {
        string summary;
        lock (_txLock)
        {
            summary = importer(_transactions);
            _txDirty = true;
        }
        PublishSnapshot();
        RynthLog.Write(LogCat.IltBanking, $"[IltHub] UB sidecar import: {summary}");
    }

    // ── Commands ────────────────────────────────────────────────────────────

    /// <summary>Queues a /bank balance refresh (eaten when <paramref name="quiet"/>).</summary>
    public void RequestRefresh(bool quiet)
    {
        RynthLog.Trace(LogCat.IltBanking, $"RequestRefresh(quiet={quiet})");
        if (_ctx.Capture.IsPending("/bank")) return;
        long now = IltHubContext.NowMs;
        if (now - _lastBalanceAt < BalanceSpacingMs) return;
        _lastBalanceAt = now;
        _ctx.Capture.Enqueue(new IltChatRequest
        {
            Command = "/bank",
            IsResponseLine = t => t.StartsWith("[BANK]", StringComparison.OrdinalIgnoreCase),
            IdleEndMs = 2000,
            FirstLineTimeoutMs = 15000,
            Eat = quiet,
            OnComplete = r =>
            {
                if (r.UnknownCommand) _ctx.Options.Set(IltFeature.Bank, IltTri.Off);
                else if (r.Lines.Count > 0) _ctx.Options.Set(IltFeature.Bank, IltTri.On);
            },
        });
    }

    /// <summary>
    /// Validates and (after the UI confirm) sends a transfer. Pump thread.
    /// Returns an error message, or empty when the command was queued.
    /// </summary>
    public string SendTransfer(int currencyIndex, long amount, string target)
    {
        RynthLog.Trace(LogCat.IltBanking, $"SendTransfer(currency={currencyIndex}, amount={amount}, target='{target}')");
        if (!Enabled) return "Bank is not available on this server.";
        if (currencyIndex < 0 || currencyIndex >= Currencies.Length) return "Pick a currency.";
        if (amount <= 0) return "Amount must be positive.";
        target = (target ?? string.Empty).Trim();
        if (target.Length == 0) return "Enter a recipient.";
        // The recipient is spliced into a chat command, so only character-name characters are
        // allowed: no '/', control characters or line breaks that could start a second command.
        if (!IltParse.CharacterName.IsMatch(target)) return "Recipient must be a character name (letters, spaces, ' and - only).";
        if (target.Equals(_ctx.CharName, StringComparison.OrdinalIgnoreCase)) return "You cannot transfer to yourself.";
        long now = IltHubContext.NowMs;
        if (now - _lastBankCommandAt < BankCommandSpacingMs) return "Bank commands are limited to one every 5 seconds.";
        _lastBankCommandAt = now;

        string cmd = $"/bank t {Currencies[currencyIndex].Code} {amount} {target}";
        _ctx.Capture.Enqueue(new IltChatRequest
        {
            Command = cmd,
            IsResponseLine = t => t.StartsWith("Transferred ", StringComparison.Ordinal)
                               || t.StartsWith("Character '", StringComparison.Ordinal)
                               || t.StartsWith("You cannot transfer", StringComparison.Ordinal)
                               || t.StartsWith("You don't have enough", StringComparison.Ordinal)
                               || t.StartsWith("[Transfer]", StringComparison.Ordinal),
            IdleEndMs = 500,
            FirstLineTimeoutMs = 8000,
            Eat = false,
            OnComplete = r =>
            {
                _lastResult = r.Lines.Count > 0 ? r.Lines[^1] : (r.TimedOut ? "No reply from server." : string.Empty);
                // Balances changed: read them again shortly.
                _refreshDueAt = IltHubContext.NowMs + RefreshAfterActionMs;
            },
        });
        return string.Empty;
    }

    /// <summary>
    /// Sends a deposit (<see cref="IltBankCommands.Deposits"/> index). Pump thread. Returns an
    /// error message, or empty when the command was queued; the reply lands in the tab.
    /// </summary>
    public string SendDeposit(int index)
    {
        string cmd = IltBankCommands.Deposit(index);
        if (cmd.Length == 0) return "Pick what to deposit.";
        return SendAction(cmd, "Deposit");
    }

    /// <summary>
    /// Sends a withdrawal (<see cref="IltBankCommands.Withdrawals"/> index; for trade notes
    /// <paramref name="amount"/> is the note count). Pump thread; same result as SendDeposit.
    /// </summary>
    public string SendWithdraw(int index, long amount, int denomination)
    {
        string cmd = IltBankCommands.Withdraw(index, amount, denomination, out string error);
        if (cmd.Length == 0) return error;
        return SendAction(cmd, "Withdraw");
    }

    /// <summary>A deposit or withdrawal through the capture queue (shown, not eaten), then a quiet balance read.</summary>
    private string SendAction(string cmd, string what)
    {
        RynthLog.Trace(LogCat.IltBanking, $"{what}: '{cmd}'");
        if (!Enabled) return "Bank is not available on this server.";
        long now = IltHubContext.NowMs;
        if (now - _lastBankCommandAt < BankCommandSpacingMs) return "Bank commands are limited to one every 5 seconds.";
        _lastBankCommandAt = now;
        _moveUntil = now + MoveWindowMs;
        _actionResult = what + " sent...";
        _ctx.Capture.Enqueue(new IltChatRequest
        {
            Command = cmd,
            IsResponseLine = IltBankCommands.IsActionReply,
            IdleEndMs = 800,
            FirstLineTimeoutMs = 8000,
            Eat = false,
            OnComplete = r =>
            {
                _actionResult = r.Lines.Count > 0 ? string.Join("\n", r.Lines.TakeLast(6))
                    : r.TimedOut ? "No reply from the server." : string.Empty;
                _refreshDueAt = IltHubContext.NowMs + RefreshAfterActionMs;
            },
        });
        return string.Empty;
    }

    // ── IIltFeature ─────────────────────────────────────────────────────────

    public void Tick(long nowMs)
    {
        // Periodic quiet refresh only while the Hub is open and the server has /bank.
        int every = Bank.AutoRefreshSeconds;
        if (Enabled && _ctx.WindowOpen && every > 0 && nowMs - _lastAutoRefreshAt >= every * 1000L
            && nowMs - _lastBalanceAt >= BalanceSpacingMs)
        {
            _lastAutoRefreshAt = nowMs;
            RequestRefresh(quiet: true);
        }
        // The balance read a deposit, withdrawal or transfer asked for (retried until the spacing allows).
        if (_refreshDueAt != 0 && nowMs >= _refreshDueAt && Enabled && nowMs - _lastBalanceAt >= BalanceSpacingMs)
        {
            _refreshDueAt = 0;
            _lastAutoRefreshAt = nowMs;
            RequestRefresh(quiet: true);
        }

        if (_txDirty)
        {
            lock (_txLock)
            {
                _ctx.Store.SaveTransactionsIfDirty(_transactions);
                _txDirty = false;
            }
            PublishSnapshot();
        }
    }

    public bool OnChat(string text)
    {
        if (text.StartsWith("[BANK]", StringComparison.OrdinalIgnoreCase))
        {
            ParseBalanceLine(text);
            return false; // eating is decided by the capture queue
        }
        // A deposit or withdrawal reply (ours or a typed /b): the next balance and pack changes are moves.
        if (text.StartsWith("Deposited ", StringComparison.Ordinal) || text.StartsWith("Withdrew ", StringComparison.Ordinal))
            _moveUntil = Math.Max(_moveUntil, IltHubContext.NowMs + MoveWindowMs);
        TryLogTransaction(text);
        return false;
    }

    public void OnLogout()
    {
        lock (_txLock) _ctx.Store.SaveTransactionsIfDirty(_transactions);
    }

    // ── Parsing ─────────────────────────────────────────────────────────────

    private void ParseBalanceLine(string text)
    {
        string body = text.Substring("[BANK]".Length).Trim();
        int colon = body.IndexOf(':');
        if (colon <= 0) return;
        string label = body.Substring(0, colon).Trim();
        long value = IltParse.ParseLeadingLong(body.Substring(colon + 1));
        if (body.Substring(colon + 1).Trim().Length == 0) return; // header "Your balances are:"

        // Order matters: "Weakly Enlightened Coins" also contains "Enlightened Coins".
        if (label.Contains("Prestige", StringComparison.OrdinalIgnoreCase)) Apply("PrestigeCoins", ref Bank.PrestigeCoins, value);
        else if (label.Contains("Weakly", StringComparison.OrdinalIgnoreCase)) Apply("WeaklyEnlightenedCoins", ref Bank.WeaklyEnlightenedCoins, value);
        else if (label.Contains("Enlightened", StringComparison.OrdinalIgnoreCase)) Apply("EnlightenedCoins", ref Bank.EnlightenedCoins, value);
        else if (label.Contains("Mythical", StringComparison.OrdinalIgnoreCase)) Apply("MythicalKeys", ref Bank.MythicalKeys, value);
        else if (label.Contains("Legendary", StringComparison.OrdinalIgnoreCase)) Apply("LegendaryKeys", ref Bank.LegendaryKeys, value);
        else if (label.Contains("Luminance", StringComparison.OrdinalIgnoreCase)) Apply("Luminance", ref Bank.Luminance, value);
        else if (label.Contains("Pyreal", StringComparison.OrdinalIgnoreCase)) Apply("Pyreals", ref Bank.Pyreals, value);
        else return;

        Bank.LastUpdated = DateTime.Now;
        _ctx.Options.Set(IltFeature.Bank, IltTri.On);
    }

    private void Apply(string key, ref long field, long value)
    {
        long old = field;
        field = value;
        if (old != value)
        {
            try { BalanceChanged?.Invoke(key, old, value); } catch { }
        }
    }

    private void TryLogTransaction(string text)
    {
        if (!(text.StartsWith("Transferred ", StringComparison.Ordinal) || text.StartsWith("Received ", StringComparison.Ordinal)))
            return;
        // Only server system lines are logged. Anything another player can author (tells, says,
        // emotes, any bracketed channel) is rejected so quoted "Transferred ..." text can't
        // forge entries in the transaction log.
        if (IsPlayerAuthoredLine(text))
        {
            RynthLog.Trace(LogCat.IltBanking, "transaction ignored (player-authored line): " + text);
            return;
        }

        var sent = IltParse.BankSent.Match(text);
        var recv = sent.Success ? System.Text.RegularExpressions.Match.Empty : IltParse.BankReceived.Match(text);
        var m = sent.Success ? sent : recv;
        if (!m.Success) return;

        string other = IltParse.StripMarkup(m.Groups[3].Value).Replace(" (offline)", string.Empty).Trim();
        var tx = new IltBankTransaction
        {
            Timestamp = DateTime.Now,
            Type = sent.Success ? "Sent" : "Received",
            Amount = IltParse.ParseLeadingLong(m.Groups[1].Value),
            Currency = m.Groups[2].Value.Trim(),
            OtherPlayer = other,
            Description = text,
        };
        lock (_txLock)
        {
            _transactions.Add(tx);
            if (_transactions.Count > IltHubStore.MaxTransactions)
                _transactions.RemoveRange(0, _transactions.Count - IltHubStore.MaxTransactions);
            _txDirty = true;
        }
    }

    /// <summary>True for chat another player could have written (tells, says, channel or emote markup).</summary>
    private static bool IsPlayerAuthoredLine(string text)
        => text.Contains("<Tell:", StringComparison.Ordinal)
        || text.Contains(" tells you", StringComparison.Ordinal)
        || text.Contains(" says, \"", StringComparison.Ordinal)
        || text.Contains("[Allegiance]", StringComparison.Ordinal)
        || text.Contains("[Fellowship]", StringComparison.Ordinal)
        || text.Contains("[General]", StringComparison.Ordinal)
        || text.Contains("[Trade]", StringComparison.Ordinal)
        || text.Contains("[LFG]", StringComparison.Ordinal)
        || text.Contains("[Roleplay]", StringComparison.Ordinal)
        || text.Contains("[Society]", StringComparison.Ordinal)
        || text.Contains("[Olthoi]", StringComparison.Ordinal)
        || text.Contains("[Patron]", StringComparison.Ordinal)
        || text.Contains("[Vassals]", StringComparison.Ordinal)
        || text.Contains("[Covassals]", StringComparison.Ordinal)
        || text.Contains("[Monarch]", StringComparison.Ordinal);

    private void PublishSnapshot()
    {
        lock (_txLock) _txSnapshot = _transactions.ToArray();
    }

    // ── UI (render thread) ──────────────────────────────────────────────────

    public void Render()
    {
        if (!Enabled)
        {
            ImGui.TextColored(LegacyDashboardRenderer.ColTextMute,
                _ctx.Options.IsOff(IltFeature.Bank) ? "The server reports /bank is not available." : "Waiting for the server options check...");
            return;
        }

        // Balances
        ImGui.TextColored(LegacyDashboardRenderer.ColTeal, "Balances");
        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh##bank")) _ctx.Post(() => RequestRefresh(quiet: true));
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy##bank")) ImGui.SetClipboardText(BuildCopyLine());
        ImGui.SameLine();
        ImGui.TextDisabled(Bank.LastUpdated == DateTime.MinValue ? "never updated" : "updated " + Bank.LastUpdated.ToString("g"));

        if (ImGui.BeginTable("##iltbal", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            Row("Pyreals", Bank.Pyreals);
            Row("Luminance", Bank.Luminance);
            Row("Legendary Keys", Bank.LegendaryKeys);
            Row("Mythical Keys", Bank.MythicalKeys);
            Row("Enlightened Coins", Bank.EnlightenedCoins);
            Row("Weakly Enlightened Coins", Bank.WeaklyEnlightenedCoins);
            Row("Prestige Coins", Bank.PrestigeCoins);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Bound to you: deposit and withdraw only, never transferred.");
            ImGui.EndTable();
        }

        int refresh = Bank.AutoRefreshSeconds;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Auto-refresh seconds (0 = off)", ref refresh, 30))
            Bank.AutoRefreshSeconds = refresh <= 0 ? 0 : Math.Max(30, refresh);

        ImGui.Separator();
        RenderDepositWithdraw();
        ImGui.Separator();
        RenderTransferForm();
        ImGui.Separator();
        RenderTransactionLog();
    }

    private static void Row(string label, long value)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(label);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"{IltParse.N0(value)}  ({IltParse.Compact(value)})");
    }

    /// <summary>Deposit (one kind or everything, luminance tokens included) and withdraw (an amount, or trade notes).</summary>
    private void RenderDepositWithdraw()
    {
        ImGui.TextColored(LegacyDashboardRenderer.ColTeal, "Deposit");
        int dep = Math.Clamp(Bank.DepositIndex, 0, DepositNames.Length - 1);
        ImGui.SetNextItemWidth(200);
        if (ImGui.Combo("##bankdep", ref dep, DepositNames, DepositNames.Length)) Bank.DepositIndex = dep;
        ImGui.SameLine();
        if (ImGui.Button("Deposit##bankdep"))
        {
            int index = dep;
            _ctx.Post(() => { string err = SendDeposit(index); if (err.Length > 0) _actionResult = err; });
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(IltBankCommands.Deposit(dep) + "\nEverything banks all currencies, notes and luminance tokens."
                             + "\nThe server refuses while you are busy or moving.");

        ImGui.TextColored(LegacyDashboardRenderer.ColTeal, "Withdraw");
        int wd = Math.Clamp(Bank.WithdrawIndex, 0, WithdrawNames.Length - 1);
        ImGui.SetNextItemWidth(200);
        if (ImGui.Combo("##bankwd", ref wd, WithdrawNames, WithdrawNames.Length)) Bank.WithdrawIndex = wd;
        bool notes = IltBankCommands.Withdrawals[wd].Code == "n";
        int denom = Math.Clamp(Bank.WithdrawNoteIndex, 0, NoteNames.Length - 1);
        if (notes)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(110);
            if (ImGui.Combo("##bankwdnote", ref denom, NoteNames, NoteNames.Length)) Bank.WithdrawNoteIndex = denom;
        }
        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##bankwdamt", notes ? "how many notes" : "amount, e.g. 250000, 1.5m", ref _withdrawText, 32u);
        bool ok = IltParse.TryParseAmount(_withdrawText, out long amount);
        ImGui.SameLine();
        ImGui.BeginDisabled(!ok);
        if (ImGui.Button("Withdraw##bankwd"))
        {
            int index = wd, note = denom;
            long amt = amount;
            _ctx.Post(() => { string err = SendWithdraw(index, amt, note); if (err.Length > 0) _actionResult = err; });
        }
        ImGui.EndDisabled();
        if (_withdrawText.Length > 0)
        {
            ImGui.SameLine();
            if (!ok) ImGui.TextColored(LegacyDashboardRenderer.ColHp, "invalid amount");
            else if (notes) ImGui.TextDisabled("= " + IltParse.N0(amount * IltBankCommands.NoteDenominations[denom].Value) + " pyreals");
            else ImGui.TextDisabled("= " + IltParse.N0(amount));
        }

        string result = _actionResult;
        if (result.Length > 0) ImGui.TextWrapped(result);
    }

    private void RenderTransferForm()
    {
        ImGui.TextColored(LegacyDashboardRenderer.ColTeal, "Transfer");
        int idx = Math.Clamp(Bank.TransferCurrencyIndex, 0, Currencies.Length - 1);
        ImGui.SetNextItemWidth(200);
        if (ImGui.Combo("Currency##xfer", ref idx, CurrencyNames, CurrencyNames.Length))
            Bank.TransferCurrencyIndex = idx;

        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("Amount##xfer", "e.g. 250000, 1.5m, 2b", ref _amountText, 32u);
        string target = Bank.TransferTarget;
        ImGui.SetNextItemWidth(200);
        if (ImGui.InputTextWithHint("Recipient##xfer", "character name", ref target, 64u))
            Bank.TransferTarget = target;

        bool amountOk = IltParse.TryParseAmount(_amountText, out long amount);
        if (_amountText.Length > 0)
        {
            ImGui.SameLine();
            if (amountOk) ImGui.TextDisabled("= " + IltParse.N0(amount));
            else ImGui.TextColored(LegacyDashboardRenderer.ColHp, "invalid amount");
        }

        ImGui.BeginDisabled(!amountOk || string.IsNullOrWhiteSpace(Bank.TransferTarget));
        if (ImGui.Button("Send transfer..."))
        {
            int cur = idx;
            long amt = amount;
            string who = Bank.TransferTarget.Trim();
            _ctx.Confirm("Confirm bank transfer",
                $"Send {IltParse.N0(amt)} {Currencies[cur].Name} to {who}?\n\nThis cannot be undone.",
                () =>
                {
                    string err = SendTransfer(cur, amt, who);
                    _lastResult = err.Length > 0 ? err : "Transfer sent...";
                },
                "Send");
        }
        ImGui.EndDisabled();
        if (_lastResult.Length > 0) ImGui.TextWrapped(_lastResult);
    }

    private void RenderTransactionLog()
    {
        var snap = _txSnapshot;
        ImGui.TextColored(LegacyDashboardRenderer.ColTeal, $"Transactions ({snap.Length})");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180);
        ImGui.InputTextWithHint("##txfilter", "filter player / currency", ref _txFilter, 64u);
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy CSV")) ImGui.SetClipboardText(BuildCsv(snap));

        if (ImGui.BeginTable("##ilttx", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable,
                new Vector2(0, 220)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("When");
            ImGui.TableSetupColumn("Type");
            ImGui.TableSetupColumn("Amount");
            ImGui.TableSetupColumn("Currency");
            ImGui.TableSetupColumn("Player");
            ImGui.TableHeadersRow();
            for (int i = snap.Length - 1; i >= 0; i--)
            {
                var t = snap[i];
                if (_txFilter.Length > 0
                    && !t.OtherPlayer.Contains(_txFilter, StringComparison.OrdinalIgnoreCase)
                    && !t.Currency.Contains(_txFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(t.Timestamp.ToString("g"));
                ImGui.TableNextColumn();
                ImGui.TextColored(t.Type == "Sent" ? LegacyDashboardRenderer.ColAmber : LegacyDashboardRenderer.ColGreen, t.Type);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(IltParse.N0(t.Amount));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(t.Currency);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(t.OtherPlayer);
            }
            ImGui.EndTable();
        }
    }

    /// <summary>One-line summary in UB's chat-paste format.</summary>
    public string BuildCopyLine()
        => $"Bank Balances - {_ctx.CharName} | P: {IltParse.N0(Bank.Pyreals)} | L: {IltParse.N0(Bank.Luminance)} | "
         + $"LK: {IltParse.N0(Bank.LegendaryKeys)} | MK: {IltParse.N0(Bank.MythicalKeys)} | "
         + $"Coins: {IltParse.N0(Bank.EnlightenedCoins)} | W Coins: {IltParse.N0(Bank.WeaklyEnlightenedCoins)} | "
         + $"P Coins: {IltParse.N0(Bank.PrestigeCoins)} | "
         + $"Updated: {(Bank.LastUpdated == DateTime.MinValue ? "never" : Bank.LastUpdated.ToString("g"))}";

    private static string BuildCsv(IltBankTransaction[] rows)
    {
        var sb = new System.Text.StringBuilder("Timestamp,Type,Amount,Currency,Player\n");
        foreach (var t in rows)
            sb.Append(t.Timestamp.ToString("s")).Append(',').Append(t.Type).Append(',').Append(t.Amount).Append(',')
              .Append('"').Append(t.Currency.Replace("\"", "\"\"")).Append("\",\"")
              .Append(t.OtherPlayer.Replace("\"", "\"\"")).Append("\"\n");
        return sb.ToString();
    }
}
