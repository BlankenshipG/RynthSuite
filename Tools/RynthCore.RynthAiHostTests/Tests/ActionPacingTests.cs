using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// One server action at a time (2026-10-06, ops\overnight\2026-10-06-action-pacing.md).
// Drakkon's stuck uses and stuck casts on DreamWeave: every stall began with a combat-mode change
// sent while a corpse use was still unanswered, and once the server held an action RynthAi fired
// the next cast or use every 0.4-2.5 s on top of it (it paced on a busy count it kept zeroing, and
// on the enchantment rather than the server's UseDone). These tests replay those sequences against
// Plugins/Shared/ActionPacer.cs and the RynthAi code wired to it:
//  - the pacer itself: answers (UseDone, a refusal, a wield, a stance read), time limits, the heal
//    rules, and that nothing is ever force-cleared;
//  - the onset sequence (corpse use -> idle peace swap 3-677 ms later) and the movement hold;
//  - BuffManager: a cast whose result is in but whose UseDone isn't waits (one cast, not a flood),
//    heals still go first, and the emergency stance settle is unchanged;
//  - looting: one open per answer; Drakkon's stall is still found and reported once;
//  - CombatManager's idle peace swap waits for an unanswered kit;
//  - the busy mirror is reset without touching the client's count.
internal static class ActionPacingTests
{
    private const uint Player = 0x50000E01;
    private const uint Corpse = 0x80068ACE;
    private const uint CorpseB = 0x80072023;
    private const uint Kit = 0x8004BEF8;
    private const uint Wand = 0x8007467A;
    private const uint Pack = 0x50000E01;   // the player's main pack is the player
    private const uint ItemTypeContainer = 0x200;
    private const uint LongDescKey = 16;
    private const uint TooBusy = 0x1D;
    private const string Me = "Drakkon";
    private static readonly (uint Cell, float X, float Y, float Z) Me0 = (0xA9B40019u, 100f, 100f, 0f);
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

    public static void Register(Runner r)
    {
        r.Add("pacing: a use is the server action until its UseDone; a refusal answers it too; a wield answers by wielding", UseAnswers);
        r.Add("pacing: the stall onset replayed - the idle peace swap waits for the corpse use's answer, then goes; no swap to the mode you're in", OnsetModeChangeHeld);
        r.Add("pacing: moves, turns and redundant stops wait while the server answers; a real stop and the cast nudge go through", MovesHeld);
        r.Add("pacing: no answer - each kind's time limit lets the next action go, logged, and nothing is force-cleared", TimeLimits);
        r.Add("pacing: heals (a plain heal waits for a corpse use's answer, 300 ms at Emergency, one behind a cast's result, 300 ms after the stance reads)", HealRules);
        r.Add("pacing (vitals): Drakkon 07:03 - a cast whose result is in waits for its UseDone: one cast, not a too-busy flood", CastWaitsForUseDone);
        r.Add("pacing (vitals): a heal waits for an unanswered corpse use's answer, then goes at once; the emergency stance settle is unchanged", HealLatencyKept);
        r.Add("pacing (looting): one open per server answer, never re-sent on top of an unanswered one", LootOneOpenPerAnswer);
        r.Add("pacing (looting): Drakkon's stall is still found and said once with pacing on", LootStallStillDetected);
        r.Add("pacing (combat): the idle peace swap waits while a kit is unanswered", CombatPeaceWaitsForKit);
        r.Add("pacing (arbiter): the activity-change StopCompletely only when RynthAi itself has the character moving", ArbiterStopOnlyWhenMoving);
        r.Add("busy mirror: follows the engine's count (1.5 s), never reset while the engine says busy; old engines: the mirror only; never a force-clear", MirrorResetOnly);
        r.Add("motion gate (14:29): a kit waits for the swing's animation; the corpse open waits for the kit's answer and then its animation", MotionGate1429);
        r.Add("pacing (motion): after a swing is sent, nothing else starts until it has played (16:45 wand on a swing)", SwingNotStarted);
        r.Add("motion gate (13:58): wand wield -> the cast and the Magic stance wait until the wield's animation is over", MotionGate1358);
        r.Add("motion gate (morning): corpse use -> the idle peace swap waits for the UseDone and then the turn's animation", MotionGateMorning);
        r.Add("motion gate: 4 s of 'animating' is taken as stuck (logged once, open until it reads clear); an emergency heal waits 1.5 s at most", MotionGateLimits);
        r.Add("motion gate: melee/missile attack repeats are not held by the animation, peace/magic combat starts are; off with the pacing", MotionGateAttackExempt);
        r.Add("motion gate (looting): a corpse open waits while the character animates, then goes", MotionGateLooting);
        r.Add("motion gate (items): the item managers' busy count includes the animation", MotionGateItems);
    }

    // ── Pacer rig ───────────────────────────────────────────────────────────────

    private static long _t;

    /// <summary>Turns the pacing on with RynthAi's use recorder and a hand-moved clock.</summary>
    private static void PaceOn()
    {
        _t = 10_000_000;
        ActionPacer.Reset();
        ActionPacer.Enabled = true;
        ActionPacer.NowMs = () => _t;
        UseAudit.Reset("RynthAi", () => true);
        UseAudit.NowMs = () => _t;
        MoveAudit.Reset();
        MoveAudit.NowMs = () => _t;
        var rec = typeof(RynthAiPlugin).GetMethod("RecordUseForPacing", Any)!;
        UseAudit.AfterSend = (h, u) => rec.Invoke(null, new object[] { h, u });
    }

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

    private static void Paced(Action body)
    {
        PaceOn();
        try { body(); }
        finally { PaceOff(); }
    }

    private static RynthCoreHost World(int combatMode = 2)
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;      // UseObject, GetCurrentCombatMode
        FakeHost.SalvageCalls = true;     // GetObjectWielderInfo
        FakeHost.ServerGates = true;      // GetUseDoneSeq
        FakeHost.LastUseDoneCalls = true;
        FakeHost.UseOnCalls = true;
        FakeHost.CombatModeCalls = true;
        FakeHost.StopCalls = true;
        FakeHost.BusyCalls = true;
        FakeHost.CombatModeValue = combatMode;
        FakeHost.Names[Player] = Me;
        FakeHost.Names[Corpse] = "Corpse of Olthoi Swarm Worker";
        FakeHost.Names[Kit] = "Peerless Healing Kit";
        FakeHost.Names[Wand] = "Academy Wand";
        FakeHost.Bitfields[Corpse] = 0x2000;
        FakeHost.Containers[Kit] = Pack;
        FakeHost.Containers[Wand] = Pack;
        return FakeHost.Create(Player);
    }

    private static bool Logged(string part) => FakeHost.Logs.Any(l => l.Contains(part, StringComparison.Ordinal));

    // ── The pacer ───────────────────────────────────────────────────────────────

    private static void UseAnswers() => Paced(() =>
    {
        var host = World();
        Check.True(host.UseFor(Corpse, "Loot", "open the claimed corpse"), "the open goes out");
        var a = ActionPacer.Current(host);
        Check.True(a != null && a.Kind == PacedKind.WorldUse, "a corpse is a world use the server walks to");
        Check.False(ActionPacer.MayStart(host, "Combat", HealNeed.None, out string why), "nothing else starts meanwhile");
        Check.True(why.Contains("Corpse of Olthoi Swarm Worker") && why.Contains("RynthAi/Loot"), "why names the corpse and the sender: " + why);
        _t += 690;   // the healthy median answer
        Check.True(ActionPacer.Current(host) != null, "still unanswered at 690 ms");
        FakeHost.UseDone(0);
        Check.True(ActionPacer.Current(host) == null, "its UseDone answers it");
        Check.True(ActionPacer.MayStart(host, "Combat", HealNeed.None, out _), "the next action may go");

        // A kit on yourself is an item use; "You're too busy" (0x1D) is an answer as well.
        Check.True(host.UseOnFor(Kit, Player, "Buff", "vitals: healing kit on yourself"), "the kit goes out");
        Check.True(ActionPacer.Current(host)?.Kind == PacedKind.ItemUse, "a kit on yourself is an item use");
        FakeHost.UseDone(TooBusy);
        Check.True(ActionPacer.Current(host) == null, "a refusal answers it (the server is busy with something else; the caller backs off)");

        // A wield gets no UseDone at all (0 of 18 wand wields on 10-05/10-06): it answers by wielding.
        Check.True(host.UseFor(Wand, "Buff", "buffing: wield the wand"), "the wield goes out");
        Check.True(ActionPacer.Current(host)?.Kind == PacedKind.ItemUse, "an item use");
        _t += 400;
        Check.True(ActionPacer.Current(host) != null, "not wielded yet: waiting");
        FakeHost.Wielded[Wand] = 0x1000000;
        Check.True(ActionPacer.Current(host) != null, "wielded: a short settle first");
        _t += ActionPacer.WieldSettleMs;
        Check.True(ActionPacer.Current(host) == null, "wielded and settled: answered");
        Check.Eq(ActionPacer.TimedOut, 0L, "no time limit was needed");
    });

    private static void OnsetModeChangeHeld() => Paced(() =>
    {
        // 06:18:44.549 corpse use in melee stance, 06:18:44.552 RequestCombatMode(peace).
        var host = World(combatMode: 2);
        host.UseFor(Corpse, "Loot", "open the claimed corpse");
        _t += 3;
        Check.False(host.ChangeCombatModeFor(CombatMode.NonCombat, "Combat", "idle: no target"), "3 ms later: the peace swap is held");
        _t += 674;
        Check.False(host.ChangeCombatModeFor(CombatMode.NonCombat, "Combat", "idle: no target"), "677 ms later (10-05 13:18:31): still held");
        Check.Eq(FakeHost.CombatModeRequests.Count, 0, "no mode change reached the server while the use was unanswered");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("change to Peace mode held")), 1, "logged once");

        FakeHost.UseDone(0);
        Check.True(host.ChangeCombatModeFor(CombatMode.NonCombat, "Combat", "idle: no target"), "answered: the swap goes out");
        Check.Eq(string.Join(",", FakeHost.CombatModeRequests), "1", "one request, to peace");
        Check.True(ActionPacer.Current(host)?.Kind == PacedKind.CombatMode, "the stance change is now the action the server is answering");

        // The next corpse waits for the stance: the client reads peace, then the animation.
        FakeHost.CombatModeValue = 1;
        _t += 500;
        Check.False(ActionPacer.MayStart(host, "Loot", HealNeed.None, out _), "500 ms: the stance animation is still running");
        _t += ActionPacer.ModeSettleMs - 500;
        Check.True(ActionPacer.MayStart(host, "Loot", HealNeed.None, out _), "800 ms: settled, the next open may go");

        // No request for the mode the client already reads (the engine pushed a raw 0x53 anyway).
        FakeHost.CombatModeRequests.Clear();
        Check.False(host.ChangeCombatModeFor(CombatMode.NonCombat, "Loot/Wedge", "loot wedge reset"), "already in peace: nothing to send");
        Check.Eq(FakeHost.CombatModeRequests.Count, 0, "nothing sent");
        Check.True(host.ChangeCombatModeFor(CombatMode.NonCombat, "Command", "/ra panic", force: true), "a typed command forces it");
        Check.Eq(FakeHost.CombatModeRequests.Count, 1, "forced: sent");
    });

    private static void MovesHeld() => Paced(() =>
    {
        var host = World();
        host.UseFor(Corpse, "Loot", "open the claimed corpse");
        Check.False(host.SetAutoRunBy("Nav", true), "nav can't start running");
        Check.False(host.TurnToHeadingBy("Nav", 90f), "nor turn");
        Check.False(host.SetMotionBy("Combat", 0x6500000D, true), "nor start a turn motion");
        Check.False(host.StopCompletelyBy("Arbiter"), "a stop with nothing of ours moving is a redundant movement packet: held");
        Check.False(host.SetMotionBy("Combat", 0x6500000D, false), "Combat's turn-off with no turn on: held");
        Check.Eq(FakeHost.AutoRunCalls.Count, 0, "no autorun reached the client");
        Check.Eq(FakeHost.Stops, 0, "no stop either");
        Check.True(Logged("held (move/turn): the server is still answering the use of"), "the hold is logged");

        Check.True(host.SetAutoRunBy("Buff/Nudge", true) && host.SetAutoRunBy("Buff/Nudge", false), "the held-cast nudge is deliberate: it goes through");
        Check.True(host.StopCompletelyBy("MacroStop"), "a macro stop goes through");

        FakeHost.UseDone(0);
        Check.True(host.SetAutoRunBy("Nav", true), "answered: nav runs again");
        host.UseFor(CorpseB, "Loot", "open the claimed corpse");
        FakeHost.Names[CorpseB] = "Corpse of Olthoi Swarm Drone";
        Check.True(host.StopCompletelyBy("Arbiter"), "while we really run, a stop always goes through");
        Check.False(MoveAudit.OwnMovementActive, "and we are not moving any more");
    });

    private static void TimeLimits() => Paced(() =>
    {
        var host = World();
        void Expect(PacedKind kind, long limit, Action send)
        {
            long before = ActionPacer.TimedOut;
            send();
            Check.True(ActionPacer.Current(host)?.Kind == kind, $"{kind}: outstanding");
            _t += limit - 1;
            Check.True(ActionPacer.Current(host) != null, $"{kind}: still waiting 1 ms before {limit} ms");
            _t += 1;
            Check.True(ActionPacer.Current(host) == null, $"{kind}: the time limit lets the next action go");
            Check.Eq(ActionPacer.TimedOut, before + 1, $"{kind}: counted");
        }
        Expect(PacedKind.WorldUse, ActionPacer.WorldUseMaxMs, () => host.UseFor(Corpse, "Loot", "open"));
        Expect(PacedKind.ItemUse, ActionPacer.ItemUseMaxMs, () => host.UseOnFor(Kit, Player, "Buff", "kit"));
        Expect(PacedKind.Cast, ActionPacer.CastMaxMs, () => host.CastSpellFor(Player, 1426, "Buff", "Focus Self VI"));
        FakeHost.CombatModeValue = 2;
        Expect(PacedKind.CombatMode, ActionPacer.ModeMaxMs, () => host.ChangeCombatModeFor(CombatMode.Magic, "Buff", "wand"));
        Check.True(Logged("no answer from the server to the cast of Focus Self VI") && Logged("nothing cleared"), "each is logged, and says nothing was cleared");
        Check.Eq(FakeHost.ForceResets, 0, "the client's busy count was never force-cleared");
    });

    private static void HealRules() => Paced(() =>
    {
        var host = World();
        host.UseFor(Corpse, "Loot", "open");
        _t += 900;
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.Heal, out _), "a heal 900 ms behind an unanswered corpse use waits");
        _t += 3000;
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.Heal, out _),
            "a plain heal still waits at 3.9 s: on ACE any motion during the server's turn to the corpse stops that turn for good");
        FakeHost.UseDone(0);
        Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.Heal, out _), "the use is answered: the heal goes");

        host.UseFor(Corpse, "Loot", "open");
        _t += 299;
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.Emergency, out _), "at Emergency Heal At: not before 300 ms");
        _t += 1;
        Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.Emergency, out _), "at Emergency Heal At: 300 ms");
        Check.True(Logged("the heal goes ahead of the use of"), "logged");
        Check.False(ActionPacer.MayStart(host, "Combat", HealNeed.None, out _), "anything else still waits");

        FakeHost.UseDone(0);
        host.CastSpellFor(Player, 1426, "Buff", "Focus Self VI");
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.Heal, out _), "a cast before its result: the heal waits (the server would refuse it)");
        ActionPacer.NoteCastResult();
        Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.Heal, out _), "its result is in: one heal may queue behind the recoil");
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.Heal, out _), "only one");
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "a buff waits for the UseDone");

        FakeHost.UseDone(0);
        FakeHost.CombatModeValue = 2;
        host.ChangeCombatModeFor(CombatMode.Magic, "Buff", "wand wielded: Magic mode");
        _t += 60;
        FakeHost.CombatModeValue = 8;
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.Emergency, out _), "the client reads Magic: not yet");
        _t += 299;
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.Emergency, out _), "299 ms after the read: not yet");
        _t += 1;
        Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.Emergency, out _), "300 ms after the read: the heal goes");
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "a buff waits for the full 800 ms settle");
    });

    // ── BuffManager ──────────────────────────────────────────────────────────────

    private sealed class BuffRig
    {
        public DateTime T0 = new(2026, 10, 6, 7, 3, 23);
        public DateTime NowAt;
        public readonly BuffManager B;
        public readonly LegacyUiSettings S;
        public readonly PlayerVitalsCache V;
        public RynthCoreHost Host;

        public BuffRig(int combatMode = CombatMode.Magic)
        {
            Host = World(combatMode);
            FakeHost.Containers.Clear();
            foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
            FakeHost.Skills[(Player, 33)] = (400, 2);
            Host = FakeHost.Create(Player);
            S = new LegacyUiSettings
            {
                IsMacroRunning = true, HealAt = 0, TopOffHP = 0, EmergencyHealAt = 0, StaminaToHealthAt = 0,
                GetManaAt = 50, TopOffMana = 50, RestamAt = 0, TopOffStam = 0, UsePotions = false,
                EnableBuffing = false, SpellCastIntervalMs = 0,
            };
            var skills = new CharacterSkills(Host);
            skills.SetPlayerId(Player);
            var sm = new SpellManager(Host, S);
            sm.InitializeNatively();
            sm.SetCharacterSkills(skills);
            V = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 100, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 10 };
            B = new BuffManager(Host, S, sm, V);
            B.SetCharacterSkills(skills);
            NowAt = T0;
            B.Clock = () => NowAt;
            B.MarkLiveBuffsReadyForTests();
            _t = 10_000_000;
            ActionPacer.NowMs = () => _t + (long)(NowAt - T0).TotalMilliseconds;
            MoveAudit.NowMs = ActionPacer.NowMs;
        }

        public void At(double seconds) => NowAt = T0.AddSeconds(seconds);
        public void Chat(string text, int type = 0) => B.OnChatWindowText(text, type);
        public string CastName => SpellTable.ById(FakeHost.Casts.Last().SpellId)?.Name ?? "";
    }

    private static void CastWaitsForUseDone()
    {
        // Without the pacing (as before): the result line ends the cast, and with mana still low the
        // next Stamina to Mana goes straight out - on a server holding the first one, "too busy".
        int Script(bool pacing)
        {
            PaceOn();
            ActionPacer.Enabled = pacing;
            try
            {
                var r = new BuffRig();
                r.At(0); r.B.OnHeartbeat();
                Check.Eq(FakeHost.Casts.Count, 1, $"pacing={pacing}: Stamina to Mana goes out");
                string name = r.CastName;
                r.At(0.1); r.Chat("You say, \"Puish Zharil\"", 17);
                // 07:03:26.799: the result is in 3.3 s later; the server's UseDone never comes.
                r.At(2.3); r.Chat($"You cast {name} on yourself and lose 35 points of stamina and also gain 47 points of mana");
                for (double s = 2.4; s < 4.0; s += 0.1) { r.At(s); r.B.OnHeartbeat(); }
                int before = FakeHost.Casts.Count;
                if (pacing)
                {
                    Check.True(r.B.PacerHolds > 0, "the vitals step waited for the server's answer");
                    FakeHost.UseDone(0);
                    r.At(4.0); r.B.OnHeartbeat();
                    Check.Eq(FakeHost.Casts.Count, 2, "the UseDone arrives: the next cast goes at once");
                }
                return before;
            }
            finally { PaceOff(); }
        }
        int unpaced = Script(pacing: false);
        int paced = Script(pacing: true);
        Check.True(unpaced >= 2, $"without pacing a second cast went out before the server answered ({unpaced} casts by 4 s)");
        Check.Eq(paced, 1, "with pacing: one cast until the server answers it");
    }

    private static void HealLatencyKept()
    {
        PaceOn();
        try
        {
            // A heal behind a corpse use the server walks/turns to: it waits for the use's answer (a cast
            // during the server's turn stops the turn for good on ACE), then goes at once.
            var r = new BuffRig();
            r.S.HealAt = 60; r.S.TopOffHP = 60; r.S.GetManaAt = 0; r.S.TopOffMana = 0;
            r.V.CurrentHealth = 50; r.V.CurrentMana = 100;
            r.At(0);
            r.Host.UseFor(Corpse, "Loot", "open the claimed corpse");
            r.B.BusyCount = 1;   // the open's +1 on the client's count, as the engine reports it
            for (double s = 0; s < 1.45; s += 0.1) { r.At(s); r.B.OnHeartbeat(); }
            Check.Eq(FakeHost.Casts.Count, 0, "behind the unanswered open: no heal");
            FakeHost.UseDone(0);
            r.B.BusyCount = 0;   // the engine's UseDone decrement
            r.At(1.5); r.B.OnHeartbeat();
            Check.Eq(FakeHost.Casts.Count, 1, "the open is answered: the heal goes on the next heartbeat");
            string healName = SpellTable.ById(FakeHost.Casts[0].SpellId)?.Name ?? "";
            Check.True(healName.StartsWith("Heal Self") || healName == "Adja's Intervention", "and it is the heal spell: " + healName);

            // Nothing outstanding: the heal goes on the first heartbeat, as before.
            PaceOff(); PaceOn();
            var q = new BuffRig();
            q.S.HealAt = 60; q.S.TopOffHP = 60; q.S.GetManaAt = 0; q.S.TopOffMana = 0;
            q.V.CurrentHealth = 50; q.V.CurrentMana = 100;
            q.At(0); q.B.OnHeartbeat();
            Check.Eq(FakeHost.Casts.Count, 1, "nothing outstanding: Heal Self on the first heartbeat");

            // The emergency settle after a stance change (CastSafetyTests.EmergencyStanceWait) with the
            // pacing on: the stance change is the outstanding action, and the heal still goes 300 ms
            // after the client reads Magic.
            PaceOff(); PaceOn();
            var e = new BuffRig(CombatMode.NonCombat);
            e.S.EmergencyHealAt = 30; e.S.HealAt = 60; e.S.TopOffHP = 60; e.S.GetManaAt = 0; e.S.TopOffMana = 0;
            e.V.CurrentHealth = 20; e.V.CurrentMana = 100;
            e.At(0); e.B.OnHeartbeat();
            Check.Eq(string.Join(",", FakeHost.CombatModeRequests), "8", "out of Magic: the stance request goes out");
            Check.Eq(FakeHost.Casts.Count, 0, "no cast before the stance");
            FakeHost.CombatModeValue = CombatMode.Magic;
            e.At(0.06); e.B.OnHeartbeat();
            e.At(0.30); e.B.OnHeartbeat();
            Check.Eq(FakeHost.Casts.Count, 0, "240 ms after Magic: still settling");
            e.At(0.37); e.B.OnHeartbeat();
            Check.Eq(FakeHost.Casts.Count, 1, "310 ms after Magic (370 ms after the request): Heal Self goes, as without pacing");
        }
        finally { PaceOff(); }
    }

    // ── Looting ──────────────────────────────────────────────────────────────────

    private static void LootWorld()
    {
        FakeHost.Reset();
        FakeHost.SalvageCalls = true;
        FakeHost.WeaponCalls = true;
        FakeHost.ServerGates = true;
        FakeHost.LastUseDoneCalls = true;
        FakeHost.StopCalls = true;
        FakeHost.BusyCalls = true;
        FakeHost.Names[Player] = Me;
        FakeHost.Positions[Player] = Me0;
        FakeHost.PlayerPose = Me0;
        foreach ((uint id, float dx) in new[] { (Corpse, 1.0f), (CorpseB, 1.5f) })
        {
            FakeHost.Names[id] = "Corpse of Olthoi Swarm Worker";
            FakeHost.ItemTypes[id] = ItemTypeContainer;
            FakeHost.Strings[(id, LongDescKey)] = $"Killed by {Me}.";
            FakeHost.NotAttackable.Add(id);
            FakeHost.Positions[id] = (Me0.Cell, Me0.X + dx, Me0.Y, Me0.Z);
        }
    }

    private sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new()
        {
            IsMacroRunning = true, EnableLooting = true, EnableCombat = false, LootOwnership = 0,
            LootContentSettleMs = 0, LootEmptyCorpseMs = 0, StopLootingWhenPackFull = false,
        };

        public Bot(RynthCoreHost host)
        {
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            Set("_dashboard", dash);
            var cache = new WorldObjectCache(host);
            cache.SetPlayerId(Player);
            Set("_objectCache", cache);
            Set("_playerId", Player);
            cache.OnCreateObject(Corpse);
            cache.OnCreateObject(CorpseB);
            cache.Tick();
            string profile = Path.Combine(Program.TempRoot, "action-pacing-test.json");
            new LootProfile { Name = "nothing" }.Save(profile);
            S.CurrentLootPath = profile;
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public T Get<T>(string f) => (T)typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P)!;
        public object? Call(string m, params object[] a) => typeof(RynthAiPlugin).GetMethod(m, Any)!.Invoke(P, a);
        public void Tick() { Set("_activity", BotActivity.Looting); Call("TickCorpseOpening"); }
    }

    /// <summary>The loot clock and the pacer clock on one fake time.</summary>
    private static void WithClock(Action<Func<long>, Action<long>> body)
    {
        long now = 50_000_000;
        var saved = RynthAiPlugin.CorpseClock;
        RynthAiPlugin.CorpseClock = () => now;
        ActionPacer.NowMs = () => now;
        UseAudit.NowMs = () => now;
        MoveAudit.NowMs = () => now;
        try { body(() => now, ms => now += ms); }
        finally { RynthAiPlugin.CorpseClock = saved; }
    }

    private static void LootOneOpenPerAnswer()
    {
        LootWorld();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        PaceOn();
        try
        {
            WithClock((now, advance) =>
            {
                for (int i = 0; i < 10 && FakeHost.Uses.Count == 0; i++) { bot.Tick(); advance(100); }
                Check.Eq(FakeHost.Uses.Count, 1, "the open goes out");
                // A long walk: no answer for 3 s. Before, the open was re-sent every LootOpenRetryMs,
                // and each re-send restarted the server's walk and failed the first open's callback.
                for (int i = 0; i < 30; i++) { bot.Tick(); advance(100); }
                Check.Eq(FakeHost.Uses.Count, 1, "not re-sent while the server hasn't answered");
                Check.True(Logged("[Pace] Loot waits: the use of"), "the wait is logged once");
                FakeHost.UseDone(0);
                bot.P.OnViewObjectContents(FakeHost.Uses.Last());
                Check.True(ActionPacer.Current(host) == null, "answered: nothing outstanding");
                // The empty corpse is done; the next one is opened - once, and only now.
                for (int i = 0; i < 20; i++) { bot.Tick(); advance(100); }
                Check.Eq(FakeHost.Uses.Count, 2, "the next corpse's open went out after the answer, once");
                Check.Eq(FakeHost.Uses.Last(), CorpseB, "it is the other corpse");
                Check.True(ActionPacer.Current(host)?.Id == CorpseB, "and it is the action the server is answering now");
                Check.Eq(FakeHost.ForceResets, 0, "no force-clear");
            });
        }
        finally { PaceOff(); }
    }

    private static void LootStallStillDetected()
    {
        LootWorld();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        PaceOn();
        try
        {
            WithClock((now, advance) =>
            {
                long answerAt = 0;
                int maxOutstanding = 0;
                void Step()
                {
                    int uses = FakeHost.Uses.Count;
                    bot.Tick();
                    if (FakeHost.Uses.Count > uses) answerAt = now() + 60;
                    maxOutstanding = Math.Max(maxOutstanding, FakeHost.Uses.Count - FakeHost.UseDoneSeq);
                    advance(100);
                    if (answerAt != 0 && now() >= answerAt) { FakeHost.UseDone(0); answerAt = 0; }
                }
                int steps = 0;
                while (FakeHost.Chat.Count(c => c.Contains("isn't letting this character use objects")) == 0 && steps++ < 900) Step();
                Check.Eq(FakeHost.Chat.Count(c => c.Contains("isn't letting this character use objects")), 1,
                    $"the stall is found and said once ({steps / 10.0} s)");
                Check.True(steps <= 600, $"within a minute ({steps / 10.0} s)");
                Check.True(maxOutstanding <= 1, $"never more than one open unanswered (max {maxOutstanding})");
            });
        }
        finally { PaceOff(); }
    }

    // ── Combat ───────────────────────────────────────────────────────────────────

    private static void CombatPeaceWaitsForKit() => Paced(() =>
    {
        var host = World(combatMode: 2);
        FakeHost.Positions[Player] = Me0;
        FakeHost.PlayerPose = Me0;
        var s = new LegacyUiSettings { IsMacroRunning = true, EnableCombat = true, PeaceModeWhenIdle = true, MonsterRange = 50 };
        var cache = new WorldObjectCache(host);
        cache.SetPlayerId(Player);
        var combat = new CombatManager(host, s, cache);
        combat.SetPlayerId(Player);

        // 07:00:11.8: a kit on yourself in melee stance; the server answers 2.4 s later.
        host.UseOnFor(Kit, Player, "Buff", "vitals: healing kit on yourself");
        for (int i = 0; i < 5; i++) { combat.Think(); _t += 400; }
        Check.Eq(FakeHost.CombatModeRequests.Count, 0, "no peace swap while the kit is unanswered");
        FakeHost.UseDone(0);
        combat.Think();
        Check.Eq(string.Join(",", FakeHost.CombatModeRequests), "1", "the kit answered: the idle swap goes out");
    });

    private static void ArbiterStopOnlyWhenMoving()
    {
        LootWorld();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        PaceOn();
        try
        {
            WithClock((now, advance) =>
            {
                bot.Call("StopNavFor", BotActivity.Looting);
                Check.Eq(FakeHost.Stops, 0, "nothing of ours moving: no StopCompletely (another plugin's movement isn't cut off)");
                host.SetAutoRunBy("Nav", true);
                bot.Call("StopNavFor", BotActivity.Buffing);
                Check.Eq(FakeHost.Stops, 1, "our autorun on: the stop goes out");
                ActionPacer.Enabled = false;
                bot.Call("StopNavFor", BotActivity.Combat);
                Check.Eq(FakeHost.Stops, 2, "pacing off: as before, always");
            });
        }
        finally { PaceOff(); }
    }

    // ── Busy mirror ──────────────────────────────────────────────────────────────

    private static void MirrorResetOnly()
    {
        // The engine's count is the owner: a mirror left up follows it down after 1.5 s.
        LootWorld();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        WithClock((now, advance) =>
        {
            FakeHost.BusyStateValue = 0;   // the engine has seen the UseDones
            bot.Set("_busyCount", 2);
            bot.Set("_busyCountBecamePositiveAt", now());
            bot.Call("CheckBusyTimeout");
            Check.Eq(bot.Get<int>("_busyCount"), 2, "a fresh disagreement waits (events may be on their way)");
            advance(1_600);
            bot.Call("CheckBusyTimeout");
            Check.Eq(bot.Get<int>("_busyCount"), 0, "1.6 s later: the mirror follows the engine's 0");
            Check.True(Logged("following the engine's count"), "logged");

            FakeHost.BusyStateValue = 1;   // the server owes an answer: the mirror follows that too
            bot.Call("CheckBusyTimeout"); advance(1_600); bot.Call("CheckBusyTimeout");
            Check.Eq(bot.Get<int>("_busyCount"), 1, "and up, while the server still owes an answer");
            advance(30_000); bot.Call("CheckBusyTimeout");
            Check.Eq(bot.Get<int>("_busyCount"), 1, "never reset on a timer while the engine says busy");
            Check.Eq(FakeHost.ForceResets, 0, "the client's busy count is left to the client");
        });

        // An engine without GetBusyState: the old timer, resetting the mirror only.
        LootWorld();
        FakeHost.BusyCalls = false;
        host = FakeHost.Create(Player);
        bot = new Bot(host);
        WithClock((now, advance) =>
        {
            bot.Set("_busyCount", 2);
            bot.Set("_busyCountBecamePositiveAt", now());
            advance(10_500);
            bot.Call("CheckBusyTimeout");
            Check.Eq(bot.Get<int>("_busyCount"), 0, "old engine: our mirror is reset after 10 s");
            Check.True(Logged("resetting the mirror only"), "and the line says so");
        });
    }

    // ── The motion gate (2026-10-06 afternoon, ops\overnight\2026-10-06-turnto-stall.md) ──────────
    // Nothing starts while the character is still animating (the client's motion queue, the engine's
    // GetCastBusyState); FakeHost.CastBusy = 1 stands for "animating".

    // 16:45:37: the wand's use went 110 ms after a MeleeAttack, before the swing animated.
    private static void SwingNotStarted() => Paced(() =>
    {
        var host = World();
        FakeHost.CastBusy = 0;
        ActionPacer.NoteAttackSent();
        _t += 110;
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.None, out string why), "the wand waits: the swing hasn't animated yet");
        Check.True(why.Contains("swing"), "why: " + why);
        Check.True(ActionPacer.MayStart(host, "Combat", HealNeed.None, out _, gestureGate: false), "the next attack isn't held");
        _t += 200;
        FakeHost.CastBusy = 1;
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "swinging: still waits");
        _t += 700;
        FakeHost.CastBusy = 0;
        Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "the swing played: the wand goes");

        ActionPacer.NoteAttackSent();
        _t += 600;
        Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.Emergency, out _), "an emergency heal waits at most 500 ms");
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "anything else still waits");
        _t += 1000;
        Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "a swing that never animates holds at most 1.5 s");
        Check.True(ActionPacer.SwingHoldCount >= 2, "holds counted");
    });

    private static void MotionGate1429() => Paced(() =>
    {
        var host = World();
        // 14:29:21.435 the swing; 22.380 AttackDone (the attack sequence is over, the swing still plays).
        FakeHost.CastBusy = 1;
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.Heal, out string why), "22.41: the kit waits for the swing's animation");
        Check.True(why.Contains("still animating"), "why says so: " + why);
        Check.True(Logged("[Pace] Buff waits: the character is still animating"), "logged once");
        _t += 300;
        FakeHost.CastBusy = 0;
        Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.Heal, out _), "the swing is over: the kit goes");
        host.UseOnFor(Kit, Player, "Buff", "vitals: healing kit on yourself");
        FakeHost.CastBusy = 1;   // the kit's SkillHealSelf
        _t += 2400;
        Check.False(ActionPacer.MayStart(host, "Loot", HealNeed.None, out _), "the corpse open waits for the kit's answer");
        FakeHost.UseDone(0);     // 24.794 the kit's UseDone; the client still plays the kit's last frames
        Check.False(ActionPacer.MayStart(host, "Loot", HealNeed.None, out why), "answered, but still animating: the open waits");
        Check.True(why.Contains("still animating"), "why: " + why);
        _t += 120;
        FakeHost.CastBusy = 0;
        Check.True(ActionPacer.MayStart(host, "Loot", HealNeed.None, out _), "at rest: the corpse open goes");
        Check.True(ActionPacer.GestureHoldCount >= 2, "holds counted");
    });

    private static void MotionGate1358() => Paced(() =>
    {
        var host = World(combatMode: 2);
        // 13:58:54.210 the wand is wielded in melee stance (its use is an item use, answered by wielding).
        host.UseFor(Wand, "Buff", "buffing: switch to the wand");
        FakeHost.CastBusy = 1;                       // the unwield/wield and stance motion
        FakeHost.Wielded[Wand] = 0x01000000;
        _t += 50;
        ActionPacer.Current(host);                   // the wield is first seen done
        _t += 200;
        Check.True(ActionPacer.Current(host) == null, "the wield reads done: answered");
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "the cast waits: still animating");
        Check.False(host.ChangeCombatModeFor(CombatMode.Magic, "Buff", "wand wielded: Magic mode"), "the Magic stance waits too");
        Check.Eq(FakeHost.CombatModeRequests.Count, 0, "no mode request sent");
        Check.True(Logged("[Pace] mode:Buff waits: the character is still animating"), "logged");
        _t += 900;
        FakeHost.CastBusy = 0;
        Check.True(host.ChangeCombatModeFor(CombatMode.Magic, "Buff", "wand wielded: Magic mode"), "at rest: Magic mode");
        Check.Eq(string.Join(",", FakeHost.CombatModeRequests), "8", "sent");
        FakeHost.CombatModeValue = 8;
        FakeHost.CastBusy = 1;                       // the stance animation
        _t += ActionPacer.ModeSettleMs;
        Check.True(ActionPacer.Current(host) == null, "the stance is read and settled");
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "but the client still animates it: the cast waits");
        FakeHost.CastBusy = 0;
        Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "at rest: the cast goes");
    });

    private static void MotionGateMorning() => Paced(() =>
    {
        var host = World(combatMode: 2);
        FakeHost.Positions[Player] = Me0;
        FakeHost.PlayerPose = Me0;
        var s = new LegacyUiSettings { IsMacroRunning = true, EnableCombat = true, PeaceModeWhenIdle = true, MonsterRange = 50 };
        var cache = new WorldObjectCache(host);
        cache.SetPlayerId(Player);
        var combat = new CombatManager(host, s, cache);
        combat.SetPlayerId(Player);

        // 06:18:44.549 the corpse use in melee stance; the idle peace swap came 3 ms later.
        host.UseFor(Corpse, "Loot", "open the claimed corpse");
        _t += 3;
        combat.Think();
        Check.Eq(FakeHost.CombatModeRequests.Count, 0, "3 ms later: no peace swap (the use is unanswered)");
        FakeHost.CastBusy = 1;   // the turn to the corpse
        _t += 400;
        FakeHost.UseDone(0);
        typeof(CombatManager).GetField("_lastPeaceAttempt", Any)!.SetValue(combat, DateTime.MinValue);
        combat.Think();
        Check.Eq(FakeHost.CombatModeRequests.Count, 0, "answered, but the character still animates: no peace swap");
        _t += 200;
        FakeHost.CastBusy = 0;
        typeof(CombatManager).GetField("_lastPeaceAttempt", Any)!.SetValue(combat, DateTime.MinValue);
        combat.Think();
        Check.Eq(string.Join(",", FakeHost.CombatModeRequests), "1", "at rest: the idle swap goes out");
    });

    private static void MotionGateLimits() => Paced(() =>
    {
        var host = World();
        FakeHost.CastBusy = 1;
        Check.False(ActionPacer.MayStart(host, "Loot", HealNeed.None, out _), "animating: held");
        _t += 1499;
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.Emergency, out _), "an emergency heal waits up to 1.5 s");
        _t += 1;
        Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.Emergency, out _), "at 1.5 s the emergency heal goes");
        Check.True(Logged("emergency heal after 1500 ms of animation - going on"), "logged");
        Check.False(ActionPacer.MayStart(host, "Loot", HealNeed.None, out _), "anything else still waits");
        _t += ActionPacer.GestureHoldMaxMs - 1500;
        Check.True(ActionPacer.MayStart(host, "Loot", HealNeed.None, out _), "4 s: taken as stuck, the action goes");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("taken as stuck, going on")), 1, "logged once");
        _t += 5000;
        Check.True(ActionPacer.MayStart(host, "Combat", HealNeed.None, out _), "still open while it reads busy");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("taken as stuck, going on")), 1, "not logged again");
        Check.Eq(ActionPacer.GestureLimits, 2L, "two limits counted");
        FakeHost.CastBusy = 0;
        Check.True(ActionPacer.MayStart(host, "Loot", HealNeed.None, out _), "clear");
        Check.True(Logged("animation queue reads clear again"), "the recovery is logged");
        FakeHost.CastBusy = 1;
        Check.False(ActionPacer.MayStart(host, "Loot", HealNeed.None, out _), "a new animation: held again (a fresh 4 s)");
    });

    private static void MotionGateAttackExempt() => Paced(() =>
    {
        var host = World(combatMode: 2);
        FakeHost.CastBusy = 1;   // the last swing still plays
        Check.True(ActionPacer.MayStart(host, "Combat", HealNeed.None, out _, gestureGate: false), "a melee attack repeat is not held by the animation");
        Check.False(ActionPacer.MayStart(host, "Combat", HealNeed.None, out _), "anything gated is");
        host.UseFor(Corpse, "Loot", "open");
        Check.False(ActionPacer.MayStart(host, "Combat", HealNeed.None, out _, gestureGate: false), "but an attack still waits for an unanswered corpse use (the server's turn)");
        FakeHost.UseDone(0);
        Check.True(ActionPacer.Current(host) == null, "the use is answered");
        ActionPacer.Enabled = false;
        Check.True(ActionPacer.MayStart(host, "Loot", HealNeed.None, out _), "pacing off: no gate");
        Check.False(ActionPacer.GestureHolds(host, "Loot", HealNeed.None, out _), "pacing off: no animation hold");
        ActionPacer.Enabled = true;
        FakeHost.ServerGates = false;
        var old = FakeHost.Create(Player);
        Check.True(ActionPacer.MayStart(old, "Loot", HealNeed.None, out _), "an engine without the gate: no opinion");
    });

    private static void MotionGateLooting()
    {
        LootWorld();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        PaceOn();
        try
        {
            WithClock((now, advance) =>
            {
                FakeHost.CastBusy = 1;
                for (int i = 0; i < 20; i++) { bot.Tick(); advance(100); }
                Check.Eq(FakeHost.Uses.Count, 0, "no open while the character animates");
                Check.True(Logged("[Pace] Loot waits: the character is still animating"), "logged");
                FakeHost.CastBusy = 0;
                for (int i = 0; i < 10 && FakeHost.Uses.Count == 0; i++) { bot.Tick(); advance(100); }
                Check.Eq(FakeHost.Uses.Count, 1, "at rest: the open goes");
            });
        }
        finally { PaceOff(); }
    }

    private static void MotionGateItems()
    {
        LootWorld();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        PaceOn();
        try
        {
            int Busy() => (int)typeof(RynthAiPlugin).GetProperty("PacedBusy", Any)!.GetValue(bot.P)!;
            Check.Eq(Busy(), 0, "at rest, nothing outstanding: 0");
            FakeHost.CastBusy = 1;
            Check.Eq(Busy(), 1, "animating: +1 (a pack move of a wielded item, a stone, a summon waits)");
            FakeHost.CastBusy = 0;
            Check.Eq(Busy(), 0, "at rest again: 0");
        }
        finally { PaceOff(); }
    }
}
