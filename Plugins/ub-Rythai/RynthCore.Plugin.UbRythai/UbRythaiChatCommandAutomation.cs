using System;
using System.Collections.Generic;
using System.Globalization;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.UbRythai;

/// <summary>
/// Chat command queues for ub-Rythai (.NET 10 RynthCore plugin): login batch and periodic batch,
/// sent via <see cref="RynthCoreHost.InvokeChatParser"/> (not the net48 Decal host).
/// </summary>
internal sealed class UbRythaiChatCommandAutomation
{
    private const int MaxLoginDiagnostics = 100;
    private readonly RynthCoreHost _host;
    private readonly Func<UbRythaiSettings> _getSettings;
    private readonly Action<UbRythaiLogLevel, string> _log;
    private readonly Func<string, string, bool> _dispatchChatCommand;

    private bool _loginBatchActive;
    private int _loginCursor;
    private DateTime _nextLoginInvokeUtc = DateTime.MinValue;

    private bool _periodicBatchActive;
    private int _periodicCursor;
    private DateTime _nextPeriodicInvokeUtc = DateTime.MinValue;
    private DateTime _lastPeriodicCycleCompleteUtc = DateTime.MinValue;

    private bool _gapWaitLogin;
    private bool _gapWaitPeriodic;
    private readonly List<LoginQueueDiagnostic> _loginDiagnostics = [];

    public UbRythaiChatCommandAutomation(
        RynthCoreHost host,
        Func<UbRythaiSettings> getSettings,
        Action<UbRythaiLogLevel, string> log,
        Func<string, string, bool> dispatchChatCommand)
    {
        _host = host;
        _getSettings = getSettings;
        _log = log;
        _dispatchChatCommand = dispatchChatCommand;
    }

    public void OnLoginComplete()
    {
        UbRythaiSettings s = _getSettings();
        _loginBatchActive = s.LoginCommandsEnabled && HasNonEmptyLines(s.LoginCommandsText);
        _loginCursor = 0;
        _gapWaitLogin = false;
        if (_loginBatchActive)
        {
            int initialMs = ClampMs(s.LoginCommandsInitialDelayMs, 0, 120_000);
            _nextLoginInvokeUtc = DateTime.UtcNow.AddMilliseconds(initialMs);
            _log(UbRythaiLogLevel.Info, $"ub-rythai: login queue scheduled ({CountLines(s.LoginCommandsText)} line(s), initial delay {initialMs} ms).");
        }
        else
        {
            _nextLoginInvokeUtc = DateTime.MinValue;
        }

        _lastPeriodicCycleCompleteUtc = DateTime.UtcNow;
        _periodicBatchActive = false;
        _periodicCursor = 0;
        _gapWaitPeriodic = false;
    }

    public void RequestLoginQueueReplay()
    {
        UbRythaiSettings s = _getSettings();
        if (!s.LoginCommandsEnabled)
        {
            _log(UbRythaiLogLevel.Warn, "ub-rythai: login queue disabled (open ub-Rythai window from bar action).");
            return;
        }

        if (!HasNonEmptyLines(s.LoginCommandsText))
        {
            _log(UbRythaiLogLevel.Warn, "ub-rythai: login queue text is empty.");
            return;
        }

        _loginBatchActive = true;
        _loginCursor = 0;
        _gapWaitLogin = false;
        _nextLoginInvokeUtc = DateTime.UtcNow.AddMilliseconds(ClampMs(s.LoginCommandsInitialDelayMs, 0, 120_000));
        _log(UbRythaiLogLevel.Info, "ub-rythai: login queue replay requested.");
    }

    public void OnTick()
    {
        if (!_host.HasInvokeChatParser)
            return;

        UbRythaiSettings s = _getSettings();
        DateTime now = DateTime.UtcNow;

        if (_loginBatchActive && s.LoginCommandsEnabled)
            TickLoginQueue(s, now);
        else if (_periodicBatchActive || ShouldStartPeriodicCycle(s, now))
            TickPeriodicQueue(s, now);
    }

    public string GetPeriodicStatusLine()
    {
        UbRythaiSettings s = _getSettings();
        if (!s.PeriodicCommandsEnabled)
            return "periodic: disabled.";
        int n = CountLines(s.PeriodicCommandsText);
        if (n == 0)
            return "periodic: enabled, no lines.";
        return string.Format(CultureInfo.InvariantCulture,
            "periodic: enabled, {0} line(s), interval {1}s, batchActive={2}, cursor={3}, lastCycleUtc={4:O}",
            n, s.PeriodicIntervalSeconds, _periodicBatchActive, _periodicCursor, _lastPeriodicCycleCompleteUtc);
    }

    public string GetLoginQueueDiagnosticsStatusLine()
    {
        int success = 0;
        int failed = 0;
        for (int i = 0; i < _loginDiagnostics.Count; i++)
        {
            if (_loginDiagnostics[i].Success) success++;
            else failed++;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "loginq diag: attempts={0}, ok={1}, fail={2}, batchActive={3}, cursor={4}",
            _loginDiagnostics.Count, success, failed, _loginBatchActive, _loginCursor);
    }

    public IReadOnlyList<LoginQueueDiagnostic> GetLoginQueueDiagnosticsSnapshot() => _loginDiagnostics;

    public void ClearLoginQueueDiagnostics()
    {
        _loginDiagnostics.Clear();
    }

    private void TickLoginQueue(UbRythaiSettings s, DateTime now)
    {
        if (!s.LoginCommandsEnabled)
        {
            _loginBatchActive = false;
            return;
        }

        List<ParsedCommandLine> lines = ParseCommandLines(s.LoginCommandsText);
        if (_loginCursor >= lines.Count)
        {
            _loginBatchActive = false;
            _log(UbRythaiLogLevel.Info, "ub-rythai: login queue finished.");
            return;
        }

        int gap = ClampMs(s.LoginCommandsGapMs, 50, 60_000);
        if (_gapWaitLogin)
        {
            if (now < _nextLoginInvokeUtc)
                return;
            _gapWaitLogin = false;
        }

        if (now < _nextLoginInvokeUtc)
            return;

        ParsedCommandLine line = lines[_loginCursor];
        _loginCursor++;
        if (!TryInvokeLine(line.Command, "login queue", true, line.SourceLine))
            return;

        _gapWaitLogin = true;
        _nextLoginInvokeUtc = now.AddMilliseconds(gap);
    }

    private bool ShouldStartPeriodicCycle(UbRythaiSettings s, DateTime now)
    {
        if (!s.PeriodicCommandsEnabled || !HasNonEmptyLines(s.PeriodicCommandsText))
            return false;
        if (_periodicBatchActive)
            return false;

        int intervalSec = Math.Clamp(s.PeriodicIntervalSeconds, 5, 86_400);
        double elapsed = (now - _lastPeriodicCycleCompleteUtc).TotalSeconds;
        return elapsed >= intervalSec;
    }

    private void TickPeriodicQueue(UbRythaiSettings s, DateTime now)
    {
        if (!s.PeriodicCommandsEnabled)
        {
            _periodicBatchActive = false;
            return;
        }

        List<ParsedCommandLine> lines = ParseCommandLines(s.PeriodicCommandsText);
        if (lines.Count == 0)
        {
            _periodicBatchActive = false;
            return;
        }

        if (!_periodicBatchActive)
        {
            _periodicBatchActive = true;
            _periodicCursor = 0;
            _gapWaitPeriodic = false;
            _nextPeriodicInvokeUtc = now;
        }

        if (_periodicCursor >= lines.Count)
        {
            _periodicBatchActive = false;
            _lastPeriodicCycleCompleteUtc = now;
            _log(UbRythaiLogLevel.Info, "ub-rythai: periodic batch finished.");
            return;
        }

        int gap = ClampMs(s.PeriodicCommandsGapMs, 50, 60_000);
        if (_gapWaitPeriodic)
        {
            if (now < _nextPeriodicInvokeUtc)
                return;
            _gapWaitPeriodic = false;
        }

        if (now < _nextPeriodicInvokeUtc)
            return;

        ParsedCommandLine line = lines[_periodicCursor];
        _periodicCursor++;
        if (!TryInvokeLine(line.Command, "periodic queue", false, line.SourceLine))
            return;

        _gapWaitPeriodic = true;
        _nextPeriodicInvokeUtc = now.AddMilliseconds(gap);
    }

    private bool TryInvokeLine(string line, string source, bool trackLoginDiagnostics, int sourceLine)
    {
        try
        {
            bool ok = _dispatchChatCommand(line, source);
            if (trackLoginDiagnostics)
            {
                AddLoginDiagnostic(new LoginQueueDiagnostic(
                    DateTime.UtcNow,
                    sourceLine,
                    Truncate(line, 100),
                    ok,
                    ok ? "dispatched" : "dispatch blocked or failed"));
            }
            if (!ok)
                _log(UbRythaiLogLevel.Warn, $"ub-rythai: dispatch blocked/failed for {source}: {Truncate(line, 120)}");
            return ok;
        }
        catch (Exception ex)
        {
            if (trackLoginDiagnostics)
            {
                AddLoginDiagnostic(new LoginQueueDiagnostic(
                    DateTime.UtcNow,
                    sourceLine,
                    Truncate(line, 100),
                    false,
                    $"exception: {ex.Message}"));
            }
            _log(UbRythaiLogLevel.Error, $"ub-rythai: InvokeChatParser exception: {ex.Message}");
            return false;
        }
    }

    private static List<ParsedCommandLine> ParseCommandLines(string blob)
    {
        var list = new List<ParsedCommandLine>();
        if (string.IsNullOrWhiteSpace(blob))
            return list;

        int sourceLine = 1;
        foreach (ReadOnlySpan<char> raw in blob.AsSpan().EnumerateLines())
        {
            string line = raw.Trim().ToString();
            if (line.Length == 0)
            {
                sourceLine++;
                continue;
            }
            if (line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
            {
                sourceLine++;
                continue;
            }
            list.Add(new ParsedCommandLine(sourceLine, line));
            sourceLine++;
        }

        return list;
    }

    private static bool HasNonEmptyLines(string blob) => ParseCommandLines(blob).Count > 0;

    private static int CountLines(string blob) => ParseCommandLines(blob).Count;

    private void AddLoginDiagnostic(LoginQueueDiagnostic diagnostic)
    {
        _loginDiagnostics.Add(diagnostic);
        if (_loginDiagnostics.Count > MaxLoginDiagnostics)
            _loginDiagnostics.RemoveAt(0);
    }

    private static int ClampMs(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private static string Truncate(string s, int maxLen)
    {
        if (s.Length <= maxLen)
            return s;
        return s.Substring(0, maxLen) + "...";
    }

    internal readonly record struct LoginQueueDiagnostic(
        DateTime TimestampUtc,
        int SourceLine,
        string CommandPreview,
        bool Success,
        string Detail);

    private readonly record struct ParsedCommandLine(int SourceLine, string Command);
}
