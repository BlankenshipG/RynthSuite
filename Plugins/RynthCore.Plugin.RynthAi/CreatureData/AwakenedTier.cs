using System;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.CreatureData;

/// <summary>
/// Aelrynth's difficulty tier where the player stands, and which server this is (2026-10-03).
///
/// Aelrynth's awakened worlds and scaled dungeon copies hold the same monsters as real Dereth -
/// same names, same wcids - with health, skills and damage scaled 5% a tier (resistances and
/// weaknesses are not). Every monster in one copy or world shares one tier. The server tells
/// the player's own client that tier as PropertyInt 9051 on the player (Aeshnidae.AwakenedRewards
/// TierFeed.cs): a private update on login, teleport and every change; 0 = real Dereth.
///
/// The rule: on any server other than Aelrynth this is inert - <see cref="Current"/> is 0 and
/// <see cref="Active"/> false - so every store keyed by tier behaves exactly as before. On
/// Aelrynth it also stays inert until the property has been seen this login (an older server
/// build that does not send it changes nothing either).
///
/// Server detection matches RynthOracle's (Data/ServerIdentity.cs) older-engine path: the world
/// name the server announced (Aelrynth's live world is still named "Aeshnidae"; both names, and
/// the staging world, count), else the Bank mod's player properties 9101-9103, which only
/// Aelrynth sends. Engine API v75 adds GetServerInfo (the engine's own verdict); when the SDK
/// this builds against has it, it should be asked first, as RynthOracle does.
/// </summary>
internal sealed class AwakenedTier
{
    /// <summary>PropertyInt 9051 on the player: the difficulty tier where they stand.</summary>
    public const uint TierProperty = 9051;

    /// <summary>The server's scaling per tier (InstancesNoDat DifficultyPercentPerEnlightenment, 5.0).</summary>
    public const double PercentPerTier = 5.0;

    private const uint QuadRadianceEarned = 9101, QuadLuminanceBanked = 9102, QuadLuminanceDrawn = 9103;

    private volatile bool _aelrynth;
    private volatile bool _seen;
    private volatile int _tier;
    private string _serverKey = "";
    private long _nextRefreshMs;

    /// <summary>On Aelrynth (live or staging), by the world name or the Bank properties.</summary>
    public bool OnAelrynth => _aelrynth;

    /// <summary>The tier property has been seen this login.</summary>
    public bool Seen => _seen;

    /// <summary>The tier feature is in force: on Aelrynth and the property seen. Off everywhere else.</summary>
    public bool Active => _aelrynth && _seen;

    /// <summary>The tier where the player stands; 0 whenever <see cref="Active"/> is false.</summary>
    public int Current => Active ? _tier : 0;

    /// <summary>
    /// The server's key for per-server data (creatures.json): "Aelrynth" for every Aelrynth
    /// world name (live "Aeshnidae", "Aelrynth", "Aeshnidae Staging"), else the world name made
    /// safe for a folder name; "" while unknown.
    /// </summary>
    public string ServerKey => _serverKey;

    /// <summary>The multiplier a tier puts on a monster's health: 1 + 5% per tier.</summary>
    public static double HealthScale(int tier) => tier <= 0 ? 1.0 : 1.0 + PercentPerTier / 100.0 * tier;

    /// <summary>Logout: nothing carries to the next login (it may be another server).</summary>
    public void Reset()
    {
        _aelrynth = false;
        _seen = false;
        _tier = 0;
        _serverKey = "";
        _nextRefreshMs = 0;
    }

    /// <summary>Re-reads the server and the tier, at most once a second. Pump thread.</summary>
    public void Refresh(RynthCoreHost host, uint playerId, bool force = false)
    {
        long now = Environment.TickCount64;
        if (!force && now < _nextRefreshMs) return;
        _nextRefreshMs = now + 1000;

        try
        {
            string world = host.HasGetWorldName && host.TryGetWorldName(out string w) ? (w ?? "").Trim() : "";
            bool aelrynth = IsAelrynthWorld(world)
                || (playerId != 0 && host.HasGetObjectQuadProperty
                    && (host.TryGetObjectQuadProperty(playerId, QuadRadianceEarned, out _)
                        || host.TryGetObjectQuadProperty(playerId, QuadLuminanceBanked, out _)
                        || host.TryGetObjectQuadProperty(playerId, QuadLuminanceDrawn, out _)));
            _aelrynth = aelrynth;
            _serverKey = aelrynth ? "Aelrynth" : SafeKey(world);

            if (aelrynth && playerId != 0 && host.HasGetObjectIntProperty
                && host.TryGetObjectIntProperty(playerId, TierProperty, out int tier))
            {
                _tier = Math.Max(0, tier);
                _seen = true;
            }
        }
        catch { }
    }

    /// <summary>Aelrynth's world names: "Aelrynth...", or "Aeshnidae..." (its earlier name, still the live config's).</summary>
    public static bool IsAelrynthWorld(string? world)
    {
        if (string.IsNullOrWhiteSpace(world)) return false;
        string w = world.Trim();
        return w.StartsWith("Aelrynth", StringComparison.OrdinalIgnoreCase)
            || w.StartsWith("Aeshnidae", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A world name as a folder name (the SettingsProfiles rule); "" for none.</summary>
    public static string SafeKey(string? world)
    {
        if (string.IsNullOrWhiteSpace(world)) return "";
        var sb = new System.Text.StringBuilder(world.Length);
        foreach (char c in world.Trim())
            sb.Append(char.IsLetterOrDigit(c) || c == '\'' || c == '-' ? c : '_');
        return sb.ToString();
    }
}
