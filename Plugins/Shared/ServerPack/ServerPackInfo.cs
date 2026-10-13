// ServerPackInfo.cs - What a server plugin knows about its game server (shared by every server plugin).
//
// Identity (id, name, hosts, world names) comes from the plugin's csproj through RynthPluginInfo, the
// same values the engine reads from the embedded manifest, so detection has one source. The rest is
// what is publicly known about the server (ServerData.cs in each project): connection, rule set,
// summary, links, account limits and the botting policy. Unknown stays empty; nothing is guessed.
// Pure: RynthServerPack.Tests compiles it.
using System;

namespace RynthCore.Plugin.ServerPack;

/// <summary>What the server's rules say about bots (RynthAi automation).</summary>
internal enum BottingPolicy
{
    /// <summary>Not published here: check the server's own rules.</summary>
    Unknown,
    Allowed,
    /// <summary>Allowed with limits (attended only, a client cap, ...): see <see cref="ServerPackInfo.BottingNote"/>.</summary>
    Restricted,
    /// <summary>The server forbids bots.</summary>
    Forbidden,
}

/// <summary>A chat command the server adds, and what it does.</summary>
internal sealed record ServerCommand(string Command, string What);

internal sealed class ServerPackInfo
{
    public ServerPackInfo(string id, string name, string[] hosts, string[] worldNames)
    {
        Id = (id ?? string.Empty).Trim().ToLowerInvariant();
        Name = string.IsNullOrWhiteSpace(name) ? Id : name.Trim();
        Hosts = hosts ?? Array.Empty<string>();
        WorldNames = worldNames ?? Array.Empty<string>();
    }

    /// <summary>The engine's id for the server ("coldeve"); a built-in one ("ilt", "aelrynth") keeps the engine's own detection.</summary>
    public string Id { get; }
    public string Name { get; }
    /// <summary>Connect hosts (the first is the one players are given).</summary>
    public string[] Hosts { get; }
    /// <summary>World names the server announces at login.</summary>
    public string[] WorldNames { get; }

    public int Port { get; init; } = 9000;
    /// <summary>"ACE" or "GDLE" (the launcher's emulator setting).</summary>
    public string Emulator { get; init; } = "ACE";
    /// <summary>"PvE" or "PvP".</summary>
    public string Ruleset { get; init; } = "PvE";
    /// <summary>"Stable", "Development", ... as the server list says.</summary>
    public string Status { get; init; } = string.Empty;
    /// <summary>The server's own one-paragraph description.</summary>
    public string Summary { get; init; } = string.Empty;
    /// <summary>Notable rules and features, one line each.</summary>
    public string[] Facts { get; init; } = Array.Empty<string>();
    /// <summary>Chat commands the server adds (only ones known to exist).</summary>
    public ServerCommand[] Commands { get; init; } = Array.Empty<ServerCommand>();
    /// <summary>"3 accounts", "3 active characters", ...; empty when not published.</summary>
    public string AccountLimit { get; init; } = string.Empty;
    public BottingPolicy Botting { get; init; } = BottingPolicy.Unknown;
    /// <summary>The rule in the server's words, when there is one.</summary>
    public string BottingNote { get; init; } = string.Empty;
    public string Discord { get; init; } = string.Empty;
    public string Website { get; init; } = string.Empty;
    public string Wiki { get; init; } = string.Empty;
    /// <summary>Where the facts come from, and when they were checked.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>"play.coldeve.ac:9000" (the first host), or "" with none.</summary>
    public string Address => Hosts.Length == 0 ? string.Empty : Hosts[0] + ":" + Port;

    /// <summary>The botting line for the card.</summary>
    public string BottingText => Botting switch
    {
        BottingPolicy.Allowed => "Allowed" + (BottingNote.Length > 0 ? " - " + BottingNote : string.Empty),
        BottingPolicy.Restricted => "Restricted" + (BottingNote.Length > 0 ? " - " + BottingNote : string.Empty),
        BottingPolicy.Forbidden => "Forbidden" + (BottingNote.Length > 0 ? " - " + BottingNote : string.Empty),
        _ => "Not published here - check the server's rules" + (Discord.Length > 0 ? " (Discord)" : string.Empty),
    };
}
