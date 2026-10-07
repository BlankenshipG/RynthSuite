// ServerFeatureGate.cs — Which worlds get the ILT / infinite-attribute features.
//
// One gate for everything ILT-shaped: the ILT Hub and Mini Remote, the Skills panel's
// Progression planners and the attribute raiser. A world is "on" when:
//   * its name matches one of the server patterns in Settings > Misc (FeatureServerNames,
//     '*' wildcard, case-insensitive; defaults cover InfiniteLeaftide), or
//   * the manual override is ticked (Settings > Misc ForceServerFeatures, or the older
//     per-character ILT Hub "Treat this world as ILT" / "/ra hub force on").
// The gate only decides whether RynthAi ASKS the server; each feature still follows the
// server's own report (IltServerOptions bits), so a shard with /xp off keeps the raiser off.
using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi.IltHub;

internal static class ServerFeatureGate
{
    /// <summary>Patterns a fresh profile starts with (the names the old hard-coded check accepted).</summary>
    public static readonly string[] DefaultServerNames = { "InfiniteLeaftide", "*Leaftide*" };

    /// <summary>World name of the current session; set by RynthAiPlugin (empty before login).</summary>
    public static Func<string>? WorldNameProvider;

    /// <summary>The per-character ILT Hub override (ilt-hub.json); set by the Hub while it exists.</summary>
    public static Func<bool>? HubForceProvider;

    public static string CurrentWorld
    {
        get
        {
            try { return (WorldNameProvider?.Invoke() ?? string.Empty).Trim(); }
            catch { return string.Empty; }
        }
    }

    /// <summary>The patterns in force: the profile's list, or the defaults when there is no profile yet.</summary>
    public static IReadOnlyList<string> Patterns(LegacyUiSettings? settings)
        => settings?.FeatureServerNames ?? (IReadOnlyList<string>)DefaultServerNames;

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

    /// <summary>Is the override on (Settings > Misc, or the per-character Hub checkbox)?</summary>
    public static bool ForceOn(LegacyUiSettings? settings)
    {
        if (settings?.ForceServerFeatures == true) return true;
        try { return HubForceProvider?.Invoke() == true; }
        catch { return false; }
    }

    /// <summary>The gate itself.</summary>
    public static bool IsEnabled(string world, LegacyUiSettings? settings)
        => ForceOn(settings) || NameMatches(world, Patterns(settings), out _);

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

    /// <summary>One status line for Settings > Misc: the world, whether the features are on, and why.</summary>
    public static string Describe(LegacyUiSettings? settings)
    {
        string world = CurrentWorld;
        string shown = world.Length > 0 ? $"'{world}'" : "(not logged in)";
        if (settings?.ForceServerFeatures == true)
            return $"This world {shown}: ON - manual override.";
        if (NameMatches(world, Patterns(settings), out string matched))
            return $"This world {shown}: ON - matches '{matched}'.";
        bool hubForce;
        try { hubForce = HubForceProvider?.Invoke() == true; } catch { hubForce = false; }
        if (hubForce)
            return $"This world {shown}: ON - the ILT Hub's \"Treat this world as ILT\" override.";
        return world.Length > 0
            ? $"This world {shown}: OFF - not in the list. Add it, or tick the override."
            : "This world: not logged in yet.";
    }
}
