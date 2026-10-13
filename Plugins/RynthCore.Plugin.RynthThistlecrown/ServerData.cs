// ServerData.cs - What is known about Thistlecrown (RynthThistlecrown). Identity (id, hosts, world names) is in the csproj.
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
            Summary = "Everyone welcome! 3 account limit. Recreation of TD.",
            Facts = new[] { "A recreation of the retail Thistledown (TD) server." },
            AccountLimit = "3 accounts",
            Discord = "https://discord.gg/uhZ3hn7",
            Source = "public AC server list (acresources/serverslist), checked 2026-10-13",
        };
}
