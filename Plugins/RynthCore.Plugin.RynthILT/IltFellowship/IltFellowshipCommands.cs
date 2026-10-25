// IltFellowshipCommands.cs - Infinite Leaftide's fellowship chat commands (ACECustom /fship).
//
// ACECustom (Source/ACE.Server/Command/Handlers/PlayerCommands.cs, "fship") adds these on top
// of retail's fellowship panel; its fellowships hold up to 29 (Fellowship.MaxFellows). The list
// is kept in the shared "servercommands" form (Plugins/Shared/ServerCommandShare.cs) and drawn by
// RynthILT's fellowship window (IltFellowshipUi, "/ilt fellow").
// A name with spaces is sent quoted; ACE's command parser joins "two words". Pure: no ImGui, no host.
using System;
using System.Collections.Generic;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthILT.IltFellowship;

internal static class IltFellowshipCommands
{
    /// <summary>The RynthCore section the commands belong to.</summary>
    public const string Section = "fellowship";

    /// <summary>RynthCore's server id for Infinite Leaftide.</summary>
    public const string ServerId = "ilt";

    /// <summary>The server's display name.</summary>
    public const string ServerName = "Infinite Leaftide";

    /// <summary>ACECustom's fellowship size (Fellowship.MaxFellows; retail is 9).</summary>
    public const int MaxFellows = 29;

    /// <summary>The /fship commands, in button order.</summary>
    public static readonly IReadOnlyList<ServerCommand> Commands = new[]
    {
        new ServerCommand("Create", "/fship create", "A",
            "Create a fellowship with the name in the box (you lead it). Up to " + MaxFellows + " members on ILT."),
        new ServerCommand("Invite", "/fship add", "a",
            "Invite the player named in the box, or your selected player when the box is empty."),
        new ServerCommand("Dismiss", "/fship remove", "ac",
            "Leader: remove the player named in the box, or your selected player when the box is empty."),
        new ServerCommand("Invite landblock", "/fship landblock", "c",
            "Invite every player in your landblock (not in the Marketplace). Over level 50, only level 50+ players are invited."),
        new ServerCommand("List nearby", "/fship list", "",
            "The fellowships in your area and their leaders (wider in VOD and T10). Once every 10 seconds."),
        new ServerCommand("Leave", "/fship leave", "c",
            "Leave your fellowship."),
        new ServerCommand("Disband", "/fship disband", "c",
            "Leader: disband your fellowship."),
        new ServerCommand("Help", "/fship", "",
            "The server's /fship help."),
    };

    /// <summary>The set while ILT is active; with no commands when it isn't (a receiver drops it).</summary>
    public static ServerCommandSet Set(bool active) =>
        new(Section, ServerId, ServerName, active ? Commands : Array.Empty<ServerCommand>());

    /// <summary>The "servercommands" payload for <see cref="Set"/>.</summary>
    public static string Payload(bool active) => ServerCommandShare.Build(Set(active));

    /// <summary>
    /// ILT is active: RynthCore's verdict once it has sent one (<paramref name="engineActive"/>),
    /// else the world name has "Leaftide" in it (RynthCore's default ILT names).
    /// </summary>
    public static bool IsActive(bool? engineActive, string? worldName) =>
        engineActive ?? (worldName ?? string.Empty).IndexOf("Leaftide", StringComparison.OrdinalIgnoreCase) >= 0;
}
