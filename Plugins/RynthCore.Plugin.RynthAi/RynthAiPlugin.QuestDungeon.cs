using System;
using System.Collections.Generic;
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
        if (verb == "enter") return QuestDungeonEnter(words.Length > 1 ? string.Join(" ", words, 1, words.Length - 1) : "");
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
    internal const double QuestPortalRange = 40.0;

    /// <summary>
    /// "dungeon enter [word || word]": the nearest portal within QuestPortalRange, one whose name has a word
    /// the step gives ("Hidden", "Keep") before the others (2026-11-07: RynthNav arrives a few metres short).
    /// </summary>
    private (bool Ok, string Text) QuestDungeonEnter(string hints)
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
            if (d > QuestPortalRange) continue;
            bool named = words.Exists(w => wo.Name.Contains(w, StringComparison.OrdinalIgnoreCase));
            if (best != null && (bestNamed && !named || (named == bestNamed && d >= bestDist))) continue;
            best = wo; bestDist = d; bestNamed = named;
        }
        if (best == null) return (false, $"[RynthAi] Quest: no portal within {QuestPortalRange:F0} m here to go in by.");
        Host.UseFor(unchecked((uint)best.Id), "Quest", "quest run: the step goes in by this portal", UseKind.Asked);
        return (true, $"[RynthAi] Quest: going in by {best.Name} ({bestDist:F0} m).");
    }

    private (bool Ok, string Text) QuestDungeonStop()
    {
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
