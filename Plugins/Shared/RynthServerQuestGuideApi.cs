// ============================================================================
//  RynthSuite - Plugins/Shared/RynthServerQuestGuideApi.cs
//  The "RynthServer.QuestGuide" interface (2026-11-21): a server plugin's own wiki
//  quest guide for a quest reader such as RynthOracle, whose wiki tab shows it
//  (with Run this quest, its route and loot profile) without knowing the server.
//  RynthConquest serves the Conquest wiki's quests this way; RynthILT has its own
//  older table ("RynthILT.Server" v2, GetQuestGuideInfoJson) with the same info.
//  The reader asks only the plugin RynthCore names as the active server's owner
//  (GetServerStateJson "activePlugin", host GetPluginInterface), so a guide shows
//  only while its server is the active one. No interface = no server guide.
//  Compiled into the server plugins and their readers (linked source).
//
//  Rules (as the other shared interfaces):
//  - Append-only: new functions go at the end, and Version goes up.
//  - Strings are UTF-8, NUL-terminated.
//  - Every function returns safely (no exception crosses the boundary).
//  - The table is allocated once per loaded copy of the plugin and never freed;
//    shutdown sets Version to 0, and a reader re-queries when it sees that.
//
//  GetGuideInfoJson payload (read leniently; RynthILT's guide info plus "tag"):
//  {
//    "schema": 1,
//    "path": "C:\\...\\cq-guide.json",   // the guide file (QuestGuide JSON, RynthOracle's Data/QuestGuide.cs)
//    "revision": 3,                       // goes up when the file is written again (read it again)
//    "running": false,                    // a read of the wiki is under way
//    "login": true,                       // the guide needs no login (or has one)
//    "status": "317 pages ...",           // a line for the reader to show
//    "tag": "Conquest"                    // the server's display name (the tab's label)
//  }
//  0 / "" = no guide yet.
// ============================================================================

using System.Runtime.InteropServices;

namespace RynthCore.Plugin.Shared;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct RynthServerQuestGuideApiV1
{
    public const string InterfaceName = "RynthServer.QuestGuide";
    public const uint InterfaceVersion = 1;

    public uint Version;      // InterfaceVersion of the table the plugin filled; 0 once it has shut down
    public uint StructSize;   // sizeof(RynthServerQuestGuideApiV1) as the plugin built it

    /// <summary>
    /// GetGuideInfoJson(buffer, capacity): writes the info JSON (UTF-8, NUL-terminated) into buffer when it
    /// fits, and returns its length in bytes without the NUL. A return value of capacity or more means
    /// nothing was written: call again with a bigger buffer. 0 = no guide. Any thread.
    /// </summary>
    public delegate* unmanaged[Cdecl]<byte*, int, int> GetGuideInfoJson;
}
