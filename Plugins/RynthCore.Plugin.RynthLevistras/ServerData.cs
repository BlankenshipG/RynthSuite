// ServerData.cs - What is known about Levistras (RynthLevistras). Identity (id, hosts, world names) is in the csproj.
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
            Summary = "A 100% Botting-Free PvE Server! Open access! 2 account limit",
            Facts = new[]
            {
                "Open access.",
                "RynthOracle has a Levistras quest pack (quests.levistras.csv).",
            },
            AccountLimit = "2 accounts",
            Botting = BottingPolicy.Forbidden,
            BottingNote = "\"100% Botting-Free\" (the server's own description)",
            Discord = "https://discord.gg/TtkcWbv",
            Source = "public AC server list (acresources/serverslist), checked 2026-10-13",
        };
}
