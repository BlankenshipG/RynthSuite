// IltLog.cs - RynthILT's diagnostics log: the ILT features' trace categories and key events,
// written to %APPDATA%\RynthCore\RynthILT\Logs\RynthILT_yyyy-MM-dd.txt.
//
// The ILT Hub logged through RynthAi's RynthLog (LogCat.Ilt*, LogEvents.Ilt*) while it lived in
// RynthAi; the moved code keeps the same calls (RynthLog.Write / Trace / Exception / Event), so
// this is a small RynthLog of RynthILT's own:
//   Write      file (always) and the engine log
//   Trace      file, only while that category is traced ("/ilt trace <cat|all> on|off")
//   Exception  file and the engine log, always
//   Event      as Write for the key events (login, options refreshed, ...); trace-only events
//              as Trace
// "/ilt debug on" echoes trace and exception lines to chat (local only). Lines are queued on any
// thread and written by Pump() on the plugin pump.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthILT;

/// <summary>One trace category per ILT feature (the names RynthAi's ILT Hub used).</summary>
internal enum LogCat
{
    IltHub,
    IltOptions,
    IltChat,
    IltStore,
    IltBanking,
    IltPets,
    IltQuests,
    IltProgression,
    IltRates,
    IltGear,
    IltGames,
}

/// <summary>Named key events (the ILT Hub's catalogue in RynthAi's RynthLog).</summary>
internal static class LogEvents
{
    public const string IltLogin = "IltHub.Login";
    public const string IltWorldCheck = "IltHub.WorldCheck";
    public const string IltOptionsRefreshStart = "IltHub.OptionsRefreshStart";
    public const string IltOptionsRefreshed = "IltHub.OptionsRefreshed";
    public const string IltWindowShown = "IltHub.WindowShown";
    public const string IltWindowHidden = "IltHub.WindowHidden";
    public const string IltCommand = "IltHub.Command";
    public const string IltConfirmation = "IltHub.Confirmation";
    public const string IltLogout = "IltHub.Logout";
    /// <summary>/aug ApplyAugLines outcome (Progress Refresh current / login probe).</summary>
    public const string IltAugLoad = "IltHub.AugLoad";

    /// <summary>Events written as a normal log line; the rest only while their category is traced.</summary>
    internal static readonly Dictionary<string, (LogCat Cat, bool Info)> Catalog = new(StringComparer.Ordinal)
    {
        [IltLogin] = (LogCat.IltHub, true),
        [IltWorldCheck] = (LogCat.IltOptions, true),
        [IltOptionsRefreshStart] = (LogCat.IltOptions, false),
        [IltOptionsRefreshed] = (LogCat.IltOptions, true),
        [IltWindowShown] = (LogCat.IltHub, true),
        [IltWindowHidden] = (LogCat.IltHub, false),
        [IltCommand] = (LogCat.IltHub, false),
        [IltConfirmation] = (LogCat.IltHub, false),
        [IltLogout] = (LogCat.IltHub, false),
        [IltAugLoad] = (LogCat.IltProgression, true),
    };
}

internal static class RynthLog
{
    private static readonly ConcurrentQueue<string> Pending = new();
    private static readonly ConcurrentQueue<string> PendingChat = new();
    private static readonly bool[] Traced = new bool[Enum.GetValues<LogCat>().Length];
    private static RynthCoreHost _host;
    private static bool _hasHost;
    private static string _directory = string.Empty;

    /// <summary>Echo trace and exception lines to the chat window ("/ilt debug on").</summary>
    public static volatile bool DebugToChat;

    /// <summary>Where the daily files go (tests point it at a scratch folder).</summary>
    public static string Directory => _directory;

    public static void Init(RynthCoreHost host, string? directory = null)
    {
        _host = host;
        _hasHost = true;
        _directory = directory ?? Path.Combine(RynthILTPlugin.SettingsRoot, "Logs");
    }

    /// <summary>Drops the host (plugin shutdown): nothing is written to it afterwards.</summary>
    public static void Shutdown()
    {
        Pump();
        _hasHost = false;
    }

    public static void Write(LogCat cat, string message)
    {
        Enqueue(cat, message);
        HostLog(message);
    }

    public static void Trace(LogCat cat, string message)
    {
        if (!IsTracing(cat)) return;
        Enqueue(cat, message);
        if (DebugToChat) Chat($"[RynthILT {cat}] {message}");
    }

    public static void Exception(LogCat cat, Exception ex, string context = "")
    {
        string line = $"EXCEPTION {(context.Length > 0 ? context + ": " : "")}{ex.GetType().Name}: {ex.Message}";
        Enqueue(cat, line + Environment.NewLine + ex.StackTrace);
        HostLog(line);
        if (DebugToChat) Chat($"[RynthILT {cat}] {line}");
    }

    public static void Event(string eventKey, string message)
    {
        if (!LogEvents.Catalog.TryGetValue(eventKey, out var e)) { Write(LogCat.IltHub, $"[{eventKey}] {message}"); return; }
        if (e.Info) Write(e.Cat, $"[{eventKey}] {message}");
        else Trace(e.Cat, $"[{eventKey}] {message}");
    }

    /// <summary>True when <see cref="Event"/> would record the event (an Info event, or a traced Trace one).</summary>
    public static bool IsEventEnabled(string eventKey)
        => !LogEvents.Catalog.TryGetValue(eventKey, out var e) || e.Info || IsTracing(e.Cat);

    public static bool IsTracing(LogCat cat) => (int)cat >= 0 && (int)cat < Traced.Length && Traced[(int)cat];

    public static void SetTracing(LogCat cat, bool on)
    {
        if ((int)cat >= 0 && (int)cat < Traced.Length) Traced[(int)cat] = on;
    }

    /// <summary>Every category on or off; returns how many.</summary>
    public static int SetTracingAll(bool on)
    {
        for (int i = 0; i < Traced.Length; i++) Traced[i] = on;
        return Traced.Length;
    }

    public static bool TryParseCategory(string name, out LogCat cat)
    {
        if (Enum.TryParse(name, ignoreCase: true, out cat)) return true;
        return Enum.TryParse("Ilt" + name, ignoreCase: true, out cat);
    }

    public static string TracedList()
    {
        var on = new List<string>();
        foreach (LogCat c in Enum.GetValues<LogCat>()) if (IsTracing(c)) on.Add(c.ToString());
        return on.Count == 0 ? "none" : string.Join(", ", on);
    }

    /// <summary>Writes the queued lines (plugin pump).</summary>
    public static void Pump()
    {
        int lines = 0;
        while (lines++ < 20 && PendingChat.TryDequeue(out string? chat))
        {
            if (!_hasHost) continue;
            try { if (_host.HasWriteToChat) _host.WriteToChat(chat, 1); } catch { }
        }
        if (Pending.IsEmpty || _directory.Length == 0) return;
        var sb = new StringBuilder();
        int n = 0;
        while (n++ < 2000 && Pending.TryDequeue(out string? line)) sb.Append(line).Append(Environment.NewLine);
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            File.AppendAllText(Path.Combine(_directory, $"RynthILT_{DateTime.Now:yyyy-MM-dd}.txt"), sb.ToString());
        }
        catch { /* a log that can't be written must never break a feature */ }
    }

    private static void Enqueue(LogCat cat, string message)
    {
        if (Pending.Count > 20000) return;   // nothing is draining (no pump): don't grow forever
        Pending.Enqueue($"{DateTime.Now:HH:mm:ss.fff} [{cat}] {message}");
    }

    private static void HostLog(string message)
    {
        if (!_hasHost) return;
        try { _host.Log("[RynthILT] " + message); } catch { }
    }

    /// <summary>Queued for the pump (trace lines can come from the render thread).</summary>
    private static void Chat(string text)
    {
        if (PendingChat.Count < 200) PendingChat.Enqueue(text);
    }
}
