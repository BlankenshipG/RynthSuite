using System;
using System.Linq;
using System.Reflection;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Silentkelpie, 2026-10-15: with Missile Crafting on, combat swapped Electric Bow + arrows for
// Fire Crossbow + quarrels, and crafting kept wielding 'Infinite Deadly Prismatic Arrow' every
// 5 s (784 tries in one session). The stowed bow still read wielded (the client keeps a stowed
// item's old wielder and slot), the crossbow in hand read slot 0, the server ignored each arrow
// wield, and every try stopped the nav route while it waited.
internal static class MissileCraftingTests
{
    private const uint Player = 0x50000C01;
    private const uint Crossbow = 0x80000601, Bow = 0x80000602, Quarrels = 0x80000603, Arrows = 0x80000604;
    private const int MissileSlot = 0x00400000, AmmoSlot = 0x00800000;

    public static void Register(Runner r)
    {
        r.Add("missile crafting: a stowed bow that still reads wielded is not the launcher (no arrows at a crossbow)", StowedBowNotLauncher);
        r.Add("missile crafting: wielded quarrels are not taken for a bow launcher", AmmoNotLauncher);
        r.Add("missile crafting: an ammo wield the server ignores backs off after 2 tries", IgnoredWieldBacksOff);
        r.Add("missile crafting: a bow in hand with its arrows in the pack still gets them wielded", BowInHandArrowsInPack);
    }

    /// <summary>Kelpie's state at 13:36: crossbow and quarrels in hand (slot 0), bow and arrows in the main pack.</summary>
    private static void StowedBowNotLauncher()
    {
        var cm = Rig(crossbowInHand: true);
        Idle(cm);
        Check.Eq(cm.State, MissileCraftingManager.CraftState.Idle, "crossbow with quarrels on: nothing to do");
        Check.Eq(FakeHost.Uses.Count, 0, "no arrow wield");
        Check.False(FakeHost.Logs.Any(l => l.Contains("MissileCraft: no")), "no evaluation logged");
    }

    /// <summary>The quarrels wielded at login (slot set, typed MissileWeapon) and no launcher readable.</summary>
    private static void AmmoNotLauncher()
    {
        var cm = Rig(crossbowInHand: false, arrange: () =>
        {
            FakeHost.Containers.Remove(Bow);              // no bow anywhere
            FakeHost.Wielded.Remove(Bow);
            FakeHost.Containers.Remove(Quarrels);         // the quarrels on, the crossbow in the pack
            FakeHost.Wielded[Quarrels] = AmmoSlot;
        });
        Idle(cm);
        Check.Eq(cm.State, MissileCraftingManager.CraftState.Idle, "no launcher: crafting idles");
        Check.Eq(FakeHost.Uses.Count, 0, "the quarrels never pass for a bow");
    }

    /// <summary>Bow in hand, arrows in the pack, and the server never puts them on.</summary>
    private static void IgnoredWieldBacksOff()
    {
        var cm = BowRig();
        for (int i = 0; i < 4; i++) Cycle(cm);
        Check.Eq(FakeHost.Uses.Count(u => u == Arrows), 2, "two arrow wields, then no more");
        Check.Eq(cm.State, MissileCraftingManager.CraftState.Idle, "crafting let go");
        Check.True(FakeHost.Logs.Any(l => l.Contains("didn't wield") && l.Contains("2 tries")), "the back-off is logged");

        Cycle(cm);
        Check.Eq(FakeHost.Uses.Count(u => u == Arrows), 2, "inside the back-off: still two");
    }

    /// <summary>The fix must not stop crafting doing its job: no ammo on, the pack's arrows go on.</summary>
    private static void BowInHandArrowsInPack()
    {
        var cm = BowRig();
        Cycle(cm);
        Check.Eq(FakeHost.Uses.LastOrDefault(), Arrows, "UseObject(arrows)");
        Check.True(FakeHost.Logs.Any(l => l.Contains("MissileCraft: no Arrow") && l.Contains("'Longbow'")), "the launcher it saw is logged");
    }

    // ── The rig ─────────────────────────────────────────────────────────────

    private static WorldObjectCache? _cache;

    /// <param name="arrange">Changes to the fake world, made before the host and cache read it.</param>
    private static MissileCraftingManager Rig(bool crossbowInHand, Action? arrange = null)
    {
        PaceOff();
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;       // UseObject, GetCurrentCombatMode
        FakeHost.SalvageCalls = true;      // GetObjectWielderInfo (answers from Wielded)
        FakeHost.Names[Player] = "Silentkelpie";
        Item(Crossbow, "Fire Crossbow", MissileSlot);
        Item(Bow, "Electric Bow", MissileSlot);
        Item(Quarrels, "Infinite Deadly Prismatic Quarrel", AmmoSlot);
        Item(Arrows, "Infinite Deadly Prismatic Arrow", AmmoSlot);

        // The pack listing holds only what is in the pack; the stowed bow's description still
        // names the player and the missile slot.
        FakeHost.Containers[Bow] = Player;
        FakeHost.Containers[Arrows] = Player;
        FakeHost.Wielded[Bow] = MissileSlot;
        if (crossbowInHand)
        {
            FakeHost.Wielded[Crossbow] = 0;                 // wielded after login: slot reads 0
            FakeHost.Wielded[Quarrels] = 0;
        }
        else
        {
            FakeHost.Containers[Crossbow] = Player;
            FakeHost.Containers[Quarrels] = Player;
        }
        arrange?.Invoke();

        var host = FakeHost.Create(Player);
        _cache = FakeHost.MakeCache(host, Player, new[] { Crossbow, Bow, Quarrels, Arrows });
        var cm = new MissileCraftingManager(host, new LegacyUiSettings { EnableMissileCrafting = true });
        cm.SetObjectCache(_cache);
        return cm;
    }

    /// <summary>A real bow in hand (slot set) with nothing in the ammo slot and arrows in the pack.</summary>
    private static MissileCraftingManager BowRig() => Rig(crossbowInHand: false, arrange: () =>
    {
        FakeHost.Containers.Remove(Bow);
        FakeHost.Names[Bow] = "Longbow";
    });

    private static void Item(uint id, string name, int validSlots)
    {
        FakeHost.Names[id] = name;
        FakeHost.ItemTypes[id] = 0x100;                    // MissileWeapon, as the server types ammo too
        FakeHost.Ints[(id, 9)] = validSlots;
    }

    private static readonly FieldInfo LastAmmoCheck =
        typeof(MissileCraftingManager).GetField("_lastAmmoCheck", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo PhaseStart =
        typeof(MissileCraftingManager).GetField("_phaseStart", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>One idle tick with the 5 s ammo-check interval already passed.</summary>
    private static void Idle(MissileCraftingManager cm)
    {
        LastAmmoCheck.SetValue(cm, DateTime.MinValue);
        ForceRescan();
        cm.ProcessCrafting();
    }

    /// <summary>One ammo check through to its wield (or its back-off), the clocks moved past each wait.</summary>
    private static void Cycle(MissileCraftingManager cm)
    {
        for (int i = 0; i < 4 && (i == 0 || cm.State != MissileCraftingManager.CraftState.Idle); i++)
        {
            if (cm.State == MissileCraftingManager.CraftState.Idle)
            {
                // The back-off moves _lastAmmoCheck into the future; only an expired check is reset.
                if ((DateTime)LastAmmoCheck.GetValue(cm)! <= DateTime.Now)
                    LastAmmoCheck.SetValue(cm, DateTime.MinValue);
            }
            else
            {
                PhaseStart.SetValue(cm, DateTime.Now.AddSeconds(-1));   // past EQUIP_DELAY_MS
            }
            ForceRescan();
            cm.ProcessCrafting();
        }
    }

    /// <summary>GetDirectInventory re-serves a scan under 1 s old; the test can't wait on the wall clock.</summary>
    private static void ForceRescan() =>
        typeof(WorldObjectCache).GetField("_lastDirectInventoryScan", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_cache, DateTime.MinValue);

    private static void PaceOff()
    {
        ActionPacer.Enabled = false;
        ActionPacer.Reset();
        ActionPacer.NowMs = () => Environment.TickCount64;
        UseAudit.Reset("?", null);
        UseAudit.NowMs = () => Environment.TickCount64;
        MoveAudit.Reset();
        MoveAudit.NowMs = () => Environment.TickCount64;
    }
}
