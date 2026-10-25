// ServerPackModel.cs - A server plugin's view of "is this my server", and what it tells RynthCore.
//
// RynthCore owns the verdict. Each server plugin declares its server in its Mini Remote sections
// manifest (RynthPluginGetSectionsJson, "servers"), so RynthCore detects it (connect host, then the
// announced world name), applies this character's Auto / On / Off override, and pushes the result
// to the plugin as "servermenu" (RynthCore UI/ServerMenus.cs):
//     V  id  name  active 0/1  force auto|on|off  detected 0/1  reason
// Other line kinds are RynthAi's menus and are skipped. Until a push arrives (an engine older than
// 2026.10.5.48), the plugin falls back to matching the world name itself.
//
// Pushes arrive on whichever thread sent them; each is parsed into one immutable view and published
// with a single reference write, so the render thread always reads a whole one.
// Pure (no host calls): RynthServerPack.Tests covers it.
using System;
using System.Text;
using System.Threading;

namespace RynthCore.Plugin.ServerPack;

/// <summary>RynthCore's verdict for this plugin's server.</summary>
internal sealed record ServerVerdictView(string Name, bool Active, string Force, bool Detected, string Reason);

internal sealed class ServerPackModel
{
    /// <summary>Payloads longer than this are not ours (a push is a few hundred bytes).</summary>
    public const int MaxPayload = 64 * 1024;

    private readonly ServerPackInfo _info;
    private ServerVerdictView? _engine;
    private long _engineAtMs;

    public ServerPackModel(ServerPackInfo info) => _info = info;

    public ServerPackInfo Info => _info;

    /// <summary>The last RynthCore verdict for this server (null until a push lists it). Any thread.</summary>
    public ServerVerdictView? Engine => Volatile.Read(ref _engine);

    /// <summary>When the last push arrived (Environment.TickCount64), 0 for never.</summary>
    public long EngineAtMs => Interlocked.Read(ref _engineAtMs);

    /// <summary>A RynthCore "servermenu" push. Any thread. False when it was not usable.</summary>
    public bool ApplyEngine(string? payload, long nowMs)
    {
        ServerVerdictView? view = ParseEngine(payload, _info.Id, out bool listed);
        if (!listed) return false;
        Volatile.Write(ref _engine, view);
        Interlocked.Exchange(ref _engineAtMs, nowMs);
        return true;
    }

    /// <summary>Logout: the next character's verdict comes fresh.</summary>
    public void Clear()
    {
        Volatile.Write(ref _engine, null);
        Interlocked.Exchange(ref _engineAtMs, 0);
    }

    /// <summary>
    /// True when this plugin should act: RynthCore's verdict when it sent one, else the world
    /// name (<paramref name="world"/>) against the plugin's own list.
    /// </summary>
    public bool IsActive(string? world)
    {
        ServerVerdictView? v = Engine;
        return v != null ? v.Active : WorldMatches(world, _info.WorldNames);
    }

    /// <summary>
    /// The verdict for <paramref name="serverId"/> in a "servermenu" payload. <paramref name="listed"/>
    /// is false when the payload has no server lines at all (not a push); the view is null when it has
    /// some but not this server (RynthCore hasn't read this plugin's manifest yet).
    /// </summary>
    public static ServerVerdictView? ParseEngine(string? payload, string serverId, out bool listed)
    {
        listed = false;
        if (string.IsNullOrEmpty(payload) || payload.Length > MaxPayload || string.IsNullOrEmpty(serverId)) return null;
        ServerVerdictView? view = null;
        foreach (string raw in payload.Split('\n'))
        {
            string[] f = raw.TrimEnd('\r').Split('\t');
            if (f[0] != "V" || f.Length < 6) continue;
            listed = true;
            if (!f[1].Equals(serverId, StringComparison.OrdinalIgnoreCase)) continue;
            view = new ServerVerdictView(f[2], f[3] == "1", NormalizeForce(f[4]), f[5] == "1", f.Length >= 7 ? f[6] : string.Empty);
        }
        return view;
    }

    /// <summary>"auto", "on" or "off" (anything else reads as auto).</summary>
    public static string NormalizeForce(string? text)
    {
        string t = (text ?? string.Empty).Trim().ToLowerInvariant();
        return t is "on" or "off" ? t : "auto";
    }

    /// <summary>"/rc server {id} {mode}" for auto / on / off; "" for anything else.</summary>
    public static string ForceCommand(string serverId, string mode)
    {
        string m = (mode ?? string.Empty).Trim().ToLowerInvariant();
        if (m is not ("auto" or "on" or "off") || !IsSafeId(serverId)) return string.Empty;
        return "/rc server " + serverId + " " + m;
    }

    /// <summary>True for an id RynthCore accepts in a command: 1-32 of a-z, 0-9 and '-'.</summary>
    public static bool IsSafeId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 32) return false;
        foreach (char c in id)
            if (!(c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c == '-')) return false;
        return true;
    }

    /// <summary>Case-insensitive world-name match, '*' standing for any text (RynthCore's rules).</summary>
    public static bool WorldMatches(string? world, string[] patterns)
    {
        if (string.IsNullOrWhiteSpace(world)) return false;
        string w = world.Trim();
        foreach (string p in patterns)
            if (!string.IsNullOrWhiteSpace(p) && Glob(w, p.Trim())) return true;
        return false;
    }

    private static bool Glob(string text, string pattern)
    {
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

    // ── RynthPluginGetSectionsJson ────────────────────────────────────────

    /// <summary>The Mini Remote surface of the server card.</summary>
    public static string SurfaceFor(string serverId) => "section:server-" + serverId;

    /// <summary>
    /// The Mini Remote section the server cards share: right after Session Stats (core order 0) and
    /// before Map (100). Every server plugin declares it the same way; the first declaration wins.
    /// </summary>
    public const string SectionId = "server";
    public const string SectionLabel = "Server";
    public const int SectionOrder = 50;

    /// <summary>
    /// The plugin's sections manifest (schema 1): its server, for RynthCore to detect, and the server
    /// card in the shared "Server" section, shown only while that server is active (detected or
    /// forced in RynthCore Options > Diagnostics). The card left the Char section on 2026-10-13: that
    /// section is Session Stats now, session stats only.
    /// </summary>
    public static string BuildSectionsJson(ServerPackInfo info, string pluginName)
    {
        var sb = new StringBuilder(512);
        sb.Append("{\"version\":1,\"servers\":[{\"id\":").Append(Q(info.Id)).Append(",\"name\":").Append(Q(info.Name));
        sb.Append(",\"worldNames\":[");
        AppendList(sb, info.WorldNames);
        sb.Append("],\"hosts\":[");
        AppendList(sb, info.Hosts);
        sb.Append("]}],\"sections\":[{\"id\":").Append(Q(SectionId)).Append(",\"label\":").Append(Q(SectionLabel));
        sb.Append(",\"order\":").Append(SectionOrder).Append("}],\"contributions\":[{\"section\":").Append(Q(SectionId));
        sb.Append(",\"server\":").Append(Q(info.Id));
        sb.Append(",\"label\":").Append(Q(info.Name + " server (" + pluginName + ")"));
        sb.Append(",\"surface\":").Append(Q(SurfaceFor(info.Id)));
        sb.Append(",\"primary\":false,\"height\":320,\"enabled\":true,\"tooltip\":");
        sb.Append(Q("What is known about " + info.Name + ": rules, account limits, botting policy, links and setup ("
                    + pluginName + "; also /rserver)."));
        sb.Append(",\"order\":130}]}");
        return sb.ToString();
    }

    private static void AppendList(StringBuilder sb, string[] values)
    {
        for (int i = 0; i < values.Length; i++) sb.Append(i == 0 ? "" : ",").Append(Q(values[i]));
    }

    /// <summary>A JSON string literal.</summary>
    public static string Q(string? s)
    {
        var sb = new StringBuilder((s?.Length ?? 0) + 2).Append('"');
        foreach (char c in s ?? string.Empty)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    // ── The launcher side ─────────────────────────────────────────────────

    /// <summary>How to set up an account for this server in the RynthCore launcher.</summary>
    public static string[] LauncherSteps(ServerPackInfo info) => new[]
    {
        "1. Launcher > Servers > Add: pick \"" + info.Name + "\" under Known server. It fills in "
            + (info.Address.Length > 0 ? info.Address : "the host and port") + " (" + info.Emulator + ").",
        "2. Launcher > Accounts > Add: choose that server, then the account and character.",
        "3. Launch: RynthCore loads this plugin only for clients connecting to " + info.Name + ".",
    };
}
