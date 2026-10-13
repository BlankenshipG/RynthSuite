// ServerData.cs - What is known about AChard (RynthAChard). Identity (id, hosts, world names) is in the csproj.
namespace RynthCore.Plugin.ServerPack;

internal static class ServerData
{
    public static ServerPackInfo Create() =>
        new(RynthPluginInfo.ServerId, RynthPluginInfo.ServerName, RynthPluginInfo.ServerHosts, RynthPluginInfo.ServerWorldNames)
        {
            Port = 9000,
            Emulator = "ACE",
            Ruleset = "PvP",
            Status = "Stable",
            Summary = "ACE EOR PVP Server/Custom PK Content/PK Kill Rankings",
            Facts = new[]
            {
                "PvP server: other players can attack you.",
                "Custom PK content and PK kill rankings.",
            },
            Discord = "https://discord.gg/gmHfqt2J2D",
            Website = "http://a-chard.ddns.net/",
            Source = "public AC server list (acresources/serverslist), checked 2026-10-13",
        };
}
