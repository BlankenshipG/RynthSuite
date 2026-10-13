// ServerData.cs - What is known about Sundering (RynthSundering). Identity (id, hosts, world names) is in the csproj.
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
            Summary = "The Sundering is a 3 active character limit, PVE server targeting an End of Retail feel with convenience " +
                      "modifications, custom content, and pre-planned weekend/weekday bonuses.",
            Facts = new[]
            {
                "End-of-retail feel with convenience changes and custom content.",
                "Planned weekend and weekday bonuses.",
            },
            AccountLimit = "3 active characters",
            Discord = "https://discord.gg/XgHpcVbmJA",
            Website = "https://sunderingac.com",
            Source = "public AC server list (acresources/serverslist), checked 2026-10-13",
        };
}
