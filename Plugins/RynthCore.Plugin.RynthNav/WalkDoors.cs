using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthNav;

// Doors on RynthNav's walks inside dungeons (2026-10-30): which closed door stands across the walk
// ahead, and which pack items may unlock one. No host calls: the offline tests compile this file.
// RynthNavPlugin.Doors.cs does the using.
internal static class WalkDoors
{
    /// <summary>A door this far (metres, flat) from the walk's line stands across it.</summary>
    public const double CorridorUnits = 1.8;
    /// <summary>Only the walk this far ahead (metres along it) is looked at.</summary>
    public const double LookAheadUnits = 10.0;
    /// <summary>A door more than this above or below the walk at that spot is on another level.</summary>
    public const double LevelUnits = 3.0;

    /// <summary>
    /// How far along the walk (metres, from (x, y)) a door at (dx, dy, dz) stands across it, or -1:
    /// within CorridorUnits of a leg in the first LookAheadUnits, on the leg's level when the walk
    /// has heights (<paramref name="levels"/>). <paramref name="ahead"/>: the points still ahead, in order.
    /// </summary>
    public static double AlongWalk(double x, double y, double z, IReadOnlyList<TownNetPoint> ahead, bool levels,
        double dx, double dy, double dz)
    {
        double along = 0, px = x, py = y, pz = z;
        foreach (TownNetPoint p in ahead)
        {
            double sx = p.X - px, sy = p.Y - py, len = Math.Sqrt(sx * sx + sy * sy);
            double t = len < 1e-6 ? 0 : Math.Clamp(((dx - px) * sx + (dy - py) * sy) / (len * len), 0, 1);
            double cx = px + sx * t - dx, cy = py + sy * t - dy;
            double off = Math.Sqrt(cx * cx + cy * cy);
            double legZ = double.IsNaN(pz) ? p.Z : pz + (p.Z - pz) * t;
            bool level = !levels || double.IsNaN(dz) || Math.Abs(legZ - dz) < LevelUnits;
            if (off <= CorridorUnits && level) return along + len * t <= LookAheadUnits ? along + len * t : -1;
            along += len;
            if (along > LookAheadUnits) break;
            px = p.X; py = p.Y; pz = p.Z;
        }
        return -1;
    }

    /// <summary>A door this close (metres, flat) in front of us is what a blocked walk ran into.</summary>
    public const double InFrontUnits = 4.5;
    /// <summary>...within this angle either side of the heading (degrees).</summary>
    public const double InFrontHalfAngle = 65.0;

    /// <summary>
    /// The door at (dx, dy, dz) is in front of us at (x, y, z) facing (hx, hy) (a unit vector): within
    /// InFrontUnits, InFrontHalfAngle of the heading and LevelUnits up or down; <paramref name="dist"/> how far.
    /// </summary>
    public static bool InFront(double x, double y, double z, double hx, double hy, double dx, double dy, double dz, out double dist)
    {
        double vx = dx - x, vy = dy - y;
        dist = Math.Sqrt(vx * vx + vy * vy);
        if (dist > InFrontUnits) return false;
        if (!double.IsNaN(z) && !double.IsNaN(dz) && Math.Abs(dz - z) >= LevelUnits) return false;
        if (dist < 0.5) return true;   // on top of it
        double cos = (vx * hx + vy * hy) / dist;
        return cos >= Math.Cos(InFrontHalfAngle * Math.PI / 180.0);
    }

    /// <summary>What a pack item can do to a lock.</summary>
    public enum Unlocker { None, Key, Lockpick }

    private static readonly Regex KeyName = new(@"\bkey(ring)?s?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PickName = new(@"\block\s?picks?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A key (or keyring) or a lockpick, by its name.</summary>
    public static Unlocker UnlockerOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Unlocker.None;
        if (PickName.IsMatch(name)) return Unlocker.Lockpick;
        if (KeyName.IsMatch(name)) return Unlocker.Key;
        return Unlocker.None;
    }

    private static readonly Regex LockedText = new(@"^\s*the\s.+\sis\s+locked\s*[.!]?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The server's "The door is locked!" (ACE Door.ActOnUse).</summary>
    public static bool IsLockedText(string? text) =>
        !string.IsNullOrEmpty(text) && text.Length <= 120 && LockedText.IsMatch(text);

    /// <summary>The order to try unlockers in: keys first (they aren't used up), then lockpicks.</summary>
    public static List<(uint Id, string Name, Unlocker Kind)> TryOrder(IEnumerable<(uint Id, string Name)> pack)
    {
        var keys = new List<(uint, string, Unlocker)>();
        var picks = new List<(uint, string, Unlocker)>();
        foreach (var (id, name) in pack)
        {
            Unlocker k = UnlockerOf(name);
            if (k == Unlocker.Key) keys.Add((id, name, k));
            else if (k == Unlocker.Lockpick) picks.Add((id, name, k));
        }
        keys.AddRange(picks);
        return keys;
    }
}
