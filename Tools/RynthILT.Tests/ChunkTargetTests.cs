using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthILT.Hub;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthILT.Tests;

// The chunk rules end to end: a fake inventory with the Lugian Aetheria Hammer and one candidate,
// IltChunkClap.Tick, and whether the hammer was used on the candidate. These cover what the pure
// rule tests (AetheriaChunkTests) cannot: the properties as RynthILT reads them from the host, an
// item that is not appraised, and a host that cannot tell whether an appraisal arrived. The hammer
// destroys what it is used on, so "used on it" is the thing to get right.
internal static class ChunkTargetTests
{
    private const uint Player = 0x50000F01;
    private const uint Hammer = 0x5000A001;
    private const uint Item = 0x5000A002;
    private const uint ItemTypeGem = 0x00000800;
    private const uint ItemTypeMelee = 0x00000001;
    private const uint ItemTypeMisc = 0x00000080;

    public static void Register(Runner r)
    {
        r.Add("chunk (live path): unlevelled Coalesced chunks only when its level was truly read as none", UnlevelledVsUnreadable);
        r.Add("chunk (live path): levelled Coalesced Aetheria obeys the cap; other aetheria needs a level", LevelledCap);
        r.Add("chunk (live path): corrupted two-handed cleave 2 chunks, other corrupted cleave 2 does not", CorruptedCleave);
        r.Add("chunk (live path): corrupted keep rules and the rating level are off by default and protect when on", KeepRulesLive);
        r.Add("chunk (live path): a busy client in combat chunks with chunk-in-combat on, not off; peace waits for idle", CombatBusy);
    }

    /// <summary>One chunk pass on a fake pack. Returns the targets the hammer was used on.</summary>
    private static List<uint> Pass(string folder, Action<IltGearState> settings, Action<uint> candidate,
        uint itemType = ItemTypeGem, string name = "Coalesced Aetheria", bool appraised = true,
        bool hostCanSayAppraised = true, bool busy = false, int combatMode = 1)
    {
        FakeHost.Reset();
        FakeHost.WorldName = "InfiniteLeaftide";
        FakeHost.ContentsCalls = true;
        FakeHost.UseOnCalls = true;
        FakeHost.WeaponCalls = hostCanSayAppraised;   // installs HasAppraisalData / RequestId / combat mode
        FakeHost.BusyCalls = true;
        FakeHost.BusyStateValue = busy ? 1 : 0;
        FakeHost.CombatModeValue = combatMode;
        FakeHost.Names[Hammer] = "Lugian Aetheria Hammer"; FakeHost.ItemTypes[Hammer] = ItemTypeMisc; FakeHost.Containers[Hammer] = Player;
        FakeHost.Names[Item] = name; FakeHost.ItemTypes[Item] = itemType; FakeHost.Containers[Item] = Player;
        if (appraised) FakeHost.Appraised.Add(Item);
        candidate(Item);
        var host = FakeHost.Create(Player);
        string dir = Path.Combine(Program.TempRoot, "chunk-" + folder);
        Directory.CreateDirectory(dir);
        var hub = new IltHubController(host, dir, new IltSettings(), new RynthAiClient(host));
        var g = hub.State.Gear;
        g.AutoChunkAetheria = true;
        g.AutoChunkCorrupted = true;
        settings(g);
        hub.Gear.ChunkClap.Tick(10_000);
        return FakeHost.UsesOn.Where(u => u.Source == Hammer).Select(u => u.Target).ToList();
    }

    private static void Ints(uint id, params (uint Key, int Value)[] values)
    {
        foreach (var (k, v) in values) FakeHost.Ints[(id, k)] = v;
    }

    private static void UnlevelledVsUnreadable()
    {
        // Truly unlevelled: the appraisal arrived and the item has no level (int 319), no XP, no tinks.
        Check.Eq(Pass("coal-unlevelled", _ => { }, _ => { }).Count, 1, "appraised, no level: chunked (ub-IT)");
        // Even with the cap at its lowest.
        Check.Eq(Pass("coal-unlevelled-cap1", g => g.ChunkAetheriaUnderLevel = 1, _ => { }).Count, 1, "cap 1 does not stop an unlevelled one");
        // Not appraised yet: nothing is known, so it is asked for, not hammered.
        var asked = Pass("coal-unappraised", _ => { }, _ => { }, appraised: false);
        Check.Eq(asked.Count, 0, "not appraised: not chunked");
        Check.True(FakeHost.IdRequests.Contains(Item), "its appraisal is requested");
        // The host cannot say whether an appraisal arrived: a level of 0 might be a failed read.
        Check.Eq(Pass("coal-unreadable", _ => { }, _ => { }, hostCanSayAppraised: false).Count, 0,
            "host cannot confirm the appraisal: kept, not chunked");
        // The same unconfirmable host with a read level still keeps (no half-trusted piece is hammered).
        Check.Eq(Pass("coal-unreadable-lvl", _ => { }, id => Ints(id, (IltInventory.IntItemMaxLevel, 2)), hostCanSayAppraised: false).Count, 0,
            "host cannot confirm the appraisal: even a read level 2 is kept");
        // A revealed (non-Coalesced) aetheria with no level is never chunked.
        Check.Eq(Pass("rev-unlevelled", _ => { }, _ => { }, name: "Blue Aetheria").Count, 0, "revealed aetheria with no level: kept");
        // Item XP, tinks or the never-chunk flag keep it.
        Check.Eq(Pass("coal-xp", _ => { }, id => FakeHost.Quads[(id, IltInventory.QuadItemTotalXp)] = 500).Count, 0, "has item XP: kept");
        Check.Eq(Pass("coal-tinked", _ => { }, id => Ints(id, (IltInventory.IntNumTimesTinkered, 1))).Count, 0, "tinkered: kept");
        Check.Eq(Pass("coal-never", _ => { }, id => FakeHost.Bools[(id, IltInventory.BoolNeverChunk)] = true).Count, 0, "never-chunk flag: kept");
    }

    private static void LevelledCap()
    {
        Check.Eq(Pass("cap-l3", _ => { }, id => Ints(id, (IltInventory.IntItemMaxLevel, 3))).Count, 1, "level 3 under the default cap of 4");
        Check.Eq(Pass("cap-l4", _ => { }, id => Ints(id, (IltInventory.IntItemMaxLevel, 4))).Count, 0, "level 4 at the default cap of 4: kept");
        Check.Eq(Pass("cap9-l8", g => g.ChunkAetheriaUnderLevel = 9, id => Ints(id, (IltInventory.IntItemMaxLevel, 8))).Count, 1, "level 8 under a cap of 9");
        Check.Eq(Pass("cap9-l9", g => g.ChunkAetheriaUnderLevel = 9, id => Ints(id, (IltInventory.IntItemMaxLevel, 9))).Count, 0, "level 9 at a cap of 9: kept");
        Check.Eq(Pass("cap9-l10", g => g.ChunkAetheriaUnderLevel = 9, id => Ints(id, (IltInventory.IntItemMaxLevel, 10))).Count, 0, "level 10 over a cap of 9: kept");
        Check.Eq(Pass("cap11-l10", g => g.ChunkAetheriaUnderLevel = 11, id => Ints(id, (IltInventory.IntItemMaxLevel, 10))).Count, 1, "cap 11 chunks every level");
        Check.Eq(Pass("rev-l2", _ => { }, id => Ints(id, (IltInventory.IntItemMaxLevel, 2)), name: "Blue Aetheria").Count, 1, "a levelled revealed piece under the cap");
    }

    private static void CorruptedCleave()
    {
        void Weapon(uint id, int cleave, int wieldSkill = 45, int weaponType = 0, int attackType = 4) =>
            Ints(id, (IltInventory.IntItemWorkmanship, 6), (IltInventory.IntCleaving, cleave), (IltInventory.IntWieldSkillType, wieldSkill),
                (IltInventory.IntWeaponType, weaponType), (IltInventory.IntAttackType, attackType));
        Check.Eq(Pass("cor-1h-c1", _ => { }, id => Weapon(id, 1), ItemTypeMelee, "Corrupted Sword").Count, 1, "one-handed cleave 1 chunks");
        Check.Eq(Pass("cor-1h-c2", _ => { }, id => Weapon(id, 2), ItemTypeMelee, "Corrupted Sword").Count, 0, "one-handed cleave 2 is kept");
        Check.Eq(Pass("cor-2h-c2", _ => { }, id => Weapon(id, 2, wieldSkill: 41), ItemTypeMelee, "Corrupted Greatsword").Count, 1, "two-handed cleave 2 chunks");
        Check.Eq(Pass("cor-2h-c3", _ => { }, id => Weapon(id, 3, wieldSkill: 41), ItemTypeMelee, "Corrupted Greatsword").Count, 0, "two-handed cleave 3 is kept");
        Check.Eq(Pass("cor-2h-thrust", _ => { }, id => Weapon(id, 2, wieldSkill: 41, attackType: 2), ItemTypeMelee, "Corrupted Spear").Count, 0, "two-handed thrust cleave 2 is kept");
        Check.Eq(Pass("cor-nowork", _ => { }, id => Ints(id, (IltInventory.IntCleaving, 1)), ItemTypeMelee, "Corrupted Sword").Count, 0, "no workmanship read: kept");
        Check.Eq(Pass("cor-unreadable", _ => { }, id => Weapon(id, 1), ItemTypeMelee, "Corrupted Sword", hostCanSayAppraised: false).Count, 0,
            "host cannot confirm the appraisal: kept");
        Check.Eq(Pass("cor-off", g => g.AutoChunkCorrupted = false, id => Weapon(id, 1), ItemTypeMelee, "Corrupted Sword").Count, 0, "auto-chunk corrupted off: not touched");
    }

    private static void KeepRulesLive()
    {
        var def = new IltGearState();
        Check.True(!def.KeepCorruptedCrushingBlow && !def.KeepCorruptedBitingStrike && !def.KeepCorruptedArmorCleaving
                   && !def.KeepCorruptedAnySlayer && !def.KeepCorruptedMultiStrike && !def.KeepCorruptedElementalBonus
                   && !def.KeepCorruptedAnySpell && def.KeepCorruptedMaxDamageAtLeast == 0 && def.KeepCorruptedSpellIds == "",
            "every corrupted keep rule is off by default");
        Check.Eq(def.KeepAetheriaRatingAtLeast, 1, "the rating level defaults to 1 (any rating keeps, as before)");

        void Loaded(uint id)
        {
            Ints(id, (IltInventory.IntItemWorkmanship, 6), (IltInventory.IntCleaving, 1), (IltInventory.IntAttackType, 160),
                (IltInventory.IntSlayerCreatureType, 5), (IltInventory.IntElementalDamageBonus, 10), (IltInventory.IntDamage, 400));
            FakeHost.Doubles[(id, IltInventory.FloatCriticalFrequency)] = 0.1;
            FakeHost.Doubles[(id, IltInventory.FloatCriticalMultiplier)] = 1.5;
            FakeHost.Doubles[(id, IltInventory.FloatIgnoreArmor)] = 0.2;
            FakeHost.SpellIds[id] = new uint[] { 2072 };
        }
        int Run(string tag, Action<IltGearState> s) => Pass("keep-" + tag, s, Loaded, ItemTypeMelee, "Corrupted Sword").Count;
        Check.Eq(Run("none", _ => { }), 1, "a loaded corrupted weapon chunks while every keep rule is off");
        Check.Eq(Run("crush", g => g.KeepCorruptedCrushingBlow = true), 0, "Crushing Blow on: kept");
        Check.Eq(Run("bite", g => g.KeepCorruptedBitingStrike = true), 0, "Biting Strike on: kept");
        Check.Eq(Run("armor", g => g.KeepCorruptedArmorCleaving = true), 0, "Armor Cleaving on: kept");
        Check.Eq(Run("slayer", g => g.KeepCorruptedAnySlayer = true), 0, "slayer on: kept");
        Check.Eq(Run("multi", g => g.KeepCorruptedMultiStrike = true), 0, "multi-strike on: kept");
        Check.Eq(Run("elem", g => g.KeepCorruptedElementalBonus = true), 0, "elemental bonus on: kept");
        Check.Eq(Run("maxdmg", g => g.KeepCorruptedMaxDamageAtLeast = 400), 0, "max damage 400 on: kept");
        Check.Eq(Run("maxdmg-hi", g => g.KeepCorruptedMaxDamageAtLeast = 401), 1, "max damage above the weapon's: chunks");
        Check.Eq(Run("spell", g => g.KeepCorruptedAnySpell = true), 0, "any spell on: kept");
        Check.Eq(Run("spellid", g => g.KeepCorruptedSpellIds = "2072"), 0, "a listed spell id: kept");
        Check.Eq(Run("spellid-other", g => g.KeepCorruptedSpellIds = "100"), 1, "another spell id: chunks");

        // The rating level on a live Coalesced piece with a C 1 rating.
        void C1(uint id) => Ints(id, (IltInventory.IntGearCrit, 1));
        Check.Eq(Pass("rate-default", _ => { }, C1).Count, 0, "C 1 is kept at the default rating level");
        Check.Eq(Pass("rate-2", g => g.KeepAetheriaRatingAtLeast = 2, C1).Count, 1, "C 1 chunks once the rating level is 2");
        Check.Eq(Pass("rate-2-c2", g => g.KeepAetheriaRatingAtLeast = 2, id => Ints(id, (IltInventory.IntGearCrit, 2))).Count, 0, "C 2 is kept at level 2");
        Check.Eq(Pass("rate-2-off", g => { g.KeepAetheriaRatingAtLeast = 2; g.KeepAetheriaWithCrit = false; }, id => Ints(id, (IltInventory.IntGearCrit, 5))).Count, 1,
            "the C switch off ignores the rating level");
    }

    private static void CombatBusy()
    {
        Check.Eq(Pass("busy-combat-on", g => g.ChunkInCombat = true, _ => { }, busy: true, combatMode: 2).Count, 1,
            "busy in combat mode with chunk-in-combat on: chunks");
        Check.Eq(Pass("busy-combat-off", g => g.ChunkInCombat = false, _ => { }, busy: true, combatMode: 2).Count, 0,
            "busy in combat mode with chunk-in-combat off: waits");
        Check.Eq(Pass("idle-combat-on", g => g.ChunkInCombat = true, _ => { }, busy: false, combatMode: 2).Count, 1,
            "idle in combat mode with chunk-in-combat on: chunks");
        Check.Eq(Pass("busy-peace", g => g.ChunkInCombat = true, _ => { }, busy: true, combatMode: 1).Count, 0,
            "busy in peace mode waits for idle even with chunk-in-combat on");
        Check.Eq(Pass("idle-peace", g => g.ChunkInCombat = false, _ => { }, busy: false, combatMode: 1).Count, 1,
            "idle in peace mode chunks");
    }
}
