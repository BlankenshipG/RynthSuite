using System;
using System.Collections.Generic;
using System.Globalization;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

// Going to someone (or something) INSIDE a dungeon (2026-11-11, Silentkelpie: "some npc can be inside dungeons
// or bur and should route to them correctly"). The Atlas has 2,963 NPCs and 172 vendors in dungeon cells, with no
// map position: a goto by their name used to say "no place called ... on the map". Now the way in is found:
//   1. already in that dungeon: arrived (the quest run walks the rest with RynthAi's dungeon map);
//   2. a portal on the map that leads into that dungeon's landblock: walked to and into (the nearest, one that
//      needs no quest first when there is a choice);
//   3. a recall this character knows that lands in it (Bur Recall into the Bur, a learned Lifestone or Portal
//      Recall): cast.
// The arrival line ends with "(<name> is in cell XXXXXXXX)", which RynthOracle's quest run reads to walk the
// dungeon to that cell. Tick thread.
public sealed partial class RynthNavPlugin
{
    /// <summary>What the goto into a dungeon is after: the person's name and cell (0 = none). Added to the arrival line.</summary>
    private string _insideName = "";
    private uint _insideCell;

    /// <summary>"... (Drogur is in cell 009C0270)": the end of an arrival line for a target inside a dungeon.</summary>
    internal static string InsideTail(string name, uint cell) =>
        string.Create(CultureInfo.InvariantCulture, $" ({name} is in cell {cell:X8})");

    /// <summary>
    /// "a || b || c" for go: the candidates in the order given; the first that is coordinates or a place on the
    /// map is left to the usual go, unless the same name is someone inside a dungeon by its exact name and the
    /// map's place only half matches. False when the usual go should take it.
    /// </summary>
    private bool TryGoInsideAlternative(string text)
    {
        foreach (string raw in text.Split("||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (NavCoords.TrySplitCoordsAndName(raw, out _, out _, out _)) return false;
            AtlasLocation? inside = PreferInside(raw, out bool onMap);
            if (inside != null) return GoInside(inside);
            if (onMap) return false;
        }
        return false;
    }

    /// <summary>A go to one name: someone inside a dungeon when the map has nothing better for it.</summary>
    private bool TryGoInside(string text)
    {
        AtlasLocation? inside = PreferInside(text, out _);
        return inside != null && GoInside(inside);
    }

    /// <summary>
    /// The place inside a dungeon a name means, or null when the map answers it as well or better (a place on the
    /// map with the exact name, or any whole-word match when nothing inside has the exact name).
    /// </summary>
    private AtlasLocation? PreferInside(string name, out bool onMap)
    {
        bool hasPos = PlayerMapPos(out double pns, out double pew);
        AtlasLocation? map = _atlas.FindBest(name, hasPos, pns, pew);
        int mapScore = map == null ? 0 : Atlas.MatchScore(map.Name, name);
        onMap = mapScore >= 60;
        AtlasLocation? inside = _atlas.FindInside(name, CurrentLandblock);
        if (inside == null) return null;
        if (mapScore >= 100) return null;
        bool exact = inside.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase);
        if (onMap && !exact) return null;
        return inside;
    }

    /// <summary>
    /// "/rnav inside a || b": which of these people is in THIS dungeon, and in which cell (RynthOracle's quest run,
    /// already inside, before it walks the dungeon). The answer is the arrival line, or "inside: ..." saying why not.
    /// It never travels.
    /// </summary>
    private void CmdInside(string text)
    {
        uint here = CurrentLandblock;
        bool inDungeon = (_cellId & 0xFFFF) >= 0x100;
        string elsewhere = "";
        foreach (string raw in text.Split("||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            AtlasLocation? l = _atlas.FindInside(raw, here);
            if (l == null || !Atlas.TryCell(l.Cell, out uint cell)) continue;
            if (inDungeon && cell >> 16 == here)
            {
                _gotoName = l.Name;
                Say($"goto: arrived in {InsideDungeonName(here)}: {l.Name} is inside{InsideTail(l.Name, cell)}");
                return;
            }
            if (elsewhere.Length == 0) elsewhere = $"{l.Name} is in {InsideDungeonName(cell >> 16)}, not here";
        }
        Say("inside: " + (elsewhere.Length > 0 ? elsewhere : "none of these is someone inside a dungeon: " + text.Replace("||", ",")));
    }

    /// <summary>The way into <paramref name="place"/>'s dungeon, started. True when it was handled (gone, or why not said).</summary>
    private bool GoInside(AtlasLocation place)
    {
        if (!Atlas.TryCell(place.Cell, out uint cell)) return false;
        uint lb = cell >> 16;
        string dungeon = InsideDungeonName(lb);
        if (!_hasPose) { Note("goto: no player position yet"); return true; }

        // 1. Already there.
        if (CurrentLandblock == lb && (_cellId & 0xFFFF) >= 0x100)
        {
            _gotoName = place.Name;
            Host.Log($"[RynthNav] goto {place.Name}: already in {dungeon} (cell {cell:X8})");
            Say($"goto: arrived in {dungeon}: {place.Name} is inside{InsideTail(place.Name, cell)}");
            return true;
        }

        // 2. A portal on the map into it.
        bool hasPos = PlayerMapPos(out double pns, out double pew);
        AtlasLocation? portal = Entrance.PortalInto(_atlas, lb, hasPos, pns, pew);
        bool inDungeonNow = InDungeon(_cellId) || ((_cellId & 0xFFFF) >= 0x100 && !hasPos);
        if (portal != null && !inDungeonNow)
        {
            _insideName = place.Name; _insideCell = cell;
            _nextEnterPortal = portal.Name;
            Say($"goto: {place.Name} is inside {dungeon}: going in by {portal.Name} at {NavCoords.Fmt(portal.Ns, portal.Ew)}");
            StartGoto(place.Name, place.Type, portal.Ns, portal.Ew, dungeon, "Dungeon");
            return true;
        }

        // 3. A recall that lands in it.
        RecallOption? recall = RecallInto(lb);
        if (recall != null)
        {
            StartRecallInto(place, cell, dungeon, recall);
            return true;
        }

        if (portal != null)
            Note($"goto: {place.Name} is inside {dungeon}; its portal is at {NavCoords.Fmt(portal.Ns, portal.Ew)}, but there's no navmesh where you are: walk out of this dungeon first");
        else
            Note($"goto: {place.Name} is inside {dungeon}, and no portal on the map or recall you know leads in there");
        return true;
    }

    /// <summary>The dungeon's name: the route hub's or the Atlas's dungeon (DungeonName), else where its portals or recalls lead.</summary>
    private string InsideDungeonName(uint lb)
    {
        string n = DungeonName(lb);
        if (!n.StartsWith("dungeon ", StringComparison.Ordinal)) return n;
        foreach (AtlasLocation l in _atlas.Locations)
            if (l.HasDest && l.DestName.Length > 0 && Atlas.TryCell(l.DestCell, out uint dc) && dc >> 16 == lb) return l.DestName;
        foreach (AtlasRecall r in _atlas.Recalls)
            if (!r.OnMap && r.Cell >> 16 == lb && r.In.Length > 0) return r.In;
        return lb == 0x009C ? "the Bur" : n;
    }

    /// <summary>A recall in this character's spellbook (or a learned personal one) that lands in landblock <paramref name="lb"/>.</summary>
    private RecallOption? RecallInto(uint lb)
    {
        if (!RecallsRouting || _known == null) return null;
        foreach (AtlasRecall r in _atlas.Recalls)
            if (!r.OnMap && r.Cell != 0 && r.Cell >> 16 == lb && _known.Contains(r.SpellId))
                return new RecallOption { Name = r.Name, SpellId = r.SpellId, CastSeconds = RecallPlanner.SpellCastSeconds, Into = r.In };
        (int Spell, string Name, RecallSlot Slot)[] personal =
        {
            (RecallPlanner.LifestoneRecall, "Lifestone Recall", RecallSlot.Lifestone),
            (RecallPlanner.PortalRecall, "Portal Recall", RecallSlot.LastPortal),
            (RecallPlanner.PrimaryPortalTieRecall, "Primary Portal Tie Recall", RecallSlot.Tie1),
            (RecallPlanner.SecondaryPortalTieRecall, "Secondary Portal Tie Recall", RecallSlot.Tie2),
        };
        foreach (var (spell, name, slot) in personal)
        {
            if (!_known.Contains(spell)) continue;
            RecallSpot? spot = _memory.Get(_charKey, slot);
            if (spot != null && !spot.OnMap && spot.Cell != 0 && spot.Cell >> 16 == lb)
                return new RecallOption { Name = name, SpellId = spell, CastSeconds = RecallPlanner.SpellCastSeconds, Learned = true, Into = spot.Label };
        }
        return null;
    }

    /// <summary>A goto of one recall step: cast it, wait for the teleport, arrive (the usual recall step and settle).</summary>
    private void StartRecallInto(AtlasLocation place, uint cell, string dungeon, RecallOption recall)
    {
        if (_gotoActive) { _gotoActive = false; _route = null; _portalWait = false; ResetHub(); AutoRun.Stop(); HideMarkers("a new goto"); }
        if (_recallWait) EndRecall();
        _goJobId = 0;
        _landReplanned = false; _enterPortal = ""; _nextEnterPortal = "";
        _portalWait = false; _portalWaitTicks = 0; _wasPortaling = false; _settling = false;
        _enterName = ""; _enterType = "";
        _finalTew = _wx; _finalTns = _wy;
        ResetHub();
        _routeRecalls = new List<RecallOption> { recall };
        bool hasPos = PlayerMapPos(out double pns, out double pew);
        _route = new List<RouteStep> { new(hasPos ? pns : 0, hasPos ? pew : 0, usePortal: false, useRecall: true, recall.Name, 0, double.NaN, double.NaN) };
        _routeIdx = 0;
        _insideName = place.Name; _insideCell = cell;
        _gotoActive = true;
        _gotoName = place.Name; _gotoType = place.Type;
        _walkHaveLast = false;
        AutoRun.Forget();
        Host.Log($"[RynthNav] goto {place.Name} (inside {dungeon}, cell {cell:X8}) by {recall.Name}");
        Say($"goto: {place.Name} is inside {dungeon}: casting {recall.Name} — /rnav stop to cancel");
        BeginRecallStep();
    }

    /// <summary>FinishGoto: the arrival line's tail for a target inside a dungeon (and the target forgotten).</summary>
    private string TakeInsideTail(string status)
    {
        string name = _insideName;
        uint cell = _insideCell;
        _insideName = ""; _insideCell = 0;
        if (cell == 0 || !status.StartsWith("arrived", StringComparison.Ordinal)) return "";
        if (CurrentLandblock != cell >> 16) return "";
        return InsideTail(name, cell);
    }
}
