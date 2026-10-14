// ============================================================================
//  RynthSuite - Plugins/Shared/EngineServerState.cs
//  RynthCore's answer to "which server is this?" (plugin API v86,
//  GetServerStateJson), parsed: every server the engine knows (built-in ILT and
//  Aelrynth, plus each loaded server plugin's declaration), whether it is
//  active, this character's override, and which plugin owns it (the one to ask
//  for its settings: "RynthServer.Profile", or RynthILT's "RynthILT.Server").
//  The same verdict the Mini Remote, the menus and the Diagnostics server
//  picker use, so every plugin follows one choice, overrides included.
//
//  Pure (BCL only): the plugins' test tools compile it. ServerStateClient.cs
//  polls the engine and the owner plugin.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace RynthCore.Plugin.Shared;

/// <summary>One server in the engine's list.</summary>
internal sealed record EngineServer(string Id, string Name, bool Active, bool Detected, string Force, bool BuiltIn,
                                    string Plugin, string Reason);

/// <summary>The engine's server list at one moment (GetServerStateJson, schema 1).</summary>
internal sealed class EngineServerState
{
    /// <summary>Engine server ids with a meaning across RynthSuite.</summary>
    public const string IltId = "ilt";
    public const string AelrynthId = "aelrynth";
    /// <summary>RynthILT serves "RynthILT.Server" for the "ilt" server, not "RynthServer.Profile".</summary>
    public const string IltPlugin = "RynthILT";

    public int Revision { get; private init; }
    /// <summary>The connect host the client was launched with ("" when not known).</summary>
    public string Host { get; private init; } = string.Empty;
    /// <summary>The world name the server announced at login ("" before, or without the hook).</summary>
    public string World { get; private init; } = string.Empty;
    /// <summary>"auto", a server id (picked on the Diagnostics page or "/rc server pick"), or "mixed".</summary>
    public string Pick { get; private init; } = "auto";
    /// <summary>The active server's id, "" when none is (a retail-like world).</summary>
    public string Active { get; private init; } = string.Empty;
    public string ActiveName { get; private init; } = string.Empty;
    /// <summary>The plugin that owns the active server ("" when none is loaded).</summary>
    public string ActivePlugin { get; private init; } = string.Empty;
    public IReadOnlyList<EngineServer> Servers { get; private init; } = Array.Empty<EngineServer>();

    /// <summary>True when a server is active.</summary>
    public bool HasActive => Active.Length > 0;

    /// <summary>True when the active server was chosen by hand (a pick), not detected.</summary>
    public bool Picked => !Pick.Equals("auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>The server with <paramref name="id"/>, or null when the engine doesn't list it.</summary>
    public EngineServer? Find(string id)
    {
        foreach (EngineServer s in Servers)
            if (s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    /// <summary>True when <paramref name="id"/> is the active server.</summary>
    public bool IsActive(string id) => Active.Equals(id, StringComparison.OrdinalIgnoreCase) && Active.Length > 0;

    /// <summary>
    /// Whether this world is one of <paramref name="names"/> (a user's server list, "InfiniteLeaftide,
    /// Conquest"): names compare case- and space-insensitively against server ids, names and the world.
    /// Without the engine (<paramref name="engine"/> null) the world name decides. With it, an active
    /// server decides ("ilt" counts as any Leaftide name); with none active the world name still counts,
    /// unless it names a server the engine lists, which is then off on purpose (not detected, or this
    /// character's override).
    /// </summary>
    public static bool MatchesNameList(EngineServerState? engine, string? world, IReadOnlyList<string> names)
    {
        bool Named(string? s)
        {
            string n = Norm(s);
            if (n.Length == 0) return false;
            foreach (string name in names)
                if (Norm(name) == n) return true;
            return false;
        }
        bool anyLeaftide = false;
        foreach (string name in names)
            if (Norm(name).Contains("leaftide", StringComparison.Ordinal)) anyLeaftide = true;

        if (engine == null) return Named(world);
        if (engine.HasActive)
            return Named(engine.Active) || Named(engine.ActiveName) || (anyLeaftide && engine.IsActive(IltId));
        if (!Named(world)) return false;
        string w = Norm(world);
        foreach (EngineServer s in engine.Servers)
        {
            if (Norm(s.Id) == w || Norm(s.Name) == w) return false;
            // ILT's world names ("InfiniteLeaftide") are the engine's own ILT detection.
            if (s.Id.Equals(IltId, StringComparison.OrdinalIgnoreCase) && w.Contains("leaftide", StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>Lower case without spaces, so "Infinite Leaftide" equals "InfiniteLeaftide".</summary>
    private static string Norm(string? s) => (s ?? string.Empty).Replace(" ", string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// Parses GetServerStateJson output. Null for empty or unusable text, or another schema's.
    /// Missing fields read as empty; nothing throws.
    /// </summary>
    public static EngineServerState? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Int(root, "schema") != 1) return null;

            var servers = new List<EngineServer>();
            if (root.TryGetProperty("servers", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in list.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    string id = Str(e, "id");
                    if (id.Length == 0) continue;
                    servers.Add(new EngineServer(id, Str(e, "name"), Bool(e, "active"), Bool(e, "detected"),
                        Str(e, "force"), Bool(e, "builtIn"), Str(e, "plugin"), Str(e, "reason")));
                }
            }

            return new EngineServerState
            {
                Revision = Int(root, "revision"),
                Host = Str(root, "host"),
                World = Str(root, "world"),
                Pick = Str(root, "pick") is { Length: > 0 } pick ? pick : "auto",
                Active = Str(root, "active"),
                ActiveName = Str(root, "activeName"),
                ActivePlugin = Str(root, "activePlugin"),
                Servers = servers,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? string.Empty) : string.Empty;

    internal static bool Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

    internal static int Int(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
}

/// <summary>
/// The headline facts of a server owner's profile JSON ("RynthServer.Profile" from a server plugin,
/// or RynthILT's "RynthILT.Server"), read leniently: a field the owner doesn't send stays "".
/// </summary>
internal sealed record ServerProfileView(string Id, string Name, string Plugin, string Ruleset, string Emulator,
                                         string Botting, string BottingNote, string AccountLimit, bool Ilt, int FeaturesOn)
{
    /// <summary>Null for empty or unusable text.</summary>
    public static ServerProfileView? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            // RynthILT's profile: its feature list, state 1 = on.
            int on = 0;
            if (root.TryGetProperty("features", out JsonElement features) && features.ValueKind == JsonValueKind.Array)
                foreach (JsonElement f in features.EnumerateArray())
                    if (f.ValueKind == JsonValueKind.Object && EngineServerState.Int(f, "state") == 1) on++;
            return new ServerProfileView(
                EngineServerState.Str(root, "id"), EngineServerState.Str(root, "name"), EngineServerState.Str(root, "plugin"),
                EngineServerState.Str(root, "ruleset"), EngineServerState.Str(root, "emulator"),
                EngineServerState.Str(root, "botting"), EngineServerState.Str(root, "bottingNote"),
                EngineServerState.Str(root, "accountLimit"), EngineServerState.Bool(root, "ilt"), on);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One plain-ASCII line for a status report ("PvE, ACE; botting forbidden - ...").</summary>
    public string Summary()
    {
        var parts = new List<string>();
        if (Ruleset.Length > 0 || Emulator.Length > 0)
            parts.Add(Ruleset.Length > 0 && Emulator.Length > 0 ? Ruleset + ", " + Emulator : Ruleset + Emulator);
        if (Botting.Length > 0) parts.Add("botting " + Botting + (BottingNote.Length > 0 ? " - " + BottingNote : string.Empty));
        if (AccountLimit.Length > 0) parts.Add("accounts: " + AccountLimit);
        if (Ilt || FeaturesOn > 0) parts.Add((Ilt ? "ILT world" : "not ILT") + ", " + FeaturesOn + " Leaftide features on");
        return parts.Count == 0 ? "no details" : string.Join("; ", parts);
    }
}
