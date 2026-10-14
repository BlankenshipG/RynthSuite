using System;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.UbRythai;

/// <summary>
/// UB-ILT XPMeter parity without native client hooks. Since 3.1.13 (2026-10-13) it counts the way
/// every other tracker does:
/// <list type="bullet">
/// <item>With RynthAi loaded it shows RynthAi's own session (the Mini Remote's Session Stats, read
/// through "RynthAi.Hub"), so the meter, the Mini Remote, the Tracker panel, RynthOracle and RynthILT
/// all show one session; "/ub xpmeter reset" then starts that session over.</item>
/// <item>Without RynthAi it runs the shared SessionStats code (Plugins\Shared, the same code RynthAi
/// and RynthTracker use) on the player's properties: earned XP from TotalExperience rises, and
/// luminance from AvailableLuminance or, on ACECustom, the bank (9005) with deposits and transfers
/// left out, plus Aelrynth's bank totals (9102 / 9103).</item>
/// </list>
/// Tick and OnChat run on the pump; the overlay (render thread) reads the published <see cref="Reading"/>.
/// </summary>
internal sealed class UbRythaiXpMeterState
{
    // ACE.Entity.Enum.Properties.PropertyInt64 (ushort values passed through the host as uint stype).
    private const uint PropertyTotalExperience = 1;
    private const uint PropertyAvailableLuminance = 6;
    private const uint PropertyMaximumLuminance = 7;
    private const uint PropertyBankedLuminance = 9005;   // ACECustom bank
    private const uint PropertyRadianceEarned = 9101;    // Aelrynth Bank mod session totals
    private const uint PropertyLuminanceBanked = 9102;
    private const uint PropertyLuminanceDrawn = 9103;

    private const long PollIntervalMs = 500;

    /// <summary>What the overlay and "/ub xpmeter status" show (immutable; swapped after each poll).</summary>
    internal sealed record Reading(long Xp, long Lum, double Seconds, double XpPerHour, double LumPerHour, bool HasLuminance, bool FromRynthAi)
    {
        public static readonly Reading Empty = new(0, 0, 0, 0, 0, false, false);
    }

    private readonly SessionStats _stats = new();
    private readonly RynthAiSessionClient _ai = new();
    private uint _playerId;
    private long _nextPollMs;
    private bool _hasLuminance;
    private volatile Reading _reading = Reading.Empty;

    public long AccumulatedXp => _reading.Xp;
    public long AccumulatedLum => _reading.Lum;
    public bool HasLuminance => _reading.HasLuminance;
    /// <summary>True while the numbers are RynthAi's session (one session for every tracker).</summary>
    public bool FromRynthAi => _reading.FromRynthAi;

    /// <summary>The session's length in seconds (floored at one second, as the rates are).</summary>
    public double RunTimeSeconds() => Math.Max(1.0, _reading.Seconds);

    public double XpPerHour() => _reading.XpPerHour;
    public double LumPerHour() => _reading.LumPerHour;

    /// <summary>
    /// Starts the session over: RynthAi's when it is the one shown (so every tracker restarts
    /// together), and the meter's own either way. Pump thread.
    /// </summary>
    public void Reset(RynthCoreHost host)
    {
        if (_reading.FromRynthAi) _ai.ResetSession(host, Environment.TickCount64);
        _stats.Reset(DateTime.UtcNow);
        _reading = Reading.Empty;
        _nextPollMs = 0;
    }

    /// <summary>Login (or a reset): a fresh session whose first poll sets the baselines.</summary>
    public void SeedFromPlayer(RynthCoreHost host, uint playerId)
    {
        _playerId = playerId;
        _ai.Reset();
        _stats.Reset(DateTime.UtcNow);
        _reading = Reading.Empty;
        _nextPollMs = 0;
        _hasLuminance = playerId != 0 && host.HasGetObjectQuadProperty
            && host.TryGetObjectQuadProperty(playerId, PropertyMaximumLuminance, out long maxLum) && maxLum > 0;
        Tick(host, playerId);
    }

    /// <summary>The server's luminance lines (deposits and transfers into the bank aren't earnings). Pump thread.</summary>
    public void OnChat(string? text) => _stats.OnChat(text, DateTime.UtcNow);

    /// <summary>Polls the player's properties every half second and publishes the reading. Pump thread.</summary>
    public void Tick(RynthCoreHost host, uint playerId)
    {
        if (playerId == 0 || !host.HasGetObjectQuadProperty) return;
        if (playerId != _playerId)
        {
            SeedFromPlayer(host, playerId);
            return;
        }
        long nowMs = Environment.TickCount64;
        if (nowMs < _nextPollMs) return;
        _nextPollMs = nowMs + PollIntervalMs;
        DateTime now = DateTime.UtcNow;

        _stats.Sample(new SessionSample(
            Read(host, PropertyTotalExperience),
            Read(host, PropertyAvailableLuminance),
            Read(host, PropertyBankedLuminance),
            Read(host, PropertyLuminanceBanked),
            Read(host, PropertyLuminanceDrawn),
            Read(host, PropertyRadianceEarned)), now);

        if (_ai.TryGet(host, nowMs, out SessionStatsV1 s))
        {
            _reading = new Reading(s.Xp, s.Lum, s.SessionSeconds, s.XpPerHour, s.LumPerHour, _hasLuminance || s.Lum > 0, true);
            return;
        }
        SessionStatsView v = _stats.View(now);
        _reading = new Reading(v.Xp, v.Lum, v.SessionSeconds, v.XpPerHour, v.LumPerHour, _hasLuminance || v.PassUpTracked || v.Lum > 0, false);
    }

    private long? Read(RynthCoreHost host, uint stype)
        => host.TryGetObjectQuadProperty(_playerId, stype, out long value) ? value : null;
}
