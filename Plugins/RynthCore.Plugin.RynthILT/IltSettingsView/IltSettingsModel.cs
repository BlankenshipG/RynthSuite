// IltSettingsModel.cs - RynthILT's copy of the Infinite Leaftide settings (the "ILT settings" view).
//
// Nothing here is decided by RynthILT; it shows and edits what others own:
//   * RynthCore owns the world verdict, this character's Auto / On / Off override and the ILT
//     world-name list. RynthILT reads the verdict from its server list (plugin API v86,
//     GetServerStateJson, parsed by Plugins/Shared/EngineServerState.cs): the plugin tick polls
//     it every 2 s and publishes the "ilt" entry here (ApplyServerState). The world names aren't
//     in that list; "/rc server names ilt" prints them. Edits go back as RynthCore commands:
//     "/rc server ilt auto|on|off" and "/rc server names ilt add|remove <pattern>".
//   * RynthILT's own Hub reads which ILT features the server has on; the plugin tick publishes
//     them (PublishFeatures).
// Each is one immutable snapshot published with a single reference write, so the render thread
// always reads a whole one. Pure (no host calls): RynthILT.Tests covers it.
using System;
using System.Collections.Generic;
using System.Threading;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthILT.IltSettingsView;

/// <summary>RynthCore's verdict for Infinite Leaftide.</summary>
internal sealed record IltServerView(string Name, bool Active, string Force, bool Detected, string Reason);

/// <summary>What RynthCore's server list says: the ILT verdict (null if it lists no "ilt").</summary>
internal sealed record IltEngineView(IltServerView? Server);

/// <summary>The Hub's server read: the world, its last check, and each feature (-1 unknown, 0 off, 1 on).</summary>
internal sealed record IltFeatureView(string World, string Source, string LastCheck, bool Checking, bool IltLike,
                                      KeyValuePair<string, int>[] Features);

internal sealed class IltSettingsModel
{
    /// <summary>RynthCore's id for Infinite Leaftide.</summary>
    public const string ServerId = EngineServerState.IltId;

    /// <summary>Longest pattern RynthCore keeps (EngineSettings.MaxIltServerNameLength).</summary>
    public const int MaxPattern = 64;

    /// <summary>The RynthCore command that prints the ILT world names (and hosts) in chat.</summary>
    public const string NamesListCommand = "/rc server names " + ServerId;

    private IltEngineView? _engine;
    private IltFeatureView? _features;

    /// <summary>The last server list from RynthCore (null until one arrives). Any thread.</summary>
    public IltEngineView? Engine => Volatile.Read(ref _engine);

    /// <summary>The last feature read (null until the Hub publishes one). Any thread.</summary>
    public IltFeatureView? Features => Volatile.Read(ref _features);

    /// <summary>RynthCore's server list (GetServerStateJson); null (no answer) keeps the last one. Any thread.</summary>
    public void ApplyServerState(EngineServerState? state)
    {
        IltEngineView? view = FromServerState(state);
        if (view != null) Volatile.Write(ref _engine, view);
    }

    /// <summary>
    /// Older "servermenu" text push (RynthPluginApplyRemoteCommand). No-op here: the verdict now
    /// comes from <see cref="ApplyServerState"/> / IltEngineVerdict. Kept so ApplyRemoteCommand compiles.
    /// </summary>
    public void ApplyEngine(string? payload) { _ = payload; }

    /// <summary>Older "iltfeatures" text push. No-op; features come from the Hub's PublishFeatures.</summary>
    public void ApplyFeatures(string? payload) { _ = payload; }

    /// <summary>The Hub's own feature read (plugin tick); null clears it. Any thread.</summary>
    public void PublishFeatures(IltFeatureView? view) => Volatile.Write(ref _features, view);

    /// <summary>Logout: the next character's world and features come fresh.</summary>
    public void Clear()
    {
        Volatile.Write(ref _engine, null);
        Volatile.Write(ref _features, null);
    }

    /// <summary>The ILT verdict from RynthCore's server list; null for no list.</summary>
    public static IltEngineView? FromServerState(EngineServerState? state)
    {
        if (state == null) return null;
        EngineServer? s = state.Find(ServerId);
        return new IltEngineView(s == null ? null
            : new IltServerView(s.Name.Length > 0 ? s.Name : "Infinite Leaftide", s.Active, NormalizeForce(s.Force), s.Detected, s.Reason));
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
