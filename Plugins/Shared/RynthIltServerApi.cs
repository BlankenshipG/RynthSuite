// ============================================================================
//  RynthSuite - Plugins/Shared/RynthIltServerApi.cs
//  The "RynthILT.Server" interface: what RynthILT knows about the Infinite Leaftide
//  server, for any plugin that wants it without drawing RynthILT's UI (RynthOracle's
//  Fellowship tab, for one). One call hands out a JSON snapshot: whether this is an
//  ILT world, every Leaftide feature RynthILT supports with the server's state for it,
//  and the server's fellowship rules.
//  Separate from "RynthILT.Hub" (RynthIltHubApi.cs) so that interface, which RynthAi asks
//  for at an exact version, never has to change for this.
//  Compiled into RynthILT and its readers (linked source), so the layout can't drift.
//
//  Rules (as the other shared interfaces):
//  - Append-only: new functions go at the end, and Version goes up.
//  - Strings are UTF-8, NUL-terminated. The JSON itself is plain ASCII.
//  - Every function returns safely (no exception crosses the boundary).
//  - The table is allocated once per loaded copy of RynthILT and never freed; shutdown
//    sets Version to 0, and a reader re-queries (host GetPluginInterface) when it sees that.
//
//  GetProfileJson payload (every field may be missing on an older RynthILT; read leniently):
//  {
//    "ilt": true,                         // ILT world (its world list, or the character's override)
//    "world": "InfiniteLeaftide",         // the world name the server announced ("" before login)
//    "forced": false,                     // "Treat this world as ILT" is on
//    "source": "probe",                   // where the feature bits came from ("ilt-features", "probe", "")
//    "checked": "2026-10-13T21:25:37Z",   // last server-options check (UTC), "" = never
//    "features": [ { "key": "bank", "label": "Banking", "group": "Character",
//                    "state": 1, "where": "Mini Remote > Banking tab" }, ... ],
//                                         // state: 1 on, 0 off, -1 not known yet
//    "fellowship": { "maxMembers": 29, "zoneShare": 1, "blackjack": 0, "luminanceShare": 1 }
//  }
// ============================================================================

using System.Runtime.InteropServices;

namespace RynthCore.Plugin.Shared;

/// <summary>Constants of the "RynthILT.Server" interface.</summary>
public static class RynthIltServer
{
    /// <summary>The providing plugin's display name.</summary>
    public const string PluginName = "RynthILT";

    /// <summary>Feature state in the profile JSON: the server reports it on.</summary>
    public const int StateOn = 1;
    /// <summary>Feature state: the server reports it off.</summary>
    public const int StateOff = 0;
    /// <summary>Feature state: not known yet (not checked, not an ILT world, or no RynthILT).</summary>
    public const int StateUnknown = -1;

    /// <summary>A retail fellowship's member limit (what a reader assumes without RynthILT).</summary>
    public const int RetailFellowshipMax = 9;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct RynthIltServerApiV1
{
    public const string InterfaceName = "RynthILT.Server";
    public const uint InterfaceVersion = 1;

    public uint Version;      // InterfaceVersion of the table RynthILT filled; 0 once it has shut down
    public uint StructSize;   // sizeof(RynthIltServerApiV1) as RynthILT built it

    /// <summary>
    /// GetProfileJson(buffer, capacity): writes the profile JSON (UTF-8, NUL-terminated) into
    /// buffer when it fits, and returns its length in bytes without the NUL. A return value of
    /// capacity or more means nothing was written: call again with a bigger buffer. 0 = nothing
    /// to report (RynthILT shutting down). Any thread (it hands out a snapshot RynthILT's pump
    /// rebuilds about once a second).
    /// </summary>
    public delegate* unmanaged[Cdecl]<byte*, int, int> GetProfileJson;
}
