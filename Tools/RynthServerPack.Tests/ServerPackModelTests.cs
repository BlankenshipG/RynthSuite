using System;
using System.Text.Json;
using RynthCore.Plugin.ServerPack;
using RynthCore.RynthAiTests.Harness;

namespace RynthServerPack.Tests;

// The shared server-plugin model (Plugins\Shared\ServerPack\ServerPackModel.cs): RynthCore's
// "servermenu" verdict for the plugin's own server, the world-name fallback before one arrives,
// the override commands the card sends, and the sections manifest RynthCore reads.
internal static class ServerPackModelTests
{
    public static void Register(Runner r)
    {
        r.Add("server pack: RynthCore's push gives this server's verdict, override and reason", EnginePush);
        r.Add("server pack: a push without this server, or not a push, changes nothing", ForeignPush);
        r.Add("server pack: before any push the world name decides", WorldFallback);
        r.Add("server pack: override commands are checked before sending", ForceCommands);
        r.Add("server pack: the sections manifest declares the server and a server-only card", SectionsJson);
        r.Add("server pack: logout clears the verdict", ClearOnLogout);
        r.Add("server pack: the card's botting line and address", InfoText);
    }

    private static ServerPackInfo Coldeve() =>
        new("Coldeve", "Coldeve", new[] { "play.coldeve.ac" }, new[] { "Coldeve" }) { Discord = "https://discord.gg/x" };

    private const string Push =
        "V\tcoldeve\tColdeve\t1\ton\t0\tforced on for this character\n" +
        "V\tilt\tInfinite Leaftide\t0\tauto\t0\tnot Infinite Leaftide\n" +
        "X\tchar\tChar\n" +
        "S\tcoldeve\tColdeve\t1\t/rc server coldeve on\n" +
        "I\tColdeve server (RynthColdeve)\t/rc miniremote section char\tWhat is known.\tsection:char\n";

    private static void EnginePush()
    {
        var model = new ServerPackModel(Coldeve());
        Check.True(model.ApplyEngine(Push, 1234), "taken");
        ServerVerdictView? v = model.Engine;
        Check.True(v != null, "this server's verdict");
        Check.True(v!.Active, "active");
        Check.Eq(v.Force, "on", "override");
        Check.False(v.Detected, "forced, not detected");
        Check.Eq(v.Reason, "forced on for this character", "reason");
        Check.Eq(model.EngineAtMs, 1234L, "when");
        Check.True(model.IsActive("SomewhereElse"), "RynthCore's verdict beats the world name");
    }

    private static void ForeignPush()
    {
        var model = new ServerPackModel(Coldeve());
        Check.False(model.ApplyEngine("X\tchar\tChar\n", 1), "no server lines: not a push");
        Check.False(model.ApplyEngine(null, 1), "null");
        Check.False(model.ApplyEngine(new string('V', ServerPackModel.MaxPayload + 1), 1), "too long");
        Check.True(model.Engine == null, "nothing published");

        // A push that lists other servers but not this one yet (RynthCore hasn't read the manifest).
        Check.True(model.ApplyEngine("V\tilt\tInfinite Leaftide\t1\tauto\t1\thost matches\n", 2), "a push");
        Check.True(model.Engine == null, "no verdict for this server");
        ServerPackModel.ParseEngine("V\tCOLDEVE\tColdeve\t0\tweird\t1\n", "coldeve", out bool listed);
        Check.True(listed, "listed");
        Check.Eq(ServerPackModel.ParseEngine("V\tCOLDEVE\tColdeve\t0\tweird\t1\n", "coldeve", out _)!.Force, "auto",
                 "id case ignored; an unknown override reads as auto; no reason field is fine");
    }

    private static void WorldFallback()
    {
        var model = new ServerPackModel(Coldeve());
        Check.True(model.IsActive("Coldeve"), "exact world name");
        Check.True(model.IsActive(" coldeve "), "case and spaces");
        Check.False(model.IsActive("ColdeveTwo"), "no partial match without '*'");
        Check.False(model.IsActive(null), "unknown world");
        Check.True(ServerPackModel.WorldMatches("Aeshnidae Live", new[] { "Aelrynth*", "Aeshnidae*" }), "wildcard");
        Check.False(ServerPackModel.WorldMatches("LeafDawn", new[] { "Leafdawning" }), "LeafDawn is not Leafdawning");
        Check.True(ServerPackModel.WorldMatches("Infinite Leaftide", new[] { "*Leaftide*" }), "both ends");
    }

    private static void ForceCommands()
    {
        Check.Eq(ServerPackModel.ForceCommand("coldeve", "On"), "/rc server coldeve on", "on");
        Check.Eq(ServerPackModel.ForceCommand("coldeve", "auto"), "/rc server coldeve auto", "auto");
        Check.Eq(ServerPackModel.ForceCommand("coldeve", "maybe"), "", "unknown mode");
        Check.Eq(ServerPackModel.ForceCommand("cold eve", "on"), "", "an id with a space");
        Check.Eq(ServerPackModel.ForceCommand("coldeve; /quit", "on"), "", "no command injection through the id");
        Check.False(ServerPackModel.IsSafeId(new string('a', 33)), "too long");
    }

    private static void SectionsJson()
    {
        string json = ServerPackModel.BuildSectionsJson(Coldeve(), "RynthColdeve");
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Check.Eq(root.GetProperty("version").GetInt32(), 1, "schema 1");
        JsonElement server = root.GetProperty("servers")[0];
        Check.Eq(server.GetProperty("id").GetString(), "coldeve", "server id lower-cased");
        Check.Eq(server.GetProperty("hosts")[0].GetString(), "play.coldeve.ac", "hosts");
        Check.Eq(server.GetProperty("worldNames")[0].GetString(), "Coldeve", "world names");
        JsonElement card = root.GetProperty("contributions")[0];
        Check.Eq(card.GetProperty("section").GetString(), "char", "in the Char section");
        Check.Eq(card.GetProperty("server").GetString(), "coldeve", "only while Coldeve is active");
        Check.Eq(card.GetProperty("surface").GetString(), "section:server-coldeve", "the card's surface");

        var quoted = new ServerPackInfo("odd", "Odd \"Name\"\n", new[] { "a\\b" }, Array.Empty<string>());
        using JsonDocument odd = JsonDocument.Parse(ServerPackModel.BuildSectionsJson(quoted, "RynthOdd"));
        Check.Eq(odd.RootElement.GetProperty("servers")[0].GetProperty("hosts")[0].GetString(), "a\\b", "escaped");
    }

    private static void ClearOnLogout()
    {
        var model = new ServerPackModel(Coldeve());
        model.ApplyEngine(Push, 5);
        model.Clear();
        Check.True(model.Engine == null, "verdict cleared");
        Check.Eq(model.EngineAtMs, 0L, "time cleared");
        Check.False(model.IsActive("Elsewhere"), "back to the world name");
    }

    private static void InfoText()
    {
        ServerPackInfo c = Coldeve();
        Check.Eq(c.Address, "play.coldeve.ac:9000", "address");
        Check.True(c.BottingText.StartsWith("Not published"), "unknown policy says so");
        Check.True(c.BottingText.Contains("Discord"), "and points at the Discord");
        var lev = new ServerPackInfo("levistras", "Levistras", new[] { "levistras.acportalstorm.com" }, new[] { "Levistras" })
        { Botting = BottingPolicy.Forbidden, BottingNote = "100% Botting-Free" };
        Check.Eq(lev.BottingText, "Forbidden - 100% Botting-Free", "forbidden");
        Check.Eq(new ServerPackInfo("x", "", Array.Empty<string>(), Array.Empty<string>()).Address, "", "no host, no address");
        Check.True(ServerPackModel.LauncherSteps(c)[0].Contains("play.coldeve.ac:9000"), "setup names the address");
    }
}
