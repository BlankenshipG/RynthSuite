using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.Meta;

internal sealed class QuestRecord
{
    public string Key = "";
    /// <summary>Server-supplied description from /myquests (may be empty on older servers).</summary>
    public string Description = "";
    public int Solves;
    public int MaxSolves;
    public DateTime CompletedOn = DateTime.MinValue;
    public TimeSpan RepeatTime = TimeSpan.Zero;

    /// <summary>
    /// Returns true if the quest can be done again now.
    /// Mirrors UB QuestFlag.IsReady(): timer expired AND not a once-only flag.
    /// Once-only: MaxSolves==1 &amp;&amp; Solves&lt;=1 — never ready again.
    /// </summary>
    public bool IsReady()
    {
        if ((CompletedOn + RepeatTime) > DateTime.UtcNow)
            return false;
        return !(MaxSolves == 1 && Solves <= 1);
    }

    /// <summary>Time until the flag can be solved again (zero when ready).</summary>
    public TimeSpan TimeUntilReady()
    {
        var left = (CompletedOn + RepeatTime) - DateTime.UtcNow;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }
}

/// <summary>
/// Fires /myquests, parses the resulting chat lines into a per-session quest flag cache,
/// and exposes UB-compatible query methods used by ExpressionEngine. The ILT Hub Quest
/// Tracker UI reads <see cref="Snapshot"/> and may request quiet refreshes.
/// </summary>
internal sealed class QuestTracker
{
    // Matches lines produced by the AC /myquests command.
    // Format: key - N solves (unixTimestamp)"description" maxSolves repeatSeconds
    // Example: blankaug - 1 solves (1609459200)"Blank Aug" 1 0
    private static readonly Regex QuestLineRegex = new Regex(
        @"(?<key>\S+) \- (?<solves>\d+) solves \((?<completedOn>\d{0,11})\)""?((?<description>.*)"" (?<maxSolves>.*) (?<repeatTime>\d{0,11}))?.*$",
        RegexOptions.Compiled);

    private readonly RynthCoreHost _host;
    private readonly object _gate = new();
    private readonly Dictionary<string, QuestRecord> _flags = new(StringComparer.OrdinalIgnoreCase);

    private bool _refreshing;
    private bool _gotFirstQuest;
    private bool _clearOnFirstLine;
    private bool _quietRefresh;
    private DateTime _lastLineTime;
    private DateTime _refreshStarted;

    public bool IsRefreshing => _refreshing;

    /// <summary>The server answered that /myquests is not enabled.</summary>
    public bool Disabled { get; private set; }

    /// <summary>UTC time the last refresh finished with data (MinValue = never).</summary>
    public DateTime LastRefreshUtc { get; private set; } = DateTime.MinValue;

    /// <summary>Last refresh outcome text for the UI ("12 flags", "rate limited", ...).</summary>
    public string LastStatus { get; private set; } = "not refreshed";

    /// <summary>Number of cached flags.</summary>
    public int Count { get { lock (_gate) return _flags.Count; } }

    /// <summary>Raised on the pump thread whenever a refresh ends (any reason).</summary>
    public event Action? RefreshCompleted;

    public QuestTracker(RynthCoreHost host) => _host = host;

    /// <summary>
    /// Issues /myquests to the server to populate the quest flag cache.
    /// No-ops if already refreshing or InvokeChatParser is not available.
    /// </summary>
    /// <param name="quiet">When true, <see cref="OnChatLine"/> reports quest lines as eatable.</param>
    public void Refresh(bool quiet = false)
    {
        if (_refreshing || !_host.HasInvokeChatParser)
            return;
        _refreshing = true;
        _gotFirstQuest = false;
        _clearOnFirstLine = true;
        _quietRefresh = quiet;
        _lastLineTime = DateTime.UtcNow;
        _refreshStarted = DateTime.UtcNow;
        _host.InvokeChatParser("/myquests");
    }

    /// <summary>
    /// Feed every incoming chat line here. Quest lines are parsed and cached;
    /// terminal lines (empty list, rate limit, etc.) end the refresh early.
    /// Returns true when the line belonged to a QUIET refresh (caller may hide it).
    /// </summary>
    public bool OnChatLine(string text)
    {
        if (!_refreshing)
            return false;

        if (text.Contains("Quest list is empty"))
        {
            lock (_gate) _flags.Clear();
            Finish("quest list is empty", gotData: true);
            return _quietRefresh;
        }

        if (text.Contains("The command \"myquests\" is not currently enabled"))
        {
            Disabled = true;
            Finish("/myquests is disabled on this server", gotData: false);
            return _quietRefresh;
        }

        if (text.Contains("This command may only be run once every"))
        {
            Finish("rate limited by the server (try again in a minute)", gotData: false);
            return _quietRefresh;
        }

        var m = QuestLineRegex.Match(text);
        if (!m.Success)
            return false;

        _gotFirstQuest = true;
        _lastLineTime = DateTime.UtcNow;

        var rec = new QuestRecord();
        rec.Key = m.Groups["key"].Value.ToLowerInvariant();
        rec.Description = m.Groups["description"].Value.Trim().Trim('"');
        int.TryParse(m.Groups["solves"].Value, out rec.Solves);
        int.TryParse(m.Groups["maxSolves"].Value, out rec.MaxSolves);

        if (double.TryParse(m.Groups["completedOn"].Value,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out double ts) && ts > 0)
            rec.CompletedOn = DateTimeOffset.FromUnixTimeSeconds((long)ts).UtcDateTime;

        if (double.TryParse(m.Groups["repeatTime"].Value,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out double rt) && rt > 0)
            rec.RepeatTime = TimeSpan.FromSeconds(rt);

        lock (_gate)
        {
            // A new listing replaces the old one wholesale (removed flags disappear).
            if (_clearOnFirstLine) { _flags.Clear(); _clearOnFirstLine = false; }
            _flags[rec.Key] = rec;
        }
        return _quietRefresh;
    }

    /// <summary>
    /// Call once per game tick. Detects end-of-list by silence:
    ///   1 second after the last quest line received → done.
    ///   15 second global timeout if no quest lines ever arrive → done.
    /// </summary>
    public void Tick()
    {
        if (!_refreshing)
            return;

        var now = DateTime.UtcNow;
        if (_gotFirstQuest)
        {
            if ((now - _lastLineTime).TotalSeconds >= 1.0)
                Finish($"{Count} flags", gotData: true);
        }
        else
        {
            if ((now - _refreshStarted).TotalSeconds >= 15.0)
                Finish("no reply from server", gotData: false);
        }
    }

    private void Finish(string status, bool gotData)
    {
        _refreshing = false;
        _quietRefresh = false;
        LastStatus = status;
        if (gotData)
        {
            Disabled = false;
            LastRefreshUtc = DateTime.UtcNow;
        }
        try { RefreshCompleted?.Invoke(); }
        catch (Exception ex) { RynthLog.Write(LogCat.Quests, $"[RynthAi] QuestTracker RefreshCompleted handler threw: {ex.Message}"); }
    }

    /// <summary>Returns true if the key exists in the cached quest flag list.</summary>
    public bool HasFlag(string key) { lock (_gate) return _flags.ContainsKey(key.ToLowerInvariant()); }

    /// <summary>Returns the cached record for the given key, or false if not found.</summary>
    public bool TryGetFlag(string key, out QuestRecord rec)
    {
        lock (_gate) return _flags.TryGetValue(key.ToLowerInvariant(), out rec!);
    }

    /// <summary>Copy of every cached record (safe to enumerate from any thread).</summary>
    public QuestRecord[] Snapshot()
    {
        lock (_gate) return _flags.Values.ToArray();
    }
}
