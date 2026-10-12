using System;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.UbRythai;

/// <summary>
/// UB-ILT XPMeter parity without native client hooks: polls <see cref="RynthCoreHost.TryGetObjectQuadProperty"/> on the player.
/// Uses <c>PropertyInt64.TotalExperience</c> for earned XP deltas (available XP can decrease when spending).
/// </summary>
internal sealed class UbRythaiXpMeterState
{
    // ACE.Entity.Enum.Properties.PropertyInt64 (ushort values passed through the host as uint stype).
    private const uint PropertyTotalExperience = 1;
    private const uint PropertyAvailableLuminance = 6;
    private const uint PropertyMaximumLuminance = 7;

    private long _lastTotalXp;
    private long _lastAvailableLum;
    private long _accumXp;
    private long _accumLum;
    private DateTime _startUtc = DateTime.UtcNow;
    private bool _hasLuminance;

    public long AccumulatedXp => _accumXp;
    public long AccumulatedLum => _accumLum;

    public void Reset()
    {
        _accumXp = 0;
        _accumLum = 0;
        _startUtc = DateTime.UtcNow;
        _lastTotalXp = 0;
        _lastAvailableLum = 0;
        _hasLuminance = false;
    }

    public void SeedFromPlayer(RynthCoreHost host, uint playerId)
    {
        if (playerId == 0 || !host.HasGetObjectQuadProperty)
        {
            Reset();
            return;
        }

        if (!host.TryGetObjectQuadProperty(playerId, PropertyTotalExperience, out long totalXp))
            totalXp = 0;

        long maxLum = 0;
        long availLum = 0;
        _hasLuminance = host.TryGetObjectQuadProperty(playerId, PropertyMaximumLuminance, out maxLum) && maxLum > 0
            && host.TryGetObjectQuadProperty(playerId, PropertyAvailableLuminance, out availLum);
        if (!_hasLuminance)
            availLum = 0;

        _lastTotalXp = totalXp;
        _lastAvailableLum = availLum;
        _accumXp = 0;
        _accumLum = 0;
        _startUtc = DateTime.UtcNow;
    }

    public void Tick(RynthCoreHost host, uint playerId)
    {
        if (playerId == 0 || !host.HasGetObjectQuadProperty)
            return;

        if (host.TryGetObjectQuadProperty(playerId, PropertyTotalExperience, out long totalXp))
        {
            long delta = totalXp - _lastTotalXp;
            if (delta > 0)
                _accumXp += delta;
            _lastTotalXp = totalXp;
        }

        if (_hasLuminance && host.TryGetObjectQuadProperty(playerId, PropertyAvailableLuminance, out long lum))
        {
            long deltaL = lum - _lastAvailableLum;
            if (deltaL > 0)
                _accumLum += deltaL;
            _lastAvailableLum = lum;
        }
    }

    public double RunTimeSeconds()
    {
        double sec = (DateTime.UtcNow - _startUtc).TotalSeconds;
        return sec < 1.0 ? 1.0 : sec;
    }

    public double XpPerHour() => (_accumXp / RunTimeSeconds()) * 3600.0;
    public double LumPerHour() => (_accumLum / RunTimeSeconds()) * 3600.0;

    public bool HasLuminance => _hasLuminance;
}
