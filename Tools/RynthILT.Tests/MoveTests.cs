using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using RynthCore.Loot;
using RynthCore.Plugin.RynthILT;
using RynthCore.Plugin.RynthILT.Hub;
using RynthCore.Plugin.Shared;
using RynthCore.RynthAiTests.Harness;

namespace RynthILT.Tests;

// The ILT Hub's move out of RynthAi into RynthILT (2026-10-13): the saved data carried over from
// RynthAi's folders (copied, never moved), the RynthILT.Hub interface table RynthAi's Mini Remote
// reads, which "/ra" verbs RynthILT takes from RynthAi, and the host-backed object view that
// replaced RynthAi's WorldObjectCache.
internal static class MoveTests
{
    public static void Register(Runner r)
    {
        r.Add("move: a character's RynthAi ILT Hub files are copied, the RynthAi copies left alone", MigratesCharacter);
        r.Add("move: migration never overwrites RynthILT's files and runs once", MigrationIdempotent);
        r.Add("move: the pre-per-server ACEmulator folder is found too", MigratesFromAceEmulatorFolder);
        r.Add("move: shared Hub profiles and aug costs are copied once", MigratesShared);
        r.Add("move: a migration copy that fails is reported (log + chat) and the shared ones retried", MigrationFailuresReported);
        r.Add("move: a failed Hub save is reported once and retried", StoreSaveFailureReported);
        r.Add("move: an unfinished copy blocks the character's Hub writes until the retry copies the original", PendingMigrationNeverShadowed);
        r.Add("move: an old ilt-hub.json loads into RynthILT's state (ILT parts kept)", LegacyStateLoads);
        r.Add("move: RynthAi's \"/ra\" verbs RynthILT takes (and the ones it leaves)", VerbRouting);
        r.Add("move: item classes from the client's item type (the gear tools' checks)", Classification);
        r.Add("move: the interface structs have the agreed layout", InterfaceLayout);
        r.Add("move: server-list status lines", DescribeLines);
        r.Add("move: off ILT nothing is sent, no tab or section shows; on ILT the login probe runs", DormantOffIlt);
        r.Add("move: Character / Charms / Banking redirect to RynthOracle and Global Inventory", MovedSections);
        r.Add("move: the charms JSON RynthOracle reads carries the Guardian Hands", CharmsReaderJson);
        // Last: Release() switches the table off for the rest of the process.
        r.Add("move: the RynthILT.Hub table: query, functions, release", HubTable);
    }

    private static string Root(string name)
    {
        string dir = Path.Combine(Program.TempRoot, "move-" + name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        IltMigration.LegacyProfilesBase = Path.Combine(dir, "RynthAi", "SettingsProfiles");
        IltMigration.LegacySharedRoot = Path.Combine(dir, "RynthAi");
        return dir;
    }

    private const string LegacyHubJson = """
        {
          "SchemaVersion": 1,
          "ForceLeaftideFeatures": false,
          "UbSidecarsImported": true,
          "Bank": { "Pyreals": 1234567, "Luminance": 89, "EnlightenedCoins": 42, "AutoRefreshSeconds": 300 },
          "Pet": { "HealingEnabled": true, "HealPetName": "Healing Buddy", "PetLog": ["Fire Banshee"], "ShinyLog": [] },
          "Character": {
            "QuestFilter": "temple", "QbFilter": "wait", "QuestFavorites": ["foo"],
            "AttrOrder": ["qui","coo","str","end","foc","sel","hea","sta","man"], "AttrRaiseStats": ["qui"],
            "AugTargets": { "war": 1800 }
          },
          "Gear": { "AutoClap": true, "LastUsdPath": "C:\\vt\\x.usd", "LastEquipProfile": "Melee" },
          "Guardian": { "AutoHandIn": true, "TurnInCounts": { "Strength": 3 } }
        }
        """;

    private static string MakeLegacyChar(string world, string charName, bool withProfile = true)
    {
        string dir = Path.Combine(IltMigration.LegacyProfilesBase, IltMigration.RynthAiSafeName(world), IltMigration.RynthAiSafeName(charName));
        Directory.CreateDirectory(Path.Combine(dir, "ilt-hub-profiles"));
        File.WriteAllText(Path.Combine(dir, "ilt-hub.json"), LegacyHubJson);
        File.WriteAllText(Path.Combine(dir, "ilt-hub-transactions.json"), "[{\"Type\":\"Received\",\"Currency\":\"Pyreals\",\"Amount\":5}]");
        File.WriteAllText(Path.Combine(dir, "ilt-hub-profiles", "farm.json"), "{\"SchemaVersion\":1}");
        if (withProfile)
        {
            File.WriteAllText(Path.Combine(dir, "active_profile.txt"), "Melee");
            File.WriteAllText(Path.Combine(dir, "Melee.json"),
                "{\"MetaDebug\":false,\"FeatureServerNames\":[\"InfiniteLeaftide\",\"*Leaftide*\",\"MyShard\"],\"ForceServerFeatures\":true}");
            File.WriteAllText(Path.Combine(dir, "Default.json"), "{\"FeatureServerNames\":[\"Unused\"]}");
        }
        return dir;
    }

    private static void MigratesCharacter()
    {
        string root = Root("char");
        string legacy = MakeLegacyChar("Infinite Leaftide", "Sir Tester");
        string before = File.ReadAllText(Path.Combine(legacy, "ilt-hub.json"));
        var settings = new IltSettings();
        string dest = Path.Combine(root, "RynthILT", "Infinite Leaftide", "Sir Tester");

        var done = IltMigration.MigrateCharacter(dest, "Infinite Leaftide", "Sir Tester", settings, out bool force);
        Check.True(File.Exists(Path.Combine(dest, IltHubStore.StateFile)), "ilt-hub.json -> hub.json");
        Check.Eq(File.ReadAllText(Path.Combine(dest, IltHubStore.StateFile)), before, "copied byte for byte");
        Check.True(File.Exists(Path.Combine(dest, IltHubStore.TransactionsFile)), "transactions copied");
        Check.True(File.Exists(Path.Combine(dest, IltHubStore.ProfilesFolder, "farm.json")), "per-character Hub profiles copied");
        Check.True(File.Exists(Path.Combine(legacy, "ilt-hub.json")) && File.Exists(Path.Combine(legacy, "ilt-hub-transactions.json")),
            "RynthAi's files are still there (copy, not move)");
        Check.True(settings.ServerNames.Contains("MyShard"), "the active profile's server names are added");
        Check.False(settings.ServerNames.Contains("Unused"), "only the ACTIVE profile is read");
        Check.Eq(settings.ServerNames.Count(n => n.Equals("InfiniteLeaftide", StringComparison.OrdinalIgnoreCase)), 1, "no duplicates");
        Check.True(force, "\"Force enable\" carries over as Treat this world as ILT");
        Check.True(done.Count >= 4, "the log line lists what was carried over: " + string.Join(", ", done));
    }

    private static void MigrationIdempotent()
    {
        string root = Root("idem");
        string legacy = MakeLegacyChar("InfiniteLeaftide", "Again", withProfile: false);
        string dest = Path.Combine(root, "RynthILT", "InfiniteLeaftide", "Again");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, IltHubStore.TransactionsFile), "[]");   // RynthILT already has one
        var settings = new IltSettings();

        IltMigration.MigrateCharacter(dest, "InfiniteLeaftide", "Again", settings, out bool force);
        Check.Eq(File.ReadAllText(Path.Combine(dest, IltHubStore.TransactionsFile)), "[]", "an existing RynthILT file is never overwritten");
        Check.True(File.Exists(Path.Combine(dest, IltHubStore.StateFile)), "the missing hub.json is still copied");
        Check.False(force, "no profile, no override");

        File.WriteAllText(Path.Combine(legacy, "ilt-hub.json"), "{\"Bank\":{\"Pyreals\":1}}");
        var second = IltMigration.MigrateCharacter(dest, "InfiniteLeaftide", "Again", settings, out _);
        Check.Eq(second.Count, 0, "once hub.json exists the character is never migrated again");
        Check.False(File.ReadAllText(Path.Combine(dest, IltHubStore.StateFile)).Contains("\"Pyreals\":1}"), "later RynthAi edits don't overwrite RynthILT's state");

        var none = IltMigration.MigrateCharacter(Path.Combine(root, "RynthILT", "X", "Nobody"), "X", "Nobody", settings, out _);
        Check.Eq(none.Count, 0, "a character RynthAi never saw: nothing to do");
        Check.False(Directory.Exists(Path.Combine(root, "RynthILT", "X", "Nobody")), "and no folder is made for it");
    }

    private static void MigratesFromAceEmulatorFolder()
    {
        string root = Root("ace");
        MakeLegacyChar("ACEmulator", "Old Timer", withProfile: false);
        var settings = new IltSettings();
        string dest = Path.Combine(root, "RynthILT", "InfiniteLeaftide", "Old Timer");
        Check.NotNull(IltMigration.FindLegacyCharFolder("InfiniteLeaftide", "Old Timer"), "the world folder is missing, ACEmulator\\<char> is used");
        IltMigration.MigrateCharacter(dest, "InfiniteLeaftide", "Old Timer", settings, out _);
        Check.True(File.Exists(Path.Combine(dest, IltHubStore.StateFile)), "copied from the ACEmulator folder");
        Check.Eq(IltMigration.RynthAiSafeName("Sir O'Neil-Smith"), "Sir_O'Neil-Smith", "RynthAi's folder names keep ' and - only");
    }

    private static void MigratesShared()
    {
        string root = Root("shared");
        Directory.CreateDirectory(Path.Combine(IltMigration.LegacySharedRoot, "IltHubProfiles"));
        File.WriteAllText(Path.Combine(IltMigration.LegacySharedRoot, "IltHubProfiles", "team.json"), "{}");
        File.WriteAllText(Path.Combine(IltMigration.LegacySharedRoot, "ilt-aug-costs.json"), "{\"x\":1}");
        var settings = new IltSettings();
        string dest = Path.Combine(root, "RynthILT");

        var done = IltMigration.MigrateShared(settings, dest);
        Check.True(File.Exists(Path.Combine(dest, "Profiles", "team.json")), "shared profiles copied");
        Check.True(File.Exists(Path.Combine(dest, "ilt-aug-costs.json")), "aug costs copied");
        Check.True(settings.SharedFilesMigrated, "marked done in the settings");
        Check.Eq(done.Count, 2, "both reported");
        File.Delete(Path.Combine(dest, "ilt-aug-costs.json"));
        Check.Eq(IltMigration.MigrateShared(settings, dest).Count, 0, "once only");
        Check.False(File.Exists(Path.Combine(dest, "ilt-aug-costs.json")), "a file deleted afterwards isn't brought back");
    }

    // A copy that fails (here: the RynthAi file held open by another program) is never silent: it
    // is logged and queued for chat with the reason, the rest still copies, and the shared files
    // aren't marked done so the next login tries again.
    private static void MigrationFailuresReported()
    {
        string root = Root("fail");
        DataWriteGuard.Reset();
        string legacy = MakeLegacyChar("InfiniteLeaftide", "Locked Out");
        string dest = Path.Combine(root, "RynthILT", "InfiniteLeaftide", "Locked Out");
        var settings = new IltSettings();
        List<string> done;
        using (new FileStream(Path.Combine(legacy, "ilt-hub-transactions.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            done = IltMigration.MigrateCharacter(dest, "InfiniteLeaftide", "Locked Out", settings, out _);
        Check.True(File.Exists(Path.Combine(dest, IltHubStore.StateFile)), "the other files are still copied");
        Check.False(File.Exists(Path.Combine(dest, IltHubStore.TransactionsFile)), "the held file isn't");
        Check.True(done.Any(l => l.Contains("NOT copied") && l.Contains(legacy)), "the log line says so and where the originals are: " + string.Join(", ", done));
        Check.Eq(DataWriteGuard.PendingCount, 1, "one chat line queued (shown on ILT)");
        var lines = new List<string>();
        DataWriteGuard.DrainChat(lines.Add);
        Check.True(lines.Count == 1 && lines[0].StartsWith("[RynthILT]", StringComparison.Ordinal)
                   && lines[0].Contains("ilt-hub-transactions.json") && lines[0].Contains("another program"),
                   "it names the file and the reason: " + string.Join(" | ", lines));

        DataWriteGuard.Reset();
        Directory.CreateDirectory(Path.Combine(IltMigration.LegacySharedRoot, "IltHubProfiles"));
        File.WriteAllText(Path.Combine(IltMigration.LegacySharedRoot, "IltHubProfiles", "team.json"), "{}");
        string aug = Path.Combine(IltMigration.LegacySharedRoot, "ilt-aug-costs.json");
        File.WriteAllText(aug, "{\"x\":1}");
        string sharedDest = Path.Combine(root, "RynthILT");
        using (new FileStream(aug, FileMode.Open, FileAccess.Read, FileShare.None))
            done = IltMigration.MigrateShared(settings, sharedDest);
        Check.False(settings.SharedFilesMigrated, "a failed shared copy isn't marked done");
        Check.True(done.Any(l => l.Contains("NOT copied")), "and is reported: " + string.Join(", ", done));
        Check.True(DataWriteGuard.PendingCount >= 1, "in chat too");
        done = IltMigration.MigrateShared(settings, sharedDest);
        Check.True(File.Exists(Path.Combine(sharedDest, "ilt-aug-costs.json")), "the next login copies it");
        Check.True(settings.SharedFilesMigrated, "then it is marked done");
        Check.Eq(done.Count, 1, "only the missing file is copied the second time: " + string.Join(", ", done));
        DataWriteGuard.Reset();
    }

    // A character whose ilt-hub.json couldn't be copied (held open by another program): the Hub
    // runs from the RynthAi original read-only (or the defaults), writes no hub.json, bank log or
    // per-character profile there, the pending mark survives a restart, and the next attempt copies
    // the original byte for byte - never shadowed by a fresh file.
    private static void PendingMigrationNeverShadowed()
    {
        string root = Root("pending");
        DataWriteGuard.Reset();
        string legacy = MakeLegacyChar("InfiniteLeaftide", "Held Open");
        string legacyHub = Path.Combine(legacy, "ilt-hub.json");
        string dest = Path.Combine(root, "RynthILT", "InfiniteLeaftide", "Held Open");
        string hubJson = Path.Combine(dest, IltHubStore.StateFile);
        var settings = new IltSettings();

        // Held open by another program: neither copied nor readable.
        using (var held = new FileStream(legacyHub, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var done = IltMigration.MigrateCharacter(dest, "InfiniteLeaftide", "Held Open", settings, out _);
            Check.False(File.Exists(hubJson), "the held ilt-hub.json isn't copied");
            Check.True(IltMigration.IsPending(settings, dest), "the character is marked pending");
            Check.True(done.Any(l => l.Contains("NOT copied")), "and the log says so: " + string.Join(", ", done));

            var store = new IltHubStore(default, dest)
            {
                WritesBlocked = () => IltMigration.IsPending(settings, dest),
                LegacyFolder = legacy,
            };
            IltHubState state = store.LoadState();
            Check.Eq(state.Bank.Pyreals, 0L, "unreadable: the Hub runs from the defaults");
            state.Bank.Pyreals = 1;
            store.SaveStateIfDirty(state);
            Check.False(File.Exists(hubJson), "no hub.json is written while the copy is unfinished");
            Check.False(store.SaveProfile(state, "mine", shared: false, out string why), "nor a per-character profile");
            Check.True(why.Contains("RynthAi"), "with the reason: " + why);
            Check.True(store.SaveProfile(state, "team-" + Guid.NewGuid().ToString("N"), shared: true, out _), "shared profiles still save");

            // A second login while still held: still pending, still nothing written.
            IltMigration.MigrateCharacter(dest, "InfiniteLeaftide", "Held Open", settings, out _);
            Check.True(IltMigration.IsPending(settings, dest) && !File.Exists(hubJson), "still pending, still no hub.json");
        }

        // Readable again but not yet retried: a Hub started now reads the original, still writes nothing.
        var readOnly = new IltHubStore(default, dest)
        {
            WritesBlocked = () => IltMigration.IsPending(settings, dest),
            LegacyFolder = legacy,
        };
        IltHubState fromLegacy = readOnly.LoadState();
        Check.Eq(fromLegacy.Bank.Pyreals, 1234567L, "the Hub runs from the RynthAi original, read only");
        fromLegacy.Bank.Pyreals = 2;
        readOnly.SaveStateIfDirty(fromLegacy);
        Check.False(File.Exists(hubJson), "and still writes no hub.json");

        // The mark is in settings.json, so a client restart still knows the copy is unfinished.
        IltSettingsStore.Save(settings);
        IltSettings reloaded = IltSettingsStore.Load();
        Check.True(IltMigration.IsPending(reloaded, dest), "pending survives a restart");

        var finished = IltMigration.MigrateCharacter(dest, "InfiniteLeaftide", "Held Open", reloaded, out bool force);
        Check.True(File.Exists(hubJson), "the next attempt copies it");
        Check.Eq(File.ReadAllText(hubJson), File.ReadAllText(legacyHub), "byte for byte: the original, not a fresh file");
        Check.False(IltMigration.IsPending(reloaded, dest), "no longer pending");
        Check.True(force, "the profile's Force enable still carries over on the retry");
        Check.True(finished.Any(l => l.Contains("complete")), "the log says it finished: " + string.Join(", ", finished));

        var after = new IltHubStore(default, dest) { WritesBlocked = () => IltMigration.IsPending(reloaded, dest) };
        IltHubState loaded = after.LoadState();
        Check.Eq(loaded.Bank.Pyreals, 1234567L, "the restarted Hub loads the copied file");
        loaded.Bank.Pyreals = 7;
        after.SaveStateIfDirty(loaded);
        Check.True(File.ReadAllText(hubJson).Contains("\"Pyreals\": 7"), "and saves normally from then on");
        File.Delete(Path.Combine(IltSettingsStore.Folder, IltSettingsStore.FileName));
        DataWriteGuard.Reset();
    }

    // RynthILT's own saves report a failure (DataWriteGuard) and try again on the next save.
    private static void StoreSaveFailureReported()
    {
        string root = Root("storefail");
        DataWriteGuard.Reset();
        string blocker = Path.Combine(root, "NotAFolder");
        File.WriteAllText(blocker, "a file where the character folder should be");
        var store = new IltHubStore(default, blocker);
        var state = new IltHubState();
        store.SaveStateIfDirty(state);
        Check.Eq(DataWriteGuard.PendingCount, 1, "the failed Hub save is queued for chat");
        store.SaveStateIfDirty(state);
        Check.Eq(DataWriteGuard.PendingCount, 1, "once per folder, not every retry");
        File.Delete(blocker);
        store.SaveStateIfDirty(state);
        Check.True(File.Exists(Path.Combine(blocker, IltHubStore.StateFile)), "the next save retries and works");
        DataWriteGuard.Reset();
    }

    private static void LegacyStateLoads()
    {
        string root = Root("state");
        string dir = Path.Combine(root, "char");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, IltHubStore.StateFile), LegacyHubJson);
        var store = new IltHubStore(default, dir);
        var s = store.LoadState();
        Check.Eq(s.Bank.Pyreals, 1234567L, "bank balances");
        Check.Eq(s.Bank.AutoRefreshSeconds, 300, "bank settings");
        Check.Eq(s.Pet.PetLog.FirstOrDefault(), "Fire Banshee", "the /pets log (an ILT part of the Pet section)");
        Check.Eq(s.Character.QbFilter, "wait", "the /qb filter");
        Check.Eq(s.Character.AttrOrder.FirstOrDefault(), "qui", "the attribute raiser's order");
        Check.Eq(s.Character.AugTargets["war"], 1800, "aug targets");
        Check.True(s.Gear.AutoClap && s.Gear.LastEquipProfile == "Melee", "gear settings");
        Check.True(s.Guardian.AutoHandIn && s.Guardian.TurnInCounts["Strength"] == 3, "guardian settings");
        Check.True(s.UbSidecarsImported, "the UB import isn't redone");

        store.SaveStateIfDirty(s);
        string saved = File.ReadAllText(Path.Combine(dir, IltHubStore.StateFile));
        Check.False(saved.Contains("HealPetName"), "the Pets tab's settings (RynthAi's now) aren't written back here");
        Check.False(saved.Contains("QuestFavorites"), "nor the Quests tab's");
    }

    private static void VerbRouting()
    {
        static bool Takes(string line) => RynthILTPlugin.IsIltVerb(line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Check.True(Takes("hub bank"), "hub bank");
        Check.True(Takes("hub attr run"), "hub attr run");
        Check.True(Takes("hub refresh"), "hub refresh");
        Check.True(Takes("hub help"), "hub help (RynthILT prints the whole list)");
        Check.True(Takes("hub open banking show"), "hub open <ILT tab>");
        Check.True(Takes("hub open guardian"), "hub open guardian (the engine panel)");
        Check.True(Takes("hub open bounties"), "hub open bounties");
        Check.False(Takes("hub open pets"), "hub open pets is RynthAi's");
        Check.False(Takes("hub open quests hide"), "hub open quests is RynthAi's");
        Check.True(Takes("hub tab gear"), "hub tab <ILT tab>");
        Check.False(Takes("hub tab quests"), "hub tab quests is RynthAi's");
        Check.False(Takes("hub tab remote"), "the Remote tab is RynthAi's");
        Check.False(Takes("hub"), "bare /ra hub toggles RynthAi's Mini Remote");
        Check.True(Takes("guardian handin"), "guardian");
        Check.True(Takes("bounty show") && Takes("bounties") && Takes("bountys"), "bounty, bounties, bountys");
        Check.True(Takes("rates reset"), "rates reset (the Mini Remote's Reset XP)");
        Check.False(Takes("quests refresh"), "/ra quests is RynthAi's");
        Check.False(Takes("pets show"), "/ra pets is RynthAi's");
        Check.False(Takes("start"), "anything else is RynthAi's");
        // The VTank hotkey verbs (RynthAi 2026-10-13, RynthAiPlugin.VTankHotkeys.cs) stay RynthAi's.
        foreach (string vt in new[] { "vtkeys", "targetlock", "recharge", "lootthis", "meleelow", "navprio", "lootprio", "navlootprio", "setmetastate Default", "testitem", "propertydump" })
            Check.False(Takes(vt), $"{vt} is RynthAi's (VTank hotkeys)");
    }

    private static void Classification()
    {
        Check.Eq(IltObjects.ClassifyByItemType(0x00000800), AcObjectClass.Gem, "gem (aetheria)");
        Check.Eq(IltObjects.ClassifyByItemType(0x00000001), AcObjectClass.MeleeWeapon, "melee weapon");
        Check.Eq(IltObjects.ClassifyByItemType(0x00000100), AcObjectClass.MissileWeapon, "missile weapon");
        Check.Eq(IltObjects.ClassifyByItemType(0x00008000), AcObjectClass.WandStaffOrb, "caster");
        Check.Eq(IltObjects.ClassifyByItemType(0x40000000), AcObjectClass.Salvage, "salvage (white quartz)");
        Check.Eq(IltObjects.ClassifyByItemType(0x00001000), AcObjectClass.SpellComponent, "spell component");
        Check.Eq(IltObjects.ClassifyByItemType(0x00000200), AcObjectClass.Container, "pack");
        Check.Eq(IltObjects.ClassifyByItemType(0x00000010), AcObjectClass.Monster, "creature");
        Check.Eq(IltObjects.ClassifyByItemType(0x00000002 | 0x00000004), AcObjectClass.Armor, "armor wins over clothing");
        Check.Eq(IltObjects.ClassifyByItemType(0), AcObjectClass.Unknown, "no type yet");
    }

    private static unsafe void InterfaceLayout()
    {
        Check.Eq(sizeof(IltTabV1), 24 + 32 + 4 + 4 + 4, "IltTabV1: id, label, width, height, flags");
        Check.Eq((int)Marshal.OffsetOf<IltTabV1>(nameof(IltTabV1.Flags)), 64, "IltTabV1.Flags offset");
        Check.Eq(sizeof(RynthIltHubApiV1), 8 + 9 * IntPtr.Size, "RynthIltHubApiV1: header + 9 functions");
        // SessionStatsMore was appended (2026-10-13); readers accept any table at least
        // BaseStructSize long, so an older RynthAi still connects.
        // VaultedDungeonsEnabled was appended (2026-10-18): the Vaulted Dungeons master switch.
        Check.Eq(sizeof(RynthAiHubApiV1), 8 + 12 * IntPtr.Size, "RynthAiHubApiV1: header + 12 functions");
        Check.Eq((int)RynthAiHubApiV1.BaseStructSize, 8 + 10 * IntPtr.Size, "RynthAiHubApiV1.BaseStructSize: the first release's 10 functions");
        Check.Eq(RynthAiHubApiV1.SessionStatsMoreOffset, (int)RynthAiHubApiV1.BaseStructSize, "SessionStatsMore right after the base table");
        Check.Eq(RynthAiHubApiV1.VaultedDungeonsEnabledOffset, 8 + 11 * IntPtr.Size, "VaultedDungeonsEnabled right after SessionStatsMore");
        Check.Eq((int)Marshal.OffsetOf<RynthAiHubApiV1>(nameof(RynthAiHubApiV1.VaultedDungeonsEnabled)), RynthAiHubApiV1.VaultedDungeonsEnabledOffset,
            "VaultedDungeonsEnabledOffset is where the field really is");
        Check.Eq(sizeof(QuestFlagV1), 24, "QuestFlagV1");
        Check.Eq(sizeof(QuestStatusV1), 16, "QuestStatusV1");
        Check.Eq(RynthIltHubApiV1.InterfaceName, "RynthILT.Hub", "interface name");
        Check.Eq(RynthAiHubApiV1.InterfaceName, "RynthAi.Hub", "RynthAi's interface name");
    }

    private static void DescribeLines()
    {
        var s = new IltSettings();
        Check.True(ServerFeatureGate.Describe("InfiniteLeaftide", s, false).Contains("ON - matches 'InfiniteLeaftide'"), "on by name");
        Check.True(ServerFeatureGate.Describe("Aelrynth", s, true).Contains("Treat this world as ILT"), "on by the override");
        Check.True(ServerFeatureGate.Describe("Aelrynth", s, false).Contains("OFF"), "off");
        Check.True(ServerFeatureGate.Describe("", s, false).Contains("not logged in"), "before login");
    }

    private static IltHubController NewHub(string world, string folder)
    {
        RynthCore.RynthAiHostTests.Fakes.FakeHost.Reset();
        RynthCore.RynthAiHostTests.Fakes.FakeHost.WorldName = world;
        var host = RynthCore.RynthAiHostTests.Fakes.FakeHost.Create(0x50000F01);
        string dir = Path.Combine(Program.TempRoot, "move-" + folder);
        Directory.CreateDirectory(dir);
        return new IltHubController(host, dir, new IltSettings(), new RynthAiClient(host));
    }

    private static void TickFor(IltHubController hub, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { hub.Tick(); System.Threading.Thread.Sleep(50); }
    }

    /// <summary>The Mini Remote tabs that moved out (2026-10-13) map to their new homes' commands.</summary>
    private static void MovedSections()
    {
        Check.Eq(IltSections.MovedCommand(IltSection.Character), IltSections.OracleCharacterCommand, "Character -> RynthOracle Character");
        Check.Eq(IltSections.MovedCommand(IltSection.Charms), IltSections.OracleCharacterCommand, "Charms -> RynthOracle Character");
        Check.Eq(IltSections.MovedCommand(IltSection.Banking), IltSections.GlobalBankCommand, "Banking -> Global Inventory Banking");
        Check.True(IltSections.MovedCommand(IltSection.Gear) == null && IltSections.MovedCommand(IltSection.Games) == null,
                   "Gear and Games are still Mini Remote tabs");
        Check.True(IltSections.Tabs.SequenceEqual(new[] { IltSection.Gear, IltSection.Games }), "the tab list is Gear, Games");
        foreach (var moved in new[] { IltSection.Character, IltSection.Charms, IltSection.Banking })
            Check.False(IltSections.IsTab(moved), $"{IltSections.Label(moved)} isn't a tab");
        Check.Eq(IltSections.OracleCharacterCommand, "/ro character", "Oracle command");
        Check.Eq(IltSections.GlobalBankCommand, "/ginv bank", "Global Inventory command");
    }

    /// <summary>
    /// The charms JSON behind "RynthILT.Data" is valid JSON; on ILT it has a "hands" array (empty
    /// with no hand carried) that the shared reader parses, off ILT it has none and still parses.
    /// </summary>
    private static void CharmsReaderJson()
    {
        var on = NewHub("InfiniteLeaftide", "charms-on");
        string json = on.CharmsTab.BuildReaderJson();
        using (var doc = System.Text.Json.JsonDocument.Parse(json))
            Check.True(doc.RootElement.TryGetProperty("hands", out var hands) && hands.GetArrayLength() == 0,
                       "on ILT: an empty hands array with no Guardian Hand carried");
        Check.True(json.All(c => c < 128), "ASCII only");
        var view = IltCharmsView.Parse(json);
        Check.True(view.Loaded && view.Hands.Count == 0, "the shared reader takes it");

        var withHands = IltCharmsView.Parse("{\"available\":true,\"charms\":[],\"hands\":[" +
            "{\"id\":1234,\"name\":\"Guardian Hand\",\"active\":\"on\",\"server\":\"off\"},{\"id\":0,\"name\":\"no id\"}]}");
        Check.Eq(withHands.Hands.Count, 1, "a hand without an id is skipped (nothing to toggle)");
        Check.True(withHands.Hands[0] == new IltHandRow(1234, "Guardian Hand", "on", "off"), "id, name, active, server");

        var off = NewHub("Aeshnidae", "charms-off");
        string offJson = off.CharmsTab.BuildReaderJson();
        using (var doc = System.Text.Json.JsonDocument.Parse(offJson))
            Check.False(doc.RootElement.TryGetProperty("hands", out _), "off ILT: no hands");
        Check.True(IltCharmsView.Parse(offJson).Hands.Count == 0, "and the reader shows none");
    }

    private static void DormantOffIlt()
    {
        var off = NewHub("Aeshnidae", "dormant");
        off.OnLoginComplete("Tester", forceFromMigration: false);
        foreach (string line in new[] { "Your Pet Log:", "[BANK] Pyreals: 5", "Guardian of Strength tells you, \"x\"" }) off.OnChat(line);
        TickFor(off, 6800);   // past the 6 s login delay
        Check.Eq(RynthCore.RynthAiHostTests.Fakes.FakeHost.ChatCommands.Count, 0, "off ILT: no probe, no command at all");
        Check.False(off.Options.IsIltLikeWorld, "not an ILT world");
        foreach (var s in IltSections.Tabs) Check.False(off.TabShown(s), $"no {IltSections.Label(s)} tab");
        Check.False(off.RenderSection(RynthIltHub.SectionRemoteBank) || off.RenderSection(RynthIltHub.SectionQuestBonus)
                    || off.RenderSection(RynthIltHub.SectionPetLogs) || off.RenderSection(RynthIltHub.SectionRemoteGuardian)
                    || off.RenderSection(RynthIltHub.SectionServerMenu), "no ILT section draws (checked before any ImGui call)");
        Check.True(off.BuildProgressionJson().StartsWith("{\"available\":false"), "Progression: not available");
        off.OnLogout();

        var on = NewHub("InfiniteLeaftide", "awake");
        on.OnLoginComplete("Tester", forceFromMigration: false);
        TickFor(on, 6800);
        Check.True(on.Options.IsIltLikeWorld, "InfiniteLeaftide is ILT");
        Check.True(RynthCore.RynthAiHostTests.Fakes.FakeHost.ChatCommands.Count > 0, "on ILT the login probe asks the server");
        Check.False(RynthCore.RynthAiHostTests.Fakes.FakeHost.ChatCommands.Any(c => c.Contains("/ilt features", StringComparison.OrdinalIgnoreCase)),
                    "the server is never asked for a feature list (the ILT profile is Rynth-side)");
        // Character, Charms and Banking moved out of the Mini Remote (RynthOracle / Global Inventory).
        foreach (var moved in new[] { IltSection.Character, IltSection.Charms, IltSection.Banking })
            Check.False(on.TabShown(moved), $"{IltSections.Label(moved)} is no Mini Remote tab any more");
        Check.Eq(on.TabShown(IltSection.Gear), on.Available, "Gear shows once the server features are known");
        on.OnLogout();

        var forced = NewHub("Aeshnidae", "forced");
        forced.OnLoginComplete("Tester", forceFromMigration: true);
        Check.True(forced.Options.IsIltLikeWorld, "a migrated \"Force enable\" makes the world ILT for this character");
        forced.OnLogout();
    }

    private static unsafe void HubTable()
    {
        Check.Eq(HubInterface.Query("RynthAi.Hub", 1), IntPtr.Zero, "another interface's name: not ours");
        Check.Eq(HubInterface.Query(RynthIltHubApiV1.InterfaceName, 2), IntPtr.Zero, "a newer version than we have: none");
        var t = (RynthIltHubApiV1*)HubInterface.Query(RynthIltHubApiV1.InterfaceName, 1);
        Check.True(t != null, "v1 is served");
        Check.Eq(t->Version, 1u, "Version 1");
        Check.Eq(t->StructSize, (uint)sizeof(RynthIltHubApiV1), "StructSize");
        Check.True(t->IsIltWorld != null && t->GetFeature != null && t->GetRates != null && t->GetForceIlt != null && t->SetForceIlt != null
                   && t->GetTabs != null && t->RenderTab != null && t->RenderSection != null && t->Command != null, "every function is filled in");
        Check.Eq((IntPtr)t, HubInterface.Query(RynthIltHubApiV1.InterfaceName, 1), "the same table each time");

        // No plugin instance here (not logged in): every function answers "nothing".
        Check.Eq(t->IsIltWorld(), 0, "not ILT before login");
        byte* key = stackalloc byte[] { (byte)'b', (byte)'a', (byte)'n', (byte)'k', 0 };
        Check.Eq(t->GetFeature(key), -1, "feature unknown before login");
        Check.Eq(t->GetTabs(null, 0), 0, "no tabs before login");
        Check.Eq(t->Command(key), 0, "no command taken before login");

        HubInterface.Release();
        Check.Eq(t->Version, 0u, "Release zeroes Version (callers re-query)");
        Check.Eq(HubInterface.Query(RynthIltHubApiV1.InterfaceName, 1), IntPtr.Zero, "and nothing is served after shutdown");
        Check.Eq(t->IsIltWorld(), 0, "the old table stays callable and answers nothing");
    }
}
