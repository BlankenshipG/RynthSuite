using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using RynthCore.Engine.UI.ScriptWindows;
using RynthCore.Plugin.RynthThreads;
using RynthCore.Plugin.RynthThreads.Threads;
using RynthCore.Plugin.RynthThreads.Ui;
using RynthCore.Plugin.RynthThreads.Views;
using RynthCore.RynthAiTests.Harness;

namespace RynthThreads.Tests;

// The window's display list against the engine's own parser, the server check and /rthreads stats.
internal static class WindowTests
{
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Write")]
    private static extern int Write(UiHost h);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_submit")]
    private static extern ref byte[] SubmitBuffer(UiHost h);

    public static void Register(Runner r)
    {
        r.Add("window: every tab, headers open, parses in the engine (bar button set)", EveryTab);
        r.Add("window: DreamWeave check and /rthreads stats lines", ServerAndStats);
        r.Add("window: Components and Modifiers tab text for the 4 real presses", RealText);
    }

    private static List<Press> Data()
    {
        var presses = new List<Press>();
        for (int i = 0; i < 3; i++) presses.Add(InferenceTests.P("Red Taper", "Bounteous=kills drop x1.38 loot, Hardy=monsters have x1.5 health", level: 185));
        for (int i = 0; i < 4; i++) presses.Add(InferenceTests.P("", "Hardy=monsters have x1.81 health, Teeming=75% more monsters", level: 185));
        presses.Add(InferenceTests.P("Blue Taper, Red Taper", "Bounteous=kills drop x1.4 loot", level: 120));
        Press raw = new PressParser().Feed(string.Join("\n", ParserTests.Sample), 1)![0];
        presses.Add(raw);
        presses.Add(InferenceTests.P("Red Taper", "Hardy", complete: false));
        return presses;
    }

    private static unsafe void EveryTab()
    {
        List<Press> presses = Data();
        ThreadsReport report = PressInference.Build(presses);
        var panel = new ThreadsPanel { Report = () => report, Presses = () => presses };
        var host = new UiHost();
        UiWindow w = host.Add(new UiWindow("RynthThreads/Main", "RynthThreads") { DefaultWidth = 440f, DefaultHeight = 480f, MinWidth = 320f, MinHeight = 220f });

        void Pass()
        {
            w.Begin();
            panel.Draw(w);
            w.End();
        }
        void OpenAll(IEnumerable<string> ids)
        {
            foreach (string id in ids) w.Dispatch(w.KeyOf(id), new UiEvent(UiFormat.EvOpen, b: true));
        }
        void Click(string id) => w.Dispatch(w.KeyOf(id), new UiEvent(UiFormat.EvClicked));

        string[] tabs = { "tab.c", "tab.m", "tab.p" };
        foreach (string tab in tabs)
        {
            Pass();
            Click(tab);
            Pass();
            OpenAll(report.Components.Select(c => "c:" + c.Name).Append("c:(none)").Append("c:(all)")
                .Concat(report.Combinations.Select(c => "k:" + c.Name))
                .Concat(report.Modifiers.Select(m => "m:" + m.Name))
                .Concat(presses.Select(p => "p:" + p.Id)));
            Pass();

            int n = Write(host);
            byte[] buf = SubmitBuffer(host);
            int rc;
            List<ParsedWindow> windows;
            string error;
            fixed (byte* p = buf) rc = DisplayListParser.Parse(p, n, 4000f, out _, out windows, out error);
            Check.Eq(rc, 0, $"{tab}: the engine accepts the list ({error})");
            if (rc != 0 || windows.Count != 1) continue;
            Check.Eq(windows[0].Title, "RynthThreads", "title (the bar button's tooltip)");
            Check.True((windows[0].Flags & WindowFlags.ShowInBar) != 0 && (windows[0].Flags & WindowFlags.BarButton) != 0, "bar button");
            Check.True(windows[0].List!.OpCount > 20, $"{tab}: something drawn ({windows[0].List!.OpCount} ops)");
        }
        string body = System.Text.Encoding.UTF8.GetString(w.Body);
        Check.True(body.Contains("Level 190, 4 entries."), "presses tab shows raw lines");
        Check.True(body.Contains("Yorick is recalling"), "and the other lines kept beside them");

        // Empty data still draws.
        var empty = new ThreadsPanel();
        w.Begin();
        empty.Draw(w);
        w.End();
        Check.True(System.Text.Encoding.UTF8.GetString(w.Body).Contains("No presses yet"), "empty window explains itself");
    }

    private static void ServerAndStats()
    {
        Check.True(RynthThreadsPlugin.IsDreamWeave("DreamWeave"), "DreamWeave");
        Check.True(RynthThreadsPlugin.IsDreamWeave("dreamweave PvE"), "any case, extra words");
        Check.False(RynthThreadsPlugin.IsDreamWeave("Aelrynth"), "Aelrynth");
        Check.False(RynthThreadsPlugin.IsDreamWeave(""), "empty");
        Check.False(RynthThreadsPlugin.IsDreamWeave(null), "null");

        List<string> lines = RynthThreadsPlugin.StatsLines(PressInference.Build(Data()), @"C:\x\presses.jsonl");
        Check.True(lines[0].StartsWith("10 presses (9 complete, 1 incomplete)"), "totals line: " + lines[0]);
        Check.True(lines.Any(l => l.StartsWith("Red Taper (5): Bounteous 100%+, Hardy 80%")), "Red Taper line");
        Check.True(lines.Any(l => l == "Blue Taper (1): Bounteous 100%"), "Blue Taper line");
        Check.True(lines.Any(l => l.StartsWith("No components (4): Hardy 100%")), "No components line");
        Check.False(lines.Any(l => l.Contains("Affinity")), "the Affinity group is left out");
        Check.True(lines.Contains("+ = more likely with it than without."), "legend");
        Check.Eq(lines[^1], @"File: C:\x\presses.jsonl", "file named last");
        Check.True(lines.Count <= 9, "short");
    }

    /// <summary>The window's text ops in order (headers as "== label", indented by Indent ops).</summary>
    internal static List<string> TextOf(ReadOnlySpan<byte> b)
    {
        var lines = new List<string>();
        int i = 0, indent = 0;
        string Str(ReadOnlySpan<byte> s, int at) =>
            System.Text.Encoding.UTF8.GetString(s.Slice(at + 2, s[at] | (s[at + 1] << 8)));
        while (i + 4 <= b.Length)
        {
            byte code = b[i], flags = b[i + 1];
            int len = b[i + 2] | (b[i + 3] << 8), at = i + 4;
            if (code == UiFormat.OpEnd && len == 0) break;
            string pad = new string(' ', indent * 2);
            switch (code)
            {
                case UiFormat.OpText: case UiFormat.OpTextWrapped: case UiFormat.OpTextDisabled: case UiFormat.OpSeparatorText:
                    lines.Add(pad + Str(b, at)); break;
                case UiFormat.OpBulletText: lines.Add(pad + "- " + Str(b, at)); break;
                case UiFormat.OpTextColored: lines.Add(pad + Str(b, at + 4)); break;
                case UiFormat.OpCollapsingHeader:
                    string h = Str(b, at + 4);
                    int cut = h.IndexOf("###", StringComparison.Ordinal);
                    lines.Add(pad + "== " + (cut >= 0 ? h.Substring(0, cut) : h)); break;
                case UiFormat.OpIndent: indent++; break;
                case UiFormat.OpUnindent: indent = Math.Max(0, indent - 1); break;
            }
            i = at + len + ((flags & UiFormat.OpHasBlock) != 0 ? 4 : 0);
        }
        return lines;
    }

    private static void RealText()
    {
        List<Press> presses = InferenceTests.Real();
        ThreadsReport report = PressInference.Build(presses);
        var panel = new ThreadsPanel { Report = () => report, Presses = () => presses };
        var w = new UiWindow("RynthThreads/Main", "RynthThreads");
        void Pass() { w.Begin(); panel.Draw(w); w.End(); }
        void Open(string id) => w.Dispatch(w.KeyOf(id), new UiEvent(UiFormat.EvOpen, b: true));

        Pass();
        Open("c:(all)");
        Open("c:Lead Scarab");
        Pass();
        List<string> comp = TextOf(w.Body);
        Check.True(comp.Contains("== Lead Scarab  (4 presses)"), "Lead Scarab header");
        Check.True(comp.Any(l => l.Trim() == "- Guarded  75% (3/4, 30-95%); without: no data; x1.83 to x2.18"), "Guarded line");
        Check.True(comp.Any(l => l.Trim() == "- Affinity (any)  100% (4/4, 51-100%); without: no data; 7% to 22%"), "Affinity line");
        Check.True(comp.Any(l => l.Trim() == "Amethyst 1, Iron 1, Obsidian 1, Serpentine 1"), "split line");
        Check.True(comp.Any(l => l.Trim() == "- Guarded  75% (3/4, 30-95%); x1.83 to x2.18"), "All presses: no comparison");

        w.Dispatch(w.KeyOf("tab.m"), new UiEvent(UiFormat.EvClicked));
        Pass();
        Open("m:Affinity");
        Open("m:Guarded");
        Pass();
        List<string> mods = TextOf(w.Body);
        Check.True(mods.Contains("== Guarded  75% (3/4, 30-95%)  [harder]") || mods.Contains("== Guarded  75% (3/4, 30-95%)"), "Guarded header");
        Check.True(mods.Any(l => l.Trim() == "- Lead Scarab  75% (3/4, 30-95%); without: no data; x1.83 to x2.18"), "Guarded with Lead Scarab");
        Check.True(mods.Any(l => l.Trim() == "- Amethyst: 25% (1/4, 5-70%)"), "Affinity by material");

        if (Environment.GetEnvironmentVariable("RYNTHTHREADS_DUMP") == "1")
        {
            Console.WriteLine("---- Components tab ----");
            foreach (string l in comp) Console.WriteLine(l);
            Console.WriteLine("---- Modifiers tab ----");
            foreach (string l in mods) Console.WriteLine(l);
        }
    }
}
