using RynthCore.RynthAiTests.Harness;

namespace RynthServerPack.Tests;

// Offline tests for the server plugins: the shared model and every server plugin's detection data.
//
// Run: dotnet run -c Release                 (all tests)
//      dotnet run -c Release -- host         (tests whose name contains "host")
internal static class Program
{
    private static int Main(string[] args)
    {
        var runner = new Runner();
        ServerPackModelTests.Register(runner);
        ServerProjectsTests.Register(runner);
        return runner.Run("RynthServerPack tests", args.Length > 0 ? args[0] : null);
    }
}
