// IltSettingsModel.cs - RynthILT's copy of the Infinite Leaftide settings (the "ILT settings" view).
//
// Nothing here is decided by RynthILT; it shows and edits what others own:
//   * RynthCore (2026.10.5.47+) owns the world verdict, this character's Auto / On / Off override
//     and the ILT world-name list. It pushes them as "servermenu" (UI/ServerMenus.cs in RynthCore):
//       V  id  name  active 0/1  force auto|on|off  detected 0/1  reason
//       N  id  pattern  default|engine.json
//     (other line kinds are RynthAi's menus and are skipped). Edits go back as RynthCore commands:
//     "/rc server ilt auto|on|off" and "/rc server names ilt add|remove <pattern>".
//   * RynthAi (0.5.56+) reads which ILT features the server has on and forwards them as
//     "iltfeatures" (RynthAi IltHub/IltFeatureShare.cs):
//       W  world
//       R  source  last-check  checking 0/1  ilt-like 0/1
//       F  key  -1|0|1
// Both arrive through RynthPluginApplyRemoteCommand on whichever thread sent them; each is parsed
// into one immutable snapshot and published with a single reference write, so the render thread
// always reads a whole one. Pure (no host calls): RynthILT.Tests covers it.
using System;
using System.Collections.Generic;
using System.Threading;

namespace RynthCore.Plugin.RynthILT.IltSettings;

/// <summary>RynthCore's verdict for Infinite Leaftide.</summary>
internal sealed record IltServerView(string Name, bool Active, string Force, bool Detected, string Reason);

/// <summary>What RynthCore sent: the ILT verdict (null if it lists no "ilt") and its world names.</summary>
internal sealed record IltEngineView(IltServerView? Server, string[] Names, bool NamesDefault);

/// <summary>What RynthAi sent: the world, its last server check, and each feature (-1 unknown, 0 off, 1 on).</summary>
internal sealed record IltFeatureView(string World, string Source, string LastCheck, bool Checking, bool IltLike,
                                      KeyValuePair<string, int>[] Features);

internal sealed class IltSettingsModel
{
    /// <summary>RynthCore's id for Infinite Leaftide.</summary>
    public const string ServerId = "ilt";

    /// <summary>Longest pattern RynthCore keeps (EngineSettings.MaxIltServerNameLength).</summary>
    public const int MaxPattern = 64;

    /// <summary>Payloads longer than this are not ours (both are a few hundred bytes).</summary>
    public const int MaxPayload = 64 * 1024;

    private const int MaxNames = 64;
    private const int MaxFeatures = 128;

    private IltEngineView? _engine;
    private IltFeatureView? _features;

    /// <summary>The last RynthCore push (null until one arrives). Any thread.</summary>
    public IltEngineView? Engine => Volatile.Read(ref _engine);

    /// <summary>The last RynthAi feature copy (null until one arrives). Any thread.</summary>
    public IltFeatureView? Features => Volatile.Read(ref _features);

    /// <summary>A RynthCore "servermenu" push. Any thread.</summary>
    public void ApplyEngine(string? payload)
    {
        IltEngineView? view = ParseEngine(payload);
        if (view != null) Volatile.Write(ref _engine, view);
    }

    /// <summary>A RynthAi "iltfeatures" copy. Any thread.</summary>
    public void ApplyFeatures(string? payload)
    {
        IltFeatureView? view = ParseFeatures(payload);
        if (view != null) Volatile.Write(ref _features, view);
    }

    /// <summary>Logout: the next character's world and features come fresh.</summary>
    public void Clear()
    {
        Volatile.Write(ref _engine, null);
        Volatile.Write(ref _features, null);
    }

    /// <summary>The ILT verdict and world names from a "servermenu" payload; null for nothing usable.</summary>
    public static IltEngineView? ParseEngine(string? payload)
    {
        if (string.IsNullOrEmpty(payload) || payload.Length > MaxPayload) return null;
        IltServerView? server = null;
        var names = new List<string>();
        bool namesDefault = false, sawServers = false;
        foreach (string raw in payload.Split('\n'))
        {
            string[] f = raw.TrimEnd('\r').Split('\t');
            if (f[0] == "V" && f.Length >= 6)
            {
                sawServers = true;
                if (!f[1].Equals(ServerId, StringComparison.OrdinalIgnoreCase)) continue;
                server = new IltServerView(f[2], f[3] == "1", NormalizeForce(f[4]), f[5] == "1", f.Length >= 7 ? f[6] : string.Empty);
            }
            else if (f[0] == "N" && f.Length >= 3 && f[1].Equals(ServerId, StringComparison.OrdinalIgnoreCase)
                     && f[2].Length > 0 && names.Count < MaxNames)
            {
                names.Add(f[2]);
                namesDefault = f.Length >= 4 && f[3] == "default";
            }
        }
        return sawServers ? new IltEngineView(server, names.ToArray(), namesDefault) : null;
    }

    /// <summary>The features from an "iltfeatures" payload; null for nothing usable.</summary>
    public static IltFeatureView? ParseFeatures(string? payload)
    {
        if (string.IsNullOrEmpty(payload) || payload.Length > MaxPayload) return null;
        string world = string.Empty, source = string.Empty, last = string.Empty;
        bool checking = false, iltLike = false, any = false;
        var features = new List<KeyValuePair<string, int>>();
        foreach (string raw in payload.Split('\n'))
        {
            string[] f = raw.TrimEnd('\r').Split('\t');
            if (f[0] == "W" && f.Length >= 2) { world = f[1]; any = true; }
            else if (f[0] == "R" && f.Length >= 5)
            {
                source = f[1];
                last = f[2];
                checking = f[3] == "1";
                iltLike = f[4] == "1";
                any = true;
            }
            else if (f[0] == "F" && f.Length >= 3 && f[1].Length > 0 && features.Count < MaxFeatures
                     && int.TryParse(f[2], out int v))
            {
                features.Add(new KeyValuePair<string, int>(f[1], Math.Clamp(v, -1, 1)));
                any = true;
            }
        }
        return any ? new IltFeatureView(world, source, last, checking, iltLike, features.ToArray()) : null;
    }

    /// <summary>"auto", "on" or "off" (anything else is shown as auto).</summary>
    public static string NormalizeForce(string? force) => (force ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "on" => "on",
        "off" => "off",
        _ => "auto",
    };

    /// <summary>The RynthCore command that sets this character's override; "" for an unknown mode.</summary>
    public static string ForceCommand(string mode) => mode switch
    {
        "auto" or "on" or "off" => "/rc server " + ServerId + " " + mode,
        _ => string.Empty,
    };

    /// <summary>
    /// The RynthCore command that adds or removes a world-name pattern, or "" (with
    /// <paramref name="error"/>) when the pattern can't be sent: empty, longer than
    /// <see cref="MaxPattern"/>, or with a control character or '/' (it is spliced into a command).
    /// </summary>
    public static string NamesCommand(bool add, string? pattern, out string error)
    {
        error = string.Empty;
        string p = (pattern ?? string.Empty).Trim();
        if (p.Length == 0) { error = "Enter a world name ('*' matches any text)."; return string.Empty; }
        if (p.Length > MaxPattern) { error = $"At most {MaxPattern} characters."; return string.Empty; }
        foreach (char c in p)
            if (char.IsControl(c) || c == '/' || c >= 128)
            {
                error = "World names are plain text (no '/', line breaks or special characters).";
                return string.Empty;
            }
        return "/rc server names " + ServerId + (add ? " add " : " remove ") + p;
    }

    /// <summary>A feature's state as text.</summary>
    public static string StateText(int value) => value switch { 1 => "on", 0 => "off", _ => "unknown" };
}
