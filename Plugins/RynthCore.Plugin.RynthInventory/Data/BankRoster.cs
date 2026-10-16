using System;
using System.Collections.Generic;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthInventory.Data;

/// <summary>One character on the Banking tab: its saved balances (null = never read) and account.</summary>
internal sealed record BankRosterEntry(string Name, string Account, bool Current, IltBankRow? Bank);

/// <summary>
/// The Banking tab's character list (2026-10-13; the Mini Remote's Banking tab moved here). Every
/// character RynthILT has bank balances for on this server (its saved per-character state, so every
/// account played on this PC), plus every character Global Inventory has recorded on the same
/// server without a balance yet, so the list covers the same characters as the inventory does.
/// Pure: the tests feed it a parsed bank view and an index.
/// </summary>
internal static class BankRoster
{
    /// <summary>
    /// The merged list: the logged-in character first, then by name. A name in both lists keeps
    /// RynthILT's balances and the inventory's account.
    /// </summary>
    public static List<BankRosterEntry> Build(IltBankView bank, InventoryIndex? index)
    {
        var byName = new Dictionary<string, BankRosterEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (IltBankRow r in bank.Characters)
            byName[r.Name] = new BankRosterEntry(r.Name, string.Empty, r.Current, r);

        if (index != null)
        {
            foreach (CharacterInfo ci in index.Characters)
            {
                // Same server only: a bank belongs to one world.
                if (!SameServer(ci, bank)) continue;
                string account = !string.IsNullOrEmpty(ci.Account) && ci.Account != "unknown" ? ci.Account : string.Empty;
                if (byName.TryGetValue(ci.Name, out BankRosterEntry? have))
                    byName[ci.Name] = have with { Account = account, Current = have.Current || ci.IsCurrentCharacter };
                else
                    byName[ci.Name] = new BankRosterEntry(ci.Name, account, ci.IsCurrentCharacter, null);
            }
        }

        var list = new List<BankRosterEntry>(byName.Values);
        list.Sort((a, b) => a.Current != b.Current ? (a.Current ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>
    /// A recorded character is on the bank's server: the world RynthILT names when it gave one,
    /// otherwise the server Global Inventory says you are on now.
    /// </summary>
    private static bool SameServer(CharacterInfo ci, IltBankView bank) =>
        bank.World.Length > 0 ? InventoryIndex.SameName(ci.Server, bank.World) : ci.IsCurrentServer;

    /// <summary>The sums over every character with a balance (one row of the same shape).</summary>
    public static IltBankRow Totals(IReadOnlyList<BankRosterEntry> roster)
    {
        long py = 0, lum = 0, lk = 0, mk = 0, ec = 0, wec = 0, pc = 0;
        foreach (BankRosterEntry e in roster)
        {
            if (e.Bank is not { } b) continue;
            py += b.Pyreals; lum += b.Luminance; lk += b.LegendaryKeys; mk += b.MythicalKeys;
            ec += b.EnlightenedCoins; wec += b.WeaklyEnlightenedCoins; pc += b.PrestigeCoins;
        }
        return new IltBankRow("Total", false, py, lum, lk, mk, ec, wec, pc, DateTime.MinValue);
    }

    /// <summary>
    /// The withdraw / transfer amount typed in the form: a positive whole number (commas and spaces
    /// allowed), or 0 when it isn't one.
    /// </summary>
    public static long ParseAmount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        string digits = text.Replace(",", string.Empty).Replace(" ", string.Empty).Trim();
        return long.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long n) && n > 0 ? n : 0;
    }
}
