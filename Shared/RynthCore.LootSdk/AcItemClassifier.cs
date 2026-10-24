using System;

namespace RynthCore.Loot;

/// <summary>
/// An item's <see cref="AcObjectClass"/> from what the client knows about it without an
/// identify: its ItemType flags (STypeInt 1), whether it carries a spell in its header (data
/// id 28), its name, ValidLocations (STypeInt 9) and WeaponSkill (STypeInt 48). The same
/// decisions as RynthAi's WorldObjectCache (ClassifyByItemType, then ClassifyItem's scroll and
/// weapon-slot corrections), for a plugin that has no object cache of its own: RynthInventory
/// stores the class with each saved item so a "like this" rule can be judged on another
/// character's items (2026-10-24). Pure.
/// </summary>
public static class AcItemClassifier
{
    public const uint ItemTypeMeleeWeapon = 0x00000001;
    public const uint ItemTypeArmor = 0x00000002;
    public const uint ItemTypeClothing = 0x00000004;
    public const uint ItemTypeJewelry = 0x00000008;
    public const uint ItemTypeFood = 0x00000020;
    public const uint ItemTypeMoney = 0x00000040;
    public const uint ItemTypeMisc = 0x00000080;
    public const uint ItemTypeMissileWeapon = 0x00000100;
    public const uint ItemTypeContainer = 0x00000200;
    public const uint ItemTypeGem = 0x00000800;
    public const uint ItemTypeSpellComponents = 0x00001000;
    public const uint ItemTypeWritable = 0x00002000;
    public const uint ItemTypeKey = 0x00004000;
    public const uint ItemTypeCaster = 0x00008000;
    public const uint ItemTypePortal = 0x00010000;
    public const uint ItemTypePromissoryNote = 0x00040000;
    public const uint ItemTypeManaStone = 0x00080000;
    public const uint ItemTypeService = 0x00100000;
    public const uint ItemTypeCraftCookingBase = 0x00400000;
    public const uint ItemTypeCraftAlchemyBase = 0x00800000;
    public const uint ItemTypeCraftFletchingBase = 0x02000000;
    public const uint ItemTypeCraftAlchemyIntermediate = 0x04000000;
    public const uint ItemTypeCraftFletchingIntermediate = 0x08000000;
    public const uint ItemTypeLifeStone = 0x10000000;
    public const uint ItemTypeTinkeringTool = 0x20000000;
    public const uint ItemTypeTinkeringMaterial = 0x40000000;

    // ValidLocations (EquipMask) weapon slots.
    private const uint SlotMeleeWeapon = 0x00100000, SlotMissileWeapon = 0x00400000, SlotTwoHanded = 0x02000000;

    /// <summary>The class the ItemType flags give (most specific first), as RynthAi's ClassifyByItemType.</summary>
    public static AcObjectClass FromItemType(uint typeFlags)
    {
        if ((typeFlags & ItemTypePromissoryNote) != 0) return AcObjectClass.TradeNote;
        if ((typeFlags & ItemTypeManaStone) != 0) return AcObjectClass.ManaStone;
        if ((typeFlags & ItemTypeSpellComponents) != 0) return AcObjectClass.SpellComponent;
        if ((typeFlags & ItemTypeGem) != 0) return AcObjectClass.Gem;
        if ((typeFlags & ItemTypeKey) != 0) return AcObjectClass.Key;
        if ((typeFlags & ItemTypeLifeStone) != 0) return AcObjectClass.Lifestone;
        if ((typeFlags & ItemTypeTinkeringMaterial) != 0) return AcObjectClass.Salvage;
        if ((typeFlags & ItemTypeTinkeringTool) != 0) return AcObjectClass.Ust;
        if ((typeFlags & ItemTypeCraftCookingBase) != 0) return AcObjectClass.BaseCooking;
        if ((typeFlags & ItemTypeCraftAlchemyBase) != 0) return AcObjectClass.BaseAlchemy;
        if ((typeFlags & ItemTypeCraftFletchingBase) != 0) return AcObjectClass.BaseFletching;
        if ((typeFlags & ItemTypeCraftAlchemyIntermediate) != 0) return AcObjectClass.CraftedAlchemy;
        if ((typeFlags & ItemTypeCraftFletchingIntermediate) != 0) return AcObjectClass.CraftedFletching;
        if ((typeFlags & ItemTypeService) != 0) return AcObjectClass.Services;
        if ((typeFlags & ItemTypePortal) != 0) return AcObjectClass.Portal;
        if ((typeFlags & ItemTypeWritable) != 0) return AcObjectClass.Book;
        if ((typeFlags & ItemTypeContainer) != 0) return AcObjectClass.Container;
        if ((typeFlags & ItemTypeCaster) != 0) return AcObjectClass.WandStaffOrb;
        if ((typeFlags & ItemTypeMissileWeapon) != 0) return AcObjectClass.MissileWeapon;
        if ((typeFlags & ItemTypeArmor) != 0) return AcObjectClass.Armor;
        if ((typeFlags & ItemTypeMeleeWeapon) != 0) return AcObjectClass.MeleeWeapon;
        if ((typeFlags & ItemTypeClothing) != 0) return AcObjectClass.Clothing;
        if ((typeFlags & ItemTypeJewelry) != 0) return AcObjectClass.Jewelry;
        if ((typeFlags & ItemTypeFood) != 0) return AcObjectClass.Food;
        if ((typeFlags & ItemTypeMoney) != 0) return AcObjectClass.Money;
        if ((typeFlags & ItemTypeMisc) != 0) return AcObjectClass.Misc;
        return AcObjectClass.Unknown;
    }

    /// <summary>
    /// <see cref="FromItemType"/>, then: a writable that carries a spell or is named "Scroll ..." is a
    /// Scroll; an item typed Misc (or nothing) that fits a weapon slot is that weapon (custom content
    /// does this), and one with a weapon skill but no slot reads as melee.
    /// </summary>
    public static AcObjectClass Classify(uint itemType, string? name, bool hasSpellDid, int validLocations, int weaponSkill)
    {
        AcObjectClass cls = FromItemType(itemType);
        if (cls == AcObjectClass.Book
            && (hasSpellDid || (name ?? string.Empty).StartsWith("Scroll", StringComparison.OrdinalIgnoreCase)))
            return AcObjectClass.Scroll;
        if (cls == AcObjectClass.Misc || cls == AcObjectClass.Unknown)
        {
            if (validLocations != 0)
            {
                uint s = unchecked((uint)validLocations);
                if ((s & SlotMissileWeapon) != 0) return AcObjectClass.MissileWeapon;
                if ((s & (SlotMeleeWeapon | SlotTwoHanded)) != 0) return AcObjectClass.MeleeWeapon;
            }
            else if (weaponSkill > 0)
                return AcObjectClass.MeleeWeapon;
        }
        return cls;
    }

    /// <summary>
    /// An ObjectClass condition's test, as RynthAi's looter runs it (ScrollLearner.ClassMatches):
    /// Scroll also takes a spell-carrying writable, Book also takes a Scroll.
    /// </summary>
    public static bool ClassMatches(int itemClass, int wanted)
        => itemClass == wanted
           || (wanted == (int)AcObjectClass.Book && itemClass == (int)AcObjectClass.Scroll);
}
