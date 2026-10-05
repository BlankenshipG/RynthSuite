using System;
using RynthCore.Loot;

namespace RynthCore.Plugin.RynthAi.Combat;

/// <summary>
/// Shield / off-hand classification shared by the Items window, the USD importer and
/// combat. Uses the item's valid-equip-locations bitmask (STypeInt 9, AC <c>EquipMask</c>)
/// with a name fallback for items whose properties haven't been read yet.
/// </summary>
internal static class ShieldHelper
{
    /// <summary>AC EquipMask: shield (off-hand) slot.</summary>
    public const int EquipMaskShield = 0x00200000;

    /// <summary>AC EquipMask: two-handed weapon slot (no shield possible).</summary>
    public const int EquipMaskTwoHanded = 0x02000000;

    /// <summary>Name fragments of AC shields, used only when the equip mask reads 0.</summary>
    private static readonly string[] ShieldNameHints =
        { "Shield", "Buckler", "Aegis", "Scutum", "Pavise", "Kite", "Heater" };

    /// <summary>True when <paramref name="wo"/> wields into the shield slot.</summary>
    public static bool IsShieldObject(WorldObject? wo)
    {
        if (wo == null) return false;
        int locations = wo.Values(LongValueKey.Locations, 0);
        if (locations != 0) return (locations & EquipMaskShield) != 0;

        // Mask not known yet: only armor-class items can be shields; match by name.
        if (wo.ObjectClass is not (AcObjectClass.Armor or AcObjectClass.Unknown)) return false;
        foreach (string hint in ShieldNameHints)
            if (wo.Name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>
    /// True when a shield can be carried next to <paramref name="weapon"/>: a melee weapon
    /// whose equip mask is not two-handed. An unknown mask counts as one-handed (the server
    /// refuses the shield otherwise, and combat caps its retries).
    /// </summary>
    public static bool AllowsShield(WorldObject? weapon)
    {
        if (weapon == null || weapon.ObjectClass != AcObjectClass.MeleeWeapon) return false;
        return (weapon.Values(LongValueKey.Locations, 0) & EquipMaskTwoHanded) == 0;
    }
}
