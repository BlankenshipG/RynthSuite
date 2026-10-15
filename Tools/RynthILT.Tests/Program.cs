using System;
using System.IO;
using RynthCore.RynthAiTests.Harness;

namespace RynthILT.Tests;

// Offline tests for the RynthILT plugin: the ACECustom bounty tracker (parsing, merging, the
// quiet /bounty list read loop on a fake clock, persistence, the bridge JSON) and the ILT Hub
// (attribute raiser, aug costs, bank commands, sigil chain, and the move out of RynthAi: data
// migration, the RynthILT.Hub table, verb routing, the object view). Each test prints PASS / FAIL.
//
// Run: dotnet run -c Release                 (all tests)
//      dotnet run -c Release -- window       (tests whose name contains "window")
internal static class Program
{
    /// <summary>Scratch folder next to the exe for files tests write. Emptied at start.</summary>
    public static string TempRoot { get; } = Path.Combine(AppContext.BaseDirectory, "scratch");

    private static int Main(string[] args)
    {
        if (Directory.Exists(TempRoot)) Directory.Delete(TempRoot, recursive: true);
        Directory.CreateDirectory(TempRoot);

        // SAFETY: every file the ILT Hub would write under %APPDATA%\RynthCore\RynthILT or read from
        // the RynthAi folder points at the scratch folder before any test runs.
        RynthCore.Plugin.RynthILT.Hub.IltAugCosts.FilePath = Path.Combine(TempRoot, "ilt-aug-costs.json");
        RynthCore.Plugin.RynthILT.Hub.IltHubStore.SharedRoot = Path.Combine(TempRoot, "RynthILT");
        RynthCore.Plugin.RynthILT.Hub.IltSettingsStore.Folder = Path.Combine(TempRoot, "RynthILT");
        RynthCore.Plugin.RynthILT.Hub.IltMigration.LegacyProfilesBase = Path.Combine(TempRoot, "RynthAi", "SettingsProfiles");
        RynthCore.Plugin.RynthILT.Hub.IltMigration.LegacySharedRoot = Path.Combine(TempRoot, "RynthAi");
        RynthCore.Plugin.RynthILT.RynthLog.Init(default, Path.Combine(TempRoot, "Logs"));
        RynthCore.Plugin.Shared.DataWriteGuard.PluginName = "RynthILT";   // as RynthILTPlugin.Initialize sets it

        var runner = new Runner();
        BountyTests.Register(runner);
        BountyReviewTests.Register(runner);
        AttributeRaiserTests.Register(runner);
        SigilChainTests.Register(runner);
        BankCommandTests.Register(runner);
        MoveTests.Register(runner);
        FeatureCatalogTests.Register(runner);
        IltSettingsTests.Register(runner);
        FellowshipCommandTests.Register(runner);
        AetheriaChunkTests.Register(runner);
        BreedingTests.Register(runner);
        return runner.Run("RynthILT tests", args.Length > 0 ? args[0] : null);
    }
}
