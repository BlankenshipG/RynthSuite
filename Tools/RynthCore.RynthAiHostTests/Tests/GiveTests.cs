using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// /ub give and /mt give (Tom, 2026-10-05): "/ub give ... should give every item that matches the
// name in the inventory. /mt give should give the first one it finds." Both use AC's give action
// (an NPC ignores MoveItemExternal) and never hand over worn or wielded gear.
internal static class GiveTests
{
    private const uint Player = 0x50000F01;
    private const uint Npc = 0x80281000;
    private const uint Mote1 = 0x80281101, Mote2 = 0x80281102, Mote3 = 0x80281103, WornMote = 0x80281104, Other = 0x80281105;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("give: /ub give hands over every matching carried item, never worn gear", UbGivesAll);
        r.Add("give: /ub give with a count hands over that many", UbGiveCount);
        r.Add("give: /mt give hands over only the first match", MtGivesFirst);
        r.Add("give: /ub givep matches part of the name", UbGivePartial);
    }

    private sealed class World
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new() { GiveQueueIntervalMs = 0 };
        public readonly WorldObjectCache Cache;

        public World()
        {
            FakeHost.Reset();
            FakeHost.SalvageCalls = true;   // MoveItemExternal + GetObjectWielderInfo (Wielded)
            FakeHost.GiveCalls = true;
            FakeHost.Names[Player] = "Giver";
            FakeHost.PlayerIdValue = Player;
            FakeHost.Names[Npc] = "Town Crier";
            FakeHost.Positions[Npc] = (0x1048002Au, 101f, 100f, 0f);
            FakeHost.ItemTypes[Npc] = 0x10;   // a creature (an NPC)
            FakeHost.NotAttackable.Add(Npc);
            FakeHost.Positions[Player] = (0x1048002Au, 100f, 100f, 0f);
            FakeHost.PlayerPose = (0x1048002Au, 100f, 100f, 0f);
            foreach (var (id, name) in new[] { (Mote1, "Mana Mote"), (Mote2, "Mana Mote"), (Mote3, "Mana Mote"), (WornMote, "Mana Mote"), (Other, "Lesser Mana Mote") })
            {
                FakeHost.Names[id] = name;
                FakeHost.Containers[id] = Player;
                FakeHost.ItemTypes[id] = 0x80;   // misc
            }
            FakeHost.Wielded[WornMote] = 0x8000;   // worn: never given
            var host = FakeHost.Create(playerId: Player);
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            Set("_dashboard", dash);
            Cache = new WorldObjectCache(host);
            Cache.SetPlayerId(Player);
            Set("_objectCache", Cache);
            Set("_playerId", Player);
            Set("_initialized", true);
            Set("_loginComplete", true);
            foreach (uint id in new[] { Npc, Mote1, Mote2, Mote3, WornMote, Other }) Cache.OnCreateObject(id);
            Cache.Tick();
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public object? Call(string m, params object?[] a) => typeof(RynthAiPlugin).GetMethod(m, Any)!.Invoke(P, a);

        /// <summary>Runs the chat command, then drains the give queue.</summary>
        public void Run(string command)
        {
            if (command.StartsWith("/ub", StringComparison.Ordinal)) Call("HandleUbCommand", command);
            else Call("HandleMtCommand", command);
            for (int i = 0; i < 20; i++) Call("DrainGiveQueue");
        }

        public uint[] GivenItems => FakeHost.Gives.Select(g => g.Item).ToArray();
    }

    private static void UbGivesAll()
    {
        var w = new World();
        w.Run("/ub give Mana Mote to Town Crier");
        Check.Eq(FakeHost.Gives.Count, 3, "all three carried Mana Motes");
        Check.True(new[] { Mote1, Mote2, Mote3 }.All(id => w.GivenItems.Contains(id)), "each of them");
        Check.False(w.GivenItems.Contains(WornMote), "the worn one stays on");
        Check.False(w.GivenItems.Contains(Other), "an exact name doesn't take 'Lesser Mana Mote'");
        Check.True(FakeHost.Gives.All(g => g.Target == Npc), "all to the NPC, with AC's give action");
        Check.Eq(FakeHost.ExternalMoves.Count, 0, "never MoveItemExternal (an NPC ignores it)");
    }

    private static void UbGiveCount()
    {
        var w = new World();
        w.Run("/ub give 2 Mana Mote to Town Crier");
        Check.Eq(FakeHost.Gives.Count, 2, "a count gives that many");
    }

    private static void MtGivesFirst()
    {
        var w = new World();
        w.Run("/mt give Mana Mote to Town Crier");
        Check.Eq(FakeHost.Gives.Count, 1, "/mt give: one item");
        Check.False(w.GivenItems.Contains(WornMote), "and never the worn one");
    }

    private static void UbGivePartial()
    {
        var w = new World();
        w.Run("/ub givep Mana Mote to Town");
        Check.Eq(FakeHost.Gives.Count, 4, "partial: the three Mana Motes and the Lesser Mana Mote");
        Check.False(w.GivenItems.Contains(WornMote), "still not the worn one");
    }
}
