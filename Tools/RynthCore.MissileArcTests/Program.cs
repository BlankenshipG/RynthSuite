using System;
using RynthCore.Plugin.RynthAi.Raycasting;

namespace RynthCore.MissileArcTests;

// Tests for the missile arc math combat's LOS uses (MissileBallistics): the low ballistic
// solution passes through the aim point, rises with distance and falls with launch speed,
// goes out of reach past v²/g, and the clearance bump leaves both ends in place.
// Self-contained; fails on zero assertions.
//
// Run: dotnet run -c Release  (exit 0 = pass, 1 = fail)
internal static class Program
{
    private static int _asserts;
    private static int _fails;

    private static void Check(bool cond, string msg)
    {
        _asserts++;
        if (!cond) { _fails++; Console.WriteLine($"  [FAIL] {msg}"); }
    }

    private static void Near(double actual, double expected, double tol, string msg)
    {
        _asserts++;
        if (double.IsNaN(actual) || Math.Abs(actual - expected) > tol)
        { _fails++; Console.WriteLine($"  [FAIL] {msg}: expected {expected:0.####} ±{tol}, got {actual:0.####}"); }
    }

    private static int Main()
    {
        Console.WriteLine("=== Missile arc tests ===");
        TestFlatShot();
        TestPassesThroughAimPoint();
        TestRiseGrowsWithDistance();
        TestFasterIsFlatter();
        TestOutOfReach();
        TestUphillAndDownhill();
        TestShortShotIsALine();
        TestVerticalShot();
        TestClearanceBump();
        TestDungeonCeiling();
        TestSegmentCount();
        TestBadInput();

        Console.WriteLine($"\n{_asserts} assertions, {_fails} failed.");
        if (_asserts == 0) { Console.WriteLine("ABORT: zero assertions ran."); return 1; }
        Console.WriteLine(_fails == 0 ? "ALL MISSILE ARC TESTS PASSED." : $"{_fails} FAILURE(S).");
        return _fails == 0 ? 0 : 1;
    }

    private static MissileArc Flat(float d, float v) => MissileBallistics.Solve(0, 0, 1, d, 0, 1, v);

    // 40 m on the level at 25 m/s: low solution tanθ = (v² − √(v⁴ − g²d²))/(g·d) ≈ 0.3526
    // (19.4°), apex at mid-flight, d·tanθ/4 ≈ 3.53 m above the launch point.
    private static void TestFlatShot()
    {
        var a = Flat(40, 25);
        Check(a.Valid, "40 m at 25 m/s is in reach");
        Near(a.TanTheta, 0.35259, 0.0005, "tanθ, 40 m at 25 m/s");
        Check(a.TanTheta < 1f, "the LOW solution is used (under 45°)");
        Near(a.ApexAboveLaunch, 40 * a.TanTheta / 4, 0.01, "flat apex = d·tanθ/4");
        Near(a.ApexAboveLaunch, 3.526, 0.01, "apex, 40 m at 25 m/s");
        Near(a.MaxRiseAboveChord, a.ApexAboveLaunch, 0.01, "on the level the rise above the line is the apex");
        Near(a.HeightAt(0.5f), a.ApexAboveLaunch, 0.01, "apex at mid-flight");
        Near(a.HeightAt(0.25f), a.HeightAt(0.75f), 0.001, "symmetric on the level");
    }

    private static void TestPassesThroughAimPoint()
    {
        var a = MissileBallistics.Solve(10, 20, 5, 40, -10, 7.5f, 30);
        Check(a.Valid, "diagonal shot in reach");
        a.PointAt(1f, 0f, out float x, out float y, out float z);
        Near(x, 40, 0.001, "end x");
        Near(y, -10, 0.001, "end y");
        Near(z, 7.5, 0.01, "end z (aim point)");
        a.PointAt(0f, 0f, out x, out y, out z);
        Near(x, 10, 0.001, "start x");
        Near(y, 20, 0.001, "start y");
        Near(z, 5, 0.001, "start z (launch point)");
        a.PointAt(0.5f, 0f, out x, out y, out _);
        Near(x, 25, 0.001, "mid x on the horizontal line");
        Near(y, 5, 0.001, "mid y on the horizontal line");
    }

    private static void TestRiseGrowsWithDistance()
    {
        float r10 = Flat(10, 25).MaxRiseAboveChord;
        float r20 = Flat(20, 25).MaxRiseAboveChord;
        float r40 = Flat(40, 25).MaxRiseAboveChord;
        float r55 = Flat(55, 25).MaxRiseAboveChord;
        Check(r10 < r20 && r20 < r40 && r40 < r55, $"rise grows with distance ({r10:0.00} < {r20:0.00} < {r40:0.00} < {r55:0.00})");
        Check(r10 < 0.25f, $"10 m is nearly flat ({r10:0.000} m)");
        Check(r55 > 5f, $"55 m rises past 5 m at 25 m/s ({r55:0.00} m)");
    }

    private static void TestFasterIsFlatter()
    {
        float slow = Flat(40, 22).MaxRiseAboveChord;
        float mid  = Flat(40, 25).MaxRiseAboveChord;
        float fast = Flat(40, 40).MaxRiseAboveChord;
        Check(slow > mid && mid > fast, $"faster launch = flatter arc ({slow:0.00} > {mid:0.00} > {fast:0.00})");
    }

    private static void TestOutOfReach()
    {
        float v = 25f, g = MissileBallistics.AcGravity;
        float maxRange = v * v / g;   // 63.8 m on the level, at 45°
        Check(Flat(maxRange - 0.5f, v).Valid, "just inside v²/g is in reach");
        Check(!Flat(maxRange + 0.5f, v).Valid, "past v²/g is out of reach");
        var edge = Flat(maxRange * 0.9999f, v);
        Check(edge.Valid && Math.Abs(edge.TanTheta - 1f) < 0.05f, "maximum range is flown at 45°");
        Check(!MissileBallistics.Solve(0, 0, 0, 5, 0, 40, 20).Valid, "40 m straight up a 5 m slope at 20 m/s is out of reach");
    }

    private static void TestUphillAndDownhill()
    {
        var up = MissileBallistics.Solve(0, 0, 1, 30, 0, 9, 25);
        var down = MissileBallistics.Solve(0, 0, 9, 30, 0, 1, 25);
        Check(up.Valid && down.Valid, "8 m up / down over 30 m in reach");
        Near(up.HeightAt(1f), 8, 0.01, "uphill arc ends at the aim point");
        Near(down.HeightAt(1f), -8, 0.01, "downhill arc ends at the aim point");
        Check(up.TanTheta > Flat(30, 25).TanTheta, "uphill launches steeper than level");
        Check(down.TanTheta < Flat(30, 25).TanTheta, "downhill launches flatter than level");
        Check(up.MaxRiseAboveChord > 0f && down.MaxRiseAboveChord > 0f, "both still bow above the straight line");
        Check(up.ApexAboveLaunch >= 8f, "uphill apex is at least the aim point");
    }

    private static void TestShortShotIsALine()
    {
        var a = Flat(5, 25);
        Check(a.Valid, "5 m in reach");
        Check(a.MaxRiseAboveChord < 0.06f, $"5 m at 25 m/s rises under 6 cm ({a.MaxRiseAboveChord:0.000})");
    }

    private static void TestVerticalShot()
    {
        var a = MissileBallistics.Solve(3, 3, 0, 3.01f, 3, 6, 25);
        Check(a.Valid && a.Vertical, "near-vertical shot is flown as a line");
        Near(a.HeightAt(0.5f), 3, 0.001, "vertical shot mid height");
        Near(a.MaxRiseAboveChord, 0, 0.0001, "vertical shot has no rise above the line");
    }

    private static void TestClearanceBump()
    {
        Near(MissileBallistics.ClearanceBump(0f, 0.5f), 0, 1e-6, "bump 0 at launch");
        Near(MissileBallistics.ClearanceBump(1f, 0.5f), 0, 1e-6, "bump 0 at the aim point");
        Near(MissileBallistics.ClearanceBump(0.5f, 0.5f), 0.5, 1e-6, "bump = clearance at mid-flight");
        Near(MissileBallistics.ClearanceBump(0.5f, 0f), 0, 1e-6, "no clearance, no bump");
        Near(MissileBallistics.ClearanceBump(0.5f, -1f), 0, 1e-6, "negative clearance ignored");
        var a = Flat(40, 25);
        a.PointAt(0.5f, 0.5f, out _, out _, out float zWith);
        a.PointAt(0.5f, 0f, out _, out _, out float zWithout);
        Near(zWith - zWithout, 0.5, 1e-4, "PointAt applies the bump");
        a.PointAt(1f, 2f, out _, out _, out float zEnd);
        Near(zEnd, 1, 1e-3, "the bump never moves the aim point");
    }

    // A 4 m high corridor, launching from chest height (1 m above the floor): the straight
    // line to a target 40 m away clears, the 25 m/s arrow (apex ≈ 4.5 m) doesn't.
    private static void TestDungeonCeiling()
    {
        const float ceiling = 4f;
        var far = Flat(40, 25);
        var near = Flat(15, 25);
        float farTop = MissileBallistics.MaxPathZ(far, 0f);
        float nearTop = MissileBallistics.MaxPathZ(near, 0.5f);
        Check(farTop > ceiling, $"40 m shot hits a 4 m ceiling (top {farTop:0.00} m)");
        Check(nearTop < ceiling, $"15 m shot clears it even with 0.5 m clearance (top {nearTop:0.00} m)");
        Check(MissileBallistics.MaxPathZ(near, 3f) > MissileBallistics.MaxPathZ(near, 0f) + 2.9f, "clearance raises the tested top");
        Check(float.IsNaN(MissileBallistics.MaxPathZ(Flat(100, 25), 0f)), "out of reach has no path");
    }

    private static void TestSegmentCount()
    {
        Check(MissileBallistics.SegmentCount(0f) == 6, "minimum 6 segments");
        Check(MissileBallistics.SegmentCount(5f) == 6, "5 m → 6 segments");
        Check(MissileBallistics.SegmentCount(25f) == 10, "25 m → 10 segments");
        Check(MissileBallistics.SegmentCount(50f) == 20, "50 m → 20 segments");
        Check(MissileBallistics.SegmentCount(500f) == 24, "capped at 24 segments");
        Check(MissileBallistics.SegmentCount(float.NaN) == 6, "NaN → minimum");
    }

    private static void TestBadInput()
    {
        Check(!MissileBallistics.Solve(0, 0, 0, 10, 0, 0, 0f).Valid, "zero speed is out of reach");
        Check(!MissileBallistics.Solve(0, 0, 0, 10, 0, 0, -5f).Valid, "negative speed is out of reach");
        Check(!MissileBallistics.Solve(0, 0, 0, float.NaN, 0, 0, 25f).Valid, "NaN position is not a shot");
        var bad = MissileBallistics.Solve(0, 0, 0, 10, 0, 0, 0f);
        Near(bad.MaxRiseAboveChord, 0, 0, "invalid arc has no rise");
        Near(bad.ApexAboveLaunch, 0, 0, "invalid arc has no apex");
    }
}
