using RynthCore.Plugin.RynthAi;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The quest loot list (2026-11-01): RynthOracle's Run this quest puts a wiki quest's items on it ("questloot"
// through the plugin-command broker, or /ra questloot), and the looter takes them by exact name first.
internal static class QuestLootTests
{
    public static void Register(Runner r)
    {
        r.Add("quest loot: add, remove, clear, on/off and the replies", Commands);
    }

    private static void Commands()
    {
        QuestLootList.Clear();
        QuestLootList.Enabled = true;
        Check.False(QuestLootList.Wants("Shiny Red Stone"), "empty: nothing wanted");
        var (ok, text) = QuestLootList.Apply("add Shiny Red Stone");
        Check.True(ok && text.Contains("'Shiny Red Stone'"), "added: " + text);
        Check.True(QuestLootList.Wants("shiny red stone") && QuestLootList.Wants(" Shiny Red Stone "), "any case, trimmed");
        Check.False(QuestLootList.Wants("Shiny Red Stones") || QuestLootList.Wants("Red Stone"), "the exact name only");
        Check.True(QuestLootList.Apply("add \"Vial of The Architect's Blood\"").Ok && QuestLootList.Wants("Vial of The Architect's Blood"), "quotes dropped");
        Check.Eq(QuestLootList.Count, 2, "two on the list");
        Check.True(QuestLootList.Apply("add Shiny Red Stone").Text.Contains("already"), "a second add says so");
        Check.True(QuestLootList.Apply("off").Ok && !QuestLootList.Wants("Shiny Red Stone"), "off pauses it");
        Check.True(QuestLootList.Apply("on").Ok && QuestLootList.Wants("Shiny Red Stone"), "on again, not forgotten");
        Check.True(QuestLootList.Apply("list").Text.Contains("Shiny Red Stone"), "list names them");
        Check.True(QuestLootList.Apply("remove Shiny Red Stone").Ok && !QuestLootList.Wants("Shiny Red Stone"), "removed");
        Check.False(QuestLootList.Apply("remove Nothing").Ok, "removing what isn't there fails");
        Check.False(QuestLootList.Apply("add").Ok || QuestLootList.Apply("frobnicate").Ok, "usage on a bad line");
        Check.True(QuestLootList.Apply("clear").Ok && QuestLootList.Count == 0, "cleared");
        for (int i = 0; i < QuestLootList.MaxItems; i++) QuestLootList.Apply("add Item " + i);
        Check.False(QuestLootList.Apply("add One Too Many").Ok, "capped");
        QuestLootList.Clear();
    }
}
