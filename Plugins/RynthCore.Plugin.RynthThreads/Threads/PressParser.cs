using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthThreads.Threads;

/// <summary>
/// Reads Fragment Press results out of chat, one line at a time. A press starts at
///   "Using the Raw Fragment (Level 185) with the Fragment Press"
/// and collects, in any order and with other chat in between:
///   "Pressed. It holds."                 the outcome (any "Pressed..." line)
///   "Components selected: A, B"          (or "No components ...")
///   "Remaining slots drawn at random."   (any "... drawn at random" line)
///   "The gem carries:"                   then one "  Name - description" line per modifier
/// and ends at "Level 190, 4 entries." (complete). A press that never gets there is still saved,
/// marked incomplete: after <see cref="TimeoutMs"/> without a line of its own, when another
/// "Using ..." line comes, or at logout. Lines that don't fit are kept in the press's Extra list
/// (other players' chat lands there too). Never throws on odd text.
/// </summary>
internal sealed class PressParser
{
    /// <summary>A press's lines arrive within the same second; this long without one ends it.</summary>
    public const long TimeoutMs = 15_000;
    private const int MaxLineLength = 400;
    private const int MaxRawLines = 80;
    private const int MaxExtraLines = 40;
    private const int MaxModifiers = 40;

    private static readonly RegexOptions Opt = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
    private static readonly Regex UseRx = new(@"^Using (?:the |an? )?(?<item>.+?) with (?:the |an? )?(?<tool>[^.]*\bPress)\b", Opt);
    private static readonly Regex LevelInName = new(@"^(?<name>.*?)\s*\(\s*Level\s+(?<lvl>\d+)\s*\)\s*$", Opt);
    private static readonly Regex ComponentsRx = new(@"^Components?(?: selected)?\s*:\s*(?<list>.*)$", Opt);
    private static readonly Regex NoComponentsRx = new(@"^No components\b", Opt);
    private static readonly Regex CarriesRx = new(@"^The [^:]{1,40} carries\s*:?\s*$", Opt);
    private static readonly Regex EndRx = new(@"^Level\s+(?<lvl>\d+)\s*(?:,\s*(?<entries>\d+)\s+entr(?:y|ies))?\s*\.?\s*$", Opt);
    // "Name - description": a short name of letters, spaces, apostrophes and hyphens.
    private static readonly Regex ModifierRx = new(@"^(?<name>[A-Za-z][A-Za-z' \-]{0,47}?)\s+[-–—]\s+(?<desc>.+)$", Opt);
    private static readonly Regex NumberRx = new(
        @"(?<![A-Za-z0-9.])(?:(?<mult>[x×]\s?\d+(?:\.\d+)?)|(?<plus>[+\-]\d+(?:\.\d+)?)(?!\d|\.\d|\s?%)|(?<pct>[+\-]?\d+(?:\.\d+)?\s?%)|(?<num>\d+(?:\.\d+)?))",
        RegexOptions.CultureInvariant);

    private enum State { Idle, Open, Carries }

    private State _state = State.Idle;
    private Press? _press;
    private long _lastOwnLineMs;

    /// <summary>A press is being read.</summary>
    public bool Busy => _press != null;

    /// <summary>
    /// Feeds one chat message (it may hold several lines). Returns the presses it finished, or
    /// null. <paramref name="nowMs"/> is a monotonic clock (Environment.TickCount64).
    /// </summary>
    public List<Press>? Feed(string? text, long nowMs)
    {
        if (string.IsNullOrEmpty(text)) return null;
        List<Press>? done = null;
        foreach (string piece in text.Split('\n'))
        {
            string line = piece.TrimEnd('\r', ' ', '\t');
            if (line.Trim().Length == 0) continue;
            if (line.Length > MaxLineLength) line = line.Substring(0, MaxLineLength);
            try
            {
                Press? finished = FeedLine(line, nowMs);
                if (finished != null) (done ??= new List<Press>()).Add(finished);
            }
            catch
            {
                // Odd text must never stop chat; the line is just not used.
            }
        }
        return done;
    }

    /// <summary>Ends a press that has gone quiet. Returns it (incomplete), or null.</summary>
    public Press? Tick(long nowMs)
    {
        if (_press == null || nowMs - _lastOwnLineMs < TimeoutMs) return null;
        return Finish("timeout");
    }

    /// <summary>Ends whatever is open (logout, unload). Returns it (incomplete), or null.</summary>
    public Press? Flush(string reason) => _press == null ? null : Finish(reason);

    private Press? FeedLine(string line, long nowMs)
    {
        string t = line.Trim();
        Press? previous = null;

        if (t.StartsWith("Using ", StringComparison.OrdinalIgnoreCase))
        {
            Match use = UseRx.Match(t);
            if (_press != null) previous = Finish("next use");
            if (!use.Success) return previous;
            Start(use, line, nowMs);
            return previous;
        }
        if (_press == null) return null;

        if (_state == State.Carries)
        {
            Match mod = ModifierRx.Match(t);
            // Modifier lines are indented; an unindented one must still look exactly like "Name - text".
            bool indented = line.Length > 0 && char.IsWhiteSpace(line[0]);
            if (mod.Success && (indented || !LooksLikeChat(t)))
            {
                if (_press.Modifiers.Count < MaxModifiers) _press.Modifiers.Add(MakeModifier(mod));
                Own(line, nowMs);
                return null;
            }
        }

        Match end = EndRx.Match(t);
        if (end.Success)
        {
            _press.ResultLevel = Int(end.Groups["lvl"].Value);
            if (end.Groups["entries"].Success) _press.Entries = Int(end.Groups["entries"].Value);
            Own(line, nowMs);
            return Finish("done", complete: true);
        }
        if (t.StartsWith("Pressed", StringComparison.OrdinalIgnoreCase) && _press.Outcome.Length == 0)
        {
            _press.Outcome = t;
            Own(line, nowMs);
            return null;
        }
        Match comps = ComponentsRx.Match(t);
        if (comps.Success && !_press.ComponentsSeen)
        {
            _press.ComponentsSeen = true;
            _press.Components = SplitComponents(comps.Groups["list"].Value);
            Own(line, nowMs);
            return null;
        }
        if (NoComponentsRx.IsMatch(t) && !_press.ComponentsSeen)
        {
            _press.ComponentsSeen = true;
            Own(line, nowMs);
            return null;
        }
        if (t.IndexOf("drawn at random", StringComparison.OrdinalIgnoreCase) >= 0 && _press.RandomNote.Length == 0)
        {
            _press.RandomNote = t;
            Own(line, nowMs);
            return null;
        }
        if (CarriesRx.IsMatch(t))
        {
            _state = State.Carries;
            Own(line, nowMs);
            return null;
        }

        // Not ours (or not known yet): keep it beside the press, don't let it extend the timeout.
        if (_press.Extra.Count < MaxExtraLines) _press.Extra.Add(line);
        return null;
    }

    private void Start(Match use, string line, long nowMs)
    {
        string item = use.Groups["item"].Value.Trim();
        var p = new Press
        {
            Id = Press.NewId(),
            TimeUtc = DateTime.UtcNow,
            Tool = use.Groups["tool"].Value.Trim(),
            Fragment = item,
        };
        Match lv = LevelInName.Match(item);
        if (lv.Success)
        {
            p.Fragment = lv.Groups["name"].Value.Trim();
            p.FragmentLevel = Int(lv.Groups["lvl"].Value);
        }
        _press = p;
        _state = State.Open;
        Own(line, nowMs);
    }

    private void Own(string line, long nowMs)
    {
        if (_press!.Raw.Count < MaxRawLines) _press.Raw.Add(line);
        _lastOwnLineMs = nowMs;
    }

    private Press Finish(string reason, bool complete = false)
    {
        Press p = _press!;
        p.Complete = complete;
        p.End = reason;
        _press = null;
        _state = State.Idle;
        return p;
    }

    /// <summary>Chat from players and channels ("Bob says, ...", "[Trade] ...", "You tell ...").</summary>
    private static bool LooksLikeChat(string t) =>
        t.StartsWith("[", StringComparison.Ordinal) || t.Contains(" says, ") || t.Contains(" tells you")
        || t.StartsWith("You ", StringComparison.Ordinal) || t.Contains('"');

    internal static List<string> SplitComponents(string list)
    {
        var result = new List<string>();
        string s = list.Trim().TrimEnd('.');
        if (s.Length == 0 || s.Equals("none", StringComparison.OrdinalIgnoreCase)) return result;
        foreach (string part in s.Replace(" and ", ",", StringComparison.OrdinalIgnoreCase).Split(','))
        {
            string c = part.Trim();
            if (c.Length > 0 && !result.Exists(x => x.Equals(c, StringComparison.OrdinalIgnoreCase))) result.Add(c);
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static GemModifier MakeModifier(Match m)
    {
        string desc = m.Groups["desc"].Value.Trim();
        return new GemModifier
        {
            Name = m.Groups["name"].Value.Trim(),
            Description = desc,
            Numbers = Numbers(desc),
        };
    }

    /// <summary>Every number in a description with its form: x1.38, 12%, +29 (or -5), 75.</summary>
    internal static List<ModNumber> Numbers(string text)
    {
        var list = new List<ModNumber>();
        foreach (Match m in NumberRx.Matches(text))
        {
            string kind;
            string raw;
            if (m.Groups["mult"].Success) { kind = ModNumber.Mult; raw = m.Groups["mult"].Value; }
            else if (m.Groups["plus"].Success) { kind = ModNumber.Plus; raw = m.Groups["plus"].Value; }
            else if (m.Groups["pct"].Success) { kind = ModNumber.Pct; raw = m.Groups["pct"].Value; }
            else { kind = ModNumber.Num; raw = m.Groups["num"].Value; }
            string digits = raw.TrimStart('x', '×', '+').TrimEnd('%').Trim();
            if (double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                list.Add(new ModNumber(v, kind, raw.Replace(" ", string.Empty)));
        }
        return list;
    }

    private static int? Int(string s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;
}
