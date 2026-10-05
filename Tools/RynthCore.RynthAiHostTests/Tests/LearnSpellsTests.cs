using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Learn unknown spells (VTank's ReadUnknownScrolls, 2026-10-04): loot scrolls of spells the
// character doesn't know and can learn (ACE's CanReadScroll rule, school trained), read them at
// a safe moment, remember refused reads; a loot profile's Read action reads what it loots.
internal static class LearnSpellsTests
{
    private const uint Player = 0x50000E01;
    private const uint Corpse = 0x80271100;
    private const uint Scroll = 0x80271101;
    private const uint Scroll2 = 0x80271102;
    private const uint PackScroll = 0x80271201;
    private const uint Gem = 0x80271301;
    private const uint ItemTypeWritable = 0x2000, ItemTypeContainer = 0x200, ItemTypeGem = 0x800;
    private const uint SkWar = 34, SkLife = 33;
    private const uint LongDescKey = 16;
    private const string Me = "Mira Quill";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    // Real ids from the plugin's spell table; school and power as the dat has them.
    private const int FrostBoltVI = 74;      // War, power 250: needs War 200
    private const int FlameBoltIII = 82;     // War, power 100: needs War 50
    private const int HealSelfVI = 1161;     // Life, power 250
    private const int FrostBoltI = 28;       // War, power 1: anyone (school trained)
    private const int IncFrostBolt = 4447;   // War, power 400: anyone (school trained)

    public static void Register(Runner r)
    {
        r.Add("learn: ACE's rule - level I and VII/VIII need only the school trained, II-VI also skill >= power - 50", AceRule);
        r.Add("learn: the scroll's spell comes from its Spell data id; school and power from the dat's SpellTable", SpellFacts);
        r.Add("learn: unknown spell, school trained -> looted whatever the profile says, read, learned", LootAndRead);
        r.Add("learn: a known spell is left on the corpse", KnownIsSkipped);
        r.Add("learn: an untrained school is left on the corpse", UntrainedIsSkipped);
        r.Add("learn: too difficult by ACE's rule is left on the corpse", TooDifficultIsSkipped);
        r.Add("learn: a refused read is remembered - not read again, another copy not looted", FailedReadRemembered);
        r.Add("learn: a loot profile's Read action loots and reads (setting off); a Read on a non-scroll is left", ProfileReadAction);
        r.Add("learn: a VTank profile's 'ObjectClass == Scroll -> Read' matches scrolls (filed as Book) and reads them", VTankScrollReadRule);
        r.Add("learn: with the setting off nothing is looted or read", SettingOffDoesNothing);
        r.Add("learn: a scroll already in the pack is read, only at a safe moment", PackScrollAtSafeMoment);
        r.Add("learn: a read with no answer is retried once, then given up", ReadTimeoutBounded);
        r.Add("learn: settings - on by default, old files keep it on, /vt opt and metas reach ReadUnknownScrolls", Settings);
    }

    // ── World ─────────────────────────────────────────────────────────────────

    private static (uint Cell, float X, float Y, float Z) Me0 => (0x1048002Au, 100f, 100f, 0f);

    private static void BaseWorld()
    {
        FakeHost.Reset();
        FakeHost.SalvageCalls = true;   // UseObject + MoveItemExternal
        FakeHost.WeaponCalls = true;    // RequestId, HasAppraisalData
        FakeHost.Names[Player] = Me;
        FakeHost.Positions[Player] = Me0;
        FakeHost.PlayerPose = Me0;
        FakeHost.Names[Corpse] = "Corpse of Drudge Slinker";
        FakeHost.ItemTypes[Corpse] = ItemTypeContainer;
        FakeHost.Strings[(Corpse, LongDescKey)] = $"Killed by {Me}.";
        FakeHost.NotAttackable.Add(Corpse);
        FakeHost.Positions[Corpse] = (Me0.Cell, Me0.X + 1.0f, Me0.Y, Me0.Z);

        // A war mage: War Magic trained at 260, Life untrained. A spellbook (not empty = warm).
        FakeHost.Skills[(Player, SkWar)] = (260, 2);
        FakeHost.Skills[(Player, SkLife)] = (10, 1);
        FakeHost.Spellbook.AddRange(new uint[] { 1, 2, 27 });

        ComponentDatabase.SpellMetaOverride.Clear();
        ComponentDatabase.SpellMetaOverride[FrostBoltVI] = (1, 250);
        ComponentDatabase.SpellMetaOverride[FlameBoltIII] = (1, 100);
        ComponentDatabase.SpellMetaOverride[HealSelfVI] = (2, 250);
        ComponentDatabase.SpellMetaOverride[FrostBoltI] = (1, 1);
        ComponentDatabase.SpellMetaOverride[IncFrostBolt] = (1, 400);
        UseAudit.Reset("RynthAi", () => true);
    }

    private static void AddScroll(uint id, int spellId, string name, uint container)
    {
        FakeHost.Names[id] = name;
        FakeHost.ItemTypes[id] = ItemTypeWritable;
        FakeHost.DataIds[(id, ScrollLearner.STypeDidSpell)] = (uint)spellId;
        FakeHost.Containers[id] = container;
        FakeHost.Appraised.Add(id);
    }

    private static void Done()
    {
        ComponentDatabase.SpellMetaOverride.Clear();
        UseAudit.Reset("?", null);
    }

    private sealed class Clock { public long Now = 1_000_000; }

    private static ScrollLearner Learner(RynthCoreHost host, LegacyUiSettings s, WorldObjectCache cache, Clock clock) =>
        new(host, s, cache)
        {
            PlayerId = () => Player,
            KnownSpell = id => FakeHost.Spellbook.Contains((uint)id),
            NowMs = () => clock.Now,
            Chat = m => host.WriteToChat(m, 1),
        };

    private static LegacyUiSettings Settings0(bool readUnknown = true) => new()
    {
        IsMacroRunning = true,
        EnableLooting = true,
        EnableCombat = false,
        LootOwnership = 0,
        LootContentSettleMs = 0,
        LootEmptyCorpseMs = 0,
        StopLootingWhenPackFull = false,
        ReadUnknownScrolls = readUnknown,
    };

    // The plugin without Initialize (as LootCorpseTests), with our learner plugged in.
    private sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S;
        public readonly WorldObjectCache Cache;
        public readonly ScrollLearner L;
        public readonly Clock Clock = new();

        public Bot(RynthCoreHost host, LegacyUiSettings s, LootProfile profile)
            : this(host, s, (string?)null)
        {
            string path = Path.Combine(Program.TempRoot, $"learn-{Guid.NewGuid():N}.json");
            profile.Save(path);
            S.CurrentLootPath = path;
        }

        /// <summary>With a VTank .utl profile.</summary>
        public Bot(RynthCoreHost host, LegacyUiSettings s, RynthCore.Loot.VTank.VTankLootProfile utl)
            : this(host, s, (string?)null)
        {
            string path = Path.Combine(Program.TempRoot, $"learn-{Guid.NewGuid():N}.utl");
            RynthCore.Loot.VTank.VTankLootWriter.Save(utl, path);
            S.CurrentLootPath = path;
        }

        private Bot(RynthCoreHost host, LegacyUiSettings s, string? _)
        {
            S = s;
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            Set("_dashboard", dash);
            Cache = new WorldObjectCache(host);
            Cache.SetPlayerId(Player);
            Set("_objectCache", Cache);
            Set("_playerId", Player);
            L = Learner(host, S, Cache, Clock);
            Set("_scrollLearner", L);
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public object? Call(string m, params object?[] a) => typeof(RynthAiPlugin).GetMethod(m, Any)!.Invoke(P, a);

        /// <summary>Claims and opens the corpse, then loots until <paramref name="until"/> or 100 ticks.</summary>
        public void LootCorpse(Func<bool> until)
        {
            Cache.OnCreateObject(Corpse);
            Cache.Tick();
            Set("_activity", BotActivity.Looting);
            Call("TickCorpseOpening");
            foreach (uint id in FakeHost.Containers.Where(kv => kv.Value == Corpse).Select(kv => kv.Key).ToList())
                Cache.OnCreateObject(id);
            Cache.Tick();
            P.OnViewObjectContents(Corpse);
            for (int i = 0; i < 100 && !until(); i++)
            {
                Call("TickCorpseOpening");
                Thread.Sleep(5);
            }
        }

        public (bool Kept, string Action, string Rule) Classify(uint id)
        {
            object?[] args = { Cache[unchecked((int)id)]!, S, null, null, null };
            bool kept = (bool)typeof(RynthAiPlugin).GetMethod("ClassifyItemAgainstProfile", Any)!.Invoke(P, args)!;
            return (kept, (string)args[2]!, (string)args[4]!);
        }
    }

    private static LootProfile GemsOnly()
    {
        var p = new LootProfile { Name = "gems" };
        var rule = new LootRule { Name = "Gems", Action = LootAction.Keep };
        rule.Conditions.Add(new ObjectClassCondition { ObjectClass = AcObjectClass.Gem });
        p.Rules.Add(rule);
        return p;
    }

    private static bool Moved(uint id) => FakeHost.ExternalMoves.Any(m => m.Id == id);
    private static bool Logged(string part) => FakeHost.Logs.Any(l => l.Contains(part));
    private static string LearnLogs() => string.Join(" | ", FakeHost.Logs.Where(l => l.Contains("[Learn]")));

    /// <summary>The server moves the item into the main pack.</summary>
    private static void IntoPack(uint id) => FakeHost.Containers[id] = Player;

    /// <summary>Ticks the learner (safe moment) until it starts a read or 20 ticks pass.</summary>
    private static void TickUntilRead(ScrollLearner l, Clock c, bool safe = true, int busy = 0)
    {
        for (int i = 0; i < 20 && !l.IsReading; i++)
        {
            c.Now += 1_000;
            l.Tick(safe, busy);
        }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    private static void AceRule()
    {
        // ACE Player.CanReadScroll: power < 50 || power >= 300 -> true; else trained && Current >= power - 50.
        Check.True(ScrollLearner.AceCanRead(2, 0, 1), "level I (power 1), trained: any skill");
        Check.True(ScrollLearner.AceCanRead(2, 5, 300), "level VII (power 300), trained: any skill");
        Check.True(ScrollLearner.AceCanRead(3, 5, 400), "level VIII (power 400), specialized: any skill");
        Check.True(ScrollLearner.AceCanRead(2, 0, 50), "level II (power 50): needs 0");
        Check.False(ScrollLearner.AceCanRead(2, 49, 100), "level III (power 100): 49 < 50");
        Check.True(ScrollLearner.AceCanRead(2, 50, 100), "level III: 50 is enough");
        Check.False(ScrollLearner.AceCanRead(2, 199, 250), "level VI (power 250): 199 < 200");
        Check.True(ScrollLearner.AceCanRead(2, 200, 250), "level VI: 200 is enough");
        Check.False(ScrollLearner.AceCanRead(1, 500, 1), "untrained: never (RynthAi wants the school trained)");
        Check.False(ScrollLearner.AceCanRead(1, 500, 400), "untrained level VIII: never");
        Check.Eq(ScrollLearner.AceSkillMargin, 50, "ACE magicSkillCheckMargin");
    }

    private static void SpellFacts()
    {
        BaseWorld();
        AddScroll(Scroll, FrostBoltVI, "Scroll of Frost Bolt VI", Player);
        FakeHost.Names[Scroll2] = "Scroll of Flame Bolt III";   // no data id: falls back to the name
        FakeHost.ItemTypes[Scroll2] = ItemTypeWritable;
        FakeHost.Containers[Scroll2] = Player;
        var host = FakeHost.Create(Player);
        var cache = FakeHost.MakeCache(host, Player, new[] { Scroll, Scroll2 });
        var l = Learner(host, Settings0(), cache, new Clock());

        var wo = cache[unchecked((int)Scroll)]!;
        Check.True(ScrollLearner.LooksLikeScroll(wo), $"a writable is a scroll candidate (class {wo.ObjectClass})");
        Check.Eq(l.ReadSpellId(wo), FrostBoltVI, "spell from data id 28");
        Check.Eq(l.ReadSpellId(cache[unchecked((int)Scroll2)]!), FlameBoltIII, "no data id: 'Scroll of <name>' with one id");
        var j = l.Judge(wo);
        Check.Eq(j.Verdict, ScrollVerdict.Learnable, "unknown, War trained 260 >= 200: " + j.Why);
        Check.Eq(j.SkillName, "War Magic", "the school's skill");
        Check.Eq(ScrollLearner.SchoolSkill(5).SType, 43u, "Void Magic skill id");
        Done();

        // The real dat, when this PC has one: the parse reads School and Power at the right offsets.
        ComponentDatabase.SpellMetaOverride.Clear();
        if (ComponentDatabase.TryGetSpellSchoolAndPower(FrostBoltVI, out int school, out int power))
        {
            Check.Eq((school, power), (1, 250), "dat: Frost Bolt VI is War, power 250");
            Check.True(ComponentDatabase.TryGetSpellSchoolAndPower(HealSelfVI, out int s2, out int p2) && s2 == 2 && p2 == 250, $"dat: Heal Self VI is Life, 250 ({s2}, {p2})");
            Check.True(ComponentDatabase.TryGetSpellSchoolAndPower(IncFrostBolt, out int s3, out int p3) && s3 == 1 && p3 == 400, $"dat: Incantation of Frost Bolt is War, 400 ({s3}, {p3})");
        }
        else
            Console.WriteLine("      (no portal.dat on this PC: dat offsets not checked)");
    }

    private static void LootAndRead()
    {
        BaseWorld();
        AddScroll(Scroll, FrostBoltVI, "Scroll of Frost Bolt VI", Corpse);
        var host = FakeHost.Create(Player);
        var bot = new Bot(host, Settings0(), GemsOnly());

        bot.LootCorpse(() => Moved(Scroll));
        Check.True(Moved(Scroll), "the scroll is looted though the profile only keeps gems: " + LearnLogs());
        Check.True(Logged("[Learn] looting 'Scroll of Frost Bolt VI' (Frost Bolt VI: unknown, War Magic trained)"), "one decision line: " + LearnLogs());
        Check.True(FakeHost.Chat.Any(c => c.Contains("[Read] Learn unknown spell")), "the loot rule line says Read / Learn unknown spell");
        Check.True(bot.L.IsPending(unchecked((int)Scroll)), "noted to read");
        Check.False(FakeHost.Uses.Contains(Scroll), "not used on the corpse");

        IntoPack(Scroll);
        TickUntilRead(bot.L, bot.Clock);
        Check.True(FakeHost.Uses.Contains(Scroll), "read once in the pack (UseObject on the scroll)");
        Check.True(Logged("[Use] RynthAi/Learn") && Logged("read the scroll to learn Frost Bolt VI"), "the use is logged with a reason");

        bot.L.OnChat("The scroll is destroyed.");
        bot.Clock.Now += 500;
        bot.L.Tick(true, 0);
        Check.False(bot.L.IsReading, "the read is over");
        Check.True(Logged("[Learn] read 'Scroll of Frost Bolt VI' - learned Frost Bolt VI"), "learned line: " + LearnLogs());
        Check.True(FakeHost.Chat.Any(c => c == "[RynthAi] Learned Frost Bolt VI from a scroll."), "a plain chat line");
        Check.False(bot.L.IsPending(unchecked((int)Scroll)), "no longer pending");
        Done();
    }

    private static void KnownIsSkipped()
    {
        BaseWorld();
        FakeHost.Spellbook.Add(FrostBoltVI);
        AddScroll(Scroll, FrostBoltVI, "Scroll of Frost Bolt VI", Corpse);
        var host = FakeHost.Create(Player);
        var bot = new Bot(host, Settings0(), GemsOnly());

        bot.LootCorpse(() => false);
        Check.False(Moved(Scroll), "a known spell's scroll stays on the corpse");
        Check.True(Logged("[Learn] leaving 'Scroll of Frost Bolt VI' (Frost Bolt VI: already known)"), "and says why: " + LearnLogs());
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("[Learn] leaving 'Scroll of Frost Bolt VI'")), 1, "once, not per tick");
        Done();
    }

    private static void UntrainedIsSkipped()
    {
        BaseWorld();
        AddScroll(Scroll, HealSelfVI, "Scroll of Heal Self VI", Corpse);
        AddScroll(Scroll2, FrostBoltI, "Scroll of Frost Bolt I", Player);
        FakeHost.Skills[(Player, SkWar)] = (0, 1);   // and War untrained too, for the level I case
        var host = FakeHost.Create(Player);
        var cache = FakeHost.MakeCache(host, Player, new[] { Scroll, Scroll2 });
        var l = Learner(host, Settings0(), cache, new Clock());

        Check.False(l.ShouldLootFromCorpse(cache[unchecked((int)Scroll)]!, out _), "Life Magic untrained: left");
        Check.True(Logged("[Learn] leaving 'Scroll of Heal Self VI' (Heal Self VI: Life Magic untrained)"), "logged: " + LearnLogs());
        Check.False(l.ShouldLootFromCorpse(cache[unchecked((int)Scroll2)]!, out _), "level I with War untrained: left (ACE would allow it; no use untrained)");
        Done();
    }

    private static void TooDifficultIsSkipped()
    {
        BaseWorld();
        FakeHost.Skills[(Player, SkWar)] = (150, 2);
        AddScroll(Scroll, FrostBoltVI, "Scroll of Frost Bolt VI", Corpse);
        AddScroll(Scroll2, IncFrostBolt, "Scroll of Incantation of Frost Bolt", Corpse);
        var host = FakeHost.Create(Player);
        var cache = FakeHost.MakeCache(host, Player, new[] { Scroll, Scroll2 });
        var l = Learner(host, Settings0(), cache, new Clock());

        Check.False(l.ShouldLootFromCorpse(cache[unchecked((int)Scroll)]!, out _), "War 150 < 200 for power 250: left");
        Check.True(Logged("(Frost Bolt VI: War Magic 150, needs 200)"), "logged with the numbers: " + LearnLogs());
        Check.True(l.ShouldLootFromCorpse(cache[unchecked((int)Scroll2)]!, out string rule), "power 400: ACE lets any trained caster read it");
        Check.Eq(rule, ScrollLearner.RuleLabel, "the built-in rule's label");
        Done();
    }

    private static void FailedReadRemembered()
    {
        BaseWorld();
        AddScroll(PackScroll, FrostBoltVI, "Scroll of Frost Bolt VI", Player);
        AddScroll(Scroll, FrostBoltVI, "Scroll of Frost Bolt VI", Corpse);
        var host = FakeHost.Create(Player);
        var cache = FakeHost.MakeCache(host, Player, new[] { PackScroll, Scroll });
        var clock = new Clock();
        var l = Learner(host, Settings0(), cache, clock);

        TickUntilRead(l, clock);
        Check.True(FakeHost.Uses.Contains(PackScroll), "the pack scroll is read");
        // The server says no (say our skill read was stale).
        l.OnChat("You are not skilled enough in War Magic to learn this spell.");
        clock.Now += 500;
        l.Tick(true, 0);
        Check.False(l.IsReading, "read over");
        Check.True(l.HasFailed(FrostBoltVI), "the spell is remembered as failed");
        Check.True(Logged("[Learn] read 'Scroll of Frost Bolt VI' failed (the server said: You are not skilled enough in War Magic to learn this spell.)"), "logged: " + LearnLogs());

        int uses = FakeHost.Uses.Count;
        for (int i = 0; i < 10; i++) { clock.Now += 6_000; l.Tick(true, 0); }
        Check.Eq(FakeHost.Uses.Count, uses, "not read again this session");
        Check.False(l.ShouldLootFromCorpse(cache[unchecked((int)Scroll)]!, out _), "another copy on a corpse is not looted");
        Check.True(Logged("a read failed earlier this session"), "and says why");
        Done();
    }

    private static void ProfileReadAction()
    {
        BaseWorld();
        AddScroll(Scroll, FrostBoltVI, "Scroll of Frost Bolt VI", Corpse);
        FakeHost.Names[Gem] = "Black Opal";
        FakeHost.ItemTypes[Gem] = ItemTypeGem;
        FakeHost.Containers[Gem] = Corpse;
        FakeHost.Appraised.Add(Gem);
        var host = FakeHost.Create(Player);
        var p = new LootProfile { Name = "read" };
        var books = new LootRule { Name = "Read books", Action = LootAction.Read };
        books.Conditions.Add(new ObjectClassCondition { ObjectClass = AcObjectClass.Book });
        var gems = new LootRule { Name = "Read gems", Action = LootAction.Read };
        gems.Conditions.Add(new ObjectClassCondition { ObjectClass = AcObjectClass.Gem });
        p.Rules.Add(books);
        p.Rules.Add(gems);
        var bot = new Bot(host, Settings0(readUnknown: false), p);

        bot.Cache.OnCreateObject(Gem);
        bot.Cache.Tick();
        var g = bot.Classify(Gem);
        Check.False(g.Kept, "a Read rule on a gem: left (VTank ignores Read on a non-scroll)");
        Check.True(Logged("[Learn] loot rule 'Read gems' says Read, but 'Black Opal' isn't a scroll - leaving it"), "logged: " + LearnLogs());

        bot.LootCorpse(() => Moved(Scroll));
        Check.True(Moved(Scroll), "the profile's Read rule loots the scroll (setting off)");
        Check.True(bot.L.IsPending(unchecked((int)Scroll)), "noted to read");
        IntoPack(Scroll);
        TickUntilRead(bot.L, bot.Clock);
        Check.True(FakeHost.Uses.Contains(Scroll), "and reads it: " + LearnLogs());
        Done();
    }

    private static void VTankScrollReadRule()
    {
        BaseWorld();
        AddScroll(Scroll, FrostBoltVI, "Frost Bolt VI", Corpse);   // no "Scroll" in the name: the data id says so
        FakeHost.Names[Gem] = "Black Opal";
        FakeHost.ItemTypes[Gem] = ItemTypeGem;
        FakeHost.Containers[Gem] = Corpse;
        FakeHost.Names[Scroll2] = "Letter from Home";               // a writable without a spell: a Book
        FakeHost.ItemTypes[Scroll2] = ItemTypeWritable;
        FakeHost.Containers[Scroll2] = Corpse;
        var host = FakeHost.Create(Player);
        var utl = new RynthCore.Loot.VTank.VTankLootProfile();
        var rule = new RynthCore.Loot.VTank.VTankLootRule { Name = "Read scrolls", Action = RynthCore.Loot.VTank.VTankLootAction.Read };
        rule.Conditions.Add(new RynthCore.Loot.VTank.VTankLootCondition(RynthCore.Loot.VTank.VTankNodeTypes.ObjectClass, "0", new[] { "42" }));
        utl.Rules.Add(rule);
        var bot = new Bot(host, Settings0(readUnknown: false), utl);
        foreach (uint id in new[] { Scroll, Scroll2, Gem }) bot.Cache.OnCreateObject(id);
        bot.Cache.Tick();

        Check.Eq(bot.Cache[unchecked((int)Scroll)]!.ObjectClass, AcObjectClass.Scroll, "a spell-carrying writable is classed Scroll (as in Decal)");
        Check.True(ScrollLearner.IsDecalScroll(bot.Cache[unchecked((int)Scroll)]), "but it is Decal's Scroll (it carries a spell)");
        Check.False(ScrollLearner.IsDecalScroll(bot.Cache[unchecked((int)Scroll2)]), "a letter is not");
        var c = bot.Classify(Scroll);
        Check.True(c.Kept && c.Action == "Read" && c.Rule == "Read scrolls", $"ObjectClass == Scroll matches it, action Read ({c.Kept}, {c.Action}, {c.Rule})");
        Check.False(bot.Classify(Scroll2).Kept, "the letter does not match a Scroll rule");
        Check.False(bot.Classify(Gem).Kept, "nor the gem");
        var native = new ObjectClassCondition { ObjectClass = AcObjectClass.Scroll };
        Check.True(LootEvaluator.Evaluate(native, bot.Cache[unchecked((int)Scroll)]!, null), "native profiles: Scroll matches it too");

        bot.LootCorpse(() => Moved(Scroll));
        Check.True(Moved(Scroll), "looted");
        IntoPack(Scroll);
        TickUntilRead(bot.L, bot.Clock);
        Check.True(FakeHost.Uses.Contains(Scroll), "and read: " + LearnLogs());
        Done();
    }

    private static void SettingOffDoesNothing()
    {
        BaseWorld();
        AddScroll(Scroll, FrostBoltVI, "Scroll of Frost Bolt VI", Corpse);
        AddScroll(PackScroll, FlameBoltIII, "Scroll of Flame Bolt III", Player);
        var host = FakeHost.Create(Player);
        var bot = new Bot(host, Settings0(readUnknown: false), GemsOnly());

        bot.LootCorpse(() => false);
        Check.False(Moved(Scroll), "not looted");
        for (int i = 0; i < 10; i++) { bot.Clock.Now += 6_000; bot.L.Tick(true, 0); }
        Check.Eq(FakeHost.Uses.Count(u => u == Scroll || u == PackScroll), 0, "nothing read");
        Check.Eq(bot.L.PendingCount, 0, "nothing queued");
        Check.False(Logged("[Learn]"), "and nothing to say: " + LearnLogs());
        Done();
    }

    private static void PackScrollAtSafeMoment()
    {
        BaseWorld();
        AddScroll(PackScroll, FlameBoltIII, "Scroll of Flame Bolt III", Player);
        var host = FakeHost.Create(Player);
        var bot = new Bot(host, Settings0(), GemsOnly());
        bot.Cache.OnCreateObject(PackScroll);
        bot.Cache.Tick();

        for (int i = 0; i < 5; i++) { bot.Clock.Now += 6_000; bot.L.Tick(false, 0); }
        Check.True(bot.L.IsPending(unchecked((int)PackScroll)), "found in the pack and queued");
        Check.True(Logged("[Learn] found 'Scroll of Flame Bolt III' in the pack (Flame Bolt III: unknown, War Magic trained) - will read it"), "logged: " + LearnLogs());
        Check.False(FakeHost.Uses.Contains(PackScroll), "not read while it isn't safe");
        for (int i = 0; i < 5; i++) { bot.Clock.Now += 6_000; bot.L.Tick(true, 2); }
        Check.False(FakeHost.Uses.Contains(PackScroll), "not read while the client is busy");
        TickUntilRead(bot.L, bot.Clock);
        Check.True(FakeHost.Uses.Contains(PackScroll), "read at a safe moment");
        Check.True(bot.L.Tick(true, 0), "the bot holds still while the read is in flight");

        // The plugin's safe-moment test.
        bool Safe() => (bool)bot.Call("IsSafeToReadScroll", bot.S)!;
        bot.Set("_activity", BotActivity.Idle);
        Check.True(Safe(), "idle, macro on, nothing around: safe");
        bot.Set("_activity", BotActivity.Navigating);
        Check.True(Safe(), "navigating: safe (the read stops nav for a moment)");
        bot.Set("_activity", BotActivity.Combat);
        Check.False(Safe(), "combat: not safe");
        bot.Set("_activity", BotActivity.Buffing);
        Check.False(Safe(), "buffing: not safe");
        bot.Set("_activity", BotActivity.Idle);
        bot.Set("_targetCorpseId", unchecked((int)Corpse));
        Check.False(Safe(), "a corpse claimed: not safe");
        bot.Set("_targetCorpseId", 0);
        bot.S.IsMacroRunning = false;
        Check.False(Safe(), "macro off: not safe");
        Done();
    }

    private static void ReadTimeoutBounded()
    {
        BaseWorld();
        AddScroll(PackScroll, FlameBoltIII, "Scroll of Flame Bolt III", Player);
        var host = FakeHost.Create(Player);
        var cache = FakeHost.MakeCache(host, Player, new[] { PackScroll });
        var clock = new Clock();
        var l = Learner(host, Settings0(), cache, clock);

        TickUntilRead(l, clock);
        Check.Eq(FakeHost.Uses.Count(u => u == PackScroll), 1, "first read");
        clock.Now += ScrollLearner.ReadTimeoutMs + 1;
        l.Tick(true, 0);
        Check.True(Logged("not confirmed (no answer in 8 s) - will try again"), "timeout logged: " + LearnLogs());
        TickUntilRead(l, clock);
        Check.Eq(FakeHost.Uses.Count(u => u == PackScroll), 2, "one retry");
        clock.Now += ScrollLearner.ReadTimeoutMs + 1;
        l.Tick(true, 0);
        Check.True(l.HasFailed(FlameBoltIII), "then given up and remembered");
        for (int i = 0; i < 10; i++) { clock.Now += 6_000; l.Tick(true, 0); }
        Check.Eq(FakeHost.Uses.Count(u => u == PackScroll), 2, "no third read");
        Done();
    }

    private static void Settings()
    {
        Check.True(new LegacyUiSettings().ReadUnknownScrolls, "on by default (VTank's default)");
        var old = JsonSerializer.Deserialize("{\"EnableAutostack\":true}", RynthAiJsonContext.Default.LegacyUiSettings)!;
        Check.True(old.ReadUnknownScrolls, "a profile from before the setting: on");
        var bridge = JsonSerializer.Deserialize("{\"EnableAutostack\":true}", RynthAiJsonContext.Default.SettingsBridgePayload)!;
        Check.True(bridge.ReadUnknownScrolls, "an older engine's settings: kept on");
        var off = JsonSerializer.Deserialize("{\"ReadUnknownScrolls\":false}", RynthAiJsonContext.Default.LegacyUiSettings)!;
        Check.False(off.ReadUnknownScrolls, "off saves and loads");

        Check.True(RynthCore.Plugin.RynthAi.Meta.MetaManager.TryMapVtOption("ReadUnknownScrolls", out string ra) && ra == "ReadUnknownScrolls", "/vt opt ReadUnknownScrolls maps to it");
        Check.Eq(RynthCore.Plugin.RynthAi.Meta.MetaManager.NormalizeVtValue("False"), "0", "VTank's False is 0");

        var s = new LegacyUiSettings();
        var e = ExpressionTests.Engine(settings: s);
        Check.Eq(e.Evaluate("vtsetsetting[ReadUnknownScrolls, 0]"), "1", "metas: vtsetsetting knows it");
        Check.False(s.ReadUnknownScrolls, "and turns it off");
        Check.Eq(e.Evaluate("vtgetsetting[ReadUnknownScrolls]"), "0", "vtgetsetting reads it back");
    }
}
