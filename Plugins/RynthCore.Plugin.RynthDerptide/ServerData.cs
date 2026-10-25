// ServerData.cs - What is known about Derptide (RynthDerptide). Identity (id, hosts, world names) is in the csproj.
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
            Summary = "EOR server that pushes custom content and Qol changes, All quest weapons have been upgraded to be useful, " +
                      "and usable. No quest item will EVER be better than lootgen, Live events, Ongoing story arcs. and TONS of tailoring options.",
            Facts = new[]
            {
                "Quest weapons are upgraded to be useful, but never better than loot-generated items.",
                "Live events and ongoing story arcs.",
                "Many tailoring options.",
            },
            Discord = "https://discord.gg/3gDTBVtfAg",
            Website = "https://derptide.net",
            Source = "public AC server list (acresources/serverslist), checked 2026-10-13",
        };
}
