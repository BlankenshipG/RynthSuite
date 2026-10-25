// ServerFeatureGate.cs — Which worlds get the ILT / infinite-attribute features.
// Originally part of SilentKelpie's ILT Hub in RynthAi; moved to RynthILT 2026-10-13.
//
// One gate for everything ILT-shaped: the Mini Remote's ILT tabs, the Guardian, the Skills
// panel's Progression planners and the attribute raiser. A world is "on" when:
//   * its name matches one of the server patterns (IltSettings.ServerNames, '*' wildcard,
//     case-insensitive; defaults cover InfiniteLeaftide; "/ilt servers"
//     edits them — they were RynthAi's Settings > Misc "Server Features" list), or
//   * this character's override is on ("Treat this world as ILT",
//     "/ra hub force on"; also set by migration from RynthAi's "Force enable" profile flag).
// The gate only decides whether RynthILT ASKS the server; each feature still follows the
// server's own report (IltServerOptions bits), so a shard with /xp off keeps the raiser off.
using System;
using System.Collections.Generic;
using System.Linq;

namespace RynthCore.Plugin.RynthILT.Hub;

internal static class ServerFeatureGate
{
    /// <summary>Patterns a fresh install starts with (the names the old hard-coded check accepted).</summary>
    public static readonly string[] DefaultServerNames = { "InfiniteLeaftide", "*Leaftide*" };

    /// <summary>The patterns in force (null settings: the defaults).</summary>
    public static IReadOnlyList<string> Patterns(IltSettings? settings)
        => settings?.ServerNames is { Count: > 0 } list ? list : DefaultServerNames;

    /// <summary>True when <paramref name="world"/> matches one of <paramref name="patterns"/>.</summary>
    public static bool NameMatches(string world, IEnumerable<string>? patterns, out string matched)
    {
        matched = string.Empty;
        if (string.IsNullOrWhiteSpace(world) || patterns == null) return false;
        foreach (string p in patterns)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (!Matches(world.Trim(), p.Trim())) continue;
            matched = p.Trim();
            return true;
        }
        return false;
    }

    /// <summary>Case-insensitive match where '*' stands for any run of characters (anywhere in the pattern).</summary>
    public static bool Matches(string text, string pattern)
    {
        if (pattern.Length == 0) return false;
        if (!pattern.Contains('*')) return text.Equals(pattern, StringComparison.OrdinalIgnoreCase);

        string[] parts = pattern.Split('*');
        int pos = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.Length == 0) continue;
            if (i == 0)
            {
                if (!text.StartsWith(part, StringComparison.OrdinalIgnoreCase)) return false;
                pos = part.Length;
                continue;
            }
            if (i == parts.Length - 1)
                return text.Length - pos >= part.Length && text.EndsWith(part, StringComparison.OrdinalIgnoreCase);
            int at = text.IndexOf(part, pos, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            pos = at + part.Length;
        }
        return true;
    }

    /// <summary>
    /// RynthILT's own gate: the character's override, or the world name in the list. RynthCore's
    /// verdict, once it sends one, takes over in IltServerOptions.IsIltLikeWorld; this stays ours
    /// (it is what the status export reports, IltServerOptions.IsIltLikeWorldLocal).
    /// </summary>
    public static bool IsEnabled(string world, IltSettings? settings, bool forceOn)
        => forceOn || NameMatches(world, Patterns(settings), out _);

    /// <summary>"InfiniteLeaftide, *Leaftide*" → trimmed, de-duplicated list (case-insensitive).</summary>
    public static List<string> ParseList(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (string raw in text.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string p = raw.Trim();
            if (p.Length > 0 && !list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p);
        }
        return list;
    }

    public static string FormatList(IEnumerable<string>? patterns)
        => patterns == null ? string.Empty : string.Join(", ", patterns.Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <summary>One status line: the world, whether the features are on, and why.</summary>
    public static string Describe(string world, IltSettings? settings, bool forceOn)
    {
        world = (world ?? string.Empty).Trim();
        string shown = world.Length > 0 ? $"'{world}'" : "(not logged in)";
        var engine = IltSettingsView.IltEngineVerdict.Server;
        if (engine != null)
        {
            string why = engine.Force == "on" ? "forced on for this character"
                : engine.Force == "off" ? "forced off for this character"
                : engine.Detected ? "RynthCore recognises the world" : "RynthCore doesn't recognise the world";
            return $"This world {shown}: {(engine.Active ? "ON" : "OFF")} - {why}.";
        }
        if (NameMatches(world, Patterns(settings), out string matched))
            return $"This world {shown}: ON - matches '{matched}'.";
        if (forceOn)
            return $"This world {shown}: ON - \"Treat this world as ILT\" is ticked.";
        return world.Length > 0
            ? $"This world {shown}: OFF - not in the list. Add it, or tick \"Treat this world as ILT\"."
            : "This world: not logged in yet.";
    }
}
