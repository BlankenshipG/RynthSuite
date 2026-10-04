using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>A board in Aelrynth's /top overview.</summary>
internal sealed class AelrynthBoardSummary
{
    public string Key = "", Title = "", Leader = "", Value = "";
}

/// <summary>Your standing on one board ("/top me").</summary>
internal sealed class AelrynthStanding
{
    public string Title = "";
    public int Rank;
    public int Of;
    public string Value = "";
}

/// <summary>A line of the "/trial" overview: a dungeon's record, yours and this week's best.</summary>
internal sealed class TrialDungeon
{
    public string Name = "", Landblock = "", Record = "", You = "", Week = "";
}

/// <summary>
/// Aelrynth's leaderboards (tag Aelrynth), read from the replies of the server's own commands
/// (ACEPublic Mods: Aeshnidae.Leaderboard /top, Aeshnidae.Trials /trial; one Broadcast line
/// each, numbers with thousands separators). Formats (Commands.cs):
///   /top               "Leaderboards - N characters, refreshed X. /top &lt;board&gt; ..."
///                      then "  key   Title: Leader, value" (or ": nobody yet")
///   /top &lt;board&gt;       "Title - top K of N[, counted since ...][, per account]:"
///                      then "  r. Name   value[  extra][  &lt;- you]", "  ...  you: #r with v", "  nobody yet."
///   /top me            "Name on the boards:" then "  Title   #r of N, value" or "  Title   -"
///   /trial             a few intro lines, then "  Dungeon (LLLL): records | you: ... | this week: ..."
///   /trial &lt;dungeon&gt;   "Dungeon (LLLL) - top 10, best per character, any tier:"
///                      then "  r. m:ss  Name (tier T, K kills, d MMM)" and "  records per tier/you/this week" lines
/// Entry lines only count right after their header (within a few seconds), so ordinary chat
/// can't add rows. The two commands share /top with ConquestAC's very different reply, which is
/// why each parser only runs on its own server.
/// </summary>
internal sealed class AelrynthBoards
{
    private const long BlockMs = 4000;

    private static readonly Regex OverviewHeader = new(@"^\s*Leaderboards - ([\d,]+) characters, refreshed (.+?)\.\s");
    private static readonly Regex OverviewEntry = new(@"^\s*(\S+)\s+(.+?): (?:(.+), ([\d,]+)|nobody yet)\s*$");
    private static readonly Regex BoardHeader = new(@"^\s*(.+?) - top (\d+) of (\d+)(.*):\s*$");
    private static readonly Regex BoardEntry = new(@"^\s*(\d+)\.\s+(.+?)\s+([\d,]+)(?:\s{2,}(?!<-)(.+?))?(\s{2,}<- you)?\s*$");
    private static readonly Regex BoardYouBelow = new(@"^\s*\.\.\.\s+you: #(\d+) with ([\d,]+)\s*$");
    private static readonly Regex MeHeader = new(@"^\s*(.+?) on the boards:\s*$");
    private static readonly Regex MeEntry = new(@"^\s*(.+?)\s+#(\d+) of (\d+), ([\d,]+)\s*$");
    private static readonly Regex MeNone = new(@"^\s*(.+?)\s+-\s*$");
    private static readonly Regex OneMe = new(@"^\s*(.+?): you are #(\d+) of (\d+) with ([\d,]+)");
    private static readonly Regex TopError = new(@"^\s*(?:No board called '|Leaderboards are unavailable|The boards are still being built)");
    private static readonly Regex TrialDungeonLine = new(@"^\s*(.+?) \(([0-9A-Fa-f]{4})\): (.+?) \| you: (.+?) \| this week: (.+?)\s*$");
    private static readonly Regex TrialBoardHeader = new(@"^\s*(.+?) \(([0-9A-Fa-f]{4})\) - top 10, best per character, any tier:\s*$");
    private static readonly Regex TrialEntry = new(@"^\s*(\d+)\.\s+(\d+:\d{2}(?::\d{2})?)\s+(.+?) \((.+)\)\s*$");
    private static readonly Regex TrialNote = new(@"^\s*(?:records per tier|you|this week \([^)]*\)):\s");
    private static readonly Regex TrialError = new(@"^\s*(?:No Trial matches '|The Trials are switched off|The Trials could not be read)");
    private static readonly Regex TrialIntro = new(@"^\s*Trials - a fresh personal copy of a listed dungeon");
    private static readonly Regex TrialExtra = new(@"^\s*(?:your next attempt here: in \d+ min|running now: .+|Trophy: the week's fastest clear .+)$");

    private enum Block { None, Overview, Board, Me, TrialList, TrialBoard }

    public readonly List<AelrynthBoardSummary> Overview = new();
    public string OverviewNote = "";
    public string BoardTitle = "", BoardNote = "";
    public readonly List<BoardRow> BoardRows = new();
    public string BoardYou = "";
    public readonly List<AelrynthStanding> Standings = new();
    public readonly List<TrialDungeon> Trials = new();
    public readonly List<string> TrialLines = new();
    public string TrialBoardTitle = "";
    public readonly List<BoardRow> TrialRows = new();
    public readonly List<string> TrialNotes = new();
    public string LastError = "";
    public int Revision { get; private set; }

    private Block _block;
    private long _blockMs;
    private readonly ChatRequest _topReq = new(), _trialReq = new();

    public void Clear()
    {
        Overview.Clear(); BoardRows.Clear(); Standings.Clear(); Trials.Clear(); TrialLines.Clear(); TrialRows.Clear(); TrialNotes.Clear();
        OverviewNote = BoardTitle = BoardNote = BoardYou = TrialBoardTitle = LastError = "";
        _block = Block.None;
        Revision++;
    }

    public string RequestTop(string arg, long now)
    {
        _topReq.Sent(now);
        LastError = "";
        if (string.IsNullOrWhiteSpace(arg)) { BoardTitle = ""; BoardRows.Clear(); }
        return string.IsNullOrWhiteSpace(arg) ? "/top" : "/top " + arg.Trim();
    }

    public string RequestTrial(string arg, long now)
    {
        _trialReq.Sent(now);
        LastError = "";
        return string.IsNullOrWhiteSpace(arg) ? "/trial" : "/trial " + arg.Trim();
    }

    private bool InBlock(Block b, long now) => _block == b && now - _blockMs < BlockMs;

    private void Start(Block b, long now) { _block = b; _blockMs = now; }

    /// <summary>A chat line: true when it was a leaderboard reply; <paramref name="ours"/> = it answers RynthOracle's request.</summary>
    public bool OnChatLine(string text, long now, out bool ours)
    {
        ours = false;
        Match m;

        if ((m = OverviewHeader.Match(text)).Success)
        {
            Overview.Clear();
            OverviewNote = $"{m.Groups[1].Value} characters, refreshed {m.Groups[2].Value}";
            Start(Block.Overview, now);
            return Mine(_topReq, now, out ours);
        }
        if ((m = TrialBoardHeader.Match(text)).Success)
        {
            TrialBoardTitle = $"{m.Groups[1].Value} ({m.Groups[2].Value.ToUpperInvariant()})";
            TrialRows.Clear();
            TrialNotes.Clear();
            Start(Block.TrialBoard, now);
            return Mine(_trialReq, now, out ours);
        }
        if ((m = BoardHeader.Match(text)).Success && !text.Contains(" on the boards", StringComparison.Ordinal))
        {
            BoardTitle = m.Groups[1].Value.Trim();
            BoardNote = $"top {m.Groups[2].Value} of {m.Groups[3].Value}{m.Groups[4].Value}";
            BoardRows.Clear();
            BoardYou = "";
            Start(Block.Board, now);
            return Mine(_topReq, now, out ours);
        }
        if ((m = MeHeader.Match(text)).Success)
        {
            Standings.Clear();
            Start(Block.Me, now);
            return Mine(_topReq, now, out ours);
        }
        if ((m = OneMe.Match(text)).Success)
        {
            BoardYou = $"{m.Groups[1].Value}: #{m.Groups[2].Value} of {m.Groups[3].Value} with {m.Groups[4].Value}";
            return Mine(_topReq, now, out ours);
        }
        if (TopError.IsMatch(text))
        {
            LastError = text.Trim();
            return Mine(_topReq, now, out ours);
        }
        if (TrialError.IsMatch(text))
        {
            LastError = text.Trim();
            return Mine(_trialReq, now, out ours);
        }
        if ((m = TrialDungeonLine.Match(text)).Success)
        {
            if (!InBlock(Block.TrialList, now)) { Trials.Clear(); TrialLines.Clear(); }
            Trials.Add(new TrialDungeon
            {
                Name = m.Groups[1].Value.Trim(), Landblock = m.Groups[2].Value.ToUpperInvariant(),
                Record = m.Groups[3].Value.Trim(), You = m.Groups[4].Value.Trim(), Week = m.Groups[5].Value.Trim(),
            });
            Start(Block.TrialList, now);
            return Mine(_trialReq, now, out ours);
        }

        if (InBlock(Block.Overview, now) && (m = OverviewEntry.Match(text)).Success)
        {
            Overview.Add(new AelrynthBoardSummary
            {
                Key = m.Groups[1].Value, Title = m.Groups[2].Value.Trim(),
                Leader = m.Groups[3].Success ? m.Groups[3].Value.Trim() : "nobody yet",
                Value = m.Groups[4].Success ? m.Groups[4].Value : "",
            });
            _blockMs = now;
            return Mine(_topReq, now, out ours);
        }
        if (InBlock(Block.Board, now))
        {
            if ((m = BoardEntry.Match(text)).Success)
            {
                int.TryParse(m.Groups[1].Value, out int rank);
                BoardRows.Add(new BoardRow
                {
                    Rank = rank, Name = m.Groups[2].Value.Trim(), Value = m.Groups[3].Value,
                    Extra = m.Groups[4].Success ? m.Groups[4].Value.Trim() : "", IsYou = m.Groups[5].Success,
                });
                _blockMs = now;
                return Mine(_topReq, now, out ours);
            }
            if ((m = BoardYouBelow.Match(text)).Success)
            {
                BoardYou = $"you: #{m.Groups[1].Value} with {m.Groups[2].Value}";
                return Mine(_topReq, now, out ours);
            }
            if (text.Trim() == "nobody yet.") return Mine(_topReq, now, out ours);
        }
        if (InBlock(Block.Me, now))
        {
            if ((m = MeEntry.Match(text)).Success)
            {
                int.TryParse(m.Groups[2].Value, out int r);
                int.TryParse(m.Groups[3].Value, out int of);
                Standings.Add(new AelrynthStanding { Title = m.Groups[1].Value.Trim(), Rank = r, Of = of, Value = m.Groups[4].Value });
                _blockMs = now;
                return Mine(_topReq, now, out ours);
            }
            if ((m = MeNone.Match(text)).Success)
            {
                Standings.Add(new AelrynthStanding { Title = m.Groups[1].Value.Trim() });
                _blockMs = now;
                return Mine(_topReq, now, out ours);
            }
        }
        if (InBlock(Block.TrialBoard, now))
        {
            if ((m = TrialEntry.Match(text)).Success)
            {
                int.TryParse(m.Groups[1].Value, out int rank);
                TrialRows.Add(new BoardRow { Rank = rank, Value = m.Groups[2].Value, Name = m.Groups[3].Value.Trim(), Extra = m.Groups[4].Value.Trim() });
                _blockMs = now;
                return Mine(_trialReq, now, out ours);
            }
            if (TrialNote.IsMatch(text) || text.Trim() == "no clear yet")
            {
                TrialNotes.Add(text.Trim());
                _blockMs = now;
                return Mine(_trialReq, now, out ours);
            }
        }
        // The rest of a /trial overview: the intro, "next attempt", "running now" and trophy lines.
        if (TrialIntro.IsMatch(text))
        {
            Trials.Clear();
            TrialLines.Clear();
            TrialLines.Add(text.Trim());
            Start(Block.TrialList, now);
            return Mine(_trialReq, now, out ours);
        }
        if (InBlock(Block.TrialList, now) && TrialExtra.IsMatch(text))
        {
            TrialLines.Add(text.Trim());
            _blockMs = now;
            return Mine(_trialReq, now, out ours);
        }
        return false;
    }

    private bool Mine(ChatRequest req, long now, out bool ours)
    {
        ours = req.Awaiting(now);
        req.Extend(now);
        Revision++;
        return true;
    }
}
