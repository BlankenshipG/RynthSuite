using System;
using System.IO;
using RynthCore.RynthAiTests.Harness;

// The engine parser's only outside dependency: the registry's limits (the engine's values).
namespace RynthCore.Engine.UI.ScriptWindows
{
    internal static class ScriptWindowRegistry
    {
        public const int MaxOpsPerWindow = 1500;
        public const int MaxBytesPerWindow = 64 * 1024;
        public const int MaxWindowsPerOwner = 32;
        public const int MaxBytesPerSubmit = 256 * 1024;
    }
}

namespace RynthThreads.Tests
{
    // Offline tests for RynthThreads. Each test prints PASS / FAIL.
    //
    // Run: dotnet run -c Release                 (all tests)
    //      dotnet run -c Release -- infer        (tests whose name contains "infer")
    internal static class Program
    {
        /// <summary>Scratch folder next to the exe for files tests write. Emptied at start.</summary>
        public static string TempRoot { get; } = Path.Combine(AppContext.BaseDirectory, "scratch");

        private static int Main(string[] args)
        {
            if (Directory.Exists(TempRoot)) Directory.Delete(TempRoot, recursive: true);
            Directory.CreateDirectory(TempRoot);

            var runner = new Runner();
            ParserTests.Register(runner);
            InferenceTests.Register(runner);
            StoreTests.Register(runner);
            WindowTests.Register(runner);
            int rc = runner.Run("RynthThreads tests", args.Length > 0 ? args[0] : null);
            try { Directory.Delete(TempRoot, recursive: true); } catch { }
            return rc;
        }

        /// <summary>A fresh empty folder under the scratch root.</summary>
        public static string NewFolder(string name)
        {
            string path = Path.Combine(TempRoot, name + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
