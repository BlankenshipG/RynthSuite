using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RynthCore.Plugin.RynthNav;

// Learn portals (2026-10-29): RynthNav maps a server's portals by itself. From where you start it
// (the home spot), it scans the portals within the learn range, walks into the nearest one it hasn't
// tried, lets the teleport teach where that portal lands (CustomPortals.cs), comes back to the home
// spot by a route (portals and recalls, whatever the switches say), and goes on with the next one,
// until every portal in range has been tried or you stop it (Stop, any movement key, or
// "/rnav portals learn stop"). The range is editable: "/rnav portals learn range <yd>" and the
// panel's - / + (rynthnav.json "learnRange").
//
// States (tick thread):
//   Pick      at home: scan, choose the nearest untried portal in range (live ones and saved ones
//             without a landing), or finish;
//   Going     walking into it; a teleport means it was taken, the goto ending without one means
//             it wouldn't take you (tried, failed);
//   Landed    a short settle after the teleport (the landing is learned in OnTeleported);
//   Return    the route back to the home spot; arriving there goes back to Pick. Two failed returns
//             stop learning (no route back from where the portal went: a recall it doesn't have).
public sealed partial class RynthNavPlugin
{
    public const double LearnRangeMin = 20, LearnRangeMax = 2000, LearnRangeDefault = 200;
    private const double LearnHomeUnits = 25.0;        // this close to the home spot counts as back
    private const long LearnSettleMs = 3000;           // after a teleport before planning the way back
    private const long LearnGoTimeoutMs = 180_000;     // one portal attempt, walk included
    private const long LearnReturnTimeoutMs = 600_000; // one way back
    private const int LearnMaxPortals = 150;           // a session's cap, so nothing runs forever

    private enum LearnState { Off, Pick, Going, Landed, Return }

    private LearnState _learn = LearnState.Off;
    private bool LearningPortals => _learn != LearnState.Off;
    private double _learnRange = LearnRangeDefault;
    private double _homeNs, _homeEw;
    private string _homeLabel = "";
    private long _learnStepMs;
    private string _learnTarget = "";
    private double _learnTargetNs, _learnTargetEw;
    private int _learnTried, _learnLearned, _learnFailed, _learnReturnFails;
    private readonly HashSet<string> _learnDone = new(StringComparer.OrdinalIgnoreCase);
    private string _learnLine = "";   // the panel's line; read under _gate

    /// <summary>Route planning uses portals: the switch, or while learning (the way back may need them).</summary>
    private bool PortalsRouting => _portalsEnabled || LearningPortals;
    /// <summary>Recalls in routes: the switch, or while learning (the way back out of a dungeon is a recall).</summary>
    private bool RecallsRouting => _recallsEnabled || LearningPortals;

    private void InitLearn() =>
        _learnRange = Math.Clamp(NavDataConfig.ReadNumber("learnRange", LearnRangeDefault), LearnRangeMin, LearnRangeMax);

    private static string LearnKey(string name, double ns, double ew) =>
        string.Create(CultureInfo.InvariantCulture, $"{name}|{ns:0.00}|{ew:0.00}");

    private void SetLearnLine(string text)
    {
        lock (_gate) _learnLine = text;
    }

    // ── Start / stop ───────────────────────────────────────────────────────

    private void StartLearn()
    {
        if (LearningPortals) { Say("already learning portals — /rnav portals learn stop"); return; }
        string server = CurrentServer();
        if (server.Length == 0) { Note("learn portals: not logged in yet (no server name)"); return; }
        if (!_customLoaded) { Note("learn portals: the saved portals are still loading; try again in a moment"); return; }
        if (!Host.HasGetLiveObjectIds || !Host.HasUseObject) { Note("learn portals: this RynthCore engine can't see or use the objects around you"); return; }
        if (!PlayerMapPos(out double ns, out double ew)) { Note("learn portals: stand somewhere on the map (not in a dungeon) to start"); return; }
        _homeNs = ns; _homeEw = ew; _homeLabel = NearLabel(ns, ew);
        _learnDone.Clear();
        _learnTried = _learnLearned = _learnFailed = _learnReturnFails = 0;
        _learn = LearnState.Pick;
        _learnStepMs = NowMs;
        Note($"learning portals within {_learnRange:0} yd of {NavCoords.Fmt(ns, ew)} on {server} — Stop (or any movement key) ends it");
        Host.Log($"[RynthNav] learn: start at {NavCoords.Fmt(ns, ew)} ({_homeLabel}), range {_learnRange:0} yd, server {server}");
        SetLearnLine("starting");
    }

    /// <summary>Ends learning (said in chat and on the panel). Stops a walk it started only when <paramref name="stopWalk"/>.</summary>
    private void StopLearn(string why, bool stopWalk = false)
    {
        if (!LearningPortals) return;
        _learn = LearnState.Off;
        if (stopWalk && _gotoActive) FinishGoto("stopped (learning portals ended)");
        string summary = $"learning portals ended: {why} — tried {_learnTried}, learned {_learnLearned} landing(s), {_learnFailed} wouldn't take you";
        Note(summary);
        Host.Log("[RynthNav] learn: " + summary);
        SetLearnLine(summary);
    }

    private void SetLearnRange(double yards)
    {
        _learnRange = Math.Clamp(Math.Round(yards), LearnRangeMin, LearnRangeMax);
        NavDataConfig.WriteNumber("learnRange", _learnRange);
        Say($"learn range {_learnRange:0} yd" + (LearningPortals ? " (the next pick uses it)" : ""));
    }

    // ── Tick ───────────────────────────────────────────────────────────────

    /// <summary>One step of learning. Tick thread, after the teleport tracker.</summary>
    private void TickLearn(bool teleported)
    {
        if (!LearningPortals) return;
        long now = NowMs;
        if (CurrentServer().Length == 0) { StopLearn("logged out"); return; }
        switch (_learn)
        {
            case LearnState.Pick:
                if (_gotoActive || !_hasPose || now - LastTeleportMs < LearnSettleMs) return;
                PickNextPortal(now);
                break;

            case LearnState.Going:
                // Taken: a teleport since the walk began (the goto's own portal wait may see it first).
                if (teleported || LastTeleportMs >= _learnStepMs)
                {
                    _learn = LearnState.Landed;
                    _learnStepMs = now;
                    SetLearnLine($"took '{_learnTarget}': learning where it lands");
                    return;
                }
                if (_trkPortaling || (Host.HasIsPortaling && Host.IsPortaling())) return;   // in portal space: wait for the landing
                if (!_gotoActive || now - _learnStepMs > LearnGoTimeoutMs)
                {
                    _learnFailed++;
                    Host.Log($"[RynthNav] learn: '{_learnTarget}' didn't take you ({(_gotoActive ? "timed out" : _goNote)})");
                    if (_gotoActive) FinishGoto($"learn: gave up on '{_learnTarget}'");
                    BeginReturn(now);
                }
                break;

            case LearnState.Landed:
                if (now - _learnStepMs < LearnSettleMs || _gotoActive) return;
                CountLearned();
                BeginReturn(now);
                break;

            case LearnState.Return:
                if (_gotoActive && now - _learnStepMs < LearnReturnTimeoutMs) return;
                if (_gotoActive) FinishGoto("learn: the way back timed out");
                if (AtHome())
                {
                    _learnReturnFails = 0;
                    _learn = LearnState.Pick;
                    _learnStepMs = now;
                    SetLearnLine($"back at the start — tried {_learnTried}, learned {_learnLearned}");
                    return;
                }
                if (++_learnReturnFails >= 2) { StopLearn($"no way back to the start from here ({_goNote})"); return; }
                Host.Log($"[RynthNav] learn: not back yet ({_goNote}); trying the way back again");
                BeginReturn(now);
                break;
        }
    }

    private bool AtHome() =>
        PlayerMapPos(out double ns, out double ew) && NavCoords.Distance(ns, ew, _homeNs, _homeEw) <= LearnHomeUnits;

    private void CountLearned()
    {
        foreach (CustomPortal p in _custom.For(CurrentServer()))
            if (p.DestKnown && p.Name.Equals(_learnTarget, StringComparison.OrdinalIgnoreCase)
                && NavCoords.Distance(p.Ns, p.Ew, _learnTargetNs, _learnTargetEw) <= CustomPortalStore.SameUnits * 2)
            { _learnLearned++; return; }
        // Taken, but it wasn't written down: the world data has it, or the portal wasn't seen as portal space began.
        Host.Log($"[RynthNav] learn: took '{_learnTarget}' but no landing was saved for it (the world data has it, or it wasn't the portal beside you)");
    }

    private void BeginReturn(long now)
    {
        if (AtHome())
        {
            _learn = LearnState.Pick;
            _learnStepMs = now;
            return;
        }
        _learn = LearnState.Return;
        _learnStepMs = now;
        SetLearnLine($"going back to the start (tried {_learnTried}, learned {_learnLearned})");
        StartWalk("the learning start", "", _homeNs, _homeEw);
    }

    /// <summary>Scans, then walks into the nearest untried portal in range; none left = done.</summary>
    private void PickNextPortal(long now)
    {
        if (_learnTried >= LearnMaxPortals) { StopLearn($"tried {LearnMaxPortals} portals (the cap for one run)"); return; }
        string server = CurrentServer();
        EnsurePortalsLoaded();

        // Live portals in range: written down (Saw), and candidates while they have no landing yet.
        var cands = new List<(string Name, double Ns, double Ew)>();
        DateTime utc = DateTime.UtcNow;
        foreach (uint id in Host.GetLiveObjectIds())
        {
            if (!LivePortalAt(id, out uint cell, out double wx, out double wy, out string name)) continue;
            if (!IsOnMap(cell) || Entrance.IsDestroyed(name)) continue;
            double ns = NavCoords.NsFromWorld(wy), ew = NavCoords.EwFromWorld(wx);
            if (NavCoords.Distance(ns, ew, _homeNs, _homeEw) > _learnRange) continue;
            Host.TryGetObjectWcid(id, out uint wcid);
            if (_custom.Saw(server, name, wcid, cell, true, ns, ew, _portals, utc) == CustomScanResult.BuiltIn) continue;
            cands.Add((name, ns, ew));
        }
        // Saved ones in range with no landing yet (out of sight from here).
        foreach (CustomPortal p in _custom.For(server))
            if (p.OnMap && !p.DestKnown && NavCoords.Distance(p.Ns, p.Ew, _homeNs, _homeEw) <= _learnRange)
                cands.Add((p.Name, p.Ns, p.Ew));

        PlayerMapPos(out double hereNs, out double hereEw);
        (string Name, double Ns, double Ew)? best = null;
        double bestD = double.MaxValue;
        int left = 0;
        var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cands)
        {
            string key = LearnKey(c.Name, c.Ns, c.Ew);
            if (_learnDone.Contains(key) || !counted.Add(key)) continue;
            if (_custom.Find(server, c.Name, 0, true, c.Ns, c.Ew) is { DestKnown: true }) continue;   // mapped already
            left++;
            double d = NavCoords.Distance(hereNs, hereEw, c.Ns, c.Ew);
            if (d < bestD) { bestD = d; best = c; }
        }
        if (best is not { } next)
        {
            StopLearn($"every portal within {_learnRange:0} yd of the start has been tried");
            return;
        }
        _learnDone.Add(LearnKey(next.Name, next.Ns, next.Ew));
        _learnTried++;
        _learnTarget = next.Name; _learnTargetNs = next.Ns; _learnTargetEw = next.Ew;
        _learn = LearnState.Going;
        _learnStepMs = now;
        SetLearnLine($"trying '{next.Name}' ({_learnTried} tried, {left - 1} more in range)");
        Host.Log($"[RynthNav] learn: trying '{next.Name}' at {NavCoords.Fmt(next.Ns, next.Ew)} ({bestD:0} yd away, {left - 1} more in range)");
        StartWalk(next.Name, "Portal", next.Ns, next.Ew, next.Name, "Portal");
    }

    // ── Panel ──────────────────────────────────────────────────────────────

    // This server's custom portal counts for the panel (tick writes, the pump thread reads).
    private volatile int _customSaved, _customRoutable;
    private (string Server, int Version) _customCountKey = ("", -1);

    /// <summary>Recounts this server's custom portals when the server or the store changed. Tick thread.</summary>
    private void RefreshCustomCounts()
    {
        string server = _server;
        if (_customCountKey == (server, _custom.Version)) return;
        _customCountKey = (server, _custom.Version);
        IReadOnlyList<CustomPortal> list = server.Length > 0 ? _custom.For(server) : Array.Empty<CustomPortal>();
        int routable = 0;
        foreach (CustomPortal p in list) if (p.Routable) routable++;
        _customSaved = list.Count;
        _customRoutable = routable;
    }

    /// <summary>The panel's "learn" object: on, range, the line, this server's counts. Any thread.</summary>
    private void AppendLearnJson(StringBuilder sb)
    {
        var ci = CultureInfo.InvariantCulture;
        string line, server = _server;
        lock (_gate) line = _learnLine;
        int saved = _customSaved, routable = _customRoutable;
        sb.Append(",\"learn\":{\"on\":").Append(LearningPortals ? 1 : 0)
          .Append(",\"range\":").Append(_learnRange.ToString("0", ci))
          .Append(",\"min\":").Append(LearnRangeMin.ToString("0", ci)).Append(",\"max\":").Append(LearnRangeMax.ToString("0", ci))
          .Append(",\"server\":\"").Append(Esc(server)).Append("\",\"saved\":").Append(saved).Append(",\"routable\":").Append(routable)
          .Append(",\"tried\":").Append(_learnTried).Append(",\"learned\":").Append(_learnLearned)
          .Append(",\"line\":\"").Append(Esc(line)).Append("\"}");
    }
}
