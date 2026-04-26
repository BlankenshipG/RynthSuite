using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.PluginSdk;
using RynthCore.Plugin.RynthAi;

namespace RynthCore.Plugin.RynthAi.Combat;

/// <summary>
/// Classifies missile weapons and loose ammunition so equipped ammo matches the wielded launcher (bow / crossbow / atlatl).
/// </summary>
public enum MissileWeaponKind
{
    Bow,
    Crossbow,
    Atlatl,
}

public static class MissileAmmoHelper
{
    /// <summary>Returns true when the item name looks like loose ammo (not bundles) for the given launcher kind.</summary>
    public static bool IsLooseAmmoForKind(WorldObject item, MissileWeaponKind kind)
        => IsLooseAmmoForKind(item.Name, kind);

    public static bool IsLooseAmmoForKind(string? itemName, MissileWeaponKind kind)
    {
        if (string.IsNullOrEmpty(itemName)) return false;
        string normalized = NormalizeName(itemName);
        if (normalized.Contains("bundle") || normalized.Contains("wrapped")) return false;
        if (normalized.Contains("arrowhead") || normalized.Contains("arrowshaft")) return false;
        if (normalized.Contains("quarrelhead") || normalized.Contains("quarrelshaft")) return false;
        if (normalized.Contains("darthead") || normalized.Contains("dartshaft")) return false;

        return kind switch
        {
            MissileWeaponKind.Bow => normalized.Contains("arrow"),
            // "crossbow" normalizes to a string containing "bolt" as a substring — exclude launcher names.
            MissileWeaponKind.Crossbow => normalized.Contains("quarrel")
                || (normalized.Contains("bolt") && !normalized.Contains("crossbow")),
            MissileWeaponKind.Atlatl => normalized.Contains("dart"),
            _ => false,
        };
    }

    /// <summary>Maps loose ammo name to launcher kind, or null if not recognized as ammo.</summary>
    public static MissileWeaponKind? GetAmmoKindFromName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        string n = NormalizeName(name);
        if (n.Contains("bundle") || n.Contains("wrapped")) return null;
        if (n.Contains("arrow") && !n.Contains("arrowhead") && !n.Contains("arrowshaft")) return MissileWeaponKind.Bow;
        if (n.Contains("quarrel") || (n.Contains("bolt") && !n.Contains("crossbow"))) return MissileWeaponKind.Crossbow;
        if (n.Contains("dart")) return MissileWeaponKind.Atlatl;
        return null;
    }

    public static MissileWeaponKind GetKindFromMissileWeaponName(string name)
    {
        if (name.IndexOf("crossbow", StringComparison.OrdinalIgnoreCase) >= 0) return MissileWeaponKind.Crossbow;
        if (name.IndexOf("atlatl", StringComparison.OrdinalIgnoreCase) >= 0) return MissileWeaponKind.Atlatl;
        return MissileWeaponKind.Bow;
    }

    public static bool LooksLikeMissileWeapon(WorldObject item)
    {
        if (item.ObjectClass == AcObjectClass.MissileWeapon)
            return true;
        if (string.IsNullOrWhiteSpace(item.Name)) return false;
        string n = item.Name;
        return n.IndexOf("crossbow", StringComparison.OrdinalIgnoreCase) >= 0
            || n.IndexOf("atlatl", StringComparison.OrdinalIgnoreCase) >= 0
            || n.IndexOf("bow", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>True when the player is wielding a missile launcher and (optionally) ammo of the matching kind.</summary>
    public static bool TryGetWieldedMissileKind(IEnumerable<WorldObject> inventory, uint playerId, out MissileWeaponKind kind)
    {
        foreach (var item in inventory)
        {
            if (!IsPlayerWielded(item, playerId))
                continue;
            // Loose ammo stacks share AcObjectClass.MissileWeapon with launchers; never treat them as the launcher.
            if (LooksLikeLooseAmmoForAnyKind(item))
                continue;
            if (!LooksLikeMissileWeapon(item))
                continue;
            kind = GetKindFromMissileWeaponName(item.Name);
            return true;
        }

        kind = MissileWeaponKind.Bow;
        return false;
    }

    /// <summary>True if any wielded stack is loose ammo compatible with the wielded missile weapon kind.</summary>
    public static bool HasWieldedAmmoMatchingKind(IEnumerable<WorldObject> inventory, uint playerId, out MissileWeaponKind weaponKind)
    {
        if (!TryGetWieldedMissileKind(inventory, playerId, out weaponKind))
            return false;

        foreach (var item in inventory)
        {
            if (!IsPlayerWielded(item, playerId)) continue;
            if (IsLooseAmmoForKind(item, weaponKind))
                return true;
        }

        return false;
    }

    /// <summary>Best-effort priority for prismatic / specialty ammo (higher = preferred).</summary>
    public static int LooseAmmoPriority(string? name, MissileWeaponKind kind)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        if (!IsLooseAmmoForKind(name, kind)) return 0;
        string n = name;
        if (n.IndexOf("lethal", StringComparison.OrdinalIgnoreCase) >= 0) return 40;
        if (n.IndexOf("deadly", StringComparison.OrdinalIgnoreCase) >= 0) return 30;
        if (n.IndexOf("greater", StringComparison.OrdinalIgnoreCase) >= 0) return 20;
        if (n.IndexOf("prismatic", StringComparison.OrdinalIgnoreCase) >= 0) return 10;
        if (n.IndexOf("armor piercing", StringComparison.OrdinalIgnoreCase) >= 0) return 6;
        if (n.IndexOf("broad", StringComparison.OrdinalIgnoreCase) >= 0) return 5;
        if (n.IndexOf("blunt", StringComparison.OrdinalIgnoreCase) >= 0) return 4;
        if (n.IndexOf("frog", StringComparison.OrdinalIgnoreCase) >= 0) return 3;
        return 1;
    }

    /// <summary>Picks the highest-priority loose ammo in inventory for the launcher kind.</summary>
    public static WorldObject? FindBestLooseAmmo(IEnumerable<WorldObject> inventory, MissileWeaponKind kind)
    {
        WorldObject? best = null;
        int bestPri = -1;
        foreach (var item in inventory)
        {
            if (!IsLooseAmmoForKind(item, kind)) continue;
            int p = LooseAmmoPriority(item.Name, kind);
            if (p > bestPri) { bestPri = p; best = item; }
        }
        return best;
    }

    public static bool AmmoRuleMatchesKind(string ruleCategory, MissileWeaponKind kind)
    {
        if (string.IsNullOrWhiteSpace(ruleCategory) || ruleCategory.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            return true;
        if (ruleCategory.Equals("Bow", StringComparison.OrdinalIgnoreCase)) return kind == MissileWeaponKind.Bow;
        if (ruleCategory.Equals("Crossbow", StringComparison.OrdinalIgnoreCase)) return kind == MissileWeaponKind.Crossbow;
        if (ruleCategory.Equals("Atlatl", StringComparison.OrdinalIgnoreCase)) return kind == MissileWeaponKind.Atlatl;
        return true;
    }

    private static bool IsPlayerWielded(WorldObject item, uint playerId)
    {
        // Prefer cache ownership; fall back to client LVK when wield slot is known but ownership row lags.
        int loc = item.WieldedLocation > 0
            ? item.WieldedLocation
            : item.Values(LongValueKey.CurrentWieldedLocation, 0);
        if (loc <= 0) return false;
        if (playerId == 0) return false;
        int pid = unchecked((int)playerId);
        return item.Wielder == 0 || item.Wielder == pid;
    }

    /// <summary>True if the name matches loose ammo rules for any launcher (used to disambiguate launcher vs ammo).</summary>
    public static bool LooksLikeLooseAmmoForAnyKind(WorldObject item)
    {
        return IsLooseAmmoForKind(item, MissileWeaponKind.Bow)
            || IsLooseAmmoForKind(item, MissileWeaponKind.Crossbow)
            || IsLooseAmmoForKind(item, MissileWeaponKind.Atlatl);
    }

    private static string NormalizeName(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        int count = 0;
        foreach (char ch in value)
        {
            if (!char.IsLetterOrDigit(ch)) continue;
            buffer[count++] = char.ToLowerInvariant(ch);
        }
        return new string(buffer[..count]);
    }
}
