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
            // The running message is long (who has to stand where): wrap it; the end states are short.
            if (trade.Phase == TradePhase.Executing) w.TextWrapped(trade.Message);
            else w.TextColored(msgColor, trade.Message);
        }
        // Each giving account's client: which character it is on and what it is doing.
        foreach (string line in trade.AccountLines)
            w.TextDisabled(line);

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
            // Item info tooltip (hover to see details from the inventory index).
            ItemTooltip(w, item);

            if (item.StatusText.Length > 0)
            {
                w.SameLine();
                w.TextDisabled(" - " + item.StatusText);
            }

            // Remove button (only while building).
            if (building)
            {
                int idx = i; // capture for lambda
                w.SameLine();
                w.SmallButton("X", $"trade.rm.{i}", () =>
                {
                    trade.RemoveAt(idx);
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

    /// <summary>
    /// Shows a tooltip with item details from the inventory index when hovering over a trade item.
    /// </summary>
    private void ItemTooltip(UiWindow w, TradeItem item)
    {
        InventoryIndex? index = _c.Index;
        if (index == null) return;

        // Find the item in the index by character key + object id, or character key + wcid + name.
        InventoryRow? row = null;
        foreach (InventoryRow r in index.Rows)
        {
            if (!string.Equals(r.Owner.Key, item.CharacterKey, StringComparison.Ordinal)) continue;
            if (item.ItemId != 0 && r.Item.Id == item.ItemId) { row = r; break; }
            if (item.Wcid != 0 && r.Item.Wcid == item.Wcid
                && string.Equals(r.Item.Name, item.ItemName, StringComparison.OrdinalIgnoreCase))
            { row = r; break; }
        }
        if (row == null) return;

        ItemRecord it = row.Item;
        var lines = new List<string>();
        lines.Add(it.Name + (it.Stack > 1 ? $" (stack {it.Stack:N0}/{it.MaxStack:N0})" : ""));
        lines.Add(ItemCategory.Label(it.Category));
        string material = ItemNames.Material(it.Material);
        if (material.Length > 0) lines.Add(material);
        if (it.Workmanship > 0) lines.Add("Workmanship: " + it.Workmanship.ToString(CultureInfo.InvariantCulture));
        if (it.Value != 0) lines.Add("Value: " + it.Value.ToString("N0", CultureInfo.InvariantCulture));
        if (it.Burden != 0) lines.Add("Burden: " + it.Burden.ToString("N0", CultureInfo.InvariantCulture));
        if (it.ArmorLevel > 0) lines.Add("Armor Level: " + it.ArmorLevel.ToString(CultureInfo.InvariantCulture));
        string set = ItemNames.Set(it.SetId);
        if (set.Length > 0) lines.Add(set);
        if (it.SpellNames.Length > 0) lines.Add("Spells: " + string.Join(", ", it.SpellNames));
        lines.Add($"Owner: {row.Owner.Name} on {row.Owner.Server}");
        w.Tooltip(string.Join("\n", lines));
    }
}
