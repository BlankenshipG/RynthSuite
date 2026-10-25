using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthILT.Hub;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthILT.Tests;

// The Vaulted Dungeons keeper (Hub/IltVaultedDungeons.cs, Silentkelpie's, taken in with the owner's
// safety rules): ILT only, a master switch (RynthAi's setting, off by default) that gates everything,
// no resume of Active from the saved file, a recall that waits out combat / buffing, /rc quit once per
// activation and never while gated, and a no-kill timer that restarts on kills and captures.
// The clock, the master switch, RynthAi's activity and the gem use are the module's test seams.
internal static class VaultedDungeonsTests
{
    private const uint Player = 0x50000C01;
    private const uint Quarry = 0x01F7;        // dungeon 1: Tyrant's Quarry
    private const uint Elsewhere = 0x0001;
    private const string Luminance = "You've banked 1,234 Luminance.";

    public static void Register(Runner r)
    {
        r.Add("vaulted: not an ILT world -> inert (no tick work, no recall, no quit, a 'not available' reply)", NotIlt);
        r.Add("vaulted: master off -> inert even with a saved Active, Auto Start and Kill Client", MasterOff);
        r.Add("vaulted: master on + explicit start -> recalls at 1 s then every 5 s outside, stops inside; a saved Active is not resumed", ExplicitStartRecalls);
        r.Add("vaulted: Auto Start On Arrival is honoured only with the master on", AutoStart);
        r.Add("vaulted: Kill Client fires once per activation and restarts on luminance, a killing blow and a capture", QuitOnceAndResets);
        r.Add("vaulted: Kill Client is never sent once the master goes off, even between the timer and the send", QuitNeverWhenGated);
        r.Add("vaulted: a busy bot (combat, buffing, server wait, client busy) defers the recall; the warning keeps its cadence", BusyDefers);
        r.Add("vaulted: turning the master off mid-run stops it at once and says so once; turning it on starts Off", MasterOffMidRun);
        r.Add("vaulted: the tab and /ra hub open vaulted follow ILT + the master; settings file format kept", TabAndFile);
        r.Add("vaulted: capture lines are the Pets tab's own success replies", CaptureLines);
        r.Add("vaulted: settings go through the source-generated JSON context (NativeAOT has no reflection serializer)", SourceGeneratedContext);
    }

    // ── Rig ──────────────────────────────────────────────────────────────────

    private sealed class Rig
    {
        public IltHubController Hub = null!;
        public IltVaultedDungeons V = null!;
        public string Dir = "";
        public long T = 1_000_000;
        public bool Master;
        public RynthAiActivity? Act = RynthAiActivity.None;
        public int MasterCalls, AllowCalls = int.MaxValue;
        public readonly List<string> GemsUsed = new();
        public bool HasGem = true;

        public int Quits => FakeHost.ChatCommands.Count(c => c == "/rc quit");
        public int ChatCount(string part) => FakeHost.Chat.Count(c => c.Contains(part, StringComparison.OrdinalIgnoreCase));

        public void Step(long ms) { T += ms; V.Tick(T); }
        public void Inside() => FakeHost.PlayerPose = ((Quarry << 16) | 0x0100u, 0, 0, 0);
        public void Outside() => FakeHost.PlayerPose = ((Elsewhere << 16) | 0x0100u, 0, 0, 0);
        public string SettingsFile => Path.Combine(Dir, "VaultedDungeons", "settings.json");
        public void WriteSettings(string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, json);
        }
    }

    private static Rig NewRig(string world, string folder, bool master, bool busyCalls = false)
    {
        FakeHost.Reset();
        FakeHost.WorldName = world;
        FakeHost.BusyCalls = busyCalls;
        var host = FakeHost.Create(Player);
        var rig = new Rig { Master = master, Dir = Path.Combine(Program.TempRoot, folder) };
        Directory.CreateDirectory(rig.Dir);
        rig.Hub = new IltHubController(host, rig.Dir, new IltSettings(), new RynthAiClient(host));
        rig.V = rig.Hub.VaultedDungeons;
        rig.V.Clock = () => rig.T;
        rig.V.MasterEnabled = () => { rig.MasterCalls++; return rig.Master && rig.MasterCalls <= rig.AllowCalls; };
        rig.V.Activity = () => rig.Act;
        rig.V.UseGem = name => { if (!rig.HasGem) return false; rig.GemsUsed.Add(name); return true; };
        rig.Outside();
        return rig;
    }

    /// <summary>Dungeon 1 (Tyrant's Quarry), the primary gem, the given extras. "active": true is on disk to prove it is ignored.</summary>
    private static string Settings(string extras = "")
        => "{ \"active\": true, \"dungeon\": 1, \"gem\": 1" + (extras.Length > 0 ? ", " + extras : "") + " }";

    // ── ILT only ─────────────────────────────────────────────────────────────

    private static void NotIlt()
    {
        var rig = NewRig("Aelrynth", "vd-notilt", master: true);
        rig.WriteSettings(Settings("\"autoStart\": true, \"quit\": true, \"quitMinutes\": 1"));
        string before = File.ReadAllText(rig.SettingsFile);

        rig.Outside();
        for (int i = 0; i < 20; i++) rig.Step(1000);
        rig.Inside();
        for (int i = 0; i < 10; i++) rig.Step(30_000);
        Check.False(rig.V.IsActive, "not running on another world, though the master is on and Auto Start was saved");
        Check.Eq(rig.GemsUsed.Count, 0, "no recall gem");
        Check.Eq(rig.Quits, 0, "no /rc quit");
        Check.Eq(FakeHost.ChatCommands.Count, 0, "no command of any kind (no /vt, /ub or /rc)");
        Check.Eq(rig.ChatCount("[Vaulted Dungeons]"), 0, "and not a word in chat");
        Check.Eq(File.ReadAllText(rig.SettingsFile), before, "the settings file wasn't even read or rewritten");

        rig.V.HandleCommand(new[] { "on" });
        Check.False(rig.V.IsActive, "/ilt vaulted on is refused");
        Check.Eq(rig.ChatCount("Not available on this server"), 1, "with the 'not available on this server' reply");
        FakeHost.Chat.Clear();
        rig.V.HandleCommand(new[] { "status" });
        Check.Eq(rig.ChatCount("Not available on this server"), 1, "status says the same");
        Check.False(rig.Hub.TabShown(IltSection.VaultedDungeons), "no tab");
        Check.False(rig.Hub.SectionAvailable(IltSection.VaultedDungeons), "not available");

        // The controller doesn't tick it either (and the module would refuse anyway).
        rig.Hub.Tick();
        Check.Eq(rig.GemsUsed.Count, 0, "the controller's tick does nothing here");
    }

    // ── Master switch ────────────────────────────────────────────────────────

    private static void MasterOff()
    {
        var rig = NewRig("InfiniteLeaftide", "vd-masteroff", master: false);
        rig.WriteSettings(Settings("\"autoStart\": true, \"quit\": true, \"quitMinutes\": 1, \"disableMeta\": true"));
        string before = File.ReadAllText(rig.SettingsFile);

        rig.Outside();
        for (int i = 0; i < 20; i++) rig.Step(1000);
        rig.Inside();
        for (int i = 0; i < 10; i++) rig.Step(30_000);     // five minutes inside: a one-minute Kill Client would have fired
        Check.False(rig.V.IsActive, "off: a saved Active and Auto Start start nothing");
        Check.Eq(rig.GemsUsed.Count, 0, "no recall gem");
        Check.Eq(rig.Quits, 0, "no /rc quit");
        Check.Eq(FakeHost.ChatCommands.Count, 0, "no arrival commands (/vt opt set ...) either");
        Check.Eq(rig.ChatCount("[Vaulted Dungeons]"), 0, "silent");
        Check.Eq(File.ReadAllText(rig.SettingsFile), before, "settings untouched");
        Check.True(rig.Hub.Options.IsIltLikeWorld, "(it is an ILT world: the master switch is what holds it)");
        Check.False(rig.Hub.TabShown(IltSection.VaultedDungeons), "the tab is hidden");
        Check.False(rig.Hub.SectionAvailable(IltSection.VaultedDungeons), "and not available");

        rig.V.HandleCommand(new[] { "on" });
        Check.False(rig.V.IsActive, "/ilt vaulted on is refused");
        Check.Eq(rig.ChatCount("Settings > Navigation"), 1, "and says where the switch is");
        rig.V.HandleCommand(new[] { "toggle" });
        Check.False(rig.V.IsActive, "toggle too");

        // Through the controller, the real wiring: nothing either.
        for (int i = 0; i < 5; i++) rig.Hub.Tick();
        Check.Eq(rig.GemsUsed.Count, 0, "the controller's tick does nothing while off");

        // The lines that restart the timer are ignored too (nothing is running).
        rig.V.OnChat(Luminance);
        rig.V.OnKillNotification();
        Check.Eq(rig.Quits, 0, "still nothing");
    }

    // ── Recalls ──────────────────────────────────────────────────────────────

    private static void ExplicitStartRecalls()
    {
        var rig = NewRig("InfiniteLeaftide", "vd-recall", master: true);
        rig.WriteSettings(Settings());   // "active": true on disk: ignored
        rig.Outside();
        rig.Step(0);
        Check.False(rig.V.IsActive, "a saved Active is not resumed after login");
        for (int i = 0; i < 10; i++) rig.Step(1000);
        Check.Eq(rig.GemsUsed.Count, 0, "so nothing is recalled until the player starts it");

        rig.V.HandleCommand(new[] { "on" });
        Check.True(rig.V.IsActive, "an explicit start");
        rig.Step(0);                       // first tick outside: the clock starts
        rig.Step(500);
        Check.Eq(rig.GemsUsed.Count, 0, "0.5 s out: too early");
        rig.Step(500);                     // 1 s
        Check.Eq(rig.GemsUsed.Count, 1, "the first recall comes 1 s after leaving");
        Check.Eq(rig.GemsUsed[0], "Primary Portal Recall Gem", "the chosen gem");
        rig.Step(4000);
        Check.Eq(rig.GemsUsed.Count, 1, "4 s later: not yet");
        rig.Step(1000);                    // 5 s since the last use
        Check.Eq(rig.GemsUsed.Count, 2, "then every 5 s");
        rig.Step(5000);
        Check.Eq(rig.GemsUsed.Count, 3, "again");

        rig.Inside();
        rig.Step(1000);
        for (int i = 0; i < 12; i++) rig.Step(1000);
        Check.Eq(rig.GemsUsed.Count, 3, "inside the dungeon: no more gems");

        FakeHost.Portaling = true;
        rig.Outside();
        rig.Step(10_000);
        Check.Eq(rig.GemsUsed.Count, 3, "portal space: nothing used");
        FakeHost.Portaling = false;
        rig.Step(0);
        rig.Step(1000);
        Check.Eq(rig.GemsUsed.Count, 4, "out again: 1 s, then a recall");

        rig.HasGem = false;
        rig.Step(5000);
        Check.Eq(rig.GemsUsed.Count, 4, "no gem in the pack: none used");
        Check.True(rig.ChatCount("No Primary Portal Recall Gem") >= 1, "and it says so");
    }

    private static void AutoStart()
    {
        // Master on: arriving in the chosen dungeon switches it on, and the arrival commands go out.
        var on = NewRig("InfiniteLeaftide", "vd-auto-on", master: true);
        on.WriteSettings(Settings("\"autoStart\": true, \"disableMeta\": true"));
        on.Inside();
        on.Step(0);
        Check.True(on.V.IsActive, "Auto Start On Arrival switches the keeper on (master on)");
        Check.True(FakeHost.ChatCommands.Contains("/vt opt set EnableMeta False"), "and runs the arrival commands");

        // Auto Start off, master on: a relog inside does not resume a saved Active.
        var manual = NewRig("InfiniteLeaftide", "vd-auto-off", master: true);
        manual.WriteSettings(Settings("\"disableMeta\": true"));
        manual.Inside();
        manual.Step(0);
        Check.False(manual.V.IsActive, "no Auto Start: not resumed");
        Check.Eq(FakeHost.ChatCommands.Count, 0, "and the arrival commands wait for an Active keeper");

        // Master off, Auto Start saved: nothing; then the master comes on while inside.
        var gated = NewRig("InfiniteLeaftide", "vd-auto-gated", master: false);
        gated.WriteSettings(Settings("\"autoStart\": true"));
        gated.Inside();
        gated.Step(0);
        Check.False(gated.V.IsActive, "master off: Auto Start does nothing");
        gated.Master = true;
        gated.Step(1000);
        Check.True(gated.V.IsActive, "with the master on, Auto Start (the player's own saved choice) applies");
    }

    // ── Kill Client ──────────────────────────────────────────────────────────

    private static Rig QuitRig(string folder, int minutes = 2)
    {
        var rig = NewRig("InfiniteLeaftide", folder, master: true);
        rig.WriteSettings(Settings("\"quit\": true, \"quitMinutes\": " + minutes));
        rig.Inside();
        rig.Step(0);
        rig.V.HandleCommand(new[] { "on" });
        rig.Step(0);       // inside: the no-kill clock starts here
        return rig;
    }

    private static void QuitOnceAndResets()
    {
        // Luminance line.
        var rig = QuitRig("vd-quit-lum");
        rig.Step(119_000);
        Check.Eq(rig.Quits, 0, "just under 2 minutes: no quit");
        rig.V.OnChat(Luminance);
        rig.Step(119_000);
        Check.Eq(rig.Quits, 0, "the luminance line restarted the timer");
        rig.Step(2_000);
        Check.Eq(rig.Quits, 1, "2 minutes after the last kill: /rc quit");
        for (int i = 0; i < 10; i++) rig.Step(60_000);
        Check.Eq(rig.Quits, 1, "and only once");
        // A kill after the quit went out doesn't arm a second one (once per activation).
        rig.V.OnChat(Luminance);
        for (int i = 0; i < 10; i++) rig.Step(60_000);
        Check.Eq(rig.Quits, 1, "still once");

        // The player's own killing blow.
        rig = QuitRig("vd-quit-kill");
        rig.Step(110_000);
        rig.V.OnKillNotification();
        rig.Step(110_000);
        Check.Eq(rig.Quits, 0, "a killing blow restarted the timer");
        rig.Step(15_000);
        Check.Eq(rig.Quits, 1, "and it fires after the next idle stretch");

        // A pet capture.
        rig = QuitRig("vd-quit-capture");
        rig.Step(110_000);
        rig.V.OnChat("You have successfully siphoned the essence of the Olthoi Warrior.");
        rig.Step(110_000);
        Check.Eq(rig.Quits, 0, "a capture restarted the timer");
        rig.V.OnChat("The echo resonates perfectly.");
        rig.Step(110_000);
        Check.Eq(rig.Quits, 0, "so does the other success reply");
        rig.Step(15_000);
        Check.Eq(rig.Quits, 1, "then it fires");

        // Lines that are not kills don't restart it.
        rig = QuitRig("vd-quit-noise");
        rig.Step(110_000);
        rig.V.OnChat("You hit the Olthoi Warrior for 50 points of damage.");
        rig.V.OnChat("The essence is not ready yet.");
        rig.V.OnChat("Bob says, \"You've banked 5 Luminance.\"");     // a player's chat, not the system line
        rig.Step(15_000);
        Check.Eq(rig.Quits, 1, "damage lines, a refused capture and someone else's chat don't count");

        // The minutes are the player's setting: 1 minute here.
        rig = QuitRig("vd-quit-1m", minutes: 1);
        rig.Step(59_000);
        Check.Eq(rig.Quits, 0, "1 minute setting: not yet");
        rig.Step(2_000);
        Check.Eq(rig.Quits, 1, "fires at 1 minute");

        // Off -> on again is a new activation: one more quit may be sent.
        rig.V.HandleCommand(new[] { "off" });
        rig.V.HandleCommand(new[] { "on" });
        rig.Step(0);
        rig.Step(61_000);
        Check.Eq(rig.Quits, 2, "a new activation gets its own single quit");
    }

    private static void QuitNeverWhenGated()
    {
        // The master goes off, the timer would have fired: nothing is sent, and it stopped.
        var rig = QuitRig("vd-quit-gated");
        rig.Step(100_000);
        rig.Master = false;
        rig.Step(30_000);
        Check.Eq(rig.Quits, 0, "master off before the timer: no /rc quit");
        Check.False(rig.V.IsActive, "and the keeper is off");
        rig.Step(600_000);
        Check.Eq(rig.Quits, 0, "ever");

        // The master goes off between the timer firing and the send (asked again right before sending).
        rig = QuitRig("vd-quit-race");
        rig.Step(100_000);
        rig.AllowCalls = rig.MasterCalls + 1;       // true at the top of the next tick, false when it re-checks
        rig.Step(21_000);                            // the timer fires in this tick
        Check.Eq(rig.Quits, 0, "master off between the timer firing and the send: not sent");
        Check.False(rig.V.IsActive, "the keeper stopped");
        Check.Eq(rig.ChatCount("Stopped"), 1, "said once");

        // Not an ILT world any more (the world name changes): same.
        rig = QuitRig("vd-quit-world");
        rig.Step(100_000);
        FakeHost.WorldName = "Aelrynth";
        rig.Step(30_000);
        Check.Eq(rig.Quits, 0, "world no longer ILT: no /rc quit");
        Check.False(rig.V.IsActive, "stopped");

        // Not Active: the timer has nothing to do (the player turned it off).
        rig = QuitRig("vd-quit-off");
        rig.V.HandleCommand(new[] { "off" });
        for (int i = 0; i < 10; i++) rig.Step(60_000);
        Check.Eq(rig.Quits, 0, "Off: no quit");
    }

    // ── Combat / buff guard ──────────────────────────────────────────────────

    private static void BusyDefers()
    {
        var rig = NewRig("InfiniteLeaftide", "vd-busy", master: true, busyCalls: true);
        rig.WriteSettings(Settings());
        rig.Outside();
        rig.Step(0);
        rig.V.HandleCommand(new[] { "on" });
        rig.Act = RynthAiActivity.MacroRunning | RynthAiActivity.Combat;
        rig.Step(0);
        rig.Step(1000);
        rig.Step(4000);
        Check.Eq(rig.GemsUsed.Count, 0, "in combat: the recall is deferred");
        Check.Eq(rig.ChatCount("Waiting to use the Primary Portal Recall Gem"), 1, "and says why, once");
        rig.Step(20_000);
        Check.Eq(rig.ChatCount("Waiting to use"), 1, "no repeat inside the 30 s warning cadence");
        rig.Step(10_000);
        Check.Eq(rig.ChatCount("Waiting to use"), 2, "the warning comes again after 30 s");
        Check.Eq(rig.GemsUsed.Count, 0, "still nothing used");

        rig.Act = RynthAiActivity.MacroRunning;       // clear
        rig.Step(100);
        Check.Eq(rig.GemsUsed.Count, 1, "the first clear tick recalls (it was due all along)");

        // Buffing, the server wait, looting, salvaging, crafting and RynthAi not answering all defer too.
        foreach (RynthAiActivity? busy in new RynthAiActivity?[]
                 { RynthAiActivity.Buffing, RynthAiActivity.ServerAction, RynthAiActivity.Looting, RynthAiActivity.Salvaging, RynthAiActivity.Crafting, null })
        {
            int before = rig.GemsUsed.Count;
            rig.Act = busy;
            rig.Step(6000);
            Check.Eq(rig.GemsUsed.Count, before, $"busy ({(busy.HasValue ? busy.Value.ToString() : "RynthAi not ready")}): deferred");
            rig.Act = RynthAiActivity.None;
            rig.Step(100);
            Check.Eq(rig.GemsUsed.Count, before + 1, "and used once it is clear");
        }

        // The client's own busy hourglass.
        FakeHost.BusyStateValue = 1;
        int n = rig.GemsUsed.Count;
        rig.Step(6000);
        Check.Eq(rig.GemsUsed.Count, n, "the client is busy: deferred");
        FakeHost.BusyStateValue = 0;
        rig.Step(100);
        Check.Eq(rig.GemsUsed.Count, n + 1, "used when it clears");

        // Busy never blocks the Kill Client (it is not a game action that can be lost).
        var q = NewRig("InfiniteLeaftide", "vd-busy-quit", master: true);
        q.WriteSettings(Settings("\"quit\": true, \"quitMinutes\": 1"));
        q.Inside();
        q.Step(0);
        q.V.HandleCommand(new[] { "on" });
        q.Step(0);
        q.Act = RynthAiActivity.Combat;
        q.Step(61_000);
        Check.Eq(q.Quits, 1, "a busy bot still gets its Kill Client");
    }

    // ── Master off mid-run ───────────────────────────────────────────────────

    private static void MasterOffMidRun()
    {
        var rig = NewRig("InfiniteLeaftide", "vd-midrun", master: true);
        rig.WriteSettings(Settings("\"quit\": true, \"quitMinutes\": 1"));
        rig.Outside();
        rig.Step(0);
        rig.V.HandleCommand(new[] { "on" });
        rig.Step(0);
        rig.Step(1000);
        Check.Eq(rig.GemsUsed.Count, 1, "running: a recall went out");

        rig.Master = false;
        rig.Step(10);
        Check.False(rig.V.IsActive, "the master went off: stopped on that very tick");
        Check.Eq(rig.ChatCount("Stopped"), 1, "and it said so");
        for (int i = 0; i < 20; i++) rig.Step(5000);
        Check.Eq(rig.GemsUsed.Count, 1, "no more recalls");
        Check.Eq(rig.ChatCount("Stopped"), 1, "and the notice wasn't repeated");
        Check.Eq(rig.Quits, 0, "no quit");

        rig.Master = true;
        rig.Step(10);
        Check.False(rig.V.IsActive, "turned back on: it starts Off, nothing resumes by itself");
        rig.Step(10_000);
        Check.Eq(rig.GemsUsed.Count, 1, "nothing recalled");
        rig.V.HandleCommand(new[] { "on" });
        rig.Step(0);
        rig.Step(1000);
        Check.Eq(rig.GemsUsed.Count, 2, "until the player starts it again");

        // The Mini Remote button path (posted to the pump) re-checks the switch as well.
        rig.V.HandleCommand(new[] { "off" });
        rig.Master = false;
        rig.Hub.Context.Post(() => rig.V.HandleCommand(new[] { "on" }));
        rig.Hub.Tick();
        Check.False(rig.V.IsActive, "an On click that lands after the switch went off is refused");
    }

    // ── Tab, commands, file ──────────────────────────────────────────────────

    private static void TabAndFile()
    {
        var rig = NewRig("InfiniteLeaftide", "vd-tab", master: true);
        Check.True(rig.Hub.TabShown(IltSection.VaultedDungeons), "ILT + master on: the tab is shown");
        Check.True(IltSections.Tabs.Contains(IltSection.VaultedDungeons), "it is one of the tab-bar tabs");
        Check.True(IltSections.IsTab(IltSection.VaultedDungeons), "a Mini Remote tab");
        Check.Eq(IltSections.TabId(IltSection.VaultedDungeons), "vaulted", "tab id");
        Check.True(IltSections.TryParse("vaulted", out var s) && s == IltSection.VaultedDungeons, "/ra hub open vaulted parses");
        Check.True(IltSections.TryParse("vaulted-dungeons", out s) && s == IltSection.VaultedDungeons, "and the long names");
        Check.True(RynthCore.Plugin.RynthILT.RynthILTPlugin.IsIltVerb(new[] { "hub", "open", "vaulted" }), "RynthILT takes '/ra hub open vaulted'");
        rig.Master = false;
        Check.False(rig.Hub.TabShown(IltSection.VaultedDungeons), "master off: hidden");

        // The file: the format Silentkelpie's keeper wrote, kept. A start is saved.
        rig = NewRig("InfiniteLeaftide", "vd-file", master: true);
        rig.WriteSettings("""{ "dungeon": 3, "gem": 2, "quit": true, "quitMinutes": 7, "autoStart": false, "attackPower": 0.5 }""");
        rig.V.HandleCommand(new[] { "status" });
        Check.Eq(rig.ChatCount("dungeon=Wailing Cells, gem=Secondary Portal Recall Gem, kill-client=7m"), 1, "dungeon list and gem names as upstream");
        rig.V.HandleCommand(new[] { "on" });
        rig.Step(0);
        string saved = File.ReadAllText(rig.SettingsFile);
        Check.True(saved.Contains("\"active\": true") && saved.Contains("\"quitMinutes\": 7") && saved.Contains("\"dungeon\": 3"),
            "saved in the same camelCase layout");

        // A new session (relog) reads it back with Active Off.
        var again = NewRig("InfiniteLeaftide", "vd-file", master: true);
        FakeHost.Chat.Clear();
        again.V.HandleCommand(new[] { "status" });
        Check.True(again.ChatCount("Off — dungeon=Wailing Cells") == 1 && !again.V.IsActive, "the next session starts Off, with the same choices");
    }

    // The NativeAOT build (what ships) turns reflection-based System.Text.Json off, so the keeper's
    // settings must use IltVaultedDungeonsJsonContext. This project runs on the normal runtime where
    // reflection would still work, so assert the generated metadata exists and that the file layout
    // is what the context (not an options object) produces.
    private static void SourceGeneratedContext()
    {
        var ctx = IltVaultedDungeonsJsonContext.Default;
        Check.True(ctx.Cfg != null, "the context has metadata for the settings type (IltVaultedDungeonsJsonContext.Default.Cfg)");
        Check.True(ctx.GetTypeInfo(typeof(IltVaultedDungeons.Cfg)) != null, "and resolves it by type");

        var cfg = new IltVaultedDungeons.Cfg
        {
            Active = true, Dungeon = 3, Gem = 2, Quit = true, QuitMinutes = 7, ShowInDungeon = true,
            AutoStart = true, DisableMeta = true, DisableNav = false, RepeatAttack = true, SetPower = true, AttackPower = 0.5f,
        };
        string json = System.Text.Json.JsonSerializer.Serialize(cfg, ctx.Cfg);
        Check.True(json.Contains("\"active\": true") && json.Contains("\"quitMinutes\": 7") && json.Contains("\"attackPower\": 0.5"),
            "serializes indented with camelCase names");
        Check.True(json.Contains("\n"), "indented");

        var back = System.Text.Json.JsonSerializer.Deserialize(json, ctx.Cfg);
        Check.True(back != null, "round-trips");
        Check.True(back!.Active && back.Dungeon == 3 && back.Gem == 2 && back.Quit && back.QuitMinutes == 7 && back.ShowInDungeon
                   && back.AutoStart && back.DisableMeta && !back.DisableNav && back.RepeatAttack && back.SetPower && back.AttackPower == 0.5f,
            "every field survives");

        // The shape of the file Silentkelpie's keeper wrote (camelCase, partial) still reads.
        var upstream = System.Text.Json.JsonSerializer.Deserialize(
            """{ "dungeon": 2, "gem": 1, "quit": true, "quitMinutes": 5 }""", ctx.Cfg);
        Check.True(upstream != null && upstream.Dungeon == 2 && upstream.Gem == 1 && upstream.Quit && upstream.QuitMinutes == 5 && !upstream.Active,
            "a partial upstream file reads, missing fields keep their defaults");

        bool threw = false;
        try { System.Text.Json.JsonSerializer.Deserialize("{ not json", ctx.Cfg); } catch (System.Text.Json.JsonException) { threw = true; }
        Check.True(threw, "a corrupt file still throws, which the keeper's bad-file copy relies on");
    }

    private static void CaptureLines()
    {
        Check.True(PetCaptureLines.IsSuccess("You have successfully siphoned the essence."), "siphoned");
        Check.True(PetCaptureLines.IsSuccess("The echo resonates perfectly."), "echo");
        Check.True(PetCaptureLines.IsSuccess("The essence is captured!"), "captured");
        Check.False(PetCaptureLines.IsSuccess("The creature is too strong to be siphoned."), "a refusal is not a success");
        Check.False(PetCaptureLines.IsSuccess("siphon failed"), "nor a failure");
        Check.False(PetCaptureLines.IsSuccess(""), "empty");
    }
}
