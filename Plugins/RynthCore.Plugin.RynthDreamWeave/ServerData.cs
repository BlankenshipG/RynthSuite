// ServerData.cs - What is known about DreamWeave (RynthDreamWeave). Identity (id, hosts, world names) is in the csproj.
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
            Summary = "PvE server pushing unmodified end-of-retail ToD to the limit. Lore driven, new class abilities, " +
                      "new crafting system, tons of QOL, up-to-date wiki.",
            Facts = new[]
            {
                "Lore driven, built on end-of-retail Throne of Destiny.",
                "New class abilities and a new crafting system.",
                "Its wiki is kept up to date.",
            },
            Discord = "https://discord.gg/p2h3yca4BD",
            Wiki = "https://wiki.acdreamweave.com",
            Source = "public AC server list (acresources/serverslist), checked 2026-10-13",
        };
}
