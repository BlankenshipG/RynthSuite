// ServerData.cs - What is known about Conquest (RynthConquest). Identity (id, hosts, world names) is in the csproj.
namespace RynthCore.Plugin.ServerPack;

internal static class ServerData
{
    public static ServerPackInfo Create() =>
        new(RynthPluginInfo.ServerId, RynthPluginInfo.ServerName, RynthPluginInfo.ServerHosts, RynthPluginInfo.ServerWorldNames)
        {
            Port = 9000,
            Emulator = "ACE",
            Ruleset = "PvE",
            Status = "Development",
            Summary = "Custom PvE server with live events, deep crafting, active development, and PK system.",
            Facts = new[]
            {
                "Live events and deep crafting.",
                "Has a PK system on a PvE server.",
                "RynthOracle has a Conquest quest pack (quests.conquest.csv).",
            },
            Discord = "https://discord.gg/Gsadhhv72S",
            Website = "https://dsc.gg/conquerac",
            Source = "public AC server list (acresources/serverslist), checked 2026-10-13",
        };
}
