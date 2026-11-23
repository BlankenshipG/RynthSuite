// Pure tell / !buy parsing for Trade Shop (no host deps — unit-tested).
using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthAi.Trade;

/// <summary>Parse incoming AC tells and !buy selectors for the Trade Shop.</summary>
public static class TradeShopTellParse
{
    private static readonly Regex TellLineRx = new(
        @"^(?:<Tell:IIDString:\d+:(?<linkName>[^>]+)>[^<]*<\\Tell>|(?<plainName>.+?))\s+tells you,?\s*""(?<msg>.*)""\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Parse AC "Name tells you, \"msg\"" (optional Tell IIDString link).</summary>
    public static bool TryParseTell(string? text, out string sender, out string message)
    {
        sender = "";
        message = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        Match m = TellLineRx.Match(text.Trim());
        if (!m.Success) return false;
        sender = m.Groups["linkName"].Success && m.Groups["linkName"].Length > 0
            ? m.Groups["linkName"].Value.Trim()
            : m.Groups["plainName"].Value.Trim();
        message = m.Groups["msg"].Value;
        return sender.Length > 0;
    }

    /// <summary>!buy selector: 3 / #3 / id:3 / "Exact Name" / partial name. Bare numbers are slots.</summary>
    public static (int? id, string text) ParseBuySelector(string? raw)
    {
        var p = ParsePriceSelector(raw);
        if (p.slot.HasValue) return (p.slot, "");
        return (null, p.text);
    }

    /// <summary>
    /// Price / buy lookup: slot (#N, slot:N, id:N, bare 1..999), value (value:N, py:N, bare ≥1000),
    /// or name text.
    /// </summary>
    public static (int? slot, long? valuePy, string text) ParsePriceSelector(string? raw)
    {
        string text = (raw ?? "").Trim();
        if (text.Length == 0) return (null, null, "");
        if ((text.StartsWith('"') && text.EndsWith('"') && text.Length > 1)
            || (text.StartsWith('\'') && text.EndsWith('\'') && text.Length > 1))
            text = text[1..^1].Trim();

        if (text.StartsWith('#'))
        {
            if (int.TryParse(text[1..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int hashId) && hashId > 0)
                return (hashId, null, "");
        }
        if (text.StartsWith("slot:", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(text[5..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot) && slot > 0)
                return (slot, null, "");
        }
        if (text.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(text[3..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int prefixed) && prefixed > 0)
                return (prefixed, null, "");
        }
        if (text.StartsWith("value:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("py:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("price:", StringComparison.OrdinalIgnoreCase))
        {
            int colon = text.IndexOf(':');
            if (long.TryParse(text[(colon + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long val) && val > 0)
                return (null, val, "");
        }
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) && n > 0)
        {
            // Small bare numbers = catalog slot; large = pyreal price ask.
            if (n < 1000) return ((int)n, null, "");
            return (null, n, "");
        }
        return (null, null, text);
    }

    /// <summary>True when the tell body is a shop verb (with or without leading !).</summary>
    public static bool IsShopVerb(string? cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return false;
        string raw = cmd.Trim();
        if (raw.StartsWith('!')) raw = raw[1..].Trim();
        if (raw.Length == 0) return false;
        string v = raw.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
        return v is "help" or "hi" or "hello" or "shop" or "rates" or "rate" or "fx"
            or "list" or "sheet" or "catalog" or "menu" or "price" or "cost" or "buy";
    }
}
