using System;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Empyrean Handler lethal-ring evade (CombatManager.HandleChatForRingEvade; Silentkelpie's PR 12). The line
// "The handler briefly channels infused Empyrean hatred to release a lethal ring." makes combat escape a
// Handler inside the ring radius: a run away from it where the engine can turn and autorun (the
// 2026-10-17 escape), else a back-jump. The base adds a gate: only while the macro is running with
// combat on, and a setting (HandlerRingEvade) turns it off.
internal static class RingEvadeTests
{
    private const uint Player = 0x50000D11;
    private const uint Handler = 0x80300110;
    private const uint Lb = 0xA9B40000;
    private const string RingLine = "The handler briefly channels infused Empyrean hatred to release a lethal ring.";

    public static void Register(Runner r)
    {
        r.Add("ring evade: the Handler's ring line starts an escape when it is in range and the macro runs with combat on", Jumps);
        r.Add("ring evade: not with the setting off, the macro stopped, or combat off", Gated);
        r.Add("ring evade: not for a Handler out of range, no Handler, or a player's copy of the line", NotThese);
    }

    private sealed class Rig
    {
        public readonly LegacyUiSettings S = new() { MonsterRange = 40, EnableCombat = true };
        public readonly CombatManager Combat;
        public int Jumps;
        public float Heading = -1;
        /// <summary>Escapes started: back-jumps, or the run-away (it holds combat while it runs).</summary>
        public int Evades => Jumps + (Jumps == 0 && Combat.RingEvadeActive ? 1 : 0);

        public Rig(float handlerYards, string handlerName = "Empyrean Handler")
        {
            FakeHost.Reset();
            FakeHost.Names[Player] = "Evader";
            FakeHost.Positions[Player] = (Lb | 0x1C, 96f, 96f, 0f);
            FakeHost.PlayerPose = (Lb | 0x1C, 96f, 96f, 0f);
            FakeHost.Names[Handler] = handlerName;
            FakeHost.Positions[Handler] = (Lb | 0x1C, 96f, 96f + handlerYards, 0f);
            FakeHost.ItemTypes[Handler] = 0x10;
            var host = FakeHost.Create(Player);
            var cache = FakeHost.MakeCache(host, Player, new[] { Handler });
            S.IsMacroRunning = true;
            Combat = new CombatManager(host, S, cache);
            Combat.SetPlayerId(Player);
            Combat.ScanNearbyTargets();
            Combat.RequestBackJump = (h, ms) => { Jumps++; Heading = h; return true; };
            Combat.JumpBusy = () => false;
        }
    }

    private static void Jumps()
    {
        var rig = new Rig(handlerYards: 5);
        rig.Combat.HandleChatForRingEvade(RingLine, 0x0C);
        Check.Eq(rig.Evades, 1, "a Handler 5 yards away: one escape");
        rig.Combat.HandleChatForRingEvade(RingLine, 0x0C);
        Check.Eq(rig.Evades, 1, "a second line inside the cooldown does nothing");
    }

    private static void Gated()
    {
        var off = new Rig(5);
        off.S.HandlerRingEvade = false;
        off.Combat.HandleChatForRingEvade(RingLine, 0x0C);
        Check.Eq(off.Evades, 0, "setting off: no escape");

        var stopped = new Rig(5);
        stopped.S.IsMacroRunning = false;
        stopped.Combat.HandleChatForRingEvade(RingLine, 0x0C);
        Check.Eq(stopped.Evades, 0, "macro stopped: no escape");

        var noCombat = new Rig(5);
        noCombat.S.EnableCombat = false;
        noCombat.Combat.HandleChatForRingEvade(RingLine, 0x0C);
        Check.Eq(noCombat.Evades, 0, "combat off: no escape");

        Check.True(new LegacyUiSettings().HandlerRingEvade, "on by default (it is gated)");
    }

    private static void NotThese()
    {
        var far = new Rig(30);
        far.Combat.HandleChatForRingEvade(RingLine, 0x0C);
        Check.Eq(far.Evades, 0, "a Handler 30 yards away is outside the ring");

        var none = new Rig(5, handlerName: "Drudge Skulker");
        none.Combat.HandleChatForRingEvade(RingLine, 0x0C);
        Check.Eq(none.Evades, 0, "no Empyrean Handler nearby");

        var said = new Rig(5);
        said.Combat.HandleChatForRingEvade("Someone says, \"" + RingLine + "\"", 0x03);
        Check.Eq(said.Evades, 0, "a player's copy of the line is ignored");
    }
}
