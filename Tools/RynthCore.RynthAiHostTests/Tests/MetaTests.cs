using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;
using C = RynthCore.Plugin.RynthAi.LegacyUi.MetaConditionType;
using A = RynthCore.Plugin.RynthAi.LegacyUi.MetaActionType;

namespace RynthCore.RynthAiHostTests.Tests;

// Metas: MetaManager.Think() over in-memory rules. Every condition type, nested conditions,
// timers (seconds in state, the chat window, the watchdog) on a fake clock, and the
// state-transition rules (the HasFired latch, a state change ending the tick, call/return).
// Rules report that they fired through an expression action that sets a variable.
internal static class MetaTests
{
    private const uint Player = 0x50000A01;
    private static readonly DateTime T0 = new(2026, 9, 30, 2, 0, 0);

    public static void Register(Runner r)
    {
        r.Add("meta: nothing runs unless the macro runs with metas on", Gating);
        r.Add("meta: a rule fires once per state entry (HasFired latch)", Latch);
        r.Add("meta: a state change ends that tick's rule pass", StateChangeEndsTick);
        r.Add("meta: rules run in list order; several can fire in one tick", ListOrder);
        r.Add("meta: only the current state's rules run; names ignore case", CurrentStateOnly);
        r.Add("meta: disabled rules never run; re-enabling needs the version bump", DisabledRules);
        r.Add("meta: All / Any / Not and nesting", Composites);
        r.Add("meta: vital conditions (value, percent, death)", Vitals);
        r.Add("meta: vitae penalty", Vitae);
        r.Add("meta: seconds in state on a fake clock", SecondsInState);
        r.Add("meta: chat message and capture within one second", Chat);
        r.Add("meta: pack slots and inventory item count", PackAndInventory);
        r.Add("meta: monster counts by name, by priority, and none near", Monsters);
        r.Add("meta: nav route empty and distance to the route", NavConditions);
        r.Add("meta: landblock and landcell", LandblockLandcell);
        r.Add("meta: portal space entered / exited edges", Portal);
        r.Add("meta: vendor open", VendorOpen);
        r.Add("meta: time left on a spell", TimeLeftOnSpell);
        r.Add("meta: burden percentage", Burden);
        r.Add("meta: expression condition fails closed on errors", ExpressionCondition);
        r.Add("meta: need-to-buff with no buff manager, unknown condition types", NeedToBuffAndUnknown);
        r.Add("meta action: chat commands, /ra, /vt and /mt", ChatCommands);
        r.Add("meta action: SetMetaState, CallMetaState and ReturnFromCall", CallReturn);
        r.Add("meta action: All runs every child in order", AllAction);
        r.Add("meta action: embedded nav route", EmbeddedNav);
        r.Add("meta action: watchdog", Watchdog);
        r.Add("meta action: RA options, chat expression, expression, views", MiscActions);
        r.Add("meta: an exception in a tick is caught and forces a reset", ExceptionInTick);
        r.Add("meta: state snapshot", Snapshot);
        r.Add("meta: VendorClosed fires after a vendor closes", VendorClosedFires);   // was a known failure; fixed by ef68455
        r.Add("meta: InventoryItemCount counts stack sizes", InventoryCountsStacks);   // was a known failure; fixed by 716f16d
        r.KnownFailure("meta: SecondsInStateP keeps counting across a call and return",
            "SecondsInStateP_GE survives a macro stop/start since 1ee6145, but still restarts whenever the state is re-entered (SetState, Call, Return), as VTank documents it; RynthScript says it survives re-entry. Open compatibility call (2026-09-30 audit, question 16)",
            SecondsInStatePPersists);
        r.Add("meta: MainHealthPHE / MainManaPHE mean 'percent >='", PercentConditionsAreGE);   // was a known failure; fixed by a7c4618
        r.Add("meta: a negative vital threshold never holds", NegativeVitalThreshold);   // was a known failure; fixed by 6f72b67
    }

    // ── Rig ───────────────────────────────────────────────────────────────────

    private sealed class Rig
    {
        public readonly LegacyUiSettings S;
        public readonly PlayerVitalsCache V = new();
        public readonly MetaManager M;
        public readonly List<string> Ra = new();
        public DateTime Now = T0;

        public Rig(RynthCoreHost host = default, uint playerId = 0, params MetaRule[] rules)
        {
            S = new LegacyUiSettings { IsMacroRunning = true, EnableMeta = true, CurrentState = "Default" };
            S.MetaRules = rules.ToList();
            M = new MetaManager(S, host, V) { Clock = () => Now };
            M.SetPlayerId(playerId);
            M.SetRaCommandHandler(Ra.Add);
        }

        public void Tick(double seconds = 0)
        {
            Now = Now.AddSeconds(seconds);
            M.Think();
        }

        public string Var(string name) => M.Expressions.Variables.TryGetValue(name, out string? v) ? v : "(unset)";
        public bool Has(string name) => M.Expressions.Variables.ContainsKey(name);

        /// <summary>Runs one tick with only <paramref name="cond"/> (plus a marker action) in the current
        /// state, freshly armed, and says whether it fired. Time does not move.</summary>
        public bool Holds(MetaRule cond)
        {
            cond.State = S.CurrentState;
            cond.Action = A.ExpressionAction;
            cond.ActionData = "setvar[__hit, 1]";
            S.MetaRules = new List<MetaRule> { cond };
            M.Expressions.Evaluate("clearvar[__hit]");
            S.ForceStateReset = true;
            M.Think();
            return Has("__hit");
        }

        public bool Holds(C type, string data) => Holds(Cond(type, data));
    }

    private static MetaRule Cond(C type, string data = "", params MetaRule[] children)
        => new() { Condition = type, ConditionData = data, Children = children.ToList() };

    private static MetaRule Rule(string state, C cond, string condData, A act, string actData)
        => new() { State = state, Condition = cond, ConditionData = condData, Action = act, ActionData = actData };

    private static MetaRule Mark(string state, C cond, string condData, string varName)
        => Rule(state, cond, condData, A.ExpressionAction, $"setvar[{varName}, getvar[{varName}]+1]");

    private static List<string> NavLines(int routeType, params (int Type, double EW, double NS, string[] Trailer)[] pts)
    {
        var lines = new List<string> { "uTank2 NAV 1.2", routeType.ToString(), pts.Length.ToString() };
        foreach (var p in pts)
        {
            lines.Add(p.Type.ToString());
            lines.Add(p.EW.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add(p.NS.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("0");
            lines.Add("0");
            lines.AddRange(p.Trailer);
        }
        return lines;
    }

    private static readonly string[] NoTrailer = Array.Empty<string>();

    // ── Think gating and transitions ──────────────────────────────────────────

    private static void Gating()
    {
        var rig = new Rig(rules: Mark("Default", C.Always, "", "n"));
        rig.S.IsMacroRunning = false;
        rig.Tick();
        Check.False(rig.Has("n"), "macro stopped");
        rig.S.IsMacroRunning = true;
        rig.S.EnableMeta = false;
        rig.Tick();
        Check.False(rig.Has("n"), "metas off");
        rig.S.EnableMeta = true;
        var keep = rig.S.MetaRules;
        rig.S.MetaRules = null!;
        rig.Tick();
        Check.False(rig.Has("n"), "no rule list");
        rig.S.MetaRules = keep;
        rig.Tick();
        Check.Eq(rig.Var("n"), "1", "running, enabled, with rules");
    }

    private static void Latch()
    {
        var rig = new Rig(rules: Mark("Default", C.Always, "", "n"));
        rig.Tick(); rig.Tick(1); rig.Tick(1);
        Check.Eq(rig.Var("n"), "1", "Always fired once across three ticks");
        rig.S.ForceStateReset = true;
        rig.Tick();
        Check.Eq(rig.Var("n"), "2", "a forced reset re-arms it");
        rig.S.IsMacroRunning = false;
        rig.Tick();
        rig.S.IsMacroRunning = true;
        rig.Tick();
        Check.Eq(rig.Var("n"), "3", "stopping and starting the macro re-arms it");
        // Operational state cycling (no ForceStateReset) does not re-arm.
        rig.S.CurrentState = "Combat";
        rig.Tick();
        rig.S.CurrentState = "Default";
        rig.Tick();
        Check.Eq(rig.Var("n"), "3", "leaving and coming back without a reset does not re-fire");
    }

    private static void StateChangeEndsTick()
    {
        var rig = new Rig(rules: new[]
        {
            Rule("Default", C.Always, "", A.SetMetaState, "Hunt"),
            Mark("Default", C.Always, "", "after"),
            Mark("Hunt", C.Always, "", "hunt"),
        });
        rig.Tick();
        Check.Eq(rig.S.CurrentState, "Hunt", "moved to Hunt");
        Check.False(rig.Has("after"), "the next Default rule did not run in that tick");
        Check.False(rig.Has("hunt"), "Hunt's rules wait for the next tick");
        rig.Tick();
        Check.Eq(rig.Var("hunt"), "1", "Hunt rule ran next tick");
        Check.False(rig.Has("after"), "Default rule never ran");

        // A SetMetaState to the state you are in still ends the pass and re-arms the state.
        var same = new Rig(rules: new[]
        {
            Mark("Default", C.Always, "", "n"),
            Rule("Default", C.SecondsInState_GE, "5", A.SetMetaState, "Default"),
        });
        same.Tick();
        same.Tick(5);
        same.Tick();
        Check.Eq(same.Var("n"), "2", "re-entering the same state re-fires its rules");
    }

    private static void ListOrder()
    {
        var rig = new Rig(rules: new[]
        {
            Rule("Default", C.Always, "", A.ExpressionAction, "setvar[o, getvar[o] + `a`]"),
            Rule("Default", C.Never, "", A.ExpressionAction, "setvar[o, getvar[o] + `x`]"),
            Rule("Default", C.Always, "", A.ExpressionAction, "setvar[o, getvar[o] + `b`]"),
            Rule("Default", C.Always, "", A.ExpressionAction, "setvar[o, getvar[o] + `c`]"),
        });
        rig.Tick();
        Check.Eq(rig.Var("o"), "0abc", "all true rules fired, top to bottom");
    }

    private static void CurrentStateOnly()
    {
        var rig = new Rig(rules: new[] { Mark("Hunt", C.Always, "", "hunt"), Mark("Default", C.Always, "", "def") });
        rig.S.CurrentState = "dEfAuLt";
        rig.Tick();
        Check.Eq(rig.Var("def"), "1", "state lookup ignores case");
        Check.False(rig.Has("hunt"), "other state's rule");
        rig.S.CurrentState = "Nowhere";
        rig.S.ForceStateReset = true;
        rig.Tick();
        Check.Eq(rig.Var("def"), "1", "a state with no rules does nothing");
    }

    private static void DisabledRules()
    {
        var rule = Mark("Default", C.Always, "", "n");
        rule.Enabled = false;
        var rig = new Rig(rules: rule);
        rig.Tick();
        Check.False(rig.Has("n"), "disabled");
        rule.Enabled = true;
        rig.S.ForceStateReset = true;
        rig.Tick();
        Check.False(rig.Has("n"), "re-enabled in place without a version bump: the index is not rebuilt yet");
        rig.S.MetaRulesStructuralVersion++;
        rig.S.ForceStateReset = true;
        rig.Tick();
        Check.Eq(rig.Var("n"), "1", "after the bump it runs");
    }

    private static void Composites()
    {
        var rig = new Rig();
        MetaRule Always() => Cond(C.Always);
        MetaRule Never() => Cond(C.Never);
        Check.True(rig.Holds(C.Always, ""), "Always");
        Check.False(rig.Holds(C.Never, ""), "Never");
        Check.True(rig.Holds(Cond(C.All)), "All with no children is true");
        Check.False(rig.Holds(Cond(C.Any)), "Any with no children is false, as in VTank (eced377)");
        Check.True(rig.Holds(Cond(C.Not, "", Cond(C.Any))), "Not of an empty Any is true, as in VTank (eced377)");
        Check.False(rig.Holds(Cond(C.Not)), "Not with no child is false");
        Check.False(rig.Holds(Cond(C.All, "", Always(), Never())), "All: one false");
        Check.True(rig.Holds(Cond(C.All, "", Always(), Always())), "All: all true");
        Check.True(rig.Holds(Cond(C.Any, "", Never(), Always())), "Any: one true");
        Check.False(rig.Holds(Cond(C.Any, "", Never(), Never())), "Any: none true");
        Check.True(rig.Holds(Cond(C.Not, "", Never())), "Not Never");
        Check.False(rig.Holds(Cond(C.Not, "", Always())), "Not Always");
        Check.True(rig.Holds(Cond(C.Not, "", Never(), Always())), "Not looks at its first child only");
        Check.True(rig.Holds(Cond(C.All, "", Cond(C.Any, "", Never(), Cond(C.Not, "", Never())), Always())), "nested All/Any/Not");
        Check.False(rig.Holds(Cond(C.Any, "", Cond(C.All, "", Always(), Never()), Never())), "nested, false");
        var deep = Always();
        for (int i = 0; i < 40; i++) deep = Cond(C.Not, "", deep);
        Check.True(rig.Holds(deep), "40 nested Nots");
        var children = rig.Holds(Cond(C.All, "", Cond(C.SecondsInState_GE, "0"), Cond(C.Expression, "1")));
        Check.True(children, "leaf conditions work inside a composite");
    }

    private static void Vitals()
    {
        var rig = new Rig();
        rig.V.CurrentHealth = 100; rig.V.MaxHealth = 200;
        rig.V.CurrentMana = 40; rig.V.MaxMana = 400;
        rig.V.CurrentStamina = 7;
        Check.True(rig.Holds(C.MainHealthLE, "100"), "health 100 <= 100");
        Check.False(rig.Holds(C.MainHealthLE, "99"), "health 100 <= 99");
        Check.False(rig.Holds(C.MainHealthLE, "lots"), "non-numeric");
        Check.False(rig.Holds(C.MainHealthLE, ""), "empty");
        Check.True(rig.Holds(C.MainManaLE, "40"), "mana");
        Check.False(rig.Holds(C.MainManaLE, "39"), "mana below");
        Check.True(rig.Holds(C.MainStamLE, "7"), "stamina");
        Check.False(rig.Holds(C.MainStamLE, "6"), "stamina below");
        rig.V.MaxHealth = 0;
        Check.False(rig.Holds(C.MainHealthPHE, "100"), "percent with no max health known");
        rig.V.MaxHealth = 200;
        Check.False(rig.Holds(C.MainHealthPHE, "x"), "percent, non-numeric");
        Check.False(rig.Holds(C.CharacterDeath, ""), "alive");
        rig.V.CurrentHealth = 0;
        Check.True(rig.Holds(C.CharacterDeath, ""), "health 0 = dead");
        // 44703de: before the first vitals poll everything reads 0; that is not a death, and the
        // value conditions wait for the vitals too (MaxHealth > 0 = read at least once).
        var fresh = new Rig();
        Check.False(fresh.Holds(C.CharacterDeath, ""), "before any vitals arrive death does not hold");
        Check.False(fresh.Holds(C.MainHealthLE, "100"), "before any vitals arrive health <= 100 does not hold");
        Check.False(fresh.Holds(C.MainManaLE, "100"), "before any vitals arrive mana <= 100 does not hold");
        Check.False(fresh.Holds(C.MainStamLE, "100"), "before any vitals arrive stamina <= 100 does not hold");
        // A real death is latched (the plugin holds the ticks until after the respawn) and the
        // first meta tick after it sees Death once, with health already back above 0.
        var died = new Rig(rules: Mark("Default", C.CharacterDeath, "", "dead"));
        died.V.CurrentHealth = 150; died.V.MaxHealth = 200;
        died.Tick();
        Check.False(died.Has("dead"), "alive, no death reported");
        died.M.OnPlayerDeath();
        died.S.ForceStateReset = true;
        died.Tick();
        Check.Eq(died.Var("dead"), "1", "the tick after a death sees Death, health already restored");
        died.S.ForceStateReset = true;
        died.Tick();
        Check.Eq(died.Var("dead"), "1", "only once");
        died.M.OnPlayerDeath();
        died.S.IsMacroRunning = false;
        died.Tick();
        died.S.IsMacroRunning = true;
        died.Tick();
        Check.Eq(died.Var("dead"), "1", "a death while the macro is stopped is dropped");
    }

    private static void Vitae()
    {
        FakeHost.Reset();
        FakeHost.Vitae = 0.9f;
        var rig = new Rig(FakeHost.Create(), Player);
        Check.True(rig.Holds(C.VitaePHE, "10"), "10% penalty >= 10");
        Check.False(rig.Holds(C.VitaePHE, "11"), "10% penalty >= 11");
        Check.False(rig.Holds(C.VitaePHE, "ten"), "non-numeric");
        var noPlayer = new Rig(FakeHost.Create());
        Check.False(noPlayer.Holds(C.VitaePHE, "0"), "no player id");
        Check.False(new Rig().Holds(C.VitaePHE, "0"), "no host call");
    }

    private static void SecondsInState()
    {
        var rig = new Rig(rules: new[] { Mark("Default", C.SecondsInState_GE, "5", "s"), Mark("Default", C.SecondsInStateP_GE, "0.5", "p") });
        rig.Tick();
        Check.False(rig.Has("s"), "0 s");
        rig.Tick(0.5);
        Check.Eq(rig.Var("p"), "1", "fractional seconds");
        rig.Tick(4.4);
        Check.False(rig.Has("s"), "4.9 s");
        rig.Tick(0.1);
        Check.Eq(rig.Var("s"), "1", "5 s");

        // A SetMetaState restarts the clock; an operational state change does not.
        var r2 = new Rig(rules: new[]
        {
            Rule("Default", C.SecondsInState_GE, "10", A.SetMetaState, "Hunt"),
            Mark("Hunt", C.SecondsInState_GE, "3", "h"),
        });
        r2.Tick();
        r2.Tick(10);
        Check.Eq(r2.S.CurrentState, "Hunt", "moved after 10 s");
        r2.Tick();                    // the clock restarts on the first tick in the new state
        r2.Tick(2.9);
        Check.False(r2.Has("h"), "2.9 s into Hunt (the clock restarted)");
        r2.Tick(0.1);
        Check.Eq(r2.Var("h"), "1", "3 s into Hunt");

        var r3 = new Rig(rules: Mark("Hunt", C.SecondsInState_GE, "3", "h"));
        r3.Tick();
        r3.Tick(100);                 // 100 s in Default
        r3.S.CurrentState = "Hunt";   // no ForceStateReset
        r3.Tick();
        Check.Eq(r3.Var("h"), "1", "without a reset the timer carries over from the previous state");

        var bad = new Rig();
        Check.False(bad.Holds(C.SecondsInState_GE, "soon"), "non-numeric");
        Check.True(bad.Holds(C.SecondsInState_GE, "0"), "0 s holds at once");
        Check.True(bad.Holds(C.SecondsInState_GE, "-5"), "negative holds at once");
    }

    private static void Chat()
    {
        FakeHost.Reset();
        var host = FakeHost.Create();
        var rig = new Rig(host);
        rig.M.HandleChat("You have killed the Drudge Skulker!");
        rig.Now = rig.Now.AddSeconds(0.5);
        Check.True(rig.Holds(C.ChatMessage, "killed the (Drudge|Mite)"), "regex match 0.5 s later");
        Check.False(rig.Holds(C.ChatMessage, "KILLED"), "case-sensitive");
        Check.False(rig.Holds(C.ChatMessage, ""), "empty pattern");
        Check.False(rig.Holds(C.ChatMessage, "killed ("), "bad regex: no match, no crash");
        rig.Now = rig.Now.AddSeconds(0.5);
        Check.True(rig.Holds(C.ChatMessage, "killed"), "exactly 1 s later");
        rig.Now = rig.Now.AddSeconds(0.01);
        Check.False(rig.Holds(C.ChatMessage, "killed"), "just over 1 s later");

        // Capture groups fill {0}, {1} ... in that rule's action only.
        var cap = new Rig(host, 0,
            Rule("Default", C.ChatMessageCapture, "You have killed the (.+)!", A.ChatCommand, "/say got {1} ({0})"),
            Rule("Default", C.Always, "", A.ChatCommand, "/say {1}"));
        cap.M.HandleChat("You have killed the Drudge Skulker!");
        cap.Tick();
        Check.True(FakeHost.ChatCommands.Contains("/say got Drudge Skulker (You have killed the Drudge Skulker!)"), "groups substituted");
        Check.True(FakeHost.ChatCommands.Contains("/say {1}"), "the next rule does not see the groups");

        // A plain ChatMessage does not capture.
        FakeHost.ChatCommands.Clear();
        var plain = new Rig(host, 0, Rule("Default", C.ChatMessage, "killed the (.+)!", A.ChatCommand, "/say {1}"));
        plain.M.HandleChat("You have killed the Mite!");
        plain.Tick();
        Check.True(FakeHost.ChatCommands.Contains("/say {1}"), "ChatMessage leaves {1} alone");

        var none = new Rig();
        Check.False(none.Holds(C.ChatMessage, "."), "no chat yet");
    }

    private static (RynthCoreHost Host, WorldObjectCache Cache) InventoryCache(int kitStackA = 1, int kitStackB = 1)
    {
        FakeHost.Reset();
        FakeHost.Names[0x60000001] = "Healing Kit";
        FakeHost.Names[0x60000002] = "Healing Kit";
        FakeHost.Names[0x60000003] = "Mana Stone";
        FakeHost.Ints[(0x60000001, 12)] = kitStackA;
        FakeHost.Ints[(0x60000002, 12)] = kitStackB;
        // All three are loose in the player's main pack (PackSlots_LE only counts those, 0a2866a).
        FakeHost.Containers[0x60000001] = Player;
        FakeHost.Containers[0x60000002] = Player;
        FakeHost.Containers[0x60000003] = Player;
        var host = FakeHost.Create();
        return (host, FakeHost.MakeCache(host, Player, new uint[] { 0x60000001, 0x60000002, 0x60000003 }));
    }

    private static void PackAndInventory()
    {
        var (host, cache) = InventoryCache();
        var rig = new Rig(host, Player);
        rig.M.SetObjectCache(cache);
        Check.True(rig.Holds(C.PackSlots_LE, "99"), "102 - 3 = 99 free");
        Check.False(rig.Holds(C.PackSlots_LE, "98"), "not <= 98");
        Check.False(rig.Holds(C.PackSlots_LE, "many"), "non-numeric");
        var noPlayerId = new Rig(host);
        noPlayerId.M.SetObjectCache(cache);
        Check.False(noPlayerId.Holds(C.PackSlots_LE, "200"), "no player id: pack slots false");

        // Main pack only (0a2866a): a side pack and what is in it use none of the 102 slots.
        FakeHost.Names[0x60000004] = "Pack";
        FakeHost.ItemTypes[0x60000004] = 0x200;          // a container
        FakeHost.Containers[0x60000004] = Player;
        FakeHost.Names[0x60000005] = "Pyreal Mote";
        FakeHost.Containers[0x60000005] = 0x60000004;    // inside the side pack
        var packHost = FakeHost.Create();
        var packCache = FakeHost.MakeCache(packHost, Player,
            new uint[] { 0x60000001, 0x60000002, 0x60000003, 0x60000004, 0x60000005 });
        var packRig = new Rig(packHost, Player);
        packRig.M.SetObjectCache(packCache);
        Check.True(packRig.Holds(C.PackSlots_LE, "99"), "side pack and its contents ignored: still 99 free");
        Check.False(packRig.Holds(C.PackSlots_LE, "98"), "side pack and its contents ignored: not <= 98");
        Check.True(rig.Holds(C.InventoryItemCount_GE, "Healing Kit,2"), "two kits >= 2");
        Check.False(rig.Holds(C.InventoryItemCount_GE, "Healing Kit,3"), "not >= 3");
        Check.True(rig.Holds(C.InventoryItemCount_LE, "healing kit,2"), "name ignores case");
        Check.True(rig.Holds(C.InventoryItemCount_LE, " Healing Kit ,2"), "name is trimmed");
        Check.True(rig.Holds(C.InventoryItemCount_LE, "Pyreal,0"), "none of an item <= 0");
        Check.False(rig.Holds(C.InventoryItemCount_GE, "Healing Kit"), "no count");
        Check.False(rig.Holds(C.InventoryItemCount_GE, "Healing Kit,x"), "bad count");
        Check.False(rig.Holds(C.InventoryItemCount_GE, "Healing,1"), "exact names only");

        var noCache = new Rig(host, Player);
        Check.False(noCache.Holds(C.PackSlots_LE, "200"), "no cache: pack slots false");
        Check.False(noCache.Holds(C.InventoryItemCount_LE, "Healing Kit,5"), "no cache: count false");
    }

    private static void Monsters()
    {
        FakeHost.Reset();
        uint drudge = 0x80000011, farDrudge = 0x80000012, crier = 0x80000013, dead = 0x80000014, lurker = 0x80000015;
        FakeHost.Positions[Player] = (0xA9B4001Cu, 50f, 50f, 0f);
        foreach (var (id, name, x) in new[]
        {
            (drudge, "Drudge Skulker", 60f), (farDrudge, "Drudge Ravener", 150f), (crier, "Town Crier", 51f),
            (dead, "Drudge Deadling", 55f), (lurker, "Drudge Lurker", 55f),
        })
        {
            FakeHost.Names[id] = name;
            FakeHost.Positions[id] = (0xA9B4001Cu, x, 50f, 0f);
            FakeHost.ItemTypes[id] = 0x10;
        }
        FakeHost.NotAttackable.Add(crier);
        var host = FakeHost.Create();
        // The lurker never got a health update (untracked); the dead one reads 0.
        var cache = FakeHost.MakeCache(host, Player, new[] { drudge, farDrudge, crier, dead, lurker },
            new Dictionary<uint, float> { [drudge] = 1f, [farDrudge] = 0.5f, [crier] = 1f, [dead] = 0f });
        var rig = new Rig(host, Player);
        rig.M.SetObjectCache(cache);
        rig.S.MonsterRules.Add(new MonsterRule { Name = "Default" });
        rig.S.MonsterRules.Add(new MonsterRule { Name = "^Drudge" });

        Check.True(rig.Holds(C.MonsterNameCountWithinDistance, "Drudge,20,1"), "one drudge within 20");
        Check.False(rig.Holds(C.MonsterNameCountWithinDistance, "Drudge,20,2"), "dead and untracked ones do not count");
        Check.True(rig.Holds(C.MonsterNameCountWithinDistance, "drudge,100,2"), "two within 100 (ignores case, inclusive)");
        Check.False(rig.Holds(C.MonsterNameCountWithinDistance, "Drudge,99.9,2"), "the far one is at 100");
        Check.False(rig.Holds(C.MonsterNameCountWithinDistance, "Crier,20,1"), "not attackable");
        Check.False(rig.Holds(C.MonsterNameCountWithinDistance, "Drudge,x,1"), "bad distance");
        Check.False(rig.Holds(C.MonsterNameCountWithinDistance, "Drudge,20"), "missing count");
        Check.False(rig.Holds(C.MonsterNameCountWithinDistance, "(,20,1"), "bad regex");
        Check.True(rig.Holds(C.MonsterNameCountWithinDistance, "Drudge,20,0"), "at least 0 always holds");

        Check.True(rig.Holds(C.NoMonstersWithinDistance, "9"), "none within 9 (crier, dead, untracked skipped)");
        Check.False(rig.Holds(C.NoMonstersWithinDistance, "10"), "the drudge at 10");
        Check.False(rig.Holds(C.NoMonstersWithinDistance, ""), "empty means 20");
        Check.False(rig.Holds(C.NoMonstersWithinDistance, "0"), "0 means 20");
        Check.False(rig.Holds(C.NoMonstersWithinDistance, "junk"), "junk means 20");

        // Priority counts use the Monsters list (Default excluded) and, unlike the two above,
        // do count a monster that never had a health update.
        Check.True(rig.Holds(C.MonsterPriorityCountWithinDistance, "2,20"), "drudge + untracked lurker");
        Check.False(rig.Holds(C.MonsterPriorityCountWithinDistance, "3,20"), "not three within 20");
        Check.True(rig.Holds(C.MonsterPriorityCountWithinDistance, "3,100"), "three within 100");
        Check.False(rig.Holds(C.MonsterPriorityCountWithinDistance, "1"), "missing distance");
        rig.S.MonsterRules.RemoveAt(1);
        Check.False(rig.Holds(C.MonsterPriorityCountWithinDistance, "1,500"), "only Default in the list: nothing is priority");

        var noCache = new Rig(host, Player);
        Check.True(noCache.Holds(C.NoMonstersWithinDistance, "50"), "no cache: no monsters");
        Check.False(noCache.Holds(C.MonsterNameCountWithinDistance, "Drudge,20,0"), "no cache: name count false");
        Check.False(noCache.Holds(C.MonsterPriorityCountWithinDistance, "0,20"), "no cache: priority false");
        var noPlayer = new Rig(host);
        noPlayer.M.SetObjectCache(cache);
        Check.True(noPlayer.Holds(C.NoMonstersWithinDistance, "50"), "no player id: no monsters");
    }

    private static void NavConditions()
    {
        FakeHost.Reset();
        FakeHost.PlayerPose = (0xA9B4001Cu, 96f, 96f, 0f);   // NS 42.45, EW 33.65
        var rig = new Rig(FakeHost.Create(), Player);
        Check.True(rig.Holds(C.NavrouteEmpty, ""), "no route loaded");
        rig.S.CurrentRoute = NavRouteParser.LoadFromLines(NavLines(1,
            (0, 33.65, 43.45, NoTrailer),                  // a point 1.0 north of the player
            (2, 33.65, 42.45, new[] { "48" })));           // a Recall point right on the player
        rig.S.ActiveNavIndex = 0;
        Check.False(rig.Holds(C.NavrouteEmpty, ""), "route with points, index 0");
        rig.S.ActiveNavIndex = 2;
        Check.True(rig.Holds(C.NavrouteEmpty, ""), "index past the end");

        // Distance is in yards (b26b338: the NS/EW gap x 240, 1.0 map unit = 240 yd), and counts plain waypoints only.
        Check.True(rig.Holds(C.DistAnyRoutePT_GE, "239.9"), "240 yd away >= 239.9");
        Check.False(rig.Holds(C.DistAnyRoutePT_GE, "240.1"), "240 yd away < 240.1 (the Recall point on top of us is ignored)");
        Check.True(rig.Holds(C.DistAnyRoutePT_GE, "50"), "240 yd away >= 50 yd");
        Check.False(rig.Holds(C.DistAnyRoutePT_GE, "far"), "non-numeric");
        rig.S.CurrentRoute = NavRouteParser.LoadFromLines(NavLines(1, (2, 33.65, 42.45, new[] { "48" })));
        Check.False(rig.Holds(C.DistAnyRoutePT_GE, "0"), "no plain waypoint at all: false");
        rig.S.CurrentRoute = new NavRouteParser();
        Check.False(rig.Holds(C.DistAnyRoutePT_GE, "0"), "empty route: false");
        FakeHost.HasPose = false;
        rig.S.CurrentRoute = NavRouteParser.LoadFromLines(NavLines(1, (0, 33.65, 43.45, NoTrailer)));
        Check.False(rig.Holds(C.DistAnyRoutePT_GE, "0"), "no position: false");
    }

    private static void LandblockLandcell()
    {
        FakeHost.Reset();
        FakeHost.PlayerPose = (0xA9B4001Cu, 10f, 10f, 0f);
        var rig = new Rig(FakeHost.Create(), Player);
        Check.True(rig.Holds(C.Landblock_EQ, "A9B4"), "landblock hex");
        Check.True(rig.Holds(C.Landblock_EQ, "a9b4"), "lower case");
        Check.False(rig.Holds(C.Landblock_EQ, "A9B5"), "other landblock");
        Check.False(rig.Holds(C.Landblock_EQ, ""), "empty");
        // 6e022f7: metaf writes the full 8-digit value and VTank matches its leading 4 digits.
        Check.True(rig.Holds(C.Landblock_EQ, "A9B40000"), "an 8-digit value matches on its leading 4 digits");
        Check.True(rig.Holds(C.Landblock_EQ, "A9B4001C"), "an 8-digit value's low 4 digits are ignored");
        Check.False(rig.Holds(C.Landblock_EQ, "A9B50000"), "an 8-digit value for another landblock");
        Check.False(rig.Holds(C.Landblock_EQ, "43444"), "43444 (= 0xA9B4 in decimal) is read as hex, not decimal");
        Check.True(rig.Holds(C.Landcell_EQ, "A9B4001C"), "landcell");
        Check.True(rig.Holds(C.Landcell_EQ, "0xA9B4001C") == false, "a 0x prefix is not accepted");
        Check.False(rig.Holds(C.Landcell_EQ, "A9B4001D"), "other cell");
        Check.False(rig.Holds(C.Landcell_EQ, "zz"), "not a number");
        FakeHost.HasPose = false;
        Check.False(rig.Holds(C.Landblock_EQ, "A9B4"), "no position");
        Check.False(new Rig().Holds(C.Landcell_EQ, "A9B4001C"), "no host call");
    }

    private static void Portal()
    {
        FakeHost.Reset();
        var rig = new Rig(FakeHost.Create(), Player);
        Check.False(rig.Holds(C.PortalspaceEntered, ""), "not portaling");
        FakeHost.Portaling = true;
        Check.True(rig.Holds(C.PortalspaceEntered, ""), "entered on the tick it starts");
        Check.False(rig.Holds(C.PortalspaceEntered, ""), "only on that tick");
        Check.False(rig.Holds(C.PortalspaceExited, ""), "still in portal space");
        FakeHost.Portaling = false;
        Check.True(rig.Holds(C.PortalspaceExited, ""), "exited on the tick it ends");
        Check.False(rig.Holds(C.PortalspaceExited, ""), "only on that tick");
        Check.False(new Rig().Holds(C.PortalspaceEntered, ""), "no host call: never");
    }

    private static void VendorOpen()
    {
        var rig = new Rig();
        Check.False(rig.Holds(C.AnyVendorOpen, ""), "no vendor");
        rig.M.OnVendorOpen(0);
        Check.False(rig.Holds(C.AnyVendorOpen, ""), "vendor id 0 ignored");
        rig.M.OnVendorOpen(5);
        Check.True(rig.Holds(C.AnyVendorOpen, ""), "vendor open");
        rig.M.OnVendorClose(6);
        Check.True(rig.Holds(C.AnyVendorOpen, ""), "closing another vendor does not count");
        rig.M.OnVendorClose(5);
        Check.False(rig.Holds(C.AnyVendorOpen, ""), "closed");
    }

    private static void TimeLeftOnSpell()
    {
        FakeHost.Reset();
        FakeHost.ServerTime = 1000;
        FakeHost.Enchantments.Add((1332, 1100.5));
        var rig = new Rig(FakeHost.Create(), Player);
        Check.True(rig.Holds(C.TimeLeftOnSpell_GE, "1332,100"), "100.5 s left >= 100");
        Check.False(rig.Holds(C.TimeLeftOnSpell_GE, "1332,101"), "not >= 101");
        Check.True(rig.Holds(C.TimeLeftOnSpell_LE, "1332,101"), "<= 101");
        Check.False(rig.Holds(C.TimeLeftOnSpell_LE, "1332,100"), "not <= 100");
        Check.False(rig.Holds(C.TimeLeftOnSpell_GE, "1337,0"), "a spell you do not have: GE is false");
        Check.True(rig.Holds(C.TimeLeftOnSpell_LE, "1337,0"), "a spell you do not have counts as 0 left");
        Check.False(rig.Holds(C.TimeLeftOnSpell_GE, "1332"), "missing seconds");
        Check.False(rig.Holds(C.TimeLeftOnSpell_LE, "x,5"), "bad spell id");
        Check.False(rig.Holds(C.TimeLeftOnSpell_LE, ""), "empty");
        FakeHost.Enchantments.Clear();
        Check.True(rig.Holds(C.TimeLeftOnSpell_LE, "1332,10"), "no enchantments at all: LE holds");
        Check.False(rig.Holds(C.TimeLeftOnSpell_GE, "1332,0"), "no enchantments at all: GE fails");
        var nullHost = new Rig();
        Check.False(nullHost.Holds(C.TimeLeftOnSpell_LE, "1332,10"), "no host call: LE false");
        Check.False(nullHost.Holds(C.TimeLeftOnSpell_GE, "1332,0"), "no host call: GE false");
    }

    private static void Burden()
    {
        FakeHost.Reset();
        FakeHost.Ints[(Player, 5)] = 1500;
        FakeHost.Ints[(Player, 96)] = 1000;
        var rig = new Rig(FakeHost.Create(), Player);
        Check.True(rig.Holds(C.BurdenPercentage_GE, "150"), "150%");
        Check.False(rig.Holds(C.BurdenPercentage_GE, "151"), "not 151%");
        Check.False(rig.Holds(C.BurdenPercentage_GE, "heavy"), "non-numeric");
        FakeHost.Ints[(Player, 96)] = 0;
        Check.False(rig.Holds(C.BurdenPercentage_GE, "0"), "capacity 0");
        Check.False(new Rig(FakeHost.Create()).Holds(C.BurdenPercentage_GE, "0"), "no player id");
    }

    private static void ExpressionCondition()
    {
        var rig = new Rig();
        Check.True(rig.Holds(C.Expression, "1"), "1");
        Check.False(rig.Holds(C.Expression, "0"), "0");
        Check.False(rig.Holds(C.Expression, ""), "empty");
        Check.True(rig.Holds(C.Expression, "abc"), "text is true");
        rig.M.Expressions.Evaluate("setvar[hp, 30]");
        Check.True(rig.Holds(C.Expression, "getvar[hp] < 50 && getvar[hp] > 0"), "a real expression");
        Check.False(rig.Holds(C.Expression, "randint[5,1]"), "an evaluation error fails closed");
        Check.True(rig.M.GetStateSnapshot().LastExprError.Contains("ERR:ArgumentOutOfRange"), $"and is recorded: {rig.M.GetStateSnapshot().LastExprError}");
    }

    private static void NeedToBuffAndUnknown()
    {
        var rig = new Rig();
        Check.False(rig.Holds(C.NeedToBuff, ""), "no buff manager");
        Check.False(rig.Holds((C)999, ""), "an unknown condition type is false");
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    private static void RunAction(Rig rig, A act, string data)
    {
        rig.S.MetaRules = new List<MetaRule> { Rule(rig.S.CurrentState, C.Always, "", act, data) };
        rig.S.ForceStateReset = true;
        rig.M.Think();
    }

    private static void ChatCommands()
    {
        FakeHost.Reset();
        var rig = new Rig(FakeHost.Create(), Player);
        var mt = new List<string>();
        rig.M.SetMtCommandHandler(c => { mt.Add(c); return c.StartsWith("/mt ", StringComparison.Ordinal); });

        RunAction(rig, A.ChatCommand, "/say hello");
        Check.True(FakeHost.ChatCommands.Contains("/say hello"), "plain command to the chat parser");
        RunAction(rig, A.ChatCommand, "/ra start");
        Check.True(rig.Ra.Contains("/ra start"), "/ra goes to the RynthAi handler");
        RunAction(rig, A.ChatCommand, "/mt dequip");
        Check.True(mt.Contains("/mt dequip") && !FakeHost.ChatCommands.Contains("/mt dequip"), "/mt handled by the Mag-Tools handler");
        RunAction(rig, A.ChatCommand, "/vt opt set enablecombat true");
        Check.True(rig.S.EnableCombat, "/vt opt set enablecombat true");
        RunAction(rig, A.ChatCommand, "/vt opt set enablecombat off");
        Check.False(rig.S.EnableCombat, "off");
        RunAction(rig, A.ChatCommand, "/vt opt set attackdistance 25");
        Check.Eq(rig.S.MonsterRange, 25, "attackdistance -> MonsterRange");
        RunAction(rig, A.ChatCommand, "/vt opt set CustomThing 5");
        Check.Eq(rig.M.Expressions.GetOption("customthing"), "5", "unmapped option stored for expressions");
        RunAction(rig, A.ChatCommand, "/vt stop");
        Check.True(rig.Ra.Contains("/ra stop"), "/vt stop");
        RunAction(rig, A.ChatCommand, "/vt nav load Hive Loop");
        Check.True(rig.Ra.Contains("/ra nav load Hive Loop"), "/vt nav load keeps spaces");
        RunAction(rig, A.ChatCommand, "/vt setattackbar 0.75");
        Check.True(rig.Ra.Contains("/ra power 75"), "/vt setattackbar 0.75 -> 75%");
        RunAction(rig, A.ChatCommand, "/vt setattackbar 3");
        Check.True(rig.Ra.Contains("/ra power 100"), "clamped to 100");
        RunAction(rig, A.ChatCommand, "/vt echo hello there");
        Check.True(FakeHost.Chat.Contains("hello there"), "/vt echo writes locally");
        int before = FakeHost.ChatCommands.Count;
        RunAction(rig, A.ChatCommand, "/vt settings load hunt");
        Check.Eq(FakeHost.ChatCommands.Count, before, "/vt settings is swallowed");
        RunAction(rig, A.ChatCommand, "/vt frobnicate");
        Check.True(FakeHost.ChatCommands.Contains("/vt frobnicate"), "an unknown /vt verb goes to the chat parser");
        RunAction(rig, A.ChatCommand, "");
        RunAction(rig, A.ChatCommand, "/say {0}");
        Check.True(FakeHost.ChatCommands.Contains("/say {0}"), "no capture: placeholders stay");
        RunAction(rig, A.ChatCommand, "/vt setmetastate Loot Run");
        Check.Eq(rig.S.CurrentState, "Loot Run", "/vt setmetastate");

        var reversed = new Rig(FakeHost.Create(), Player);
        reversed.S.CurrentRoute = NavRouteParser.LoadFromLines(NavLines(2,
            (0, 1, 1, NoTrailer), (0, 2, 2, NoTrailer), (0, 3, 3, NoTrailer)));
        reversed.S.ActiveNavIndex = 0;
        RunAction(reversed, A.ChatCommand, "/vt reverseroute");
        Check.Near(reversed.S.CurrentRoute.Points[0].EW, 3, 1e-9, "route reversed");
        Check.Eq(reversed.S.ActiveNavIndex, 2, "index mirrored");
    }

    private static void CallReturn()
    {
        var rig = new Rig(rules: new[]
        {
            Rule("Default", C.Always, "", A.CallMetaState, "A"),
            Rule("A", C.Always, "", A.CallMetaState, "B"),
            Rule("B", C.SecondsInState_GE, "1", A.ReturnFromCall, ""),
            Rule("A", C.SecondsInState_GE, "1", A.ReturnFromCall, ""),
            Rule("Default", C.Never, "", A.None, ""),
        });
        rig.Tick();
        Check.Eq(rig.S.CurrentState, "A", "called A");
        rig.Tick();
        Check.Eq(rig.S.CurrentState, "B", "A called B");
        Check.Eq(rig.M.GetStateSnapshot().StackDepth, 2, "two states on the stack");
        rig.Tick();
        rig.Tick(1);
        Check.Eq(rig.S.CurrentState, "A", "B returned to A");
        Check.Eq(rig.M.GetStateSnapshot().StackDepth, 1, "one left");
        // Back in A, its Always->Call B rule is re-armed and calls B again: returning re-runs the caller's rules.
        rig.Tick();
        Check.Eq(rig.S.CurrentState, "B", "A calls B again after the return");

        var empty = new Rig();
        empty.S.CurrentState = "Solo";
        RunAction(empty, A.ReturnFromCall, "");
        Check.Eq(empty.S.CurrentState, "Solo", "return with an empty stack does nothing");
        RunAction(empty, A.SetMetaState, "");
        Check.Eq(empty.S.CurrentState, "Solo", "SetMetaState with no name does nothing");
        RunAction(empty, A.CallMetaState, "");
        Check.Eq(empty.M.GetStateSnapshot().StackDepth, 0, "CallMetaState with no name pushes nothing");
    }

    private static void AllAction()
    {
        var rig = new Rig();
        var all = Rule("Default", C.Always, "", A.All, "");
        all.ActionChildren.Add(new MetaRule { Action = A.ExpressionAction, ActionData = "setvar[o, getvar[o] + `a`]" });
        all.ActionChildren.Add(new MetaRule { Action = A.ExpressionAction, ActionData = "setvar[o, getvar[o] + `b`]" });
        all.Children.Add(new MetaRule { Action = A.ExpressionAction, ActionData = "setvar[legacy, 1]" });
        rig.S.MetaRules = new List<MetaRule> { all };
        rig.Tick();
        Check.Eq(rig.Var("o"), "0ab", "children in order");
        Check.False(rig.Has("legacy"), "Children are only used when ActionChildren is empty");

        var legacy = Rule("Default", C.Always, "", A.All, "");
        legacy.Children.Add(new MetaRule { Action = A.ExpressionAction, ActionData = "setvar[legacy, 1]" });
        var r2 = new Rig(rules: legacy);
        r2.Tick();
        Check.Eq(r2.Var("legacy"), "1", "old metas keep actions in Children");

        // A state change inside an All does not stop the remaining children.
        var mixed = Rule("Default", C.Always, "", A.All, "");
        mixed.ActionChildren.Add(new MetaRule { Action = A.SetMetaState, ActionData = "Next" });
        mixed.ActionChildren.Add(new MetaRule { Action = A.ExpressionAction, ActionData = "setvar[after, 1]" });
        var r3 = new Rig(rules: mixed);
        r3.Tick();
        Check.Eq(r3.S.CurrentState, "Next", "state set");
        Check.Eq(r3.Var("after"), "1", "later child still ran");
    }

    private static void EmbeddedNav()
    {
        FakeHost.Reset();
        FakeHost.PlayerPose = (0xA9B4001Cu, 96f, 96f, 0f);   // NS 42.45, EW 33.65
        var host = FakeHost.Create();
        var circular = NavLines(1, (0, 30, 40, NoTrailer), (0, 33.65, 42.45, NoTrailer), (0, 36, 44, NoTrailer));
        var once = NavLines(4, (0, 30, 40, NoTrailer), (0, 33.65, 42.45, NoTrailer));

        var rig = new Rig(host, Player);
        rig.S.EmbeddedNavs["Hive Route"] = circular;
        rig.S.EmbeddedNavs["Once Route"] = once;
        RunAction(rig, A.EmbeddedNavRoute, "Hive Route;extra");
        Check.Eq(rig.S.CurrentRoute.Points.Count, 3, "route loaded");
        Check.Eq(rig.S.ActiveNavIndex, 1, "circular route starts at the nearest point");
        Check.True(rig.S.EnableNavigation, "navigation switched on");
        Check.Eq(rig.S.CurrentNavPath, "<embedded:Hive Route>", "nav path marks it embedded");
        Check.True(FakeHost.Chat.Exists(c => c.Contains("Route") && c.Contains("Hive Route")), "announced in chat");

        RunAction(rig, A.EmbeddedNavRoute, "Once Route");
        Check.Eq(rig.S.ActiveNavIndex, 0, "a Once route always starts at 0");

        rig.S.CurrentRoute = new NavRouteParser();
        RunAction(rig, A.EmbeddedNavRoute, "nav0__Hive_Route_nav");
        Check.Eq(rig.S.CurrentRoute.Points.Count, 3, "legacy name nav0__Hive_Route_nav finds 'Hive Route'");
        RunAction(rig, A.EmbeddedNavRoute, "hive route");
        Check.Eq(rig.S.CurrentNavPath, "<embedded:hive route>", "names ignore case");

        rig.S.CurrentRoute = new NavRouteParser();
        RunAction(rig, A.EmbeddedNavRoute, "   ");
        Check.Eq(rig.S.CurrentRoute.Points.Count, 0, "blank name ignored");
        RunAction(rig, A.EmbeddedNavRoute, "__rynthai_offline_test_no_such_route__");
        Check.Eq(rig.S.CurrentRoute.Points.Count, 0, "unknown route: nothing loaded");
        Check.True(FakeHost.Chat.Exists(c => c.Contains("Route missing")), "unknown route reported");
    }

    private static void Watchdog()
    {
        FakeHost.Reset();
        FakeHost.PlayerPose = (0xA9B4001Cu, 10f, 10f, 0f);
        var host = FakeHost.Create();
        var rig = new Rig(host, Player, Rule("Default", C.Always, "", A.SetWatchdog, "Stuck;5;10"));
        rig.Tick();
        Check.True(rig.M.GetStateSnapshot().WatchdogActive, "armed");
        rig.Tick(9.9);
        Check.Eq(rig.S.CurrentState, "Default", "9.9 s without moving");
        rig.Tick(0.2);
        Check.Eq(rig.S.CurrentState, "Stuck", "10.1 s without moving: sent to Stuck");
        Check.True(FakeHost.Chat.Exists(c => c.Contains("WATCHDOG TRIGGERED")), "announced");
        Check.False(rig.M.GetStateSnapshot().WatchdogActive, "disarmed after it fires");

        // Moving 5 m or more restarts the countdown.
        FakeHost.PlayerPose = (0xA9B4001Cu, 10f, 10f, 0f);
        var moving = new Rig(host, Player, Rule("Default", C.Always, "", A.SetWatchdog, "Stuck;5;10"));
        moving.Tick();
        moving.Tick(8);
        FakeHost.PlayerPose = (0xA9B4001Cu, 15f, 10f, 0f);   // exactly 5 m
        moving.Tick();
        moving.Tick(9.9);
        Check.Eq(moving.S.CurrentState, "Default", "countdown restarted by the move");
        FakeHost.PlayerPose = (0xA9B4001Cu, 19.9f, 10f, 0f); // 4.9 m: not enough
        moving.Tick(0.2);
        Check.Eq(moving.S.CurrentState, "Stuck", "a 4.9 m shuffle does not count");

        // ClearWatchdog, a state change, or malformed data leave no watchdog.
        var cleared = new Rig(host, Player,
            Rule("Default", C.Always, "", A.SetWatchdog, "Stuck;5;10"),
            Rule("Default", C.SecondsInState_GE, "1", A.ClearWatchdog, ""));
        cleared.Tick(); cleared.Tick(1); cleared.Tick(20);
        Check.Eq(cleared.S.CurrentState, "Default", "cleared watchdog never fires");

        var leave = new Rig(host, Player,
            Rule("Default", C.Always, "", A.SetWatchdog, "Stuck;5;10"),
            Rule("Default", C.SecondsInState_GE, "1", A.SetMetaState, "Hunt"));
        leave.Tick(); leave.Tick(1); leave.Tick(); leave.Tick(20);
        Check.Eq(leave.S.CurrentState, "Hunt", "a state change drops the watchdog");

        var bad = new Rig(host, Player, Rule("Default", C.Always, "", A.SetWatchdog, "Stuck;5"));
        bad.Tick();
        Check.False(bad.M.GetStateSnapshot().WatchdogActive, "two fields: no watchdog");
        var defaults = new Rig(host, Player, Rule("Default", C.Always, "", A.SetWatchdog, "Stuck;far;long"));
        defaults.Tick();
        defaults.Tick(5.1);
        Check.Eq(defaults.S.CurrentState, "Stuck", "unreadable numbers fall back to 5 m / 5 s");
        var noPose = new Rig(default, Player, Rule("Default", C.Always, "", A.SetWatchdog, "Stuck;5;1"));
        noPose.Tick(); noPose.Tick(10);
        Check.Eq(noPose.S.CurrentState, "Default", "no position readable: the watchdog never fires");
    }

    private static void MiscActions()
    {
        FakeHost.Reset();
        var rig = new Rig(FakeHost.Create(), Player);
        RunAction(rig, A.SetRAOption, "mode; hunt");
        Check.Eq(rig.M.Expressions.GetOption("mode"), "hunt", "SetRAOption name;value (trimmed)");
        RunAction(rig, A.GetRAOption, "v;mode");
        Check.Eq(rig.Var("v"), "hunt", "GetRAOption var;name");
        RunAction(rig, A.SetRAOption, "novalue");
        Check.Eq(rig.M.Expressions.GetOption("novalue"), "0", "malformed: nothing set");

        rig.M.Expressions.Evaluate("setvar[hp, 5]");
        RunAction(rig, A.ChatExpression, "`/say hp ` + getvar[hp]");
        Check.True(FakeHost.ChatCommands.Contains("/say hp 5"), "ChatExpression sends the evaluated text");
        RunAction(rig, A.ChatExpression, "`/vt setmetastate FromExpr`");
        Check.Eq(rig.S.CurrentState, "FromExpr", "an evaluated /vt command is handled here");
        int before = FakeHost.ChatCommands.Count;
        RunAction(rig, A.ChatExpression, "``");
        Check.Eq(FakeHost.ChatCommands.Count, before, "an empty result sends nothing");

        RunAction(rig, A.ExpressionAction, "setvar[x, 2*21]");
        Check.Eq(rig.Var("x"), "42", "ExpressionAction runs the expression");
        int unknownBefore = FakeHost.Chat.Count(c => c.Contains("Unknown action"));
        RunAction(rig, A.ExpressionAction, "nosuchfunction[]");
        Check.Eq(FakeHost.Chat.Count(c => c.Contains("Unknown action")), unknownBefore, "an unknown function is not reported as an unknown action");

        var views = new Rig(FakeHost.Create(), Player,
            Rule("Default", C.Always, "", A.CreateView, "<view/>"),
            Rule("Default", C.Always, "", A.DestroyView, "v"));
        views.Tick();
        Check.Eq(FakeHost.Chat.Count(c => c.Contains("VTank Views")), 1, "views warn once");
        Check.True(views.M.GetStateSnapshot().LastExprError.Contains("Views are not supported"), "and show on the debug strip");
    }

    private static void ExceptionInTick()
    {
        var (host, cache) = InventoryCache();
        var bad = new MetaRule { State = "Default", Condition = C.MonsterPriorityCountWithinDistance, ConditionData = null!, Action = A.None };
        var rig = new Rig(host, Player, Mark("Default", C.Always, "", "before"), bad);
        rig.M.SetObjectCache(cache);
        rig.Tick();
        var snap = rig.M.GetStateSnapshot();
        Check.True(snap.LastExprError.StartsWith("Think: NullReferenceException", StringComparison.Ordinal), $"error recorded: {snap.LastExprError}");
        Check.True(rig.S.ForceStateReset, "a reset is forced");
        Check.Eq(rig.S.CurrentState, "Default", "state unchanged");
        rig.Tick();
        Check.Eq(rig.Var("before"), "2", "so rules that already fired fire again next tick");
    }

    private static void Snapshot()
    {
        var rig = new Rig(rules: new[]
        {
            Rule("Default", C.Always, "", A.SetMetaState, "Hunt"),
            Mark("hunt", C.Never, "", "x"),
            Mark("Hunt", C.Never, "", "y"),
        });
        var s0 = rig.M.GetStateSnapshot();
        Check.Eq(s0.RuleCount, 3, "rules");
        Check.Eq(s0.StateCount, 2, "states counted ignoring case");
        Check.Eq(s0.LastFiredSecondsAgo, -1.0, "nothing fired yet");
        rig.Tick();                   // fires, moves to Hunt
        rig.Tick();                   // Hunt entered
        rig.Tick(2.5);
        var s1 = rig.M.GetStateSnapshot();
        Check.Eq(s1.CurrentState, "Hunt", "current state");
        Check.Eq(s1.LastFiredState, "Default", "last fired state");
        Check.Eq(s1.LastFiredCondition, "Always", "last fired condition");
        Check.Eq(s1.LastFiredAction, "SetMetaState(Hunt)", "last fired action");
        Check.Near(s1.LastFiredSecondsAgo, 2.5, 1e-9, "seconds since it fired");
        Check.Near(s1.SecondsInState, 2.5, 1e-9, "seconds in Hunt");
        Check.True(s1.MacroRunning && s1.MetaEnabled, "flags");
    }

    // ── Bug tests (fixed unless still registered with KnownFailure) ───────────

    private static void VendorClosedFires()
    {
        var rig = new Rig(rules: Mark("Default", C.VendorClosed, "", "closed"));
        rig.Tick();
        rig.M.OnVendorOpen(5);
        rig.Tick();
        rig.M.OnVendorClose(5);
        rig.Tick();
        Check.Eq(rig.Var("closed"), "1", "VendorClosed on the tick after the vendor closed");
    }

    private static void InventoryCountsStacks()
    {
        var (host, cache) = InventoryCache(kitStackA: 5, kitStackB: 3);
        var rig = new Rig(host, Player);
        rig.M.SetObjectCache(cache);
        Check.True(rig.Holds(C.InventoryItemCount_GE, "Healing Kit,8"), "stacks of 5 and 3 are 8 kits");
        Check.False(rig.Holds(C.InventoryItemCount_LE, "Healing Kit,2"), "8 kits is not <= 2");
    }

    private static void SecondsInStatePPersists()
    {
        var rig = new Rig(rules: new[]
        {
            Mark("Default", C.SecondsInStateP_GE, "25", "p"),
            Rule("Default", C.SecondsInState_GE, "20", A.CallMetaState, "Sub"),
            Rule("Sub", C.SecondsInState_GE, "1", A.ReturnFromCall, ""),
        });
        rig.Tick();      // T0: enter Default
        rig.Tick(20);    // T0+20: call Sub
        rig.Tick();      //         Sub entered
        rig.Tick(1);     // T0+21: return to Default
        rig.Tick();      //         Default re-entered
        rig.Tick(5);     // T0+26: 25 s in Default (26 s since first entering it)
        Check.Eq(rig.Var("p"), "1", "SecondsInStateP_GE 25 after 25 s spent in Default around a 1 s call");
    }

    private static void PercentConditionsAreGE()
    {
        var rig = new Rig();
        rig.V.MaxHealth = 100; rig.V.MaxMana = 100;
        rig.V.CurrentHealth = 80; rig.V.CurrentMana = 80;
        Check.True(rig.Holds(C.MainHealthPHE, "75"), "health 80% >= 75");
        Check.True(rig.Holds(C.MainManaPHE, "75"), "mana 80% >= 75");
        rig.V.CurrentHealth = 50; rig.V.CurrentMana = 50;
        Check.False(rig.Holds(C.MainHealthPHE, "75"), "health 50% is not >= 75");
        Check.False(rig.Holds(C.MainManaPHE, "75"), "mana 50% is not >= 75");
    }

    private static void NegativeVitalThreshold()
    {
        var rig = new Rig();
        rig.V.CurrentHealth = 100; rig.V.CurrentMana = 100; rig.V.CurrentStamina = 100;
        Check.False(rig.Holds(C.MainHealthLE, "-1"), "health 100 <= -1");
        Check.False(rig.Holds(C.MainManaLE, "-1"), "mana 100 <= -1");
        Check.False(rig.Holds(C.MainStamLE, "-1"), "stamina 100 <= -1");
    }
}
