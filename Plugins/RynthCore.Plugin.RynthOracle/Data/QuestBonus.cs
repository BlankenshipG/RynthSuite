using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthOracle.Data;

// The ILT Tracker tab's quest bonus (Infinite Leaftide, ACECustom read 2026-11-21, Silentkelpie: "look at acecustom add in
// itl qb bonus levels and bonus tracker"). In ACECustom (Source/ACE.Server):
//   * the quest bonus (QB) is the account's: one per quest row (AccountQuest, written when any character of the account first
//     stamps the quest) and one more once it has a completion (Account.CachedQuestBonusCount, what Deeds and the QB bounty
//     read); the character carries it as PropertyInt64 9006 QuestCount;
//   * each QB is +0.5% kill XP: GetQuestCountXPBonus() = 1 + QB x 0.005 (Player_Xp.questToBonusRation);
//   * "/bonus" prints the four XP multipliers: Quests, Equipment (1 + the XP enchantments + 5% a Bonus XP aug),
//     Enlightenment (1 + ENL x 0.1) and the Total, their product ("[BONUS] Your XP multiplier from Quests is: x2.35");
//   * "/qb" prints "Your current quest bonus count: 270"; "/qb list" the completed and the incomplete quest bonus list
//     (RynthILT reads that one and hands it over: IltQuestBonusList); portals can ask for a QB amount
//     (PortalRequirement.QuestBonus) or a total multiplier (XPMultiplier).
// The QB levels are ACECustom's QB Deeds: a tier every 1,000 QB, up to 15 (Data/QbDeeds.cs).

/// <summary>The four "/bonus" multipliers as the server last printed them (0 = not seen).</summary>
internal sealed record BonusReading(double Quests, double Equipment, double Enlightenment, double Total, DateTime AtUtc)
{
    public static readonly BonusReading None = new(0, 0, 0, 0, DateTime.MinValue);
    public bool Seen => AtUtc != DateTime.MinValue;
}

internal sealed class QuestBonus
{
    /// <summary>ACECustom's Player_Xp.questToBonusRation: kill XP per QB.</summary>
    public const double PerQb = 0.005;
    /// <summary>ACECustom's Player_Xp.enlightenmentToBonusRatio: kill XP per enlightenment.</summary>
    public const double PerEnlightenment = 0.1;

    private static readonly Regex BonusLine = new(
        @"^\[BONUS\] Your (?:(Total) XP multiplier|XP multiplier from (Quests|Equipment|Enlightenment)) is: x([0-9][0-9,]*(?:\.[0-9]+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex CountLine = new(@"^Your current quest bonus count: ([0-9][0-9,]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private double _q, _e, _n, _t;

    /// <summary>The last "/bonus" reply (its four lines).</summary>
    public BonusReading Bonus { get; private set; } = BonusReading.None;
    /// <summary>The last "/qb" count seen in chat (-1 none).</summary>
    public long ChatCount { get; private set; } = -1;
    /// <summary>Goes up when anything here changes (the view redraws).</summary>
    public int Revision { get; private set; }
    /// <summary>Goes up at each logout (the view asks for /bonus and the /qb list again once a session).</summary>
    public int Session { get; private set; }

    public void Clear()
    {
        Bonus = BonusReading.None;
        ChatCount = -1;
        _q = _e = _n = _t = 0;
        Revision++;
        Session++;
    }

    /// <summary>A chat line: true when it is a "/bonus" or "/qb" count line (read, not eaten).</summary>
    public bool OnChatLine(string line, DateTime utcNow)
    {
        string t = line.Trim();
        Match m = CountLine.Match(t);
        if (m.Success)
        {
            if (long.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n))
            {
                ChatCount = n;
                Revision++;
            }
            return true;
        }
        m = BonusLine.Match(t);
        if (!m.Success) return false;
        if (!double.TryParse(m.Groups[3].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return true;
        string which = m.Groups[1].Success ? "total" : m.Groups[2].Value.ToLowerInvariant();
        switch (which)
        {
            case "quests": _q = v; break;
            case "equipment": _e = v; break;
            case "enlightenment": _n = v; break;
            default: _t = v; break;
        }
        // The server prints Quests, Equipment, Enlightenment, then Total: the reading is whole at Total.
        if (which == "total") Bonus = new BonusReading(_q, _e, _n, _t, utcNow);
        Revision++;
        return true;
    }

    /// <summary>The quest multiplier a QB gives: 1 + QB x 0.005.</summary>
    public static double Multiplier(long qb) => 1 + Math.Max(0, qb) * PerQb;

    /// <summary>The QB a "/bonus" Quests multiplier stands for (it prints two decimals: about +/- 1 QB).</summary>
    public static long QbFromMultiplier(double m) => m <= 1 ? 0 : (long)Math.Round((m - 1) / PerQb);

    /// <summary>
    /// The best QB known: RynthILT's /deeds or /qb read, else a "/qb" count in chat, else the "/bonus" Quests multiplier
    /// (-1 when none). <paramref name="source"/> says which.
    /// </summary>
    public long BestCount(IltQuestBonusList? ilt, out string source, IltDeedsInfo? deeds = null)
    {
        if (deeds != null && deeds.Loaded && deeds.Qb >= 0) { source = "RynthILT's /deeds read"; return deeds.Qb; }
        if (ilt != null && ilt.Count >= 0) { source = "RynthILT's /qb read"; return ilt.Count; }
        if (ilt != null && ilt.Generation > 0 && ilt.Completed.Count > 0) { source = "the /qb list's completed quests"; return ilt.Completed.Count; }
        if (ChatCount >= 0) { source = "/qb"; return ChatCount; }
        if (Bonus.Seen && Bonus.Quests > 0) { source = "/bonus (to about 1 QB)"; return QbFromMultiplier(Bonus.Quests); }
        source = "";
        return -1;
    }

    /// <summary>A quest bonus flag that is a Dynamic stamp (they count towards QB as any quest does).</summary>
    public static bool IsStamp(string flag) => flag.StartsWith(DynamicStampList.FlagPrefix, StringComparison.OrdinalIgnoreCase);
}
