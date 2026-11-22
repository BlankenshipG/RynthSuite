using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthOracle.Data;

// ACECustom's QB Deeds for the ILT Tracker tab (2026-11-21, Silentkelpie: "look at acecustom ... should be called deeds based
// on total qb"). ACECustom (origin/master 8d2460ff2, Source/ACE.Server/Entity/DeedsTiers.cs, upstream #564 2026-10-24 and the
// damage steps of 2026-11-10): 15 cumulative tiers, tier N at N x 1,000 account QB, always on while Deeds are ON (/deeds on |
// off). The server caps the tiers with deeds_max_tier, and the damage step (deeds_damage_step, 2% by default), the Breath of
// Renewal chance and the Cheat Death timings are live settings, so the tier lines below are the defaults; RynthILT's /deeds
// list read (IltDeedsInfo) is the server's own text and wins when it is in.

/// <summary>One Deeds tier as ACECustom's DeedsTiers table has it: its QB and a short line of what it gives.</summary>
internal readonly record struct QbDeedTier(int Number, long Qb, string Summary);

internal static class QbDeeds
{
    public const int QbPerTier = 1000;
    public const int MaxTier = 15;
    /// <summary>The tier that unlocks /rebuff and the Deedkeeper's Writ (/deeds item).</summary>
    public const int RebuffTier = 10;
    /// <summary>The tiers that each add the damage step (2% at the server's default).</summary>
    public static readonly int[] DamageStepTiers = { 3, 6, 9, 12, 15 };
    public const double DefaultDamageStep = 0.02;

    public static readonly QbDeedTier[] Tiers =
    {
        new(1, 1000, "Legendary attributes, Legendary skills, Legendary Health / Stamina / Mana Gain, Augmented Understanding III, "
                     + "Asheron's Lesser Benediction and Blackmoor's Favor"),
        new(2, 2000, "Legendary Armor and the seven Legendary wards (bludgeoning, piercing, slashing, acid, flame, frost, storm)"),
        new(3, 3000, "Prodigal Dirty Fighting, Life, Deception, Recklessness, Shield, Sneak Attack, Magic Resistance, "
                     + "Invulnerability, Impregnability, Void, War and Healing"),
        new(4, 4000, "Dispersion, Uncanny Dodge, Foresight, Master Negator's / Guardian's / Wayfarer's, Master Voidlock's, "
                     + "Paragon's Void V, Master Warlock's and Paragon's War V"),
        new(5, 5000, "The health, stamina and mana buffs (31 spells, Sigil of Vigor XV among them)"),
        new(6, 6000, "Surge of Destruction, Protection and Regeneration always on, with your life augs (your Aetheria no longer "
                     + "procs these three)"),
        new(7, 7000, "Prodigal Strength to Willpower, and Prodigal Regeneration, Rejuvenation and Mana Renewal"),
        new(8, 8000, "Rating and armor buffs: Master Brute's, Hero's, Duelist's, Rover's, Sage's and Adherent's, Sigil of Fury X, "
                     + "damage and damage reduction ratings, Ruschk Skin, Timaru's Shelter, Society Master's Blessing and more"),
        new(9, 9000, "The Legendary banes and Legendary Impenetrability as a floor on every armor piece you wear"),
        new(10, 10000, "/rebuff casts all your known level 8 buffs at once; /deeds item gives a Deedkeeper's Writ that does the same"),
        new(11, 11000, "Prodigal Armor, the Prodigal protections and the Masterwork resistances"),
        new(12, 12000, "Breath of Renewal: a chance on every attack"),
        new(13, 13000, "Cheat Death: a monster's killing blow leaves you at 1 health and immune for a moment (shared cooldown with "
                       + "Cheat Death boots)"),
        new(14, 14000, "+100% of your base critical chance (10% becomes 20%)"),
        new(15, 15000, "Loot luck: every item's quality roll on your kills is rolled twice and the better kept"),
    };

    /// <summary>The tier a QB reaches: 0 below 1,000, at most 15 (ACECustom's TierFor).</summary>
    public static int TierFor(long qb) => (int)Math.Clamp(Math.Max(0, qb) / QbPerTier, 0, MaxTier);

    /// <summary>The Deeds damage bonus at a tier at the default step: 0 below tier 3, then +2% at 3, 6, 9, 12 and 15.</summary>
    public static double DamageBonus(int tier, double step = DefaultDamageStep)
    {
        int steps = 0;
        foreach (int t in DamageStepTiers) if (tier >= t) steps++;
        return steps * step;
    }

    /// <summary>The tier's line, with the damage step on the step tiers (at the default).</summary>
    public static string Line(QbDeedTier t)
    {
        if (Array.IndexOf(DamageStepTiers, t.Number) < 0) return t.Summary;
        return t.Summary + $"; +{DefaultDamageStep * 100:0}% damage ({DamageBonus(t.Number) * 100:0}% in all)";
    }

    /// <summary>How far the QB is to the next tier: the next tier (0 at the top), its fraction done, the QB still needed.</summary>
    public static (int Next, float Fraction, long ToNext) Progress(long qb)
    {
        qb = Math.Max(0, qb);
        int tier = TierFor(qb);
        if (tier >= MaxTier) return (0, 1f, 0);
        long into = qb - (long)tier * QbPerTier;
        return (tier + 1, (float)into / QbPerTier, QbPerTier - into);
    }
}
