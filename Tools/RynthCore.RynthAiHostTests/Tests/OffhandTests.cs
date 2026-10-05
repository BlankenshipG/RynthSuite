using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.CreatureData;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The off hand (2026-10-01): a listed shield with a one-handed weapon, dual wield with the Dual
// Wield skill, nothing with a two-hander / launcher / caster. ACE refuses a two-hander, a launcher
// or a caster while the Shield slot (0x00200000) is occupied, so the shield goes into a pack
// first; with Offhand None nothing in the off hand is ever touched. Listed items only, one action
// in flight, confirmed in the slot, a retry cap, no flip-flop.
internal static class OffhandTests
{
    private const uint Player = 0x50000C01;
    private const uint Monster = 0x80001001;
    private const uint Wcid = 434343;
    private const uint Sword = 0x80000401, Dagger = 0x80000402, Great = 0x80000403, Shield = 0x80000404,
                       Round = 0x80000405, Bow = 0x80000406, Arrows = 0x80000407, Wand = 0x80000408;
    private const uint Pack = 0x80000501;
    private const int MeleeSlot = 0x00100000, ShieldSlot = 0x00200000, MissileSlot = 0x00400000,
                      AmmoSlot = 0x00800000, HeldSlot = 0x01000000, TwoHandedSlot = 0x02000000;

    public static void Register(Runner r)
    {
        r.Add("offhand: ACE's rules and the plan (shield, dual wield, nothing for two-handers)", Rules);
        r.Add("offhand: one-hander + shield, wielded once the main weapon is in hand", OneHanderShield);
        r.Add("offhand: two-hander -> the shield comes off first, back on with a one-hander", TwoHanderShieldOff);
        r.Add("offhand: bow -> the shield comes off, then the arrows still go on", BowShieldOffThenAmmo);
        r.Add("offhand: a caster -> the shield comes off first (swap gate held)", CasterShieldOff);
        r.Add("offhand: dual wield with the Dual Wield skill", DualWield);
        r.Add("offhand: no Dual Wield skill -> the shield", NoSkillShield);
        r.Add("offhand: None -> hands off (a worn shield is never taken off)", NoneHandsOff);
        r.Add("offhand: an unlisted shield is never wielded", UnlistedNeverUsed);
        r.Add("offhand: no flip-flop, and a retry cap", NoFlipFlop);
        r.Add("offhand: the Items list (shields aren't weapons, no element) and buffing's wand", ItemsAndBuffing);
    }

    // ── Pure rules ────────────────────────────────────────────────────────────

    private static void Rules()
    {
        Check.True(OffhandRules.OffhandAllowed(MainHandKind.OneHandedMelee, true), "one-hander + shield");
        Check.True(OffhandRules.OffhandAllowed(MainHandKind.OneHandedMelee, false), "one-hander + off-hand weapon");
        Check.False(OffhandRules.OffhandAllowed(MainHandKind.TwoHanded, true), "two-hander: no shield");
        Check.False(OffhandRules.OffhandAllowed(MainHandKind.Launcher, true), "launcher: no shield");
        Check.False(OffhandRules.OffhandAllowed(MainHandKind.Caster, true), "caster: no shield (Held slot refuses it)");
        Check.True(OffhandRules.OffhandAllowed(MainHandKind.Thrown, true), "thrown: a shield may stay (ACE)");
        Check.False(OffhandRules.OffhandAllowed(MainHandKind.Thrown, false), "thrown: no off-hand weapon");

        Check.True(OffhandRules.IsShield(4, 0, false), "CombatUse Shield");
        Check.True(OffhandRules.IsShield(0, ShieldSlot, false), "a non-weapon for the Shield slot");
        Check.False(OffhandRules.IsShield(0, ShieldSlot, true), "a weapon is never a shield");
        Check.True(OffhandRules.IsTwoHanded(5, 0, 0), "CombatUse TwoHanded");
        Check.True(OffhandRules.IsTwoHanded(0, TwoHandedSlot, 0), "the TwoHanded slot");
        Check.True(OffhandRules.IsTwoHanded(0, MeleeSlot, 41), "WeaponSkill Two Handed Combat (old weapons with a MeleeWeapon slot)");
        Check.True(OffhandRules.CanBeOffhandWeapon(MeleeSlot, false), "ValidLocations exactly MeleeWeapon");
        Check.False(OffhandRules.CanBeOffhandWeapon(MeleeSlot | ShieldSlot, false), "not exactly MeleeWeapon");
        Check.False(OffhandRules.CanBeOffhandWeapon(MeleeSlot, true), "a two-hander never");
        Check.False(OffhandRules.CanBeOffhandWeapon(0, false), "ValidLocations not known: never");

        Check.Eq(OffhandRules.RuleMode(0), null, "0: the global setting");
        Check.Eq(OffhandRules.RuleMode(1), OffhandMode.Auto, "1 Auto");
        Check.Eq(OffhandRules.RuleMode(3), OffhandMode.Weapon, "3 Offhand weapon");
        Check.Eq(OffhandRules.RuleMode(unchecked((int)Shield)), null, "an item id is no mode");
        Check.True(OffhandRules.IsRuleItem(unchecked((int)Shield)), "an item id is an item");
        Check.Eq(OffhandRules.Parse("dual wield", OffhandMode.Auto), OffhandMode.Weapon, "parse 'dual wield'");
        Check.Eq(OffhandRules.Parse("None", OffhandMode.Auto), OffhandMode.None, "parse None");
        Check.Eq(OffhandRules.Parse("junk", OffhandMode.Shield), OffhandMode.Shield, "junk: the fallback");

        var shield = new OffhandCandidate(10, "Kite Shield", true, false);
        var shield2 = new OffhandCandidate(11, "Round Shield", true, true);
        var dagger = new OffhandCandidate(20, "Dagger", false, false);
        var main = new OffhandCandidate(30, "Long Sword", false, false);
        var all = new List<OffhandCandidate> { main, shield, dagger };
        OffhandPlan P(OffhandMode m, MainHandKind k, bool dw = false, bool pref = false, IReadOnlyList<OffhandCandidate>? l = null, int pick = 0)
            => OffhandPlanner.Choose(m, k, 30, l ?? all, dw, pref, true, pick, "pick");
        Check.Eq(P(OffhandMode.Auto, MainHandKind.OneHandedMelee).Id, 10, "Auto, one-hander: the shield");
        Check.Eq(P(OffhandMode.Auto, MainHandKind.TwoHanded).Id, 0, "two-hander: nothing");
        Check.Eq(P(OffhandMode.Shield, MainHandKind.Launcher).Id, 0, "launcher: nothing");
        Check.Eq(P(OffhandMode.Auto, MainHandKind.Caster).Id, 0, "caster: nothing");
        Check.Eq(P(OffhandMode.Auto, MainHandKind.Thrown).Id, 10, "thrown: a listed shield (owner, 2026-10-01)");
        Check.Eq(P(OffhandMode.Weapon, MainHandKind.Thrown, dw: true).Id, 10, "thrown: never a second weapon, the shield");
        Check.Eq(P(OffhandMode.None, MainHandKind.OneHandedMelee).Id, 0, "None: nothing");
        Check.Eq(P(OffhandMode.Auto, MainHandKind.OneHandedMelee, dw: true).Id, 10, "Auto with the skill: the shield (dual wield only when preferred)");
        Check.Eq(P(OffhandMode.Auto, MainHandKind.OneHandedMelee, dw: false).Id, 10, "Auto without the skill: the shield");
        Check.Eq(P(OffhandMode.Auto, MainHandKind.OneHandedMelee, dw: true, pref: true).Id, 20, "Auto, dual wield preferred: the dagger");
        Check.Eq(P(OffhandMode.Auto, MainHandKind.OneHandedMelee, dw: true, l: new[] { main, dagger }).Id, 0, "Auto, no shield listed, not preferred: nothing (two listed swords don't start dual wielding)");
        Check.Eq(P(OffhandMode.Weapon, MainHandKind.OneHandedMelee, dw: true).Id, 20, "Offhand weapon with the skill");
        Check.Eq(P(OffhandMode.Weapon, MainHandKind.OneHandedMelee, dw: false).Id, 10, "Offhand weapon, no skill: the shield");
        Check.Eq(P(OffhandMode.Weapon, MainHandKind.OneHandedMelee, dw: true, l: new[] { main, shield }).Id, 10, "no second weapon: the shield");
        Check.Eq(P(OffhandMode.Shield, MainHandKind.OneHandedMelee, l: new[] { main, dagger }).Id, 0, "Shield, none listed: nothing");
        Check.Eq(P(OffhandMode.Shield, MainHandKind.OneHandedMelee, l: new[] { shield, shield2 }).Id, 11, "the listed shield in the off hand first");
        Check.Eq(P(OffhandMode.Auto, MainHandKind.OneHandedMelee, l: new[] { shield, shield2 }, pick: 10).Id, 10, "an explicit pick wins");
        Check.Eq(P(OffhandMode.Auto, MainHandKind.OneHandedMelee, pick: 20).Id, 10, "an explicit weapon without the skill: the mode");
        Check.Eq(OffhandPlanner.Choose(OffhandMode.Weapon, MainHandKind.OneHandedMelee, 30, all, true, true, false).Id, 10,
            "no slot-wield call on the engine: no dual wield, the shield");
        Check.Eq(P(OffhandMode.Auto, MainHandKind.OneHandedMelee, l: new[] { main }).Id, 0, "the main weapon is never its own off hand");
    }

    // ── The rig ───────────────────────────────────────────────────────────────

    private sealed class Rig
    {
        public readonly LegacyUiSettings S = new();
        public readonly CombatManager C;
        public readonly MonsterDamageStore Store = new();
        public readonly WorldObject Target;
        public int InHand;
        private readonly MethodInfo _equip = typeof(CombatManager).GetMethod("EquipWeaponAndSetStance", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly FieldInfo _lastEquip = typeof(CombatManager).GetField("_lastEquipTime", BindingFlags.Instance | BindingFlags.NonPublic)!;

        public Rig(bool dualWieldSkill = false, bool shieldOn = false, bool wieldCalls = true)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            FakeHost.WieldCalls = wieldCalls;
            FakeHost.CombatModeValue = CombatMode.Melee;
            Item(Sword, "Long Sword", 0x1, MeleeSlot, combatUse: 1);
            Item(Dagger, "Dagger", 0x1, MeleeSlot, combatUse: 1);
            Item(Great, "Greatsword", 0x1, TwoHandedSlot, combatUse: 5);
            Item(Shield, "Kite Shield", 0x2, ShieldSlot, combatUse: 4);
            Item(Round, "Round Shield", 0x2, ShieldSlot, combatUse: 4);
            Item(Bow, "Longbow", 0x100, MissileSlot, combatUse: 2);
            FakeHost.Ints[(Bow, 50)] = 1;
            Item(Arrows, "Arrow", 0x100, AmmoSlot);
            FakeHost.Ints[(Arrows, 50)] = 1; FakeHost.Ints[(Arrows, 12)] = 100; FakeHost.Ints[(Arrows, 11)] = 250;
            Item(Wand, "Wand", 0x8000, HeldSlot);
            Wield(Sword, MeleeSlot);
            if (shieldOn) Wield(Shield, ShieldSlot);
            FakeHost.Wcids[Monster] = Wcid;
            FakeHost.Skills[(Player, 49)] = dualWieldSkill ? (220, 2) : (10, 1);
            var host = FakeHost.Create();
            var cache = FakeHost.MakeCache(host, Player, new[] { Sword, Dagger, Great, Shield, Round, Bow, Arrows, Wand });
            C = new CombatManager(host, S, cache);
            C.SetPlayerId(Player);
            C.SetDamageStores(null, Store);
            var skills = new CharacterSkills(host);
            skills.SetPlayerId(Player);
            C.SetCharacterSkills(skills);
            C.CanCastOverride = _ => false;
            InHand = unchecked((int)Sword);
            C.InHandOverride = () => InHand;
            C.OpenPackOverride = () => unchecked((int)Pack);
            C.OffhandReadMs = 0;
            Target = new WorldObject(unchecked((int)Monster), "Test Dummy", AcObjectClass.Monster);
            List(Sword, Shield);
        }

        public void List(params uint[] ids) =>
            S.ItemRules = ids.Select(i => new ItemRule
            {
                Id = unchecked((int)i), Name = FakeHost.Names[i], Element = "", ElementSource = "",
                Action = i == Shield || i == Round ? WeaponList.ShieldAction : "Weapon",
            }).ToList();

        public void Rule(int offhandId) =>
            S.MonsterRules = new List<MonsterRule> { new() { Name = "Default" }, new() { Name = "Test Dummy", OffhandId = offhandId } };

        public static void Item(uint id, string name, uint itemType, int validSlots, int combatUse = 0)
        {
            FakeHost.Names[id] = name;
            FakeHost.ItemTypes[id] = itemType;
            FakeHost.Ints[(id, 9)] = validSlots;
            if (combatUse != 0) FakeHost.Ints[(id, 51)] = combatUse;
        }

        public static void Wield(uint id, int slot) => FakeHost.Ints[(id, 10)] = slot;
        public static void Unwield(uint id) => FakeHost.Ints.Remove((id, 10));

        /// <summary>One combat equip tick; true = ready to attack.</summary>
        public bool Tick() => (bool)_equip.Invoke(C, new object[] { Target, "Auto" })!;

        /// <summary>Skip the 2 s pacing between a cross-kind wield and the item move before it.</summary>
        public void Unpace() => _lastEquip.SetValue(C, DateTime.MinValue);

        public void Landed(uint weapon, uint old, int slot)
        {
            Unwield(old);
            Wield(weapon, slot);
            InHand = unchecked((int)weapon);
        }

        public int ShieldWields(uint id) => FakeHost.Wields.Count(w => w.Id == id && w.Mask == ShieldSlot);
    }

    // ── Combat ────────────────────────────────────────────────────────────────

    private static void OneHanderShield()
    {
        // The sword isn't in hand yet: it goes on first, no shield before it.
        var rig = new Rig();
        Rig.Unwield(Sword);
        rig.InHand = 0;
        FakeHost.CombatModeValue = CombatMode.NonCombat;
        Check.False(rig.Tick(), "not ready: the sword is being wielded");
        Check.Eq(FakeHost.Uses.LastOrDefault(), Sword, "the sword first");
        Check.Eq(FakeHost.Wields.Count, 0, "no shield before the sword is in hand");

        rig.Landed(Sword, 0, MeleeSlot);
        FakeHost.CombatModeValue = CombatMode.Melee;
        Check.False(rig.Tick(), "sword in hand: the shield wield goes out, the attack waits");
        Check.Eq(rig.ShieldWields(Shield), 1, "one wield of the listed shield into the Shield slot");
        Check.False(rig.Tick(), "in flight: wait");
        Check.Eq(FakeHost.Wields.Count, 1, "not sent again while in flight");

        Rig.Wield(Shield, ShieldSlot);
        Check.True(rig.Tick(), "sword and shield: ready");
        Check.True(rig.Tick(), "and stays ready");
        Check.Eq(FakeHost.Wields.Count, 1, "no further wields");
        Check.Eq(FakeHost.Moves.Count, 0, "nothing moved");
        Check.True(FakeHost.Logs.Any(l => l.Contains("off hand: wielding") && l.Contains("Kite Shield")), "the wield is logged");

        // An engine without the slot-wield call: the shield goes on with a plain use.
        var old = new Rig(wieldCalls: false);
        old.Tick();
        Check.Eq(FakeHost.Uses.LastOrDefault(), Shield, "no WieldItem on the engine: UseObject(shield)");
    }

    private static void TwoHanderShieldOff()
    {
        var rig = new Rig(shieldOn: true);
        rig.List(Sword, Great, Shield);
        Check.True(rig.Tick(), "sword and shield: ready");
        rig.Store.SetManualWeapon(Wcid, Great);              // the Damage tab says greatsword
        rig.List(Sword, Great, Shield);                      // a new list: the plan is worked out again

        Check.False(rig.Tick(), "tick 1: not ready");
        Check.Eq(FakeHost.Moves.Count, 1, "the shield goes into the pack first (the server refuses a two-hander with it)");
        Check.True(FakeHost.Moves.Count == 1 && FakeHost.Moves[0] == (Shield, Pack, 1), "the shield, into the open pack");
        Check.Eq(FakeHost.Uses.Count, 0, "no greatsword wield yet");
        rig.Tick();
        Check.Eq(FakeHost.Moves.Count, 1, "the move in flight: not sent again");
        Check.Eq(FakeHost.Uses.Count, 0, "nor the greatsword");

        Rig.Unwield(Shield);
        Check.False(rig.Tick(), "shield off: the greatsword wield goes out");
        Check.Eq(FakeHost.Uses.LastOrDefault(), Great, "UseObject(greatsword)");
        rig.Landed(Great, Sword, TwoHandedSlot);
        Check.True(rig.Tick(), "greatsword in hand: ready");
        Check.Eq(FakeHost.Wields.Count, 0, "no shield with a two-hander");

        // Back to the sword: the shield goes back on after it.
        rig.C.SwapTracker.MinDwellMs = 0;
        rig.Store.SetManualWeapon(Wcid, Sword);
        rig.List(Sword, Great, Shield);
        rig.Tick();
        Check.Eq(FakeHost.Uses.LastOrDefault(), Sword, "the sword");
        rig.Landed(Sword, Great, MeleeSlot);
        Check.False(rig.Tick(), "sword in hand: the shield goes back on");
        Check.Eq(rig.ShieldWields(Shield), 1, "the shield wielded again");
        Rig.Wield(Shield, ShieldSlot);
        Check.True(rig.Tick(), "ready");

        // A shield RynthAi didn't list but took off itself goes back on too.
        var rig2 = new Rig();
        Rig.Wield(Round, ShieldSlot);                        // worn by hand, not listed
        rig2.List(Sword, Great);
        Check.True(rig2.Tick(), "sword and an unlisted shield worn by hand: left alone");
        Check.Eq(FakeHost.Moves.Count, 0, "not taken off");
        rig2.Store.SetManualWeapon(Wcid, Great);
        rig2.List(Sword, Great);
        rig2.Tick();
        Check.True(FakeHost.Moves.Count == 1 && FakeHost.Moves[0].Id == Round, "the greatsword: the worn shield comes off");
        Rig.Unwield(Round);
        rig2.Tick();
        rig2.Landed(Great, Sword, TwoHandedSlot);
        rig2.Tick();
        rig2.C.SwapTracker.MinDwellMs = 0;
        rig2.Store.SetManualWeapon(Wcid, Sword);
        rig2.List(Sword, Great);
        rig2.Tick();
        rig2.Landed(Sword, Great, MeleeSlot);
        rig2.Tick();
        Check.Eq(rig2.ShieldWields(Round), 1, "what was taken off goes back on with the one-hander");
    }

    private static void BowShieldOffThenAmmo()
    {
        var rig = new Rig(shieldOn: true);
        rig.List(Sword, Bow, Shield);
        rig.Store.SetManualWeapon(Wcid, Bow);
        var plan = rig.C.PlanFor(rig.Target, null);
        Check.Eq(unchecked((uint)plan.WeaponId), Bow, "the plan: the bow (arrows in the pack)");

        Check.False(rig.Tick(), "tick 1: not ready");
        Check.True(FakeHost.Moves.Count == 1 && FakeHost.Moves[0] == (Shield, Pack, 1), "the shield goes into the pack first");
        Check.Eq(FakeHost.Uses.Count, 0, "no bow wield with the shield on (the server refuses a launcher beside it)");

        Rig.Unwield(Shield);
        rig.Unpace();
        Check.False(rig.Tick(), "shield off: the bow wield goes out");
        Check.Eq(FakeHost.Uses.LastOrDefault(), Bow, "UseObject(bow)");
        Check.True(rig.C.AmmoSwapInProgress, "the ammo step is armed");

        rig.Landed(Bow, Sword, MissileSlot);
        FakeHost.CombatModeValue = CombatMode.Missile;
        Check.False(rig.Tick(), "bow in hand: the arrows go on");
        Check.Eq(FakeHost.Uses.LastOrDefault(), Arrows, "UseObject(arrows)");
        Rig.Wield(Arrows, AmmoSlot);
        Check.True(rig.Tick(), "bow and arrows: ready to shoot");
        Check.Eq(FakeHost.Wields.Count, 0, "no shield with a launcher");
        Check.Eq(FakeHost.Moves.Count, 1, "only the shield was moved");
    }

    private static void CasterShieldOff()
    {
        // A mage with a listed wand and a shield on: ACE refuses a Held-slot caster beside a shield.
        var rig = new Rig(shieldOn: true);
        Rig.Unwield(Sword);
        rig.InHand = 0;
        rig.List(Wand, Shield);
        rig.C.CanCastOverride = _ => true;
        var gate = new WeaponSwapGate { MinIntervalMs = 3000 };
        rig.C.SetWeaponSwapGate(gate);
        FakeHost.CombatModeValue = CombatMode.NonCombat;
        Check.False(rig.Tick(), "not ready");
        Check.True(FakeHost.Moves.Count == 1 && FakeHost.Moves[0] == (Shield, Pack, 1), "the shield into the pack (under the gate the wand path holds)");
        Check.False(FakeHost.Uses.Contains(Wand), "no wand wield beside the shield");

        Rig.Unwield(Shield);
        gate.MinIntervalMs = 0;
        rig.Tick();
        Check.True(FakeHost.Uses.Contains(Wand), "the off hand empty: the wand goes on");
        Check.Eq(FakeHost.Wields.Count, 0, "no shield with a caster");
    }

    private static void DualWield()
    {
        var rig = new Rig(dualWieldSkill: true);
        rig.List(Sword, Dagger, Shield);
        rig.S.PreferDualWield = true;
        Check.False(rig.Tick(), "sword in hand: the dagger goes into the off hand");
        Check.Eq(rig.ShieldWields(Dagger), 1, "WieldItem(dagger, Shield slot)");
        Check.Eq(rig.ShieldWields(Shield), 0, "not the shield");
        Rig.Wield(Dagger, ShieldSlot);
        Check.True(rig.Tick(), "sword and dagger: ready");

        // The weapon in hand is the main hand's, never the off hand's.
        rig.C.InHandOverride = null;
        Check.Eq(unchecked((uint)rig.C.InHandWeaponId(fresh: true)), Sword, "the main hand: the sword (the dagger is in the off hand)");
        rig.C.InHandOverride = () => rig.InHand;

        // The weakness / Damage tab wants the dagger, which already swings in the off hand: no swap.
        rig.Store.SetManualWeapon(Wcid, Dagger);
        rig.List(Sword, Dagger, Shield);
        int uses = FakeHost.Uses.Count, wields = FakeHost.Wields.Count;
        Check.True(rig.Tick(), "ready with both");
        Check.Eq(FakeHost.Uses.Count, uses, "no wield moving the dagger across");
        Check.Eq(FakeHost.Wields.Count, wields, "nor into the off hand again");
        Check.True(FakeHost.Logs.Any(l => l.Contains("fighting with both")), "logged");

        // A two-hander: the dagger comes off first, as a shield does.
        rig.Store.SetManualWeapon(Wcid, Great);
        rig.List(Sword, Dagger, Great, Shield);
        rig.Tick();
        Check.True(FakeHost.Moves.Any(m => m.Id == Dagger), "the off-hand dagger into the pack before the greatsword");

        // The rule asks for it without the preference too (a new rig resets the fake).
        var rig2 = new Rig(dualWieldSkill: true);
        rig2.List(Sword, Dagger, Shield);
        rig2.Rule(OffhandRules.RuleWeapon);
        rig2.Tick();
        Check.Eq(rig2.ShieldWields(Dagger), 1, "Monsters rule 'Offhand weapon': the dagger");
    }

    private static void NoSkillShield()
    {
        var rig = new Rig(dualWieldSkill: false);
        rig.List(Sword, Dagger, Shield);
        rig.S.PreferDualWield = true;
        rig.Rule(OffhandRules.RuleWeapon);
        Check.False(rig.Tick(), "the off hand goes on");
        Check.Eq(rig.ShieldWields(Shield), 1, "no Dual Wield skill: the shield");
        Check.Eq(rig.ShieldWields(Dagger), 0, "never the dagger");
        Check.True(FakeHost.Logs.Any(l => l.Contains("Dual Wield not trained")), "the reason is logged");

        // No shield listed either: nothing.
        var rig2 = new Rig(dualWieldSkill: false);
        rig2.List(Sword, Dagger);
        Check.True(rig2.Tick(), "ready with the sword alone");
        Check.Eq(FakeHost.Wields.Count, 0, "no off hand");
    }

    private static void NoneHandsOff()
    {
        // A shield the user wears, not listed, Offhand None everywhere.
        var rig = new Rig();
        Rig.Wield(Round, ShieldSlot);
        rig.S.OffhandDefault = "None";
        rig.List(Sword, Great, Shield);
        Check.True(rig.Tick(), "ready with the sword and the worn shield");
        Check.Eq(FakeHost.Wields.Count, 0, "the listed shield isn't swapped in");
        Check.Eq(FakeHost.Moves.Count, 0, "the worn shield stays");

        rig.Store.SetManualWeapon(Wcid, Great);
        rig.List(Sword, Great, Shield);
        for (int i = 0; i < 4; i++) rig.Tick();
        Check.Eq(FakeHost.Moves.Count, 0, "a two-hander wanted: the worn shield still stays");
        Check.Eq(FakeHost.Uses.Count, 0, "and no greatsword wield the server would refuse");
        Check.True(FakeHost.Logs.Any(l => l.Contains("Offhand is None")), "the hold is logged");

        // None from the Monsters rule, global Auto: same, and nothing goes on an empty off hand.
        var rig2 = new Rig();
        rig2.Rule(OffhandRules.RuleNone);
        Check.True(rig2.Tick(), "ready");
        Check.Eq(FakeHost.Wields.Count, 0, "rule None: the listed shield isn't wielded");

        // A wand with a None shield on: no wand wield (the server refuses it); bare-handed magic.
        var rig3 = new Rig();
        Rig.Wield(Round, ShieldSlot);
        Rig.Unwield(Sword);
        rig3.InHand = 0;
        rig3.S.OffhandDefault = "None";
        rig3.C.CanCastOverride = _ => true;
        rig3.List(Wand);
        FakeHost.CombatModeValue = CombatMode.NonCombat;
        for (int i = 0; i < 3; i++) rig3.Tick();
        Check.False(FakeHost.Uses.Contains(Wand), "no wand wield beside a None shield");
        Check.Eq(FakeHost.Moves.Count, 0, "the shield stays");
    }

    private static void UnlistedNeverUsed()
    {
        var rig = new Rig();
        rig.List(Sword);                                     // the Round Shield is only in the pack
        Check.True(rig.Tick(), "ready with the sword");
        Check.Eq(FakeHost.Wields.Count, 0, "no shield listed: none wielded");

        rig.Store.SetManualOffhand(Wcid, Round);             // the Damage tab names it
        rig.List(Sword);
        Check.True(rig.Tick(), "still ready");
        Check.Eq(FakeHost.Wields.Count, 0, "an unlisted Damage tab offhand isn't wielded");
        Check.True(FakeHost.Logs.Any(l => l.Contains("Damage tab offhand") && l.Contains("isn't in the Items list")), "logged");

        var rig2 = new Rig();
        rig2.List(Sword);
        rig2.Rule(unchecked((int)Round));                     // the rule's old item picker names it
        rig2.Tick();
        Check.Eq(FakeHost.Wields.Count, 0, "an unlisted rule offhand isn't wielded");

        // Listed shield wanted, an unlisted one worn: it comes off, the listed one goes on.
        var rig3 = new Rig();
        Rig.Wield(Round, ShieldSlot);
        rig3.Tick();
        Check.True(FakeHost.Moves.Count == 1 && FakeHost.Moves[0].Id == Round, "the worn unlisted shield into the pack");
        Check.Eq(FakeHost.Wields.Count, 0, "the listed one not before the slot is empty");
        Rig.Unwield(Round);
        rig3.Tick();
        Check.Eq(rig3.ShieldWields(Shield), 1, "then the listed shield");
        Check.Eq(rig3.ShieldWields(Round), 0, "never the unlisted one");

        // A listed Damage-tab offhand wins over Auto's first shield.
        var rig4 = new Rig();
        rig4.List(Sword, Shield, Round);
        rig4.Store.SetManualOffhand(Wcid, Round);
        rig4.Tick();
        Check.Eq(rig4.ShieldWields(Round), 1, "the Damage tab's listed offhand");
    }

    private static void NoFlipFlop()
    {
        var rig = new Rig(dualWieldSkill: true, shieldOn: true);
        rig.List(Sword, Dagger, Shield);
        rig.Rule(OffhandRules.RuleShield);
        Check.True(rig.Tick(), "sword and shield: ready");

        rig.Rule(OffhandRules.RuleWeapon);                   // this monster now wants dual wield
        rig.Tick();
        Check.True(FakeHost.Moves.Count == 1 && FakeHost.Moves[0].Id == Shield, "the shield comes off first");
        Rig.Unwield(Shield);
        rig.Tick();
        Check.Eq(rig.ShieldWields(Dagger), 1, "then the dagger goes on");
        Rig.Wield(Dagger, ShieldSlot);
        Check.True(rig.Tick(), "sword and dagger: ready");
        int moves = FakeHost.Moves.Count, wields = FakeHost.Wields.Count;

        rig.Rule(OffhandRules.RuleShield);                   // straight back to the shield
        for (int i = 0; i < 5; i++) rig.Tick();
        Check.Eq(FakeHost.Moves.Count, moves, "held: the dagger stays");
        Check.Eq(FakeHost.Wields.Count, wields, "no shield wield");
        Check.True(FakeHost.Logs.Any(l => l.Contains("off hand: keeping") && l.Contains("just swapped away from it")), "the hold is logged");

        // A shield that never lands: 3 tries, then a rest; the attack goes on.
        var rig2 = new Rig();
        rig2.C.OffhandTracker.ResolveMs = 0;
        for (int i = 0; i < 8; i++) rig2.Tick();
        Check.Eq(rig2.ShieldWields(Shield), 3, "three tries, then stop");
        Check.True(FakeHost.Logs.Any(l => l.Contains("didn't wield after 3 tries")), "the give-up is logged");
        Check.True(rig2.Tick(), "fighting on without it");
    }

    private static void ItemsAndBuffing()
    {
        var rig = new Rig();
        rig.List(Sword, Shield);
        var cands = rig.C.ListedCandidates();
        Check.Eq(cands.Count, 1, "a listed shield is no main weapon");
        Check.Eq(unchecked((uint)cands[0].Id), Sword, "only the sword");

        var rules = new List<ItemRule>
        {
            new() { Id = 1, Name = "Kite Shield", Action = WeaponList.ShieldAction, Element = "", ElementSource = "" },
            new() { Id = 2, Name = "Fire Sword", Action = "Weapon", Element = "", ElementSource = "" },
        };
        WeaponList.RefreshElements(rules, (id, name) => WeaponElements.Detect(null, null, null, name));
        Check.Eq(rules[0].Element, "", "a shield gets no element");
        Check.Eq(rules[1].Element, "Fire", "the sword does");
        var shown = WeaponList.ForDisplay(rules, r => r.Name);
        Check.Eq(shown[0].Element, "Shield", "the Items panel's element column says Shield");
        shown[0].Element = "Cold";                           // a click on the column
        var merged = WeaponList.MergeEdited(rules, shown);
        Check.Eq(merged[0].Element, "", "a shield keeps no element");
        Check.Eq(merged[0].Action, WeaponList.ShieldAction, "and stays a shield");

        // Buffing's wand: the shield blocks it (ACE refuses a caster beside a shield) unless None.
        var host = FakeHost.Create();
        var cache = FakeHost.MakeCache(host, Player, new[] { Sword, Shield, Wand });
        var s = new LegacyUiSettings();
        var bm = new BuffManager(host, s, new SpellManager(host, s), new PlayerVitalsCache());
        bm.SetWorldObjectCache(cache);
        var find = typeof(BuffManager).GetMethod("FindWieldedNonWandWeapon", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Rig.Unwield(Sword);
        Rig.Wield(Shield, ShieldSlot);
        Check.Eq((int)find.Invoke(bm, new object[] { unchecked((int)Wand) })!, unchecked((int)Shield), "buffing: the shield is the blocker to put away");
        s.OffhandDefault = "None";
        Check.Eq((int)find.Invoke(bm, new object[] { unchecked((int)Wand) })!, 0, "Offhand None: buffing leaves the shield");
        s.OffhandDefault = "Auto";
        Rig.Wield(Sword, MeleeSlot);
        Check.Eq((int)find.Invoke(bm, new object[] { unchecked((int)Wand) })!, unchecked((int)Sword), "the main weapon first, as before");
    }
}
