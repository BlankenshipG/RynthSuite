// ServerData.cs - What is known about Eversong (RynthEversong). Identity (id, hosts, world names) is in the csproj.
namespace RynthCore.Plugin.ServerPack;

internal static class ServerData
{
    public static ServerPackInfo Create() =>
        new(RynthPluginInfo.ServerId, RynthPluginInfo.ServerName, RynthPluginInfo.ServerHosts, RynthPluginInfo.ServerWorldNames)
        {
            Port = 9000,
            Emulator = "ACE",
            Ruleset = "PvE",
            Status = "Stable",
            Summary = "Five difficulty worlds, quest bonuses, enlightenment & milestones, banking & lockboxes, invasions, optional classes.",
            Facts = new[]
            {
                "Five difficulty worlds.",
                "Quest bonuses, enlightenment and milestones.",
                "Banking and lockboxes.",
                "Invasions and optional classes.",
            },
            Discord = "https://discord.gg/9eHxTb7p2J",
            Source = "public AC server list (acresources/serverslist), checked 2026-10-13",
        };
}
