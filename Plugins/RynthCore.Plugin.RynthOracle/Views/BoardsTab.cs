// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT (the ConquestAC part). See THIRD_PARTY_NOTICES.md.
using System;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>
/// Leaderboards, server-specific: ConquestAC's /top boards (upstream's Top Players tab), or
/// Aelrynth's /top boards and /trial dungeon records. Each server's view only exists on that
/// server (tab tags in OracleView), since both servers answer /top in different shapes.
/// </summary>
internal sealed partial class OracleView
{
    private int _conquestBoard;
    private int _aelrynthView;            // 0 boards, 1 me, 2 trials
    private string _aelrynthBoardArg = "";
    private string _trialArg = "";

    private void DrawBoards(UiWindow w)
    {
        if (_c.Server.Is(ServerTags.Conquest)) DrawConquestBoards(w);
        else if (_c.Server.Is(ServerTags.Aelrynth)) DrawAelrynthBoards(w);
    }

    private void DrawConquestBoards(UiWindow w)
    {
        ConquestData cq = _c.Conquest;
        _conquestBoard = Math.Clamp(_conquestBoard, 0, cq.Boards.Count - 1);
        for (int i = 0; i < cq.Boards.Count; i++)
        {
            int index = i;
            if (i > 0) w.SameLine();
            w.ToggleButton(cq.Boards[i].Label, "cb.tab." + i, i == _conquestBoard, () => _conquestBoard = index, small: true);
        }
        ConquestBoard b = cq.Boards[_conquestBoard];
        w.Button("Refresh", "cb.refresh", () => _c.Send(cq.RequestBoard(b, _c.NowMs)));
        w.Tooltip($"Sends {b.Command} and reads the reply.");
        w.SameLine();
        w.TextDisabled(b.UpdatedUtc == DateTime.MinValue ? "not read yet" : $"read at {b.UpdatedUtc.ToLocalTime():HH:mm}");
        if (b.You != null) w.Text($"You: #{b.You.Rank} with {b.You.Value}");
        DrawRows(w, "cb.rows", b.Rows);
    }

    private static void DrawRows(UiWindow w, string id, System.Collections.Generic.List<BoardRow> rows)
    {
        w.BeginChild(id, 0f, 0f, border: true);
        w.TextDisabled("Rank");
        w.SameLine(60f);
        w.TextDisabled("Name");
        w.SameLine(280f);
        w.TextDisabled("Value");
        foreach (BoardRow r in rows)
        {
            w.Text(r.Rank.ToString());
            w.SameLine(60f);
            if (r.IsYou) w.TextColored(UiColors.Gold, Cut(r.Name, 30));
            else w.Text(Cut(r.Name, 30));
            w.SameLine(280f);
            w.Text(r.Value);
            if (r.Extra.Length > 0) { w.SameLine(420f); w.TextDisabled(Cut(r.Extra, 40)); }
        }
        if (rows.Count == 0) w.TextDisabled("Nothing read yet. Press Refresh.");
        w.EndChild();
    }

    private void DrawAelrynthBoards(UiWindow w)
    {
        AelrynthBoards a = _c.Boards;
        w.ToggleButton("Boards", "ab.v0", _aelrynthView == 0, () => _aelrynthView = 0, small: true);
        w.SameLine();
        w.ToggleButton("Me", "ab.v1", _aelrynthView == 1, () => _aelrynthView = 1, small: true);
        w.SameLine();
        w.ToggleButton("Trials", "ab.v2", _aelrynthView == 2, () => _aelrynthView = 2, small: true);
        if (a.LastError.Length > 0) w.TextColored(UiColors.Red, a.LastError);

        if (_aelrynthView == 1)
        {
            w.Button("Refresh", "ab.me", () => _c.Send(a.RequestTop("me", _c.NowMs)));
            w.Tooltip("Sends /top me.");
            w.BeginChild("ab.melist", 0f, 0f, border: true);
            foreach (AelrynthStanding s in a.Standings)
            {
                w.Text(Cut(s.Title, 34));
                w.SameLine(260f);
                w.Text(s.Rank > 0 ? $"#{s.Rank} of {s.Of}" : "-");
                if (s.Value.Length > 0) { w.SameLine(380f); w.Text(s.Value); }
            }
            if (a.Standings.Count == 0) w.TextDisabled("Nothing read yet. Press Refresh.");
            w.EndChild();
            return;
        }

        if (_aelrynthView == 2)
        {
            w.Button("Refresh", "ab.trials", () => _c.Send(a.RequestTrial("", _c.NowMs)));
            w.Tooltip("Sends /trial.");
            w.SameLine();
            w.InputText("Dungeon", "ab.trialarg", _trialArg, 48, s => _trialArg = s);
            w.SameLine();
            w.Button("Top 10", "ab.trialtop", () => { if (_trialArg.Trim().Length > 0) _c.Send(a.RequestTrial(_trialArg, _c.NowMs)); });
            w.BeginChild("ab.triallist", 0f, 0f, border: true);
            foreach (TrialDungeon d in a.Trials)
            {
                TrialDungeon row = d;
                w.Selectable($"{d.Name} ({d.Landblock})", "ab.t." + d.Landblock, false,
                    () => { _trialArg = row.Name; _c.Send(a.RequestTrial(row.Name, _c.NowMs)); });
                w.Tooltip("Click for this dungeon's top 10.");
                w.TextDisabled("  record:"); w.SameLine(90f); w.TextWrapped(d.Record);
                w.TextDisabled("  you:"); w.SameLine(90f); w.Text(d.You);
                w.TextDisabled("  this week:"); w.SameLine(90f); w.Text(d.Week);
            }
            foreach (string line in a.TrialLines) if (!line.StartsWith("Trials - ", StringComparison.Ordinal)) w.TextWrapped(line);
            if (a.TrialBoardTitle.Length > 0)
            {
                w.SeparatorText(a.TrialBoardTitle);
                foreach (BoardRow r in a.TrialRows)
                {
                    w.Text($"{r.Rank,2}. {r.Value}");
                    w.SameLine(90f);
                    w.Text(Cut(r.Name, 28));
                    w.SameLine(300f);
                    w.TextDisabled(Cut(r.Extra, 40));
                }
                foreach (string n in a.TrialNotes) w.TextWrapped(n);
            }
            if (a.Trials.Count == 0 && a.TrialBoardTitle.Length == 0) w.TextDisabled("Nothing read yet. Press Refresh.");
            w.EndChild();
            return;
        }

        w.Button("All boards", "ab.all", () => _c.Send(a.RequestTop("", _c.NowMs)));
        w.Tooltip("Sends /top: every board's leader.");
        w.SameLine();
        w.InputText("Board", "ab.arg", _aelrynthBoardArg, 32, s => _aelrynthBoardArg = s);
        w.SameLine();
        w.Button("Top 10", "ab.board", () => { if (_aelrynthBoardArg.Trim().Length > 0) _c.Send(a.RequestTop(_aelrynthBoardArg, _c.NowMs)); });
        if (a.OverviewNote.Length > 0) w.TextDisabled(a.OverviewNote);

        if (a.BoardTitle.Length > 0)
        {
            w.SeparatorText($"{a.BoardTitle} ({a.BoardNote})");
            if (a.BoardYou.Length > 0) w.TextColored(UiColors.Gold, a.BoardYou);
            DrawRows(w, "ab.rows", a.BoardRows);
            return;
        }
        w.BeginChild("ab.overview", 0f, 0f, border: true);
        foreach (AelrynthBoardSummary s in a.Overview)
        {
            AelrynthBoardSummary row = s;
            w.Selectable(Cut(s.Title, 30), "ab.o." + s.Key, false, () => { _aelrynthBoardArg = row.Key; _c.Send(a.RequestTop(row.Key, _c.NowMs)); }, 230f);
            w.Tooltip($"/top {s.Key}");
            w.SameLine(240f);
            w.Text(Cut(s.Leader, 24));
            if (s.Value.Length > 0) { w.SameLine(430f); w.Text(s.Value); }
        }
        if (a.Overview.Count == 0) w.TextDisabled("Nothing read yet. Press All boards.");
        w.EndChild();
    }
}
