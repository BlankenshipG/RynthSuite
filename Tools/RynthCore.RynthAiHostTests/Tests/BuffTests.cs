using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Buff and spell selection: which tier a buffed skill allows (SpellManager), which spell id a
// buff resolves to at each tier (lore names, Incantations, auras, banes, the spellbook
// snapshot), how long the bot thinks a buff lasts, and when it recasts (BuffManager.NeedsAnyBuff
// over the live enchantment registry and chat-confirmed item spells). Expectations about spell
// names, tiers and durations are read from the plugin's own SpellData.txt (Fakes\SpellTable).
internal static class BuffTests
{
    private const uint Player = 0x50000B01;
    private const uint STypeCreature = 31, STypeItem = 32;

    /// <summary>Buff/tier pairs the name tables used to miss (fixed by 940cb21); NameGapsResolve checks them by name.</summary>
    private static readonly HashSet<(string Buff, int Tier)> NameGaps = new()
    {
        ("Bludgeoning Bane", 1), ("Bludgeoning Bane", 2), ("Bludgeoning Bane", 3), ("Bludgeoning Bane", 4),
        ("Bludgeoning Bane", 5), ("Bludgeoning Bane", 6), ("Bludgeoning Bane", 8),
        ("Mana Conversion Mastery", 7), ("Two Handed Combat Mastery", 7), ("Magic Item Tinkering Expertise", 7),
    };

    private static readonly string[] Auras =
        { "Blood Drinker Self", "Hermetic Link Self", "Heart Seeker Self", "Spirit Drinker Self", "Swift Killer Self", "Defender Self" };

    public static void Register(Runner r)
    {
        r.Add("buff tier: highest tier for a buffed skill, at every threshold", TierThresholds);
        r.Add("buff tier: a skill that can't be read doesn't collapse to tier 1", TierUnreadableSkills);
        r.Add("buff choice: the highest known tier, lower when a tier isn't known", KnownTierWalk);
        r.Add("buff choice: a cold spellbook uses the no-chat blacklist; a warm one overrides it", ColdAndWarm);
        r.Add("buff choice: every buff in the list resolves at every tier (checked against the spell data)", EveryBuffEveryTier);
        r.Add("buff choice: tier 7 lore names and tier 8 forms", LoreAndIncantations);
        r.Add("buff level: spell level from the name", SpellLevels);
        r.Add("buff timers: the duration the bot assumes matches the spell data", DurationsMatchData);
        r.Add("buff timers: Archmage's Endurance adds 20% a rank, at most 5", ArchmageEndurance);
        r.Add("buff school: which magic skill casts each buff", SkillForBuff);
        r.Add("buff school: which buffs live on an item", ItemEnchantments);
        r.Add("rebuff: recast only under the rebuff threshold", RebuffThreshold);
        r.Add("rebuff: permanent, expired, and two enchantments in one family", RebuffPermanentAndDuplicates);
        r.Add("rebuff: a higher tier is only chased above the tier that has landed", RebuffTierUpgrade);
        r.Add("rebuff: a chat-confirmed item spell counts as on", ItemSpellFromChat);

        r.Add("buff choice: spell names the tables miss (Bludgeon Bane, Nuhmudira's, T'ing, Celdiseth's)", NameGapsResolve);   // was a known failure; fixed by 940cb21
        r.Add("buff choice: a mastery whose name has several spell ids", MultiIdMastery);   // was a known failure; fixed by 0aedcee
        r.Add("buff timers: tier-8 weapon auras last 90 minutes", TierEightAuras);   // was a known failure; fixed by 9a317a8
        r.Add("buff chat: another player's speech can't record a buff timer", SpeechIsNotACast);   // was a known failure; fixed by 0e384d2
        r.KnownFailure("buff school: Armor Tinkering Expertise is a Creature Enchantment spell",
            "SkillForBuff matches \"Armor\" first and files it under Life Magic: its tier comes from Life Magic, and a character without Life Magic never casts it (not fixed on any branch)",
            ArmorTinkeringSchool);
    }

    // ── Rig ───────────────────────────────────────────────────────────────────

    private static int[] Ids(string name) => SpellTable.Named(name).Select(r => r.Id).ToArray();
    private static int Id(string name) => Ids(name).FirstOrDefault();
    private static string NameOf(int id) => SpellTable.ById(id)?.Name ?? (id == 0 ? "(none)" : $"#{id}");

    /// <summary>The tier the spell data gives a spell: its numeral, else 7 for power 300, 8 for 350+.</summary>
    private static int DataTier(SpellTable.Row row)
    {
        string last = row.Name.Split(' ').Last();
        int roman = last switch { "I" => 1, "II" => 2, "III" => 3, "IV" => 4, "V" => 5, "VI" => 6, "VII" => 7, "VIII" => 8, _ => 0 };
        if (roman > 0) return roman;
        if (row.Power >= 350 || row.Name.StartsWith("Incantation", StringComparison.Ordinal)) return 8;
        return row.Power == 300 ? 7 : 0;
    }

    /// <summary>Buff thresholds that make a 250 skill (the no-skills stub) cast exactly <paramref name="tier"/>.</summary>
    private static void ForceBuffTier(LegacyUiSettings s, int tier)
    {
        int T(int k) => k <= tier ? 0 : 10000;
        s.BuffMinSkillLevelTier2 = T(2); s.BuffMinSkillLevelTier3 = T(3); s.BuffMinSkillLevelTier4 = T(4);
        s.BuffMinSkillLevelTier5 = T(5); s.BuffMinSkillLevelTier6 = T(6); s.BuffMinSkillLevelTier7 = T(7);
        s.BuffMinSkillLevelTier8 = T(8);
    }

    private static SpellManager NewSpells(RynthCoreHost host, LegacyUiSettings s, CharacterSkills? skills = null)
    {
        var sm = new SpellManager(host, s);
        sm.InitializeNatively();
        if (skills != null) sm.SetCharacterSkills(skills);
        return sm;
    }

    private static CharacterSkills Skills(RynthCoreHost host)
    {
        var cs = new CharacterSkills(host);
        cs.SetPlayerId(Player);
        return cs;
    }

    private static void KnowAll()
    {
        foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
    }

    private static void Know(params string[] names)
    {
        foreach (string n in names) foreach (int id in Ids(n)) FakeHost.Spellbook.Add((uint)id);
    }

    private sealed class BuffRig
    {
        public readonly LegacyUiSettings S = new() { EnableBuffing = true };
        public readonly BuffManager B;
        public readonly SpellManager Sm;

        /// <summary>Call FakeHost.Reset(), set skills and the spellbook first.</summary>
        public BuffRig()
        {
            var host = FakeHost.Create();
            var skills = Skills(host);
            Sm = NewSpells(host, S, skills);
            B = new BuffManager(host, S, Sm, new PlayerVitalsCache());
            B.SetCharacterSkills(skills);
            FakeHost.ServerTime = 100_000;
        }

        /// <summary>Sets the live enchantment registry and reads it (remaining seconds; MaxValue = permanent).</summary>
        public void Live(params (string Name, double Remaining)[] enchantments)
        {
            FakeHost.Enchantments.Clear();
            foreach (var (name, remaining) in enchantments)
                FakeHost.Enchantments.Add(((uint)Id(name), remaining == double.MaxValue ? double.MaxValue : FakeHost.ServerTime + remaining));
            B.RefreshFromLiveMemory();
        }
    }

    private static int BuffTier(int buffed, Action<LegacyUiSettings>? tweak = null, bool combat = false)
    {
        FakeHost.Reset();
        FakeHost.Skills[(Player, STypeCreature)] = (buffed, 2);
        var host = FakeHost.Create();
        var s = new LegacyUiSettings();
        tweak?.Invoke(s);
        var sm = NewSpells(host, s, Skills(host));
        return combat ? sm.GetHighestSpellTier(AcSkillType.CreatureEnchantment) : sm.GetHighestBuffSpellTier(AcSkillType.CreatureEnchantment);
    }

    // ── Tiers ─────────────────────────────────────────────────────────────────

    private static void TierThresholds()
    {
        // Default thresholds: 85 / 135 / 185 / 235 / 285 / 335 / 435 for tiers 2-8.
        var cases = new[] { (10, 1), (84, 1), (85, 2), (134, 2), (135, 3), (184, 3), (185, 4), (234, 4), (235, 5),
                            (284, 5), (285, 6), (334, 6), (335, 7), (434, 7), (435, 8), (600, 8) };
        foreach (var (buffed, tier) in cases)
            Check.Eq(BuffTier(buffed), tier, $"buffed {buffed}");

        Check.Eq(BuffTier(450, s => s.MinSkillLevelTier8 = 500, combat: true), 7, "combat uses its own thresholds (tier 8 at 500)");
        Check.Eq(BuffTier(450, s => s.MinSkillLevelTier8 = 500), 8, "... while buffing keeps its own (435)");
        Check.Eq(BuffTier(300, s => s.BuffMinSkillLevelTier6 = 400), 5, "raising the tier-6 threshold drops a 300 to tier 5");
        // Pinned: the tier-1 threshold is never read; anything below tier 2 is tier 1.
        Check.Eq(BuffTier(10, s => s.BuffMinSkillLevelTier1 = 400), 1, "the tier-1 threshold is not read (pinned)");
    }

    private static void TierUnreadableSkills()
    {
        FakeHost.Reset();
        var host = FakeHost.Create();
        var s = new LegacyUiSettings();
        Check.Eq(NewSpells(host, s).GetHighestBuffSpellTier(AcSkillType.CreatureEnchantment), 5, "no skills object: assumes 250 (tier 5)");
        Check.Eq(NewSpells(host, s, Skills(host)).GetHighestBuffSpellTier(AcSkillType.CreatureEnchantment), 5,
            "the skill read fails: assumes 250 (tier 5), not tier 1");

        FakeHost.Skills[(Player, STypeCreature)] = (0, 2);
        Check.Eq(NewSpells(host, s, Skills(host)).GetHighestBuffSpellTier(AcSkillType.CreatureEnchantment), 5,
            "a trained skill reading 0 is an engine glitch: assumes 250");

        var skills = Skills(host);
        var sm = NewSpells(host, s, skills);
        FakeHost.Skills[(Player, STypeCreature)] = (400, 2);
        Check.Eq(sm.GetHighestBuffSpellTier(AcSkillType.CreatureEnchantment), 7, "a real read of 400: tier 7");
        FakeHost.Skills[(Player, STypeCreature)] = (0, 2);
        Check.Eq(sm.GetHighestBuffSpellTier(AcSkillType.CreatureEnchantment), 7, "then a 0 read keeps the last good one");

        FakeHost.Skills[(Player, STypeCreature)] = (0, 1);
        Check.Eq(NewSpells(host, s, Skills(host)).GetHighestBuffSpellTier(AcSkillType.CreatureEnchantment), 1,
            "an untrained skill reading 0 is real: tier 1");
    }

    private static int Resolve(string buff, int buffedCreature, params string[] known)
    {
        FakeHost.Reset();
        Know(known);
        FakeHost.Skills[(Player, STypeCreature)] = (buffedCreature, 2);
        var host = FakeHost.Create();
        return NewSpells(host, new LegacyUiSettings(), Skills(host)).GetDynamicSelfBuffId(buff, AcSkillType.CreatureEnchantment);
    }

    private static void KnownTierWalk()
    {
        string[] all = { "Strength Self I", "Strength Self II", "Strength Self III", "Strength Self IV", "Strength Self V",
                         "Strength Self VI", "Might of the Lugians", "Incantation of Strength Self" };
        Check.Eq(NameOf(Resolve("Strength Self", 500, all)), "Incantation of Strength Self", "skill 500, all known: tier 8");
        Check.Eq(NameOf(Resolve("Strength Self", 500, all[..7])), "Might of the Lugians", "tier 8 not known: tier 7");
        Check.Eq(NameOf(Resolve("Strength Self", 500, all[..5])), "Strength Self V", "only I-V known: V");
        Check.Eq(NameOf(Resolve("Strength Self", 250, all)), "Strength Self V", "skill 250: tier 5 even with more known");
        Check.Eq(NameOf(Resolve("Strength Self", 500, "Strength Self I", "Strength Self III")), "Strength Self III", "gaps are skipped");
        Check.Eq(Resolve("Strength Self", 500, "Focus Self VI"), 0, "no Strength spell known: 0 (the buff is skipped)");
        Check.Eq(NameOf(Resolve("Strength", 500, all)), "Incantation of Strength Self", "the base name works without ' Self'");
    }

    private static void ColdAndWarm()
    {
        FakeHost.Reset();   // empty spellbook: the snapshot is cold
        FakeHost.Skills[(Player, STypeCreature)] = (500, 2);
        var host = FakeHost.Create();
        var sm = NewSpells(host, new LegacyUiSettings(), Skills(host));
        Check.False(sm.IsKnownSnapshotWarm, "cold");
        Check.Eq(NameOf(sm.GetDynamicSelfBuffId("Strength Self", AcSkillType.CreatureEnchantment)), "Incantation of Strength Self",
            "cold, no player id: every spell in the data counts as known");
        sm.MarkSpellUnresolvable(Id("Incantation of Strength Self"));
        Check.Eq(NameOf(sm.GetDynamicSelfBuffId("Strength Self", AcSkillType.CreatureEnchantment)), "Might of the Lugians",
            "a spell that got no chat back is skipped while cold");
        Check.False(sm.TryResolveKnownSpellId("Strength Self VI", out _), "combat never guesses while cold");

        FakeHost.Reset();
        Know("Incantation of Strength Self", "Strength Self VI");
        FakeHost.Skills[(Player, STypeCreature)] = (500, 2);
        host = FakeHost.Create();
        var warm = NewSpells(host, new LegacyUiSettings(), Skills(host));
        warm.MarkSpellUnresolvable(Id("Incantation of Strength Self"));
        Check.Eq(NameOf(warm.GetDynamicSelfBuffId("Strength Self", AcSkillType.CreatureEnchantment)), "Incantation of Strength Self",
            "warm: the spellbook overrides the blacklist");
        Check.True(warm.IsKnownSnapshotWarm, "warm");
        Check.Eq(warm.UnresolvableSpellIds.Count, 0, "the known spell is purged from the blacklist");
        Check.True(warm.TryResolveKnownSpellId("Strength Self VI", out int vi) && vi == Id("Strength Self VI"), "combat resolves a known spell");
        Check.False(warm.TryResolveKnownSpellId("Strength Self V", out _), "and refuses an unknown one");
    }

    /// <summary>Resolves every buff in the list at tiers 1-8, with the whole spell data known.</summary>
    private static List<(string Buff, int Tier, int Id)> ResolveEverything(out BuffManager bm)
    {
        FakeHost.Reset();
        KnowAll();
        var host = FakeHost.Create();
        var byTier = new SpellManager[9];
        for (int t = 1; t <= 8; t++)
        {
            var s = new LegacyUiSettings();
            ForceBuffTier(s, t);
            byTier[t] = NewSpells(host, s);
        }
        bm = new BuffManager(host, new LegacyUiSettings(), byTier[8], new PlayerVitalsCache());
        var result = new List<(string, int, int)>();
        foreach (string buff in bm.BuildDynamicBuffList())
        {
            var school = BuffManager.SkillForBuff(buff);
            for (int t = 1; t <= 8; t++)
                result.Add((buff, t, byTier[t].GetDynamicSelfBuffId(buff, school)));
        }
        return result;
    }

    private static void EveryBuffEveryTier()
    {
        var all = ResolveEverything(out var bm);
        Check.True(bm.BuildDynamicBuffList().Count >= 60, $"the full buff list ({bm.BuildDynamicBuffList().Count} buffs)");
        foreach (var (buff, tier, id) in all)
        {
            var row = SpellTable.ById(id);
            Check.True(row != null && DataTier(row) == tier, $"{buff} at tier {tier}: got {NameOf(id)}");
        }
    }

    private static void NameGapsResolve()
    {
        var all = ResolveEverything(out _);
        foreach (var (buff, tier, id) in all)
        {
            if (!NameGaps.Contains((buff, tier))) continue;
            var row = SpellTable.ById(id);
            Check.True(row != null && DataTier(row) == tier, $"{buff} at tier {tier}: got {NameOf(id)}");
        }
    }

    private static void LoreAndIncantations()
    {
        var all = ResolveEverything(out _);
        string At(string buff, int tier) => NameOf(all.First(x => x.Buff == buff && x.Tier == tier).Id);

        Check.Eq(At("Strength Self", 7), "Might of the Lugians", "Strength tier 7");
        Check.Eq(At("Endurance Self", 7), "Perseverance", "Endurance tier 7 (the misspelled 'Preservance' is tried first and isn't in the data)");
        Check.Eq(At("Invulnerability Self", 7), "Aura of Defense", "Invulnerability tier 7");
        Check.Eq(At("Armor Self", 7), "Executor's Blessing", "Armor tier 7");
        Check.Eq(At("Blood Drinker Self", 6), "Aura of Blood Drinker Self VI", "weapon aura tier 6");
        Check.Eq(At("Blood Drinker Self", 7), "Aura of Infected Caress", "weapon aura tier 7");
        Check.Eq(At("Blood Drinker Self", 8), "Aura of Incantation of Blood Drinker Self", "weapon aura tier 8");
        Check.Eq(At("Spirit Drinker Self", 7), "Aura of Infected Spirit Caress", "Spirit Drinker tier 7");
        Check.Eq(At("Impenetrability", 6), "Impenetrability VI", "armor spell tier 6 (no 'Self')");
        Check.Eq(At("Impenetrability", 7), "Brogard's Defiance", "armor spell tier 7");
        Check.Eq(At("Impenetrability", 8), "Incantation of Impenetrability", "armor spell tier 8");
        Check.Eq(At("Blade Bane", 7), "Swordsman's Bane", "Blade Bane tier 7");
        Check.Eq(At("Heavy Weapon Mastery", 7), "Heavy Weapon Mastery Self VII", "masteries with a real VII use it");
        Check.Eq(At("Arcanum Salvaging", 7), "Arcanum Salvaging VII", "'X VII' without Self");
    }

    private static int Level(string name) => BuffManager.GetSpellLevel(new SpellInfo(1, name));

    private static void SpellLevels()
    {
        Check.Eq(Level("Strength Self I"), 1, "I");
        Check.Eq(Level("Strength Self II"), 2, "II");
        Check.Eq(Level("Strength Self III"), 3, "III");
        Check.Eq(Level("Strength Self IV"), 4, "IV");
        Check.Eq(Level("Strength Self V"), 5, "V");
        Check.Eq(Level("Strength Self VI"), 6, "VI");
        Check.Eq(Level("Heavy Weapon Mastery Self VII"), 7, "VII");
        Check.Eq(Level("Strength Self VIII"), 8, "VIII (not read as VII)");
        Check.Eq(Level("Incantation of Strength Self"), 8, "Incantation");
        Check.Eq(Level("Incantation of Impenetrability"), 8, "Incantation, item spell");
        foreach (string lore in new[] { "Might of the Lugians", "Brogard's Defiance", "Tusker's Bane", "Aura of Infected Caress",
                                        "Adja's Blessing", "Robustify", "Unflinching Persistence", "Aura of Cragstone's Will" })
            Check.Eq(Level(lore), 7, lore);
        Check.Eq(Level("Frobnicate"), 1, "an unknown name reads as 1");
    }

    private static void DurationsMatchData()
    {
        var all = ResolveEverything(out var bm);
        foreach (var (buff, tier, id) in all)
        {
            var row = SpellTable.ById(id);
            if (row == null) continue;
            double assumed = bm.GetCustomSpellDuration(BuffManager.GetSpellLevel(new SpellInfo(id, row.Name)));
            Check.Near(assumed, row.Duration, 0.5, $"{row.Name}: timer length");
        }
    }

    private static void TierEightAuras()
    {
        foreach (string aura in Auras)
        {
            string name = "Aura of Incantation of " + aura;
            Check.Eq(Level(name), 8, $"{name} is level 8");
        }

        // A chat-confirmed tier-8 aura, with the rebuff threshold at 62 minutes: a 90-minute
        // timer leaves it on; a 60-minute one asks for a recast at once.
        FakeHost.Reset();
        Know("Aura of Incantation of Blood Drinker Self");
        FakeHost.Skills[(Player, STypeItem)] = (500, 2);
        var rig = new BuffRig();
        rig.S.RebuffSecondsRemaining = 3720;
        rig.B.OnChatWindowText("You cast Aura of Incantation of Blood Drinker Self on Rune Sword, refreshing Aura of Infected Caress", 7);
        Check.False(rig.B.NeedsAnyBuff(), "just cast: 90 minutes left, not under 62");
    }

    private static void ArchmageEndurance()
    {
        foreach (var (augs, factor) in new[] { (0, 1.0), (1, 1.2), (2, 1.4), (5, 2.0), (7, 2.0), (-3, 1.0) })
        {
            FakeHost.Reset();
            FakeHost.Ints[(Player, 238u)] = augs;   // AugmentationIncreasedSpellDuration
            var host = FakeHost.Create(playerId: Player);
            var s = new LegacyUiSettings();
            var bm = new BuffManager(host, s, NewSpells(host, s), new PlayerVitalsCache());
            Check.Near(bm.GetCustomSpellDuration(6), 2700 * factor, 1e-6, $"{augs} rank(s): tier 6 lasts {2700 * factor} s");
            Check.Near(bm.GetCustomSpellDuration(8), 5400 * factor, 1e-6, $"{augs} rank(s): tier 8 lasts {5400 * factor} s");
        }
        FakeHost.Reset();
        var noId = FakeHost.Create();
        var s0 = new LegacyUiSettings();
        var unread = new BuffManager(noId, s0, NewSpells(noId, s0), new PlayerVitalsCache());
        Check.Near(unread.GetCustomSpellDuration(1), 1800, 1e-6, "ranks unreadable: no bonus");
        Check.Near(unread.GetCustomSpellDuration(7), 3600, 1e-6, "tier 7: an hour");
    }

    private static void SkillForBuff()
    {
        var creature = new[] { "Strength Self", "Focus Self", "Invulnerability Self", "Impregnability Self", "Magic Resistance Self",
                               "Heavy Weapon Mastery", "Missile Weapon Mastery", "Life Magic Mastery", "War Magic Mastery",
                               "Sprint", "Fealty", "Arcanum Salvaging", "Item Tinkering Expertise" };
        var life = new[] { "Regeneration Self", "Rejuvenation Self", "Mana Renewal Self", "Armor Self", "Acid Protection Self",
                           "Blade Protection Self", "Bludgeoning Protection Self", "Heal Self", "Revitalize Self" };
        var item = new[] { "Blood Drinker Self", "Hermetic Link Self", "Heart Seeker Self", "Spirit Drinker Self",
                           "Swift Killer Self", "Defender Self", "Impenetrability", "Acid Bane", "Bludgeoning Bane", "Piercing Bane" };
        foreach (string b in creature) Check.Eq(BuffManager.SkillForBuff(b), AcSkillType.CreatureEnchantment, b);
        foreach (string b in life) Check.Eq(BuffManager.SkillForBuff(b), AcSkillType.LifeMagic, b);
        foreach (string b in item) Check.Eq(BuffManager.SkillForBuff(b), AcSkillType.ItemEnchantment, b);
    }

    private static void ArmorTinkeringSchool()
    {
        Check.Eq(BuffManager.SkillForBuff("Armor Tinkering Expertise"), AcSkillType.CreatureEnchantment, "Armor Tinkering Expertise");
    }

    private static void ItemEnchantments()
    {
        foreach (string n in new[] { "Impenetrability VI", "Brogard's Defiance", "Incantation of Acid Bane", "Tusker's Bane",
                                     "Aura of Blood Drinker Self VI", "Aura of Infected Caress", "Aura of Cragstone's Will",
                                     "Aura of Incantation of Defender Self", "Swordman's Bane" })
            Check.True(BuffManager.IsItemEnchantment(n), $"{n}: on an item");
        foreach (string n in new[] { "Strength Self VI", "Impregnability Self VI", "Aura of Defense", "Aura of Deflection",
                                     "Armor Self VI", "Blade Protection Self VI", "Heal Self VI", "Might of the Lugians" })
            Check.False(BuffManager.IsItemEnchantment(n), $"{n}: on the player");
    }

    // ── Rebuff timing ─────────────────────────────────────────────────────────

    private static BuffRig StrengthRig(int buffed, params string[] known)
    {
        FakeHost.Reset();
        Know(known);
        FakeHost.Skills[(Player, STypeCreature)] = (buffed, 2);
        return new BuffRig();
    }

    private static void RebuffThreshold()
    {
        var rig = StrengthRig(300, "Strength Self VI");   // 300 buffed: tier 6
        rig.Live();
        Check.True(rig.B.NeedsAnyBuff(), "not on: needs a buff");
        rig.Live(("Strength Self VI", 1000));
        Check.False(rig.B.NeedsAnyBuff(), "1000 s left, threshold 300: fine");
        rig.Live(("Strength Self VI", 310));
        Check.False(rig.B.NeedsAnyBuff(), "310 s left: fine");
        rig.Live(("Strength Self VI", 290));
        Check.True(rig.B.NeedsAnyBuff(), "290 s left: recast");
        rig.S.RebuffSecondsRemaining = 1200;
        rig.Live(("Strength Self VI", 1000));
        Check.True(rig.B.NeedsAnyBuff(), "threshold raised to 1200: 1000 s left is under it");
        rig.S.EnableBuffing = false;
        Check.False(rig.B.NeedsAnyBuff(), "buffing off: never");
        rig.S.EnableBuffing = true;
        rig.S.RebuffSecondsRemaining = 300;
        rig.B.ForceFullRebuff();
        Check.True(rig.B.NeedsAnyBuff(), "Force Rebuff: everything is due");
    }

    private static void RebuffPermanentAndDuplicates()
    {
        var rig = StrengthRig(300, "Strength Self VI");
        rig.Live(("Strength Self VI", double.MaxValue));
        Check.False(rig.B.NeedsAnyBuff(), "a permanent enchantment counts as on");
        rig.Live(("Strength Self VI", -5));
        Check.True(rig.B.NeedsAnyBuff(), "an expired entry is dropped: recast");

        var t7 = StrengthRig(340, "Strength Self VI", "Might of the Lugians");   // 340 buffed: tier 7
        t7.Live(("Strength Self VI", double.MaxValue), ("Might of the Lugians", 3000));
        Check.False(t7.B.NeedsAnyBuff(), "a permanent VI and a timed VII: the VII owns the family");
        t7.Live(("Might of the Lugians", 3000), ("Strength Self VI", double.MaxValue));
        Check.False(t7.B.NeedsAnyBuff(), "same in the other order");

        var item = StrengthRig(340, "Strength Self VI", "Might of the Lugians");
        item.Live(("Strength Self VI", double.MaxValue));
        Check.True(item.B.NeedsAnyBuff(), "only a permanent (item-granted) VI while VII is castable: cast the VII");
    }

    private static void RebuffTierUpgrade()
    {
        var rig = StrengthRig(300, "Strength Self V", "Strength Self VI");
        rig.Live(("Strength Self V", 3000));
        // Pinned: the landed tier caps the target, so a V from before a skill gain is kept
        // until it runs low (this is the guard against Incantations that land a tier low).
        Check.False(rig.B.NeedsAnyBuff(), "only V has ever landed: V with 3000 s left is kept (pinned)");
        rig.Live(("Strength Self VI", 3000));
        rig.Live(("Strength Self V", 3000));
        Check.True(rig.B.NeedsAnyBuff(), "VI has landed before: a V on now is upgraded");

        var inc = StrengthRig(500, "Incantation of Strength Self");
        inc.Live(("Strength Self VI", 3000));
        Check.False(inc.B.NeedsAnyBuff(), "an Incantation that lands as VI isn't recast for ever");
    }

    private static BuffRig ArmorRig()
    {
        FakeHost.Reset();
        Know("Impenetrability VI");
        FakeHost.Skills[(Player, STypeItem)] = (300, 2);   // tier 6
        return new BuffRig();
    }

    private static void ItemSpellFromChat()
    {
        var rig = ArmorRig();
        Check.True(rig.B.NeedsAnyBuff(), "no armor spell yet");
        rig.B.OnChatWindowText("You cast Impenetrability VI on Rune Robe, refreshing Impenetrability V", 7);
        Check.False(rig.B.NeedsAnyBuff(), "confirmed by chat: on for 45 minutes");

        var stripped = ArmorRig();
        stripped.B.OnChatWindowText("ou cast Impenetrability VI on Rune Robe", 7);   // AC's glyph layer eats the Y
        Check.False(stripped.B.NeedsAnyBuff(), "the 'ou cast' form counts too");

        var fizzle = ArmorRig();
        fizzle.B.OnChatWindowText("Your spell fizzled.", 7);
        Check.True(fizzle.B.NeedsAnyBuff(), "a fizzle records nothing");

        var dead = ArmorRig();
        dead.B.OnChatWindowText("You cast Impenetrability VI on Rune Robe", 7);
        dead.Live(("Strength Self I", 600));   // some player enchantment ...
        dead.Live();                           // ... then the registry drops to zero: a death
        Check.True(dead.B.NeedsAnyBuff(), "after a death wipes the registry, item spells are recast too");
    }

    private static void SpeechIsNotACast()
    {
        var rig = ArmorRig();
        rig.B.OnChatWindowText("Vessa says, \"you cast Impenetrability VI on your robe?\"", 2);
        Check.True(rig.B.NeedsAnyBuff(), "someone else's speech is not our cast");
    }

    private static void MultiIdMastery()
    {
        FakeHost.Reset();
        foreach (int id in new[] { 491, 492, 493, 494, 495, 496 }) FakeHost.Spellbook.Add((uint)id);   // the second line
        FakeHost.Skills[(Player, STypeCreature)] = (300, 2);
        var host = FakeHost.Create();
        var sm = NewSpells(host, new LegacyUiSettings(), Skills(host));
        Check.Eq(SpellTable.ById(496)?.Name, "Missile Weapon Mastery Self VI", "496 is a Missile Weapon Mastery Self VI");
        Check.Eq(sm.GetDynamicSelfBuffId("Missile Weapon Mastery", AcSkillType.CreatureEnchantment), 496, "resolves to the id the character knows");
    }
}
