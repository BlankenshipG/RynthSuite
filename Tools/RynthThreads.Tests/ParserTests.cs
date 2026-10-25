using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthThreads.Threads;
using RynthCore.RynthAiTests.Harness;

namespace RynthThreads.Tests;

// The chat parser (Threads/PressParser.cs). The sample is real DreamWeave chat from 2026-10-12
// (timestamps removed: the plugin sees the text without them), with another player's line in it.
internal static class ParserTests
{
    public static readonly string[] Sample =
    {
        "Using the Raw Fragment (Level 185) with the Fragment Press",
        "Yorick is recalling to the marketplace.",
        "Pressed. It holds.",
        "Components selected: Red Taper",
        "Remaining slots drawn at random.",
        "The gem carries:",
        "  Bounteous - kills drop x1.38 loot",
        "  Tourmaline Affinity - 12% of kills leave tourmaline to salvage",
        "  Hardy - monsters have x1.81 health",
        "  Enraged - the boss attacks with +29 damage rating, hitting harder",
        "  Teeming - 75% more monsters",
        "  Savage - monsters attack with +20 damage rating, hitting harder",
        "Level 190, 4 entries.",
    };

    public static void Register(Runner r)
    {
        r.Add("parse: the real sample, line by line, with another player's line in it", SampleLineByLine);
        r.Add("parse: the sample as one message with newlines", SampleOneMessage);
        r.Add("parse: several components (commas, 'and'), sorted", SeveralComponents);
        r.Add("parse: no components (no line, 'none', 'No components ...')", NoComponents);
        r.Add("parse: incomplete presses (timeout, next press, other Using line, logout)", Incomplete);
        r.Add("parse: unknown and odd lines never throw and never become modifiers", UnknownLines);
        r.Add("parse: numbers and their forms", NumberForms);
    }

    private static List<Press> FeedAll(PressParser parser, IEnumerable<string> lines, long now = 1000)
    {
        var done = new List<Press>();
        foreach (string line in lines)
            if (parser.Feed(line, now) is { } d) done.AddRange(d);
        return done;
    }

    private static void SampleLineByLine()
    {
        var parser = new PressParser();
        List<Press> done = FeedAll(parser, Sample);
        Check.Eq(done.Count, 1, "one press");
        if (done.Count != 1) return;
        CheckSample(done[0]);
        Check.False(parser.Busy, "parser idle after the Level line");
    }

    private static void SampleOneMessage()
    {
        var parser = new PressParser();
        List<Press>? done = parser.Feed(string.Join("\n", Sample) + "\n", 1000);
        Check.Eq(done?.Count ?? 0, 1, "one press from one message");
        if (done is { Count: 1 }) CheckSample(done[0]);
        // CRLF too.
        done = new PressParser().Feed(string.Join("\r\n", Sample), 1000);
        Check.Eq(done?[0].Modifiers.Count ?? -1, 6, "CRLF lines: 6 modifiers");
    }

    internal static void CheckSample(Press p)
    {
        Check.Eq(p.Fragment, "Raw Fragment", "fragment name");
        Check.Eq(p.FragmentLevel, 185, "fragment level");
        Check.Eq(p.Tool, "Fragment Press", "tool");
        Check.Eq(p.Outcome, "Pressed. It holds.", "outcome");
        Check.True(p.Held, "held");
        Check.True(p.ComponentsSeen, "components line seen");
        Check.Eq(string.Join("|", p.Components), "Red Taper", "components");
        Check.Eq(p.RandomNote, "Remaining slots drawn at random.", "random note");
        Check.Eq(p.ResultLevel, 190, "result level");
        Check.Eq(p.Entries, 4, "entries (raw, not the modifier count)");
        Check.True(p.Complete, "complete");
        Check.Eq(p.End, "done", "end reason");
        Check.Eq(p.Modifiers.Count, 6, "six modifiers");
        Check.Eq(p.Raw.Count, 12, "12 own raw lines");
        Check.Eq(string.Join("|", p.Extra), "Yorick is recalling to the marketplace.", "the foreign line is kept apart");
        Check.False(p.Raw.Any(l => l.Contains("Yorick")), "no foreign line in Raw");
        Check.Eq(p.Raw[5], "  Bounteous - kills drop x1.38 loot", "raw lines kept as is (indent too)");
        if (p.Modifiers.Count != 6) return;

        (string Name, string Kinds, string Values, string Desc)[] want =
        {
            ("Bounteous", "mult", "1.38", "kills drop x1.38 loot"),
            ("Tourmaline Affinity", "pct", "12", "12% of kills leave tourmaline to salvage"),
            ("Hardy", "mult", "1.81", "monsters have x1.81 health"),
            ("Enraged", "plus", "29", "the boss attacks with +29 damage rating, hitting harder"),
            ("Teeming", "pct", "75", "75% more monsters"),
            ("Savage", "plus", "20", "monsters attack with +20 damage rating, hitting harder"),
        };
        for (int i = 0; i < want.Length; i++)
        {
            GemModifier m = p.Modifiers[i];
            Check.Eq(m.Name, want[i].Name, $"modifier {i} name");
            Check.Eq(m.Description, want[i].Desc, $"{want[i].Name} description");
            Check.Eq(string.Join(",", m.Numbers.Select(n => n.Kind)), want[i].Kinds, $"{want[i].Name} number kinds");
            Check.Eq(string.Join(",", m.Numbers.Select(n => n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))), want[i].Values, $"{want[i].Name} values");
        }
        Check.Eq(p.Modifiers[0].Numbers[0].Text, "x1.38", "text as written");
        Check.Eq(p.Modifiers[3].Numbers[0].Text, "+29", "rating text");
    }

    private static string[] Press(string components, params string[] mods)
    {
        var lines = new List<string> { "Using the Raw Fragment (Level 100) with the Fragment Press", "Pressed. It holds." };
        if (components != null) lines.Add(components);
        lines.Add("Remaining slots drawn at random.");
        lines.Add("The gem carries:");
        lines.AddRange(mods.Select(m => "  " + m));
        lines.Add("Level 104, 2 entries.");
        return lines.ToArray();
    }

    private static void SeveralComponents()
    {
        Press p = FeedAll(new PressParser(), Press("Components selected: Red Taper, Blue Taper, Pyreal", "Hardy - monsters have x1.5 health")).Single();
        Check.Eq(string.Join("|", p.Components), "Blue Taper|Pyreal|Red Taper", "three, sorted");
        p = FeedAll(new PressParser(), Press("Components selected: Red Taper and Yellow Taper.", "Hardy - x")).Single();
        Check.Eq(string.Join("|", p.Components), "Red Taper|Yellow Taper", "'and' splits, trailing dot dropped");
        p = FeedAll(new PressParser(), Press("Components selected: Red Taper, red taper", "Hardy - x")).Single();
        Check.Eq(p.Components.Count, 1, "the same component twice counts once");
    }

    private static void NoComponents()
    {
        Press p = FeedAll(new PressParser(), Press(null!, "Teeming - 75% more monsters")).Single();
        Check.False(p.ComponentsSeen, "no line: not seen");
        Check.Eq(p.Components.Count, 0, "no line: none");
        Check.True(p.Complete && p.Usable, "still complete and usable");

        p = FeedAll(new PressParser(), Press("Components selected: none", "Teeming - 75% more monsters")).Single();
        Check.True(p.ComponentsSeen && p.Components.Count == 0, "'none'");
        p = FeedAll(new PressParser(), Press("No components selected.", "Teeming - 75% more monsters")).Single();
        Check.True(p.ComponentsSeen && p.Components.Count == 0, "'No components selected.'");
        Check.Eq(p.ComponentsText, "no components", "shown as 'no components'");
    }

    private static void Incomplete()
    {
        // Timeout: the Level line never comes.
        var parser = new PressParser();
        string[] lines = Sample.Take(Sample.Length - 1).ToArray();
        Check.Eq(FeedAll(parser, lines, 1000).Count, 0, "nothing finished yet");
        Check.Null(parser.Tick(1000 + PressParser.TimeoutMs - 1), "not before the timeout");
        // Other chat doesn't keep it open.
        parser.Feed("Someone says, \"hello\"", 1000 + PressParser.TimeoutMs - 1);
        Press? t = parser.Tick(1000 + PressParser.TimeoutMs);
        Check.NotNull(t, "timed out");
        if (t != null)
        {
            Check.False(t.Complete, "incomplete");
            Check.Eq(t.End, "timeout", "reason");
            Check.Eq(t.Modifiers.Count, 6, "what was gathered is kept");
            Check.Null(t.ResultLevel, "no result level");
            Check.False(t.Usable, "not used by the inference");
        }

        // The next press starts before this one's Level line.
        parser = new PressParser();
        var done = FeedAll(parser, Sample.Take(4).Concat(Sample));
        Check.Eq(done.Count, 2, "two presses");
        if (done.Count == 2)
        {
            Check.True(!done[0].Complete && done[0].End == "next use", "first: incomplete, next use");
            Check.Eq(done[0].Outcome, "Pressed. It holds.", "first kept its outcome");
            Check.True(done[1].Complete && done[1].Modifiers.Count == 6, "second complete");
        }

        // Some other "Using ..." line ends it and starts nothing.
        parser = new PressParser();
        done = FeedAll(parser, Sample.Take(3).Append("Using the Mana Stone on the Robe of Ages"));
        Check.Eq(done.Count, 1, "ended by another Using line");
        Check.False(parser.Busy, "no new press from an unrelated Using line");
        Check.Eq(done.Count == 1 ? done[0].End : "", "next use", "reason");

        // Logout.
        parser = new PressParser();
        FeedAll(parser, Sample.Take(2));
        Press? f = parser.Flush("logout");
        Check.True(f != null && f.End == "logout" && !f.Complete, "flush at logout");
        Check.Null(parser.Flush("logout"), "nothing left to flush");
    }

    private static void UnknownLines()
    {
        var parser = new PressParser();
        string[] junk =
        {
            "", "   ", "\n\n", "Level 190, 4 entries.", "Pressed. It holds.", "  Hardy - monsters have x1.81 health",
            new string('x', 5000), "Components selected:", "The gem carries:", "☃ éè ???", "Using", "Using the",
            "with the Fragment Press", "1234567890123456789012345678901234567890", "  - just a dash", "x%+",
        };
        int finished = 0;
        foreach (string j in junk)
            finished += parser.Feed(j, 1000)?.Count ?? 0;
        Check.Eq(finished, 0, "nothing comes out of junk while idle");
        Check.False(parser.Busy, "still idle");
        Check.Null(parser.Feed(null, 1000), "null text");

        // Inside a press: junk goes to Extra, chat-like dash lines don't become modifiers, a failed outcome is kept.
        var lines = new List<string>
        {
            "Using the Woven Shard (Level 12) with the Fragment Press",
            "Pressed. It cracks.",
            "The gem carries:",
            "Bob says, \"Hardy - is that good?\"",
            "[Trade] Alice - selling tapers",
            "☃☃☃",
            "  Strange Sigil - something we've never seen, 3 times",
            "  not a modifier line",
            new string('y', 2000),
            "Level 12, 1 entry.",
        };
        List<Press> done = FeedAll(parser, lines);
        Check.Eq(done.Count, 1, "one press");
        if (done.Count != 1) return;
        Press p = done[0];
        Check.Eq(p.Fragment, "Woven Shard", "other fragment name");
        Check.Eq(p.FragmentLevel, 12, "its level");
        Check.Eq(p.Outcome, "Pressed. It cracks.", "a failed outcome is kept as said");
        Check.False(p.Held, "not held");
        Check.Eq(p.Modifiers.Count, 1, "only the real modifier line");
        Check.Eq(p.Modifiers.Count == 1 ? p.Modifiers[0].Name : "", "Strange Sigil", "unknown modifier name kept");
        Check.Eq(p.Entries, 1, "'1 entry' singular");
        Check.True(p.Extra.Count >= 4, "unfitting lines kept in Extra");
        Check.True(p.Extra.All(l => l.Length <= 400), "long lines cut");

        // A fragment without a level, and a press that isn't the Fragment Press.
        done = FeedAll(new PressParser(), new[] { "Using the Odd Shard with the Fragment Press", "Level 3, 0 entries." });
        Check.True(done.Count == 1 && done[0].FragmentLevel == null && done[0].Fragment == "Odd Shard", "no level in the name");
        Check.True(done.Count == 1 && done[0].Complete && !done[0].Usable, "complete, but no modifiers: not usable");
        done = FeedAll(new PressParser(), new[] { "Using the Pyreal Ingot with the Forge", "Level 3, 0 entries." });
        Check.Eq(done.Count, 0, "not a Press: ignored");
    }

    private static void NumberForms()
    {
        string Forms(string s) => string.Join(" ", PressParser.Numbers(s).Select(n => n.Kind + ":" + n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Check.Eq(Forms("kills drop x1.38 loot"), "mult:1.38", "multiplier");
        Check.Eq(Forms("12% of kills"), "pct:12", "percent");
        Check.Eq(Forms("+29 damage rating"), "plus:29", "rating");
        Check.Eq(Forms("attacks with +29."), "plus:29", "rating before a full stop");
        Check.Eq(Forms("-5 damage rating"), "plus:-5", "negative rating");
        Check.Eq(Forms("+12% more"), "pct:12", "signed percent is a percent");
        Check.Eq(Forms("spawns 3 bosses in 10.5 seconds"), "num:3 num:10.5", "plain numbers");
        Check.Eq(Forms("max5 and box2"), "", "digits glued to words aren't numbers");
        Check.Eq(Forms("no numbers here"), "", "none");
    }
}
