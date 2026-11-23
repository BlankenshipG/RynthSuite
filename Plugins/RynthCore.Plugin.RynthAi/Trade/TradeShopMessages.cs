// Build tell/advertise lines: rates, price sheet chunks, price lookup (pure; unit-testable).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RynthCore.Plugin.RynthAi.Trade;

/// <summary>Catalog message builders and price lookup (name / slot / pyreal value).</summary>
public static class TradeShopMessages
{
    /// <summary>AC tell / chat line soft limit (leave headroom for /tell Name, ).</summary>
    public const int MaxLineChars = 220;

    public static string RatesLine(TradeShopRates? rates)
        => "Rates: " + (rates ?? new TradeShopRates()).RatesHelpLine();

    /// <summary>One catalog row: #slot name | sku | price alts.</summary>
    public static string SheetRow(int slot1Based, TradeShopListing listing, TradeShopRates? rates)
    {
        string name = string.IsNullOrWhiteSpace(listing.Name) ? "(unnamed)" : listing.Name.Trim();
        string sku = string.IsNullOrWhiteSpace(listing.Sku) ? "" : " | sku " + listing.Sku.Trim();
        return $"#{slot1Based} {name}{sku} | {listing.PriceLabel(rates)}";
    }

    /// <summary>Full price sheet as one or more tell-sized chunks (rates first when included).</summary>
    public static List<string> BuildPriceSheetChunks(
        IReadOnlyList<TradeShopListing> listings,
        TradeShopRates? rates,
        bool includeRates,
        string? header = null)
    {
        var chunks = new List<string>();
        var sb = new StringBuilder();

        void Flush()
        {
            if (sb.Length == 0) return;
            chunks.Add(sb.ToString());
            sb.Clear();
        }

        void AppendPart(string part)
        {
            if (string.IsNullOrEmpty(part)) return;
            if (sb.Length == 0)
            {
                if (part.Length <= MaxLineChars) { sb.Append(part); return; }
                // Hard-split oversized part.
                for (int i = 0; i < part.Length; i += MaxLineChars)
                    chunks.Add(part.Substring(i, Math.Min(MaxLineChars, part.Length - i)));
                return;
            }
            if (sb.Length + 3 + part.Length <= MaxLineChars)
            {
                sb.Append(" || ").Append(part);
                return;
            }
            Flush();
            AppendPart(part);
        }

        if (!string.IsNullOrWhiteSpace(header))
            AppendPart(header.Trim());
        if (includeRates)
            AppendPart(RatesLine(rates));

        if (listings == null || listings.Count == 0)
        {
            AppendPart("Price sheet: (empty)");
            Flush();
            return chunks;
        }

        AppendPart($"Price sheet ({listings.Count}):");
        for (int i = 0; i < listings.Count; i++)
        {
            var row = listings[i];
            if (row == null) continue;
            AppendPart(SheetRow(i + 1, row, rates));
        }
        Flush();
        return chunks;
    }

    /// <summary>
    /// Resolve a listing by slot (#N / id:N / bare 1..Count), pyreal value (bare large N / value:N / py:N),
    /// or name / SKU (exact then partial).
    /// </summary>
    public static TradeShopListing? ResolveListing(IReadOnlyList<TradeShopListing> list, string? arg)
    {
        if (list == null || list.Count == 0) return null;
        var parsed = TradeShopTellParse.ParsePriceSelector(arg);
        if (parsed.slot.HasValue)
        {
            int idx = parsed.slot.Value - 1;
            if (idx >= 0 && idx < list.Count) return list[idx];
        }
        if (parsed.valuePy.HasValue)
        {
            long v = parsed.valuePy.Value;
            TradeShopListing? exact = list.FirstOrDefault(l => l != null && l.PricePyreals == v);
            if (exact != null) return exact;
            // Closest price at or above the asked value, else nearest.
            TradeShopListing? atOrAbove = list
                .Where(l => l != null && l.PricePyreals >= v)
                .OrderBy(l => l!.PricePyreals)
                .FirstOrDefault();
            if (atOrAbove != null) return atOrAbove;
            return list.Where(l => l != null).OrderBy(l => Math.Abs(l!.PricePyreals - v)).FirstOrDefault();
        }
        if (!string.IsNullOrWhiteSpace(parsed.text))
        {
            string needle = parsed.text.Trim();
            TradeShopListing? exact = list.FirstOrDefault(l =>
                l != null && (string.Equals(l.Name, needle, StringComparison.OrdinalIgnoreCase)
                              || string.Equals(l.Sku, needle, StringComparison.OrdinalIgnoreCase)));
            if (exact != null) return exact;
            return list.FirstOrDefault(l =>
                l != null && ((l.Name ?? "").IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                              || (l.Sku ?? "").IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0));
        }
        return null;
    }

    public static string HelpLine()
        => "Trade Shop: !help !rates !list/!sheet !price <slot|name|value> !buy <slot|name|value>. "
         + "Slot=#3; value=pyreals (e.g. 250000 or value:250000).";
}
