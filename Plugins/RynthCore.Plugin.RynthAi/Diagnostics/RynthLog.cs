using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RynthCore.PluginSdk;
using RynthCore.Install;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// One trace category per RynthAi function. Each category can be traced
/// independently (<c>/ra trace &lt;cat&gt; on</c>) into its own daily file under
/// <c>Logs\Diagnostics\Trace</c>, mirroring UtilityBelt's per-feature trace toggles.
/// </summary>
internal enum LogCat
{
    // ── Core / macro systems ───────────────────────────────────────────────
    General,
    Commands,
    Combat,
    Buffing,
    Pets,
    WorldCache,
    Navigation,
    Doors,
    Jumper,
    Looting,
    Inventory,
    Salvage,
    Vendor,
    ManaStones,
    Meta,
    Expressions,
    Quests,
    Crafting,
    Raycast,
    Radar,
    UI,
    Remote,
    Chat,

    // ── ILT Hub (IltHub/) ──────────────────────────────────────────────────
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

/// <summary>
/// Persisted diagnostics switches (<c>Logs\Diagnostics\diagnostics.json</c>).
/// Fields (not properties) so the source-generated <see cref="RynthAiJsonContext"/>
/// (IncludeFields) round-trips them without reflection under NativeAOT.
/// </summary>
internal sealed class RynthLogConfig
{
    /// <summary>Echo trace + exception lines to the in-game chat window (UB's "debug" switch).</summary>
    public bool DebugToChat;

    /// <summary>Mirror every <see cref="RynthLog.Write"/> line into the daily RynthAi log file.</summary>
    public bool FileLogAll = true;

    /// <summary>Category names (see <see cref="LogCat"/>) with tracing enabled.</summary>
    public List<string> Traces = new();

    /// <summary>Delete diagnostics files older than this many days.</summary>
    public int RetainDays = 7;

    /// <summary>Roll a log file to a new numbered part once it exceeds this size (MB).</summary>
    public int MaxFileMb = 10;

    /// <summary>Keep at most this many files per diagnostics folder (oldest pruned first).</summary>
    public int MaxFiles = 30;
}

/// <summary>
/// Central RynthAi logging / debugging facility, modelled on UtilityBelt's Logger:
/// <list type="bullet">
///   <item><see cref="Write"/> — normal log line: engine log (as before) + daily <c>rynthai_*.txt</c>
///         + the category trace file when that category is being traced.</item>
///   <item><see cref="Trace"/> — verbose line, only recorded when the category is traced; optionally
///         echoed to chat when <see cref="DebugToChat"/> is on.</item>
///   <item><see cref="Exception"/> — full stack trace into <c>exceptions_*.txt</c> with per-signature
///         throttling so a per-frame fault can't fill the disk.</item>
/// </list>
/// Thread-safe: callable from the pump thread, the ImGui render thread and background tasks.
/// Chat echo is queued and drained on the pump thread by <see cref="Pump"/>.
/// </summary>
internal static class RynthLog
{
    /// <summary>Default diagnostics root (inside the RynthAi data folder the installer creates).</summary>
    public static readonly string DefaultDirectory = System.IO.Path.Combine(RynthInstallPaths.RynthAiDir, @"Logs\Diagnostics");

    // ── Exception throttling (same budgets as UB's Logger) ────────────────
    private static readonly TimeSpan ExceptionBurstWindow = TimeSpan.FromSeconds(5);
    private const int MaxExceptionsPerSignaturePerDay = 25;
    private const int MaxExceptionsPerDay = 500;

    /// <summary>Upper bound on chat echo lines drained per tick (prevents chat floods).</summary>
    private const int MaxChatLinesPerPump = 20;

    /// <summary>Upper bound on queued chat echo lines; extra lines are dropped (files still get them).</summary>
    private const int MaxQueuedChatLines = 200;

    private static readonly object FileLock = new();
    private static readonly object ConfigLock = new();
    private static readonly Dictionary<string, StreamWriter> Writers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> RollParts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (DateTime lastAt, int count)> ExceptionSignatures = new();
    private static readonly ConcurrentQueue<string> ChatQueue = new();

    // Indexed by (int)LogCat; volatile reads are enough — toggles are rare, reads are hot.
    private static readonly bool[] Tracing = new bool[Enum.GetValues<LogCat>().Length];

    private static RynthCoreHost? _host;
    private static RynthLogConfig _config = new();
    private static string _directory = DefaultDirectory;
    private static string _openDay = string.Empty;
    private static int _exceptionsToday;
    private static bool _handlersRegistered;
    private static bool _dirty;

    /// <summary>Folder holding <c>rynthai_*.txt</c>, <c>exceptions_*.txt</c> and <c>diagnostics.json</c>.</summary>
    public static string Directory => _directory;

    /// <summary>Folder holding per-category <c>{Category}_*.txt</c> trace files.</summary>
    public static string TraceDirectory => Path.Combine(_directory, "Trace");

    /// <summary>When true, trace and exception lines are echoed to chat (UB's global debug).</summary>
    public static bool DebugToChat
    {
        get => _config.DebugToChat;
        set { _config.DebugToChat = value; SaveConfig(); }
    }

    /// <summary>When true, every <see cref="Write"/> line is mirrored to the daily RynthAi log file.</summary>
    public static bool FileLogAll
    {
        get => _config.FileLogAll;
        set { _config.FileLogAll = value; SaveConfig(); }
    }

    /// <summary>
    /// Binds the logger to the plugin host, loads persisted switches, prunes old files and
    /// installs the global unhandled-exception hooks. Safe to call more than once.
    /// </summary>
    public static void Init(RynthCoreHost host, string? directory = null)
    {
        _host = host;
        _directory = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory;

        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            System.IO.Directory.CreateDirectory(TraceDirectory);
        }
        catch { /* folder is created lazily on first write as a fallback */ }

        LoadConfig();
        Prune();
        RegisterGlobalHandlers();
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Public logging API
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Normal log line. Always goes to the engine log (unchanged behaviour), plus the daily
    /// RynthAi file and — if the category is traced — the category's trace file.
    /// <see cref="LogCat.General"/> lines are re-routed by their "[Prefix]" when one is recognised.
    /// </summary>
    public static void Write(LogCat cat, string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        if (cat == LogCat.General) cat = CategoryFromPrefix(message);

        try { _host?.Log(message); } catch { /* host gone during unload */ }

        if (_config.FileLogAll)
            AppendLine(DailyPath(_directory, "rynthai"), $"[{cat}] {message}");

        if (IsTracing(cat))
            AppendLine(DailyPath(TraceDirectory, cat.ToString()), "INFO  " + message);
    }

    /// <summary>
    /// Verbose diagnostic line. Recorded only when <paramref name="cat"/> is traced, so call
    /// sites can trace freely; guard expensive message building with <see cref="IsTracing"/>.
    /// </summary>
    public static void Trace(LogCat cat, string message)
    {
        if (!IsTracing(cat) || string.IsNullOrEmpty(message)) return;

        AppendLine(DailyPath(TraceDirectory, cat.ToString()), "TRACE " + message);
        if (_config.DebugToChat) QueueChat($"[RynthAi:{cat}] {message}");
    }

    /// <summary>
    /// Records an exception with its full stack trace. A one-line summary always goes to the
    /// engine log; the detailed entry is throttled per signature (5 s burst window,
    /// 25/signature/day, 500/day total) exactly like UB's Logger.LogException.
    /// </summary>
    public static void Exception(LogCat cat, Exception ex, string context = "")
    {
        if (ex == null) return;

        string where = string.IsNullOrEmpty(context) ? cat.ToString() : $"{cat}/{context}";
        string summary = $"[RynthAi] {where} error: {ex.GetType().Name}: {ex.Message}";
        try { _host?.Log(summary); } catch { }

        if (!ShouldRecordException(where, ex, out int repeat)) return;

        var sb = new StringBuilder();
        sb.Append("==== ").Append(where);
        if (repeat > 1) sb.Append(" (occurrence ").Append(repeat).Append(" today)");
        sb.AppendLine(" ====");
        sb.Append(ex);
        AppendLine(DailyPath(_directory, "exceptions"), sb.ToString(), flush: true);

        if (IsTracing(cat))
            AppendLine(DailyPath(TraceDirectory, cat.ToString()), "ERROR " + summary);
        if (_config.DebugToChat) QueueChat(summary);
    }

    /// <summary>True when the category is currently being traced (cheap; safe on hot paths).</summary>
    public static bool IsTracing(LogCat cat)
    {
        int i = (int)cat;
        return (uint)i < (uint)Tracing.Length && System.Threading.Volatile.Read(ref Tracing[i]);
    }

    /// <summary>Enables/disables tracing for a single category and persists the change.</summary>
    public static void SetTracing(LogCat cat, bool enabled)
    {
        int i = (int)cat;
        if ((uint)i >= (uint)Tracing.Length) return;
        System.Threading.Volatile.Write(ref Tracing[i], enabled);
        SaveConfig();
    }

    /// <summary>Enables/disables tracing for every category whose name starts with <paramref name="prefix"/> (empty = all).</summary>
    public static int SetTracingByPrefix(string prefix, bool enabled)
    {
        int changed = 0;
        foreach (LogCat cat in Enum.GetValues<LogCat>())
        {
            if (prefix.Length > 0 && !cat.ToString().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            System.Threading.Volatile.Write(ref Tracing[(int)cat], enabled);
            changed++;
        }
        SaveConfig();
        return changed;
    }

    /// <summary>Parses a category name case-insensitively (e.g. "ilthub", "Combat").</summary>
    public static bool TryParseCategory(string name, out LogCat cat)
        => Enum.TryParse(name, ignoreCase: true, out cat) && Enum.IsDefined(cat);

    /// <summary>All categories currently being traced.</summary>
    public static List<LogCat> TracedCategories()
    {
        var list = new List<LogCat>();
        foreach (LogCat cat in Enum.GetValues<LogCat>())
            if (IsTracing(cat)) list.Add(cat);
        return list;
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Lifecycle
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Pump-thread housekeeping, called once per OnTick: drains queued chat echo lines and
    /// flushes buffered file writes so a crash loses at most one tick of log output.
    /// </summary>
    public static void Pump()
    {
        // RynthCoreHost is a struct; unwrap the nullable copy before calling into it.
        if (!ChatQueue.IsEmpty && _host is RynthCoreHost host)
        {
            for (int n = 0; n < MaxChatLinesPerPump && ChatQueue.TryDequeue(out string? line); n++)
            {
                try { if (host.HasWriteToChat) host.WriteToChat(line, 1); } catch { }
            }
        }

        if (_dirty) Flush();
    }

    /// <summary>Flushes every open log writer to disk.</summary>
    public static void Flush()
    {
        lock (FileLock)
        {
            foreach (var w in Writers.Values)
            {
                try { w.Flush(); } catch { }
            }
            _dirty = false;
        }
    }

    /// <summary>Flushes and closes every log file (plugin unload / logout).</summary>
    public static void Shutdown()
    {
        lock (FileLock)
        {
            foreach (var w in Writers.Values)
            {
                try { w.Flush(); w.Dispose(); } catch { }
            }
            Writers.Clear();
            _dirty = false;
        }
    }

    /// <summary>
    /// Deletes diagnostics files older than <see cref="RynthLogConfig.RetainDays"/> and trims each
    /// folder to <see cref="RynthLogConfig.MaxFiles"/>. Only touches <c>*.txt</c> files under the
    /// diagnostics folders, never other RynthAi data.
    /// </summary>
    public static int Prune()
    {
        int removed = 0;
        lock (FileLock)
        {
            removed += PruneFolder(_directory);
            removed += PruneFolder(TraceDirectory);
        }
        return removed;
    }

    /// <summary>Human-readable status lines for <c>/ra trace status</c> and <c>/ra logs</c>.</summary>
    public static IEnumerable<string> StatusLines()
    {
        var traced = TracedCategories();
        yield return $"[RynthAi] Debug-to-chat: {(DebugToChat ? "ON" : "off")}   File log: {(FileLogAll ? "ON" : "off")}";
        yield return traced.Count == 0
            ? "[RynthAi] Tracing: (none)"
            : "[RynthAi] Tracing: " + string.Join(", ", traced);
        yield return $"[RynthAi] Logs: {_directory}  (retain {_config.RetainDays} d, roll at {_config.MaxFileMb} MB)";
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Internals
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Maps the "[Prefix]" convention used by existing plugin log lines onto a category so
    /// that, e.g., "[BuffDiag] …" lands in the Buffing trace without touching the call site.
    /// </summary>
    private static LogCat CategoryFromPrefix(string message)
    {
        if (message.Length < 3 || message[0] != '[') return LogCat.General;

        if (Starts(message, "[Buff")) return LogCat.Buffing;
        if (Starts(message, "[MetaCast") || Starts(message, "[Meta")) return LogCat.Meta;
        if (Starts(message, "[Combat") || Starts(message, "[WieldGate") || Starts(message, "[Hazard")
            || Starts(message, "[Kill") || Starts(message, "[ScanTele") || Starts(message, "[RynthAi combat"))
            return LogCat.Combat;
        if (Starts(message, "[Equip") || Starts(message, "[RynthAi dumpinv")) return LogCat.Inventory;
        if (Starts(message, "[LOS") || Starts(message, "[Raycast") || Starts(message, "[GeoLoader")) return LogCat.Raycast;
        if (Starts(message, "[Jumper")) return LogCat.Jumper;
        if (Starts(message, "[ChatDiag") || Starts(message, "[RynthChat")) return LogCat.Chat;
        if (Starts(message, "[Reclassify") || Starts(message, "[Classify")) return LogCat.WorldCache;
        if (Starts(message, "[RynthAi navstate") || Starts(message, "[Nav")) return LogCat.Navigation;
        if (Starts(message, "[IltHub")) return LogCat.IltHub;
        return LogCat.General;
    }

    private static bool Starts(string s, string prefix) => s.StartsWith(prefix, StringComparison.Ordinal);

    /// <summary>Builds <c>{folder}\{stem}_yyyy-MM-dd.txt</c>, with a numbered suffix once the day's file has rolled.</summary>
    private static string DailyPath(string folder, string stem)
    {
        string key = folder + "|" + stem;
        string day = DateTime.Now.ToString("yyyy-MM-dd");
        int part;
        lock (FileLock) part = RollParts.TryGetValue(key + "|" + day, out int p) ? p : 0;
        string name = part == 0 ? $"{stem}_{day}.txt" : $"{stem}_{day}_{part}.txt";
        return Path.Combine(folder, name);
    }

    /// <summary>Appends a timestamped line, rolling the file when it passes the size cap.</summary>
    private static void AppendLine(string path, string text, bool flush = false)
    {
        try
        {
            lock (FileLock)
            {
                RollDayIfNeeded();

                if (!Writers.TryGetValue(path, out var writer))
                {
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
                    Writers[path] = writer;
                }

                writer.Write(DateTime.Now.ToString("HH:mm:ss.fff"));
                writer.Write(' ');
                writer.WriteLine(text);
                _dirty = true;
                if (flush) writer.Flush();

                long cap = Math.Max(1, _config.MaxFileMb) * 1024L * 1024L;
                if (writer.BaseStream.Length > cap) Roll(path, writer);
            }
        }
        catch
        {
            // Logging must never throw into gameplay code (disk full, file locked, etc.).
        }
    }

    /// <summary>Closes an oversized file and bumps its part number so the next write opens a fresh file.</summary>
    private static void Roll(string path, StreamWriter writer)
    {
        try { writer.Flush(); writer.Dispose(); } catch { }
        Writers.Remove(path);

        string file = Path.GetFileNameWithoutExtension(path);
        string day = DateTime.Now.ToString("yyyy-MM-dd");
        int idx = file.IndexOf("_" + day, StringComparison.Ordinal);
        string stem = idx > 0 ? file[..idx] : file;
        string key = Path.GetDirectoryName(path) + "|" + stem + "|" + day;
        RollParts[key] = (RollParts.TryGetValue(key, out int p) ? p : 0) + 1;
    }

    /// <summary>Closes yesterday's writers at midnight so new lines go to the new day's files.</summary>
    private static void RollDayIfNeeded()
    {
        string day = DateTime.Now.ToString("yyyy-MM-dd");
        if (day == _openDay) return;

        foreach (var w in Writers.Values)
        {
            try { w.Flush(); w.Dispose(); } catch { }
        }
        Writers.Clear();
        RollParts.Clear();
        ExceptionSignatures.Clear();
        _exceptionsToday = 0;
        _openDay = day;
    }

    /// <summary>Per-signature exception throttle (UB budgets). Returns false when the entry should be skipped.</summary>
    private static bool ShouldRecordException(string where, Exception ex, out int occurrence)
    {
        occurrence = 0;
        lock (FileLock)
        {
            RollDayIfNeeded();
            if (_exceptionsToday >= MaxExceptionsPerDay) return false;

            string topFrame = ex.StackTrace?.Split('\n')[0].Trim() ?? string.Empty;
            string signature = $"{where}|{ex.GetType().FullName}|{topFrame}";
            DateTime now = DateTime.Now;

            if (ExceptionSignatures.TryGetValue(signature, out var seen))
            {
                if (seen.count >= MaxExceptionsPerSignaturePerDay) return false;
                // Repeats inside the burst window are dropped: a per-frame fault yields one entry per window.
                if (now - seen.lastAt < ExceptionBurstWindow) return false;
                occurrence = seen.count + 1;
            }
            else
            {
                occurrence = 1;
            }

            ExceptionSignatures[signature] = (now, occurrence);
            _exceptionsToday++;
            return true;
        }
    }

    /// <summary>Hooks AppDomain / TaskScheduler so faults outside our try/catch blocks are still recorded.</summary>
    private static void RegisterGlobalHandlers()
    {
        if (_handlersRegistered) return;
        _handlersRegistered = true;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Exception(LogCat.General, ex, "UnhandledException");
            Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Exception(LogCat.General, e.Exception, "UnobservedTaskException");
            e.SetObserved();
        };
    }

    /// <summary>Queues a chat echo line for the pump thread (WriteToChat is not render-thread safe).</summary>
    private static void QueueChat(string line)
    {
        if (ChatQueue.Count < MaxQueuedChatLines) ChatQueue.Enqueue(line);
    }

    /// <summary>Removes old/excess <c>*.txt</c> files from one diagnostics folder. Caller holds <see cref="FileLock"/>.</summary>
    private static int PruneFolder(string folder)
    {
        int removed = 0;
        try
        {
            if (!System.IO.Directory.Exists(folder)) return 0;

            var files = new List<FileInfo>(new DirectoryInfo(folder).GetFiles("*.txt"));
            files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc)); // newest first
            DateTime cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, _config.RetainDays));

            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i];
                bool tooOld = f.LastWriteTimeUtc < cutoff;
                bool overCount = i >= Math.Max(5, _config.MaxFiles);
                if (!tooOld && !overCount) continue;
                if (Writers.ContainsKey(f.FullName)) continue; // never delete a file we're writing

                try { f.Delete(); removed++; } catch { }
            }
        }
        catch { }
        return removed;
    }

    private static string ConfigPath => Path.Combine(_directory, "diagnostics.json");

    /// <summary>Loads persisted switches; missing or corrupt files fall back to defaults.</summary>
    private static void LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                _config = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.RynthLogConfig) ?? new RynthLogConfig();
            }
        }
        catch
        {
            _config = new RynthLogConfig();
        }

        _config.Traces ??= new List<string>();
        for (int i = 0; i < Tracing.Length; i++) Tracing[i] = false;
        foreach (string name in _config.Traces)
            if (TryParseCategory(name, out LogCat cat)) Tracing[(int)cat] = true;
    }

    /// <summary>Persists the current switches (trace set is rebuilt from the live flags).</summary>
    private static void SaveConfig()
    {
        // Toggles come from both the pump thread (/ra trace) and the render thread (Diagnostics tab).
        lock (ConfigLock)
        {
            try
            {
                _config.Traces = TracedCategories().ConvertAll(c => c.ToString());
                System.IO.Directory.CreateDirectory(_directory);
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_config, RynthAiJsonContext.Default.RynthLogConfig));
            }
            catch { }
        }
    }
}
