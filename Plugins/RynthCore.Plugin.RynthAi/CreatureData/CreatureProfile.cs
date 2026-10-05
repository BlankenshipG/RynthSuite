using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RynthCore.Plugin.RynthAi.CreatureData;

/// <summary>
/// Persistent record of observed creature data — vitals, resists, armor, spells.
/// Captured passively from IdentifyObject responses and QueryHealth replies.
/// Keyed by Name (with optional Wcid disambiguator) in CreatureProfileStore.
/// </summary>
public sealed class CreatureProfile
{
    public string Name { get; set; } = string.Empty;

    public uint Wcid { get; set; }

    public int CreatureType { get; set; }

    /// <summary>Max health at Aelrynth difficulty tier 0 (real Dereth; every tier on other servers).</summary>
    public uint MaxHealth { get; set; }

    /// <summary>
    /// Max health at Aelrynth difficulty tiers above 0 (awakened worlds, scaled copies: +5% a
    /// tier), tier -> highest seen. Null until one is seen, and then left out of the file
    /// on every other server. Resists, armour, spells and type are the same at every tier.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<int, uint>? TierMaxHealth { get; set; }
    public uint MaxStamina { get; set; }
    public uint MaxMana { get; set; }

    public int ArmorLevel { get; set; }

    public double ResistSlash { get; set; } = 1.0;
    public double ResistPierce { get; set; } = 1.0;
    public double ResistBludgeon { get; set; } = 1.0;
    public double ResistFire { get; set; } = 1.0;
    public double ResistCold { get; set; } = 1.0;
    public double ResistAcid { get; set; } = 1.0;
    public double ResistElectric { get; set; } = 1.0;

    public List<uint> KnownSpellIds { get; set; } = new();

    public int Samples { get; set; }
    public string LastSeen { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}
