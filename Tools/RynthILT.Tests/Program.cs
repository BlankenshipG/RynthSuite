using System;
using System.IO;
using RynthCore.RynthAiTests.Harness;

namespace RynthILT.Tests;

// Offline tests for the RynthILT plugin: the ACECustom bounty tracker (parsing, merging, the
// quiet /bounty list read loop on a fake clock, persistence, the bridge JSON). Each test prints
// PASS / FAIL.
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

        var runner = new Runner();
        BountyTests.Register(runner);
        BountyReviewTests.Register(runner);
        return runner.Run("RynthILT tests", args.Length > 0 ? args[0] : null);
    }
}
