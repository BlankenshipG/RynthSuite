using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RynthCore.Loot;

/// <summary>
/// A slayer weapon's damage bonus as its appraisal text says it (2026-11-15).
/// <para>
/// ACECustom never sends SlayerDamageBonus (float 138) in an appraisal: the property has no
/// [AssessmentProperty] in ACE.Entity PropertyFloat, and AppraiseInfo writes the bonus only into the
/// weapon's text as "- Shadow Slayer: 1.5x Damage". So a loot rule's "Slayer damage bonus at least
/// 1.01" read 0 on every weapon and never matched (Silentkelpie: "is the any slayer rule working for
/// looting"). The slayer type (int 166, "any slayer") is sent and always worked. The rule reader
/// falls back to this text when float 138 reads nothing.
/// </para>
/// </summary>
public static class SlayerText
{
    /// <summary>SlayerDamageBonus (STypeFloat 138).</summary>
    public const int SlayerDamageBonusKey = 138;

    private static readonly Regex Line = new(@"^\s*-?\s*(?<who>[^\r\n:]+?)\s+Slayer:\s*(?<x>\d+(?:\.\d+)?)x",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>The best "&lt;creature&gt; Slayer: N.Nx" multiplier in the texts (1.5 for "1.5x"), or 0 when none says one.</summary>
    public static double BonusOf(params string?[] texts)
    {
        double best = 0;
        foreach (string? t in texts)
        {
            if (string.IsNullOrEmpty(t) || t.IndexOf("Slayer", StringComparison.OrdinalIgnoreCase) < 0) continue;
            foreach (Match m in Line.Matches(t))
                if (double.TryParse(m.Groups["x"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) && x > best)
                    best = x;
        }
        return best;
    }
}
