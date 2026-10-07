using System;
using System.IO;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.RynthAiHostTests.Tests;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests;

// Offline tests for RynthAi's host-bound logic: loot rule matching and metas. Each test prints
// PASS / FAIL, or KNOWN for a registered real bug (which does not fail the run).
//
// Run: dotnet run -c Release                 (all tests)
//      dotnet run -c Release -- vtank        (tests whose name contains "vtank")
internal static class Program
{
    /// <summary>Scratch folder next to the exe for files tests write. Emptied at start.</summary>
    public static string TempRoot { get; } = Path.Combine(AppContext.BaseDirectory, "scratch");

    private static int Main(string[] args)
    {
        if (Directory.Exists(TempRoot)) Directory.Delete(TempRoot, recursive: true);
        Directory.CreateDirectory(TempRoot);

        // SAFETY: the expression engine reads and writes pvars/gvars (and reads ItemGiver
        // profiles) under C:\Games\RynthSuite\RynthAi. Point them at the scratch folder before
        // any engine exists. The "expr: safety" test checks this held.
        ExpressionEngine.PvarsDir = Path.Combine(TempRoot, "pvars");
        ExpressionEngine.GvarsPath = Path.Combine(TempRoot, "gvars.txt");
        ExpressionEngine.ItemGiverDir = Path.Combine(TempRoot, "ItemGiver");

        // Spell names (item spells, spell expiry by name) come from the plugin's embedded table.
        SpellDatabase.Load();

        var runner = new Runner();
        NativeLootTests.Register(runner);
        VTankLootTests.Register(runner);
        ExpressionTests.Register(runner);
        MetaTests.Register(runner);
        MetaSchedulerTests.Register(runner);
        NavEngineTests.Register(runner);
        PatrolDoorwayTests.Register(runner);
        BuffTests.Register(runner);
        CombatMathTests.Register(runner);
        WeaponTests.Register(runner);
        RadarSnapshotTests.Register(runner);
        AmmoTests.Register(runner);
        VTankYieldTests.Register(runner);
        OffhandTests.Register(runner);
        FloodPerfTests.Register(runner);
        LootSalvageTests.Register(runner);
        LootCorpseTests.Register(runner);
        VitalsTests.Register(runner);
        BlastTests.Register(runner);
        ArcWhenClearTests.Register(runner);
        UseAtLoginTests.Register(runner);
        LearnSpellsTests.Register(runner);
        AttackLatencyTests.Register(runner);
        LootAddItemTests.Register(runner);
        AttributeRaiserTests.Register(runner);
        return runner.Run("RynthAi host tests", args.Length > 0 ? args[0] : null);
    }
}
