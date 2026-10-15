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
        r.Add("server pack: the RynthServer.Profile JSON carries the server's settings", ProfileJson);
        r.Add("server pack: the profile JSON is plain ASCII and escaped", ProfileJsonAscii);
    }

    private static void ProfileJson()
    {
        ServerPackInfo info = new("Coldeve", "Coldeve", new[] { "play.coldeve.ac" }, new[] { "Coldeve" })
        {
            Port = 9050, Ruleset = "PvP", Status = "Stable", Botting = BottingPolicy.Restricted, BottingNote = "attended only",
            AccountLimit = "3 accounts", Facts = new[] { "fact one" }, Commands = new[] { new ServerCommand("/x", "does x") },
            Discord = "https://discord.gg/x",
        };
        string json = ServerPackProfile.BuildJson(info, "RynthColdeve", "0.1.2", true, "Coldeve");
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Check.Eq(root.GetProperty("schema").GetInt32(), ServerPackProfile.Schema, "schema");
        Check.Eq(root.GetProperty("id").GetString(), "coldeve", "id (lower-cased)");
        Check.Eq(root.GetProperty("plugin").GetString(), "RynthColdeve", "plugin");
        Check.Eq(root.GetProperty("version").GetString(), "0.1.2", "version");
        Check.True(root.GetProperty("active").GetBoolean(), "active");
        Check.Eq(root.GetProperty("world").GetString(), "Coldeve", "world");
        Check.Eq(root.GetProperty("hosts")[0].GetString(), "play.coldeve.ac", "hosts");
        Check.Eq(root.GetProperty("port").GetInt32(), 9050, "port");
        Check.Eq(root.GetProperty("ruleset").GetString(), "PvP", "ruleset");
        Check.Eq(root.GetProperty("botting").GetString(), "restricted", "botting");
        Check.Eq(root.GetProperty("bottingNote").GetString(), "attended only", "botting note");
        Check.Eq(root.GetProperty("facts")[0].GetString(), "fact one", "facts");
        Check.Eq(root.GetProperty("commands")[0].GetProperty("what").GetString(), "does x", "commands");
        Check.Eq(root.GetProperty("discord").GetString(), "https://discord.gg/x", "links");

        // What a reader (RynthOracle, RynthAi's /ra server) makes of it.
        var view = RynthCore.Plugin.Shared.ServerProfileView.Parse(json);
        Check.True(view != null, "the shared reader parses it");
        Check.Eq(view!.Summary(), "PvP, ACE; botting restricted - attended only; accounts: 3 accounts", "the reader's summary");

        // No salvage defined: an empty list, which the Crafting tab reads as "none defined".
        Check.Eq(root.GetProperty("salvage").GetArrayLength(), 0, "no salvage: empty list");
        Check.Eq(RynthCore.Plugin.Shared.ServerSalvageView.ParseList(json).Count, 0, "the reader sees none");

        // Salvage defined: each row round-trips through the shared reader.
        ServerPackInfo withSalvage = new("x", "X", new[] { "x.example" }, new[] { "X" })
        {
            Salvage = new[]
            {
                new ServerSalvage("melee", 52, "+1 tink past the cap", "once", UsesTinkSlot: false),
                new ServerSalvage("Armor", 64, "+30 armor level"),
                new ServerSalvage("", 64, "no bench: skipped"),
            },
        };
        var salvage = RynthCore.Plugin.Shared.ServerSalvageView.ParseList(
            ServerPackProfile.BuildJson(withSalvage, "RynthX", "1", true, "X"));
        Check.Eq(salvage.Count, 2, "rows without a bench are skipped");
        Check.Eq(salvage[0].Bench, "melee", "bench");
        Check.Eq(salvage[0].Material, 52, "material id");
        Check.Eq(salvage[0].Kind, "once", "kind");
        Check.False(salvage[0].UsesTinkSlot, "no tink slot");
        Check.Eq(salvage[1].Bench, "armor", "bench lower-cased");
        Check.Eq(salvage[1].Kind, "tink", "default kind");
        Check.True(salvage[1].UsesTinkSlot, "slot by default");
        Check.Eq(RynthCore.Plugin.Shared.ServerSalvageView.ParseList("{\"id\":\"ilt\"}").Count, 0, "a profile without the list (RynthILT, older plugins)");

        string idle = ServerPackProfile.BuildJson(info, "RynthColdeve", "0.1.2", false, null);
        using JsonDocument idleDoc = JsonDocument.Parse(idle);
        Check.False(idleDoc.RootElement.GetProperty("active").GetBoolean(), "not active");
        Check.Eq(idleDoc.RootElement.GetProperty("world").GetString(), "", "no world before login");
        Check.Eq(ServerPackProfile.BottingName(BottingPolicy.Unknown), "unknown", "unknown botting");
    }

    private static void ProfileJsonAscii()
    {
        ServerPackInfo info = new("x", "Caf\u00e9 \"Q\"\n", Array.Empty<string>(), Array.Empty<string>()) { Summary = "a\\b\tc \u2014 d" };
        string json = ServerPackProfile.BuildJson(info, "RynthX", "1", false, "");
        bool ascii = true;
        foreach (char c in json) if (c > 0x7e || c < 0x20) ascii = false;
        Check.True(ascii, "only printable ASCII: " + json);
        using JsonDocument doc = JsonDocument.Parse(json);
        Check.Eq(doc.RootElement.GetProperty("name").GetString(), "Caf\u00e9 \"Q\"", "name round-trips (trimmed)");
        Check.Eq(doc.RootElement.GetProperty("summary").GetString(), "a\\b\tc \u2014 d", "summary round-trips");
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
        Check.Eq(card.GetProperty("section").GetString(), "server", "in the Server section (not Session Stats)");
        JsonElement section = root.GetProperty("sections")[0];
        Check.Eq(section.GetProperty("id").GetString(), "server", "the Server section is declared");
        Check.Eq(section.GetProperty("label").GetString(), "Server", "its label");
        Check.Eq(section.GetProperty("order").GetInt32(), 50, "after Session Stats, before Map");
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
