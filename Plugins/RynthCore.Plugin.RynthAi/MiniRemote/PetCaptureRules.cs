// PetCaptureRules.cs — ACECustom MonsterCapture thresholds for siphon lenses (pure; no host).
//
// Copied from ACECustom Source/ACE.Server/Entity/MonsterCapture.cs so the client can tell when
// a creature is weak enough to siphon before using a lens. Keep these numbers in lockstep with
// the server: CaptureMaxHealthFraction / CaptureMaxHealthPoints / CaptureRangeM and the lens
// WCID table in PetGuideController.
using System;

namespace RynthCore.Plugin.RynthAi.MiniRemote;

/// <summary>One siphon lens the Pets tab can auto-use (name, WCID, whether health is gated).</summary>
public readonly struct PetCaptureLens
{
    public PetCaptureLens(string name, uint wcid, bool bypassesHealthGate)
    {
        Name = name;
        Wcid = wcid;
        BypassesHealthGate = bypassesHealthGate;
    }

    public string Name { get; }
    public uint Wcid { get; }
    /// <summary>Asheron's (and Debug) lenses skip the 20% / 20 HP gate.</summary>
    public bool BypassesHealthGate { get; }
}

/// <summary>
/// When a siphon lens can be used, matching ACECustom MonsterCapture.UseCaptureCrystal.
/// A creature is weak enough when its health fraction is ≤ 20% OR its current HP is under 20
/// (whichever comes first). Asheron's Lens skips that check.
/// </summary>
public static class PetCaptureRules
{
    /// <summary>A lens targets the nearest attackable creature within this many meters.</summary>
    public const float CaptureRangeM = 5.0f;

    /// <summary>Siphon once at or below this fraction of max health…</summary>
    public const float CaptureMaxHealthFraction = 0.20f;

    /// <summary>…or below this many hit points (the gate is OR with the fraction).</summary>
    public const int CaptureMaxHealthPoints = 20;

    /// <summary>
    /// Lenses the player can pick for auto-capture. Order is the combo index.
    /// Resonance needs a prior failed capture (server-side); Asheron's bypasses the health gate.
    /// </summary>
    public static readonly PetCaptureLens[] Lenses =
    {
        new("Flawed Siphon Lens", 78780001, bypassesHealthGate: false),
        new("Pristine Siphon Lens", 78780002, bypassesHealthGate: false),
        new("Perfect Siphon Lens", 78780003, bypassesHealthGate: false),
        new("Resonance Lens", 78780010, bypassesHealthGate: false),
        new("Asheron's Lens", 78780012, bypassesHealthGate: true),
    };

    /// <summary>Combo labels for the Pets tab (same order as <see cref="Lenses"/>).</summary>
    public static readonly string[] LensNames =
    {
        "Flawed", "Pristine", "Perfect", "Resonance", "Asheron's",
    };

    public static PetCaptureLens LensAt(int index)
    {
        int i = Math.Clamp(index, 0, Lenses.Length - 1);
        return Lenses[i];
    }

    /// <summary>
    /// True when ACECustom would accept a siphon on this creature for a normal (non-bypass) lens.
    /// Pass current/max when known; when only a 0–1 ratio is known, pass ratio as current and 1 as max
    /// (then only the fraction half of the gate can fire).
    /// </summary>
    public static bool IsWeakEnough(uint currentHp, uint maxHp)
    {
        if (currentHp == 0) return false;          // dead — server refuses
        if (maxHp == 0) return false;
        float pct = (float)currentHp / maxHp;
        // Server: refuse when pct > 0.20 AND current >= 20. Allowed otherwise.
        return pct <= CaptureMaxHealthFraction || currentHp < CaptureMaxHealthPoints;
    }

    /// <summary>Same gate from a 0–1 health ratio only (no absolute HP). Dead / unknown = false.</summary>
    public static bool IsWeakEnoughRatio(float healthRatio)
    {
        if (healthRatio <= 0f || healthRatio > 1f) return false;
        return healthRatio <= CaptureMaxHealthFraction;
    }

    /// <summary>True when this lens may be used given the creature's vitals (bypass lenses always, if alive).</summary>
    public static bool CanAttempt(PetCaptureLens lens, uint currentHp, uint maxHp)
    {
        if (currentHp == 0) return false;
        if (lens.BypassesHealthGate) return maxHp == 0 || currentHp > 0;
        return IsWeakEnough(currentHp, maxHp);
    }

    /// <summary>Distance gate: ACECustom CaptureRangeM.</summary>
    public static bool InRange(double distanceM) => distanceM >= 0 && distanceM <= CaptureRangeM;
}
