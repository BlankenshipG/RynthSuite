using System;

namespace RynthCore.Plugin.RynthNav;

/// <summary>
/// Keeps a goto on the trip's own path (2026-11-01). The trip's walk (TripPath, the path the markers
/// draw) is worked out once for the whole step. The walker used to plan its own way to the route graph's
/// next border crossing every 0.7 s instead, so it went its own way: across fields, round the other side
/// of a building. Players saw it wander off the path the markers showed. Now each plan aims at a point
/// <see cref="LookAhead"/> along the step's walk past where we are on it. The navmesh still plans those
/// few metres, so walls and portals are still kept out of. Off the path by more than
/// <see cref="OffPathUnits"/> (knocked off it, stepped round something), it aims back at the path first.
/// Progress along the walk only goes forward, so a path that doubles back is never cut across.
/// Host-free: the route tests drive it.
/// </summary>
internal sealed class TripFollow
{
    /// <summary>How far ahead along the walk each plan aims (yards).</summary>
    public const double LookAhead = 28.0;
    /// <summary>Farther than this from the walk is off it: aim back at it.</summary>
    public const double OffPathUnits = 14.0;
    /// <summary>Back on the walk, aim this far ahead of the nearest point on it.</summary>
    public const double RejoinAhead = 6.0;
    /// <summary>How far back along the walk (yards) the nearest point may be looked for.</summary>
    public const double SearchBack = 12.0;
    /// <summary>...and how far ahead of it (a walk that comes back close to itself isn't cut across).</summary>
    public const double SearchAhead = 90.0;

    private readonly WorldPt[] _pts;
    private readonly double[] _cum;
    private double _progress;

    public int TripId { get; }
    public int Step { get; }
    public double Length => _cum[^1];
    /// <summary>How far along the walk we are (yards; only goes forward).</summary>
    public double Progress => _progress;
    /// <summary>The last Aim's distance from the walk.</summary>
    public double LastOff { get; private set; }

    public TripFollow(int tripId, int step, WorldPt[] pts)
    {
        if (pts.Length < 2) throw new ArgumentException("a walk has two points at least", nameof(pts));
        TripId = tripId; Step = step;
        _pts = pts;
        _cum = new double[pts.Length];
        for (int i = 1; i < pts.Length; i++) _cum[i] = _cum[i - 1] + pts[i].FlatDistance(pts[i - 1]);
    }

    /// <summary>
    /// The point to plan to from (x, y): <see cref="LookAhead"/> along the walk past where we are on it,
    /// or, off the walk, a little past the nearest point on it. <paramref name="off"/> is how far from the
    /// walk we are; <paramref name="nearEnd"/> is true when the aim is the walk's end.
    /// </summary>
    public (double X, double Y) Aim(double x, double y, out double off, out bool nearEnd)
    {
        double s = Nearest(x, y, out off);
        LastOff = off;
        double aimAt;
        if (off > OffPathUnits) aimAt = s + RejoinAhead;
        else
        {
            if (s > _progress) _progress = s;
            aimAt = _progress + LookAhead;
        }
        if (aimAt >= Length) { nearEnd = true; return (_pts[^1].X, _pts[^1].Y); }
        nearEnd = false;
        return At(aimAt);
    }

    /// <summary>The point <paramref name="s"/> yards along the walk.</summary>
    public (double X, double Y) At(double s)
    {
        s = Math.Clamp(s, 0, Length);
        int i = Array.BinarySearch(_cum, s);
        if (i >= 0) return (_pts[i].X, _pts[i].Y);
        i = ~i;   // the first point past s
        if (i <= 0) return (_pts[0].X, _pts[0].Y);
        if (i >= _pts.Length) return (_pts[^1].X, _pts[^1].Y);
        double seg = _cum[i] - _cum[i - 1];
        double t = seg < 1e-9 ? 0 : (s - _cum[i - 1]) / seg;
        return (_pts[i - 1].X + (_pts[i].X - _pts[i - 1].X) * t, _pts[i - 1].Y + (_pts[i].Y - _pts[i - 1].Y) * t);
    }

    /// <summary>
    /// The nearest point on the walk to (x, y), not more than <see cref="SearchBack"/> behind the progress
    /// so far (a walk that passes close to itself isn't jumped back along): how far along it is.
    /// </summary>
    private double Nearest(double x, double y, out double dist)
    {
        double from = Math.Max(0, _progress - SearchBack);
        double best = double.MaxValue, bestS = _progress;
        for (int i = 1; i < _pts.Length; i++)
        {
            if (_cum[i] < from) continue;
            if (_cum[i - 1] > _progress + SearchAhead) break;
            double ax = _pts[i - 1].X, ay = _pts[i - 1].Y, bx = _pts[i].X, by = _pts[i].Y;
            double vx = bx - ax, vy = by - ay, len2 = vx * vx + vy * vy;
            double t = len2 < 1e-9 ? 0 : Math.Clamp(((x - ax) * vx + (y - ay) * vy) / len2, 0, 1);
            double sAt = _cum[i - 1] + Math.Sqrt(len2) * t;
            if (sAt < from) { t = (from - _cum[i - 1]) / Math.Max(Math.Sqrt(len2), 1e-9); sAt = from; }
            double px = ax + vx * t - x, py = ay + vy * t - y;
            double d = Math.Sqrt(px * px + py * py);
            if (d < best) { best = d; bestS = sAt; }
        }
        dist = best;
        return bestS;
    }
}
