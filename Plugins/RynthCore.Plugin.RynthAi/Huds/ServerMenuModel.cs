// ServerMenuModel.cs - The server sections RynthCore pushes for the Mini Remote's right-click menu.
//
// RynthCore defines each server it knows (Infinite Leaftide today), whether the client is on it
// (or forced onto it), and the windows its installed plugins add for it (UI/ServerMenus.cs in
// RynthCore). It sends that list as the "servermenu" remote command when it changes, and again
// every 30 s. The payload is one tab-separated line per section ("S", id, name, active 0/1, force
// command), then its items ("I", label, command, tooltip, feature).
//
// Groups stays null until the first push: an older RynthCore, or the ImGui shell off. The menu
// then falls back to RynthAi's own ILT entries.
using System;
using System.Collections.Generic;
using System.Threading;

namespace RynthCore.Plugin.RynthAi.Huds;

/// <summary>A server section item: an engine "/rc" command, or a Mini Remote tab ("tab:&lt;id&gt;" feature).</summary>
internal sealed record ServerMenuEntry(string Label, string Command, string Tooltip, string Feature);

/// <summary>One server's section: shown while <see cref="Active"/> (detected or forced in RynthCore).</summary>
internal sealed record ServerMenuGroup(string Id, string Name, bool Active, string ForceCommand, ServerMenuEntry[] Items);

internal static class ServerMenuModel
{
    /// <summary>Feature prefix for items that are Mini Remote tabs (drawn by RynthAi itself).</summary>
    public const string TabFeaturePrefix = "tab:";

    private static ServerMenuGroup[]? _groups;

    /// <summary>The latest sections from RynthCore; null until it sends any. Any thread.</summary>
    public static ServerMenuGroup[]? Groups => Volatile.Read(ref _groups);

    /// <summary>Pump thread: stores a "servermenu" push. A malformed line is skipped, never thrown.</summary>
    public static void Apply(string payload) => Volatile.Write(ref _groups, Parse(payload));

    /// <summary>
    /// Parses a payload. Items before the first section line, short lines and unknown line kinds
    /// are skipped.
    /// </summary>
    public static ServerMenuGroup[] Parse(string? payload)
    {
        var groups = new List<ServerMenuGroup>();
        if (string.IsNullOrEmpty(payload)) return groups.ToArray();

        string id = "", name = "", force = "";
        bool active = false, open = false;
        var items = new List<ServerMenuEntry>();

        void Close()
        {
            if (open) groups.Add(new ServerMenuGroup(id, name, active, force, items.ToArray()));
            items.Clear();
            open = false;
        }

        foreach (string raw in payload.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            string[] f = line.Split('\t');
            if (f[0] == "S" && f.Length >= 5)
            {
                Close();
                id = f[1];
                name = f[2];
                active = f[3] == "1";
                force = f[4];
                open = id.Length > 0 && name.Length > 0;
            }
            else if (f[0] == "I" && f.Length >= 5 && open && f[1].Length > 0)
            {
                items.Add(new ServerMenuEntry(f[1], f[2], f[3], f[4]));
            }
        }
        Close();
        return groups.ToArray();
    }

    /// <summary>The tab id of a "tab:&lt;id&gt;" feature, else null.</summary>
    public static string? TabOf(string feature) =>
        feature.StartsWith(TabFeaturePrefix, StringComparison.OrdinalIgnoreCase) && feature.Length > TabFeaturePrefix.Length
            ? feature.Substring(TabFeaturePrefix.Length)
            : null;
}
