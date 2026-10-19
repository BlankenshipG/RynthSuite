using System.IO;
using System.Reflection;
using RynthCore.Plugin.RynthILT.Hub;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthILT.Tests;

// The Temple guardian hand-in (Hub/IltGuardian.cs). Kelpie, 2026-10-19: the first riddle's hand-in
// failed waiting for the vendor ("the vendor didn't open"), and every riddle after it failed
// "timed out" 2 ms after it started: the failed step's deadline was never cleared.
internal static class GuardianTests
{
    private const uint Player = 0x50000C21;

    public static void Register(Runner r)
    {
        r.Add("guardian: a new hand-in does not inherit the last one's expired step deadline", StaleDeadline);
    }

    private static void StaleDeadline()
    {
        FakeHost.Reset();
        FakeHost.WorldName = "InfiniteLeaftide";
        FakeHost.GiveCalls = true;   // GiveObjectTo exists, so a hand-in can start
        RynthCoreHost host = FakeHost.Create(Player);
        string dir = Path.Combine(Program.TempRoot, "guardian-stale-deadline");
        Directory.CreateDirectory(dir);
        var hub = new IltHubController(host, dir, new IltSettings(), new RynthAiClient(host));
        IltGuardian g = hub.Guardian;

        // What a hand-in that failed waiting for the vendor leaves behind: a step deadline long past.
        FieldInfo deadline = typeof(IltGuardian).GetField("_stepDeadline", BindingFlags.NonPublic | BindingFlags.Instance)!;
        deadline.SetValue(g, 1L);

        g.StartHandIn("Myrrh", "Guardian of the Temple of Enlightenment", asked: true);
        Check.Eq((long)deadline.GetValue(g)!, 0L, "starting a hand-in clears the old step deadline");
        g.Tick(IltHubContext.NowMs);
        Check.False(g.HandInStatus.Contains("timed out"), "the first step is not failed as 'timed out': " + g.HandInStatus);
        // Still running, or (no Myrrh carried, no vendor trading on the fake host) stopped at the vendor step.
        Check.True(g.HandInRunning || g.HandInStatus.Contains("vendor"), "the hand-in is alive or at the vendor step: " + g.HandInStatus);
    }
}
