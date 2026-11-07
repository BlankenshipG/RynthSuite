// ============================================================================
//  RynthSuite - Plugins/Shared/RynthAiDungeonPathApi.cs
//  The "RynthAi.DungeonPath" interface (2026-10-29): RynthAi's dungeon pathfinder
//  (DungeonPathfinder: the cell graph and doorways read from the client's dats, what
//  the Dungeon Patrol and /ra dunnav walk) offered to other plugins through the host's
//  GetPluginInterface (API v68). RynthNav uses it to walk inside a dungeon to the one
//  portal a route needs when it has never walked there itself. Compiled into both
//  plugins (linked source), so the layout can't drift between them.
//
//  Rules (same as RynthNav.Path):
//  - Append-only: new functions go at the end, and Version goes up. A caller
//    checks StructSize before using a field past the ones it knows.
//  - Call only from the plugin pump thread (OnTick and the events it drains).
//  - Resolve the table again every time it is needed; never keep the pointer
//    across ticks. RynthAi may not be loaded, or may be reloaded.
//  - Every function returns safely (no exception crosses the boundary).
//
//  v2 (2026-10-30): FindPath2 (drops down, cells to avoid, point kinds).
//  v3 (2026-11-07): ObstacleAhead (how high the thing in front is, from the dats: RynthNav
//  jumps over what the character's jump clears and steps round what it doesn't).
//
//  World units: x = east  (landblockX * 192 + local x),
//               y = north (landblockY * 192 + local y),
//               z = height (the pose z). Both ends in the same dungeon landblock.
// ============================================================================

using System.Runtime.InteropServices;

namespace RynthCore.Plugin.Shared;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct RynthAiDungeonPathApiV1
{
    public const string PluginName = "RynthAi";
    public const string InterfaceName = "RynthAi.DungeonPath";
    public const uint InterfaceVersion = 3;

    /// <summary>FindPath2 point kinds: walk on to the next point.</summary>
    public const int KindWalk = 0;
    /// <summary>FindPath2 point kinds: the edge of a drop; the next point is where you land (walk off).</summary>
    public const int KindDrop = 1;

    /// <summary>FindPath: a path was found.</summary>
    public const int ResultOk = 1;
    /// <summary>FindPath: both ends are in the dungeon's cells, but no walk joins them (only a drop, or none).</summary>
    public const int ResultNoPath = 0;
    /// <summary>FindPath: the dats aren't loaded yet, the landblock has no dungeon cells, or an end isn't in a cell.</summary>
    public const int ResultNoData = -1;

    public uint Version;      // InterfaceVersion of the table RynthAi filled
    public uint StructSize;   // sizeof(RynthAiDungeonPathApiV1) as RynthAi built it

    /// <summary>
    /// A walk inside dungeon landblock <c>landblock</c> (0xLLLL) from (sx, sy, sz) to (gx, gy, gz):
    /// through the doorways the cells share, as the Dungeon Patrol walks. Writes the corners as
    /// x,y,z triples into <c>points</c> (room for <c>maxPoints</c> corners), start first and goal
    /// last, and the count into <c>*count</c>. Returns ResultOk, ResultNoPath or ResultNoData.
    /// </summary>
    public delegate* unmanaged[Cdecl]<uint, double, double, double, double, double, double, double*, int, int*, int> FindPath;

    /// <summary>
    /// v2 (2026-10-30): FindPath with more ways and fewer: <c>maxDrop</c> (world units, 0 = none)
    /// lets the walk drop down a ledge or shaft that high (one way: never up); <c>avoid</c> holds
    /// <c>avoidCount</c> x,y,z points (world) whose cells are left out (a locked door's doorway);
    /// <c>kinds</c> (room for <c>maxPoints</c>, may be null) gets each point's KindWalk / KindDrop.
    /// </summary>
    public delegate* unmanaged[Cdecl]<uint, double, double, double, double, double, double, double, double*, int, double*, int*, int, int*, int> FindPath2;

    /// <summary>
    /// v3 (2026-11-07): what stands in front of a walker at (x, y, z) in landcell <c>landcell</c>, looking
    /// <c>headingDeg</c> (0 north, 90 east) up to <c>ahead</c> world units: rays at rising heights through the
    /// static geometry the dats place there (walls, fences, rocks; not doors or creatures, which are live
    /// objects). ResultOk: something is there, and <c>*topAbove</c> is the lowest height above z a ray passes
    /// over it (more than <c>maxHeight</c>: <c>maxHeight</c> + 1, too high). ResultNoPath: nothing static in the
    /// way. ResultNoData: no geometry loaded.
    /// </summary>
    public delegate* unmanaged[Cdecl]<uint, double, double, double, double, double, double, double*, int> ObstacleAhead;
}
