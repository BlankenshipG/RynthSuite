using RynthCore.Plugin.RynthAi.Huds;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Mini Remote right-click menu (2026-10-11): RynthCore pushes its server sections (which server
// the client is on, and the installed server plugins' windows for it) as a tab-separated
// "servermenu" payload; RynthAi parses it for the menu's bottom half.
internal static class ServerMenuTests
{
    public static void Register(Runner r)
    {
        r.Add("server menu: sections and items parse in order", ParsesSections);
        r.Add("server menu: malformed lines are skipped, never thrown", SkipsMalformed);
        r.Add("server menu: tab features name the Mini Remote tab", TabFeature);
        r.Add("mini remote tabs: Quests and Pets are universal and first; Guardian is ILT's panel", UniversalTabs);
    }

    private static void UniversalTabs()
    {
        var tabs = RynthCore.Plugin.RynthAi.IltHub.IltSections.Tabs;
        Check.Eq(tabs[0], RynthCore.Plugin.RynthAi.IltHub.IltSection.Quests, "Quests leads the tab bar");
        Check.Eq(tabs[1], RynthCore.Plugin.RynthAi.IltHub.IltSection.Pet, "Pets second");
        int universal = 0;
        foreach (var t in tabs)
            if (RynthCore.Plugin.RynthAi.IltHub.IltSections.IsUniversal(t)) universal++;
        Check.Eq(universal, 2, "only Quests and Pets are universal");
        Check.True(!RynthCore.Plugin.RynthAi.IltHub.IltSections.IsUniversal(RynthCore.Plugin.RynthAi.IltHub.IltSection.Guardian), "Guardian is ILT only");
        Check.True(!RynthCore.Plugin.RynthAi.IltHub.IltSections.IsTab(RynthCore.Plugin.RynthAi.IltHub.IltSection.Guardian), "Guardian is a panel, not a tab");
        Check.True(!RynthCore.Plugin.RynthAi.IltHub.IltSections.IsUniversal(RynthCore.Plugin.RynthAi.IltHub.IltSection.Character), "the Character tab is ILT's");
    }

    private static void ParsesSections()
    {
        const string payload =
            "S\tilt\tInfinite Leaftide\t1\t/rc ilt on\n" +
            "I\tQuests\t/rc miniremote tab quests\tThe Quests tab.\ttab:quests\n" +
            "I\tBounties\t/rc bounties toggle\tBounties.\t\n" +
            "S\tother\tOther World\t0\t\n" +
            "I\tThing\t/rc thing\t\t\n";
        var groups = ServerMenuModel.Parse(payload);
        Check.Eq(groups.Length, 2, "two sections");
        Check.Eq(groups[0].Id, "ilt", "first section id");
        Check.Eq(groups[0].Name, "Infinite Leaftide", "first section name");
        Check.True(groups[0].Active, "first section is active");
        Check.Eq(groups[0].ForceCommand, "/rc ilt on", "force command");
        Check.Eq(groups[0].Items.Length, 2, "first section has its two items");
        Check.Eq(groups[0].Items[1].Command, "/rc bounties toggle", "item command");
        Check.Eq(groups[0].Items[1].Feature, "", "empty feature stays empty");
        Check.True(!groups[1].Active, "second section is not active");
        Check.Eq(groups[1].Items.Length, 1, "items attach to the section above them");
    }

    private static void SkipsMalformed()
    {
        Check.Eq(ServerMenuModel.Parse(null).Length, 0, "null payload = no sections");
        Check.Eq(ServerMenuModel.Parse("").Length, 0, "empty payload = no sections");
        const string payload =
            "I\tOrphan\t/rc x\t\t\n" +           // before any section
            "S\tshort\n" +                        // too few fields
            "S\tilt\tInfinite Leaftide\t0\t\r\n" + // CRLF tolerated
            "X\tunknown\tkind\t\t\n" +
            "I\t\t/rc nolabel\t\t\n" +            // no label
            "I\tGuardian\t/rc guardian toggle\t\t\n";
        var groups = ServerMenuModel.Parse(payload);
        Check.Eq(groups.Length, 1, "only the well-formed section");
        Check.Eq(groups[0].Items.Length, 1, "only the labelled item under it");
        Check.Eq(groups[0].Items[0].Label, "Guardian", "the kept item");
    }

    private static void TabFeature()
    {
        Check.Eq(ServerMenuModel.TabOf("tab:quests"), "quests", "tab id");
        Check.True(ServerMenuModel.TabOf("tab:") == null, "no id = not a tab");
        Check.True(ServerMenuModel.TabOf("") == null, "no feature = not a tab");
        Check.True(ServerMenuModel.TabOf("panel:x") == null, "other features are not tabs");
    }
}
