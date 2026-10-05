// ============================================================================
//  RynthSuite - Plugins/Shared/MoveAudit.cs
//  Movement calls with a caller tag: host.SetAutoRunBy("Nav", true) and friends.
//  Linked into the plugins that move the character, like UseAudit.cs.
//
//  Why (2026-10-05, Drakkon's stuck casts on DreamWeave): a movement packet sent
//  while a cast winds up can cancel the cast, or leave the server holding it open.
//  The engine logged only one movement call in eight and never said who sent it,
//  so "which subsystem sent SetAutoRun during Buffing" could not be answered.
//  Every call is now one log line:
//
//    [Move] RynthAi/Nav SetAutoRun(False)
//
//  Identical calls from the same subsystem in a row (the nav servo turns every
//  tick; any angle counts as identical for turns) are counted instead of written
//  one by one, and the count goes out with the next line that is written (the
//  next different call, or the next repeat after RepeatWindowMs):
//
//    [Move] RynthAi/Arbiter StopCompletely() (+14 more TurnToHeading, last 230.5 before this, over 480 ms)
//
//  The engine logs the same calls (with the plugin's name) behind its own move
//  trace switch; this line adds the subsystem, which only the plugin knows.
// ============================================================================

using System;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.Shared;

internal static class MoveAudit
{
    /// <summary>Off = the calls go through without a line. On by default.</summary>
    public static volatile bool Enabled = true;

    /// <summary>The plugin's name in every line (UseAudit.PluginName is set at Init too).</summary>
    public static string PluginName => UseAudit.PluginName;

    /// <summary>Clock (ms); tests replace it.</summary>
    public static Func<long> NowMs = () => Environment.TickCount64;

    // Identical calls closer than this to the last WRITTEN line are only counted.
    private const long RepeatWindowMs = 1000;

    private static readonly object Gate = new();
    private static string _lastKey = "";
    private static long _lastWrittenMs = long.MinValue;
    private static int _repeats;
    private static string _repeatLastArg = "";
    private static long _repeatFirstMs;

    public static bool SetAutoRunBy(this RynthCoreHost host, string who, bool on)
    {
        Note(host, who, "SetAutoRun", on ? "True" : "False", collapseByKindOnly: false);
        return host.SetAutoRun(on);
    }

    public static bool StopCompletelyBy(this RynthCoreHost host, string who)
    {
        Note(host, who, "StopCompletely", "", collapseByKindOnly: false);
        return host.StopCompletely();
    }

    public static bool SetMotionBy(this RynthCoreHost host, string who, uint motion, bool on)
    {
        Note(host, who, "SetMotion", $"0x{motion:X8}, {(on ? "True" : "False")}", collapseByKindOnly: false);
        return host.SetMotion(motion, on);
    }

    public static bool DoMovementBy(this RynthCoreHost host, string who, uint motion, float speed, int holdKey)
    {
        Note(host, who, "DoMovement", $"0x{motion:X8}, {speed:0.##}, {holdKey}", collapseByKindOnly: false);
        return host.DoMovement(motion, speed, holdKey);
    }

    public static bool StopMovementBy(this RynthCoreHost host, string who, uint motion, int holdKey)
    {
        Note(host, who, "StopMovement", $"0x{motion:X8}, {holdKey}", collapseByKindOnly: false);
        return host.StopMovement(motion, holdKey);
    }

    /// <summary>A heading snap. Successive turns from one subsystem collapse whatever the angle.</summary>
    public static bool TurnToHeadingBy(this RynthCoreHost host, string who, float headingDegrees)
    {
        Note(host, who, "TurnToHeading", headingDegrees.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), collapseByKindOnly: true);
        return host.TurnToHeading(headingDegrees);
    }

    /// <summary>Writes (or counts) one call. Internal for the tests.</summary>
    internal static string? Note(RynthCoreHost host, string who, string call, string arg, bool collapseByKindOnly)
    {
        if (!Enabled) return null;
        string? line = null;
        lock (Gate)
        {
            long now = NowMs();
            string key = collapseByKindOnly ? $"{who}|{call}" : $"{who}|{call}({arg})";
            if (key == _lastKey && now - _lastWrittenMs < RepeatWindowMs)
            {
                if (_repeats == 0) _repeatFirstMs = now;
                _repeats++;
                _repeatLastArg = arg;
                return null;
            }

            string more = _repeats > 0
                ? $" (+{_repeats} more {RepeatName()} before this, over {now - _repeatFirstMs} ms)"
                : "";
            line = $"[Move] {PluginName}/{who} {call}({arg}){more}";
            _lastKey = key;
            _lastWrittenMs = now;
            _repeats = 0;
            _repeatLastArg = "";
        }
        try { host.Log(line); } catch { }
        return line;
    }

    private static string RepeatName()
    {
        int bar = _lastKey.IndexOf('|');
        string last = bar >= 0 ? _lastKey.Substring(bar + 1) : _lastKey;
        return _lastKey.EndsWith(")", StringComparison.Ordinal) ? last : $"{last}, last {_repeatLastArg}";
    }

    /// <summary>Back to a fresh load (Init, tests).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _lastKey = "";
            _lastWrittenMs = long.MinValue;
            _repeats = 0;
            _repeatLastArg = "";
        }
    }
}
