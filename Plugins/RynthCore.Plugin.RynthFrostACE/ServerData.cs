// ServerData.cs - What is known about FrostACE (RynthFrostACE). Identity (id, hosts, world names) is in the csproj.
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
            Summary = "New accounts include lvl 275 starter characters. Retail character restoration also supported.",
            Facts = new[]
            {
                "New accounts get level 275 starter characters.",
                "Retail character restoration is supported.",
                "Listed as \"FrostfACE\" in the public server list; it connects by IP address.",
            },
            Source = "public AC server list (acresources/serverslist), checked 2026-10-13",
        };
}
