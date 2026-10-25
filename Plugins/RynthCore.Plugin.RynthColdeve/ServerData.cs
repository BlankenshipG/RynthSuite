// ServerData.cs - What is known about Coldeve (RynthColdeve). Identity (id, hosts, world names) is in the csproj.
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
            Summary = "A PVE AC Server evolving to provide a end of retail experience running ACEmulator.",
            Facts = new[] { "End-of-retail experience on ACEmulator." },
            Discord = "https://discord.gg/nUR4PHe",
            Source = "public AC server list (acresources/serverslist), checked 2026-10-13",
        };
}
