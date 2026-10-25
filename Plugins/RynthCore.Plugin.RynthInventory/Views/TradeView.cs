using System;
using System.Collections.Generic;
using System.Globalization;
using RynthCore.Plugin.RynthInventory.Data;
using RynthCore.Plugin.RynthInventory.Ui;

namespace RynthCore.Plugin.RynthInventory.Views;

/// <summary>
/// The Trade List window: shows the items queued for trading, the recipient name input,
/// status of each item, and controls to start/cancel the trade. Drawn in its own UiWindow
/// that stays open until the player closes it.
/// </summary>
internal sealed class TradeView
{
    private readonly InventoryContext _c;

    /// <summary>The pending recipient name typed by the player.</summary>
    private string _recipientInput = string.Empty;

    public TradeView(InventoryContext c) => _c = c;

    /// <summary>Draws the trade list window contents.</summary>
    public void Draw(UiWindow w)
    {
        TradeList? trade = _c.Trade;
        if (trade == null)
        {
            w.TextWrapped("Add items from Global Inventory to start building a trade list.");
            return;
        }

        bool building = trade.Phase == TradePhase.Building;
        bool running = trade.Phase is TradePhase.Executing or TradePhase.WaitSwitch;

        // ── Status message ──────────────────────────────────────────────
        if (trade.Message.Length > 0)
        {
            uint msgColor = trade.Phase switch
            {
                TradePhase.Complete => UiColors.Green,
                TradePhase.Cancelled => UiColors.Yellow,
                TradePhase.WaitSwitch => UiColors.Yellow,
                _ => UiColors.White,
            };
            w.TextColored(msgColor, trade.Message);
        }

        // ── Recipient ───────────────────────────────────────────────────
        if (building)
        {
            w.InputText("Send to", "trade.to", _recipientInput, 64, v =>
            {
                _recipientInput = v;
                trade.Recipient = v.Trim();
            });
        }
        else
        {
            w.Text("Sending to: " + trade.Recipient);
        }
        if (trade.Server.Length > 0)
            w.TextDisabled("Server: " + trade.Server);

        w.Separator();

        // ── Item list ───────────────────────────────────────────────────
        var (given, notFound, failed, pending) = trade.Counts();
        string header = $"Items ({trade.Items.Count})";
        if (!building)
            header += $"  -  {given} given, {pending} pending" +
                      (notFound > 0 ? $", {notFound} not found" : "") +
                      (failed > 0 ? $", {failed} failed" : "");
        w.Text(header);

        w.BeginChild("tradeitems", 0f, building ? -60f : -30f, border: true);

        // Group by character for readability.
        string? lastCharKey = null;
        for (int i = 0; i < trade.Items.Count; i++)
        {
            TradeItem item = trade.Items[i];
            if (!string.Equals(item.CharacterKey, lastCharKey, StringComparison.Ordinal))
            {
                lastCharKey = item.CharacterKey;
                string charLabel = item.CharacterName;
                if (!string.IsNullOrEmpty(item.Account) && item.Account != "unknown")
                    charLabel += $" (account {item.Account})";
                w.SeparatorText(charLabel);
            }

            // Status indicator.
            uint statusColor;
            string statusIcon;
            switch (item.Status)
            {
                case TradeItemStatus.Given:
                    statusColor = UiColors.Green;
                    statusIcon = "[OK] ";
                    break;
                case TradeItemStatus.Giving:
                    statusColor = UiColors.Yellow;
                    statusIcon = "[..] ";
                    break;
                case TradeItemStatus.NotFound:
                    statusColor = UiColors.Red;
                    statusIcon = "[??] ";
                    break;
                case TradeItemStatus.Failed:
                    statusColor = UiColors.Red;
                    statusIcon = "[!!] ";
                    break;
                default:
                    statusColor = UiColors.White;
                    statusIcon = "";
                    break;
            }

            string qty = item.Quantity > 0
                ? " x" + item.Quantity.ToString("N0", CultureInfo.InvariantCulture)
                : "";
            string line = $"{statusIcon}{item.ItemName}{qty}";

            if (item.Status != TradeItemStatus.Pending || !building)
                w.TextColored(statusColor, line);
            else
                w.Text(line);

            if (item.StatusText.Length > 0)
            {
                w.SameLine();
                w.TextDisabled(" - " + item.StatusText);
            }

            // Remove button (only while building).
            if (building)
            {
                w.SameLine();
                w.SmallButton("X", $"trade.rm.{i}", () =>
                {
                    trade.RemoveAt(i);
                    w.NeedsPass = true;
                });
            }
        }

        if (trade.Items.Count == 0)
            w.TextDisabled("No items added yet. Use the [+] button on items in Global Inventory.");

        w.EndChild();

        // ── Action buttons ──────────────────────────────────────────────
        if (building)
        {
            bool canStart = trade.Items.Count > 0 && trade.Recipient.Trim().Length > 0;
            if (canStart)
            {
                w.Button("Start Trade", "trade.start", () => _c.TradeCommand?.Invoke("start"));
                w.SameLine();
            }
            w.Button("Clear All", "trade.clear", () => _c.TradeCommand?.Invoke("clear"));
        }
        else if (running)
        {
            w.Button("Cancel Trade", "trade.cancel", () => _c.TradeCommand?.Invoke("cancel"));
        }
        else // Complete or Cancelled
        {
            w.Button("New Trade", "trade.new", () => _c.TradeCommand?.Invoke("new"));
        }
    }
}
