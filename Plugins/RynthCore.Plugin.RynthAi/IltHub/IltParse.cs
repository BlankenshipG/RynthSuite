// IltParse.cs — Shared parsing / formatting helpers for ILT Hub server output.
//
// Centralised so every feature uses the same number parsing (commas, k/m/b/t suffixes),
// the same compact K/M/B display, and the same regexes. Regexes are static and built once;
// RegexOptions.Compiled is harmless under NativeAOT (it falls back to the interpreter).
using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthAi.IltHub;

internal static class IltParse
{
    private const RegexOptions Opt = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    /// <summary>"/aug" level line: "Creature: 1,234".</summary>
    public static readonly Regex AugLine = new(@"^(\w+):\s*([\d,]+)$", Opt);

    /// <summary>"/pets" / "/shinies" roster entry: "12. Shiny Drudge Ravener".</summary>
    public static readonly Regex RosterLine = new(@"^\s*(\d+)\.\s+(.+)$", Opt);

    /// <summary>Bank-command amount entry: "1.5m", "250,000", "2b".</summary>
    public static readonly Regex AmountInput = new(@"^([\d,.]+)\s*([kmbt]?)$", Opt | RegexOptions.IgnoreCase);

    /// <summary>A comma-grouped or plain integer as printed by ACE's N0 format.</summary>
    public const string AmountPattern = @"(?:\d{1,3}(?:,\d{3})+|\d+)";

    /// <summary>"Transferred 5,000 Pyreals to Bob" (optional " (offline)" / trailing "--- note").</summary>
    public static readonly Regex BankSent = new(
        @"^Transferred (" + AmountPattern + @") ([^']+?) to ([^-]+?)(?:\s*---.*)?$", Opt);

    /// <summary>"Received 5,000 Pyreals from Bob".</summary>
    public static readonly Regex BankReceived = new(
        @"^Received (" + AmountPattern + @") ([^']+?) from (.+?)(?:\s*---.*)?$", Opt);

    /// <summary>"You've banked 1,234 Luminance" (session rates).</summary>
    public static readonly Regex LumBanked = new(@"You've banked (?<amt>[\d,]+)\s+Luminance", Opt | RegexOptions.IgnoreCase);

    /// <summary>"/xp all" cost line: "[XP] Your XP cost for next 1 Strength level is: 1,234".</summary>
    public static readonly Regex XpCost = new(@"\[XP\] Your XP cost for next 1 (\w+) level is: ([\d,]+)", Opt);

    /// <summary>"/attr" success: "[ATTR] Strength attribute raised by 10, costing 1,234!".</summary>
    public static readonly Regex AttrRaised = new(@"^\[ATTR\] (\w+) attribute raised by (\d+), costing ([\d,]+)", Opt);

    /// <summary>Strips commas and parses the leading integer; 0 when absent.</summary>
    public static long ParseLeadingLong(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        string s = text.Replace(",", string.Empty).Trim();
        int end = 0;
        while (end < s.Length && char.IsDigit(s[end])) end++;
        return end > 0 && long.TryParse(s.AsSpan(0, end), NumberStyles.None, CultureInfo.InvariantCulture, out long v) ? v : 0;
    }

    /// <summary>
    /// Parses a user-entered amount with optional k/m/b/t suffix ("1.5m" → 1,500,000).
    /// Returns false for empty, negative, or overflowing input.
    /// </summary>
    public static bool TryParseAmount(string input, out long amount)
    {
        amount = 0;
        var m = AmountInput.Match((input ?? string.Empty).Trim());
        if (!m.Success) return false;
        if (!decimal.TryParse(m.Groups[1].Value.Replace(",", string.Empty), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out decimal baseVal))
            return false;
        decimal mult = char.ToLowerInvariant(m.Groups[2].Value.Length > 0 ? m.Groups[2].Value[0] : ' ') switch
        {
            'k' => 1_000m,
            'm' => 1_000_000m,
            'b' => 1_000_000_000m,
            't' => 1_000_000_000_000m,
            _ => 1m,
        };
        decimal total = Math.Floor(baseVal * mult);
        if (total <= 0 || total > long.MaxValue) return false;
        amount = (long)total;
        return true;
    }

    /// <summary>Compact display: 950 → "950", 12_300 → "12.3K", 4_500_000 → "4.50M", 2e9 → "2.00B".</summary>
    public static string Compact(double value)
    {
        double a = Math.Abs(value);
        string s = a >= 1e12 ? (value / 1e12).ToString("0.00", CultureInfo.InvariantCulture) + "T"
                 : a >= 1e9 ? (value / 1e9).ToString("0.00", CultureInfo.InvariantCulture) + "B"
                 : a >= 1e6 ? (value / 1e6).ToString("0.00", CultureInfo.InvariantCulture) + "M"
                 : a >= 1e3 ? (value / 1e3).ToString("0.0", CultureInfo.InvariantCulture) + "K"
                 : value.ToString("0", CultureInfo.InvariantCulture);
        return s;
    }

    /// <summary>Full grouped display: 1234567 → "1,234,567".</summary>
    public static string N0(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"3d 4h 12m 5s" style duration; "ready" when zero or negative.</summary>
    public static string Duration(TimeSpan span)
    {
        if (span <= TimeSpan.Zero) return "ready";
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m";
        if (span.TotalHours >= 1) return $"{span.Hours}h {span.Minutes}m {span.Seconds}s";
        if (span.TotalMinutes >= 1) return $"{span.Minutes}m {span.Seconds}s";
        return $"{span.Seconds}s";
    }

    /// <summary>Strips "&lt;Tell:…&gt;" style markup the chat window sometimes leaves on names.</summary>
    public static string StripMarkup(string text)
        => Regex.Replace(text ?? string.Empty, @"<[^>]*>", string.Empty).Trim();
}
