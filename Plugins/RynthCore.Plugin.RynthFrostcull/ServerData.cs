// ServerData.cs - What is known about Frostcull (RynthFrostcull). Identity (id, hosts, world names) is in the csproj.
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
            Summary = "NO LEVEL/ATTRIBUTE CAP! 10x XP/Luminance, 25x Drop Rate, Quest Timers Reduced By 90%!",
            Facts = new[]
            {
                "No level or attribute cap.",
                "10x experience and luminance.",
                "25x drop rate.",
                "Quest timers 90% shorter.",
            },
            Discord = "https://discord.gg/RQF7HgEZn4",
            Source = "public AC server list (acresources/serverslist), checked 2026-10-13",
        };
}
