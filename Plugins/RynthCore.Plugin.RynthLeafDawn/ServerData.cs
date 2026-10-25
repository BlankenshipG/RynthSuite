// ServerData.cs - What is known about LeafDawn (RynthLeafDawn). Identity (id, hosts, world names) is in the csproj.
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
            Summary = "A PVE Server thats goal is to mimic end of game retail. With some custom content",
            Facts = new[]
            {
                "End-of-retail with some custom content.",
                "Not the same server as Leafdawning.",
            },
            Discord = "https://discord.gg/c2QUBPwPZF",
            Source = "public AC server list (acresources/serverslist), checked 2026-10-13",
        };
}
