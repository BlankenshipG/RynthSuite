using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.MiniRemote;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The ILT Hub left RynthAi for the RynthILT plugin (2026-10-13). What stayed: the Mini Remote with its
// universal Quests and Pets tabs (their settings now in miniremote.json, read once from the old
// ilt-hub.json), the "/ra hub|quests|pets" commands (the ILT verbs go to RynthILT, or say it's
// needed), the RynthAi.Hub interface RynthILT borrows from, and the profile's old Server Features
// fields kept for RynthILT's one-time migration.
internal static class MiniRemoteMoveTests
{
    private const uint Player = 0x50000E01;

    private const string LegacyHubJson = """
        {
          "Bank": { "Pyreals": 99 },
          "Pet": { "HealingEnabled": true, "HealPetName": "Healing Buddy", "RosterKind": 1, "UsePyrealRefillPath": true,
                   "Assignments": [ { "Name": "Fire Banshee Essence", "Kind": 2 }, { "Name": "", "Kind": 1 } ], "PetLog": ["x"] },
          "Character": { "QuestFilter": "temple", "QuestFavorites": ["foo", ""], "ShowQuestFavoritesHud": true, "AttrKeepXp": 5 },
          "Gear": { "LastUsdPath": "C:\\vt\\me.usd", "AutoClap": true }
        }
        """;

    public static void Register(Runner r)
    {
        r.Add("mini remote: Pets / Quests settings come over from ilt-hub.json once, which is never rewritten", LegacyState);
        r.Add("mini remote: without RynthILT there are no ILT tabs, bits or verbs", NoRynthIlt);
        r.Add("mini remote: /ra hub|quests|pets stay RynthAi's; quest windows point to RynthOracle; ILT verbs say RynthILT is needed", Commands);
        r.Add("mini remote: the pyreal refill path never spends without RynthILT's confirmation", RefillNeedsIlt);
        r.Add("profile: the old Server Features fields still round-trip (RynthILT migrates them)", LegacyProfileFields);
        r.Add("RynthAi.Hub: table, spell names, nothing before login", HubTable);
    }

    private static string Folder(string name)
    {
        string dir = Path.Combine(Program.TempRoot, "miniremote-" + name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void LegacyState()
    {
        string dir = Folder("legacy");
        string legacyPath = Path.Combine(dir, MiniRemoteStore.LegacyFileName);
        File.WriteAllText(legacyPath, LegacyHubJson);

        var store = new MiniRemoteStore(dir);
        var s = store.Load();
        Check.True(s.Pet.HealingEnabled && s.Pet.HealPetName == "Healing Buddy", "healing pet settings");
        Check.Eq(s.Pet.RosterKind, 1, "roster view");
        Check.True(s.Pet.UsePyrealRefillPath, "the refill opt-in");
        Check.Eq(s.Pet.Assignments.Count, 1, "type overrides (the blank one dropped)");
        Check.Eq(s.Quests.QuestFilter, "temple", "quest filter");
        Check.Eq(string.Join(",", s.Quests.QuestFavorites), "foo", "favorites (blanks dropped)");
        Check.True(s.Quests.ShowQuestFavoritesHud, "favorites HUD");
        Check.Eq(s.LastUsdPath, @"C:\vt\me.usd", "the .usd importer's last file");

        store.SaveIfDirty(s);
        Check.True(File.Exists(Path.Combine(dir, MiniRemoteStore.FileName)), "saved as miniremote.json");
        Check.Eq(File.ReadAllText(legacyPath), LegacyHubJson, "ilt-hub.json is left exactly as it was (RynthILT copies it)");
        string saved = File.ReadAllText(Path.Combine(dir, MiniRemoteStore.FileName));
        Check.False(saved.Contains("Pyreals") || saved.Contains("PetLog") || saved.Contains("AttrKeepXp"), "no ILT data in RynthAi's file");

        s.Quests.QuestFilter = "changed";
        store.SaveIfDirty(s);
        var again = new MiniRemoteStore(dir).Load();
        Check.Eq(again.Quests.QuestFilter, "changed", "miniremote.json wins once it exists");

        var fresh = new MiniRemoteStore(Folder("fresh")).Load();
        Check.False(fresh.Pet.HealingEnabled, "no files at all: defaults");
    }

    private static MiniRemoteController NewController(string folder, out RynthCoreHost host, out LegacyUiSettings settings)
    {
        FakeHost.Reset();
        host = FakeHost.Create(Player);
        var s = new LegacyUiSettings();
        settings = s;
        var c = new MiniRemoteController(host, Folder(folder), () => null, () => s, () => null, () => { }, new IltClient(host));
        string? lastTab = null;
        c.MiniRemoteCommand = mode => "[Mini Remote] remote " + mode;
        c.MiniRemoteTabCommand = (tab, mode) => { lastTab = tab + ":" + mode; return "tab " + lastTab; };
        return c;
    }

    private static void NoRynthIlt()
    {
        var c = NewController("noilt", out var host, out _);
        var ilt = c.Ilt;
        ilt.Tick();
        Check.False(ilt.Loaded, "no RynthILT (no plugin interfaces on this host)");
        Check.False(ilt.IsIltWorld, "so never an ILT world");
        Check.Eq(ilt.Feature(RynthIltHub.FeaturePetRefill), -1, "feature bits unknown");
        Check.False(ilt.IsOff(RynthIltHub.FeatureQuests), "unknown is not off (/myquests still refreshes)");
        Check.Eq(ilt.Tabs().Count, 0, "no ILT tabs");
        Check.False(ilt.RenderSection(RynthIltHub.SectionQuestBonus), "no ILT sections");
        Check.False(ilt.Command("hub bank"), "no ILT verbs taken");
        Check.False(ilt.TryGetRates(out _, out _), "no ILT rates");
        Check.False(c.SkipLoginQuestRefresh, "the login /myquests refresh isn't skipped");
    }

    private static void Commands()
    {
        var c = NewController("cmds", out _, out _);
        FakeHost.Chat.Clear();
        Check.True(c.HandleCommand("hub", new[] { "show" }), "hub show");
        Check.True(FakeHost.Chat.Any(x => x.Contains("remote show")), "hub show drives the Mini Remote");
        FakeHost.Chat.Clear();
        Check.True(c.HandleCommand("hub", new[] { "open", "pets" }), "hub open pets");
        Check.True(FakeHost.Chat.Any(x => x.Contains("tab pets:toggle")), "opens RynthAi's Pets tab");
        FakeHost.Chat.Clear();
        Check.True(c.HandleCommand("pets", new[] { "show" }), "pets show");
        Check.True(FakeHost.Chat.Any(x => x.Contains("tab pets:show")), "same tab");
        FakeHost.Chat.Clear();
        Check.True(c.HandleCommand("hub", new[] { "bank" }), "hub bank is handled (answered)");
        Check.True(FakeHost.Chat.Any(x => x.Contains("RynthILT")), "it says the ILT verbs come with RynthILT");
        Check.True(FakeHost.Chat.Any(x => x.Contains("/ra hub open pets|crafting")), "and lists what RynthAi still has");
        FakeHost.Chat.Clear();
        // The Quests tab moved to RynthOracle (2026-10-13): its verbs and tab names say so.
        Check.True(c.HandleCommand("hub", new[] { "open", "quests" }), "hub open quests is answered");
        Check.True(FakeHost.Chat.Any(x => x.Contains("RynthOracle") && x.Contains("/ro timers")), "it points to RynthOracle");
        FakeHost.Chat.Clear();
        Check.True(c.HandleCommand("quests", new[] { "window" }), "quests window is answered");
        Check.True(FakeHost.Chat.Any(x => x.Contains("RynthOracle")), "with the same note");
        FakeHost.Chat.Clear();
        Check.True(c.HandleCommand("quests", Array.Empty<string>()), "quests (refresh) stays RynthAi's");
        Check.True(FakeHost.Chat.Any(x => x.Contains("Quest tracker not ready")), "the meta tracker's refresh (none in this test)");
        FakeHost.Chat.Clear();
        Check.True(c.HandleCommand("guardian", new[] { "handin" }), "guardian is answered");
        Check.True(FakeHost.Chat.Any(x => x.Contains("RynthILT")), "with the RynthILT note");
        FakeHost.Chat.Clear();
        Check.True(c.HandleCommand("bountys", Array.Empty<string>()), "the bountys spelling too");
        Check.False(c.HandleCommand("start", Array.Empty<string>()), "other verbs aren't the Mini Remote's");
    }

    private static void RefillNeedsIlt()
    {
        var c = NewController("refill", out _, out _);
        c.State.Pet.UsePyrealRefillPath = true;
        FakeHost.Bools[(Player, MiniRemoteInventory.BoolPyrealRefillActive)] = true;   // the server's player bool
        Check.False(c.Pets.AllowSummonOnEmpty(), "not an ILT world (no RynthILT): an empty essence is never summoned on pyreals");
    }

    private static void LegacyProfileFields()
    {
        var s = JsonSerializer.Deserialize("{\"FeatureServerNames\":[\"MyShard\"],\"ForceServerFeatures\":true}",
            RynthAiJsonContext.Default.LegacyUiSettings)!;
        string json = JsonSerializer.Serialize(s, RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(json.Contains("MyShard"), "the old server list is written back unchanged");
        Check.True(json.Contains("\"ForceServerFeatures\": true"), "and the old override");
        var fresh = JsonSerializer.Serialize(new LegacyUiSettings(), RynthAiJsonContext.Default.LegacyUiSettings);
        Check.False(fresh.Contains("InfiniteLeaftide"), "a fresh profile no longer gets an ILT server list");
    }

    private static unsafe void HubTable()
    {
        Check.Eq(HubInterface.Query("RynthILT.Hub", 1), IntPtr.Zero, "not ours");
        var t = (RynthAiHubApiV1*)HubInterface.Query(RynthAiHubApiV1.InterfaceName, 1);
        Check.True(t != null && t->Version == 1 && t->StructSize == (uint)sizeof(RynthAiHubApiV1), "v1 served");
        Check.True(t->MiniRemoteTab != null && t->MiniRemoteVisible != null && t->QuestFlag != null && t->QuestStatus != null
                   && t->QuestRefresh != null && t->SpellName != null && t->LootKeeps != null && t->RenderSection != null
                   && t->SessionStats != null && t->SessionReset != null, "every function filled in");

        byte* buf = stackalloc byte[64];
        int n = t->SpellName(1, buf, 64);
        Check.Eq(System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)buf, n), "Strength Other I", "spell names from RynthAi's table");
        Check.Eq(t->SpellName(0, buf, 64), 0, "spell 0 has no name");
        QuestStatusV1 st;
        Check.Eq(t->QuestStatus(&st), 0, "no quest tracker before login");
        Check.Eq(t->MiniRemoteVisible(), 0, "no Mini Remote before login");
        SessionStatsV1 ss;
        Check.Eq(t->SessionStats(&ss), 0, "no session before login");
    }
}
