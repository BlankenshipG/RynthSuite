using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// HP failsafe: under Emergency Heal At with heals failing (here: every heal sent and never answered,
// as in a client log's hung casts), the character must not sit and die. 3 rounds of 2.5 s with no
// rise: a forced jump (ActionPacer.ForceBreak), then healing again; 3 more: the macro stops and
// "/lifestone" goes out (HpFailsafeRecall, default off; the tests turn it on).
internal static class HpFailsafeTests
{
    private const uint Player = 0x50000B31;
    private const uint STypeLife = 33;

    public static void Register(Runner r)
    {
        r.Add("hp failsafe: 7.5 s at Emergency Heal At with no heal landing -> one jump; 7.5 s more -> macro stop and /lifestone", BreakThenRecall);
        r.Add("hp failsafe: a heal landing (+3%) restarts the count; health over the line ends it", HealLandingResets);
        r.Add("hp failsafe: HP Failsafe Recall off -> the jump still, no recall; no recall line -> 3 sends, then it gives up", RecallOffAndResends);
        r.Add("hp failsafe: a recall that never starts turns the macro back on and re-arms; the next recall after 30 s", FailedRecallResumes);
        r.Add("hp failsafe: another player's recall line is not ours; ours with its first letter dropped is", RecallLineIsOurs);
        r.Add("hp failsafe: Emergency Heal At 100 at full health is not an emergency", FullHealthAtHundred);
        r.Add("hp failsafe: nothing counts in portal space", NothingInPortalSpace);
        r.Add("hp failsafe: a death drops the pending recall (0 health, and the plugin's death hold): no /lifestone, the macro stays off", DeathDropsRecall);
        r.Add("hp failsafe: a failed recall does not switch the macro back on past a VTank yield", VTankYieldRespected);
        r.Add("hp failsafe: our name unreadable: the recall line is taken as ours, and that is logged once", RecallNameUnreadable);
        r.Add("hp failsafe: HP Failsafe Recall is off by default; a profile with no saved value loads off, one that saved true keeps it", RecallOffByDefault);
        r.Add("hp failsafe: a character that cannot heal at all (no spell, kit or potion) never jumps or recalls", NoHealCapabilityNeverFires);
        r.Add("hp failsafe: a character that can heal (a known Heal Self) and is stuck still jumps, then recalls when the option is on", CapableStuckStillRecalls);
        r.Add("hp failsafe: a heal that went out proves the character can heal (a listed potion, no spell)", PotionSentProvesCapability);
        r.Add("vitals (L8):a heal the server leaves unanswered is not re-sent at the 2.5 s give-up after a 'too busy' answer", VitalResendObeysLatch);
        r.Add("vitals: under Emergency Heal At with a weapon out and no usable kit, a health potion before Heal Self's wand swap", EmergencyPotionBeforeWandSwap);
        r.Add("low components: tapers at 10, a scarab at 2 (not Diamond), kinds never carried ignored, a kind run out still counts", LowComponentsRule);
    }

    // Recall When Low On Components (RynthAiPlugin.LowComponents.cs), the rule itself.
    private static void LowComponentsRule()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [RynthAiPlugin.TaperKey] = 250, ["Lead Scarab"] = 12, ["Iron Scarab"] = 3,
        };
        Check.Null(RynthAiPlugin.LowComponents(counts, seen, 10, 2), "plenty of everything: nothing low");
        counts[RynthAiPlugin.TaperKey] = 10;
        Check.Eq(RynthAiPlugin.LowComponents(counts, seen, 10, 2), "tapers 10", "10 tapers: at the line");
        counts[RynthAiPlugin.TaperKey] = 11;
        counts["Iron Scarab"] = 2;
        Check.Eq(RynthAiPlugin.LowComponents(counts, seen, 10, 2), "Iron Scarab 2", "2 Iron Scarabs: low");
        counts.Remove("Iron Scarab");   // used the last ones: gone from the pack
        Check.Eq(RynthAiPlugin.LowComponents(counts, seen, 10, 2), "Iron Scarab 0", "run out entirely: still low (seen before)");
        Check.Null(RynthAiPlugin.LowComponents(counts, seen, 10, 0), "Low Scarabs At 0: scarabs aren't checked");

        var fighter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Check.Null(RynthAiPlugin.LowComponents(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), fighter, 10, 2),
            "a character that never carried tapers or scarabs is never low");

        var inv = new[]
        {
            new WorldObject(1, "Prismatic Taper"), new WorldObject(2, "Red Taper"),
            new WorldObject(3, "Diamond Scarab"), new WorldObject(4, "Mana Scarab"), new WorldObject(5, "Scarab Shell Armor"),
        };
        var c = RynthAiPlugin.CountComponents(inv);
        Check.Eq(c.GetValueOrDefault(RynthAiPlugin.TaperKey), 2, "every kind of taper counts together");
        Check.False(c.ContainsKey("Diamond Scarab"), "Diamond Scarabs never count");
        Check.Eq(c.GetValueOrDefault("Mana Scarab"), 1, "a Mana Scarab counts");
        Check.Eq(c.Count, 2, "only tapers and scarabs (not 'Scarab Shell Armor')");
    }

    // Drakkon, 2026-10-14: 15% health in melee among Depraved Shadows, the kit at 3% (under Kit Min
    // Success), and the chain went to Heal Self: a wand swap still unanswered when he died 3.2 s later.
    // The potion came after Heal Self, so it was never reached. Now the emergency step drinks first.
    private static void EmergencyPotionBeforeWandSwap()
    {
        const uint Potion = 0x80001234;
        try
        {
            var r = new Rig(healthPct: 15);
            FakeHost.CombatModeValue = CombatMode.Melee;
            FakeHost.Names[Potion] = "Healing Elixir";
            r.S.UsePotions = true;
            r.S.ConsumableRules.Add(new ConsumableRule { Id = unchecked((int)Potion), Name = "Healing Elixir", Type = "HealthPotion" });
            var cache = new WorldObjectCache(r.Host);
            cache.SetPlayerId(Player);
            cache.OnCreateObject(Player);
            _ = cache[unchecked((int)Potion)];
            cache.Tick();
            r.B.SetWorldObjectCache(cache);

            r.Run(0, 0.3);
            Check.True(FakeHost.Uses.Contains(Potion), "the health potion is drunk: " + string.Join(" | ", FakeHost.Logs.Where(l => l.Contains("Vitals")).Take(10)));
            Check.Eq(FakeHost.Casts.Count, 0, "no Heal Self cast (no wand swap) while the potion goes");
        }
        finally { Done(); }
    }

    // No re-send into a busy server: a client log re-sent a heal every 2.5 s (the vitals give-up) into a server
    // answering 0x1D. Through the whole vitals step (BuffManager heartbeats, pacer on, the pacer's
    // breaker off to isolate the latch): a heal goes out, the server never answers it, and another
    // send is refused 0x1D. No heal is re-sent while that heal is unanswered.
    private static void VitalResendObeysLatch()
    {
        try
        {
            var r = new Rig(healthPct: 40);
            r.S.EmergencyHealAt = 0;          // the failsafe out of it
            r.S.SpellCastIntervalMs = 100;
            r.Run(0, 0);
            Check.Eq(FakeHost.Casts.Count, 1, "the heal goes out (and is never answered)");
            r.Run(0.1, 0.5);
            int heal = FakeHost.Casts[0].SpellId;
            // Another send goes out over the open heal (as when a cast hangs) and is refused 'too busy'.
            Check.True(r.Host.CastSpellFor(0x80000AAAu, 1237, "Combat", "Drain Health Other I"), "the other send");
            FakeHost.UseDone(ActionPacer.TooBusyErr);
            // Real time too: BuffManager's own retry limits run on the real clock.
            for (int ms = 600; ms <= 6000; ms += 200) { System.Threading.Thread.Sleep(200); r.At(ms / 1000.0); r.B.OnHeartbeat(); }
            Check.Eq(FakeHost.Casts.Count(c => c.SpellId == heal), 1, "no heal re-sent in 6 s (the 2.5 s give-up re-sent one before): " + string.Join(" | ", FakeHost.Logs.Where(l => l.Contains("[Pace]") || l.Contains("[Buff")).Take(20)));
            Check.True(r.Logged("Buff waits:") && r.Logged("nothing goes until the server answers"), "the hold is logged: " + string.Join(" | ", FakeHost.Logs.Take(30)));
        }
        finally { Done(); }
    }

    private sealed class Rig
    {
        public DateTime T0 = new(2026, 10, 10, 19, 0, 0);
        public DateTime NowAt;
        public readonly BuffManager B;
        public readonly LegacyUiSettings S;
        public readonly PlayerVitalsCache V;
        public readonly RynthCore.PluginSdk.RynthCoreHost Host;
        public int MacroStops;

        public Rig(int healthPct = 20, bool canHeal = true, bool recall = true)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            FakeHost.ServerGates = true;
            FakeHost.LastUseDoneCalls = true;
            FakeHost.TapJumpCalls = true;
            FakeHost.CombatModeValue = CombatMode.Magic;
            FakeHost.Names[Player] = "Mira";
            FakeHost.PlayerPose = (0x6347028Au, 100f, 100f, 0f);
            FakeHost.Positions[Player] = FakeHost.PlayerPose;
            if (canHeal)
            {
                foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
                FakeHost.Skills[(Player, STypeLife)] = (400, 2);
            }
            else
            {
                // A warm spellbook that holds no heal (an empty one reads as "not loaded yet").
                FakeHost.Spellbook.Add((uint)SpellTable.Rows.First(row => !row.Name.Contains("Heal", StringComparison.OrdinalIgnoreCase)
                    && !row.Name.Contains("Health", StringComparison.OrdinalIgnoreCase)).Id);
            }
            var host = Host = FakeHost.Create(Player);
            S = new LegacyUiSettings
            {
                IsMacroRunning = true, HealAt = 60, TopOffHP = 60, EmergencyHealAt = 30, StaminaToHealthAt = 0,
                GetManaAt = 0, TopOffMana = 0, RestamAt = 0, TopOffStam = 0, UsePotions = false,
                EnableBuffing = false, SpellCastIntervalMs = 100,
                HpFailsafeRecall = recall,   // off by default now; most tests here are about the recall
            };
            var skills = new CharacterSkills(host);
            skills.SetPlayerId(Player);
            var sm = new SpellManager(host, S);
            sm.InitializeNatively();
            sm.SetCharacterSkills(skills);
            V = new PlayerVitalsCache
            {
                MaxHealth = 100, CurrentHealth = (uint)healthPct, MaxStamina = 100, CurrentStamina = 100,
                MaxMana = 100, CurrentMana = 100,
            };
            B = new BuffManager(host, S, sm, V);
            B.SetCharacterSkills(skills);
            B.MacroStopForRecall = () => MacroStops++;
            NowAt = T0;
            B.Clock = () => NowAt;
            B.MarkLiveBuffsReadyForTests();

            ActionPacer.Reset();
            ActionPacer.Enabled = true;
            ActionPacer.StuckCastMs = 0;   // the pacer's own breaker off: only the failsafe jumps here
            ActionPacer.NowMs = () => 50_000_000 + (long)(NowAt - T0).TotalMilliseconds;
            MoveAudit.Reset();
            MoveAudit.NowMs = ActionPacer.NowMs;
            FakeHost.UseDone(0);
        }

        public void At(double seconds) => NowAt = T0.AddMilliseconds(Math.Round(seconds * 1000));
        public bool Logged(string part) => FakeHost.Logs.Any(l => l.Contains(part, StringComparison.Ordinal));

        /// <summary>Heartbeats every 100 ms from <paramref name="from"/> up to and including <paramref name="to"/>.</summary>
        public void Run(double from, double to)
        {
            for (int ms = (int)Math.Round(from * 1000); ms <= (int)Math.Round(to * 1000); ms += 100)
            {
                At(ms / 1000.0);
                B.OnHeartbeat();
            }
        }
    }

    private static void Done()
    {
        ActionPacer.Enabled = false;
        ActionPacer.Reset();
        ActionPacer.NowMs = () => Environment.TickCount64;
        ActionPacer.StuckCastMs = 8000;
        MoveAudit.Reset();
        MoveAudit.NowMs = () => Environment.TickCount64;
    }

    private static void CopySettings(LegacyUiSettings src, LegacyUiSettings dst) =>
        typeof(RynthCore.Plugin.RynthAi.LegacyUi.LegacyDashboardRenderer).GetMethod("CopySettings",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!
            .Invoke(null, new object[] { src, dst });

    private static void RecallOffByDefault()
    {
        Check.False(new LegacyUiSettings().HpFailsafeRecall, "a new settings object has the recall off");
        var ctx = RynthAiJsonContext.Default.LegacyUiSettings;
        // A profile with no saved value for it: off.
        var node = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new LegacyUiSettings(), ctx))!.AsObject();
        Check.True(node.Remove("HpFailsafeRecall"), "the key is a serialized setting");
        var noKey = System.Text.Json.JsonSerializer.Deserialize(node.ToJsonString(), ctx)!;
        var live = new LegacyUiSettings { HpFailsafeRecall = true };
        CopySettings(noKey, live);
        Check.False(live.HpFailsafeRecall, "no saved value: the recall loads off");
        // A profile that saved true (the owner turned it on, or the earlier default was saved): kept.
        node["HpFailsafeRecall"] = true;
        var saved = System.Text.Json.JsonSerializer.Deserialize(node.ToJsonString(), ctx)!;
        var live2 = new LegacyUiSettings();
        CopySettings(saved, live2);
        Check.True(live2.HpFailsafeRecall, "a saved true stays on");
    }

    // A melee character with nothing to heal with (no spell, kit or potion), resting or out-healed at
    // low health, must not be jumped around and sent to the lifestone: there is nothing to retry.
    private static void NoHealCapabilityNeverFires()
    {
        try
        {
            var r = new Rig(healthPct: 20, canHeal: false);
            r.Run(0, 40);
            Check.Eq(FakeHost.Casts.Count, 0, "nothing to cast with");
            Check.Eq(FakeHost.TapJumps, 0, "no failsafe jump");
            Check.Eq(r.B.HpFailsafeBreaks, 0, "nothing counted");
            Check.Eq(FakeHost.ChatCommands.Count, 0, "no /lifestone");
            Check.Eq(r.B.HpFailsafeRecalls, 0, "no recall");
            Check.True(r.S.IsMacroRunning, "the macro keeps running");

            // Life Magic trained but no Heal Self known, and a potion listed with Use Potions off: still nothing.
            const uint Potion = 0x80001234;
            var p = new Rig(healthPct: 20, canHeal: false);
            FakeHost.Skills[(Player, STypeLife)] = (400, 2);
            FakeHost.Names[Potion] = "Healing Elixir";
            p.S.UsePotions = false;
            p.S.ConsumableRules.Add(new ConsumableRule { Id = unchecked((int)Potion), Name = "Healing Elixir", Type = "HealthPotion" });
            var cache = new WorldObjectCache(p.Host);
            cache.SetPlayerId(Player);
            cache.OnCreateObject(Player);
            _ = cache[unchecked((int)Potion)];
            cache.Tick();
            p.B.SetWorldObjectCache(cache);
            p.Run(0, 40);
            Check.Eq(FakeHost.TapJumps, 0, "no spell known, potions off: no jump");
            Check.Eq(FakeHost.ChatCommands.Count, 0, "and no recall");
            Check.True(p.S.IsMacroRunning, "the macro keeps running");
        }
        finally { Done(); }
    }

    // The gate does not switch the failsafe off for a character that can heal: stuck heals still break, then recall.
    private static void CapableStuckStillRecalls()
    {
        try
        {
            var r = new Rig(healthPct: 20, canHeal: true, recall: true);
            r.Run(0, 15.0);
            Check.Eq(FakeHost.TapJumps, 1, "capable + stuck: the jump");
            Check.False(r.S.IsMacroRunning, "capable + stuck + recall on: the macro stops");
            r.Run(15.1, 16.0);
            Check.Eq(FakeHost.ChatCommands.Count, 1, "and /lifestone goes out");

            // The option off (the default): the jump only, the macro keeps running.
            var d = new Rig(healthPct: 20, canHeal: true, recall: false);
            d.Run(0, 30.0);
            Check.Eq(FakeHost.TapJumps, 1, "recall off: the jump still");
            Check.True(d.S.IsMacroRunning, "recall off: no stop");
            Check.Eq(FakeHost.ChatCommands.Count, 0, "recall off: no /lifestone");
        }
        finally { Done(); }
    }

    // No spell, but a listed health potion with Use Potions on: the potion goes out (a heal sent), so
    // the failsafe can arm when the potion does not raise health.
    private static void PotionSentProvesCapability()
    {
        const uint Potion = 0x80001234;
        try
        {
            var r = new Rig(healthPct: 20, canHeal: false);
            FakeHost.Names[Potion] = "Healing Elixir";
            r.S.UsePotions = true;
            r.S.ConsumableRules.Add(new ConsumableRule { Id = unchecked((int)Potion), Name = "Healing Elixir", Type = "HealthPotion" });
            var cache = new WorldObjectCache(r.Host);
            cache.SetPlayerId(Player);
            cache.OnCreateObject(Player);
            _ = cache[unchecked((int)Potion)];
            cache.Tick();
            r.B.SetWorldObjectCache(cache);
            r.Run(0, 15.0);
            Check.True(FakeHost.Uses.Contains(Potion), "the potion is drunk");
            Check.Eq(FakeHost.TapJumps, 1, "it did not raise health: the failsafe jump");
            Check.False(r.S.IsMacroRunning, "and the recall stage");
        }
        finally { Done(); }
    }

    private static void BreakThenRecall()
    {
        try
        {
            var r = new Rig(healthPct: 20);
            r.Run(0, 7.4);
            Check.True(FakeHost.Casts.Count > 0, "a heal went out (and is never answered)");
            Check.Eq(FakeHost.TapJumps, 0, "7.4 s: no jump yet");
            r.Run(7.5, 7.5);
            Check.Eq(FakeHost.TapJumps, 1, "7.5 s (3 rounds, no rise): one jump");
            Check.Eq(r.B.HpFailsafeBreaks, 1, "counted");
            Check.True(r.Logged("HP failsafe: health 20% at or under Emergency Heal At 30%, no heal landed in 7.5 s: breaking the cast with a jump"),
                "the break is logged");
            r.Run(7.6, 14.9);
            Check.Eq(FakeHost.TapJumps, 1, "only one jump");
            Check.True(r.S.IsMacroRunning, "14.9 s: still running");
            r.Run(15.0, 15.0);
            Check.False(r.S.IsMacroRunning, "15 s (3 more rounds): the macro is stopped");
            Check.Eq(r.MacroStops, 1, "with the rest of a /ra stop");
            Check.Eq(r.B.HpFailsafeRecalls, 1, "counted");
            Check.True(FakeHost.Chat.Any(c => c.Contains("recalling to the lifestone")), "and said in chat");
            Check.Eq(FakeHost.ChatCommands.Count, 0, "the recall waits for the stop to take effect");
            r.Run(15.1, 15.5);
            Check.Eq(FakeHost.ChatCommands.Count, 0, "500 ms later: still waiting for the stop to take effect");
            r.Run(15.6, 15.6);
            Check.Eq(FakeHost.ChatCommands.Count, 1, "600 ms later: one recall");
            Check.Eq(FakeHost.ChatCommands[0], BuffManager.RecallCommand, "/lifestone");
            r.B.OnChatWindowText("Mira is recalling to the lifestone.", 0);
            r.Run(15.7, 20);
            Check.Eq(FakeHost.ChatCommands.Count, 1, "the recall line: no resend");
            Check.True(r.Logged("HP failsafe: the recall started"), "logged");
        }
        finally { Done(); }
    }

    private static void HealLandingResets()
    {
        try
        {
            var r = new Rig(healthPct: 20);
            r.Run(0, 5.0);
            r.V.CurrentHealth = 22;     // +2: regeneration, not a heal
            r.Run(5.1, 7.5);
            Check.Eq(FakeHost.TapJumps, 1, "+2% is not a heal landing: the jump at 7.5 s");

            var h = new Rig(healthPct: 20);
            h.Run(0, 5.0);
            h.V.CurrentHealth = 23;     // +3: a heal landed
            h.Run(5.1, 12.4);
            Check.Eq(FakeHost.TapJumps, 0, "a heal landed at 5 s: no jump at 7.5 s, none by 12.4 s");
            h.Run(12.5, 12.6);
            Check.Eq(FakeHost.TapJumps, 1, "7.5 s after the heal with no other: the jump");

            var o = new Rig(healthPct: 20);
            o.Run(0, 5.0);
            o.V.CurrentHealth = 31;     // over Emergency Heal At 30
            o.Run(5.1, 5.1);
            o.V.CurrentHealth = 20;
            o.Run(5.2, 12.6);
            Check.Eq(FakeHost.TapJumps, 0, "over the line at 5.1 s: the count started again at 5.2 s");
            Check.True(o.Logged("HP failsafe: health back to 31%"), "the end is logged");
        }
        finally { Done(); }
    }

    private static void RecallOffAndResends()
    {
        try
        {
            var r = new Rig(healthPct: 20);
            r.S.HpFailsafeRecall = false;
            r.Run(0, 20);
            Check.Eq(FakeHost.TapJumps, 1, "recall off: the jump still");
            Check.True(r.S.IsMacroRunning, "recall off: the macro keeps running");
            Check.Eq(FakeHost.ChatCommands.Count, 0, "no recall");
            Check.True(r.Logged("HP Failsafe Recall is off"), "said once in the log");

            var n = new Rig(healthPct: 20);
            n.Run(0, 15.0);
            Check.False(n.S.IsMacroRunning, "recall: macro stopped");
            n.Run(15.1, 25);
            Check.Eq(FakeHost.ChatCommands.Count, 3, "no recall line: sent 3 times, 1.5 s apart, then stopped");
            Check.True(n.Logged("no recall seen after 3 tries"), "and logged");
        }
        finally { Done(); }
    }

    // A recall that never starts must not leave the macro stopped at low health.
    private static void FailedRecallResumes()
    {
        try
        {
            var n = new Rig(healthPct: 20);
            n.Run(0, 15.0);
            Check.False(n.S.IsMacroRunning, "recall: macro stopped");
            n.Run(15.1, 21.0);
            Check.Eq(FakeHost.ChatCommands.Count, 3, "3 tries, no recall line");
            Check.True(n.S.IsMacroRunning, "the macro is back on (the heals and kits run again)");
            Check.Eq(n.B.HpFailsafeRecallsFailed, 1, "a failed recall counted");
            Check.True(FakeHost.Chat.Any(c => c.Contains("macro back on to keep healing")), "said in chat");
            n.Run(21.1, 49.0);
            Check.Eq(FakeHost.ChatCommands.Count, 3, "inside the 30 s cooldown: no recall");
            Check.True(n.S.IsMacroRunning, "and the macro keeps running");
            int breaks = FakeHost.Logs.Count(l => l.Contains("HP failsafe: health 20% at or under Emergency Heal At 30%, no heal landed in"));
            Check.True(breaks >= 2, $"the failsafe re-armed: it reached the break stage again ({breaks})");
            n.Run(49.1, 75.0);
            Check.Eq(n.B.HpFailsafeRecalls, 2, "after the cooldown: the next recall");
            Check.True(FakeHost.ChatCommands.Count > 3, "sent");
        }
        finally { Done(); }
    }

    // Only our own recall line ends the wait.
    private static void RecallLineIsOurs()
    {
        try
        {
            var r = new Rig(healthPct: 20);
            r.Run(0, 15.6);
            Check.Eq(FakeHost.ChatCommands.Count, 1, "the first recall");
            r.B.OnChatWindowText("Bob is recalling to the lifestone.", 0);
            r.Run(15.7, 17.1);
            Check.Eq(FakeHost.ChatCommands.Count, 2, "another player's recall line: sent again");
            Check.False(r.Logged("HP failsafe: the recall started"), "not taken as ours");
            r.B.OnChatWindowText("ira is recalling to the lifestone.", 0);   // the first letter dropped by the chat layer
            r.Run(17.2, 22);
            Check.Eq(FakeHost.ChatCommands.Count, 2, "ours (first letter dropped): no more sends");
            Check.True(r.Logged("HP failsafe: the recall started"), "logged");
        }
        finally { Done(); }
    }

    // Emergency Heal At 100 at full health is not an emergency.
    private static void FullHealthAtHundred()
    {
        try
        {
            var r = new Rig(healthPct: 100);
            r.S.EmergencyHealAt = 100;
            r.Run(0, 20);
            Check.Eq(FakeHost.TapJumps, 0, "100% at Emergency Heal At 100: no break");
            Check.True(r.S.IsMacroRunning, "no recall");
        }
        finally { Done(); }
    }

    // A pending recall is dropped on death: after the respawn it must not send /lifestone or switch
    // the macro on, against Stop Macro On Death.
    private static void DeathDropsRecall()
    {
        try
        {
            var r = new Rig(healthPct: 20);
            r.Run(0, 15.0);
            Check.False(r.S.IsMacroRunning, "recall pending: macro stopped");
            r.V.CurrentHealth = 0;                    // dead before the first send
            r.Run(15.1, 30);
            Check.Eq(FakeHost.ChatCommands.Count, 0, "0 health: no /lifestone");
            Check.False(r.S.IsMacroRunning, "the macro stays off");
            Check.True(r.Logged("the pending recall is dropped (dead)"), "logged");

            // The plugin's death hold drops it too (the vitals cache still reads the old health).
            var p = new Rig(healthPct: 20);
            p.Run(0, 15.0);
            var plugin = new RynthAiPlugin();
            PacerEdgeTests.SetHost(plugin, p.Host);
            typeof(RynthAiPlugin).GetField("_buffManager", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(plugin, p.B);
            FakeHost.Vitals = (0, 100, 0, 100, 0, 100);
            typeof(RynthAiPlugin).GetMethod("HoldForDeath", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(plugin, null);
            p.Run(15.1, 30);
            Check.Eq(FakeHost.ChatCommands.Count, 0, "death hold: no /lifestone after the respawn");
            Check.False(p.S.IsMacroRunning, "death hold: the macro stays off");
            Check.True(p.Logged("the pending recall is dropped (death)"), "logged");
        }
        finally { Done(); }
    }

    // The failsafe's restart is a macro start like any other.
    private static void VTankYieldRespected()
    {
        try
        {
            var r = new Rig(healthPct: 20);
            r.B.MayRestartMacro = () => false;        // VTank runs and yield is on
            r.Run(0, 21.0);
            Check.Eq(FakeHost.ChatCommands.Count, 3, "3 tries, no recall line");
            Check.False(r.S.IsMacroRunning, "VTank yield: the macro stays off");
            Check.True(FakeHost.Chat.Any(c => c.Contains("the macro stays off (VTank yield)")), "said in chat");
        }
        finally { Done(); }
    }

    private static void RecallNameUnreadable()
    {
        try
        {
            var r = new Rig(healthPct: 20);
            FakeHost.Names.Remove(Player);
            r.Run(0, 15.6);
            Check.Eq(FakeHost.ChatCommands.Count, 1, "the first recall");
            r.B.OnChatWindowText("Bob is recalling to the lifestone.", 0);
            r.Run(15.7, 20);
            Check.Eq(FakeHost.ChatCommands.Count, 1, "no name to compare: taken as ours, no resend");
            Check.Eq(FakeHost.Logs.Count(l => l.Contains("our name could not be read")), 1, "logged once");
        }
        finally { Done(); }
    }

    // Nothing counts in portal space.
    private static void NothingInPortalSpace()
    {
        try
        {
            var r = new Rig(healthPct: 20);
            FakeHost.Portaling = true;
            r.Run(0, 20);
            Check.Eq(FakeHost.TapJumps, 0, "portal space: no failsafe jump");
            Check.True(r.S.IsMacroRunning, "no recall");
            Check.Eq(r.B.HpFailsafeBreaks, 0, "nothing counted");
        }
        finally { FakeHost.Portaling = false; Done(); }
    }
}
