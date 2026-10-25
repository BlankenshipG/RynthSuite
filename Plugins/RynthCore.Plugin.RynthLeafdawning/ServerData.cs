// ServerData.cs - What is known about Leafdawning (RynthLeafdawning). Identity (id, hosts, world names) is in the csproj.
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
            Summary = "A standard, no-frills PvE end-of-retail ACE Asheron's Call server. Runs on dedicated hardware with the ability " +
                      "to host a large number of players. Enforced 3 account limit.",
            Facts = new[]
            {
                "Standard, no-frills end-of-retail.",
                "Not the same server as LeafDawn.",
            },
            AccountLimit = "3 accounts (enforced)",
            Discord = "https://discord.gg/sQpDgAzSXB",
            Source = "public AC server list (acresources/serverslist), checked 2026-10-13",
        };
}
