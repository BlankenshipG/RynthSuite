// IltDamageTypeTint.cs — damage-type colours and pet essence school picking, after UB-ILT.
//
//   Palette: UB-ILT Lib\LeaftideDamageTypeVtColors.ForDamageTypeFlag (the Virindi Tank / Leaftide
//   label colours its Items, Spell and Pets tabs share). The engine's Items panel uses the same
//   values (RynthCore ImGui\Panels\DamageTypeColors.cs); keep the two tables in step.
//   School picking: UB-ILT Lib\PetDamageTypeTintPolicy.PickFlag (v2.0.1119).
using System;
using System.Numerics;

namespace RynthCore.Plugin.RynthAi.IltHub;

/// <summary>ACE damage-type flags, colours per school, and the pet essence school pick.</summary>
internal static class IltDamageTypeTint
{
    public const uint Slash = 1, Pierce = 2, Bludgeon = 4, SlashPierce = 3, Cold = 8, Fire = 16,
        Acid = 32, Lightning = 64, Nether = 1024;

    /// <summary>Text colour when the school is unknown (white, as UB for an essence in the pack).</summary>
    public static readonly Vector4 Unknown = new(0.95f, 0.95f, 0.95f, 1f);

    // Combined masks are read in this order (UB DamageProbeOrder): elements before physical.
    private static readonly uint[] ProbeOrder = { Nether, Lightning, Acid, Fire, Cold, Bludgeon, SlashPierce, Pierce, Slash };

    /// <summary>Colour for a school name as RynthAi writes it (Slash, Pierce, Bludgeon, Fire, Cold, Lightning, Acid, Nether).</summary>
    public static Vector4 ForName(string? school) => school switch
    {
        "Slash" or "Slashing"                => Rgb(255, 180, 120),
        "Pierce" or "Piercing"               => Rgb(240, 200, 140),
        "Bludgeon" or "Bludgeoning"          => Rgb(220, 190, 140),
        "Slash/Pierce"                       => Rgb(255, 160, 100),
        "Cold" or "Frost"                    => Rgb(160, 210, 255),
        "Fire"                               => Rgb(255, 130, 70),
        "Acid"                               => Rgb(160, 255, 140),
        "Lightning" or "Electric" or "Storm" => Rgb(255, 240, 120),
        "Nether"                             => Rgb(200, 160, 255),
        _                                    => Unknown,
    };

    /// <summary>School name for one flag (empty for 0 or an undefined value).</summary>
    public static string Name(uint flag) => flag switch
    {
        Slash       => "Slash",
        Pierce      => "Pierce",
        Bludgeon    => "Bludgeon",
        SlashPierce => "Slash/Pierce",
        Cold        => "Cold",
        Fire        => "Fire",
        Acid        => "Acid",
        Lightning   => "Lightning",
        Nether      => "Nether",
        _           => string.Empty,
    };

    /// <summary>
    /// One school for a pet essence: the captured source's damage (ACE 9054), else the essence's
    /// DamageType mask (45), else the name ("Fire Banshee Essence", "Incendiary Knight"). 0 = unknown.
    /// ILT reuses 9054 as the min-summon-aug id on some builds, where 1/2/4 read as Slash/Pierce/
    /// Bludgeon; an elemental name wins over such a physical-only property read.
    /// </summary>
    public static uint PickPetFlag(int capturedSourceDamageType, int damageTypeMask, string? displayName)
    {
        uint fromProps = PickFromProperties(capturedSourceDamageType, damageTypeMask);
        uint fromName = InferFromName(displayName);
        if (fromName != 0 && IsPhysicalOnly(fromProps)) return fromName;
        return fromProps != 0 ? fromProps : fromName;
    }

    /// <summary>School from Mag-Tools essence names and ILT catalog adjectives (whole words only).</summary>
    public static uint InferFromName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return 0;
        string n = displayName.ToLowerInvariant();
        if (HasAny(n, "nether")) return Nether;
        if (HasAny(n, "lightning", "electric", "voltaic", "galvanic", "electrified", "shocked", "excited", "volt", "shock"))
            return Lightning;
        if (HasAny(n, "acid", "caustic", "corrosion", "blistered", "blistering", "corrosive")) return Acid;
        if (HasAny(n, "fire", "flaming", "incendiary", "charred", "volcanic", "scorched", "pyre", "magma", "ember"))
            return Fire;
        if (HasAny(n, "frost", "glacial", "frozen", "frigid", "freezing", "arctic", "blizzard", "ice", "chill"))
            return Cold;
        if (HasAny(n, "bludgeon", "bludgeoning")) return Bludgeon;
        if (HasAny(n, "slash", "slashing")) return Slash;
        if (HasAny(n, "pierce", "piercing")) return Pierce;
        return 0;
    }

    private static uint PickFromProperties(int captured, int mask)
    {
        if (IsDefined(captured)) return (uint)captured;
        if (IsDefined(mask)) return (uint)mask;
        if (mask <= 0) return 0;
        uint m = unchecked((uint)mask);
        foreach (uint v in ProbeOrder)
            if ((m & v) == v) return v;
        return 0;
    }

    private static bool IsDefined(int raw) => raw > 0 && Name(unchecked((uint)raw)).Length > 0;

    private static bool IsPhysicalOnly(uint flag) => flag is Slash or Pierce or Bludgeon or SlashPierce;

    private static bool HasAny(string haystack, params string[] tokens)
    {
        foreach (string t in tokens)
            if (HasToken(haystack, t)) return true;
        return false;
    }

    /// <summary>True when <paramref name="token"/> appears as a whole word ("ice" not in "Novice").</summary>
    private static bool HasToken(string haystack, string token)
    {
        int i = 0;
        while ((i = haystack.IndexOf(token, i, StringComparison.Ordinal)) >= 0)
        {
            int end = i + token.Length;
            if ((i == 0 || !char.IsLetter(haystack[i - 1])) && (end >= haystack.Length || !char.IsLetter(haystack[end])))
                return true;
            i++;
        }
        return false;
    }

    private static Vector4 Rgb(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f, 1f);
}
