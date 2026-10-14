using System;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>The servers RynthOracle has server-specific features or data packs for.</summary>
internal static class ServerTags
{
    public const string Aelrynth = "Aelrynth";
    public const string Conquest = "Conquest";
    public const string Levistras = "Levistras";
    /// <summary>Infinite Leaftide (ACECustom). Its server rules come from RynthILT ("RynthILT.Server").</summary>
    public const string InfiniteLeaftide = "InfiniteLeaftide";

    /// <summary>Tags with a quest pack (Resources\quests.&lt;tag&gt;.csv) or features.</summary>
    public static readonly string[] Known = { Aelrynth, Conquest, Levistras, InfiniteLeaftide };
}

/// <summary>
/// Which server this client is on, decided once per login and re-checked each second until
/// known. Server-specific features and data packs carry a tag (<see cref="ServerTags"/>) and
/// only load or show where <see cref="Allows"/> says so; untagged (retail) ones work anywhere.
///
/// Sources, best first:
///   0. Engine API v86 GetServerStateJson (ServerStateClient): RynthCore's active server, with
///      this character's overrides and the Diagnostics page's server pick applied (FromEngine).
///   1. Engine API v75 GetServerInfo: the engine's own verdict for Aelrynth (connect host,
///      announced world name, Bank mod properties) and the world name the server announced
///      at login. Never the launcher profile name.
///   2. Older engines: the world name (GetWorldName; the server's own once logged in, though
///      it falls back to the launcher profile name before that) and, for Aelrynth, the Bank
///      mod's player properties 9101-9103, which only Aelrynth sends.
/// Aelrynth's live config still names the world "Aeshnidae" (its earlier name); both count.
/// ConquestAC and Levistras are matched on their world names, as upstream does
/// (Decal's CharacterFilter.Server). Infinite Leaftide is matched on a world name containing
/// "Leaftide", or on RynthILT's own verdict (its world list or the character's override) when
/// RynthILT is loaded.
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

    /// <param name="rynthIltSaysIlt">RynthILT's profile reports an ILT world (IltServerLink); false without RynthILT.</param>
    /// <param name="engine">
    /// RynthCore's server list (plugin API v86, ServerStateClient); null on older engines. When there,
    /// it decides: it already applies this character's overrides and the Diagnostics page's pick.
    /// </param>
    public void Refresh(RynthCoreHost host, uint playerId, bool rynthIltSaysIlt = false, EngineServerState? engine = null)
    {
        if (engine != null)
        {
            string fallbackWorld = host.TryGetWorldName(out string fw) ? fw.Trim() : "";
            FromEngine(engine, fallbackWorld, out string t, out string wn, out string src);
            Set(t, wn, src);
            return;
        }

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
        if (tag.Length == 0 && rynthIltSaysIlt)
        {
            tag = ServerTags.InfiniteLeaftide;
            source = "RynthILT: an Infinite Leaftide world";
        }
        Set(tag, world, source);
    }

    /// <summary>
    /// The identity from RynthCore's server list. An active server decides the tag (its own, or ""
    /// for a server RynthOracle has nothing for). With none active, the world name may still tag a
    /// server the engine doesn't know (Conquest without RynthConquest loaded), but never one the
    /// engine lists: that one is off on purpose (not detected, or this character's override).
    /// Pure: the tests drive it.
    /// </summary>
    internal static void FromEngine(EngineServerState engine, string fallbackWorld, out string tag, out string world, out string source)
    {
        world = engine.World.Length > 0 ? engine.World : (fallbackWorld ?? "").Trim();
        if (engine.HasActive)
        {
            tag = TagForEngineId(engine.Active);
            EngineServer? s = engine.Find(engine.Active);
            string how = engine.Picked ? "picked by hand"
                : s != null && s.Force.Equals("on", StringComparison.OrdinalIgnoreCase) ? "forced on for this character"
                : s != null && s.Reason.Length > 0 ? s.Reason : "detected";
            string name = engine.ActiveName.Length > 0 ? engine.ActiveName : engine.Active;
            source = $"RynthCore: {name} ({how})" + (tag.Length == 0 ? " - no RynthOracle pages for it" : "");
            return;
        }

        tag = TagForWorld(world);
        string id = EngineIdForTag(tag);
        if (tag.Length > 0 && id.Length > 0 && engine.Find(id) != null)
        {
            source = $"RynthCore: {tag} is off for this world (not detected, or overridden)";
            tag = "";
            return;
        }
        source = tag.Length > 0
            ? $"world name \"{world}\" (RynthCore has no {tag} server loaded)"
            : "RynthCore: no server active (retail features only)";
    }

    /// <summary>The tag for a RynthCore server id; "" for servers without RynthOracle features.</summary>
    public static string TagForEngineId(string? id) => (id ?? "").Trim().ToLowerInvariant() switch
    {
        EngineServerState.IltId => ServerTags.InfiniteLeaftide,
        EngineServerState.AelrynthId => ServerTags.Aelrynth,
        "conquest" => ServerTags.Conquest,
        "levistras" => ServerTags.Levistras,
        _ => "",
    };

    /// <summary>The RynthCore server id for a tag; "" for none.</summary>
    public static string EngineIdForTag(string? tag) => tag switch
    {
        ServerTags.InfiniteLeaftide => EngineServerState.IltId,
        ServerTags.Aelrynth => EngineServerState.AelrynthId,
        ServerTags.Conquest => "conquest",
        ServerTags.Levistras => "levistras",
        _ => "",
    };

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
        // "InfiniteLeaftide", "Infinite Leaftide", staging copies: RynthILT's own world rule.
        if (w.IndexOf("Leaftide", StringComparison.OrdinalIgnoreCase) >= 0)
            return ServerTags.InfiniteLeaftide;
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
