using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthInventory.Data;

/// <summary>Status of one item in the trade queue.</summary>
internal enum TradeItemStatus
{
    /// <summary>Waiting to be given (not yet attempted).</summary>
    Pending,
    /// <summary>GiveObjectTo sent, waiting for confirmation.</summary>
    Giving,
    /// <summary>Successfully given to the recipient.</summary>
    Given,
    /// <summary>Item not found on the character (moved, consumed or traded).</summary>
    NotFound,
    /// <summary>GiveObjectTo failed (target not nearby, API error, etc.).</summary>
    Failed,
}

/// <summary>One item (or stack) to give to another player.</summary>
internal sealed class TradeItem
{
    /// <summary>CharacterSnapshot.Key (server\u001fcharacter, lowercase).</summary>
    public string CharacterKey = string.Empty;
    /// <summary>Display name of the character that owns the item.</summary>
    public string CharacterName = string.Empty;
    /// <summary>Account name the character belongs to.</summary>
    public string Account = string.Empty;
    /// <summary>Server name.</summary>
    public string Server = string.Empty;
    /// <summary>Display name of the item.</summary>
    public string ItemName = string.Empty;
    /// <summary>Weenie class id (for finding by class if the exact object is gone).</summary>
    public uint Wcid;
    /// <summary>How many to give (0 = give the whole object; positive = split this many off a stack).</summary>
    public int Quantity;
    /// <summary>The object id at the time it was added (may not exist when the character logs in).</summary>
    public uint ItemId;
    /// <summary>Current status.</summary>
    public TradeItemStatus Status = TradeItemStatus.Pending;
    /// <summary>A short message for the player (e.g. "not found", "given").</summary>
    public string StatusText = string.Empty;
}

/// <summary>What the trade list is doing.</summary>
internal enum TradePhase
{
    /// <summary>The player is building the list (adding/removing items, typing a name).</summary>
    Building,
    /// <summary>Trade is running: giving items on the current character.</summary>
    Executing,
    /// <summary>Current character's items are done; waiting for the player to switch characters.</summary>
    WaitSwitch,
    /// <summary>All items processed (some may have failed).</summary>
    Complete,
    /// <summary>Player cancelled the trade.</summary>
    Cancelled,
}

/// <summary>
/// A list of items to give to another player, built from the Global Inventory window.
/// All items must be on the same server. The plugin executes it by giving each item
/// via GiveObjectTo when the owning character is logged in.
/// </summary>
internal sealed class TradeList
{
    /// <summary>The name the player typed as the recipient (resolved to a nearby player id at give time).</summary>
    public string Recipient = string.Empty;
    /// <summary>The server all items must be on (set from the first item added).</summary>
    public string Server = string.Empty;
    /// <summary>The items to trade.</summary>
    public readonly List<TradeItem> Items = new();
    /// <summary>Current phase.</summary>
    public TradePhase Phase = TradePhase.Building;
    /// <summary>Status message shown at the top of the trade window.</summary>
    public string Message = string.Empty;
    /// <summary>The character key we're waiting for the player to switch to (WaitSwitch phase).</summary>
    public string WaitForCharacterKey = string.Empty;
    /// <summary>Display name of the character we're waiting for.</summary>
    public string WaitForCharacterName = string.Empty;
    /// <summary>Account of the character we're waiting for.</summary>
    public string WaitForAccount = string.Empty;
    /// <summary>The resolved target player id for GiveObjectTo (0 until resolved).</summary>
    public uint TargetPlayerId;

    /// <summary>Adds an item from an inventory row. Returns null on success, an error message on failure.</summary>
    public string? Add(InventoryRow row, int quantity)
    {
        if (Phase != TradePhase.Building)
            return "Can't add items while a trade is running.";

        string server = row.Owner.Server;
        if (Server.Length == 0)
            Server = server;
        else if (!InventoryIndex.SameName(Server, server))
            return $"All items must be from the same server. This list is for {Server}.";

        Items.Add(new TradeItem
        {
            CharacterKey = row.Owner.Key,
            CharacterName = row.Owner.Name,
            Account = row.Owner.Account,
            Server = server,
            ItemName = row.Item.Name,
            Wcid = row.Item.Wcid,
            Quantity = quantity,
            ItemId = row.Item.Id,
        });
        return null;
    }

    /// <summary>Removes the item at <paramref name="index"/>.</summary>
    public void RemoveAt(int index)
    {
        if (index >= 0 && index < Items.Count && Phase == TradePhase.Building)
        {
            Items.RemoveAt(index);
            if (Items.Count == 0) Server = string.Empty;
        }
    }

    /// <summary>Returns the distinct character keys that still have pending items, grouped.</summary>
    public List<(string Key, string Name, string Account)> PendingCharacters()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<(string, string, string)>();
        foreach (TradeItem item in Items)
        {
            if (item.Status is TradeItemStatus.Pending or TradeItemStatus.Giving)
            {
                if (seen.Add(item.CharacterKey))
                    result.Add((item.CharacterKey, item.CharacterName, item.Account));
            }
        }
        return result;
    }

    /// <summary>Items on a specific character that are still pending.</summary>
    public List<TradeItem> PendingForCharacter(string characterKey)
    {
        var result = new List<TradeItem>();
        foreach (TradeItem item in Items)
        {
            if (item.Status == TradeItemStatus.Pending
                && string.Equals(item.CharacterKey, characterKey, StringComparison.Ordinal))
                result.Add(item);
        }
        return result;
    }

    /// <summary>True when every item has a terminal status (Given, NotFound, Failed).</summary>
    public bool AllDone()
    {
        foreach (TradeItem item in Items)
            if (item.Status is TradeItemStatus.Pending or TradeItemStatus.Giving) return false;
        return true;
    }

    /// <summary>Counts by status.</summary>
    public (int Given, int NotFound, int Failed, int Pending) Counts()
    {
        int given = 0, notFound = 0, failed = 0, pending = 0;
        foreach (TradeItem item in Items)
        {
            switch (item.Status)
            {
                case TradeItemStatus.Given: given++; break;
                case TradeItemStatus.NotFound: notFound++; break;
                case TradeItemStatus.Failed: failed++; break;
                default: pending++; break;
            }
        }
        return (given, notFound, failed, pending);
    }
}
