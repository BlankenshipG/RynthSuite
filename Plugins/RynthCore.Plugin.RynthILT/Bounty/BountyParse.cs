// BountyParse.cs - Parses ACECustom's bounty output (BountyManager / BountyCommands).
//
// The progress itself (PropertyString BountyProgress) is server-only, so the tracker reads
// what the server prints. All lines are ChatMessageType.Broadcast:
//   /bounty list   "Bounties:" header, then one line per reward of every active area:
//                  "  {where}: {amount} {item} every {N kill(s)}[ (at most every {wait})] - you: {status}."
//                  footer "Bounty: type /bounty to see the bounties where you stand."
//                  or "Bounty: there are no bounties anywhere right now."
//   /bounty        "Bounty[ (all zones)| ({region})]: {amount} {item} - {status}."
//                  or "Bounty: there is no bounty here."
//   status         "{k} of {n} kills" | "unlocks in {wait}, then {N kill(s)} required" | "needs {qb} QB (you have {n})"
//   events         "Bounty unlocked: {item} - {N kill(s)} required."   "Bounty complete! +{n} {item} gained."
//                  "Next bounty unlocks in {wait} - {N kill(s)} required."   "Next bounty requires {N kill(s)}."
//                  held rewards: "Bounty: +{n} ..." / "Bounty: Your inventory is full. ..."
//   wait           "45 sec", "5 min", "2 min 30 sec", "1 hr 15 sec" (FormatWait); numbers use N0 grouping.
// A server without the bounty system answers "Unknown command: bounty".
using System;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthILT.Bounty;

/// <summary>Where the player stands on one bounty reward.</summary>
internal enum BountyStatus
{
    /// <summary>Kills are counting: Kills of KillsRequired.</summary>
    Counting = 0,
    /// <summary>The cooldown after the last award runs; kills do not count until UnlockAtUtc.</summary>
    Cooldown = 1,
    /// <summary>The player's QB is below the reward's minimum; kills do not count.</summary>
    NeedsQb = 2,
}

/// <summary>One parsed status phrase.</summary>
internal readonly record struct BountyStatusInfo(BountyStatus Status, int Kills, int KillsRequired,
                                                 int WaitSeconds, long QbRequired, long QbHave);

/// <summary>One "/bounty list" line.</summary>
internal readonly record struct BountyListLine(string Where, long Amount, string Item, int KillsRequired,
                                               int CooldownSeconds, BountyStatusInfo Status);

/// <summary>One "/bounty" (where you stand) line. Scope is "" (this dungeon/zone), "all zones" or a region name.</summary>
internal readonly record struct BountyHereLine(string Scope, long Amount, string Item, BountyStatusInfo Status);

/// <summary>Kind of a bounty chat event.</summary>
internal enum BountyEventKind { Unlocked, Complete, NextCooldown, NextReady, Held }

internal static class BountyParse
{
    private const RegexOptions Opt = RegexOptions.Compiled | RegexOptions.CultureInvariant;
    private const string Num = @"\d[\d,]*";

    // Status alternatives are spelled out inside the line patterns so backtracking can place the
    // " - " separator correctly when an item name itself contains " - ".
    private const string StatusPattern =
        @"(?<status>" + Num + @" of " + Num + @" kills?|unlocks in .+?, then " + Num + @" kills? required|needs "
        + Num + @" QB \(you have " + Num + @"\))";

    /// <summary>"/bounty list" reward line (leading indentation already trimmed).</summary>
    private static readonly Regex ListLine = new(
        @"^(?<where>.+?): (?<amount>" + Num + @") (?<item>.+?) every (?<kills>" + Num + @") kills?"
        + @"(?: \(at most every (?<cd>[^)]+)\))? - you: " + StatusPattern + @"\.$", Opt);

    /// <summary>"/bounty" reward line.</summary>
    private static readonly Regex HereLine = new(
        @"^Bounty(?: \((?<scope>[^)]+)\))?: (?<amount>" + Num + @") (?<item>.+?) - " + StatusPattern + @"\.$", Opt);

    private static readonly Regex Counting = new(@"^(?<k>" + Num + @") of (?<n>" + Num + @") kills?$", Opt);
    private static readonly Regex Cooldown = new(@"^unlocks in (?<wait>.+?), then (?<n>" + Num + @") kills? required$", Opt);
    private static readonly Regex Qb = new(@"^needs (?<qb>" + Num + @") QB \(you have (?<have>" + Num + @")\)$", Opt);
    private static readonly Regex WaitPart = new(@"(?<v>\d+)\s*(?<u>hr|min|sec)\b", Opt);

    private static readonly Regex EvUnlocked = new(@"^Bounty unlocked: (?<item>.+) - (?<n>" + Num + @") kills? required\.$", Opt);
    private static readonly Regex EvComplete = new(@"^Bounty complete! \+(?<n>" + Num + @") (?<item>.+) gained\.$", Opt);
    private static readonly Regex EvNextWait = new(@"^Next bounty unlocks in (?<wait>.+?) - (?<n>" + Num + @") kills? required\.$", Opt);
    private static readonly Regex EvNextReady = new(@"^Next bounty requires (?<n>" + Num + @") kills?\.$", Opt);

    /// <summary>"/bounty list" header line.</summary>
    public const string ListHeader = "Bounties:";

    /// <summary>True for the line that ends a "/bounty list" reply (footer or "no bounties anywhere").</summary>
    public static bool IsListTerminator(string text)
        => text.StartsWith("Bounty: type /bounty", StringComparison.OrdinalIgnoreCase)
           || text.StartsWith("Bounty: there are no bounties anywhere", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the terminator says no bounty is active anywhere.</summary>
    public static bool IsNoBountiesAnywhere(string text)
        => text.StartsWith("Bounty: there are no bounties anywhere", StringComparison.OrdinalIgnoreCase);

    /// <summary>ACE's reply to a command the server doesn't register.</summary>
    public static bool IsUnknownCommand(string text)
        => text.StartsWith("Unknown command: bounty", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses a "/bounty list" reward line.</summary>
    public static bool TryParseListLine(string text, out BountyListLine line)
    {
        line = default;
        var m = ListLine.Match((text ?? string.Empty).Trim());
        if (!m.Success || !TryParseStatus(m.Groups["status"].Value, out var status)) return false;
        int required = ToInt(m.Groups["kills"].Value);
        int cooldown = m.Groups["cd"].Success ? ParseWaitSeconds(m.Groups["cd"].Value) : 0;
        line = new BountyListLine(m.Groups["where"].Value.Trim(), ParseLong(m.Groups["amount"].Value),
                                  m.Groups["item"].Value.Trim(), required, cooldown, status);
        return true;
    }

    /// <summary>Parses a "/bounty" (where you stand) reward line.</summary>
    public static bool TryParseHereLine(string text, out BountyHereLine line)
    {
        line = default;
        var m = HereLine.Match((text ?? string.Empty).Trim());
        if (!m.Success || !TryParseStatus(m.Groups["status"].Value, out var status)) return false;
        line = new BountyHereLine(m.Groups["scope"].Success ? m.Groups["scope"].Value.Trim() : string.Empty,
                                  ParseLong(m.Groups["amount"].Value), m.Groups["item"].Value.Trim(), status);
        return true;
    }

    /// <summary>Parses one status phrase (without the trailing period).</summary>
    public static bool TryParseStatus(string text, out BountyStatusInfo status)
    {
        status = default;
        string t = (text ?? string.Empty).Trim().TrimEnd('.');
        var m = Counting.Match(t);
        if (m.Success)
        {
            status = new BountyStatusInfo(BountyStatus.Counting, ToInt(m.Groups["k"].Value), ToInt(m.Groups["n"].Value), 0, 0, 0);
            return true;
        }
        m = Cooldown.Match(t);
        if (m.Success)
        {
            status = new BountyStatusInfo(BountyStatus.Cooldown, 0, ToInt(m.Groups["n"].Value),
                                          ParseWaitSeconds(m.Groups["wait"].Value), 0, 0);
            return true;
        }
        m = Qb.Match(t);
        if (m.Success)
        {
            status = new BountyStatusInfo(BountyStatus.NeedsQb, 0, 0, 0,
                                          ParseLong(m.Groups["qb"].Value), ParseLong(m.Groups["have"].Value));
            return true;
        }
        return false;
    }

    /// <summary>"2 min 30 sec" → 150, "1 hr 15 sec" → 3615. 0 when nothing parses.</summary>
    public static int ParseWaitSeconds(string text)
    {
        long total = 0;
        foreach (Match m in WaitPart.Matches(text ?? string.Empty))
        {
            long v = ParseLong(m.Groups["v"].Value);
            total += m.Groups["u"].Value switch { "hr" => v * 3600, "min" => v * 60, _ => v };
        }
        return (int)Math.Min(total, int.MaxValue);
    }

    /// <summary>
    /// Recognises a bounty chat event (award, unlock, next cooldown, held reward). Item and
    /// KillsRequired are filled when the line names them; WaitSeconds for "Next bounty unlocks in".
    /// </summary>
    public static bool TryParseEvent(string text, out BountyEventKind kind, out string item, out int killsRequired, out int waitSeconds)
    {
        kind = BountyEventKind.Held;
        item = string.Empty;
        killsRequired = 0;
        waitSeconds = 0;
        string t = (text ?? string.Empty).Trim();
        if (t.Length == 0) return false;

        Match m;
        if ((m = EvUnlocked.Match(t)).Success)
        {
            kind = BountyEventKind.Unlocked;
            item = m.Groups["item"].Value.Trim();
            killsRequired = ToInt(m.Groups["n"].Value);
            return true;
        }
        if ((m = EvComplete.Match(t)).Success)
        {
            kind = BountyEventKind.Complete;
            item = m.Groups["item"].Value.Trim();
            return true;
        }
        if ((m = EvNextWait.Match(t)).Success)
        {
            kind = BountyEventKind.NextCooldown;
            waitSeconds = ParseWaitSeconds(m.Groups["wait"].Value);
            killsRequired = ToInt(m.Groups["n"].Value);
            return true;
        }
        if ((m = EvNextReady.Match(t)).Success)
        {
            kind = BountyEventKind.NextReady;
            killsRequired = ToInt(m.Groups["n"].Value);
            return true;
        }
        // Held-reward notices; the "/bounty" and "/bounty list" status lines also start with "Bounty: ".
        if (t.StartsWith("Bounty: +", StringComparison.Ordinal)
            || t.StartsWith("Bounty: Your inventory is full", StringComparison.OrdinalIgnoreCase))
        {
            kind = BountyEventKind.Held;
            return true;
        }
        return false;
    }

    /// <summary>Key that identifies one reward of one area across refreshes.</summary>
    public static string Key(string where, long amount, string item, int killsRequired)
        => $"{where}|{amount}|{item}|{killsRequired}".ToLowerInvariant();

    /// <summary>"1 hr 5 min", "45 sec", "2 days 3 hr". Negative spans read as 0 sec.</summary>
    public static string Duration(TimeSpan span)
    {
        long s = Math.Max(0, (long)span.TotalSeconds);
        if (s >= 86400) return $"{s / 86400} day{(s / 86400 == 1 ? "" : "s")}" + (s % 86400 >= 3600 ? $" {s % 86400 / 3600} hr" : "");
        if (s >= 3600) return $"{s / 3600} hr" + (s % 3600 >= 60 ? $" {s % 3600 / 60} min" : "");
        if (s >= 60) return $"{s / 60} min" + (s % 60 > 0 ? $" {s % 60} sec" : "");
        return $"{s} sec";
    }

    /// <summary>Digits with "," grouping removed; 0 when nothing parses.</summary>
    internal static long ParseLong(string text)
    {
        string digits = (text ?? string.Empty).Replace(",", string.Empty).Trim();
        return long.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long v) ? v : 0;
    }

    private static int ToInt(string text) => (int)Math.Min(ParseLong(text), int.MaxValue);
}
