using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using RynthCore.Engine.UI.ScriptWindows;
using RynthCore.Plugin.RynthOracle;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;
using RynthCore.Plugin.RynthOracle.Views;
using IltBreedingView = RynthCore.Plugin.Shared.IltBreedingView;

// The engine parser's only outside dependency: the registry's limits (the engine's values).
namespace RynthCore.Engine.UI.ScriptWindows
{
    internal static class ScriptWindowRegistry
    {
        public const int MaxOpsPerWindow = 1500;
        public const int MaxBytesPerWindow = 64 * 1024;
        public const int MaxWindowsPerOwner = 32;
        public const int MaxBytesPerSubmit = 256 * 1024;
    }
}

namespace RynthOracle.Tests
{
    internal static class Program
    {
        private static int _pass, _fail;

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Write")]
        private static extern int Write(UiHost h);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_submit")]
        private static extern ref byte[] SubmitBuffer(UiHost h);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Apply")]
        private static extern void Apply(UiHost h, ReadOnlySpan<byte> data);

        private static int Main()
        {
            Lists();
            QuestLines();
            Catalog();
            Servers();
            EngineServers();
            IltProfile();
            QuestNotes();
            ConquestReplies();
            AelrynthReplies();
            Void();
            Pages();
            CharacterExtras();
            Breeding();
            Trackers();
            Events();
            Console.WriteLine($"{_pass} passed, {_fail} failed");
            return _fail == 0 ? 0 : 1;
        }

        private static void Check(bool ok, string what)
        {
            if (ok) _pass++;
            else { _fail++; Console.WriteLine("FAIL: " + what); }
        }

        /// <summary>The Legendary tab's list (legendary.csv) and its have / missing / timer rules.</summary>
        private static void Legendary()
        {
            Check(LegendaryList.All.Count == 40, $"legendary quests: {LegendaryList.All.Count} (40 expected)");
            Check(LegendaryList.Groups.SequenceEqual(new[] { "Graveyard", "Rynthid", "Hoshino", "Frozen Valley", "Neftet", "Random Locations" }),
                  "legendary groups: " + string.Join(",", LegendaryList.Groups));
            Check(LegendaryList.All.All(q => q.Flags.Length > 0 && q.Luminance > 0), "every legendary quest has a flag and luminance");
            var catalog = new QuestCatalog();
            catalog.Load("", _ => { });
            var unknown = LegendaryList.All.SelectMany(q => q.Flags).Where(f => !catalog.TryGet(f, out _)).ToList();
            Check(unknown.Count == 0, "every legendary flag is a known quest flag: " + string.Join(",", unknown));
            LegendaryQuest rtw = LegendaryList.All.First(q => q.CommonName == "RTW");
            Check(rtw.Flags[0] == "rtwcompleted_1013" && rtw.Label == "Defeating the Curator of Torment (RTW)", "RTW row: " + rtw.Label);

            var lf = new QuestFlagStore();
            long t = 900_000;
            long done = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds();
            long old = DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeSeconds();
            // RTW done 2 h ago on a 20 h timer; End of Days done 3 days ago (ready); Lugian Assault never.
            lf.OnChatLine($"rtwcompleted_1013 - 4 solves ({done})\"RTW\" -1 72000", t);
            lf.OnChatLine($"eodcompleted_1013 - 1 solves ({old})\"EoD\" -1 72000", t + 1);
            lf.OnChatLine($"legendaryquestcounter_0913 - 12 solves ({old})\"John\" -1 0", t + 2);
            lf.Tick(t + 5_000);
            DateTime now = DateTime.UtcNow;
            TimeSpan? rtwLeft = rtw.TimeLeft(lf, now);
            Check(rtw.Have(lf) && rtwLeft is { } l && l.TotalHours > 17.9 && l.TotalHours < 18.1, $"RTW: done, ~18 h left ({rtwLeft})");
            LegendaryQuest eod = LegendaryList.All.First(q => q.CommonName == "EoD");
            Check(eod.Have(lf) && eod.Status(lf, now) == "ready", "EoD: done, timer up -> ready: " + eod.Status(lf, now));
            LegendaryQuest la = LegendaryList.All.First(q => q.CommonName == "LA");
            Check(!la.Have(lf) && la.TimeLeft(lf, now) == null && la.Status(lf, now) == "missing", "Lugian Assault: missing");
            Check(LegendaryList.JohnCount(lf) == 12, "John's counter read: " + LegendaryList.JohnCount(lf));
            Check(LegendaryList.JohnCount(new QuestFlagStore()) == null, "no counter on a fresh character");

            // A quest with a second timer flag: the longest wait of the two counts.
            LegendaryQuest erq = LegendaryList.All.First(q => q.Name == "Empyrean Rescue Quest");
            var ef = new QuestFlagStore();
            ef.OnChatLine($"empyreanrescuecompleted_1010 - 1 solves ({old})\"ERQ\" -1 0", t);
            ef.OnChatLine($"erqlegkeypickuptimer - 1 solves ({done})\"key\" -1 36000", t + 1);
            ef.Tick(t + 5_000);
            TimeSpan? erqLeft = erq.TimeLeft(ef, now);
            Check(erqLeft is { } e && e.TotalHours > 7.9 && e.TotalHours < 8.1, $"ERQ: key pickup timer decides (~8 h): {erqLeft}");

            // Since 2026-10-18 Legendary quests and markers are rows of the Quests tab, on every server.
            var lc = new OracleContext { InWorld = true };
            var lv = new OracleView(lc);
            lc.Server.Force("", "Coldeve");
            Check(lv.AvailableKinds().Contains(QuestKind.Legendary) && lv.AvailableKinds().Contains(QuestKind.Marker),
                  "Legendary and marker rows on every server");
            Check(!lv.VisibleTabs().Contains("Legendary") && !lv.VisibleTabs().Contains("Markers"), "no separate Legendary / Markers tabs");
        }

        /// <summary>The Dynamic Stamps tab's list (dynstamps.csv), its have / missing rules, and that the
        /// tab is Infinite Leaftide only.</summary>
        private static void DynamicStamps()
        {
            var all = DynamicStampList.All;
            Check(all.Count == 243, $"dynamic stamps: {all.Count} (243 expected from the ExtraDyn sheet)");
            Check(DynamicStampList.Areas.Count == 17 && DynamicStampList.Areas[0] == "Facility Hub" && DynamicStampList.Areas[^1] == "Candeth Keep",
                  "dynamic areas: " + string.Join(",", DynamicStampList.Areas));
            Check(all.All(s => s.Stamp.StartsWith("dynamic_") && s.Stamp == s.Stamp.ToLowerInvariant()), "every stamp is a lower-case dynamic_ flag");
            Check(all.Select(s => s.Stamp).Distinct().Count() == all.Count, "no stamp listed twice");
            Check(all.All(s => s.Label.Length > 0), "every row names an NPC or a place");

            DynamicStamp felscuda = all.First(s => s.Npc == "Felscuda");
            Check(felscuda.Stamp == "dynamic_1_&9jiytb!" && felscuda.Place == "Folthid Cellar" && felscuda.Reward == "Healing Rabbit Pie"
                  && felscuda.Note == "8.8S, 53.1E", $"Felscuda row: {felscuda.Place} / {felscuda.Reward} / {felscuda.Note}");

            // /myquests keys flags as \S+ lower case, so the stamps' ! & # ^ $ survive.
            var f = new QuestFlagStore();
            long t = 900_000, at = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
            f.OnChatLine($"Dynamic_1_&9jiyTB! - 1 solves ({at})\"Dynamic\" 1 0", t);
            f.OnChatLine($"Dynamic_1_ANND! - 1 solves ({at})\"Dynamic\" 1 0", t + 1);        // the sheet lost the closing !
            f.OnChatLine($"Dynamic_1_ZZZnewNPC! - 1 solves ({at})\"Dynamic\" 1 0", t + 2);   // not on the sheet
            f.OnChatLine($"rtwcompleted_1013 - 1 solves ({at})\"RTW\" -1 72000", t + 3);
            f.Tick(t + 5_000);
            Check(felscuda.Have(f), "Felscuda's stamp read from /myquests");
            DynamicStamp annd = all.First(s => s.Stamp == "dynamic_1_annd");
            Check(annd.Have(f) && annd.MatchedFlag(f) == "dynamic_1_annd!", "a stamp the sheet lost its ! on still matches");
            Check(!all.First(s => s.Npc == "Raxanza Folthid").Have(f), "an NPC not done is missing");
            List<string> extra = DynamicStampList.Unlisted(f);
            Check(extra.Count == 1 && extra[0] == "dynamic_1_zzznewnpc!", "unlisted: only the stamp the sheet doesn't have: " + string.Join(",", extra));
            DynamicStamp shortStamp = all.First(s => s.Stamp == "dynamic_1_s");
            var sf = new QuestFlagStore();
            sf.OnChatLine($"Dynamic_1_SomethingElse! - 1 solves ({at})\"Dynamic\" 1 0", t);
            sf.Tick(t + 5_000);
            Check(!shortStamp.Have(sf), "a cut-short stem under 14 characters isn't matched by prefix");

            // Quest walkthroughs: every coordinate pair, labelled with its step, duplicates once.
            var grael = NavTargets.Stops("Give Progenitor's Ichor to Hoshar. - Speak with Hoshar ibn Jalaq 59.6S 88.0W - Kill Mukkir Progenitor 86.5N 57.3E"
                                         + " - Speak with Hoshar ibn Jalaq - Abyssal Totem Gateway 86.5N 57.3E - Abyssal Totem Temple 86.5N 57.3E");
            Check(string.Join(" | ", grael.ConvertAll(s => s.Display)) == "Speak with Hoshar ibn Jalaq - 59.6S, 88.0W | Kill Mukkir Progenitor - 86.5N, 57.3E",
                  "Splitting Grael stops: " + string.Join(" | ", grael.ConvertAll(s => s.Display)));
            Check(grael[0].Target == "59.6S, 88.0W Speak with Hoshar ibn Jalaq", "a stop's RynthNav target is the coordinates, then its label");
            var shadow = NavTargets.Stops("Xarabydun 41.8S 15.8E - Speak with Ciandra Arcanum Alchemist - Empyrean Workshop 66.9N 15.9E - Cragstone 26.0N 47.0E Speak with Bretself the Translator - Xarabydun - Shade of Farelaith 74.3N 58.6E");
            Check(string.Join(" | ", shadow.ConvertAll(s => s.Label)) == "Xarabydun | Empyrean Workshop | Cragstone | Shade of Farelaith",
                  "Shadowfire Stone labels: " + string.Join(" | ", shadow.ConvertAll(s => s.Label)));
            Check(NavTargets.Stops("Kill 20 drudges near Holtburg.").Count == 0, "no coordinates: no stops");
            Check(NavTargets.Stops("Meet at 42.6n 62.4e").Count == 1 && NavTargets.Stops("Meet at 42.6n 62.4e")[0].Display == "Meet - 42.6N, 62.4E",
                  "lower-case letters; 'at' dropped from the label");

            // RynthNav targets, best first (RynthNav takes the first on its map).
            Check(string.Join(" | ", felscuda.NavCandidates()) == "8.8S, 53.1E | Folthid Cellar | Felscuda | Facility Hub",
                  "Felscuda nav: " + string.Join(" | ", felscuda.NavCandidates()));
            var gerent = new DynamicStamp { Area = "Graveyard", Place = "Gerent's Compound at 87.2S, 17.5W", Npc = "Gerent" };
            Check(string.Join(" | ", gerent.NavCandidates()) == "87.2S, 17.5W | Gerent's Compound | Gerent | Graveyard",
                  "coordinates inside the place, 'at' dropped: " + string.Join(" | ", gerent.NavCandidates()));
            var lost = new DynamicStamp { Area = "Odd Stamps", Place = "Lost Wish Cottages (Arwic) 42.6n 62.4e", Npc = "Bob" };
            Check(string.Join(" | ", lost.NavCandidates()) == "42.6N, 62.4E | Lost Wish Cottages (Arwic) | Lost Wish Cottages | Arwic",
                  "lower-case n/e, a bracketed town, no grouping area, a 3-letter NPC left out: " + string.Join(" | ", lost.NavCandidates()));
            var mhoire = new DynamicStamp { Area = "Looking Around", Place = "Mhoire Castle - Great Hall" };
            Check(string.Join(" | ", mhoire.NavCandidates()) == "Mhoire Castle - Great Hall | Mhoire Castle | Great Hall",
                  "a place's parts: " + string.Join(" | ", mhoire.NavCandidates()));
            Check(all.Count(s => s.NavCandidates().Count > 0) >= 200, "most stamps have somewhere to go: " + all.Count(s => s.NavCandidates().Count > 0));

            // Since 2026-10-18 the stamps are rows of the Quests tab, on Infinite Leaftide only (the guardians too).
            var dc = new OracleContext { InWorld = true };
            var dv = new OracleView(dc);
            dc.Server.Force("", "Coldeve");
            Check(!dv.AvailableKinds().Contains(QuestKind.Dynamic) && !dv.AvailableKinds().Contains(QuestKind.Guardian),
                  "no stamp or guardian rows off Infinite Leaftide");
            Check(!dv.VisibleTabs().Contains("Dynamic Stamps"), "no separate Dynamic Stamps tab");
            dc.Server.Force(ServerTags.InfiniteLeaftide, "InfiniteLeaftide");
            Check(dv.AvailableKinds().Contains(QuestKind.Dynamic) && dv.AvailableKinds().Contains(QuestKind.Guardian),
                  "stamp and guardian rows on Infinite Leaftide");

            // Show: Dynamic Stamps: all 243 listed and paged, so the page stays under the engine's ops limit.
            var host = new UiHost();
            UiWindow main = host.Add(new UiWindow("RynthOracle/Oracle", "Oracle") { Visible = true });
            dv.ShowTrackers(QuestKind.Dynamic);
            main.Begin(); dv.Draw(main); main.End();
            ParseAll(host, "Quests: Dynamic Stamps", out int ops);
            string body = Encoding.UTF8.GetString(main.Body);
            Check(ops > 40 && ops < 1400, $"Quests showing Dynamic Stamps: {ops} ops");
            Check(body.Contains($"{all.Count} shown") && body.Contains("listed Dynamic stamps") && body.Contains("DYN")
                  && body.Contains("Page 1 of 7") && !body.Contains("list cut short"),
                  "every stamp listed, 40 a page, with the stamps' status above");
            Console.WriteLine($"  InfiniteLeaftide/Quests showing Dynamic Stamps ({all.Count} rows, paged): {ops} ops, {main.Body.Length} bytes");
        }

        private static void Lists()
        {
            Check(AugmentationList.All.Count == 88, $"augmentations: {AugmentationList.All.Count} rows (88 expected)");
            Check(AugmentationList.Quests.Count == 6, $"aug gem quests: {AugmentationList.Quests.Count}");
            Check(SocietyQuestList.All.Count == 54, $"society rows: {SocietyQuestList.All.Count}");
            Check(TitleList.All.Count == 831, $"titles: {TitleList.All.Count}");
            Check(MarkerList.All.Count == 100, $"markers: {MarkerList.All.Count}");
            var mf = new QuestFlagStore();
            mf.OnChatLine("explorationmarkersfoundingroupa - 129 solves (1719432000)\"Markers\" 1023 0", 0);
            mf.Tick(5000);
            Check(MarkerList.All[0].IsFound(mf) && MarkerList.All[1].IsFound(mf) && !MarkerList.All[2].IsFound(mf), "marker bits 1 and 128 found, the rest not");
            Legendary();
            DynamicStamps();
            Check(SpellTable.Count > 6000, $"spell table: {SpellTable.Count} spells");
            Check(SpellTable.Name(5393) == "Corrosion VII", "spell 5393 is Corrosion VII: " + SpellTable.Name(5393));
            Check(SpellTable.TryGet(5395, out SpellInfo s) && s.IsDebuff && s.Duration == 31, "Corruption I is a 31 s debuff");
            Check(SpellTable.TryGet(4305, out SpellInfo f) && !f.IsDebuff && f.Duration == 5400, "Incantation of Focus Self is a 5400 s buff");
        }

        private static void QuestLines()
        {
            // ACE PlayerCommands.HandleQuests: no space between ")" and the quote.
            QuestFlag? q = QuestFlagStore.Parse("stipendtimer_0812 - 3 solves (1759276800)\"Monthly stipend\" -1 2592000");
            Check(q != null && q.Key == "stipendtimer_0812" && q.Solves == 3 && q.MaxSolves == -1
                  && q.RepeatTime == TimeSpan.FromSeconds(2592000) && q.CompletedOnUtc == DateTime.UnixEpoch.AddSeconds(1759276800)
                  && q.Description == "Monthly stipend", "ACE /myquests line");
            QuestFlag? t = QuestFlagStore.Parse("[12:34:56] blankquestflag - 1 solves (1719432000)\"Some Quest\" 5 82800");
            Check(t != null && t.Key == "blankquestflag" && t.MaxSolves == 5, "line with a chat timestamp");
            Check(QuestFlagStore.Parse("Bob says, \"somequest - 1 solves (0)\"") == null, "a chat line quoting a flag is not a flag");
            QuestFlag? capped = QuestFlagStore.Parse("onetimer - 1 solves (1719432000)\"One time\" 1 0");
            Check(capped != null && capped.AtSolveLimit, "solve limit reached");

            var store = new QuestFlagStore();
            long now = 100_000;
            Check(store.OnChatLine("alpha - 2 solves (1719432000)\"A\" -1 72000", now) == MyQuestsLine.Quest, "burst line 1");
            Check(store.OnChatLine("beta - 1 solves (1719432000)\"B\" 1 0", now + 100) == MyQuestsLine.Quest, "burst line 2");
            store.Tick(now + 500);
            Check(store.Flags.Count == 0 && store.Loading, "not complete while lines are still arriving");
            store.Tick(now + 2000);
            Check(store.Flags.Count == 2 && !store.Loading && store.LastReadUtc != DateTime.MinValue, "burst completes after a quiet spell");
            Check(store.OnChatLine("Quest list is empty.", now + 3000) == MyQuestsLine.Empty, "empty reply");
            store.Tick(now + 3001);
            Check(store.Flags.Count == 0, "an empty reply clears the flags");
            Check(store.OnChatLine(QuestFlagStore.DisabledText, now + 4000) == MyQuestsLine.Disabled && store.ServerDisabled, "disabled reply");
            Check(store.OnChatLine("You say, \"hello\"", now) == MyQuestsLine.None, "ordinary chat ignored");

            // Our own request: the typed-command bookkeeping.
            var s2 = new QuestFlagStore();
            int sent = 0;
            Check(s2.Request(cmd => { sent++; return cmd == "/myquests"; }, 50_000) && s2.AwaitingOurs, "request sends /myquests");
            Check(!s2.Request(_ => { sent++; return true; }, 51_000) && sent == 1, "a second request within 5 s is skipped");
            s2.NoteTypedCommand(); // our own send reaching the chat bar
            s2.Tick(51_100);
            Check(s2.AwaitingOurs, "our own send doesn't count as typed by the player");
            s2.NoteTypedCommand(); // the player typing it
            s2.Tick(51_200);
            Check(!s2.AwaitingOurs, "a typed /myquests shows its reply");

            // A second reply that lost most of its lines on the way (the engine's chat queue dropped
            // the head of the burst) updates the flags instead of blanking them.
            var s3 = new QuestFlagStore();
            long t3 = 200_000;
            for (int i = 0; i < 100; i++)
                s3.OnChatLine($"flag{i:D3} - 1 solves (1719432000)\"Q{i}\" -1 0", t3 + i);
            s3.Tick(t3 + 5_000);
            Check(s3.Flags.Count == 100, "first full reply: 100 flags");
            long t4 = t3 + 120_000;
            for (int i = 90; i < 100; i++)
                s3.OnChatLine($"flag{i:D3} - 2 solves (1719500000)\"Q{i}\" -1 0", t4 + i);
            s3.Tick(t4 + 5_000);
            Check(s3.Flags.Count == 100 && s3.LastPartialReply is { Got: 10, Had: 100 },
                  "a cut-short reply (10 of 100) is merged, not a replacement");
            Check(s3.TryGet("flag095", out QuestFlag f95) && f95.Solves == 2 && s3.TryGet("flag001", out _),
                  "merged reply updates the lines it had and keeps the rest");
            long t5 = t4 + 120_000;
            for (int i = 0; i < 95; i++)
                s3.OnChatLine($"flag{i:D3} - 3 solves (1719600000)\"Q{i}\" -1 0", t5 + i);
            s3.Tick(t5 + 5_000);
            Check(s3.Flags.Count == 95 && !s3.TryGet("flag099", out _), "a near-full reply still replaces (a dropped flag goes away)");

            // ACECustom's once-a-minute limit: the throttle line ends "Reading..." and keeps the flags.
            int had = s3.Flags.Count;
            long t6 = t5 + 30_000;
            Check(s3.Request(_ => true, t6) && s3.AwaitingOurs, "refresh sent");
            Check(s3.OnChatLine("[MyQuests] This command may only be run once every 60 seconds.", t6 + 200) == MyQuestsLine.Throttled
                  && !s3.AwaitingOurs && s3.Flags.Count == had, "throttle reply: not loading, flags kept");
            Check(s3.ThrottleSecondsLeft(t6 + 1_000) > 50 && !s3.Request(_ => true, t6 + 10_000), "no resend while the server's minute runs");
            Check(s3.ThrottleSecondsLeft(t6 + 61_000) == 0 && s3.Request(_ => true, t6 + 61_000), "refresh allowed again after the minute");
        }

        private static void Catalog()
        {
            var retail = new QuestCatalog();
            retail.Load("", msg => Console.WriteLine(msg));
            bool bundled = retail.UsingBundled;
            Check(bundled || System.IO.File.Exists(QuestCatalog.DownloadedPath), "bundled list used (no download present)");
            if (bundled)
            {
                Check(retail.RetailCount == 3565 && retail.ServerCount == 0 && retail.PackServer == "", $"another server: retail only ({retail.RetailCount}+{retail.ServerCount})");
                var lev = new QuestCatalog();
                lev.Load("Levistras", null);
                Check(lev.RetailCount == 3565 && lev.ServerCount == 464 && lev.PackServer == "Levistras", $"Levistras pack: {lev.RetailCount}+{lev.ServerCount}");
                var cq = new QuestCatalog();
                cq.Load("Conquest", null);
                Check(cq.RetailCount == 3565 && cq.ServerCount == 546, $"Conquest pack: {cq.RetailCount}+{cq.ServerCount}");
                Check(!retail.TryGet("trigram4", out _) && cq.TryGet("trigram4", out Quest tq) && tq.Server == "Conquest", "a Conquest-only quest only loads on ConquestAC");
            }
            Check(QuestCatalog.EmbeddedPacks().OrderBy(x => x).SequenceEqual(new[] { "aelrynth", "conquest", "levistras" }), "embedded packs: " + string.Join(",", QuestCatalog.EmbeddedPacks()));

            var cat = new QuestCatalog();
            cat.Load("Aelrynth", msg => Console.WriteLine(msg));
            if (bundled)
            {
                Check(cat.PackServer == "Aelrynth" && cat.ServerCount == 712 && cat.HiddenCount == 38 && cat.RetailCount == 3565 - 38,
                    $"Aelrynth pack: {cat.RetailCount} retail + {cat.ServerCount}, {cat.HiddenCount} hidden");
                Check(cat.TryGet("30minattributes", out Quest baseQuest) && baseQuest.Name == "Attribute and Skill Redistribution"
                      && baseQuest.Server == "" && baseQuest.MinDelta == 1800 && baseQuest.Source.StartsWith("set by", StringComparison.Ordinal),
                    "a base quest keeps Advis's name and gains Aelrynth's timer and source");
                Check(cat.TryGet("aelrynthcitadelbelowwarlord", out Quest own) && own.Server == "Aelrynth" && own.DisplayName.Contains("Kazrok"),
                    "Aelrynth's own content is in the pack");
                Check(!retail.TryGet("aelrynthcitadelbelowwarlord", out _), "Aelrynth's pack doesn't load elsewhere");
            }

            var flags = new QuestFlagStore();
            flags.OnChatLine("zzzz_custom_aelrynth_flag - 1 solves (1719432000)\"Custom\" -1 3600", 0);
            flags.OnChatLine("30minattributes - 4 solves (1719432000)\"x\" -1 1800", 0);
            flags.Tick(5000);
            cat.SyncDiscoveries(flags);
            Check(cat.TryGet("zzzz_custom_aelrynth_flag", out Quest nq) && nq.IsNew && nq.Name == "Custom", "unknown flag added as new");
            Check(cat.TryGet("30minattributes", out Quest known) && !known.IsNew && known.IsComplete(flags) && known.IsRepeatable(flags), "known flag is complete and repeatable");
            Check(QuestCatalog.Validate(string.Join("\n", Enumerable.Repeat("x", 3)), out _) == false, "validation rejects junk");
        }

        /// <summary>A RynthCore GetServerStateJson answer (schema 1) with the given servers.</summary>
        private static string EngineJson(string active, string activeName, string activePlugin, string pick, string world, params string[] servers)
            => "{\"schema\":1,\"revision\":3,\"host\":\"h\",\"world\":\"" + world + "\",\"pick\":\"" + pick + "\",\"active\":\"" + active
               + "\",\"activeName\":\"" + activeName + "\",\"activePlugin\":\"" + activePlugin + "\",\"servers\":[" + string.Join(",", servers) + "]}";

        private static string EngineRow(string id, bool active, string force, string plugin = "", string reason = "")
            => "{\"id\":\"" + id + "\",\"name\":\"" + id.ToUpperInvariant() + "\",\"active\":" + (active ? "true" : "false")
               + ",\"detected\":" + (active && force == "auto" ? "true" : "false") + ",\"force\":\"" + force
               + "\",\"builtIn\":false,\"plugin\":\"" + plugin + "\",\"reason\":\"" + reason + "\"}";

        // RynthCore's server list (API v86) drives the identity: overrides and the Diagnostics pick included.
        private static void EngineServers()
        {
            // The shared parser.
            Check(RynthCore.Plugin.Shared.EngineServerState.Parse(null) == null && RynthCore.Plugin.Shared.EngineServerState.Parse("junk") == null, "engine state: junk is null");
            Check(RynthCore.Plugin.Shared.EngineServerState.Parse("{\"schema\":2}") == null, "engine state: another schema is null");
            var st = RynthCore.Plugin.Shared.EngineServerState.Parse(EngineJson("ilt", "Infinite Leaftide", "RynthILT", "auto", "InfiniteLeaftide",
                EngineRow("ilt", true, "auto", "RynthILT", "host matches"), EngineRow("aelrynth", false, "auto")));
            Check(st != null && st.HasActive && st.IsActive("ILT") && st.ActivePlugin == "RynthILT" && !st.Picked, "engine state: active ILT, detected");
            Check(st!.Servers.Count == 2 && st.Find("aelrynth") != null && st.Find("x") == null && st.Revision == 3, "engine state: rows");

            // Identity from it.
            ServerIdentity.FromEngine(st, "Launcher Profile", out string tag, out string world, out string source);
            Check(tag == ServerTags.InfiniteLeaftide && world == "InfiniteLeaftide", "engine: ILT active is the ILT tag: " + source);
            Check(source.Contains("host matches"), "engine: the reason is shown: " + source);

            var picked = RynthCore.Plugin.Shared.EngineServerState.Parse(EngineJson("conquest", "ConquestAC", "RynthConquest", "conquest", "",
                EngineRow("ilt", false, "off"), EngineRow("conquest", true, "on", "RynthConquest")))!;
            ServerIdentity.FromEngine(picked, "Leaftide Box", out tag, out world, out source);
            Check(tag == ServerTags.Conquest && world == "Leaftide Box", "engine: a pick beats the world name (and the world falls back): " + source);
            Check(source.Contains("picked by hand"), "engine: a pick says so: " + source);

            var off = RynthCore.Plugin.Shared.EngineServerState.Parse(EngineJson("", "", "", "mixed", "InfiniteLeaftide", EngineRow("ilt", false, "off")))!;
            ServerIdentity.FromEngine(off, "", out tag, out _, out source);
            Check(tag == "", "engine: ILT forced off stays off despite the Leaftide world name: " + source);

            var unknown = RynthCore.Plugin.Shared.EngineServerState.Parse(EngineJson("", "", "", "auto", "Levistras", EngineRow("ilt", false, "auto")))!;
            ServerIdentity.FromEngine(unknown, "", out tag, out _, out source);
            Check(tag == ServerTags.Levistras, "engine: a world the engine has no server for still tags by name: " + source);

            var coldeve = RynthCore.Plugin.Shared.EngineServerState.Parse(EngineJson("coldeve", "Coldeve", "RynthColdeve", "auto", "Coldeve", EngineRow("coldeve", true, "auto", "RynthColdeve")))!;
            ServerIdentity.FromEngine(coldeve, "", out tag, out _, out source);
            Check(tag == "" && source.Contains("Coldeve"), "engine: a server without Oracle pages has no tag: " + source);

            Check(ServerIdentity.TagForEngineId("AELRYNTH") == ServerTags.Aelrynth && ServerIdentity.TagForEngineId(null) == "", "engine id to tag");
            Check(ServerIdentity.EngineIdForTag(ServerTags.InfiniteLeaftide) == "ilt" && ServerIdentity.EngineIdForTag("") == "", "tag to engine id");

            // The owner's profile, read leniently (a server plugin's, and RynthILT's).
            var prof = RynthCore.Plugin.Shared.ServerProfileView.Parse(
                "{\"schema\":1,\"id\":\"coldeve\",\"name\":\"Coldeve\",\"plugin\":\"RynthColdeve\",\"ruleset\":\"PvE\",\"emulator\":\"ACE\",\"botting\":\"forbidden\",\"bottingNote\":\"no bots\",\"accountLimit\":\"3 accounts\"}");
            Check(prof != null && prof.Botting == "forbidden" && prof.Summary() == "PvE, ACE; botting forbidden - no bots; accounts: 3 accounts", "profile: server plugin: " + prof?.Summary());
            var iltProf = RynthCore.Plugin.Shared.ServerProfileView.Parse("{\"ilt\":true,\"features\":[{\"key\":\"bank\",\"state\":1},{\"key\":\"x\",\"state\":0},{\"key\":\"y\",\"state\":1}]}");
            Check(iltProf != null && iltProf.Ilt && iltProf.FeaturesOn == 2 && iltProf.Summary() == "ILT world, 2 Leaftide features on", "profile: RynthILT: " + iltProf?.Summary());
            Check(RynthCore.Plugin.Shared.ServerProfileView.Parse("") == null && RynthCore.Plugin.Shared.ServerProfileView.Parse("[1]") == null, "profile: junk is null");
            // A user's server-name list (ub-Rythai's ILT Auto list) against the engine's verdict.
            string[] list = { "InfiniteLeaftide", "Conquest" };
            Check(RynthCore.Plugin.Shared.EngineServerState.MatchesNameList(null, "Infinite Leaftide", list), "names: no engine, the world name decides (spaces ignored)");
            Check(!RynthCore.Plugin.Shared.EngineServerState.MatchesNameList(null, "Coldeve", list), "names: no engine, another world");
            Check(RynthCore.Plugin.Shared.EngineServerState.MatchesNameList(st, "Launcher Profile", list), "names: ILT active matches a Leaftide name whatever the world");
            Check(RynthCore.Plugin.Shared.EngineServerState.MatchesNameList(picked, "", list), "names: a picked Conquest matches by id");
            Check(!RynthCore.Plugin.Shared.EngineServerState.MatchesNameList(coldeve, "InfiniteLeaftide", list), "names: Coldeve active beats a Leaftide world name");
            Check(!RynthCore.Plugin.Shared.EngineServerState.MatchesNameList(off, "InfiniteLeaftide", list), "names: ILT listed and off stays off");
            Check(RynthCore.Plugin.Shared.EngineServerState.MatchesNameList(unknown, "Conquest", list), "names: a server the engine doesn't list still matches by world");
            Check(RynthCore.Plugin.Shared.ServerStateClient.InterfaceFor("RynthILT") == "RynthILT.Server"
                  && RynthCore.Plugin.Shared.ServerStateClient.InterfaceFor("RynthColdeve") == "RynthServer.Profile", "profile: which interface to ask");
        }

        private static void Servers()
        {
            Check(ServerIdentity.TagForWorld("Aeshnidae") == ServerTags.Aelrynth, "Aeshnidae (the live world name) is Aelrynth");
            Check(ServerIdentity.TagForWorld("Aelrynth Staging") == ServerTags.Aelrynth, "Aelrynth Staging");
            Check(ServerIdentity.TagForWorld("Conquest") == ServerTags.Conquest, "Conquest");
            Check(ServerIdentity.TagForWorld("Levistras") == ServerTags.Levistras, "Levistras");
            Check(ServerIdentity.TagForWorld("Coldeve") == "" && ServerIdentity.TagForWorld("") == "", "other servers have no tag");
            Check(ServerIdentity.TagForWorld("InfiniteLeaftide") == ServerTags.InfiniteLeaftide, "InfiniteLeaftide");
            Check(ServerIdentity.TagForWorld("Infinite Leaftide Staging") == ServerTags.InfiniteLeaftide, "a Leaftide staging world is ILT");
            var id = new ServerIdentity();
            id.Force("", "Coldeve");
            Check(id.Allows(null) && id.Allows("") && !id.Allows(ServerTags.Conquest) && !id.Allows(ServerTags.Aelrynth), "untagged everywhere, tagged nowhere else");
            id.Force(ServerTags.Conquest, "Conquest");
            Check(id.Allows(ServerTags.Conquest) && !id.Allows(ServerTags.Aelrynth), "Conquest features only on ConquestAC");

            // Tabs per server.
            var c = new OracleContext { InWorld = true };
            var view = new OracleView(c);
            c.Server.Force("", "Coldeve");
            string[] none = view.VisibleTabs();
            Check(!none.Contains(OracleView.TabBoards) && !none.Contains(OracleView.TabConquest), "no server tabs on other servers: " + string.Join(",", none));
            c.Server.Force(ServerTags.Aelrynth, "Aeshnidae");
            string[] ael = view.VisibleTabs();
            Check(ael.Contains(OracleView.TabBoards) && !ael.Contains(OracleView.TabConquest), "Aelrynth: leaderboards, no Conquest tab");
            c.Server.Force(ServerTags.Conquest, "Conquest");
            string[] cq = view.VisibleTabs();
            Check(cq.Contains(OracleView.TabBoards) && cq.Contains(OracleView.TabConquest), "ConquestAC: leaderboards and the Conquest tab");
            c.Settings.TabName = OracleView.TabConquest;
            c.Server.Force(ServerTags.Aelrynth, "Aeshnidae");
            Check(view.Tab == OracleView.TabQuests, "a Conquest tab left selected falls back to Quests elsewhere");
        }

        /// <summary>RynthILT's "RynthILT.Server" profile JSON as the Fellowship tab reads it.</summary>
        private static void IltProfile()
        {
            const string json = "{\"ilt\":true,\"world\":\"InfiniteLeaftide\",\"forced\":false,\"source\":\"probe\",\"checked\":\"2026-10-13T21:25:37Z\","
                + "\"features\":[{\"key\":\"bank\",\"label\":\"Banking\",\"group\":\"Character\",\"state\":1,\"where\":\"Mini Remote > Banking tab\"},"
                + "{\"key\":\"zone_share\",\"label\":\"Zone Share\",\"group\":\"Fellowship\",\"state\":1,\"where\":\"server\"},"
                + "{\"key\":\"fellowship_blackjack\",\"label\":\"Fellowship Blackjack\",\"group\":\"Fellowship\",\"state\":0,\"where\":\"/fblackjack\"}],"
                + "\"fellowship\":{\"maxMembers\":29,\"zoneShare\":1,\"blackjack\":0,\"luminanceShare\":-1},\"later\":{\"x\":1}}";
            IltServerProfile p = IltServerProfile.Parse(json);
            Check(p.Available && p.Ilt && p.World == "InfiniteLeaftide" && p.Source == "probe", "ILT profile header");
            Check(p.FellowshipMax == 29 && p.ZoneShare == 1 && p.Blackjack == 0 && p.LuminanceShare == -1, "ILT fellowship rules");
            Check(p.Features.Count == 3 && p.InGroup("Fellowship").Count() == 2, $"ILT features: {p.Features.Count}");
            Check(p.CheckedUtc == new DateTime(2026, 10, 13, 21, 25, 37, DateTimeKind.Utc), "checked time is UTC: " + p.CheckedUtc.ToString("o"));

            IltServerProfile none = IltServerProfile.Parse("");
            Check(!none.Available && none.FellowshipMax == 9, "no RynthILT: a retail fellowship");
            Check(!IltServerProfile.Parse("not json").Available, "garbage is ignored");
            Check(IltServerProfile.Parse("{\"ilt\":false}").FellowshipMax == 9, "an older profile without fellowship rules: retail size");
            Check(IltServerProfile.Parse("{\"fellowship\":{\"maxMembers\":5000}}").FellowshipMax == 9, "an absurd size is not believed");
            Check(IltServerProfile.StateText(1) == "on" && IltServerProfile.StateText(0) == "off" && IltServerProfile.StateText(-1) == "checking", "state words");

            // The /qb list RynthILT 0.2.3 adds to the profile.
            Check(p.QuestBonus == null, "no questBonus object: no /qb list");
            IltServerProfile qb = IltServerProfile.Parse(QbProfileJson);
            Check(qb.QuestBonus is { Disabled: false, Generation: 3, Count: 41 } b && b.Completed.Count == 2 && b.Incomplete.Count == 1
                  && b.Incomplete[0] == "qbflagthree" && b.Status == "2 completed, 1 incomplete", "quest bonus list parsed");
        }

        private const string QbProfileJson = "{\"ilt\":true,\"world\":\"InfiniteLeaftide\",\"fellowship\":{\"maxMembers\":29},"
            + "\"questBonus\":{\"disabled\":false,\"generation\":3,\"count\":41,\"status\":\"2 completed, 1 incomplete\","
            + "\"completed\":[\"qbflagone\",\"qbflagtwo\"],\"incomplete\":[\"qbflagthree\"]}}";

        // Quest notes: tracking and rewards saved per character, UB's reward format and import, kill-task timers.
        private static void QuestNotes()
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RynthOracleTests-" + Guid.NewGuid().ToString("N"));
            try
            {
                var r = new QuestReward { XpPercent = 1.5, LumPercent = 2, RecordedLevel = 275, Coins = 3, Trinkets = 1, Medallions = 2,
                                          YellowPowder = 1, RedPowder = 1, BluePowder = 1, Mmd = 2 };
                Check(r.ShortSummary() == "1.5%XP 2%Lum 3C 1T 2M 1Y 1R 1B 2D L275", "UB's short summary: " + r.ShortSummary());
                Check(QuestReward.Percent(12.345) == "12.3" && QuestReward.Percent(0.1234) == "0.123" && QuestReward.Percent(1.234) == "1.23", "UB's percent format");
                Check(QuestReward.TryParsePercent("2,5%", out double pc) && pc == 2.5, "a percent with a comma and a % sign");
                Check(QuestReward.TryParsePercent("5000", out double big) && big == QuestReward.MaxPercent, "percent clamped to 1000");
                Check(!QuestReward.TryParsePercent("abc", out _) && !QuestReward.TryParseCount("1.5", out _), "junk is refused");
                Check(QuestReward.TryParseCount("-4", out int neg) && neg == 0 && QuestReward.TryParseCount("", out int blank) && blank == 0, "counts: negatives and blanks are 0");
                Check(new QuestReward().IsEmpty && !r.IsEmpty, "empty entry");

                // Track and record, then read it back as a new session would.
                var book = new QuestBook { RootOverride = root, UbRootOverride = "" };
                book.Load("Infinite Leaftide", "Test \"Char\"");
                Check(book.Loaded && book.Tracked.Count == 0 && book.RewardCount == 0, "a new character starts empty");
                book.SetTracked("killtaskdrudges", true);
                book.SetTracked("30minattributes", true);
                book.SetTracked("killtaskdrudges", true);
                book.SetReward("30minattributes", r);
                Check(book.Tracked.Count == 2 && book.IsTracked("KILLTASKDRUDGES"), "tracked once, case-insensitive");
                string file = book.PathFor("Infinite Leaftide", "Test \"Char\"");
                Check(System.IO.File.Exists(file) && System.IO.File.ReadAllText(file).All(ch => ch == '\n' || (ch >= 0x20 && ch <= 0x7E)), "saved at once, plain ASCII");

                var again = new QuestBook { RootOverride = root, UbRootOverride = "" };
                again.Load("Infinite Leaftide", "Test \"Char\"");
                Check(again.Tracked.SequenceEqual(new[] { "killtaskdrudges", "30minattributes" }), "tracked list kept in order");
                Check(again.TryGetReward("30minattributes", out QuestReward back) && back.ShortSummary() == r.ShortSummary(), "rewards read back");
                again.SetReward("30minattributes", new QuestReward());
                again.ToggleTracked("killtaskdrudges");
                Check(again.RewardCount == 0 && again.Tracked.Count == 1, "an empty entry removes the rewards; untrack");

                // UB ILT's quest_rewards.json is imported once for a character without notes.
                string ub = System.IO.Path.Combine(root, "ub");
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(ub, "InfiniteLeaftide", "Kelp"));
                System.IO.File.WriteAllText(System.IO.Path.Combine(ub, "InfiniteLeaftide", "Kelp", "quest_rewards.json"),
                    "{\"SchemaVersion\":2,\"Character\":\"Kelp\",\"Server\":\"InfiniteLeaftide\",\"Rewards\":{"
                    + "\"stipendtimer_0812\":{\"XpPercent\":1.25,\"LumPercent\":0,\"RecordedLevel\":300,\"Coins\":5},"
                    + "\"emptyone\":{\"XpPercent\":0}}}");
                var imported = new QuestBook { RootOverride = root, UbRootOverride = ub };
                imported.Load("Infinite Leaftide", "Kelp");
                Check(imported.RewardCount == 1 && imported.TryGetReward("stipendtimer_0812", out QuestReward ir) && ir.Coins == 5 && ir.RecordedLevel == 300
                      && imported.LoadNote.Contains("Imported 1"), "UB rewards imported (empty entries skipped): " + imported.LoadNote);
                var noReimport = new QuestBook { RootOverride = root, UbRootOverride = ub };
                noReimport.Load("Infinite Leaftide", "Kelp");
                Check(noReimport.RewardCount == 1 && noReimport.LoadNote.Length == 0, "imported once: the next load reads Oracle's own file");

                // Kill tasks: kills left, then the reset timer, then done (UB's rule).
                DateTime now = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
                var kt = new QuestFlag { Key = "killtaskdrudges", Solves = 12, MaxSolves = 50, RepeatTime = TimeSpan.FromHours(20), CompletedOnUtc = now.AddHours(-1) };
                Check(QuestTimer.IsKillTask(kt) && QuestTimer.State(kt, true, now, out string t1) == QuestTimerState.InProgress && t1 == "38 left", "kill task counting");
                kt.Solves = 50;
                Check(QuestTimer.State(kt, true, now, out string t2) == QuestTimerState.Waiting && t2.StartsWith("19h"), "kill task waiting: " + t2);
                kt.CompletedOnUtc = now.AddDays(-2);
                Check(QuestTimer.State(kt, true, now, out string t3) == QuestTimerState.Done && t3 == "done", "kill task done");
                var timed = new QuestFlag { Key = "stipendtimer_0812", Solves = 3, RepeatTime = TimeSpan.FromHours(1), CompletedOnUtc = now.AddMinutes(-30) };
                QuestTimerState s4 = QuestTimer.State(timed, true, now, out string t4);
                Check(!QuestTimer.IsKillTask(timed) && s4 == QuestTimerState.Waiting && t4 == "30m 0s", "timer: " + t4);
                timed.CompletedOnUtc = now.AddHours(-2);
                Check(QuestTimer.State(timed, true, now, out string t5) == QuestTimerState.Ready && t5 == "ready", "timer ran out");
                Check(QuestTimer.State(timed, false, now, out string t6) == QuestTimerState.Done && t6 == "completed", "one-time quest");
            }
            finally
            {
                try { System.IO.Directory.Delete(root, true); } catch (System.IO.IOException) { }
            }
        }

        private static void ConquestReplies()
        {
            var d = new ConquestData();
            long now = 1_000_000;
            d.RequestBank(now);
            Check(d.OnChatLine("[BANK] Your balances:", now, out bool ours) && ours, "bank heading (ours)");
            d.OnChatLine("[BANK] Pyreals: 1,039,678,533", now, out _);
            d.OnChatLine("[BANK] Event Tokens [Dragon Coins] (x): 12", now, out _);
            Check(d.Bank.Any(b => b.Name == "Pyreals" && b.Value.Contains("4,158 MMDs")), "pyreals with MMDs");
            Check(d.Bank.Any(b => b.Name == "Dragon Coins (x)"), "event token name");
            Check(!d.OnChatLine("Bob says, \"[BANK] Pyreals: 999\"", now, out _), "a pasted balance is ignored");
            d.OnChatLine("[BANK] Pyreals: 5", now + 60_000, out bool late);
            Check(!late, "a reply long after the request isn't ours (shows in chat)");

            d.RequestAugs(now);
            d.OnChatLine("Creature: 25", now, out _);
            d.OnChatLine("Duration: 3", now, out _);
            ConquestAug creature = d.Augs.First(a => a.Name == "Creature");
            Check(creature.Count == 25 && creature.NextLuminanceCost() == 6_229_500, $"Creature 25 next costs 6,229,500 ({creature.NextLuminanceCost()})");
            Check(d.DurationAugs == 3 && Math.Abs(d.VoidDurationMultiplier(5) - 2.15) < 1e-9, "void duration x(1 + 5*0.2 + 3*0.05)");

            d.RequestEnl(now);
            d.OnChatLine("Enlightenment Augmentations:", now, out _);
            d.OnChatLine("Skill Credits: 2 (10 max)", now, out _);
            d.OnChatLine("Damage: 5 (25 max)", now, out _);
            Check(d.EnlAugs.Count == 2 && d.EnlAugs[0].Name == "Damage", "enlightenment augs in display order");

            d.RequestBonus(now);
            d.OnChatLine("=== XP Bonuses ===", now, out _);
            d.OnChatLine("Quest Bonus: 14.18% (1,418 quests)", now, out _);
            d.OnChatLine("Total Bonus (Kills): 0.16%", now, out _);
            Check(d.Bonuses.First(b => b.Name == "Quest").Value == "14.18% (1,418 quests)" && d.Bonuses.First(b => b.Name == "Total (Kills)").Value == "0.16%", "bonuses");

            d.RequestFships(now);
            d.OnChatLine("Fellowships looking for members:", now, out _);
            d.OnChatLine("- Heroes (Leader: Bob) [3/14] @ Bur", now, out _);
            d.OnChatLine("- Quiet (Leader: Ann) [1/14]", now, out _);
            Check(d.Fellowships.Count == 2 && d.Fellowships[0].Location == "Bur" && d.Fellowships[1].MemberCount == 1, "fellowship list");

            ConquestBoard lvl = d.Boards.First(b => b.Key == "level");
            d.RequestBoard(lvl, now);
            d.OnChatLine("Top 2 Players by Level:", now, out _);
            d.OnChatLine("1: 300 - Alpha", now, out _);
            d.OnChatLine("2: 299 - Beta", now, out _);
            d.OnChatLine("57: 250 - Me (You)", now, out _);
            Check(lvl.Rows.Count == 2 && lvl.You != null && lvl.You.Rank == 57 && lvl.You.Name == "Me", "ConquestAC /top board");

            Check(ConquestData.WithdrawCommand(ConquestData.Withdrawable[ConquestData.DefaultWithdraw], "10") == "/bank withdraw n 10", "withdraw MMDs");
            Check(ConquestData.WithdrawCommand(ConquestData.Withdrawable[0], "-3") == null, "withdraw refuses a bad amount");
            Check(ConquestData.TransferCommand(ConquestData.Transferable[2], "4", "Some One") == "/bank transfer p 1000000 \"Some One\"", "transfer MMD notes as pyreals");
            Check(ConquestData.TransferCommand(ConquestData.Transferable[2], "1001", "X") == null, "transfer cap");
        }

        private static void AelrynthReplies()
        {
            var a = new AelrynthBoards();
            long now = 2_000_000;
            a.RequestTop("", now);
            Check(a.OnChatLine("Leaderboards - 37 characters, refreshed 4 min ago. /top <board> for the top ten, /top me for yourself.", now, out bool ours) && ours, "overview header");
            a.OnChatLine("  kills             Creature kills: Buffy, 1,234,567", now, out _);
            a.OnChatLine("  pk                PK kills: nobody yet", now, out _);
            Check(a.Overview.Count == 2 && a.Overview[0].Key == "kills" && a.Overview[0].Leader == "Buffy" && a.Overview[0].Value == "1,234,567"
                  && a.Overview[1].Leader == "nobody yet", "overview rows");

            a.RequestTop("kills", now);
            a.OnChatLine("Creature kills - top 10 of 37:", now, out _);
            a.OnChatLine("    1. Buffy                             1,234,567  <- you", now, out _);
            a.OnChatLine("    2. Dargoth                             987,654", now, out _);
            a.OnChatLine("    3. Sal                                     500  12 kills per death", now, out _);
            Check(a.BoardTitle == "Creature kills" && a.BoardRows.Count == 3 && a.BoardRows[0].IsYou && a.BoardRows[1].Value == "987,654"
                  && a.BoardRows[2].Extra == "12 kills per death", "board rows: " + string.Join(" | ", a.BoardRows.Select(r => $"{r.Rank} {r.Name} {r.Value} [{r.Extra}] {r.IsYou}")));

            a.RequestTop("me", now);
            a.OnChatLine("Buffy on the boards:", now, out _);
            a.OnChatLine("  Creature kills               #1 of 37, 1,234,567", now, out _);
            a.OnChatLine("  PK kills                     -", now, out _);
            Check(a.Standings.Count == 2 && a.Standings[0].Rank == 1 && a.Standings[1].Rank == 0, "/top me");

            Check(!a.OnChatLine("    9. Nobody   12", now + 30_000, out _), "an entry long after its header is ignored");

            a.RequestTrial("", now);
            a.OnChatLine("Trials - a fresh personal copy of a listed dungeon, entered through its portal, is a timed course: ...", now, out _);
            a.OnChatLine("  The Citadel Below - Trial (02F0): tier 0 12:34 by Buffy | you: tier 0 12:34 | this week: Buffy 12:34 (tier 0)", now, out _);
            a.OnChatLine("      your next attempt here: in 12 min", now, out _);
            a.OnChatLine("Trophy: the week's fastest clear of each dungeon, any tier - a trophy and 1,000 experience, Sunday 20:00 (2026-W40). /trial <name> for the top 10 of one dungeon.", now, out _);
            Check(a.Trials.Count == 1 && a.Trials[0].Landblock == "02F0" && a.TrialLines.Count == 3, $"/trial overview ({a.Trials.Count} dungeons, {a.TrialLines.Count} lines)");

            a.RequestTrial("citadel", now);
            a.OnChatLine("The Citadel Below - Trial (02F0) - top 10, best per character, any tier:", now, out _);
            a.OnChatLine("   1. 12:34  Buffy (tier 0, 105 kills, 3 Oct)", now, out _);
            a.OnChatLine("  you: tier 0 12:34 (105 kills)", now, out _);
            Check(a.TrialRows.Count == 1 && a.TrialRows[0].Name == "Buffy" && a.TrialRows[0].Value == "12:34" && a.TrialNotes.Count == 1, "/trial dungeon top 10");
            Check(!a.OnChatLine("Bob tells you, \"hello\"", now, out _), "ordinary chat is never taken");
        }

        private static void Void()
        {
            var v = new VoidTracker();
            long now = 1_000_000;
            bool hit = v.OnChatLine("You cast Corrosion VII on Drudge Slinker", now, _ => 0x50000001u, () => true);
            Check(hit && v.Dots.Count == 1 && v.Dots[0].Family == VoidFamily.Corrosion && v.Dots[0].Destruction, "cast with the surge up");
            Check(v.Remaining(0x50000001u, "Drudge Slinker", VoidFamily.Corrosion, now + 1000, out bool d) == 16 && d, "15 s + 2 s before the first tick");
            v.OnChatLine("You scar Drudge Slinker for 120 points of periodic nether damage!", now + 2000, _ => 0, () => false);
            Check(v.Remaining(0x50000001u, "Drudge Slinker", VoidFamily.Corrosion, now + 3000, out _) == 14, "timed from the first tick");
            v.OnChatLine("You cast Corruption III on Mosswart", now, _ => 0, () => false);
            Check(v.Remaining(0x123u, "Mosswart", VoidFamily.Corruption, now + 1000, out _) == 31, "unknown id matches by name");
            Check(!v.OnChatLine("You cast Strength Self VI on yourself", now, _ => 0, () => false), "other spells ignored");
            v.RemoveExpired(now + 60_000);
            Check(v.Dots.Count == 0, "expired spells drop");
        }

        private static OracleContext Context()
        {
            var c = new OracleContext { InWorld = true, NowMs = 10_000_000, UtcNow = DateTime.UtcNow };
            c.Server.Force(ServerTags.Aelrynth, "Aeshnidae");
            c.Catalog.Load("Aelrynth", null);
            c.Flags.OnChatLine("30minattributes - 4 solves (1719432000)\"x\" -1 1800", 0);
            c.Flags.OnChatLine("societyribbonsperdaytimer - 1 solves (" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ")\"x\" -1 72000", 0);
            c.Flags.OnChatLine("societyribbonsperdaycounter - 30 solves (1719432000)\"x\" 200 0", 0);
            c.Flags.Tick(5000);
            c.Catalog.SyncDiscoveries(c.Flags);
            CharacterSnapshot ch = c.Character;
            ch.Valid = true;
            ch.PlayerId = 0x50000001;
            ch.Name = "Test Character";
            ch.Level = 126;
            ch.TotalXp = 4_200_000_000;
            ch.Enchantments.Add(new ActiveEnchantment(4305, "Incantation of Focus Self", 3000, 5400, false));
            ch.Enchantments.Add(new ActiveEnchantment(3531, "Bobo's Quickening", 600, 1800, false));
            ch.Enchantments.Add(new ActiveEnchantment(SpellIds.SurgeOfDestruction, "Surge of Destruction", 5, 8, false));
            c.Void.OnChatLine("You cast Corrosion VII on Drudge Slinker", c.NowMs, _ => 0, () => true);
            // The quest book in a temp folder (never the player's %APPDATA%), with two tracked quests.
            c.Book.RootOverride = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RynthOracleTests-" + Guid.NewGuid().ToString("N"));
            c.Book.UbRootOverride = "";
            c.Book.Load("Aeshnidae", ch.Name);
            c.Book.SetTracked("30minattributes", true);
            c.Book.SetTracked("societyribbonsperdaycounter", true);
            c.Book.SetReward("30minattributes", new QuestReward { XpPercent = 1.5, RecordedLevel = 126, Coins = 2 });
            return c;
        }

        private static void Pages()
        {
            OracleContext c = Context();
            var view = new OracleView(c);
            var host = new UiHost();
            UiWindow main = host.Add(new UiWindow("RynthOracle/Oracle", "Oracle") { Visible = true, DefaultWidth = 620, DefaultHeight = 560 });
            UiWindow voidW = host.Add(new UiWindow("RynthOracle/Void", "Void") { Visible = true });
            var vv = new VoidView(c);
            voidW.Begin(); vv.Draw(voidW); voidW.End();
            UiWindow timersW = host.Add(new UiWindow("RynthOracle/QuestTimers", "Quest Timers") { Visible = true });
            var tv = new QuestTimersView(c);
            timersW.Begin(); tv.Draw(timersW); timersW.End();
            string timersBody = Encoding.UTF8.GetString(timersW.Body);
            Check(timersBody.Contains("Attribute and Skill Redistribution") && timersBody.Contains("ready"), "the Quest Timers window lists tracked quests with their timers");

            // Every tab on each server, including the server-specific ones (with data in them).
            c.Conquest.RequestBank(c.NowMs);
            c.Conquest.OnChatLine("[BANK] Pyreals: 1,000", c.NowMs, out _);
            c.Boards.OnChatLine("Leaderboards - 3 characters, refreshed just now. /top <board> ...", c.NowMs, out _);
            c.Boards.OnChatLine("  kills             Creature kills: Buffy, 12", c.NowMs, out _);
            c.EarnedTitles.Add(1);
            c.TitlesKnown = true;
            foreach (string server in new[] { ServerTags.Aelrynth, ServerTags.Conquest, "" })
            {
                c.Server.Force(server, server);
                foreach (string tab in view.VisibleTabs())
                {
                    c.Settings.TabName = tab;
                    main.Begin();
                    view.Draw(main);
                    main.End();
                    ParseAll(host, $"{server}/{tab}", out int ops);
                    Check(ops > 5 && ops < 1400, $"{server}/{tab}: {ops} ops");
                    if (server == ServerTags.Aelrynth || tab is OracleView.TabConquest) Console.WriteLine($"  {(server.Length > 0 ? server : "other")}/{tab}: {ops} ops, {main.Body.Length} bytes");
                    main.MarkSubmitted(true);
                }
            }
            // Infinite Leaftide: the Quests tab with a quest opened (rewards editor) and the /qb list open.
            c.Server.Force(ServerTags.InfiniteLeaftide, "InfiniteLeaftide");
            c.Ilt.SetJson(QbProfileJson);
            c.Settings.TabName = OracleView.TabQuests;
            main.Begin(); view.Draw(main); main.End();
            Apply(host, Event(2, 89, main.Hash, main.KeyOf("q.tracked"), new byte[] { 1 }));
            main.Begin(); view.Draw(main); main.End();
            Check(Encoding.UTF8.GetString(main.Body).Contains("2 shown"), "the Tracked filter lists the two tracked quests");
            Apply(host, Event(1, 90, main.Hash, main.KeyOf("q.r.30minattributes"), new byte[] { 1, 0, 0 }));
            Apply(host, Event(6, 91, main.Hash, main.KeyOf("q.qb"), new byte[] { 1 }));
            main.Begin(); view.Draw(main); main.End();   // the inner header exists once the outer one is open
            Apply(host, Event(6, 92, main.Hash, main.KeyOf("q.qb.todo"), new byte[] { 1 }));
            main.Begin(); view.Draw(main); main.End();
            ParseAll(host, "ILT quests with a quest opened", out int iltOps);
            string iltBody = Encoding.UTF8.GetString(main.Body);
            Check(iltOps > 5 && iltOps < 1400, $"ILT quests page: {iltOps} ops");
            Check(iltBody.Contains("Save rewards") && iltBody.Contains("Level: 126"), "the opened quest shows the rewards editor");
            Check(iltBody.Contains("1.5%XP 2C L126"), "the Rewards column shows the recorded rewards");
            Check(iltBody.Contains("Quest bonus (/qb) - count 41") && iltBody.Contains("qbflagthree"), "the /qb list from RynthILT");
            Console.WriteLine($"  InfiniteLeaftide/Quests (opened, /qb): {iltOps} ops, {main.Body.Length} bytes");
            main.MarkSubmitted(true);

            // The Conquest tab's sub-pages and the Aelrynth board views.
            c.Server.Force(ServerTags.Conquest, "Conquest");
            c.Settings.TabName = OracleView.TabConquest;
            for (int v = 0; v < 6; v++)
            {
                main.Begin(); view.Draw(main); main.End();
                Apply(host, Event(1, (uint)(100 + v), main.Hash, main.KeyOf("cq.v." + ((v + 1) % 6)), new byte[] { 1, 0, 0 }));
                main.Begin(); view.Draw(main); main.End();
                ParseAll(host, $"conquest view {v}", out int ops);
                Check(ops > 5 && ops < 1400, $"conquest view {v}: {ops} ops");
            }

            // A page that can't fit: everything in the catalog on one page is cut short, still valid.
            var big = new UiWindow("RynthOracle/Big", "Big") { Visible = true };
            var bigHost = new UiHost();
            bigHost.Add(big);
            big.Begin();
            for (int i = 0; i < 3000; i++) { big.Text("row " + i); big.SameLine(100); big.Text("x"); }
            big.End();
            ParseAll(bigHost, "oversized page", out int bigOps);
            Check(bigOps <= 1500, $"oversized page cut to {bigOps} ops");

            // Logged out: the main window says so and still parses.
            c.InWorld = false;
            main.Begin(); view.Draw(main); main.End();
            ParseAll(host, "logged out", out _);
        }

        private static unsafe void ParseAll(UiHost host, string what, out int maxOps)
        {
            int n = Write(host);
            byte[] buf = SubmitBuffer(host);
            int rc;
            List<ParsedWindow> windows;
            string error;
            fixed (byte* p = buf) rc = DisplayListParser.Parse(p, n, 4000f, out _, out windows, out error);
            Check(rc == 0, $"{what}: engine parser refused the submit ({rc}): {error}");
            maxOps = windows.Where(w => w.List != null).Select(w => w.List!.OpCount).DefaultIfEmpty(0).Max();
        }

        /// <summary>
        /// The Character tab's RynthAi session and RynthILT charms (the Mini Remote's Character and
        /// Charms tabs moved here 2026-10-13): drawn from what the plugin read, Toggle routed to RynthILT.
        /// </summary>
        private static void CharacterExtras()
        {
            const string charmsJson = "{\"available\":true,\"world\":\"InfiniteLeaftide\",\"acquiredCount\":2,\"activeCount\":1,"
                + "\"charms\":[{\"name\":\"Mana Barrier\",\"effect\":\"Absorbs damage\",\"acquired\":\"carried\",\"count\":1,\"tier\":3,\"maxTier\":5,"
                + "\"active\":\"on\",\"server\":\"on\",\"toggleId\":5001},"
                + "{\"name\":\"Swift Feet\",\"acquired\":\"seen\",\"lastSeen\":\"2026-10-12\",\"active\":\"unknown\",\"server\":\"off\"}],"
                + "\"hands\":[{\"id\":7001,\"name\":\"Guardian Hand\",\"active\":\"off\",\"server\":\"on\"}]}";

            OracleContext c = Context();
            c.Server.Force(ServerTags.InfiniteLeaftide, "InfiniteLeaftide");
            c.Settings.TabName = OracleView.TabCharacter;
            var view = new OracleView(c);
            var host = new UiHost();
            UiWindow main = host.Add(new UiWindow("RynthOracle/Oracle", "Oracle") { Visible = true });

            // Nothing from RynthILT or RynthAi yet: the tab says what to load.
            main.Begin(); view.Draw(main); main.End();
            string empty = Encoding.UTF8.GetString(main.Body);
            Check(empty.Contains("Load RynthILT to list your Infinite Leaftide charms"), "ILT without RynthILT: says to load it");
            Check(empty.Contains("Load RynthAi for the session rates"), "without RynthAi: says to load it");
            main.MarkSubmitted(true);

            uint toggled = 0;
            c.ToggleCharm = id => { toggled = id; return true; };
            c.Charms = RynthCore.Plugin.Shared.IltCharmsView.Parse(charmsJson);
            c.Session = new RynthCore.Plugin.Shared.SessionStatsV1
            {
                StartUtcTicks = DateTime.UtcNow.AddHours(-1).Ticks, SessionSeconds = 3600, Xp = 1_234_567, XpPerHour = 1_234_567,
                Lum = 4000, LumPerHour = 4000, LumLast5Min = 250, Kills = 42, KillsPerHour = 42,
            };
            c.SessionKnown = true;
            main.Begin(); view.Draw(main); main.End();
            ParseAll(host, "Character with charms and session", out int ops);
            string body = Encoding.UTF8.GetString(main.Body);
            Check(ops > 5 && ops < 1400, $"Character with charms: {ops} ops");
            Check(body.Contains("1,234,567") && body.Contains("4,000") && body.Contains("42 (42/h)"), "the session shows RynthAi's XP, luminance and kills");
            Check(body.Contains("Charms (2)") && body.Contains("Mana Barrier") && body.Contains("T3/5") && body.Contains("seen 2026-10-12"),
                  "the charms RynthILT listed, with tier and acquired state");
            Check(body.Contains("server off"), "a charm the server switched off is marked");
            Check(body.Contains("Guardian Hands (1)"), "the Guardian Hands are listed");

            Apply(host, Event(1, 10, main.Hash, main.KeyOf("c.charm.0"), new byte[] { 1, 0, 0 }));
            Check(toggled == 5001, "the carried charm's Toggle goes to RynthILT with its id");
            Check(body.Contains("###c.charm.0") && !body.Contains("###c.charm.1"), "an uncarried charm has no Toggle");
            Apply(host, Event(1, 11, main.Hash, main.KeyOf("c.hand.0"), new byte[] { 1, 0, 0 }));
            Check(toggled == 7001, "the Guardian Hand's Toggle goes to RynthILT with its id");
            main.MarkSubmitted(true);

            // Another world: RynthILT answers "not available", so no charms section at all.
            c.Server.Force("", "");
            c.Charms = RynthCore.Plugin.Shared.IltCharmsView.Parse("{\"available\":false,\"charms\":[]}");
            main.Begin(); view.Draw(main); main.End();
            Check(!Encoding.UTF8.GetString(main.Body).Contains("Charms ("), "off ILT: no charms section");
            Check(OracleView.SessionStatsCommand == "/rc miniremote section char", "Session Stats opens the section, not the moved tab");
        }

        /// <summary>RynthILT's breeding JSON with <paramref name="essences"/> essences (one ready stud and one ready dam first).</summary>
        private static string BreedingJson(int essences)
        {
            var sb = new StringBuilder("{\"available\":true,\"world\":\"InfiniteLeaftide\",\"enabled\":\"on\",\"maturity\":\"on\",\"bond\":\"on\","
                + "\"room\":\"landblock 0x0106 variant 2\",\"freeSlots\":12,\"message\":\"\",\"essences\":[");
            for (int i = 0; i < essences; i++)
            {
                if (i > 0) sb.Append(',');
                bool male = i % 2 == 0, ready = i < 2;
                sb.Append("{\"id\":").Append(1000 + i).Append(",\"name\":\"Essence ").Append(i).Append("\",\"sex\":\"").Append(male ? 'M' : 'F')
                  .Append("\",\"tier\":125,\"bond\":100,\"ready\":").Append(ready ? "true" : "false")
                  .Append(",\"status\":\"").Append(ready ? "Ready" : "Rest 1h 05m").Append("\",\"readyIn\":").Append(ready ? 0 : 3900)
                  .Append(",\"charges\":7,\"chargesMax\":10,\"juvenile\":false,\"stage\":\"\",\"kills\":0,\"killsRequired\":300,")
                  .Append("\"mutations\":1,\"potencyMutations\":0,\"problems\":").Append(ready ? "[]" : "[\"resting after a breed\"]")
                  .Append(",\"boosters\":").Append(i == 0 ? "[\"Incense +10%\"]" : "[]").Append('}');
            }
            sb.Append("],\"pairs\":[{\"stud\":\"Essence 0\",\"dam\":\"Essence 1\",\"chanceBest\":0.13636,\"chanceWorst\":0.05,\"inheritMin\":1,\"inheritMax\":2}],")
              .Append("\"supplies\":[{\"key\":\"incense1\",\"name\":\"Breeding Incense\",\"effect\":\"+10% mutation chance\",\"count\":2,\"price\":250000,\"apply\":true,\"targets\":[1000,1001]},")
              .Append("{\"key\":\"neuter\",\"name\":\"Neutering Kit\",\"effect\":\"Permanent\",\"count\":1,\"price\":0,\"apply\":false,\"targets\":[]}],")
              .Append("\"rules\":[{\"label\":\"Stud charges\",\"value\":\"10/day\",\"source\":\"seen on server\",\"help\":\"Breeds per refill.\"},")
              .Append("{\"label\":\"Mutation decay\",\"value\":\"0.1\",\"source\":\"ILT profile\",\"help\":\"\"}],")
              .Append("\"events\":[{\"when\":\"10-15 12:00\",\"kind\":\"birth\",\"text\":\"Congratulations! A baby pet has been born: Pup\"}],")
              .Append("\"debug\":{\"when\":\"10-15 12:01\",\"enabled\":\"on\",\"location\":\"0x0106 variant 2\",\"match\":\"valid\",\"pet\":\"Essence 0\",\"sex\":\"M\",\"partners\":1}}");
            return sb.ToString();
        }

        /// <summary>
        /// The Pet Breeding tab (Infinite Leaftide only): RynthILT's breeding JSON drawn within the op budget,
        /// Dance and supply uses asked first, then sent to RynthILT as its verbs.
        /// </summary>
        private static void Breeding()
        {
            // The tab exists only on Infinite Leaftide.
            OracleContext c = Context();
            var view = new OracleView(c);
            Check(!view.VisibleTabs().Contains(OracleView.TabBreeding), "Aelrynth: no Pet Breeding tab");
            c.Server.Force("", "Coldeve");
            Check(!view.VisibleTabs().Contains(OracleView.TabBreeding), "other servers: no Pet Breeding tab");
            c.Server.Force(ServerTags.InfiniteLeaftide, "InfiniteLeaftide");
            Check(view.VisibleTabs().Contains(OracleView.TabBreeding), "Infinite Leaftide: the Pet Breeding tab");

            // The parser: fields, lists, the debug block, and bad input.
            IltBreedingView v = IltBreedingView.Parse(BreedingJson(2));
            Check(v.Loaded && v.Available && v.Enabled == "on" && v.Room == "landblock 0x0106 variant 2" && v.FreeSlots == 12, "breeding JSON header");
            Check(v.Essences.Count == 2 && v.Essences[0].Male && !v.Essences[1].Male && v.Essences[0].Ready && v.Essences[0].Boosters.Count == 1,
                  "breeding essences with sex, readiness and boosters");
            Check(v.Pairs.Count == 1 && Math.Abs(v.Pairs[0].ChanceBest - 0.13636) < 1e-9 && v.Pairs[0].InheritMax == 2, "breeding pair odds");
            Check(v.Supplies.Count == 2 && v.Supplies[0].Targets.Count == 2 && !v.Supplies[1].Apply, "breeding supplies and their targets");
            Check(v.Debug is { Match: "valid", Partners: 1 }, "breeding diagnostics");
            Check(!IltBreedingView.Parse("not json").Loaded && !IltBreedingView.Parse("").Loaded && !IltBreedingView.Parse("[1]").Loaded,
                  "bad breeding JSON is not loaded");
            Check(IltBreedingView.ApplyVerb(" incense1 ", 1000) == "apply incense1 1000" && IltBreedingView.DanceVerb() == "dance",
                  "breeding verbs");

            // Nothing from RynthILT yet: the tab says to load it.
            var host = new UiHost();
            UiWindow main = host.Add(new UiWindow("RynthOracle/Oracle", "Oracle") { Visible = true });
            c.Settings.TabName = OracleView.TabBreeding;
            main.Begin(); view.Draw(main); main.End();
            Check(Encoding.UTF8.GetString(main.Body).Contains("Load RynthILT (0.2.7 or later)"), "without RynthILT: says to load it");
            main.MarkSubmitted(true);

            // 50 essences with every section open: rows cut at 40, the page still fits.
            var sent = new List<string>();
            string ran = "";
            c.BreedingCommand = verb => { sent.Add(verb); return true; };
            c.RunCommand = cmd => { ran = cmd; return true; };
            c.Breeding = IltBreedingView.Parse(BreedingJson(50));
            main.Begin(); view.Draw(main); main.End();
            uint seq = 200;
            foreach (string header in new[] { "br.sup", "br.rules", "br.events" })
                Apply(host, Event(6, seq++, main.Hash, main.KeyOf(header), new byte[] { 1 }));
            main.Begin(); view.Draw(main); main.End();
            ParseAll(host, "Pet Breeding with 50 essences", out int ops);
            string body = Encoding.UTF8.GetString(main.Body);
            Check(ops > 5 && ops < 1400, $"Pet Breeding with 50 essences: {ops} ops");
            Console.WriteLine($"  InfiniteLeaftide/Pet Breeding (50 essences, all open): {ops} ops, {main.Body.Length} bytes");
            Check(body.Contains("Essences (2 ready of 50)") && body.Contains("... and 10 more"), "essence rows cut at 40 with a count");
            Check(body.Contains("Essence 0 x Essence 1") && body.Contains("5%-13.6%"), "the ready pair with its odds");
            Check(body.Contains("Supplies (3 carried)") && body.Contains("250,000 pyreals"), "supplies with Ivo's price");
            Check(body.Contains("Stud charges") && body.Contains("seen on server") && body.Contains("In the breeding room"), "rules and diagnostics");
            Check(body.Contains("A baby pet has been born"), "recent events");

            // Dance asks first; No drops it, Yes sends it.
            Apply(host, Event(1, seq++, main.Hash, main.KeyOf("br.dance"), new byte[] { 1, 0, 0 }));
            Check(sent.Count == 0, "Dance asks before sending");
            main.Begin(); view.Draw(main); main.End();
            Apply(host, Event(1, seq++, main.Hash, main.KeyOf("br.no"), new byte[] { 1, 0, 0 }));
            main.Begin(); view.Draw(main); main.End();
            Check(sent.Count == 0 && !Encoding.UTF8.GetString(main.Body).Contains("###br.yes"), "No drops the question");
            Apply(host, Event(1, seq++, main.Hash, main.KeyOf("br.dance"), new byte[] { 1, 0, 0 }));
            main.Begin(); view.Draw(main); main.End();
            Apply(host, Event(1, seq++, main.Hash, main.KeyOf("br.yes"), new byte[] { 1, 0, 0 }));
            Check(sent.Count == 1 && sent[0] == "dance", "Yes sends the dance verb");

            // A supply use on the selected essence (the first) goes the same way.
            main.Begin(); view.Draw(main); main.End();
            Check(Encoding.UTF8.GetString(main.Body).Contains("###br.use.0") && !Encoding.UTF8.GetString(main.Body).Contains("###br.use.1"),
                  "only a usable, carried supply has Use");
            Apply(host, Event(1, seq++, main.Hash, main.KeyOf("br.use.0"), new byte[] { 1, 0, 0 }));
            main.Begin(); view.Draw(main); main.End();
            Apply(host, Event(1, seq++, main.Hash, main.KeyOf("br.yes"), new byte[] { 1, 0, 0 }));
            Check(sent.Count == 2 && sent[1] == "apply incense1 1000", "Use then Yes sends apply with the essence id: " + string.Join("|", sent));

            Apply(host, Event(1, seq++, main.Hash, main.KeyOf("br.refresh"), new byte[] { 1, 0, 0 }));
            Check(sent.Count == 3 && sent[2] == "refresh", "Refresh is sent straight away");
            Apply(host, Event(1, seq++, main.Hash, main.KeyOf("br.remote"), new byte[] { 1, 0, 0 }));
            Check(ran == "/ra hub open breeding", "Mini Remote opens Pets > Breeding: " + ran);
            main.MarkSubmitted(true);

            // Breeding switched off on the server: said in red, page still drawn.
            c.Breeding = IltBreedingView.Parse("{\"available\":true,\"enabled\":\"off\",\"essences\":[]}");
            main.Begin(); view.Draw(main); main.End();
            Check(Encoding.UTF8.GetString(main.Body).Contains("Pet breeding is off on this server."), "breeding off is shown");
        }

        /// <summary>RynthILT's Temple guardian JSON ("RynthILT.Data" version 3) as the Quests tab reads it.</summary>
        private const string GuardianJson = "{\"available\":true,\"world\":\"InfiniteLeaftide\",\"chat\":true,\"auto\":false,\"buy\":true,"
            + "\"cooldownHours\":20,\"answer\":\"Pyreal\",\"npc\":\"Guardian of Attribute Enlightenment\",\"answerAgeSec\":30,"
            + "\"heard\":\"What is the coin of the realm?\",\"running\":false,\"status\":\"idle\",\"synced\":\"2026-10-18T19:05:00Z\","
            + "\"rows\":[{\"attribute\":\"Strength\",\"npc\":\"Fiun Strongarm\",\"count\":3,\"status\":\"Ready\",\"eta\":\"\",\"ready\":true,\"exact\":true},"
            + "{\"attribute\":\"Focus\",\"npc\":\"Fiun Thinker\",\"count\":1,\"status\":\"Cooldown\",\"eta\":\"in 5h 2m\",\"ready\":false,\"exact\":false}]}";

        /// <summary>
        /// The Quests tab's merged trackers (2026-10-18): Legendary quests, markers, and on Infinite
        /// Leaftide Dynamic Stamps and the Temple guardians, as rows of the one list; Trackers only;
        /// opening a tracker (RynthNav Go to); the guardian controls; the ops budget.
        /// </summary>
        private static void Trackers()
        {
            // The guardian JSON and its verbs.
            var g = RynthCore.Plugin.Shared.IltGuardianView.Parse(GuardianJson);
            Check(g.Loaded && g.Chat && !g.Auto && g.Buy && g.Answer == "Pyreal" && g.AnswerAgeSec == 30 && g.SyncedUtc.Year == 2026,
                  "guardian JSON header");
            Check(g.Rows.Count == 2 && g.Rows[0].Ready && g.Rows[0].Exact && g.Rows[1].Eta == "in 5h 2m" && !g.Rows[1].Exact, "guardian rows");
            Check(!RynthCore.Plugin.Shared.IltGuardianView.Parse("nope").Loaded && !RynthCore.Plugin.Shared.IltGuardianView.Parse("").Loaded,
                  "bad guardian JSON is not loaded");
            Check(RynthCore.Plugin.Shared.IltGuardianView.SwitchVerb(" Auto ", true) == "auto on"
                  && RynthCore.Plugin.Shared.IltGuardianView.SwitchVerb("buy", false) == "buy off"
                  && RynthCore.Plugin.Shared.IltGuardianView.HandInVerb() == "handin", "guardian verbs");

            // Any server: Trackers only lists the Legendary quests and markers with their status, no ILT kinds.
            OracleContext c = Context();
            c.Server.Force("", "Coldeve");
            var view = new OracleView(c);
            var host = new UiHost();
            UiWindow main = host.Add(new UiWindow("RynthOracle/Oracle", "Oracle") { Visible = true });
            view.ShowTrackers(null);
            main.Begin(); view.Draw(main); main.End();
            string body = Encoding.UTF8.GetString(main.Body);
            int trackerRows = LegendaryList.All.Count + MarkerList.All.Count;
            Check(body.Contains($"{trackerRows} shown") && body.Contains("LEG") && body.Contains("toward John") && body.Contains("markers found"),
                  "Trackers only: every Legendary quest and marker, with their status above");
            Check(!body.Contains("###q.r.dy:") && !body.Contains("###q.r.gd:") && !body.Contains("###q.r.30minattributes")
                  && !body.Contains("###gd.auto"),
                  "no stamps, guardians or quests off Infinite Leaftide in Trackers only");
            main.MarkSubmitted(true);

            // Everything: quests and trackers in one list (the quests first in the default order).
            view.ShowTrackers(QuestKind.Quest);
            c.Settings.TabName = OracleView.TabQuests;
            main.Begin(); view.Draw(main); main.End();
            body = Encoding.UTF8.GetString(main.Body);
            Check(body.Contains("Attribute and Skill Redistribution") && !body.Contains("###q.r.lg:") && !body.Contains("###q.r.mk:"),
                  "Show: Quests lists only quests");
            main.MarkSubmitted(true);

            // A Legendary quest with coordinates in its notes opens with RynthNav's Go to.
            LegendaryQuest? withCoords = LegendaryList.All.FirstOrDefault(q => NavTargets.Stops(q.Note).Count > 0);
            view.ShowTrackers(QuestKind.Legendary);
            main.Begin(); view.Draw(main); main.End();
            LegendaryQuest pick = withCoords ?? LegendaryList.All[0];
            Apply(host, Event(1, 700, main.Hash, main.KeyOf("q.r.lg:" + pick.Name), new byte[] { 1, 0, 0 }));
            main.Begin(); view.Draw(main); main.End();
            body = Encoding.UTF8.GetString(main.Body);
            Check(body.Contains("###lg.sel.print") && body.Contains("###lg.sel.close"), "a clicked Legendary quest opens below the list");
            if (withCoords != null) Check(body.Contains("###q.nav.go") && body.Contains("###q.nav.route"), "its coordinates offer RynthNav Go / Route / Arrow");
            main.MarkSubmitted(true);

            // A marker always has somewhere to go: its directions' coordinates or its location's name.
            view.ShowTrackers(QuestKind.Marker);
            main.Begin(); view.Draw(main); main.End();
            Marker m0 = MarkerList.All[0];
            Apply(host, Event(1, 701, main.Hash, main.KeyOf("q.r.mk:" + m0.Number), new byte[] { 1, 0, 0 }));
            main.Begin(); view.Draw(main); main.End();
            body = Encoding.UTF8.GetString(main.Body);
            Check(body.Contains("Marker " + m0.Number) && body.Contains("###q.nav.go") && body.Contains("###m.sel.close"),
                  "a clicked marker opens with Go to");
            main.MarkSubmitted(true);

            // Infinite Leaftide without RynthILT's guardian data: the guardians say so.
            c.Server.Force(ServerTags.InfiniteLeaftide, "InfiniteLeaftide");
            view.ShowTrackers(QuestKind.Guardian);
            main.Begin(); view.Draw(main); main.End();
            Check(Encoding.UTF8.GetString(main.Body).Contains("RynthILT isn't loaded"), "guardians without RynthILT: says to load it");
            main.MarkSubmitted(true);

            // With it: the switches, the answer with Hand in, and a row per attribute; each control sends its verb.
            var sent = new List<string>();
            c.GuardianCommand = verb => { sent.Add(verb); return true; };
            c.Guardian = g;
            c.GuardianRevision++;
            main.Begin(); view.Draw(main); main.End();
            body = Encoding.UTF8.GetString(main.Body);
            Check(body.Contains("Temple guardians") && body.Contains("1 of 2 attribute turn-ins ready") && body.Contains("Pyreal")
                  && body.Contains("###gd.handin") && body.Contains("GRD") && body.Contains("Strength (Fiun Strongarm)") && body.Contains("in 5h 2m"),
                  "guardian controls, answer and rows");
            Apply(host, Event(1, 702, main.Hash, main.KeyOf("gd.handin"), new byte[] { 1, 0, 0 }));
            Apply(host, Event(2, 703, main.Hash, main.KeyOf("gd.auto"), new byte[] { 1 }));
            Apply(host, Event(2, 704, main.Hash, main.KeyOf("gd.buy"), new byte[] { 0 }));
            Apply(host, Event(1, 705, main.Hash, main.KeyOf("gd.refresh"), new byte[] { 1, 0, 0 }));
            Check(string.Join("|", sent) == "handin|auto on|buy off|refresh", "guardian controls send their verbs: " + string.Join("|", sent));
            main.MarkSubmitted(true);

            // Every kind at once on Infinite Leaftide (trackers' status, a guardian opened): under the ops limit.
            // The guardian is opened while Show lists guardians (they sort last), then Show goes back to everything.
            Apply(host, Event(1, 706, main.Hash, main.KeyOf("q.r.gd:Strength"), new byte[] { 1, 0, 0 }));
            view.ShowTrackers(null);
            main.Begin(); view.Draw(main); main.End();
            ParseAll(host, "ILT Quests, every tracker", out int ops);
            body = Encoding.UTF8.GetString(main.Body);
            int iltRows = trackerRows + DynamicStampList.All.Count + 2;
            Check(body.Contains($"{iltRows} shown") && body.Contains("Temple guardian: Strength") && body.Contains("###gd.sel.close"),
                  "every ILT tracker listed, a guardian opened below");
            Check(ops > 40 && ops < 1400, $"ILT Quests with every tracker and a guardian opened: {ops} ops");
            Console.WriteLine($"  InfiniteLeaftide/Quests, trackers only ({iltRows} rows, guardian opened): {ops} ops, {main.Body.Length} bytes");
        }

        private static void Events()
        {
            OracleContext c = Context();
            c.Settings.TabName = OracleView.TabQuests;
            var view = new OracleView(c);
            var host = new UiHost();
            UiWindow main = host.Add(new UiWindow("RynthOracle/Oracle", "Oracle") { Visible = true });
            main.Begin(); view.Draw(main); main.End();

            // Click the "Society" tab button.
            Apply(host, Event(1, 1, main.Hash, main.KeyOf("tab." + OracleView.TabSociety), new byte[] { 1, 0, 0 }));
            Check(view.Tab == OracleView.TabSociety, "a click on a tab switches to it");

            // Type in the quest search box, then check the filtered page.
            c.Settings.TabName = OracleView.TabQuests;
            main.Begin(); view.Draw(main); main.End();
            byte[] text = Encoding.UTF8.GetBytes("attribute");
            var payload = new byte[3 + text.Length];
            payload[0] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1), (ushort)text.Length);
            text.CopyTo(payload, 3);
            Apply(host, Event(5, 2, main.Hash, main.KeyOf("q.search"), payload));
            Check(main.NeedsPass, "an event asks for a pass");
            main.Begin(); view.Draw(main); main.End();
            string body = Encoding.UTF8.GetString(main.Body);
            Check(body.Contains("Attribute and Skill Redistribution"), "search narrows the quest list");

            // The player closes the window with its X.
            Apply(host, Event(8, 3, main.Hash, 0, new byte[] { 0, 1 }));
            Check(!main.Visible, "a player close hides the window");
        }

        private static byte[] Event(ushort type, uint seq, uint window, uint key, byte[] payload)
        {
            var e = new byte[16 + payload.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(e, type);
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(2), (ushort)e.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(4), seq);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(8), window);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(12), key);
            payload.CopyTo(e, 16);
            return e;
        }
    }
}
