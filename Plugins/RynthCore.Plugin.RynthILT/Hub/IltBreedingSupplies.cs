// IltBreedingSupplies.cs - Ivo's breeding supplies (ACECustom, Seedy Motel vendor) and when each can
// be used on an essence.
//
// WCIDs, prices and refusals copied from ACECustom (Player_Use.cs HandleActionUseOnTarget, the
// PetDevice.BreedingMath incense constants, docs/PET_BREEDING_PLAYER_GUIDE.md). RynthILT applies
// only the breeding boosters (incense, Nurturing Draught, Chromatic Catalyst, Offering of
// Subjugation), and only after the player confirms; the Neutering Kit is permanent and the kits,
// serum and tinctures are cosmetic, so those are counted, never used from here.
//
// Pure: RynthILT.Tests checks the refusals offline.
using System;
using System.Globalization;

namespace RynthCore.Plugin.RynthILT.Hub;

/// <summary>One supply: key, WCID, name, what it does, Ivo's price, and whether the tracker can apply it.</summary>
internal sealed record BreedSupplyDef(string Key, uint Wcid, string Name, string Effect, long Price, bool Apply);

internal static class IltBreedingSupplies
{
    public const string LesserIncense = "incense1";
    public const string RefinedIncense = "incense2";
    public const string ExquisiteIncense = "incense3";
    public const string Draught = "draught";
    public const string Catalyst = "catalyst";
    public const string Offering = "offering";
    public const string NeuteringKit = "neuter";
    public const string TailoringKit = "tailor";
    public const string FilledTailoringKit = "tailor_filled";
    public const string Serum = "serum";
    public const string Solidifying = "solidify";
    public const string Fading = "fade";

    /// <summary>Every supply, boosters first.</summary>
    public static readonly BreedSupplyDef[] All =
    {
        new(LesserIncense, 78780250, "Lesser Courtship Incense", "+2.5% stat mutation chance on the next breed.", 1_250_000, true),
        new(RefinedIncense, 78780251, "Refined Courtship Incense", "+5% stat mutation chance on the next breed.", 2_500_000, true),
        new(ExquisiteIncense, 78780252, "Exquisite Courtship Incense", "+10% stat mutation chance on the next breed.", 5_000_000, true),
        new(Draught, 78780253, "Nurturing Draught", "A juvenile earns 2x maturity kill credit until adulthood.", 2_500_000, true),
        new(Catalyst, 78780254, "Chromatic Catalyst", "The next baby rolls a fresh colour.", 2_500_000, true),
        new(Offering, 78780255, "Offering of Subjugation", "The next mating guardian comes weakened.", 2_500_000, true),
        new(NeuteringKit, 78780258, "Neutering Kit", "Permanently stops an essence from breeding.", 2_500_000, false),
        new(TailoringKit, 78780259, "Tailoring Kit", "Extracts an essence's appearance.", 125_000_000, false),
        new(FilledTailoringKit, 78780260, "Filled Tailoring Kit", "Applies an extracted appearance.", 0, false),
        new(Serum, 78780257, "Mutagenic Serum", "Re-rolls an essence's colour.", 25_000_000, false),
        new(Solidifying, 78780262, "Solidifying Tincture", "Makes the summoned pet less translucent.", 12_500_000, false),
        new(Fading, 78780263, "Fading Tincture", "Makes the summoned pet more translucent.", 12_500_000, false),
    };

    /// <summary>The supply with this key, or null.</summary>
    public static BreedSupplyDef? Find(string key) => Array.Find(All, s => s.Key == key);

    /// <summary>The incense bonus a supply gives (0 for anything else).</summary>
    public static double IncenseBonus(string key) => key switch
    {
        LesserIncense => 0.025,
        RefinedIncense => 0.05,
        ExquisiteIncense => 0.10,
        _ => 0,
    };

    /// <summary>"Refined incense +5%" for an applied bonus.</summary>
    public static string IncenseName(double bonus)
    {
        string grade = bonus >= 0.0999 ? "Exquisite" : bonus >= 0.0499 ? "Refined" : "Lesser";
        return $"{grade} incense +{(bonus * 100).ToString("0.#", CultureInfo.InvariantCulture)}%";
    }

    /// <summary>
    /// Why the server would refuse <paramref name="s"/> on <paramref name="e"/> (the server's own
    /// checks, so nothing is spent on a refusal), or null when it would take it.
    /// </summary>
    public static string? RefuseReason(BreedSupplyDef s, BreedEssence e, IltBreedingRules r)
    {
        if (!s.Apply) return "Not applied from here.";
        double incense = IncenseBonus(s.Key);
        if (incense > 0)
        {
            if (e.Gate == BreedGate.Neutered) return "A neutered essence can't take incense.";
            if (e.Incense >= incense - 1e-6) return $"Already primed with equal or stronger incense (+{e.Incense * 100:0.#}%).";
            return null;
        }
        return s.Key switch
        {
            Draught when !e.Juvenile => "Only a juvenile (still growing) can take a Nurturing Draught.",
            Draught when e.Draught => "Already under a Nurturing Draught.",
            Catalyst when e.Catalyst => "Already infused with a Chromatic Catalyst.",
            Offering when r.Get(IltBreedingRules.GuardianEnabled) < 0.5 => "Mating guardians are off on this server.",
            Offering when e.Offering => "Already under an Offering of Subjugation.",
            _ => null,
        };
    }

    /// <summary>A warning to show in the confirmation (a weaker incense being replaced), or "".</summary>
    public static string Warning(BreedSupplyDef s, BreedEssence e)
    {
        double incense = IncenseBonus(s.Key);
        if (incense > 0 && e.Incense > 0 && e.Incense < incense)
            return $" It replaces the +{e.Incense * 100:0.#}% incense already on it (that one is lost).";
        return string.Empty;
    }

    /// <summary>"1.25M", "125M", "-" for a price.</summary>
    public static string PriceText(long price)
    {
        if (price <= 0) return "-";
        if (price >= 1_000_000) return (price / 1_000_000.0).ToString("0.##", CultureInfo.InvariantCulture) + "M";
        if (price >= 1_000) return (price / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        return price.ToString(CultureInfo.InvariantCulture);
    }
}
