using System;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>The servers RynthOracle has server-specific features or data packs for.</summary>
internal static class ServerTags
{
    public const string Aelrynth = "Aelrynth";
    public const string Conquest = "Conquest";
    public const string Levistras = "Levistras";

    /// <summary>Tags with a quest pack (Resources\quests.&lt;tag&gt;.csv) or features.</summary>
    public static readonly string[] Known = { Aelrynth, Conquest, Levistras };
}

/// <summary>
/// Which server this client is on, decided once per login and re-checked each second until
/// known. Server-specific features and data packs carry a tag (<see cref="ServerTags"/>) and
/// only load or show where <see cref="Allows"/> says so; untagged (retail) ones work anywhere.
///
/// Sources, best first:
///   1. Engine API v75 GetServerInfo: the engine's own verdict for Aelrynth (connect host,
///      announced world name, Bank mod properties) and the world name the server announced
///      at login. Never the launcher profile name.
///   2. Older engines: the world name (GetWorldName; the server's own once logged in, though
///      it falls back to the launcher profile name before that) and, for Aelrynth, the Bank
///      mod's player properties 9101-9103, which only Aelrynth sends.
/// Aelrynth's live config still names the world "Aeshnidae" (its earlier name); both count.
/// ConquestAC and Levistras are matched on their world names, as upstream does
/// (Decal's CharacterFilter.Server).
/// </summary>
internal sealed class ServerIdentity
{
    private const uint QuadRadianceEarned = 9101, QuadLuminanceBanked = 9102, QuadLuminanceDrawn = 9103;

    /// <summary>A known tag, or "" (a server without its own features: retail features only).</summary>
    public string Tag { get; private set; } = "";
    public string WorldName { get; private set; } = "";
    public string Source { get; private set; } = "not logged in";
    public bool Known => WorldName.Length > 0 || Tag.Length > 0;
    public int Revision { get; private set; }

    public bool Is(string tag) => Tag.Length > 0 && string.Equals(Tag, tag, StringComparison.OrdinalIgnoreCase);

    /// <summary>Untagged features always; tagged ones only on their own server.</summary>
    public bool Allows(string? tag) => string.IsNullOrEmpty(tag) || Is(tag);

    public void Reset()
    {
        Set("", "", "not logged in");
    }

    public void Refresh(RynthCoreHost host, uint playerId)
    {
        string tag, world, source;
        if (host.TryGetServerInfo(out bool aelrynth, out bool staging, out string announced))
        {
            world = announced;
            if (aelrynth)
            {
                tag = ServerTags.Aelrynth;
                source = staging ? "engine: Aelrynth (staging)" : "engine: Aelrynth";
            }
            else
            {
                // The engine's Aelrynth verdict is the authority (it already weighs the world name,
                // and /rc server other overrides it for testing): never Aelrynth from the name here.
                tag = TagForWorld(world);
                if (tag == ServerTags.Aelrynth) tag = "";
                source = world.Length > 0 ? $"engine: the server calls itself \"{world}\"" : "engine: world name not announced yet";
            }
        }
        else
        {
            world = host.TryGetWorldName(out string w) ? w.Trim() : "";
            tag = TagForWorld(world);
            source = world.Length > 0 ? $"world name \"{world}\" (older engine)" : "world name not known yet (older engine)";
            if (tag.Length == 0 && playerId != 0 && HasBankProperties(host, playerId))
            {
                tag = ServerTags.Aelrynth;
                source = "the Aelrynth Bank properties (older engine)";
            }
        }
        Set(tag, world, source);
    }

    /// <summary>The tag for a world name; "" for servers without their own features.</summary>
    public static string TagForWorld(string? world)
    {
        if (string.IsNullOrWhiteSpace(world)) return "";
        string w = world.Trim();
        if (w.StartsWith("Aelrynth", StringComparison.OrdinalIgnoreCase) || w.StartsWith("Aeshnidae", StringComparison.OrdinalIgnoreCase))
            return ServerTags.Aelrynth;
        if (w.Equals(ServerTags.Conquest, StringComparison.OrdinalIgnoreCase) || w.Equals("ConquestAC", StringComparison.OrdinalIgnoreCase))
            return ServerTags.Conquest;
        if (w.Equals(ServerTags.Levistras, StringComparison.OrdinalIgnoreCase))
            return ServerTags.Levistras;
        return "";
    }

    private static bool HasBankProperties(RynthCoreHost host, uint playerId) =>
        host.HasGetObjectQuadProperty
        && (host.TryGetObjectQuadProperty(playerId, QuadRadianceEarned, out _)
            || host.TryGetObjectQuadProperty(playerId, QuadLuminanceBanked, out _)
            || host.TryGetObjectQuadProperty(playerId, QuadLuminanceDrawn, out _));

    private void Set(string tag, string world, string source)
    {
        if (tag == Tag && world == WorldName && source == Source) return;
        Tag = tag;
        WorldName = world;
        Source = source;
        Revision++;
    }

    /// <summary>For tests: a fixed identity.</summary>
    internal void Force(string tag, string world) => Set(tag, world, "forced");
}
