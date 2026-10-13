using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthILT.IltSettingsView;
using RynthCore.RynthAiTests.Harness;

namespace RynthILT.Tests;

// RynthILT's copy of the ILT settings (IltSettingsView/IltSettingsModel.cs): RynthCore's "servermenu"
// push (verdict, override, world names), the Hub's feature read (and the older "iltfeatures" text),
// the engine verdict the Hub follows (IltEngineVerdict), and the RynthCore commands the view sends.
internal static class IltSettingsTests
{
    public static void Register(Runner r)
    {
        r.Add("ilt settings: RynthCore's push gives the ILT verdict, override, reason and world names", EnginePush);
        r.Add("ilt settings: an older RynthCore push (no reason, no names) still reads", OlderEnginePush);
        r.Add("ilt settings: malformed or foreign payloads publish nothing", Malformed);
        r.Add("ilt settings: the Hub's feature read and the older text form both read", FeatureRoundTrip);
        r.Add("ilt settings: the Hub follows RynthCore's ILT verdict once one arrives", EngineVerdict);
        r.Add("ilt settings: override and world-name commands are checked before sending", Commands);
        r.Add("ilt settings: logout clears the copy", ClearOnLogout);
    }

    private const string Push =
        "V\tilt\tInfinite Leaftide\t1\ton\t0\tforced on for this character\n" +
        "V\taelrynth\tAelrynth\t0\tauto\t0\tnot detected\n" +
        "X\tchar\tChar\n" +
        "S\tilt\tInfinite Leaftide\t1\t/rc server ilt on\n" +
        "I\tBounties\t/rc bounties toggle\tBounties.\t\n" +
        "N\tilt\tInfiniteLeaftide\tengine.json\n" +
        "N\tilt\t*Frost*\tengine.json\n" +
        "N\taelrynth\tAel*\tdefault\n";

    private static void EnginePush()
    {
        IltEngineView? v = IltSettingsModel.ParseEngine(Push);
        Check.True(v != null, "parsed");
        Check.True(v!.Server != null, "the ILT server is there");
        Check.Eq(v.Server!.Name, "Infinite Leaftide", "name");
        Check.True(v.Server.Active, "active");
        Check.False(v.Server.Detected, "not detected (forced)");
        Check.Eq(v.Server.Force, "on", "override");
        Check.Eq(v.Server.Reason, "forced on for this character", "reason");
        Check.Eq(string.Join(",", v.Names), "InfiniteLeaftide,*Frost*", "only ILT's world names");
        Check.False(v.NamesDefault, "from engine.json");
    }

    private static void OlderEnginePush()
    {
        IltEngineView? v = IltSettingsModel.ParseEngine("V\tilt\tInfinite Leaftide\t0\tWEIRD\t0\n");
        Check.True(v?.Server != null, "parsed");
        Check.Eq(v!.Server!.Reason, "", "no reason field");
        Check.Eq(v.Server.Force, "auto", "an unknown override shows as auto");
        Check.Eq(v.Names.Length, 0, "no names");

        IltEngineView? none = IltSettingsModel.ParseEngine("V\taelrynth\tAelrynth\t1\tauto\t1\tx\n");
        Check.True(none != null && none.Server == null, "a push without ILT says so (not 'waiting')");
    }

    private static void Malformed()
    {
        Check.True(IltSettingsModel.ParseEngine(null) == null, "null");
        Check.True(IltSettingsModel.ParseEngine("") == null, "empty");
        Check.True(IltSettingsModel.ParseEngine("S\tilt\tx\t1\t\nI\ta\tb\tc\td\n") == null, "RynthAi-only lines aren't a verdict");
        Check.True(IltSettingsModel.ParseEngine("V\tilt\n\t\t\n") == null, "short V line skipped");
        Check.True(IltSettingsModel.ParseEngine(new string('V', IltSettingsModel.MaxPayload + 1)) == null, "oversized");
        Check.True(IltSettingsModel.ParseFeatures("F\tbank\tnotanumber\n") == null, "bad value skipped");

        var model = new IltSettingsModel();
        model.ApplyEngine(Push);
        model.ApplyEngine("garbage");
        Check.True(model.Engine?.Server?.Active == true, "garbage keeps the last good push");
    }

    private static void FeatureRoundTrip()
    {
        // The older "iltfeatures" text form (W / R / F lines) still parses.
        const string payload =
            "W\tInfinite Leaftide\n" +
            "R\tprobe\t12:00:00\t0\t1\n" +
            "F\tbank\t1\n" +
            "F\tpowerball\t0\n" +
            "F\tcharm:guardianhand\t-1\n" +
            "F\tclamped\t5\n";
        IltFeatureView? f = IltSettingsModel.ParseFeatures(payload);
        Check.True(f != null, "parsed");
        Check.Eq(f!.World, "Infinite Leaftide", "world");
        Check.Eq(f.Source, "probe", "source");
        Check.Eq(f.LastCheck, "12:00:00", "last check");
        Check.True(f.IltLike, "ilt-like");
        Check.False(f.Checking, "not checking");
        Check.Eq(f.Features.Length, 4, "every feature");
        Check.Eq(f.Features.Single(k => k.Key == "bank").Value, 1, "on");
        Check.Eq(f.Features.Single(k => k.Key == "powerball").Value, 0, "off");
        Check.Eq(f.Features.Single(k => k.Key == "charm:guardianhand").Value, -1, "unknown");
        Check.Eq(f.Features.Single(k => k.Key == "clamped").Value, 1, "out-of-range value clamped");
        Check.Eq(IltSettingsModel.StateText(0), "off", "0 is off (not unknown)");
        Check.Eq(IltSettingsModel.StateText(-1), "unknown", "-1 is unknown");

        // The Hub's own read is published as a whole snapshot; null clears it.
        var model = new IltSettingsModel();
        var read = new IltFeatureView("Infinite Leaftide", "dump", "", Checking: true, IltLike: true,
                                      new[] { new KeyValuePair<string, int>("bank", 1) });
        model.PublishFeatures(read);
        Check.True(ReferenceEquals(model.Features, read), "published as is");
        model.PublishFeatures(null);
        Check.True(model.Features == null, "cleared while there is no Hub");
    }

    private static void EngineVerdict()
    {
        var model = new IltSettingsModel();
        try
        {
            IltEngineVerdict.Attach(model);
            Check.False(IltEngineVerdict.Pushed, "nothing pushed yet");
            Check.True(IltEngineVerdict.Active == null, "no verdict: RynthILT's own list decides");
            model.ApplyEngine(Push);
            Check.True(IltEngineVerdict.Pushed, "pushed");
            Check.True(IltEngineVerdict.Active == true, "RynthCore says ILT");
            model.ApplyEngine("V\taelrynth\tAelrynth\t1\tauto\t1\tx\n");
            Check.True(IltEngineVerdict.Pushed && IltEngineVerdict.Active == null, "a push without ILT gives no ILT verdict");
            model.Clear();
            Check.False(IltEngineVerdict.Pushed, "logout forgets it");
        }
        finally { IltEngineVerdict.Attach(null); }
        Check.True(IltEngineVerdict.Active == null, "detached");
    }

    private static void Commands()
    {
        Check.Eq(IltSettingsModel.ForceCommand("on"), "/rc server ilt on", "on");
        Check.Eq(IltSettingsModel.ForceCommand("auto"), "/rc server ilt auto", "auto");
        Check.Eq(IltSettingsModel.ForceCommand("maybe"), "", "unknown mode sends nothing");

        Check.Eq(IltSettingsModel.NamesCommand(true, "  *Frost* ", out _), "/rc server names ilt add *Frost*", "add, trimmed");
        Check.Eq(IltSettingsModel.NamesCommand(false, "Infinite Leaftide", out _), "/rc server names ilt remove Infinite Leaftide", "remove");
        Check.Eq(IltSettingsModel.NamesCommand(true, "", out string e1), "", "empty refused");
        Check.True(e1.Length > 0, "with a reason");
        Check.Eq(IltSettingsModel.NamesCommand(true, new string('a', IltSettingsModel.MaxPattern + 1), out _), "", "too long refused");
        Check.Eq(IltSettingsModel.NamesCommand(true, "x\n/rc reload", out _), "", "a line break can't start a second command");
        Check.Eq(IltSettingsModel.NamesCommand(true, "a/b", out _), "", "no '/'");
    }

    private static void ClearOnLogout()
    {
        var model = new IltSettingsModel();
        model.ApplyEngine(Push);
        model.ApplyFeatures("F\tbank\t1\n");
        Check.True(model.Engine != null && model.Features != null, "both copies");
        model.Clear();
        Check.True(model.Engine == null && model.Features == null, "cleared");
    }
}
