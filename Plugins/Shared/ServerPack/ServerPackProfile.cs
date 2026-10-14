// ServerPackProfile.cs - A server plugin's settings as JSON, for other plugins ("RynthServer.Profile",
// Plugins/Shared/RynthServerProfileApi.cs, served by ServerPackExports' RynthPluginQueryInterface).
//
// RynthCore's GetServerStateJson (plugin API v85) names the active server and the plugin that owns
// it; a reader (RynthOracle, RynthAi's "/ra server", ...) asks that plugin for this profile to get
// the server's rules without knowing the server: connection, rule set, botting policy, account
// limits, commands and links. Everything ServerPackInfo holds, nothing guessed.
//
// The JSON is plain ASCII (anything else is \u-escaped), so a reader can show it in the AC client.
// Pure (no host calls): RynthServerPack.Tests covers it.
using System;
using System.Text;

namespace RynthCore.Plugin.ServerPack;

internal static class ServerPackProfile
{
    /// <summary>The profile JSON's schema (RynthServerProfile.Schema).</summary>
    public const int Schema = 1;

    /// <summary>The "botting" value for a policy.</summary>
    public static string BottingName(BottingPolicy policy) => policy switch
    {
        BottingPolicy.Allowed => "allowed",
        BottingPolicy.Restricted => "restricted",
        BottingPolicy.Forbidden => "forbidden",
        _ => "unknown",
    };

    /// <summary>
    /// The profile for <paramref name="info"/>: <paramref name="active"/> is this plugin's verdict
    /// (RynthCore's when it pushed one), <paramref name="world"/> the world name the client reports.
    /// </summary>
    public static string BuildJson(ServerPackInfo info, string pluginName, string pluginVersion, bool active, string? world)
    {
        var sb = new StringBuilder(1024);
        sb.Append("{\"schema\":").Append(Schema);
        Field(sb, "id", info.Id);
        Field(sb, "name", info.Name);
        Field(sb, "plugin", pluginName);
        Field(sb, "version", pluginVersion);
        sb.Append(",\"active\":").Append(active ? "true" : "false");
        Field(sb, "world", world ?? string.Empty);
        List(sb, "hosts", info.Hosts);
        List(sb, "worldNames", info.WorldNames);
        sb.Append(",\"port\":").Append(info.Port);
        Field(sb, "emulator", info.Emulator);
        Field(sb, "ruleset", info.Ruleset);
        Field(sb, "status", info.Status);
        Field(sb, "botting", BottingName(info.Botting));
        Field(sb, "bottingNote", info.BottingNote);
        Field(sb, "accountLimit", info.AccountLimit);
        Field(sb, "summary", info.Summary);
        List(sb, "facts", info.Facts);
        sb.Append(",\"commands\":[");
        for (int i = 0; i < info.Commands.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"command\":");
            Str(sb, info.Commands[i].Command);
            sb.Append(",\"what\":");
            Str(sb, info.Commands[i].What);
            sb.Append('}');
        }
        sb.Append(']');
        Field(sb, "discord", info.Discord);
        Field(sb, "website", info.Website);
        Field(sb, "wiki", info.Wiki);
        Field(sb, "source", info.Source);
        sb.Append('}');
        return sb.ToString();
    }

    private static void Field(StringBuilder sb, string name, string? value)
    {
        sb.Append(",\"").Append(name).Append("\":");
        Str(sb, value);
    }

    private static void List(StringBuilder sb, string name, string[]? values)
    {
        sb.Append(",\"").Append(name).Append("\":[");
        if (values != null)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(',');
                Str(sb, values[i]);
            }
        }
        sb.Append(']');
    }

    /// <summary>A JSON string literal in plain ASCII (control and non-ASCII characters \u-escaped).</summary>
    private static void Str(StringBuilder sb, string? s)
    {
        sb.Append('"');
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
                    if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
