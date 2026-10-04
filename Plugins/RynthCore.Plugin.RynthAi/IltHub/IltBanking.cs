// IltBanking.cs — ILT Hub "Banking" tab: /bank balances, confirmed transfers, transaction log.
//
// Server contract (ACECustom PlayerCommands bank handler):
//   /bank                  → "[BANK] Your balances are:" then "[BANK] Pyreals: N", "Luminance:",
//                            "Legendary Keys:", "Mythical Keys:", "Enlightened Coins:",
//                            "Weakly Enlightened Coins:".
//   /bank t <c> <n> <who>  → "Transferred {N0} {Currency} to {Name}[ (offline)]" or an error;
//                            the recipient sees "Received {N0} {currency} from {Name}".
//   Codes: p pyreals, l luminance, k legendary keys, mk mythical keys, e enl coins, we weak coins.
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

    /// <summary>Server throttle for bank commands (bank_command_limit).</summary>
    private const long BankCommandSpacingMs = 5000;

    private readonly IltHubContext _ctx;
    private readonly object _txLock = new();
    private readonly List<IltBankTransaction> _transactions;
    private IltBankTransaction[] _txSnapshot = Array.Empty<IltBankTransaction>();
    private bool _txDirty;

    private long _lastBankCommandAt;
    private long _lastAutoRefreshAt;

    // Render-thread form state.
    private string _amountText = string.Empty;
    private string _txFilter = string.Empty;
    private string _lastResult = string.Empty;

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
        if (now - _lastBankCommandAt < BankCommandSpacingMs) return;
        _lastBankCommandAt = now;
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
                // Balances changed — refresh quietly once the server throttle allows.
                _lastAutoRefreshAt = 0;
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
            && nowMs - _lastBankCommandAt >= BankCommandSpacingMs)
        {
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
        if (label.Contains("Weakly", StringComparison.OrdinalIgnoreCase)) Apply("WeaklyEnlightenedCoins", ref Bank.WeaklyEnlightenedCoins, value);
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
            ImGui.EndTable();
        }

        int refresh = Bank.AutoRefreshSeconds;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Auto-refresh seconds (0 = off)", ref refresh, 30))
            Bank.AutoRefreshSeconds = refresh <= 0 ? 0 : Math.Max(30, refresh);

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
