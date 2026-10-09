// IltBountyParse.cs - Parses ACECustom's Bounty output (BountyManager / BountyCommands).
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
using System;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthAi.IltHub;

/// <summary>Where the player stands on one bounty reward.</summary>
internal enum IltBountyStatus
{
    /// <summary>Kills are counting: Kills of KillsRequired.</summary>
    Counting = 0,
    /// <summary>The cooldown after the last award runs; kills do not count until UnlockAtUtc.</summary>
    Cooldown = 1,
    /// <summary>The player's QB is below the reward's minimum; kills do not count.</summary>
    NeedsQb = 2,
}

/// <summary>One parsed status phrase.</summary>
internal readonly record struct IltBountyStatusInfo(IltBountyStatus Status, int Kills, int KillsRequired,
                                                    int WaitSeconds, long QbRequired, long QbHave);

/// <summary>One "/bounty list" line.</summary>
internal readonly record struct IltBountyListLine(string Where, long Amount, string Item, int KillsRequired,
                                                  int CooldownSeconds, IltBountyStatusInfo Status);

/// <summary>One "/bounty" (where you stand) line. Scope is "" (this dungeon/zone), "all zones" or a region name.</summary>
internal readonly record struct IltBountyHereLine(string Scope, long Amount, string Item, IltBountyStatusInfo Status);

/// <summary>Kind of a bounty chat event.</summary>
internal enum IltBountyEventKind { Unlocked, Complete, NextCooldown, NextReady, Held }

internal static class IltBountyParse
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

    /// <summary>Parses a "/bounty list" reward line.</summary>
    public static bool TryParseListLine(string text, out IltBountyListLine line)
    {
        line = default;
        var m = ListLine.Match((text ?? string.Empty).Trim());
        if (!m.Success || !TryParseStatus(m.Groups["status"].Value, out var status)) return false;
        int required = ToInt(m.Groups["kills"].Value);
        int cooldown = m.Groups["cd"].Success ? ParseWaitSeconds(m.Groups["cd"].Value) : 0;
        line = new IltBountyListLine(m.Groups["where"].Value.Trim(), IltParse.ParseLeadingLong(m.Groups["amount"].Value),
                                     m.Groups["item"].Value.Trim(), required, cooldown, status);
        return true;
    }

    /// <summary>Parses a "/bounty" (where you stand) reward line.</summary>
    public static bool TryParseHereLine(string text, out IltBountyHereLine line)
    {
        line = default;
        var m = HereLine.Match((text ?? string.Empty).Trim());
        if (!m.Success || !TryParseStatus(m.Groups["status"].Value, out var status)) return false;
        line = new IltBountyHereLine(m.Groups["scope"].Success ? m.Groups["scope"].Value.Trim() : string.Empty,
                                     IltParse.ParseLeadingLong(m.Groups["amount"].Value), m.Groups["item"].Value.Trim(), status);
        return true;
    }

    /// <summary>Parses one status phrase (without the trailing period).</summary>
    public static bool TryParseStatus(string text, out IltBountyStatusInfo status)
    {
        status = default;
        string t = (text ?? string.Empty).Trim().TrimEnd('.');
        var m = Counting.Match(t);
        if (m.Success)
        {
            status = new IltBountyStatusInfo(IltBountyStatus.Counting, ToInt(m.Groups["k"].Value), ToInt(m.Groups["n"].Value), 0, 0, 0);
            return true;
        }
        m = Cooldown.Match(t);
        if (m.Success)
        {
            status = new IltBountyStatusInfo(IltBountyStatus.Cooldown, 0, ToInt(m.Groups["n"].Value),
                                             ParseWaitSeconds(m.Groups["wait"].Value), 0, 0);
            return true;
        }
        m = Qb.Match(t);
        if (m.Success)
        {
            status = new IltBountyStatusInfo(IltBountyStatus.NeedsQb, 0, 0, 0,
                                             IltParse.ParseLeadingLong(m.Groups["qb"].Value), IltParse.ParseLeadingLong(m.Groups["have"].Value));
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
            long v = IltParse.ParseLeadingLong(m.Groups["v"].Value);
            total += m.Groups["u"].Value switch { "hr" => v * 3600, "min" => v * 60, _ => v };
        }
        return (int)Math.Min(total, int.MaxValue);
    }

    /// <summary>
    /// Recognises a bounty chat event (award, unlock, next cooldown, held reward). Item and
    /// KillsRequired are filled when the line names them; WaitSeconds for "Next bounty unlocks in".
    /// </summary>
    public static bool TryParseEvent(string text, out IltBountyEventKind kind, out string item, out int killsRequired, out int waitSeconds)
    {
        kind = IltBountyEventKind.Held;
        item = string.Empty;
        killsRequired = 0;
        waitSeconds = 0;
        string t = (text ?? string.Empty).Trim();
        if (t.Length == 0) return false;

        Match m;
        if ((m = EvUnlocked.Match(t)).Success)
        {
            kind = IltBountyEventKind.Unlocked;
            item = m.Groups["item"].Value.Trim();
            killsRequired = ToInt(m.Groups["n"].Value);
            return true;
        }
        if ((m = EvComplete.Match(t)).Success)
        {
            kind = IltBountyEventKind.Complete;
            item = m.Groups["item"].Value.Trim();
            return true;
        }
        if ((m = EvNextWait.Match(t)).Success)
        {
            kind = IltBountyEventKind.NextCooldown;
            waitSeconds = ParseWaitSeconds(m.Groups["wait"].Value);
            killsRequired = ToInt(m.Groups["n"].Value);
            return true;
        }
        if ((m = EvNextReady.Match(t)).Success)
        {
            kind = IltBountyEventKind.NextReady;
            killsRequired = ToInt(m.Groups["n"].Value);
            return true;
        }
        // Held-reward notices; the "/bounty" and "/bounty list" status lines also start with "Bounty: ".
        if (t.StartsWith("Bounty: +", StringComparison.Ordinal)
            || t.StartsWith("Bounty: Your inventory is full", StringComparison.OrdinalIgnoreCase))
        {
            kind = IltBountyEventKind.Held;
            return true;
        }
        return false;
    }

    /// <summary>Key that identifies one reward of one area across refreshes.</summary>
    public static string Key(string where, long amount, string item, int killsRequired)
        => $"{where}|{amount}|{item}|{killsRequired}".ToLowerInvariant();

    private static int ToInt(string text) => (int)Math.Min(IltParse.ParseLeadingLong(text), int.MaxValue);
}
