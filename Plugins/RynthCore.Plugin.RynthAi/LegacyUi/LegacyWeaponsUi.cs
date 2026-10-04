using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

/// <summary>
/// The Items list logic behind the engine's Items panel (ItemsFace): add the selected
/// weapon or consumable, fill from pack, prune. Draws nothing; its ImGui window went
/// with the plugin render path.
/// </summary>
internal sealed class LegacyWeaponsUi
{
    private readonly LegacyUiSettings _settings;
    private readonly RynthCoreHost _host;
    private WorldObjectCache? _worldFilter;

    public LegacyWeaponsUi(LegacyUiSettings settings, RynthCoreHost host)
    {
        _settings = settings;
        _host = host;
    }

    public void SetWorldFilter(WorldObjectCache cache) => _worldFilter = cache;

    public void AddSelectedWeapon()
    {
        uint selId = _host.GetSelectedItemId();
        if (selId == 0)
        {
            _host.WriteToChat("[RynthAi] No item selected — click a weapon in your inventory first.", 1);
            return;
        }
        if (_worldFilter == null) { _host.WriteToChat("[RynthAi] Object cache not ready.", 1); return; }

        var wo = _worldFilter[(int)selId];
        // A class guessed from the name before the item type was known can be wrong: ask the
        // game for the real type before refusing (and fix the cached entry for combat too).
        if (wo != null && !IsWeapon(wo) && !CombatManager.IsShieldItem(wo))
            wo = _worldFilter.ReclassifyFromItemType(wo.Id) ?? wo;
        if (wo == null)
            _host.WriteToChat($"[RynthAi] Item 0x{selId:X8} not in cache — try again.", 1);
        else if (!IsWeapon(wo) && !CombatManager.IsShieldItem(wo))
            _host.WriteToChat("[RynthAi] Selected item is not a weapon, wand or shield.", 1);
        else if (_settings.ItemRules.Any(x => x.Id == wo.Id))
            _host.WriteToChat($"[RynthAi] {wo.Name} is already in the list.", 1);
        else if (!IsWeapon(wo))
        {
            // A shield: combat wields it in the off hand (the Monsters rule's / global Offhand setting).
            _settings.ItemRules.Add(new ItemRule
            {
                Id            = wo.Id,
                Name          = wo.Name,
                Element       = "",
                ElementSource = "",
                Action        = WeaponList.ShieldAction,
            });
            string shown = WeaponNames.For(_host, _worldFilter, wo.Id, wo.Name);
            _host.WriteToChat($"[RynthAi] Added shield: {shown} (0x{(uint)wo.Id:X8}) — wielded in the off hand with a one-handed weapon (Offhand: {_settings.OffhandDefault})", 1);
        }
        else
        {
            var info = WeaponElementTracker.ReadFrom(_host, wo.Id, wo.Name);
            _settings.ItemRules.Add(new ItemRule
            {
                Id            = wo.Id,
                Name          = wo.Name,
                Element       = info.Element,
                ElementSource = info.Source,
                Action        = "Weapon",
            });
            string shown = WeaponNames.For(_host, _worldFilter, wo.Id, wo.Name);
            string elem = info.Unknown ? "element unknown until identified" : $"{info.Element}, from {info.Source}";
            _host.WriteToChat($"[RynthAi] Added weapon: {shown} (0x{(uint)wo.Id:X8}) [{elem}]", 1);
        }
    }

    public void AddSelectedConsumable()
    {
        uint selId = _host.GetSelectedItemId();
        if (selId == 0)
        {
            _host.WriteToChat("[RynthAi] No item selected — click an item in your inventory first.", 1);
            return;
        }
        if (_worldFilter == null) { _host.WriteToChat("[RynthAi] Object cache not ready.", 1); return; }

        var wo = _worldFilter[(int)selId];
        if (wo == null)
            _host.WriteToChat($"[RynthAi] Item 0x{selId:X8} not in cache — try again.", 1);
        else if (_settings.ConsumableRules.Any(x => x.Id == wo.Id))
            _host.WriteToChat($"[RynthAi] {wo.Name} is already in the list.", 1);
        else
        {
            string type = DetectConsumableType(wo);
            _settings.ConsumableRules.Add(new ConsumableRule
            {
                Id   = wo.Id,
                Name = wo.Name,
                Type = type,
            });
            _host.WriteToChat($"[RynthAi] Added consumable: {wo.Name} (0x{(uint)wo.Id:X8}) [{type}]", 1);
        }
    }

    /// <summary>
    /// Adds every weapon and usable consumable in <paramref name="inventory"/> that
    /// isn't listed yet: weapons (melee, missile, wands; the wielded one first, since
    /// combat treats the first weapon as the default), healing kits, lockpicks and
    /// pet essences. Mana stones are left out on purpose: listing any switches mana
    /// tapping from "every mana stone" to "only these names".
    /// </summary>
    public (int Weapons, int Consumables) FillFromPack(IReadOnlyList<WorldObject> inventory, uint playerId)
    {
        int weapons = 0, consumables = 0;
        var ordered = inventory
            .OrderByDescending(wo => playerId != 0 && wo.Wielder == unchecked((int)playerId))
            .ThenBy(wo => wo.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var wo in ordered)
        {
            if (IsWeapon(wo))
            {
                if (_settings.ItemRules.Any(x => x.Id == wo.Id)) continue;
                var info = WeaponElementTracker.ReadFrom(_host, wo.Id, wo.Name);
                _settings.ItemRules.Add(new ItemRule { Id = wo.Id, Name = wo.Name, Element = info.Element, ElementSource = info.Source, Action = "Weapon" });
                weapons++;
                continue;
            }
            string? type = PackConsumableType(wo);
            if (type == null || _settings.ConsumableRules.Any(x => x.Id == wo.Id)) continue;
            _settings.ConsumableRules.Add(new ConsumableRule { Id = wo.Id, Name = wo.Name, Type = type });
            consumables++;
        }
        return (weapons, consumables);
    }

    /// <summary>
    /// Removes weapons and consumables this character has nothing of, by id or by
    /// name: entries a profile picked up from another character. Returns their names.
    /// </summary>
    public List<string> PruneNotOwned(IReadOnlyList<WorldObject> inventory)
    {
        var ids   = new HashSet<int>(inventory.Select(w => w.Id));
        var names = new HashSet<string>(inventory.Select(w => w.Name), StringComparer.OrdinalIgnoreCase);
        var removed = new List<string>();
        _settings.ItemRules.RemoveAll(r =>
        {
            bool gone = !ids.Contains(r.Id) && !names.Contains(r.Name);
            if (gone) removed.Add(r.Name);
            return gone;
        });
        _settings.ConsumableRules.RemoveAll(r =>
        {
            bool gone = !ids.Contains(r.Id) && !names.Contains(r.Name);
            if (gone) removed.Add(r.Name);
            return gone;
        });
        return removed;
    }

    internal static string? PackConsumableType(WorldObject wo)
    {
        switch (wo.ObjectClass)
        {
            case AcObjectClass.HealingKit: return "HealthKit";
            case AcObjectClass.Food:       return PotionType(wo);
            case AcObjectClass.Lockpick:   return "Lockpick";
            case AcObjectClass.Misc:
            case AcObjectClass.CombatPet:
                return wo.Name.Contains("Essence", StringComparison.OrdinalIgnoreCase) ? "Pet" : null;
            default: return null;
        }
    }

    /// <summary>HealthPotion / ManaPotion / StaminaPotion for a Food item named for a vital, else null.</summary>
    internal static string? PotionType(WorldObject wo)
    {
        if (wo.ObjectClass != AcObjectClass.Food || string.IsNullOrEmpty(wo.Name)) return null;
        string n = wo.Name;
        // Field Rations restore stamina (BoostEnum 4) but don't say so in the name.
        if (n.EndsWith("Field Rations", StringComparison.OrdinalIgnoreCase)) return "StaminaPotion";
        if (n.Contains("Mana", StringComparison.OrdinalIgnoreCase)) return "ManaPotion";
        if (n.Contains("Stamina", StringComparison.OrdinalIgnoreCase)) return "StaminaPotion";
        if (n.Contains("Heal", StringComparison.OrdinalIgnoreCase) || n.Contains("Health", StringComparison.OrdinalIgnoreCase)) return "HealthPotion";
        return null;
    }

    private static bool IsWeapon(WorldObject wo) =>
        wo.ObjectClass == AcObjectClass.MeleeWeapon   ||
        wo.ObjectClass == AcObjectClass.MissileWeapon ||
        wo.ObjectClass == AcObjectClass.WandStaffOrb;

    /// <summary>Auto-detect consumable type from item name.</summary>
    private static string DetectConsumableType(WorldObject wo)
    {
        string name = wo.Name;
        if (string.IsNullOrEmpty(name)) return "General";

        // Potions and elixirs are Food: typed by the vital they restore. A mana
        // potion used to become "ManaStone" from the word "Mana".
        string? potion = PotionType(wo);
        if (potion != null) return potion;

        // Health items
        if (name.Contains("Heal", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Health", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Elixir of Healing", StringComparison.OrdinalIgnoreCase))
            return "HealthKit";

        // Mana items
        if (name.Contains("Mana", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Mana Stone", StringComparison.OrdinalIgnoreCase))
            return "ManaStone";

        // Stamina items
        if (name.Contains("Stamina", StringComparison.OrdinalIgnoreCase))
            return "Stamina";

        // Lockpicks
        if (name.Contains("Lockpick", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Lock Pick", StringComparison.OrdinalIgnoreCase))
            return "Lockpick";

        // Combat-pet summoning essences (PetDevices) — consumed by PetManager.
        if (name.Contains("Essence", StringComparison.OrdinalIgnoreCase))
            return "Pet";

        return "General";
    }
}
