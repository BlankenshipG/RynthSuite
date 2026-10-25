// RynthThreadsPlugin.cs - DreamWeave's "Threads" gem crafting: records every Fragment Press result
// from chat and shows the odds of each modifier with each component (DreamWeave components give a
// chance of each modifier, not a fixed one).
//
// Active only on DreamWeave (the world name contains "DreamWeave"); anywhere else it reads nothing,
// shows no window and only answers /rthreads to say so. Presses go to one file shared by every
// character and client on the PC (Threads/PressStore.cs); the window shows what the inference
// (Threads/PressInference.cs) makes of all of them.
//
// Threads: OnTick, OnChatWindowText and login/logout run on the plugin pump. OnChatBarEnter may run
// on AC's main thread, so it only queues the command.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using RynthCore.Plugin.RynthThreads.Threads;
using RynthCore.Plugin.RynthThreads.Ui;
using RynthCore.Plugin.RynthThreads.Views;
using RynthCore.PluginCore;

namespace RynthCore.Plugin.RynthThreads;

public sealed class RynthThreadsPlugin : RynthPluginBase
{
    public const string Version = "0.1.0";
    private const string Command = "/rthreads";
    private const long IdentityRetryMs = 1_000;
    /// <summary>Wait this long after login for the character's name before recording without it.</summary>
    private const long NameWaitMs = 30_000;
    /// <summary>How often the shared file is checked for other clients' presses.</summary>
    private const long PollMs = 2_000;
    private const long PassIntervalMs = 5_000;

    private readonly PressParser _parser = new();
    private readonly UiHost _ui = new();
    private readonly ConcurrentQueue<string> _commands = new();
    private PressStore? _store;
    private UiWindow? _window;
    private ThreadsPanel? _panel;
    private ThreadsReport? _report;
    private int _reportVersion = -1;

    private bool _inWorld;
    private bool _checked;   // the world is known (DreamWeave or not)
    private bool _active;    // on DreamWeave: reading chat, showing the window
    private long _loginAtMs;
    private long _nextIdentityTryMs;
    private long _nextPollMs;
    private string _world = string.Empty;
    private string _character = string.Empty;
    private bool _noUiLogged;

    public override int Initialize()
    {
        _ui.Log = msg => Host.Log(msg);
        _panel = new ThreadsPanel
        {
            Report = CurrentReport,
            Presses = () => _store?.Presses ?? (IReadOnlyList<Press>)Array.Empty<Press>(),
            Export = Export,
            Notice = Notice,
        };
        _window = _ui.Add(new UiWindow("RynthThreads/Main", "RynthThreads")
        {
            DefaultWidth = 440f, DefaultHeight = 480f, MinWidth = 320f, MinHeight = 220f,
        });
        Host.Log($"[RynthThreads] {Version} initialized (DreamWeave only). {Command} shows or hides the window; {Command} stats|export.");
        return 0;
    }

    /// <summary>True for DreamWeave's world name (any case, any decoration around it).</summary>
    internal static bool IsDreamWeave(string? world) =>
        !string.IsNullOrEmpty(world) && world.IndexOf("DreamWeave", StringComparison.OrdinalIgnoreCase) >= 0;

    // The engine calls this on a real login and on a freshly (re)loaded plugin already in game.
    public override void OnLoginComplete()
    {
        _inWorld = true;
        _checked = false;
        _active = false;
        _character = string.Empty;
        _loginAtMs = Environment.TickCount64;
        _nextIdentityTryMs = _loginAtMs;
    }

    public override void OnLogout()
    {
        SaveFinished(_parser.Flush("logout"));
        _inWorld = false;
        _checked = false;
        _active = false;
        if (_window != null) { _window.Visible = false; _window.NeedsPass = true; }
        _ui.SubmitEmpty(Host);
    }

    // Plugin unload (hot reload, update, client closing): keep a half-read press.
    public override void Shutdown()
    {
        SaveFinished(_parser.Flush("logout"));
        _store?.FlushPending();
        _ui.SubmitEmpty(Host);
    }

    public override void OnChatWindowText(string? text, int chatType, ref int eat)
    {
        if (!_active || string.IsNullOrEmpty(text)) return;
        List<Press>? done = _parser.Feed(text, Environment.TickCount64);
        if (done == null) return;
        foreach (Press p in done) SaveFinished(p);
    }

    // May run on AC's thread: classify and queue only.
    public override void OnChatBarEnter(string? text, ref int eat)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        string t = text.Trim();
        if (!t.StartsWith(Command, StringComparison.OrdinalIgnoreCase)
            || (t.Length > Command.Length && !char.IsWhiteSpace(t[Command.Length])))
            return;
        eat = 1;
        _commands.Enqueue(t.Substring(Command.Length).Trim());
    }

    public override void OnTick()
    {
        long now = Environment.TickCount64;
        while (_commands.TryDequeue(out string? cmd)) RunCommand(cmd);
        if (_inWorld && !_checked && now >= _nextIdentityTryMs) TryIdentify(now);
        if (!_active || _store == null) return;

        SaveFinished(_parser.Tick(now));
        if (now >= _nextPollMs)
        {
            _nextPollMs = now + PollMs;
            _store.FlushPending();
            if (_store.Poll() > 0 && _window != null) _window.NeedsPass = true;
        }

        if (!Host.HasUi || _window == null || _panel == null) return;
        _ui.Poll(Host);
        Record(_window, now);
        _ui.Submit(Host, now);
    }

    /// <summary>Learns the world (and the character's name) after login; starts on DreamWeave.</summary>
    private void TryIdentify(long now)
    {
        _nextIdentityTryMs = now + IdentityRetryMs;
        if (!Host.HasGetWorldName || !Host.TryGetWorldName(out string world) || string.IsNullOrWhiteSpace(world)) return;

        uint me = Host.GetPlayerId();
        string name = string.Empty;
        bool named = me != 0 && Host.HasGetObjectName && Host.TryGetObjectName(me, out name) && !string.IsNullOrWhiteSpace(name);
        if (!named && now - _loginAtMs < NameWaitMs && IsDreamWeave(world)) return;

        _world = world.Trim();
        _character = named ? name.Trim() : string.Empty;
        _checked = true;
        _active = IsDreamWeave(_world);
        if (!_active)
        {
            Host.Log($"[RynthThreads] {_world} isn't DreamWeave: idle.");
            return;
        }
        _store ??= new PressStore(PressStore.DefaultFolder);
        _store.Poll();
        _nextPollMs = now + PollMs;
        if (_window != null) _window.NeedsPass = true;
        Host.Log($"[RynthThreads] recording Fragment Press results for {(_character.Length > 0 ? _character : "this character")} on {_world}; "
                 + $"{_store.Presses.Count} presses so far in {_store.FilePath}");
    }

    // ── Presses ─────────────────────────────────────────────────────────────

    private void SaveFinished(Press? p)
    {
        if (p == null || _store == null) return;
        p.Server = _world;
        p.Character = _character;
        string? error = _store.Record(p);
        if (_window != null) _window.NeedsPass = true;

        string level = p.FragmentLevel?.ToString(CultureInfo.InvariantCulture) ?? "?";
        string what = p.Complete
            ? $"{p.Modifiers.Count} modifier{(p.Modifiers.Count == 1 ? "" : "s")}"
            : "incomplete";
        Print($"Saved press {_store.Presses.Count}: {p.Fragment} (Level {level}), {p.ComponentsText} - {what}.");
        if (error != null)
        {
            Host.Log($"[RynthThreads] couldn't write {_store.FilePath} yet: {error}");
            Print("The press file is busy; it will be written in a moment.");
        }
    }

    private ThreadsReport CurrentReport()
    {
        if (_store == null) return new ThreadsReport();
        if (_report == null || _reportVersion != _store.Version)
        {
            _report = PressInference.Build(_store.Presses);
            _reportVersion = _store.Version;
        }
        return _report;
    }

    private string Notice()
    {
        if (_store == null) return string.Empty;
        var parts = new List<string>();
        if (_store.PendingCount > 0) parts.Add($"{_store.PendingCount} press(es) waiting to be written");
        if (_store.BadLines > 0) parts.Add($"{_store.BadLines} unreadable line(s) in the file skipped");
        return string.Join("; ", parts);
    }

    private void Export()
    {
        if (_store == null) return;
        try
        {
            string path = _store.ExportCsv(DateTime.Now);
            Print($"Exported {_store.Presses.Count} presses to {path}");
        }
        catch (Exception ex)
        {
            Print($"Export failed: {ex.Message}");
        }
    }

    // ── Commands ────────────────────────────────────────────────────────────

    private void RunCommand(string arg)
    {
        string word = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } w ? w[0].ToLowerInvariant() : string.Empty;
        if (word is "help" or "?")
        {
            Print($"DreamWeave only: records Fragment Press results and shows the odds of each modifier per component. {Command} shows or hides the window; {Command} stats; {Command} export.");
            return;
        }
        if (!_inWorld) { Print("Log in first."); return; }
        if (!_checked) { Print("Still finding out which server this is; try again in a moment."); return; }
        if (!_active)
        {
            Print($"RynthThreads is for DreamWeave only (this is {_world}).");
            return;
        }
        switch (word)
        {
            case "":
                if (_window == null) return;
                _window.Visible = !_window.Visible;
                _window.NeedsPass = true;
                if (_window.Visible && !Host.HasUi && !_noUiLogged)
                {
                    _noUiLogged = true;
                    Print($"This RynthCore engine can't show plugin windows (it needs script windows, API v71, with ImGui on). Presses are still recorded: {Command} stats.");
                }
                break;
            case "stats":
                foreach (string line in StatsLines(CurrentReport(), _store!.FilePath)) Print(line);
                break;
            case "export":
                Export();
                break;
            default:
                Print($"{Command} [stats | export | help]");
                break;
        }
    }

    /// <summary>
    /// The short /rthreads stats summary: totals, then one line per component (at most 6) and No
    /// components with its top 5 modifiers by rate ("Guarded 75%", "+" when more likely with it).
    /// The Affinity group is left out: every gem has one.
    /// </summary>
    internal static List<string> StatsLines(ThreadsReport r, string file)
    {
        var lines = new List<string>
        {
            $"{r.Total} press{(r.Total == 1 ? "" : "es")} ({r.Usable} complete, {r.Incomplete} incomplete) by {r.Characters} character{(r.Characters == 1 ? "" : "s")}; {r.Modifiers.Count} modifiers seen.",
        };
        var groups = new List<GroupReport>(r.Components);
        if (r.NoComponents != null) groups.Add(r.NoComponents);
        int shown = 0;
        foreach (GroupReport g in groups)
        {
            if (shown++ == 6) { lines.Add($"... and {groups.Count - 6} more (see the window)."); break; }
            var sb = new StringBuilder();
            sb.Append(g.Name).Append(" (").Append(g.Presses).Append("): ");
            int n = 0;
            foreach (Odds o in g.Odds)
            {
                if (o.IsGroup) continue;
                if (n == 5) break;
                if (n++ > 0) sb.Append(", ");
                sb.Append(o.Modifier).Append(' ')
                  .Append(Math.Round(o.Rate * 100).ToString("0", CultureInfo.InvariantCulture)).Append('%');
                if (o.MoreLikely) sb.Append('+');
            }
            lines.Add(sb.ToString());
        }
        if (groups.Any(g => g.Odds.Any(o => o.MoreLikely))) lines.Add("+ = more likely with it than without.");
        if (r.Total == 0) lines.Add("Press a Raw Fragment at a Fragment Press to start.");
        lines.Add("File: " + file);
        return lines;
    }

    private void Print(string text)
    {
        if (!Host.HasWriteToChat || !Host.WriteToChat("[RynthThreads] " + text, 1))
            Host.Log("[RynthThreads] " + text);
    }

    /// <summary>A pass when one is wanted, or now and then while visible.</summary>
    private void Record(UiWindow w, long now)
    {
        bool due = w.NeedsPass || (w.Visible && now >= w.NextPassMs);
        if (!due) return;
        w.NeedsPass = false;
        w.NextPassMs = now + PassIntervalMs;
        w.Begin();
        try
        {
            _panel!.Draw(w);
        }
        catch (Exception ex)
        {
            w.TextWrapped($"Something went wrong drawing this window: {ex.GetType().Name}: {ex.Message}");
            Host.Log($"[RynthThreads] {w.Key} pass failed: {ex}");
        }
        w.End();
    }
}
