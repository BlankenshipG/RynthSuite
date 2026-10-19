using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>
/// The Refresh button every page of the Oracle window starts with, and the status line beside it.
/// What "refresh" means depends on where a page's data comes from:
///   - data the server owns (quest flags, ConquestAC's bank and boards, Aelrynth's /top and /trial)
///     is asked for again with the same command the page always used, and the line waits for the
///     reply;
///   - data read from the game client (level, buffs, titles, fellowship) is re-read at once instead
///     of at the next timer;
///   - files (the About page) are loaded again.
/// A page that needs both does both. Nothing here blocks: a request is one chat command, and the
/// same request is refused for <see cref="CooldownMs"/> after it went out (the quest read has its
/// own 5 s gap), so the button can't spam the server.
/// </summary>
internal sealed partial class OracleView
{
    private const long CooldownMs = 3000;
    private const long ReplyWaitMs = 15_000;

    /// <summary>What a Refresh did, and (when it asked the server) how to tell the reply has landed.</summary>
    private sealed record RefreshResult(string What, Func<bool>? Done = null);

    private string _refreshScope = "", _refreshWhat = "";
    private Func<bool>? _refreshDone;
    private bool _refreshDoneSeen;
    private long _refreshAtMs;
    private DateTime _refreshAtUtc, _refreshDoneUtc;
    private readonly Dictionary<string, long> _refreshSent = new();

    /// <summary>The tab, plus the sub-page a tab has (Conquest's views, the boards), so a status line stays with its own page.</summary>
    private string RefreshScope(string tab) => tab switch
    {
        TabConquest => tab + _conquestView,
        TabBoards => tab + _aelrynthView + _conquestBoard,
        _ => tab,
    };

    private void DrawRefreshRow(UiWindow w, TabDef def)
    {
        string scope = RefreshScope(def.Name);
        w.Button("Refresh", "refresh." + scope, () => DoRefresh(def, scope), small: true);
        w.Tooltip(def.Help());
        if (_refreshScope != scope || _refreshWhat.Length == 0) return;

        w.SameLine();
        if (_refreshDone != null && !_refreshDoneSeen && _refreshDone())
        {
            _refreshDoneSeen = true;
            _refreshDoneUtc = _c.UtcNow;
        }
        if (_refreshDone != null && !_refreshDoneSeen)
        {
            if (_c.NowMs - _refreshAtMs > ReplyWaitMs) w.TextColored(UiColors.Red, "No reply yet. Press Refresh to ask again.");
            else w.TextColored(UiColors.Yellow, _refreshWhat + " Waiting for the reply...");
        }
        else if (_refreshDone != null)
            w.TextDisabled($"Updated {_refreshDoneUtc.ToLocalTime():HH:mm:ss}. {_refreshWhat}");
        else
            w.TextDisabled($"Refreshed {_refreshAtUtc.ToLocalTime():HH:mm:ss}. {_refreshWhat}");
    }

    private void DoRefresh(TabDef def, string scope)
    {
        RefreshResult r;
        try { r = def.Refresh(); }
        catch (Exception ex) { r = new RefreshResult("Couldn't refresh: " + ex.Message); }
        _refreshScope = scope;
        _refreshWhat = r.What;
        _refreshDone = r.Done;
        _refreshDoneSeen = false;
        _refreshAtMs = _c.NowMs;
        _refreshAtUtc = _c.UtcNow;
    }

    // ── What each page's Refresh does ───────────────────────────────────────

    /// <summary>The same request twice within <see cref="CooldownMs"/> is refused.</summary>
    private bool CoolingDown(string key)
    {
        if (_refreshSent.TryGetValue(key, out long t) && _c.NowMs - t < CooldownMs) return true;
        _refreshSent[key] = _c.NowMs;
        return false;
    }

    private const string TooSoon = "That was just asked for; wait a few seconds before asking again.";

    /// <summary>Sends one chat command (the page's own) and waits for <paramref name="state"/> to change.</summary>
    private RefreshResult Ask(string key, Func<long> state, Func<string> command)
    {
        if (CoolingDown(key)) return new RefreshResult(TooSoon);
        long before = state();
        string cmd = command();
        if (!_c.Send(cmd)) return new RefreshResult($"Couldn't send {cmd} (not in the world).");
        return new RefreshResult($"Sent {cmd}.", () => state() != before);
    }

    /// <summary>/myquests (refused for 5 s after the last one); the line waits for the new flags.</summary>
    private RefreshResult RefreshQuests(string before = "")
    {
        QuestFlagStore f = _c.Flags;
        int rev = f.Revision;
        if (_c.RequestQuests()) return new RefreshResult(before + "Asked the server for your quest flags (/myquests).", () => f.Revision != rev);
        if (f.Loading) return new RefreshResult(before + "Your quest flags are already being read.", () => f.Revision != rev);
        return new RefreshResult(before + "Your quest flags were asked for a moment ago; wait a few seconds before asking again.");
    }

    private RefreshResult RefreshCharacter()
    {
        _c.RereadCharacter();
        return new RefreshResult("Re-read your level, experience, buffs and skills from the game.");
    }

    /// <summary>Pages built from the character's properties and the quest flags: both are read again.</summary>
    private RefreshResult RefreshCharacterAndQuests()
    {
        _c.RereadCharacter();
        return RefreshQuests("Re-read your character from the game. ");
    }

    private RefreshResult RefreshTitles()
    {
        _c.RereadTitles();
        return new RefreshResult(_c.TitlesKnown
            ? $"Re-read your titles from the game: {_c.EarnedTitles.Count} held."
            : "The game hasn't sent this character's titles yet (they arrive at login).");
    }

    private RefreshResult RefreshVoid()
    {
        _c.RereadCharacter();
        return new RefreshResult("Re-read your buffs and dropped the spell timers that ran out. Timers on targets are learned from your casts.");
    }

    /// <summary>Pet Breeding: RynthILT's list is read again every few seconds while the tab shows, so nothing to send.</summary>
    private RefreshResult RefreshBreeding() =>
        new("The breeding list is read from RynthILT every few seconds while this tab is open.");

    private RefreshResult RefreshFellowship()
    {
        _c.Fellowship.Invalidate();
        return new RefreshResult("Re-read the fellowship from the game.");
    }

    private RefreshResult RefreshAbout()
    {
        if (CoolingDown("about")) return new RefreshResult(TooSoon);
        _c.ReloadQuestList();
        QuestCatalog cat = _c.Catalog;
        string pack = cat.PackServer.Length > 0 ? $" + {cat.ServerCount} {cat.PackServer}" : "";
        return new RefreshResult($"Reloaded the quest list ({cat.RetailCount} retail{pack}) and asked RynthCore which server this is.");
    }

    private RefreshResult RefreshBoards()
    {
        if (_c.Server.Is(ServerTags.Conquest))
        {
            ConquestData cq = _c.Conquest;
            ConquestBoard b = cq.Boards[Math.Clamp(_conquestBoard, 0, cq.Boards.Count - 1)];
            return Ask("board" + b.Key, () => b.UpdatedUtc.Ticks, () => cq.RequestBoard(b, _c.NowMs));
        }
        AelrynthBoards a = _c.Boards;
        return _aelrynthView switch
        {
            1 => Ask("top me", () => a.Revision, () => a.RequestTop("me", _c.NowMs)),
            2 => Ask("trial", () => a.Revision, () => a.RequestTrial("", _c.NowMs)),
            _ => Ask("top " + _aelrynthBoardArg, () => a.Revision,
                     () => a.RequestTop(a.BoardTitle.Length > 0 ? _aelrynthBoardArg : "", _c.NowMs)),
        };
    }

    private string BoardsHelp()
    {
        if (_c.Server.Is(ServerTags.Conquest))
        {
            ConquestBoard b = _c.Conquest.Boards[Math.Clamp(_conquestBoard, 0, _c.Conquest.Boards.Count - 1)];
            return $"Sends {b.Command} and reads the reply.";
        }
        return _aelrynthView switch
        {
            1 => "Sends /top me and reads the reply.",
            2 => "Sends /trial and reads the dungeon list.",
            _ => "Sends /top (every board's leader), or asks again for the board that is open.",
        };
    }

    private RefreshResult RefreshConquest()
    {
        ConquestData cq = _c.Conquest;
        switch (_conquestView)
        {
            case 0: return Ask("bank", () => cq.BankUtc.Ticks, () => cq.RequestBank(_c.NowMs));
            case 1: return Ask("augs", () => cq.AugsUtc.Ticks, () => cq.RequestAugs(_c.NowMs));
            case 2: return Ask("enl", () => cq.EnlUtc.Ticks, () => cq.RequestEnl(_c.NowMs));
            case 3:
                _c.RereadCharacter();
                return Ask("bonus", () => cq.BonusUtc.Ticks, () => cq.RequestBonus(_c.NowMs));
            case 4: return Ask("fship", () => cq.FshipUtc.Ticks, () => cq.RequestFships(_c.NowMs));
            default: return RefreshQuests();
        }
    }

    private string ConquestHelp() => _conquestView switch
    {
        0 => "Sends /b and reads your balances.",
        1 => "Sends /augs and reads your advanced augmentations.",
        2 => "Sends /enl augs and reads your enlightenment augmentations.",
        3 => "Re-reads your level, and sends /bonus for the experience bonuses.",
        4 => "Sends /fship list and reads the fellowships looking for members.",
        _ => "Sends /myquests: the custom quests' status comes from your quest flags.",
    };
}
