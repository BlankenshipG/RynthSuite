using System;
using System.Linq;
using RynthCore.Plugin.RynthAi.Huds;
using RynthCore.Plugin.RynthILT.IltFellowship;
using RynthCore.Plugin.Shared;
using RynthCore.RynthAiTests.Harness;

namespace RynthILT.Tests;

// ILT's fellowship commands (IltFellowship/IltFellowshipCommands.cs, ACECustom /fship), the
// "servercommands" format both plugins compile (Plugins/Shared/ServerCommandShare.cs), and
// RynthAi's store for it (Huds/ServerCommandModel.cs), checked together.
internal static class FellowshipCommandTests
{
    public static void Register(Runner r)
    {
        r.Add("ilt fellowship: the /fship list round-trips to RynthAi", RoundTrip);
        r.Add("ilt fellowship: chat lines quote multi-word names and need a name for create", Compose);
        r.Add("ilt fellowship: bad commands and payloads are dropped", Rejects);
        r.Add("ilt fellowship: RynthAi drops a list that is cleared or stops arriving", Store);
        r.Add("ilt fellowship: RynthCore's verdict decides, else the world name", Active);
    }

    private static void RoundTrip()
    {
        ServerCommandSet? set = ServerCommandShare.Parse(IltFellowshipCommands.Payload(active: true));
        Check.True(set != null, "parsed");
        Check.Eq(set!.Section, "fellowship", "section");
        Check.Eq(set.ServerId, "ilt", "server id");
        Check.Eq(set.ServerName, "Infinite Leaftide", "server name");
        Check.Eq(set.Commands.Count, IltFellowshipCommands.Commands.Count, "every command survives");
        for (int i = 0; i < set.Commands.Count; i++)
            Check.Eq(set.Commands[i], IltFellowshipCommands.Commands[i], "command " + i);
        Check.True(set.Commands.All(c => c.Command.StartsWith("/fship", StringComparison.Ordinal)), "all /fship");

        ServerCommandSet? off = ServerCommandShare.Parse(IltFellowshipCommands.Payload(active: false));
        Check.True(off != null, "the clear parses");
        Check.Eq(off!.Commands.Count, 0, "the clear has no commands");
    }

    private static void Compose()
    {
        ServerCommand create = Find("Create"), invite = Find("Invite"), dismiss = Find("Dismiss"), list = Find("List nearby");
        Check.True(ServerCommandShare.Compose(create, "") == null, "create needs a name");
        Check.True(ServerCommandShare.Compose(create, "   ") == null, "blank is no name");
        Check.Eq(ServerCommandShare.Compose(create, "Leaf"), "/fship create Leaf", "one word");
        Check.Eq(ServerCommandShare.Compose(create, "  Leaf   Hunters "), "/fship create \"Leaf Hunters\"", "quoted, spaces collapsed");
        Check.Eq(ServerCommandShare.Compose(invite, ""), "/fship add", "no name: the selected player");
        Check.Eq(ServerCommandShare.Compose(invite, "Raw Cigam"), "/fship add \"Raw Cigam\"", "multi-word player");
        Check.Eq(ServerCommandShare.Compose(dismiss, "Bo\"b\t"), "/fship remove Bob", "quotes and control characters dropped");
        Check.Eq(ServerCommandShare.Compose(list, "ignored"), "/fship list", "no argument taken");
        Check.True(dismiss.Confirm && Find("Disband").Confirm && Find("Leave").Confirm && Find("Invite landblock").Confirm, "risky ones ask");
        Check.False(invite.Confirm || list.Confirm || create.Confirm, "the rest don't");
        string longName = new string('x', 200);
        Check.Eq(ServerCommandShare.CleanArgument(longName).Length, ServerCommandShare.MaxArgument, "argument capped");
    }

    private static void Rejects()
    {
        Check.True(ServerCommandShare.Parse(null) == null, "null");
        Check.True(ServerCommandShare.Parse("") == null, "empty");
        Check.True(ServerCommandShare.Parse("C\tX\t/fship\t\t\n") == null, "no S line");
        Check.True(ServerCommandShare.Parse("S\t\tilt\tILT\n") == null, "no section");

        string payload = "S\tfellowship\tilt\tILT\n" +
                         "C\tNo slash\tfship list\t\t\n" +
                         "C\tSpace\t/ fship\t\t\n" +
                         "C\tQuote\t/fship add \"x\"\t\t\n" +
                         "C\t\t/fship list\t\tno label\n" +
                         "C\tLong\t/" + new string('a', ServerCommandShare.MaxCommand + 5) + "\t\t\n" +
                         "C\tShort\t/fship\t\n" +
                         "C\tGood\t/fship list\tzqA\ttip\n";
        ServerCommandSet? set = ServerCommandShare.Parse(payload);
        Check.True(set != null, "parsed");
        Check.Eq(set!.Commands.Count, 1, "only the good command");
        Check.Eq(set.Commands[0].Flags, "A", "unknown flags dropped");

        var many = Enumerable.Range(0, 40).Select(i => new ServerCommand("B" + i, "/fship list", "", ""));
        string built = ServerCommandShare.Build(new ServerCommandSet("fellowship", "ilt", "ILT", many.ToList()));
        Check.Eq(ServerCommandShare.Parse(built)!.Commands.Count, ServerCommandShare.MaxCommands, "capped");

        string tabbed = ServerCommandShare.Build(new ServerCommandSet("fellowship", "ilt", "ILT",
            new[] { new ServerCommand("A\tB", "/fship list", "", "line one\nline two") }));
        ServerCommand c = ServerCommandShare.Parse(tabbed)!.Commands[0];
        Check.Eq(c.Label, "A B", "tab in a field becomes a space");
        Check.Eq(c.Tooltip, "line one line two", "newline in a field becomes a space");
    }

    private static void Store()
    {
        ServerCommandModel.Clear();
        long t = 1_000_000;
        ServerCommandModel.Apply(IltFellowshipCommands.Payload(active: true), t);
        Check.True(ServerCommandModel.For("fellowship", t + 1000) != null, "current");
        Check.True(ServerCommandModel.For("FELLOWSHIP", t + 1000) != null, "section id ignores case");
        Check.True(ServerCommandModel.For("char", t + 1000) == null, "other sections empty");
        Check.True(ServerCommandModel.For("fellowship", t + ServerCommandModel.StaleMs + 1) == null, "stale after the window");

        ServerCommandModel.Apply(IltFellowshipCommands.Payload(active: true), t + 10_000);
        Check.True(ServerCommandModel.For("fellowship", t + ServerCommandModel.StaleMs + 1) != null, "a re-send keeps it");
        ServerCommandModel.Apply(IltFellowshipCommands.Payload(active: false), t + 12_000);
        Check.True(ServerCommandModel.For("fellowship", t + 12_000) == null, "the clear removes it");

        ServerCommandModel.Apply(IltFellowshipCommands.Payload(active: true), t);
        ServerCommandModel.Apply("garbage", t);
        Check.True(ServerCommandModel.For("fellowship", t) != null, "garbage changes nothing");
        ServerCommandModel.Clear();
        Check.True(ServerCommandModel.For("fellowship", t) == null, "logout clears");
    }

    private static void Active()
    {
        Check.True(IltFellowshipCommands.IsActive(true, "Coldeve"), "RynthCore says ILT");
        Check.False(IltFellowshipCommands.IsActive(false, "InfiniteLeaftide"), "RynthCore says not ILT");
        Check.True(IltFellowshipCommands.IsActive(null, "InfiniteLeaftide"), "no verdict yet: the world name");
        Check.False(IltFellowshipCommands.IsActive(null, "Coldeve"), "no verdict, another world");
        Check.False(IltFellowshipCommands.IsActive(null, null), "nothing known");
    }

    private static ServerCommand Find(string label) =>
        IltFellowshipCommands.Commands.First(c => c.Label == label);
}
