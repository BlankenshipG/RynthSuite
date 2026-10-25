using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using RynthCore.Plugin.RynthThreads.Threads;
using RynthCore.RynthAiTests.Harness;

namespace RynthThreads.Tests;

// The shared presses.jsonl (Threads/PressStore.cs).
internal static class StoreTests
{
    public static void Register(Runner r)
    {
        r.Add("store: a press survives the JSON round trip", RoundTrip);
        r.Add("store: another client's presses are read as the file grows; no doubles", FollowsGrowth);
        r.Add("store: odd lines are skipped, a half-written line waits", OddLines);
        r.Add("store: two writers appending at once lose nothing", ConcurrentAppends);
        r.Add("store: CSV export", CsvExport);
    }

    private static Press Sample() => new PressParser().Feed(string.Join("\n", ParserTests.Sample), 1)![0];

    private static void RoundTrip()
    {
        Press p = Sample();
        p.Server = "DreamWeave";
        p.Character = "Tester \"Quote\"";
        string json = PressStore.ToJson(p);
        Check.False(json.Contains('\n'), "one line");
        Press? back = PressStore.FromJson(json);
        Check.NotNull(back, "reads back");
        if (back == null) return;
        ParserTests.CheckSample(back);
        Check.Eq(back.Id, p.Id, "id");
        Check.Eq(back.Character, "Tester \"Quote\"", "character (escaped quotes)");
        Check.Eq(back.Server, "DreamWeave", "server");
        Check.Eq(back.TimeUtc, p.TimeUtc, "time");
        Check.Eq(back.Modifiers[3].Numbers[0].Text, "+29", "number text");

        // Missing fields and unknown ones (a later version) are fine.
        Press? minimal = PressStore.FromJson("{\"id\":\"x1\",\"future\":[1,2],\"modifiers\":[{\"name\":\"Hardy\"},{\"description\":\"no name\"}]}");
        Check.True(minimal != null && minimal.Id == "x1" && minimal.Modifiers.Count == 1 && minimal.FragmentLevel == null,
                   "minimal line reads, nameless modifier dropped");
        Check.Null(PressStore.FromJson("not json"), "garbage");
        Check.Null(PressStore.FromJson("[1,2]"), "not an object");
    }

    private static void FollowsGrowth()
    {
        string folder = Program.NewFolder("growth");
        var a = new PressStore(folder);
        var b = new PressStore(folder);
        Check.Eq(b.Poll(), 0, "no file yet");

        Check.Null(a.Record(Sample()), "client A writes");
        Check.Eq(a.Poll(), 0, "A reading its own press back adds nothing");
        Check.Eq(a.Presses.Count, 1, "A has it once");
        Check.Eq(b.Poll(), 1, "client B sees it");
        Check.Eq(b.Poll(), 0, "and only once");

        Press second = Sample();
        second.Components = new List<string> { "Blue Taper" };
        a.Record(second);
        b.Record(Sample());
        Check.Eq(b.Poll(), 1, "B gets A's second press (its own is already there)");
        Check.Eq(a.Poll(), 1, "A gets B's press");
        Check.Eq(a.Presses.Count, 3, "A: 3");
        Check.Eq(b.Presses.Count, 3, "B: 3");

        // A fresh client reads them all.
        var c = new PressStore(folder);
        Check.Eq(c.Poll(), 3, "a new client loads everything");
        int v = c.Version;
        c.Poll();
        Check.Eq(c.Version, v, "nothing new: unchanged");
    }

    private static void OddLines()
    {
        string folder = Program.NewFolder("odd");
        var s = new PressStore(folder);
        Directory.CreateDirectory(folder);
        string good = PressStore.ToJson(Sample());
        File.WriteAllText(s.FilePath, good + "\n\nnot json at all\n{\"broken\":\n" + good.Substring(0, 20));
        Check.Eq(s.Poll(), 1, "the good line");
        Check.Eq(s.BadLines, 2, "two bad lines counted");
        // The half line gets finished (with a different id: a new press).
        Press other = Sample();
        string rest = PressStore.ToJson(other);
        File.WriteAllText(s.FilePath, good + "\n\nnot json at all\n{\"broken\":\n" + rest + "\n");
        Check.Eq(s.Poll(), 1, "the finished line is read");
        Check.Eq(s.Presses.Count, 2, "two presses");
    }

    private static void ConcurrentAppends()
    {
        string folder = Program.NewFolder("concurrent");
        const int perWriter = 250;
        var writers = new[] { new PressStore(folder), new PressStore(folder) };
        var errors = new int[2];
        using var stop = new CancellationTokenSource();

        // A third party holding the file now and then, as another client mid-write would.
        var holder = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var fs = new FileStream(Path.Combine(folder, PressStore.FileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    Thread.Sleep(3);
                }
                catch (IOException) { }
                Thread.Sleep(2);
            }
        });
        holder.Start();

        var threads = Enumerable.Range(0, 2).Select(w => new Thread(() =>
        {
            for (int i = 0; i < perWriter; i++)
            {
                Press p = Sample();
                p.Character = "Writer" + w;
                p.Components = new List<string> { "C" + i };
                if (writers[w].Record(p) != null) errors[w]++;
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        stop.Cancel();
        holder.Join();

        // Whatever found the file busy every time is written now.
        foreach (PressStore w in writers)
            for (int i = 0; i < 50 && w.FlushPending() > 0; i++) Thread.Sleep(10);
        Check.Eq(writers[0].PendingCount + writers[1].PendingCount, 0, "nothing left pending");

        string[] lines = File.ReadAllLines(Path.Combine(folder, PressStore.FileName), Encoding.UTF8)
                             .Where(l => l.Length > 0).ToArray();
        Check.Eq(lines.Length, 2 * perWriter, "every press is one line");
        List<Press?> read = lines.Select(PressStore.FromJson).ToList();
        Check.True(read.All(p => p != null && p.Modifiers.Count == 6), "every line is a whole press");
        Check.Eq(read.Select(p => p!.Id).Distinct().Count(), 2 * perWriter, "all ids distinct");
        Check.Eq(read.Count(p => p!.Character == "Writer0"), perWriter, "writer 0's presses all there");
        Check.Eq(read.Count(p => p!.Character == "Writer1"), perWriter, "writer 1's presses all there");

        var reader = new PressStore(folder);
        Check.Eq(reader.Poll(), 2 * perWriter, "a reader loads them all");
        Check.Eq(reader.BadLines, 0, "no bad lines");
        Console.WriteLine($"          (appends that found the file busy every try and waited: {errors[0] + errors[1]})");
    }

    private static void CsvExport()
    {
        string folder = Program.NewFolder("csv");
        var s = new PressStore(folder);
        Press p = Sample();
        p.Character = "Tester, Jr.";
        s.Record(p);
        Press q = Sample();
        q.Components = new List<string> { "=cmd", "Red Taper" };
        q.Complete = false;
        q.End = "timeout";
        s.Record(q);
        string path = s.ExportCsv(new DateTime(2026, 10, 12, 19, 30, 0));
        Check.True(File.Exists(path) && Path.GetDirectoryName(path) == folder, "written next to presses.jsonl");
        Check.Eq(Path.GetFileName(path), "presses-20261012-193000.csv", "file name");
        string[] rows = File.ReadAllLines(path);
        Check.Eq(rows.Length, 3, "header + 2 rows");
        Check.True(rows[0].StartsWith("time_utc,server,character,fragment,fragment_level"), "header");
        Check.True(rows[1].Contains("\"Tester, Jr.\""), "comma value quoted");
        Check.True(rows[1].Contains(",Raw Fragment,185,Pressed. It holds.,Red Taper,190,4,yes,6,"), "columns");
        Check.True(rows[1].Contains("Bounteous: kills drop x1.38 loot | Tourmaline Affinity"), "modifiers joined");
        Check.True(rows[2].Contains("'=cmd; Red Taper"), "a formula-looking cell is defused");
        Check.True(rows[2].Contains("no (timeout)"), "incomplete marked");
    }
}
