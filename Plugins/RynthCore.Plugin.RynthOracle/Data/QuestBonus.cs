using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthOracle.Data;

// The ILT Tracker tab's quest bonus (Infinite Leaftide, ACECustom read 2026-11-21, Silentkelpie: "look at acecustom add in
// itl qb bonus levels and bonus tracker"). In ACECustom (Source/ACE.Server):
//   * the quest bonus (QB) is the account's count of quests with a completion row (AccountQuest, NumTimesCompleted >= 1),
//     one per quest name, shared by every character of the account (QuestManager.UpdatePlayerQuestCompletions); the
//     character carries it as PropertyInt64 9006 QuestCount;
//   * each QB is +0.5% kill XP: GetQuestCountXPBonus() = 1 + QB x 0.005 (Player_Xp.questToBonusRation);
//   * "/bonus" prints the four XP multipliers: Quests, Equipment (1 + the XP enchantments + 5% a Bonus XP aug),
//     Enlightenment (1 + ENL x 0.1) and the Total, their product ("[BONUS] Your XP multiplier from Quests is: x2.35");
//   * "/qb" prints "Your current quest bonus count: 270"; "/qb list" the completed and the incomplete quest bonus list
//     (RynthILT reads that one and hands it over: IltQuestBonusList); portals can ask for a QB amount
//     (PortalRequirement.QuestBonus) or a total multiplier (XPMultiplier).
// The bonus levels below are the quest multiplier's quarter steps (x1.25 at 50 QB, x1.5 at 100, ...): the points the
// tracker counts towards. The server has no levels of its own; the multiplier grows by 0.005 with every QB.

/// <summary>The four "/bonus" multipliers as the server last printed them (0 = not seen).</summary>
internal sealed record BonusReading(double Quests, double Equipment, double Enlightenment, double Total, DateTime AtUtc)
{
    public static readonly BonusReading None = new(0, 0, 0, 0, DateTime.MinValue);
    public bool Seen => AtUtc != DateTime.MinValue;
}

/// <summary>One bonus level: a quest multiplier and the QB it takes.</summary>
internal readonly record struct QbLevel(int Level, double Multiplier, long Qb);

internal sealed class QuestBonus
{
    /// <summary>ACECustom's Player_Xp.questToBonusRation: kill XP per QB.</summary>
    public const double PerQb = 0.005;
    /// <summary>ACECustom's Player_Xp.enlightenmentToBonusRatio: kill XP per enlightenment.</summary>
    public const double PerEnlightenment = 0.1;
    /// <summary>A bonus level is this much quest multiplier (x0.25 = 50 QB).</summary>
    public const double LevelStep = 0.25;
    public static readonly long QbPerLevel = (long)Math.Round(LevelStep / PerQb);

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

    /// <summary>The bonus level a QB is at (0 below x1.25).</summary>
    public static int LevelOf(long qb) => (int)(Math.Max(0, qb) / QbPerLevel);

    public static QbLevel Level(int level) => new(level, 1 + level * LevelStep, level * QbPerLevel);

    /// <summary>The level reached, the next <paramref name="ahead"/> levels and how far to the next one.</summary>
    public static (QbLevel Current, List<QbLevel> Next, float Fraction, long ToNext) Levels(long qb, int ahead = 4)
    {
        qb = Math.Max(0, qb);
        int lv = LevelOf(qb);
        var next = new List<QbLevel>();
        for (int i = 1; i <= ahead; i++) next.Add(Level(lv + i));
        long into = qb - lv * QbPerLevel;
        return (Level(lv), next, (float)into / QbPerLevel, QbPerLevel - into);
    }

    /// <summary>
    /// The best QB known: RynthILT's /qb read, else a "/qb" count in chat, else the "/bonus" Quests multiplier
    /// (-1 when none). <paramref name="source"/> says which.
    /// </summary>
    public long BestCount(IltQuestBonusList? ilt, out string source)
    {
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
