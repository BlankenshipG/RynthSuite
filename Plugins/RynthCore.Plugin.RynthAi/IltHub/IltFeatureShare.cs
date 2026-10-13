// IltFeatureShare.cs - The ILT feature bits RynthAi read from the server, as text for RynthILT.
//
// RynthILT (0.1.1+) shows a copy of the ILT settings. The world verdict, this character's
// override and the world names come from RynthCore's "servermenu" push; which ILT features the
// server has on only RynthAi knows (IltServerOptions probes the server once a login). RynthAi
// sends them through the engine's plugin-command broker (SendPluginCommand "RynthILT",
// "iltfeatures", <payload>), so the server isn't asked twice. ANSI, tab-separated lines:
//   W  world
//   R  source  last-check (local "yyyy-MM-dd HH:mm", "" = never)  checking 0/1  ilt-like 0/1
//   F  key  -1 unknown | 0 off | 1 on                                one per feature
// Pure: RynthILT's tests compile this file to check its reader against it.
using System;
using System.Collections.Generic;
using System.Text;

namespace RynthCore.Plugin.RynthAi.IltHub;

internal static class IltFeatureShare
{
    /// <summary>The broker action RynthILT reads.</summary>
    public const string Action = "iltfeatures";

    /// <summary>The plugin that keeps the copy (matched by name).</summary>
    public const string Target = "RynthILT";

    /// <summary>Most features sent (the server reports a few dozen).</summary>
    public const int MaxFeatures = 128;

    /// <summary>The payload for the given state. Keys and text lose tabs, line breaks and non-ASCII.</summary>
    public static string Build(string world, string source, DateTime lastCheckUtc, bool checking, bool iltLike,
                               IEnumerable<KeyValuePair<string, int>> bits)
    {
        var sb = new StringBuilder(512);
        sb.Append('W').Append('\t').Append(Clean(world)).Append('\n');
        string when = lastCheckUtc == DateTime.MinValue ? string.Empty : lastCheckUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        sb.Append('R').Append('\t').Append(Clean(source)).Append('\t').Append(when).Append('\t')
          .Append(checking ? '1' : '0').Append('\t').Append(iltLike ? '1' : '0').Append('\n');
        int n = 0;
        foreach (var kv in bits)
        {
            if (++n > MaxFeatures) break;
            string key = Clean(kv.Key);
            if (key.Length == 0) continue;
            sb.Append('F').Append('\t').Append(key).Append('\t').Append(Math.Clamp(kv.Value, -1, 1)).Append('\n');
        }
        return sb.ToString();
    }

    private static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
            sb.Append(c is '\t' or '\n' or '\r' ? ' ' : c < 128 ? c : '?');
        return sb.ToString().Trim();
    }
}
