using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RynthCore.Plugin.RynthOracle;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;
using RynthCore.Plugin.RynthOracle.Views;

namespace RynthOracle.Tests
{
    /// <summary>
    /// The Gear tab (2026-10-19, UB-ILT's Gear overlap and Paragon gear for every server): the
    /// portal.dat spell table and set reader, the overlap / ward / bane / set analysis, the ratings
    /// list, and both views through the engine's parser.
    /// </summary>
    internal static partial class Program
    {
        private const uint MetaEnchant = 1, MetaProjectile = 2;
        private const uint TargetCreature = 0x10, TargetArmor = 0x6, TargetWeapon = 0x8101;

        private static void Gear()
        {
            GearSpellTable();
            GearRealDat();
            GearNamesAndMath();
            GearOverlap();
            GearRatings();
            GearPages();
        }

        // ── portal.dat ───────────────────────────────────────────────────────

        /// <summary>A SpellTable file as ACE.DatLoader reads it: spells, then equipment sets.</summary>
        private static byte[] SpellTableBytes((uint Id, string Name, uint School, uint Cat, uint Power, uint Meta, uint Target)[] spells,
                                              (uint Set, (uint Tier, uint[] Ids)[] Tiers)[] sets)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            void Obf(string s)
            {
                byte[] b = Encoding.Latin1.GetBytes(s);
                for (int i = 0; i < b.Length; i++) b[i] = (byte)((b[i] << 4) | (b[i] >> 4));
                w.Write((ushort)b.Length);
                w.Write(b);
                while (ms.Position % 4 != 0) w.Write((byte)0);
            }
            w.Write(PortalSpellBook.SpellTableFileId);
            w.Write((ushort)spells.Length);
            w.Write((ushort)64);
            foreach (var s in spells)
            {
                w.Write(s.Id);
                Obf(s.Name);
                Obf("A test spell.");
                w.Write(s.School); w.Write(0u); w.Write(s.Cat); w.Write(0u); w.Write(10u);
                w.Write(0f); w.Write(0f);
                w.Write(s.Power);
                w.Write(1f); w.Write(1u); w.Write(0f);
                w.Write(s.Meta); w.Write(0u);
                if (s.Meta == 1 || s.Meta == 12) { w.Write(3600d); w.Write(0f); w.Write(0f); }
                else if (s.Meta == 7) w.Write(60d);
                for (int i = 0; i < 8; i++) w.Write(0u);
                w.Write(0u); w.Write(0u); w.Write(0u);
                w.Write(0d); w.Write(0f); w.Write(0u);
                w.Write(s.Target);
                w.Write(0u);
            }
            w.Write((ushort)sets.Length);
            w.Write((ushort)16);
            foreach (var set in sets)
            {
                w.Write(set.Set);
                w.Write((ushort)set.Tiers.Length);
                w.Write((ushort)8);
                foreach (var (tier, ids) in set.Tiers)
                {
                    w.Write(tier);
                    w.Write(ids.Length);
                    foreach (uint id in ids) w.Write(id);
                }
            }
            return ms.ToArray();
        }

        private static readonly (uint, string, uint, uint, uint, uint, uint)[] TestSpells =
        {
            (1, "Major Strength", 4, 261, 15, MetaEnchant, TargetCreature),
            (2, "Legendary Strength", 4, 261, 35, MetaEnchant, TargetCreature),
            (3, "Legendary Flame Ward", 2, 287, 25, MetaEnchant, TargetCreature),
            (4, "Epic Flame Ward", 2, 287, 20, MetaEnchant, TargetCreature),
            (5, "Legendary Slashing Bane", 3, 395, 4, MetaEnchant, TargetArmor),
            (6, "Major Slashing Bane", 3, 395, 2, MetaEnchant, TargetArmor),
            (7, "Legendary Defender", 3, 329, 9, MetaEnchant, TargetWeapon),
            (8, "Legendary Willpower", 4, 265, 35, MetaEnchant, TargetCreature),
            (9, "Flame Bolt Proc", 1, 77, 100, MetaProjectile, TargetCreature),
            (10, "Frost Bolt Proc", 1, 77, 120, MetaProjectile, TargetCreature),
            (11, "Set Bonus Spell", 4, 600, 50, MetaEnchant, TargetCreature),
            (12, "Set Bonus Spell II", 4, 600, 60, MetaEnchant, TargetCreature),
        };

        private static PortalSpellBook TestBook()
        {
            byte[] raw = SpellTableBytes(TestSpells, new[] { (13u, new[] { (2u, new uint[] { 11 }), (4u, new uint[] { 12 }) }) });
            var (spells, sets) = PortalSpellBook.Parse(raw);
            return PortalSpellBook.FromParsed(spells, sets);
        }

        private static void GearSpellTable()
        {
            byte[] raw = SpellTableBytes(TestSpells, new[] { (13u, new[] { (2u, new uint[] { 11 }), (4u, new uint[] { 12 }) }) });
            var (spells, sets) = PortalSpellBook.Parse(raw);
            Check(spells.Count == TestSpells.Length, $"spell table: every spell read ({spells.Count})");
            Check(spells[5].Name == "Legendary Slashing Bane" && spells[5].Category == 395 && spells[5].Power == 4,
                  "spell table: name, family and power");
            Check(spells[5].IsItemTarget && !spells[3].IsItemTarget, "spell table: a bane is cast on an item, a ward on the character");
            Check(spells[1].IsEnchantment && !spells[9].IsEnchantment, "spell table: enchantments and projectiles told apart");
            Check(sets.Count == 1 && sets[13].Tiers.Count == 2 && sets[13].HighestTier == 4, "spell table: the equipment set and its tiers");

            SpellSetDef d = sets[13];
            Check(d.SpellsAt(1).Length == 0, "set: below the first tier, no bonus");
            Check(d.SpellsAt(2).SequenceEqual(new uint[] { 11 }) && d.SpellsAt(3).SequenceEqual(new uint[] { 11 }), "set: a level between tiers takes the lower tier");
            Check(d.SpellsAt(9).SequenceEqual(new uint[] { 12 }) && d.TierAt(9) == 4, "set: a level past the top is capped at the highest tier");

            var (cut, _) = PortalSpellBook.Parse(raw.Take(raw.Length / 2).ToArray());
            Check(cut.Count > 0 && cut.Count < TestSpells.Length, $"spell table: a cut-short file keeps what it read ({cut.Count})");
        }

        /// <summary>The real client's table, when this machine has one (skipped otherwise).</summary>
        private static void GearRealDat()
        {
            const string path = @"C:\Turbine\Asheron's Call\client_portal.dat";
            if (!File.Exists(path)) { Console.WriteLine("  gear: no client_portal.dat here, real table check skipped"); return; }
            using var dat = new RynthCore.Plugin.RynthAi.Raycasting.DatDatabase();
            if (!dat.Open(path)) { Check(false, "real portal.dat opens"); return; }
            byte[]? raw = dat.GetFileData(PortalSpellBook.SpellTableFileId);
            Check(raw != null, "real portal.dat has the spell table");
            if (raw == null) return;
            var (spells, sets) = PortalSpellBook.Parse(raw);
            Check(spells.Count > 6000, $"real table: {spells.Count} spells");
            Check(spells.TryGetValue(6097, out PortalSpell bane) && bane.Name == "Legendary Slashing Bane" && bane.Category == 395 && bane.IsItemTarget,
                  "real table: Legendary Slashing Bane is an item spell of family 395");
            Check(spells.TryGetValue(6082, out PortalSpell ward) && ward.Name == "Legendary Flame Ward" && ward.Category == 287 && !ward.IsItemTarget,
                  "real table: Legendary Flame Ward is a character spell of family 287");
            Check(sets.Count > 100 && sets.TryGetValue(13, out SpellSetDef? s13) && s13.SpellsAt(3).Length > 0, $"real table: {sets.Count} equipment sets");
            Console.WriteLine($"  gear: real portal.dat, {spells.Count} spells, {sets.Count} sets");
        }

        private static void GearNamesAndMath()
        {
            Check(GearNames.FamilyLabel(403) == "Slash Ward", "family label: " + GearNames.FamilyLabel(403));
            Check(GearNames.FamilyLabel(395) == "Slash Resistance", "bane family label: " + GearNames.FamilyLabel(395));
            Check(GearNames.FamilyLabel(261) == "Strength Increase", "cantrip family label: " + GearNames.FamilyLabel(261));
            Check(GearNames.FamilyLabel(99_999) == "Family 99999" && GearNames.FamilyLabel(0) == "?", "an unknown family still shows its number");
            Check(GearNames.SetName(138) == "Paragon Missile" && GearNames.SetName(0) == "-" && GearNames.SetName(9_999) == "Set 9999", "set names");
            Check(GearNames.Pretty("ShimmeringShadowsSet") == "Shimmering Shadows Set" && GearNames.Pretty("PKDamage") == "PK Damage",
                  "CamelCase to words: " + GearNames.Pretty("PKDamage"));

            Check(GearMath.ItemLevel(50, 10, 20, 1) == 5, "item level: fixed cost per level");
            Check(GearMath.ItemLevel(70, 10, 20, 2) == 3, "item level: each level costs double (10 + 20 + 40)");
            Check(GearMath.ItemLevel(15, 10, 20, 3) == 1 && GearMath.ItemLevel(50, 10, 20, 3) == 4, "item level: fixed plus base");
            Check(GearMath.ItemLevel(1_000, 10, 20, 1) == 20, "item level: capped at the maximum");
            Check(GearMath.ItemLevel(1_000, 0, 20, 1) == 0, "item level: no base experience, no level");
        }

        /// <summary>Worn pieces built by hand (the test spells above).</summary>
        private static List<GearPiece> TestPieces() => new()
        {
            new GearPiece { Id = 0x8000_0001, Name = "Chest", ItemType = GearAnalysis.ItemArmor, Equipped = true, Appraised = true,
                            Inscribed = new uint[] { 2, 5 }, OnItem = new uint[] { 6 }, Ratings = new[] { (371, 3), (373, 2) } },
            new GearPiece { Id = 0x8000_0002, Name = "Ring", ItemType = GearAnalysis.ItemJewelry, Equipped = true, Appraised = true,
                            Inscribed = new uint[] { 1, 8 }, SetId = 13, Ratings = new[] { (370, 4) } },
            new GearPiece { Id = 0x8000_0003, Name = "Bracelet", ItemType = GearAnalysis.ItemJewelry, Equipped = true, Appraised = true,
                            Inscribed = new uint[] { 8, 9 }, SetId = 13 },
            new GearPiece { Id = 0x8000_0004, Name = "Fire Sword", ItemType = GearAnalysis.ItemMelee, Equipped = true, Appraised = true,
                            Inscribed = new uint[] { 7, 10, 99_999 }, DamageType = 0x10, Ratings = new[] { (370, 5), (374, 2) } },
            new GearPiece { Id = 0x8000_0005, Name = "Unread Gloves", ItemType = GearAnalysis.ItemArmor, Equipped = true, Appraised = false },
        };

        private static GearReport TestReport(GearScanner scanner)
        {
            var pieces = TestPieces();
            return GearAnalysis.Analyze(pieces, new uint[] { 3, 4, 2 }, scanner.Lookup,
                id => scanner.Book.TryGetSet((uint)id, out SpellSetDef d) ? d : null, "Tester", new DateTime(2026, 10, 19, 17, 0, 0));
        }

        private static void GearOverlap()
        {
            var scanner = new GearScanner { Book = TestBook() };
            GearReport r = TestReport(scanner);

            Check(r.Equipped == 5 && r.NotAppraised == 1, $"overlap: 5 worn, 1 not appraised ({r.NotAppraised})");
            Check(r.UnknownSpells == 1, $"overlap: the spell no table knows is counted ({r.UnknownSpells})");

            OverlapRow? str = r.Rows.FirstOrDefault(x => x.Category == 261);
            Check(str != null && str.Kind == OverlapKind.Overlap && str.Winner.Spell.Id == 2 && str.Scope == "",
                  "overlap: Major and Legendary Strength compete on the character, Legendary wins");
            Check(str != null && str.Summary.Contains("(beat)"), "overlap: the losing source is marked (beat): " + str?.Summary);

            OverlapRow? will = r.Rows.FirstOrDefault(x => x.Category == 265);
            Check(will != null && will.Kind == OverlapKind.Duplicate && will.Summary.Contains("(extra)"),
                  "duplicate: the same cantrip on the ring and the bracelet: " + will?.Summary);

            OverlapRow? procs = r.Rows.FirstOrDefault(x => x.Category == 77);
            Check(procs != null && procs.Kind == OverlapKind.Stacks, "stacks: two projectiles of one family both work");

            OverlapRow? bane = r.Rows.FirstOrDefault(x => x.Category == 395);
            Check(bane != null && bane.Kind == OverlapKind.Overlap && bane.Scope == "Chest" && bane.Winner.Spell.Id == 5,
                  "per item: the chest's inscribed Legendary bane beats the Major bane cast on it");
            Check(r.Rows.Count(x => x.Category == 329) == 1 && r.Rows.First(x => x.Category == 329).Scope == "Fire Sword",
                  "per item: a weapon spell's row is the weapon's");
            Check(r.Rows.First(x => x.Kind == OverlapKind.Overlap).Kind <= r.Rows.Last().Kind, "rows: overlap first, OK last");

            ProtSummary p = r.Protection;
            ProtRow fire = p.Life.First(x => x.Element == "Fire");
            Check(fire.Tier == ProtTier.Legendary && fire.Winner == "Legendary Flame Ward" && fire.Beaten.Contains("Epic Flame Ward"),
                  $"wards: Legendary Flame Ward wins over Epic ({fire.Tier}, beats {fire.Beaten})");
            ProtRow slash = p.Crit.First(x => x.Element == "Slashing");
            Check(slash.Tier == ProtTier.Legendary && slash.Source == "Chest", $"banes: the chest's Legendary Slashing Bane ({slash.Tier} from {slash.Source})");
            Check(p.LifeLegendary == 1 && p.CritLegendary == 1 && p.Life.Count(x => x.Missing) == 6 && p.LifeMissing.StartsWith("Slashing"),
                  $"wards and banes: 1 + 1 Legendary, six life elements missing ({p.LifeMissing})");

            Check(r.Sets.Count == 1 && r.Sets[0].Pieces == 2 && r.Sets[0].Spells.Count == 1 && r.Sets[0].Spells[0].Spell.Id == 11,
                  "set: two pieces of set 13 give the tier-2 bonus");
            Check(r.Buffs.Count == 3 && r.Buffs.All(b => b.ItemName == "Tester"), "buffs: the character's enchantments, by name");
            Check(r.PieceSpells[0x8000_0001].Count == 3 && r.PieceSpells[0x8000_0001].Any(s => s.Layer == SpellLayer.OnItem),
                  "pieces: inscribed and active-on-item spells listed per piece");

            // A leveled set: its level is the sum of the pieces' item levels.
            var leveled = new List<GearPiece>
            {
                new() { Id = 1, Name = "A", SetId = 13, XpStyle = 1, BaseXp = 10, MaxLevel = 5, TotalXp = 30, Equipped = true },
                new() { Id = 2, Name = "B", SetId = 13, XpStyle = 1, BaseXp = 10, MaxLevel = 5, TotalXp = 20, Equipped = true },
            };
            var sets = GearAnalysis.SetBonuses(leveled, id => scanner.Book.TryGetSet((uint)id, out SpellSetDef d) ? d : null, scanner.Lookup);
            Check(sets.Count == 1 && sets[0].Level == 5 && sets[0].Tier == 4 && sets[0].Spells[0].Spell.Id == 12,
                  $"leveled set: levels 3 + 2 reach tier 4 ({sets.FirstOrDefault()?.Level})");
        }

        private static void GearRatings()
        {
            var pieces = TestPieces();
            pieces.Add(new GearPiece { Id = 0x8000_0010, Name = "Spare Paragon Bow", ItemType = GearAnalysis.ItemMissile, SetId = 138,
                                       MaxLevel = 50, BaseXp = 2_000_000_000, XpStyle = 1, TotalXp = 6_000_000_000, DamageType = 0x8 });
            pieces.Add(new GearPiece { Id = 0x8000_0011, Name = "Plain Cloak", ItemType = GearAnalysis.ItemClothing });

            RatingsReport worn = GearAnalysis.Ratings(pieces, includePack: false, DateTime.Now);
            Check(worn.Rows.All(x => x.Piece.Equipped) && worn.Rows.Count == 4 && worn.Rows.All(x => x.Piece.Name != "Unread Gloves"),
                  $"ratings: worn items with a set or ratings, the plain gloves left out ({worn.Rows.Count})");
            Check(worn.WornTotals.Contains((370, 9)) && worn.WornTotals.Contains((371, 3)) && worn.WornTotals.Contains((374, 2)),
                  "ratings: worn totals add up: " + string.Join(" ", worn.WornTotals));
            RatingRow sword = worn.Rows.First(x => x.Piece.Name == "Fire Sword");
            Check(sword.TypeLabel == "Weapon Fire" && sword.Stats.Contains("Damage 5"), $"ratings: a weapon's element and ratings ({sword.TypeLabel}: {sword.Stats})");

            RatingsReport all = GearAnalysis.Ratings(pieces, includePack: true, DateTime.Now);
            RatingRow bow = all.Rows.First(x => x.Piece.Name == "Spare Paragon Bow");
            Check(bow.Paragon && bow.SetName == "Paragon Missile" && bow.Piece.Level == 3 && !bow.Piece.Equipped,
                  $"ratings: a pack paragon bow, level {bow.Piece.Level}/50");
            Check(all.Rows.All(x => x.Piece.Name != "Plain Cloak"), "ratings: gear with no set, level or ratings is left out");
            Check(all.Rows.First().Piece.Equipped && !all.Rows.Last().Piece.Equipped, "ratings: worn first, then the packs");
            Check(!all.WornTotals.Any(t => t.Prop == 0), "ratings: pack items don't count in the worn totals");
        }

        private static void GearPages()
        {
            OracleContext c = Context();
            var view = new OracleView(c);
            var host = new UiHost();
            UiWindow main = host.Add(new UiWindow("RynthOracle/Oracle", "Oracle") { Visible = true, DefaultWidth = 700, DefaultHeight = 860 });
            c.Server.Force("", "");
            Check(view.VisibleTabs().Contains(OracleView.TabGear), "the Gear tab is on every server (here: retail)");
            c.Settings.TabName = OracleView.TabGear;

            // Before any Check.
            c.Settings.GearView = OracleView.GearOverlap;
            main.Begin(); view.Draw(main); main.End();
            ParseAll(host, "gear overlap, empty", out int emptyOps);
            Check(Encoding.UTF8.GetString(main.Body).Contains("Press Check"), "gear: says to press Check before the first one");
            main.MarkSubmitted(true);

            var scanner = new GearScanner { Book = TestBook() };
            c.Gear.Book = scanner.Book;
            c.Gear.UseReports(TestReport(scanner), GearAnalysis.Ratings(TestPieces(), true, DateTime.Now));

            main.Begin(); view.Draw(main); main.End();
            ParseAll(host, "gear overlap", out int ops);
            string body = Encoding.UTF8.GetString(main.Body);
            Check(ops > 20 && ops < 1400, $"gear overlap: {ops} ops");
            Check(body.Contains("Legendary Flame Ward") && body.Contains("MISSING") && body.Contains("Duplicate - the same spell"),
                  "gear overlap: wards, a missing element and the duplicate section");
            Check(body.Contains("Set bonuses") && body.Contains("not appraised"), "gear overlap: set bonuses and the unread piece");
            Console.WriteLine($"  gear overlap: {ops} ops, {main.Body.Length} bytes");
            main.MarkSubmitted(true);

            c.Settings.GearHideOverlap = true;
            main.Begin(); view.Draw(main); main.End();
            ParseAll(host, "gear overlap hidden", out _);
            Check(!Encoding.UTF8.GetString(main.Body).Contains("Overlap - highest power wins"), "gear: Hide overlap hides the overlap rows");
            main.MarkSubmitted(true);

            c.Settings.GearView = OracleView.GearRatings;
            main.Begin(); view.Draw(main); main.End();
            ParseAll(host, "gear ratings", out int rops);
            string rbody = Encoding.UTF8.GetString(main.Body);
            Check(rops > 10 && rops < 1400, $"gear ratings: {rops} ops");
            Check(rbody.Contains("Worn totals") && rbody.Contains("Fire Sword") && rbody.Contains("Include pack items"),
                  "gear ratings: totals, rows and the pack option");
            main.MarkSubmitted(true);
            c.Settings.GearView = OracleView.GearOverlap;
            c.Settings.GearHideOverlap = false;
        }
    }
}
