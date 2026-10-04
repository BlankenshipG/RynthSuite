using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-10-03, Dargoth Hera on a busy server: RynthAi never looted his own kills, in "My Kills
// Only" and in "Loot All". Two causes:
//  1. A fresh corpse whose name and type (Container) were in the engine's identity snapshot
//     before its position was got filed as an inventory Container ("nopos-item") and was never
//     looked at again, so it was never a loot candidate. Your own kills hit it most: they are
//     created right next to you and classified at once.
//  2. Combat outranks looting, and a busy spawn always has a monster inside MonsterRange, so
//     a corpse waited until the area emptied. A lootable corpse in range that has waited
//     LootStarveMs now gets a turn while no monster is within LootStarveCloseYards.
internal static class LootCorpseTests
{
    private const uint Player = 0x50000D01;
    private const uint Corpse = 0x80261100;
    private const uint Gem = 0x80261101;
    private const uint Monster = 0x80246096;
    private const uint ItemTypeContainer = 0x200, ItemTypeGem = 0x800, ItemTypeCreature = 0x10;
    private const uint BfCorpse = 0x2000;
    private const uint LongDescKey = 16;
    private const string Me = "Dargoth Hera";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("corpse: created before its position is readable, it is still a Corpse on the landscape", CorpseWithoutPositionIsCorpse);
        r.Add("corpse: the weenie corpse flag makes a nameless corpse a Corpse", CorpseFlagWithoutName);
        r.Add("corpse: one stuck in the inventory as a Container is moved to the landscape", CorpseInInventoryRescued);
        r.Add("corpse: a corpse named late (stored nameless) becomes a Corpse on access", CorpseNamedLateOnAccess);
        r.Add("loot chain: own corpse appears -> claimed -> opened -> item moved into the pack", FullLootChain);
        r.Add("loot chain: 'My Kills Only' loots your kill and skips another player's", OwnershipModes);
        r.Add("loot starvation: a corpse waiting behind far-off combat gets a turn, a close monster keeps combat", LootStarvation);
        r.Add("loot logging: a corpse held by combat is logged once, with the reason", HeldByCombatLoggedOnce);
    }

    // ── World set-up ──────────────────────────────────────────────────────────

    private static (uint Cell, float X, float Y, float Z) Me0 => (0x1048002Au, 100f, 100f, 0f);

    private static void BaseWorld(bool corpsePositioned)
    {
        FakeHost.Reset();
        FakeHost.SalvageCalls = true; // UseObject + MoveItemExternal
        FakeHost.WeaponCalls = true;  // RequestId, HasAppraisalData
        FakeHost.Names[Player] = Me;
        FakeHost.Positions[Player] = Me0;
        FakeHost.PlayerPose = Me0;
        FakeHost.Names[Corpse] = "Corpse of Ancient Water Elemental";
        FakeHost.ItemTypes[Corpse] = ItemTypeContainer;
        FakeHost.Strings[(Corpse, LongDescKey)] = $"Killed by {Me}.";
        FakeHost.NotAttackable.Add(Corpse);
        if (corpsePositioned)
            FakeHost.Positions[Corpse] = (Me0.Cell, Me0.X + 1.0f, Me0.Y, Me0.Z);
        FakeHost.Names[Gem] = "Black Opal";
        FakeHost.ItemTypes[Gem] = ItemTypeGem;
        FakeHost.Containers[Gem] = Corpse;   // also installs GetObjectOwnershipInfo
        FakeHost.Appraised.Add(Gem);
    }

    // ── Classification (cause 1) ──────────────────────────────────────────────

    private static void CorpseWithoutPositionIsCorpse()
    {
        BaseWorld(corpsePositioned: false);
        var host = FakeHost.Create(Player);
        var cache = new WorldObjectCache(host);
        cache.SetPlayerId(Player);

        cache.OnCreateObject(Corpse);
        cache.Tick();

        Check.Eq(cache[unchecked((int)Corpse)]?.ObjectClass, AcObjectClass.Corpse, "a Corpse, not an inventory Container");
        Check.True(cache.GetLandscapeObjects().Any(o => o.Id == unchecked((int)Corpse)), "on the landscape (loot candidates come from there)");
        Check.False(cache.GetInventory().Any(o => o.Id == unchecked((int)Corpse)), "not in the inventory");

        FakeHost.Positions[Corpse] = (Me0.Cell, Me0.X + 1.0f, Me0.Y, Me0.Z);
        double d = cache.Distance(unchecked((int)Player), unchecked((int)Corpse));
        Check.True(d < 1.5, $"its distance reads once the position lands (d={d:0.00})");
    }

    private static void CorpseFlagWithoutName()
    {
        BaseWorld(corpsePositioned: true);
        FakeHost.Names.Remove(Corpse);
        FakeHost.Bitfields[Corpse] = BfCorpse;
        var host = FakeHost.Create(Player);
        var cache = new WorldObjectCache(host);
        cache.SetPlayerId(Player);

        cache.OnCreateObject(Corpse);
        cache.Tick();

        Check.Eq(cache[unchecked((int)Corpse)]?.ObjectClass, AcObjectClass.Corpse, "the corpse flag alone makes it a Corpse");
        FakeHost.Names[Corpse] = "Corpse of Ancient Water Elemental";
        Check.Eq(cache[unchecked((int)Corpse)]?.Name, "Corpse of Ancient Water Elemental", "the name fills in later");
    }

    private static void CorpseInInventoryRescued()
    {
        // The old trap, reproduced directly: an entry filed as an inventory Container (as
        // TryClassify did before the fix when the position wasn't readable yet).
        BaseWorld(corpsePositioned: false);
        FakeHost.Names.Remove(Corpse);
        var host = FakeHost.Create(Player);
        var cache = new WorldObjectCache(host);
        cache.SetPlayerId(Player);
        _ = cache[unchecked((int)Corpse)]; // nameless, positionless: the lazy lookup says "not yet"
        // File it as the old code did.
        var byId = (Dictionary<int, WorldObject>)typeof(WorldObjectCache).GetField("_byId", Any)!.GetValue(cache)!;
        var inv = (HashSet<int>)typeof(WorldObjectCache).GetField("_inventory", Any)!.GetValue(cache)!;
        var make = typeof(WorldObjectCache).GetMethod("Make", Any)!;
        lock (typeof(WorldObjectCache).GetField("_gate", Any)!.GetValue(cache)!)
        {
            byId[unchecked((int)Corpse)] = (WorldObject)make.Invoke(cache, new object[] { unchecked((int)Corpse), "", AcObjectClass.Container })!;
            inv.Add(unchecked((int)Corpse));
        }
        FakeHost.Names[Corpse] = "Corpse of Ancient Water Elemental";
        FakeHost.Positions[Corpse] = (Me0.Cell, Me0.X + 1.0f, Me0.Y, Me0.Z);

        typeof(WorldObjectCache).GetField("_lastReclassifyTime", Any)!.SetValue(cache, DateTime.MinValue);
        cache.Tick(); // the 2 s pass

        var wo = byId[unchecked((int)Corpse)];
        Check.Eq(wo.ObjectClass, AcObjectClass.Corpse, "rescued to Corpse");
        Check.True(cache.GetLandscapeObjects().Any(o => o.Id == unchecked((int)Corpse)), "and on the landscape");
        Check.True(FakeHost.Logs.Any(l => l.Contains("was filed as Container")), "the rescue is logged");
    }

    private static void CorpseNamedLateOnAccess()
    {
        BaseWorld(corpsePositioned: true);
        FakeHost.Names.Remove(Corpse);
        FakeHost.ItemTypes.Remove(Corpse);
        var host = FakeHost.Create(Player);
        var cache = new WorldObjectCache(host);
        cache.SetPlayerId(Player);
        _ = cache[unchecked((int)Corpse)];  // positioned, nameless, typeless: Unknown landscape
        Check.Eq(cache[unchecked((int)Corpse)]?.ObjectClass, AcObjectClass.Unknown, "stored as Unknown first");

        FakeHost.Names[Corpse] = "Corpse of Ancient Water Elemental";
        Check.Eq(cache[unchecked((int)Corpse)]?.ObjectClass, AcObjectClass.Corpse, "a Corpse as soon as its name reads");
    }

    // ── The plugin, without Initialize (no UI, no disk) ───────────────────────

    private sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new()
        {
            IsMacroRunning = true,
            EnableLooting = true,
            EnableCombat = false,
            LootOwnership = 0, // My Kills Only
            LootContentSettleMs = 0,
            LootEmptyCorpseMs = 0,
            StopLootingWhenPackFull = false,
        };
        public readonly WorldObjectCache Cache;

        public Bot(RynthCoreHost host)
        {
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            Set("_dashboard", dash);
            Cache = new WorldObjectCache(host);
            Cache.SetPlayerId(Player);
            Set("_objectCache", Cache);
            Set("_playerId", Player);

            string profile = Path.Combine(Program.TempRoot, "loot-corpse-test.json");
            var p = new LootProfile { Name = "gems" };
            var rule = new LootRule { Name = "Gems", Action = LootAction.Keep };
            rule.Conditions.Add(new ObjectClassCondition { ObjectClass = AcObjectClass.Gem });
            p.Rules.Add(rule);
            p.Save(profile);
            S.CurrentLootPath = profile;
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public T Get<T>(string f) => (T)typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P)!;
        public object? Call(string m, params object[] a) => typeof(RynthAiPlugin).GetMethod(m, Any)!.Invoke(P, a);
        public bool HasLootWork() => (bool)Call("HasLootWork", S, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())!;
        public void Tick(BotActivity act)
        {
            Set("_activity", act);
            Call("TickCorpseOpening");
        }
    }

    private static void FullLootChain()
    {
        BaseWorld(corpsePositioned: false);   // identity before position: the old trap
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);

        bot.Cache.OnCreateObject(Corpse);
        bot.Cache.Tick();
        Check.Eq(bot.Cache[unchecked((int)Corpse)]?.ObjectClass, AcObjectClass.Corpse, "the fresh corpse is a Corpse");

        FakeHost.Positions[Corpse] = (Me0.Cell, Me0.X + 1.0f, Me0.Y, Me0.Z);
        Check.True(bot.HasLootWork(), "looting wants the tick: an own corpse in range");

        bot.Tick(BotActivity.Looting);
        Check.Eq(bot.Get<int>("_targetCorpseId"), unchecked((int)Corpse), "the corpse is claimed");
        Check.True(FakeHost.Logs.Any(l => l.Contains("claimed corpse target")), "claim logged");
        Check.True(FakeHost.Uses.Contains(Corpse), "the corpse is opened (UseObject on it)");

        // The server opens it: the item arrives, then ViewContents.
        bot.Cache.OnCreateObject(Gem);
        bot.Cache.Tick();
        bot.P.OnViewObjectContents(Corpse);

        for (int i = 0; i < 100 && !FakeHost.ExternalMoves.Any(m => m.Id == Gem); i++)
        {
            bot.Tick(BotActivity.Looting);
            Thread.Sleep(10);
        }
        Check.True(FakeHost.ExternalMoves.Contains((Gem, Player, 0)),
            "the gem is moved into the main pack (moves: " + string.Join(",", FakeHost.ExternalMoves.Select(m => $"{m.Id:X8}->{m.Target:X8}")) + ")");
        Check.False(FakeHost.Uses.Contains(Gem), "never used (a use can wield)");
    }

    private static void OwnershipModes()
    {
        BaseWorld(corpsePositioned: true);
        const uint Other = 0x80261200;
        FakeHost.Names[Other] = "Corpse of Ancient Water Elemental";
        FakeHost.ItemTypes[Other] = ItemTypeContainer;
        FakeHost.Strings[(Other, LongDescKey)] = "Killed by Charlie.";
        FakeHost.Positions[Other] = (Me0.Cell, Me0.X, Me0.Y + 1.0f, Me0.Z);
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        bot.Cache.OnCreateObject(Corpse);
        bot.Cache.OnCreateObject(Other);
        bot.Cache.Tick();

        bool Loot(uint id) => (bool)bot.Call("ShouldLootCorpse", bot.Cache[unchecked((int)id)]!)!;
        Check.True(Loot(Corpse), "My Kills Only: your own kill is looted");
        Check.False(Loot(Other), "My Kills Only: another player's kill is skipped");
        bot.S.LootOwnership = 2;
        Check.True(Loot(Other), "All Corpses: anyone's kill is looted");
    }

    private static Bot StarvationWorld(float monsterMeters, out CombatManager combat)
    {
        BaseWorld(corpsePositioned: true);
        FakeHost.Names[Monster] = "Ancient Water Elemental";
        FakeHost.ItemTypes[Monster] = ItemTypeCreature;
        FakeHost.Positions[Monster] = (Me0.Cell, Me0.X + monsterMeters, Me0.Y + 0.5f, Me0.Z);
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        bot.S.EnableCombat = true;
        bot.S.MonsterRange = 50;
        bot.Cache.OnCreateObject(Corpse);
        _ = bot.Cache[unchecked((int)Monster)];
        bot.Cache.OnUpdateHealth(Monster, 1f);
        bot.Cache.Tick();
        combat = new CombatManager(host, bot.S, bot.Cache);
        combat.SetPlayerId(Player);
        typeof(CombatManager).GetField("_lastScanTime", Any)?.SetValue(combat, DateTime.MinValue);
        combat.ScanNearbyTargets();
        bot.Set("_combatManager", combat);
        return bot;
    }

    private static void AgeCorpse(Bot bot, long ms)
    {
        var waiting = bot.Get<Dictionary<int, long>>("_corpseWaitingSince");
        waiting[unchecked((int)Corpse)] = Environment.TickCount64 - ms;
    }

    private static void LootStarvation()
    {
        var bot = StarvationWorld(15f, out var combat);
        Check.True(combat.HasTargets, "combat has a target in range (15 m)");
        Check.True(combat.HasEngageableTarget, "and wants the tick");

        Check.True(bot.HasLootWork(), "the corpse is lootable and in range");
        Check.False(bot.Get<bool>("_lootStarved"), "fresh: not starved yet");
        var noStarve = new ArbiterInputs(true, false, true, true, false, true, combatEngaged: true);
        Check.Eq(ActivityArbiter.Decide(in noStarve), BotActivity.Combat, "combat first while the corpse is fresh");

        bot.Tick(BotActivity.Combat);
        Check.Eq(bot.Get<int>("_targetCorpseId"), 0, "no claim while combat holds the tick");

        AgeCorpse(bot, RynthAiPlugin.LootStarveMs + 1000);
        Check.True(bot.HasLootWork(), "still lootable");
        Check.True(bot.Get<bool>("_lootStarved"), "waited past LootStarveMs: starved");
        Check.True((bool)bot.Call("IsLootStarvedTurn", true, true)!, "no monster within 5 yd: looting gets a turn");
        var starve = new ArbiterInputs(true, false, true, true, false, true, combatEngaged: true, lootStarved: true);
        Check.Eq(ActivityArbiter.Decide(in starve), BotActivity.Looting, "the arbiter gives Looting the tick");

        bot.Set("_lootStarveTurn", true);
        bot.Tick(BotActivity.Looting);
        Check.Eq(bot.Get<int>("_targetCorpseId"), unchecked((int)Corpse), "the starved corpse is claimed despite combat targets");
        Check.True(FakeHost.Uses.Contains(Corpse), "and opened");

        // A monster on top of you keeps combat in charge.
        var close = StarvationWorld(2f, out var combat2);
        Check.True(combat2.HasCloseThreat(RynthAiPlugin.LootStarveCloseYards), "a monster within 5 yd");
        AgeCorpse(close, RynthAiPlugin.LootStarveMs + 1000);
        Check.True(close.HasLootWork(), "corpse lootable");
        Check.False((bool)close.Call("IsLootStarvedTurn", true, true)!, "close monster: no loot turn, combat keeps the tick");
        var buffing = new ArbiterInputs(true, true, true, true, false, true, lootStarved: true);
        Check.Eq(ActivityArbiter.Decide(in buffing), BotActivity.Buffing, "buffing still outranks a starved corpse");
    }

    private static void HeldByCombatLoggedOnce()
    {
        var bot = StarvationWorld(15f, out _);
        bot.HasLootWork();
        for (int i = 0; i < 5; i++) bot.Tick(BotActivity.Combat);
        int lines = FakeHost.Logs.Count(l => l.Contains($"corpse 0x{Corpse:X8} waits for combat"));
        Check.Eq(lines, 1, "one line for the held corpse, not one per tick");
    }
}
