using System.Collections.Generic;
using RynthCore.Plugin.RynthOracle.Data;

namespace RynthOracle.Tests
{
    // Society quest runs must use the player's CH / EW / RB alternatives (2026-11-22).
    internal static partial class Program
    {
        private static void SocietyQuestResolveTests()
        {
            var ew = new SocietyStanding(0, 150, 0);
            Check(SocietyQuestResolve.Code(ew) == "EW" && SocietyQuestResolve.StrongholdName(ew) == "Eldrytch Web Stronghold",
                "society resolve: EW code and stronghold name");

            var palm = new GuideQuest
            {
                Title = "Palm Fort",
                SocietyRank = "Lord",
                StartsAt = "Moarsman City at: 92.1S, 50.3W (CH) 91.4S, 53.8W (EW) 93.6S, 51.8W (RB)",
                StartsWith = "Fort Commander",
                Steps =
                {
                    new GuideStep
                    {
                        Text = "Speak to the Fort Commander of your respective faction to start.",
                        Coords = { "91.4S, 53.8W", "93.6S, 51.8W", "92.1S, 50.3W" },
                    },
                },
            };
            Check(SocietyQuestResolve.NeedsFilter(palm), "society resolve: Palm Fort needs a filter");
            GuideQuest ewPalm = SocietyQuestResolve.ForPlayer(palm, ew);
            Check(ewPalm.StartsAt.Contains("91.4S, 53.8W", System.StringComparison.OrdinalIgnoreCase)
                  && !ewPalm.StartsAt.Contains("92.1S", System.StringComparison.OrdinalIgnoreCase)
                  && !ewPalm.StartsAt.Contains("93.6S", System.StringComparison.OrdinalIgnoreCase),
                "society resolve: Palm Fort StartsAt keeps only EW coords");
            Check(ewPalm.Steps[0].Coords.Count == 1
                  && ewPalm.Steps[0].Coords[0].Contains("91.4S", System.StringComparison.OrdinalIgnoreCase),
                "society resolve: Palm Fort step coords keep only EW fort");

            var dark = new GuideQuest
            {
                Title = "Dark Isle Delivery",
                SocietyRank = "Initiate",
                StartsAt = "Faction Stronghold (east wing)",
                StartsWith = "Erline the Fleet (CH) Aisley Swiftfoot (EW) Rheda the Watcher (RB)",
                Steps =
                {
                    new GuideStep { Text = "Speak to the appropriate NPC in your Faction Stronghold to receive an ." },
                },
            };
            GuideQuest chDark = SocietyQuestResolve.ForPlayer(dark, new SocietyStanding(50, 0, 0));
            Check(chDark.StartsWith.Contains("Erline", System.StringComparison.OrdinalIgnoreCase)
                  && !chDark.StartsWith.Contains("Aisley", System.StringComparison.OrdinalIgnoreCase)
                  && !chDark.StartsWith.Contains("Rheda", System.StringComparison.OrdinalIgnoreCase),
                "society resolve: Dark Isle StartsWith keeps only CH NPC");
            Check(chDark.StartsAt.StartsWith("Celestial Hand Stronghold", System.StringComparison.Ordinal),
                "society resolve: Faction Stronghold → Celestial Hand Stronghold");
            Check(chDark.Steps[0].Text.Contains("Celestial Hand Stronghold", System.StringComparison.Ordinal),
                "society resolve: step text rewrites Faction Stronghold");

            string hint = QuestRunner.PortalHint(chDark.Steps[0], new SocietyStanding(50, 0, 0));
            Check(hint.Contains("Celestial", System.StringComparison.OrdinalIgnoreCase)
                  && hint.Contains("Hand", System.StringComparison.OrdinalIgnoreCase)
                  && hint.Contains("Stronghold", System.StringComparison.OrdinalIgnoreCase),
                "society resolve: PortalHint prefers society stronghold words: " + hint);

            // Not a member: leave the wiki page alone.
            GuideQuest raw = SocietyQuestResolve.ForPlayer(dark, default);
            Check(ReferenceEquals(raw, dark), "society resolve: non-member keeps the original quest");
        }
    }
}
