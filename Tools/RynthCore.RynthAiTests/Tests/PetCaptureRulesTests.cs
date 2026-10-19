// ACECustom MonsterCapture health / range / lens table — matches Source/ACE.Server/Entity/MonsterCapture.cs.
using RynthCore.Plugin.RynthAi.MiniRemote;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiTests.Tests;

internal static class PetCaptureRulesTests
{
    public static void Register(Runner r)
    {
        r.Add("pet capture: health gate matches ACECustom (≤20% or under 20 HP)", HealthGate);
        r.Add("pet capture: Asheron's Lens bypasses the health gate", AsheronsBypass);
        r.Add("pet capture: range is 5 m; dead creatures are never capturable", RangeAndDead);
        r.Add("pet capture: lens WCIDs match PetGuideController", LensWcids);
        r.Add("pet capture: a creature is left alone after 5 tries with no capture, then retried after the block", ThrottlePerTarget);
        r.Add("pet capture: 15 lens uses with no capture anywhere trip the backstop; a success resets it", ThrottleBackstop);
    }

    private static void HealthGate()
    {
        // Full health — refuse.
        Check.False(PetCaptureRules.IsWeakEnough(100, 100), "100/100 too strong");
        Check.False(PetCaptureRules.IsWeakEnough(21, 100), "21% and ≥20 HP too strong");

        // Fraction gate.
        Check.True(PetCaptureRules.IsWeakEnough(20, 100), "20% exactly is allowed");
        Check.True(PetCaptureRules.IsWeakEnough(1, 100), "1% allowed");

        // Absolute HP gate (high % but under 20 HP).
        Check.True(PetCaptureRules.IsWeakEnough(15, 50), "15/50 = 30% but under 20 HP");
        Check.True(PetCaptureRules.IsWeakEnough(19, 19), "19/19 under 20 HP");

        // Ratio-only path (no absolute HP): only the fraction half.
        Check.True(PetCaptureRules.IsWeakEnoughRatio(0.20f), "ratio 0.20");
        Check.False(PetCaptureRules.IsWeakEnoughRatio(0.21f), "ratio 0.21");
        Check.False(PetCaptureRules.IsWeakEnoughRatio(0f), "ratio 0 = dead");
        Check.False(PetCaptureRules.IsWeakEnough(0, 100), "0 current = dead");
    }

    private static void AsheronsBypass()
    {
        var asheron = PetCaptureRules.LensAt(4);
        Check.Eq(asheron.Wcid, 78780012u, "Asheron's WCID");
        Check.True(asheron.BypassesHealthGate, "bypasses health");
        Check.True(PetCaptureRules.CanAttempt(asheron, 100, 100), "full health ok for Asheron's");
        Check.False(PetCaptureRules.CanAttempt(asheron, 0, 100), "still not when dead");

        var flawed = PetCaptureRules.LensAt(0);
        Check.False(PetCaptureRules.CanAttempt(flawed, 100, 100), "Flawed refuses full health");
        Check.True(PetCaptureRules.CanAttempt(flawed, 10, 100), "Flawed at 10%");
    }

    private static void RangeAndDead()
    {
        Check.Eq(PetCaptureRules.CaptureRangeM, 5.0f, "5 m range");
        Check.True(PetCaptureRules.InRange(0), "at feet");
        Check.True(PetCaptureRules.InRange(5.0), "at 5 m");
        Check.False(PetCaptureRules.InRange(5.01), "past 5 m");
        Check.False(PetCaptureRules.InRange(-1), "unknown distance");
    }

    private static void LensWcids()
    {
        Check.Eq(PetCaptureRules.Lenses.Length, 5, "five selectable lenses");
        Check.Eq(PetCaptureRules.LensAt(0).Wcid, 78780001u, "Flawed");
        Check.Eq(PetCaptureRules.LensAt(1).Wcid, 78780002u, "Pristine");
        Check.Eq(PetCaptureRules.LensAt(2).Wcid, 78780003u, "Perfect");
        Check.Eq(PetCaptureRules.LensAt(3).Wcid, 78780010u, "Resonance");
        Check.Eq(PetCaptureRules.LensAt(4).Wcid, 78780012u, "Asheron's");
        Check.Eq(PetCaptureRules.LensNames.Length, PetCaptureRules.Lenses.Length, "combo labels match");
    }

    private static void ThrottlePerTarget()
    {
        var t = new PetCaptureThrottle();
        long now = 1000;
        for (int i = 0; i < PetCaptureThrottle.MaxTriesPerTarget; i++)
        {
            Check.True(t.MayAttempt(7, now), $"try {i + 1} allowed");
            t.RecordAttempt(7, now);
            now += 2500;
        }
        Check.False(t.MayAttempt(7, now), "5 tries used: blocked");
        Check.True(t.BlockedSecondsLeft(7, now) > 0, "block time left reported");
        Check.True(t.MayAttempt(8, now), "another creature is not blocked");
        Check.False(t.MayAttempt(7, now + PetCaptureThrottle.TargetBlockMs - 3000), "still blocked just before the block ends");
        Check.True(t.MayAttempt(7, now + PetCaptureThrottle.TargetBlockMs + 1), "block served: tries again");
        t.RecordAttempt(7, now + PetCaptureThrottle.TargetBlockMs + 1);
        Check.True(t.MayAttempt(7, now + PetCaptureThrottle.TargetBlockMs + 2), "a fresh set of tries after the block");
    }

    private static void ThrottleBackstop()
    {
        var t = new PetCaptureThrottle();
        long now = 0;
        for (int i = 0; i < PetCaptureThrottle.MaxTriesWithoutSuccess; i++)
        {
            Check.False(t.Exhausted, $"not exhausted before use {i + 1}");
            t.RecordAttempt(100 + i, now);          // a new creature each time: per-target never trips
            now += 2500;
        }
        Check.True(t.Exhausted, "15 uses over many creatures, no capture: exhausted");
        Check.False(t.MayAttempt(999, now), "nothing more is allowed");
        t.RecordSuccess();
        Check.False(t.Exhausted, "a capture reply resets the backstop");
        Check.True(t.MayAttempt(100, now), "and the per-target blocks");
    }
}
