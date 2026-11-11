using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

// A quest run inside a dungeon (2026-11-07, Silentkelpie: "when creating a route for a quest and enters a portal
// to a dungeon, look if a map is loaded if so use that map to then create a route to continue the quest inside
// the dungeon, to fight, pickup items, unlock doors, open doors etc."). RynthOracle's Run this quest sends, through
// the "questloot" broker action, once the character is in a dungeon:
//   "dungeon patrol [| key | key]"            the dungeon's map (its cells from the client's dats, as the Dungeon
//                                             Patrol and /ra dunnav use) walked all over with the macro on:
//                                             fighting, looting (the quest's loot profile), opening doors
//   "dungeon goto <NS> <EW> [| key | key]"    the map's walk to that point (/ra dunnav), the same way
//   "dungeon stop"                            the walk ends; the macro and Open Doors go back as they were
//   "dungeon enter"                           outdoors at a step's place ("Enter the Keep at ..."): the nearest
//                                             portal within QuestPortalRange is used
// The keys are the quest's (a locked door tries them first, then any carried key, then lockpicks:
// DoorInteractionController). No map (no dungeon cells here, the dats not loaded): the reply says so, and the
// run waits for the player. Pump thread.
public sealed partial class RynthAiPlugin
{
    private bool _questDungeonActive;
    private readonly List<string> _questDungeonKeys = new();
    private bool? _questDungeonMacroWas, _questDungeonDoorsWas;

    /// <summary>A quest dungeon verb's answer, or null when <paramref name="line"/> isn't one.</summary>
    private (bool Ok, string Text)? TryQuestDungeon(string line)
    {
        line = (line ?? "").Trim();
        if (!line.StartsWith("dungeon", StringComparison.OrdinalIgnoreCase)) return null;
        string rest = line.Length > 7 ? line.Substring(7).Trim() : "";
        string[] parts = rest.Split('|');
        string[] words = parts[0].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = words.Length > 0 ? words[0].ToLowerInvariant() : "";
        var keys = new List<string>();
        for (int i = 1; i < parts.Length; i++) if (parts[i].Trim().Length > 0) keys.Add(parts[i].Trim());

        var settings = _dashboard?.Settings;
        if (settings == null) return (false, "[RynthAi] Quest: RynthAi's settings aren't loaded yet.");

        if (verb == "stop") return QuestDungeonStop();
        if (verb == "enter")
        {
            // "enter near [hints]": only a portal right here (QuestPortalNearRange), no scan (2026-11-10: a step's
            // coordinates at a dungeon's portal, the next step inside it).
            bool near = words.Length > 1 && words[1].Equals("near", StringComparison.OrdinalIgnoreCase);
            int from = near ? 2 : 1;
            return QuestDungeonEnter(words.Length > from ? string.Join(" ", words, from, words.Length - from) : "", near: near);
        }
        if (verb is not ("patrol" or "goto"))
            return (false, "[RynthAi] questloot dungeon patrol | goto <NS> <EW> | stop  [| key | key] (RynthOracle's quest run sends these)");

        if (!Host.TryGetPlayerPose(out uint cell, out _, out _, out _, out _, out _, out _, out _) || (cell & 0xFFFF) < 0x0100)
            return (false, "[RynthAi] Quest: not inside a dungeon.");
        if (_raycast?.GeometryLoader?.CellDat is not { IsLoaded: true } cellDat)
            return (false, "[RynthAi] Quest: no map of this dungeon yet (the client's dats aren't loaded): walk it yourself, then Next.");
        // An indoor cell in a town's landblock is a building, not a dungeon (2026-11-07, 21:33 at the Tusker Burrow).
        if (_navPlanner != null && !_navPlanner.IsDungeonLandblock(cell >> 16, cellDat))
            return (false, "[RynthAi] Quest: in a building, not a dungeon: nothing to patrol here.");

        // Remember how the macro and Open Doors were, once per run in the dungeon.
        if (!_questDungeonActive)
        {
            _questDungeonMacroWas = settings.IsMacroRunning;
            _questDungeonDoorsWas = settings.OpenDoors;
        }
        _questDungeonActive = true;
        _questDungeonKeys.Clear();
        _questDungeonKeys.AddRange(keys);
        settings.OpenDoors = true;
        string keyNote = keys.Count > 0 ? $"; keys first on locked doors: {string.Join(", ", keys)}" : "";

        if (verb == "goto")
        {
            if (words.Length < 3) return (false, "[RynthAi] questloot dungeon goto <NS> <EW>");
            HandleDungeonNavCommand(new[] { "/ra", "dunnav", words[1], words[2] });
            return (true, $"[RynthAi] Quest: walking the dungeon's map to {words[1]}, {words[2]}, fighting and looting on the way, opening doors{keyNote}.");
        }
        HandleDungeonNavPatrol();
        return (true, $"[RynthAi] Quest: patrolling dungeon 0x{cell >> 16:X4} with its map: fighting, looting the quest's items, opening doors{keyNote}. The quest's stamp or Next ends it.");
    }

    /// <summary>How near the portal of an "Enter ..." step must be (metres).</summary>
    internal const double QuestPortalRange = 60.0;

    // 2026-11-08 ("when you get to the portal spot it should scan to find it and walk to enter the portal"): a portal
    // not in sight yet (drawn a moment after RynthNav arrives, or just out of range) is looked for again every
    // QuestPortalRetryMs for QuestPortalScanMs; the one found is used (ACE walks you to it and in).
    private string? _questPortalHints;
    private long _questPortalNextAt, _questPortalUntil;
    internal const long QuestPortalRetryMs = 1500, QuestPortalScanMs = 20000;
    internal const double QuestPortalNearRange = 15.0;

    // 2026-11-10 ("the route isn't going in the portal like it should"): one use of the portal could be lost (a fight
    // stops ACE's walk to it, the portal still out of reach). Used again every QuestPortalUseAgainMs while the
    // character is still in the same landblock, up to QuestPortalUses times.
    private uint _questPortalUseId, _questPortalUseLb;
    private int _questPortalUses;
    private long _questPortalUseAt;
    internal const long QuestPortalUseAgainMs = 7000;
    internal const int QuestPortalUses = 4;

    /// <summary>Each tick: the portal scan, when one is due. Pump thread.</summary>
    private void TickQuestPortalScan()
    {
        TickQuestPortalUse();
        if (_questPortalHints == null) return;
        long now = Environment.TickCount64;
        if (now < _questPortalNextAt) return;
        string hints = _questPortalHints;
        _questPortalHints = null;
        var (ok, text) = QuestDungeonEnter(hints, scanning: now < _questPortalUntil);
        if (_questPortalHints == null) ChatLine(text);   // found it, or the scan ran out (quiet while scanning)
    }

    /// <summary>
    /// "dungeon enter [word || word]": the nearest portal within QuestPortalRange, one whose name has a word
    /// the step gives ("Hidden", "Keep") before the others (2026-11-07: RynthNav arrives a few metres short).
    /// </summary>
    /// <summary>The portal used for the step: used again while we're still here (see QuestPortalUseAgainMs).</summary>
    private void TickQuestPortalUse()
    {
        if (_questPortalUseId == 0) return;
        long now = Environment.TickCount64;
        if (now - _questPortalUseAt < QuestPortalUseAgainMs) return;
        if (!Host.TryGetPlayerPose(out uint cell, out _, out _, out _, out _, out _, out _, out _) || (cell >> 16) != _questPortalUseLb)
        {
            _questPortalUseId = 0;   // through it (or teleported anyway)
            return;
        }
        if (_questPortalUses >= QuestPortalUses || _objectCache?.GetLandscapeObjects().Any(w => unchecked((uint)w.Id) == _questPortalUseId) != true)
        {
            ChatLine($"[RynthAi] Quest: the portal wasn't taken after {_questPortalUses} tries; walk into it, or Next.");
            _questPortalUseId = 0;
            return;
        }
        _questPortalUses++;
        _questPortalUseAt = now;
        Host.UseFor(_questPortalUseId, "Quest", $"quest run: the step's portal again (try {_questPortalUses})", UseKind.Asked);
    }

    private (bool Ok, string Text) QuestDungeonEnter(string hints, bool scanning = false, bool near = false)
    {
        if (_objectCache == null) return (false, "[RynthAi] Quest: the object cache isn't ready.");
        var words = new List<string>();
        foreach (string h in (hints ?? "").Replace("||", "\u0001").Split('\u0001'))
            if (h.Trim().Length >= 3) words.Add(h.Trim());
        WorldObject? best = null;
        double bestDist = double.MaxValue;
        bool bestNamed = false;
        foreach (WorldObject wo in _objectCache.GetLandscapeObjects())
        {
            if (wo.ObjectClass != RynthCore.Loot.AcObjectClass.Portal) continue;
            double d = _playerId != 0 ? _objectCache.Distance((int)_playerId, wo.Id) : double.MaxValue;
            if (d > (near ? QuestPortalNearRange : QuestPortalRange)) continue;
            bool named = words.Exists(w => wo.Name.Contains(w, StringComparison.OrdinalIgnoreCase));
            if (best != null && (bestNamed && !named || (named == bestNamed && d >= bestDist))) continue;
            best = wo; bestDist = d; bestNamed = named;
        }
        if (best == null && near) return (true, "[RynthAi] Quest: no portal right here.");
        if (best == null)
        {
            // Not in sight yet: scan again for a while (the first try starts the clock).
            long now = Environment.TickCount64;
            if (!scanning) _questPortalUntil = now + QuestPortalScanMs;
            if (now < _questPortalUntil)
            {
                _questPortalHints = hints ?? "";
                _questPortalNextAt = now + QuestPortalRetryMs;
                return (true, $"[RynthAi] Quest: looking for the portal here (scanning for {QuestPortalScanMs / 1000} s).");
            }
            return (false, $"[RynthAi] Quest: no portal within {QuestPortalRange:F0} m here to go in by.");
        }
        _questPortalHints = null;
        Host.UseFor(unchecked((uint)best.Id), "Quest", "quest run: the step goes in by this portal", UseKind.Asked);
        _questPortalUseId = unchecked((uint)best.Id);
        _questPortalUses = 1;
        _questPortalUseAt = Environment.TickCount64;
        _questPortalUseLb = Host.TryGetPlayerPose(out uint pc, out _, out _, out _, out _, out _, out _, out _) ? pc >> 16 : 0;
        return (true, $"[RynthAi] Quest: going in by {best.Name} ({bestDist:F0} m).");
    }

    private (bool Ok, string Text) QuestDungeonStop()
    {
        _questPortalHints = null;   // a portal scan ends with the run's dungeon part
        _questPortalUseId = 0;
        if (!_questDungeonActive) return (true, "[RynthAi] Quest: no dungeon walk to stop.");
        _questDungeonActive = false;
        _questDungeonKeys.Clear();
        var settings = _dashboard?.Settings;
        if (settings != null)
        {
            if (_dunPatrolActive) StopPatrolForServer();
            else { settings.EnableNavigation = false; settings.CurrentRoute = new LegacyUi.NavRouteParser(); _navigationEngine?.Stop(); }
            if (_questDungeonDoorsWas is bool doors) settings.OpenDoors = doors;
            if (_questDungeonMacroWas == false) settings.IsMacroRunning = false;
        }
        _questDungeonMacroWas = _questDungeonDoorsWas = null;
        return (true, "[RynthAi] Quest: the dungeon walk ended; the macro and Open Doors are as they were.");
    }
}
