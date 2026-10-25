// ServerData.cs - What is known about PortalStorm (RynthPortalStorm). Identity (id, hosts, world names) is in the csproj.
namespace RynthCore.Plugin.ServerPack;

internal static class ServerData
{
    public static ServerPackInfo Create() =>
        new(RynthPluginInfo.ServerId, RynthPluginInfo.ServerName, RynthPluginInfo.ServerHosts, RynthPluginInfo.ServerWorldNames)
        {
            // Not the usual 9000.
            Port = 9020,
            Emulator = "ACE",
            Ruleset = "PvE",
            Status = "Stable",
            Summary = "15 Tiers, 10 Enlightenments, Infinite Lum Augs, Optional Hard Mode, Custom Skills, Years of custom content.",
            Facts = new[]
            {
                "15 loot tiers.",
                "10 enlightenments and infinite luminance augmentations.",
                "Optional hard mode.",
                "Custom skills and years of custom content.",
                "Connects on port 9020, not 9000.",
            },
            Discord = "https://discord.gg/YeX6mupcf",
            Source = "public AC server list (acresources/serverslist), checked 2026-10-13",
        };
}
