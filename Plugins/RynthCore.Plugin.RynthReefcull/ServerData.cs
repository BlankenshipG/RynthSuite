// ServerData.cs - What is known about Reefcull (RynthReefcull). Identity (id, hosts, world names) is in the csproj.
namespace RynthCore.Plugin.ServerPack;

internal static class ServerData
{
    public static ServerPackInfo Create() =>
        new(RynthPluginInfo.ServerId, RynthPluginInfo.ServerName, RynthPluginInfo.ServerHosts, RynthPluginInfo.ServerWorldNames)
        {
            Port = 9000,
            Emulator = "GDLE",
            Ruleset = "PvE",
            Status = "Stable",
            Summary = "A PVE Server thats goal is to mimic end of game retail.",
            Facts = new[]
            {
                "End-of-retail experience on GDLE (not ACE): set the launcher's emulator to Gdle.",
            },
            Discord = "https://discord.gg/3ngsm6e",
            Source = "public AC server list (acresources/serverslist), checked 2026-10-13",
        };
}
