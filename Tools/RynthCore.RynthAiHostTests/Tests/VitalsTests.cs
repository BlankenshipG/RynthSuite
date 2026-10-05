using System.Linq;
using RynthCore.Loot;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Self vitals (BuffManager.CheckVitals): when Stamina to Health Self is cast. It has its own
// lines, Stamina To Health At and Stamina To Health Min Stamina, apart from Emergency Heal At
// (kits and potions). Run in Magic mode with no kits or potions and every other vital line at
// 0, so the only thing CheckVitals can do is the Stamina to Health cast.
internal static class VitalsTests
{
    private const uint Player = 0x50000B01;
    private const uint STypeLife = 33;

    public static void Register(Runner r)
    {
        r.Add("vitals: Stamina to Health follows its own health line and stamina floor", StaminaToHealthThresholds);
        r.Add("vitals: profiles without the Stamina to Health keys load today's 30 / 20", StaminaToHealthDefaults);
        r.Add("vitals: after a kit, nothing else heals until the kit's result line lands", KitResultWait);
        r.Add("vitals: low health wins the arbiter even on a busy tick; backs off when nothing can heal", HealthWinsArbiter);
        r.Add("vitals: 'You're too busy!' paces vital casts, and a long streak stops the character once", TooBusyVitals);
        // Drakkon's death, 2026-10-05 13:43:20 (ops\overnight\2026-10-05-death-fixes.md).
        r.Add("vitals: a hot reload's login refresh trusts its first read; a real login waits for two equal reads", HotReloadRefresh);
        r.Add("vitals: RynthAi tells the engine a reload is unsafe while engaged, under a heal line or casting", ReloadBlockerReport);
    }

    // Drakkon, 2026-10-05: stuck in the cast gesture, AC answering "You're too busy!" every
    // 0.4 s. The buff path backed off; Stamina to Mana didn't, and re-cast on every
    // SpellCastIntervalMs tick until the player moved. Mana/stamina casts now wait out the
    // backoff, and a streak of refusals sends one StopCompletely (what moving did).
    private static void TooBusyVitals()
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        FakeHost.StopCalls = true;
        FakeHost.CombatModeValue = CombatMode.Magic;
        foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
        FakeHost.Skills[(Player, STypeLife)] = (400, 2);
        var host = FakeHost.Create();
        var s = new LegacyUiSettings { IsMacroRunning = true, HealAt = 0, TopOffHP = 0, EmergencyHealAt = 0, StaminaToHealthAt = 0,
                                       GetManaAt = 50, TopOffMana = 50, RestamAt = 0, TopOffStam = 0, UsePotions = false };
        var skills = new CharacterSkills(host);
        skills.SetPlayerId(Player);
        var sm = new SpellManager(host, s);
        sm.InitializeNatively();
        sm.SetCharacterSkills(skills);
        var vitals = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 100, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 10 };
        var b = new BuffManager(host, s, sm, vitals);
        b.SetCharacterSkills(skills);

        Check.True(b.CheckVitals(), "10% mana: Stamina to Mana is cast");
        Check.Eq(FakeHost.Casts.Count, 1, "one cast");
        b.OnChatWindowText("You're too busy!", 26);
        Check.True(b.CheckVitals(), "too busy: the vital tick still yields (no potion, no buff)");
        Check.Eq(FakeHost.Casts.Count, 1, "but nothing is re-cast inside the backoff");

        for (int i = 0; i < 3; i++) b.OnChatWindowText("You're too busy!", 26);
        Check.Eq(FakeHost.Stops, 1, "4 refusals in a row: one StopCompletely to clear the stuck gesture");
        for (int i = 0; i < 4; i++) b.OnChatWindowText("You're too busy!", 26);
        Check.Eq(FakeHost.Stops, 1, "only once per streak");
    }

    // 2026-10-04, "nav and looting have priority over healing, almost dying in packs": the
    // arbiter only saw a heal on ticks where CheckVitals ran (never while looting kept the client
    // busy). Health under a line now says "wants vitals" by itself.
    private static void HealthWinsArbiter()
    {
        BuffManager Make(byte lifeTraining, out PlayerVitalsCache vitals, out LegacyUiSettings s)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            FakeHost.CombatModeValue = CombatMode.Magic;
            foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
            FakeHost.Skills[(Player, STypeLife)] = (400, lifeTraining);
            var host = FakeHost.Create();
            s = new LegacyUiSettings { IsMacroRunning = true, HealAt = 60, TopOffHP = 60, EmergencyHealAt = 0, StaminaToHealthAt = 0, UsePotions = false };
            var skills = new CharacterSkills(host);
            skills.SetPlayerId(Player);
            var sm = new SpellManager(host, s);
            sm.InitializeNatively();
            sm.SetCharacterSkills(skills);
            vitals = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 40, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 100 };
            var b = new BuffManager(host, s, sm, vitals);
            b.SetCharacterSkills(skills);
            return b;
        }

        var b = Make(2, out var v, out var st);
        Check.True(b.WantsVitalRecharge, "40% health under Heal At 60: wants vitals before CheckVitals ever ran (a busy tick)");
        v.CurrentHealth = 80;
        Check.False(b.WantsVitalRecharge, "80% health: no");
        v.CurrentHealth = 40;
        st.IsMacroRunning = false;
        Check.False(b.WantsVitalRecharge, "macro off: no");

        var none = Make(1, out _, out _);   // Life Magic untrained, no kits, no potions: nothing can heal
        Check.True(none.WantsVitalRecharge, "before any check it still asks");
        none.CheckVitals();
        Check.False(none.WantsVitalRecharge, "after a check that healed nothing: backs off, the bot isn't held");
    }

    // The double-kit bug (2026-10-03): the tick after a kit still read the old health and used
    // another. While a kit is pending, CheckVitals does nothing else (here: no Stamina to Health
    // at 30% health, which it would otherwise cast); the heal line plus a short grace frees it.
    private static void KitResultWait()
    {
        Check.True(CastsStamToHealth(30, 50), "baseline: Stamina to Health is cast at 30% health");

        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        FakeHost.CombatModeValue = CombatMode.Magic;
        foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
        FakeHost.Skills[(Player, STypeLife)] = (400, 2);
        var host = FakeHost.Create();
        var s = new LegacyUiSettings { HealAt = 0, TopOffHP = 0, RestamAt = 0, TopOffStam = 0, GetManaAt = 0, TopOffMana = 0, UsePotions = false };
        var skills = new CharacterSkills(host);
        skills.SetPlayerId(Player);
        var sm = new SpellManager(host, s);
        sm.InitializeNatively();
        sm.SetCharacterSkills(skills);
        var vitals = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 30, MaxStamina = 100, CurrentStamina = 50, MaxMana = 100, CurrentMana = 100 };
        var b = new BuffManager(host, s, sm, vitals);
        b.SetCharacterSkills(skills);

        b.MarkKitUsed();
        Check.True(b.CheckVitals(), "kit pending: CheckVitals holds the vitals step");
        Check.Eq(FakeHost.Casts.Count, 0, "kit pending: no spell cast meanwhile");

        b.OnChatWindowText("Bob says, \"heal yourself\"", 0);
        System.Threading.Thread.Sleep(800);
        Check.True(b.KitPending(), "speech that says \"heal yourself\" doesn't end the wait");
        b.OnChatWindowText("You heal yourself for 52 Health points.", 0);
        Check.True(b.KitPending(), "result line seen: still waiting out the short grace for the vitals");
        System.Threading.Thread.Sleep(800);
        Check.False(b.KitPending(), "after the grace the kit is done");
        Check.True(b.CheckVitals() && FakeHost.Casts.Count > 0, "and the vitals step runs again");

        Check.True(BuffManager.IsKitResultLine("you fail to heal yourself."), "fail line ends the wait");
        Check.True(BuffManager.IsKitResultLine("your movement disrupted healing!"), "disrupted line ends the wait");
        Check.True(BuffManager.IsKitResultLine("lucy mcjuicy is already at full health!"), "full-health line ends the wait");
        Check.False(BuffManager.IsKitResultLine("bob heals you for 40 health points."), "someone else's kit doesn't");
    }

    /// <summary>One CheckVitals on a fresh BuffManager; true when it cast a Stamina to Health spell.</summary>
    private static bool CastsStamToHealth(int healthPct, int staminaPct, System.Action<LegacyUiSettings>? tweak = null)
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;                       // GetCurrentCombatMode
        FakeHost.CombatModeValue = CombatMode.Magic;       // no wand swap in the way
        foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
        FakeHost.Skills[(Player, STypeLife)] = (400, 2);
        var host = FakeHost.Create();

        var s = new LegacyUiSettings
        {
            HealAt = 0, TopOffHP = 0, RestamAt = 0, TopOffStam = 0, GetManaAt = 0, TopOffMana = 0,
            UsePotions = false,
        };
        tweak?.Invoke(s);

        var skills = new CharacterSkills(host);
        skills.SetPlayerId(Player);
        var sm = new SpellManager(host, s);
        sm.InitializeNatively();
        sm.SetCharacterSkills(skills);
        var vitals = new PlayerVitalsCache
        {
            MaxHealth = 100, CurrentHealth = (uint)healthPct,
            MaxStamina = 100, CurrentStamina = (uint)staminaPct,
            MaxMana = 100, CurrentMana = 100,
        };
        var b = new BuffManager(host, s, sm, vitals);
        b.SetCharacterSkills(skills);

        int stamToHealth = sm.GetDynamicSelfBuffId("Stamina to Health Self", AcSkillType.LifeMagic);
        Check.True(stamToHealth != 0, "a Stamina to Health spell is known at this skill");
        bool handled = b.CheckVitals();
        bool cast = FakeHost.Casts.Any(c => c.SpellId == stamToHealth);
        Check.Eq(handled, cast, $"hp {healthPct} stam {staminaPct}: CheckVitals reports handled exactly when it cast");
        return cast;
    }

    private static void StaminaToHealthThresholds()
    {
        // Defaults: today's behaviour (30% health, stamina over 20%).
        Check.True(CastsStamToHealth(30, 50), "defaults: cast at 30% health");
        Check.False(CastsStamToHealth(31, 50), "defaults: not above 30% health");
        Check.False(CastsStamToHealth(30, 20), "defaults: not at 20% stamina (must be over it)");
        Check.True(CastsStamToHealth(30, 21), "defaults: cast at 21% stamina");

        // Stamina To Health At is its own line.
        Check.False(CastsStamToHealth(10, 80, s => s.StaminaToHealthAt = 0), "Stamina To Health At 0: never cast");
        Check.True(CastsStamToHealth(45, 80, s => { s.StaminaToHealthAt = 50; s.EmergencyHealAt = 0; }),
            "Stamina To Health At 50 casts at 45% even with Emergency Heal At off");
        Check.False(CastsStamToHealth(45, 80, s => { s.StaminaToHealthAt = 30; s.EmergencyHealAt = 60; }),
            "Emergency Heal At 60 doesn't cast Stamina to Health above its own 30% line");

        // Stamina To Health Min Stamina.
        Check.False(CastsStamToHealth(20, 40, s => s.StaminaToHealthMinStamina = 50), "min stamina 50: not at 40%");
        Check.True(CastsStamToHealth(20, 60, s => s.StaminaToHealthMinStamina = 50), "min stamina 50: cast at 60%");
        Check.True(CastsStamToHealth(20, 5, s => s.StaminaToHealthMinStamina = 0), "min stamina 0: cast at 5%");

        // The pure predicate the emergency step uses.
        var d = new LegacyUiSettings();
        Check.True(BuffManager.StaminaToHealthAllowed(d, 30, 21), "predicate: 30 / 21 on defaults");
        Check.False(BuffManager.StaminaToHealthAllowed(d, 31, 99), "predicate: 31 on defaults");
    }

    private static BuffManager RefreshRig(out LegacyUiSettings s, out PlayerVitalsCache vitals, int healthPct = 100)
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        FakeHost.CombatModeValue = CombatMode.Magic;
        foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
        FakeHost.Skills[(Player, STypeLife)] = (400, 2);
        var host = FakeHost.Create();
        s = new LegacyUiSettings { IsMacroRunning = true, HealAt = 60, TopOffHP = 60, EmergencyHealAt = 30, StaminaToHealthAt = 0,
                                   GetManaAt = 0, TopOffMana = 0, RestamAt = 0, TopOffStam = 0, UsePotions = false, EnableBuffing = false };
        var skills = new CharacterSkills(host);
        skills.SetPlayerId(Player);
        var sm = new SpellManager(host, s);
        sm.InitializeNatively();
        sm.SetCharacterSkills(skills);
        vitals = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = (uint)healthPct, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 100 };
        var b = new BuffManager(host, s, sm, vitals);
        b.SetCharacterSkills(skills);
        return b;
    }

    // The engine reload at 13:42:55 looked like a login to RynthAi: the buff refresh waited 5.8 s
    // for two equal reads of a registry that was already complete (the client never left the world).
    private static void HotReloadRefresh()
    {
        int strength = SpellTable.Named("Strength Self I").First().Id;
        int endurance = SpellTable.Named("Endurance Self I").First().Id;

        var b = RefreshRig(out _, out _);
        FakeHost.ServerTime = 1000;
        FakeHost.Enchantments.Add(((uint)strength, 1000 + 3600));
        FakeHost.Enchantments.Add(((uint)endurance, 1000 + 3600));
        b.CarriedOverSession = true;
        b.OnHeartbeat();
        Check.True(b.LiveBuffsReady, "hot reload: ready on the first successful read");
        Check.True(FakeHost.Logs.Any(l => l.Contains("login refresh ready") && l.Contains("hot reload")), "and the line says why");

        var real = RefreshRig(out _, out _);
        FakeHost.ServerTime = 1000;
        FakeHost.Enchantments.Add(((uint)strength, 1000 + 3600));
        real.OnHeartbeat();
        Check.False(real.LiveBuffsReady, "a real login: one read isn't trusted (the registry may still be streaming)");
        System.Threading.Thread.Sleep(1100);
        real.OnHeartbeat();
        Check.True(real.LiveBuffsReady, "two equal reads a second apart: ready");
    }

    private static void ReloadBlockerReport()
    {
        // The pure text.
        Check.Eq(ReloadSafety.Reason(false, true, "Emergency Heal At", 12, 30, "Heal Self V"), "", "macro off: never blocks a reload");
        Check.Eq(ReloadSafety.Reason(true, false, "", 100, 0, ""), "", "nothing going on: safe");
        Check.Eq(ReloadSafety.Reason(true, true, "", 100, 0, ""), "in combat (a monster engaged)", "a monster engaged");
        Check.Eq(ReloadSafety.Reason(true, false, "Emergency Heal At", 12, 30, ""), "in combat (health 12% under Emergency Heal At 30%)", "under a heal line");
        Check.Eq(ReloadSafety.Reason(true, false, "", 100, 0, "Strength Self VI"), "casting ('Strength Self VI' awaiting its result)", "a buff in flight");
        Check.Eq(ReloadSafety.Reason(true, true, "Heal At", 45, 60, "Heal Self V"),
                 "in combat (a monster engaged, health 45% under Heal At 60%, 'Heal Self V' awaiting its result)", "all three");

        // What BuffManager feeds it.
        var b = RefreshRig(out _, out var v, healthPct: 50);
        b.MarkLiveBuffsReadyForTests();
        Check.Eq(b.HealLineNow(out int hp, out int lineValue), "Top Off HP", "50% under Top Off HP (no monster: the idle line)");
        Check.Eq((hp, lineValue), (50, 60), "with the health and the line");
        Check.Eq(b.CastInFlightName, "", "nothing cast yet");
        Check.True(b.CheckVitals(), "Heal Self is cast");
        int healCat = SpellTable.Named("Heal Self I").First().Category;
        Check.True(b.CastInFlightName.Length > 0 && SpellTable.Named(b.CastInFlightName).Any(row => row.Category == healCat),
                   $"the cast in flight is a Heal Self tier: {b.CastInFlightName}");
        v.CurrentHealth = 100;
        Check.Eq(b.HealLineNow(out _, out _), "", "full health: no line");
        v.MaxHealth = 0;
        Check.Eq(b.HealLineNow(out _, out _), "", "vitals not read yet: no line");
    }

    private static void StaminaToHealthDefaults()
    {
        var old = JsonSerializer.Deserialize("{\"EmergencyHealAt\":25}", RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(old != null, "an old profile parses");
        Check.Eq(old!.EmergencyHealAt, 25, "Emergency Heal At kept");
        Check.Eq(old.StaminaToHealthAt, 30, "Stamina To Health At defaults to 30");
        Check.Eq(old.StaminaToHealthMinStamina, 20, "Stamina To Health Min Stamina defaults to 20");

        var bridge = JsonSerializer.Deserialize("{\"EmergencyHealAt\":25}", RynthAiJsonContext.Default.SettingsBridgePayload);
        Check.Eq(bridge!.StaminaToHealthAt, 30, "settings bridge: Stamina To Health At defaults to 30");
        Check.Eq(bridge.StaminaToHealthMinStamina, 20, "settings bridge: Stamina To Health Min Stamina defaults to 20");
    }
}
