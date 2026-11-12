using System;
using System.IO;
using System.Text.Json;
using RynthCore.Plugin.RynthOracle;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Views;

namespace RynthOracle.Tests
{
    // RynthOracle without one server's features (2026-10-20: Conquest's went to RynthConquest,
    // Aelrynth's to RynthAelrynth): the settings file keeps what moved, so the server plugin can
    // take it over once, and nothing of those servers is left in RynthOracle itself.
    internal static partial class Program
    {
        private static void ServerMove()
        {
            SettingsKeepUnknown();
            ConquestGone();
            AelrynthGone();
            IltGone();
        }

        /// <summary>
        /// Infinite Leaftide (2026-10-21, gated 2026-10-25): its charms, pet breeding, /qb list page and
        /// feature list are RynthILT's own pages; RynthOracle keeps only the Dynamic Stamps and Temple
        /// guardian rows of the Quests list (read from RynthILT), shown only while RynthCore's active
        /// server is "ilt". No server tags, no Pet Breeding tab, the same twelve tabs everywhere.
        /// </summary>
        private static void IltGone()
        {
            const System.Reflection.BindingFlags All = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            foreach (string field in new[] { "Charms", "Breeding", "ToggleCharm", "BreedingCommand", "UbGiveAvailable" })
                Check(typeof(OracleContext).GetField(field, All) == null, "ilt: OracleContext has no " + field);
            Check(typeof(OracleContext).Assembly.GetType("RynthCore.Plugin.RynthOracle.Data.ServerTags") == null, "ilt: no server tags at all");
            Check(typeof(OracleView).GetField("TabBreeding", All) == null, "ilt: no Pet Breeding tab");
            Check(Enum.GetNames<QuestKind>().Length == 5 && Enum.IsDefined(QuestKind.Dynamic) && Enum.IsDefined(QuestKind.Guardian),
                  "ilt: the Quests list's kinds include Dynamic Stamps and the Temple guardians");

            var c = new OracleContext { InWorld = true };
            var view = new OracleView(c);
            foreach ((string id, string world, int kinds) in new[] { ("ilt", "InfiniteLeaftide", 5), ("", "InfiniteLeaftide", 3), ("", "Coldeve", 3), ("aelrynth", "Aeshnidae", 3) })
            {
                c.Server.Force(id, world);
                string[] tabs = view.VisibleTabs();
                // T11 Tracking (2026-11-11) is the one tab only Infinite Leaftide has.
                bool ilt = id == "ilt";
                Check(Array.IndexOf(tabs, "Pet Breeding") < 0 && tabs.Length == (ilt ? 13 : 12) && (Array.IndexOf(tabs, OracleView.TabT11) >= 0) == ilt,
                      $"ilt: {world} ({id}): the same twelve tabs (Gear included), and T11 Tracking on Infinite Leaftide only: " + string.Join(",", tabs));
                Check(c.Server.IsIlt == (id == "ilt"), $"ilt: {world} ({id}): IsIlt only for RynthCore's \"ilt\" server");
                Check(view.AvailableKinds().Count == kinds, $"ilt: {world} ({id}): {kinds} kinds of row (stamps and guardians only on ilt)");
            }
        }

        /// <summary>Aelrynth without RynthAelrynth: no leaderboards or Trials in RynthOracle (the quest pack stays, as data).</summary>
        private static void AelrynthGone()
        {
            Check(typeof(RynthOraclePlugin).Assembly.GetType("RynthCore.Plugin.RynthOracle.Data.AelrynthBoards") == null,
                  "aelrynth: the /top and /trial parser is not in RynthOracle");
            Check(typeof(OracleContext).GetField("Boards") == null, "aelrynth: RynthOracle keeps no leaderboard data");
            var c = new OracleContext { InWorld = true };
            var view = new OracleView(c);
            c.Server.Force("aelrynth", "Aeshnidae", "RynthAelrynth");
            string[] tabs = view.VisibleTabs();
            Check(Array.IndexOf(tabs, "Leaderboards") < 0, "aelrynth: no Leaderboards tab in RynthOracle on Aelrynth: " + string.Join(",", tabs));
            // The quest pack is RynthAelrynth's since 2026-10-21; RynthOracle merges it as offered.
            c.Catalog.Load(QuestCatalog.SourceFor(c.Server, TestPacks.Offer("aelrynth", "Aelrynth", "RynthAelrynth")), null);
            Check(c.Catalog.PackServer == "Aelrynth" && c.Catalog.ServerCount > 0 && c.Catalog.PackSource == "from RynthAelrynth",
                  "aelrynth: RynthAelrynth's quest pack, merged as offered");
        }

        /// <summary>
        /// Conquest without RynthConquest: no Conquest data, setting, auto-deposit or tab in RynthOracle,
        /// and the Void DoT rule comes only from the server plugin's profile.
        /// </summary>
        private static void ConquestGone()
        {
            const System.Reflection.BindingFlags All = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            Check(typeof(OracleSettings).GetField("ConquestAutoDeposit", All) == null, "conquest: RynthOracle has no auto-deposit setting");
            Check(typeof(OracleContext).GetField("Conquest", All) == null, "conquest: RynthOracle keeps no Conquest data");
            Check(typeof(RynthOraclePlugin).GetMethod("TickConquest", All) == null, "conquest: RynthOracle's tick has no Conquest part (no auto-deposit, no /augs)");
            Check(typeof(RynthOraclePlugin).Assembly.GetType("RynthCore.Plugin.RynthOracle.Data.ConquestData") == null
                  && typeof(RynthOraclePlugin).Assembly.GetType("RynthCore.Plugin.RynthOracle.Data.CustomQuestList") == null,
                  "conquest: the Conquest reply parser and custom quest list are not in RynthOracle");

            // Conquest tagged by its world name (RynthConquest not installed): retail tabs only.
            var c = new OracleContext { InWorld = true };
            var view = new OracleView(c);
            c.Server.Force("conquest", "Conquest");
            string[] tabs = view.VisibleTabs();
            Check(Array.IndexOf(tabs, "Conquest") < 0 && Array.IndexOf(tabs, "Leaderboards") < 0,
                  "conquest: no Conquest pages in RynthOracle on Conquest: " + string.Join(",", tabs));

            // The Void DoT rule: whatever the server plugin publishes (1 to 10), 1 without one.
            Check(RynthOraclePlugin.VoidScaleFrom(null) == 1.0 && RynthOraclePlugin.VoidScaleFrom("") == 1.0, "void scale: 1 without a profile");
            Check(RynthOraclePlugin.VoidScaleFrom("{\"id\":\"coldeve\",\"values\":{}}") == 1.0, "void scale: 1 for a server without the rule");
            Check(Math.Abs(RynthOraclePlugin.VoidScaleFrom("{\"id\":\"conquest\",\"values\":{\"voidDotDurationScale\":2.15}}") - 2.15) < 1e-12,
                  "void scale: RynthConquest's value is applied");
            Check(RynthOraclePlugin.VoidScaleFrom("{\"values\":{\"voidDotDurationScale\":0.5}}") == 1.0
                  && RynthOraclePlugin.VoidScaleFrom("{\"values\":{\"voidDotDurationScale\":50}}") == 1.0,
                  "void scale: a value out of 1..10 is ignored");
            var v = new VoidTracker();
            v.OnChatLine("You cast Corrosion VII on Drudge Slinker", 1_000_000, _ => 0x50000001u, () => false, 2.0);
            Check(v.Remaining(0x50000001u, "Drudge Slinker", VoidFamily.Corrosion, 1_001_000, out _) == 31,
                  "void scale: a scaled Corrosion lasts 30 s (+2 s before the first tick)");
        }

        /// <summary>A value this version doesn't use survives a load and a save, unchanged.</summary>
        private static void SettingsKeepUnknown()
        {
            string? saved = OracleSettings.PathOverride;
            string path = Path.Combine(Path.GetTempPath(), "RynthOracleTests-" + Guid.NewGuid().ToString("N") + "-keep.json");
            OracleSettings.PathOverride = path;
            try
            {
                File.WriteAllText(path, "{ \"hideOwnQuestReplies\": false, \"conquestAutoDeposit\": true, \"fromTheFuture\": {\"x\": [1, \"two\"]}, \"tabName\": \"Void\" }");
                var s = new OracleSettings();
                s.Load();
                Check(!s.HideOwnQuestReplies && s.TabName == "Void", "settings: known values read as before");
                s.MainWindowOpen = true;
                s.Save();
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path)))
                {
                    JsonElement r = doc.RootElement;
                    Check(r.TryGetProperty("conquestAutoDeposit", out JsonElement cq) && cq.ValueKind == JsonValueKind.True,
                          "settings: a moved setting (conquestAutoDeposit) is still in the file after a save");
                    Check(r.TryGetProperty("fromTheFuture", out JsonElement f) && f.GetProperty("x").GetArrayLength() == 2,
                          "settings: an unknown value is written back unchanged");
                    Check(r.GetProperty("mainWindowOpen").GetBoolean(), "settings: the change was saved");
                }
                var again = new OracleSettings();
                again.Load();
                again.Save();
                again.Save();
                string text = File.ReadAllText(path);
                Check(text.IndexOf("fromTheFuture", StringComparison.Ordinal) == text.LastIndexOf("fromTheFuture", StringComparison.Ordinal),
                      "settings: kept values aren't duplicated by repeated saves");
            }
            finally
            {
                try { File.Delete(path); } catch { }
                OracleSettings.PathOverride = saved;
            }
        }
    }
}
