using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>
/// Gear: how the worn gear's spells fit together, for any server. Two views, as UB-ILT's
/// Character tab had them:
///   - Overlap: life wards and banes by element and tier, every spell on each worn piece (and the
///     set bonuses, paragon spells and the character's buffs), and the families where spells
///     compete (the highest power wins), repeat (the same spell on two pieces) or stack.
///   - Ratings: items with a set, an item level or gear ratings (UB-ILT's Paragon gear), with the
///     ratings summed over what is worn.
/// The Check button reads it all again (GearScanner); nothing is read until it is pressed.
/// </summary>
internal sealed partial class OracleView
{
    public const string GearOverlap = "Overlap", GearRatings = "Ratings";

    // Tier colours (UB-ILT's): Minor green, Major blue, Epic purple, Legendary orange; missing red.
    private static readonly uint TierMinor = UiFormat.Rgb(120, 220, 120), TierMajor = UiFormat.Rgb(140, 180, 255),
        TierEpic = UiFormat.Rgb(200, 140, 255), TierLegendary = UiFormat.Rgb(255, 180, 100), TierLower = UiFormat.Rgb(190, 190, 190);
    private static readonly uint HeaderBlue = UiFormat.Rgb(180, 200, 255), SetPurple = UiFormat.Rgb(200, 180, 255),
        ParagonTan = UiFormat.Rgb(220, 200, 140), WornGreen = UiFormat.Rgb(200, 235, 180), PackGrey = UiFormat.Rgb(210, 210, 210),
        T11Teal = UiFormat.Rgb(120, 220, 210);

    private string GearView
    {
        get => _c.Settings.GearView == GearRatings ? GearRatings : GearOverlap;
        set { _c.Settings.GearView = value; _c.Settings.Save(); }
    }

    private void DrawGear(UiWindow w)
    {
        string view = GearView;
        w.ToggleButton(GearOverlap, "g.v.o", view == GearOverlap, () => GearView = GearOverlap, small: true);
        w.Tooltip("Spell overlap and stacking on your worn gear, life wards and banes.");
        w.SameLine();
        w.ToggleButton(GearRatings, "g.v.r", view == GearRatings, () => GearView = GearRatings, small: true);
        w.Tooltip("Sets, item levels and gear ratings (paragon gear where the server has it).");
        w.SameLine();
        w.PushStyleColor(21, UiFormat.Rgb(150, 30, 30));   // ImGuiCol_Button: UB-ILT's red Check
        w.Button(view == GearOverlap ? "Check spell overlap" : "Check gear ratings", "g.check", StartGearCheck, small: true);
        w.PopStyleColor();
        w.Tooltip("Reads your worn gear again: asks the server to appraise items it hasn't yet, then rebuilds the lists.");
        DrawGearProgress(w);
        w.Separator();
        if (view == GearRatings) DrawGearRatings(w);
        else DrawGearOverlap(w);
    }

    /// <summary>Starts a Check (the button, the Refresh row and a change of the pack option).</summary>
    private void StartGearCheck()
    {
        if (_c.Gear.Busy) return;
        if (!_c.Gear.Start(_c.Host, _c.NowMs, _c.Settings.GearIncludePack, msg => _c.Host.Log(msg)) && _c.Gear.Problem.Length > 0)
            _c.Print("Gear: " + _c.Gear.Problem);
    }

    private RefreshResult RefreshGear()
    {
        if (_c.Gear.Busy) return new RefreshResult("A gear check is already running.", () => !_c.Gear.Busy);
        StartGearCheck();
        return _c.Gear.Problem.Length > 0
            ? new RefreshResult(_c.Gear.Problem)
            : new RefreshResult("Checking your gear.", () => !_c.Gear.Busy);
    }

    private void DrawGearProgress(UiWindow w)
    {
        GearScanner g = _c.Gear;
        switch (g.State)
        {
            case GearScanner.Phase.Identifying:
                w.TextColored(UiColors.Yellow, $"Appraising items: {g.IdsSent} of {g.IdsQueued} asked...");
                break;
            case GearScanner.Phase.Settling:
                w.TextColored(UiColors.Yellow, "Waiting for the appraisals...");
                break;
            case GearScanner.Phase.WaitingForSpells:
                w.TextColored(UiColors.Yellow, "Reading the client's spell table...");
                break;
            default:
                if (g.Problem.Length > 0) w.TextColored(UiColors.Red, g.Problem);
                break;
        }
    }

    // ── Overlap ──────────────────────────────────────────────────────────────

    private void DrawGearOverlap(UiWindow w)
    {
        GearReport? r = _c.Gear.Overlap;
        w.TextDisabled("Worn and wielded gear and your buffs: wards, banes, set bonuses and the spells that compete.");
        if (r == null)
        {
            w.TextDisabled("Press Check to scan your worn gear and active buffs.");
            return;
        }
        w.Text($"Checked {r.CheckedLocal:HH:mm:ss}  |  {r.Equipped} worn  |  {r.SpellSources} spells  |  "
               + $"{r.Count(OverlapKind.Overlap)} overlap / {r.Count(OverlapKind.Duplicate)} duplicate / {r.Count(OverlapKind.Stacks)} stack");
        ProtSummary prot = r.Protection;
        string line2 = $"Life {prot.LifeLegendary}/7 Legendary  |  Crit {prot.CritLegendary}/7 Legendary";
        if (r.Sets.Count > 0) line2 += $"  |  Sets: {r.Sets.Count}";
        if (r.Paragon.Count > 0) line2 += $"  |  Paragon: {r.Paragon.Count}";
        if (r.Buffs.Count > 0) line2 += $"  |  Buffs: {r.Buffs.Count}";
        w.TextDisabled(line2);
        if (prot.LifeMissing.Length > 0) w.TextColored(UiColors.Red, "Life wards missing: " + prot.LifeMissing);
        if (prot.CritMissing.Length > 0) w.TextColored(UiColors.Red, "Banes missing: " + prot.CritMissing);
        if (r.NotAppraised > 0) w.TextColored(UiColors.Yellow, $"{r.NotAppraised} item(s) not appraised yet: Check again in a moment.");
        if (r.UnknownSpells > 0) w.TextDisabled($"{r.UnknownSpells} spell id(s) the client's spell table doesn't know.");
        if (!PortalSpellBook.Shared.HasDat && PortalSpellBook.Shared.Done)
            w.TextColored(UiColors.Yellow, "Spells from RynthOracle's own table (" + PortalSpellBook.Shared.Status + "): custom spells may show as unknown.");

        if (w.CollapsingHeader("Life / Crit protection (Minor - Major - Epic - Legendary)", "g.prot", defaultOpen: true))
        {
            w.TextDisabled("Highest tier wins: Legendary > Epic > Major > Minor (red = missing element).");
            DrawProtSection(w, "Life prot (7 wards)", prot.Life, "g.pl");
            DrawProtSection(w, "Crit prot (7 banes)", prot.Crit, "g.pc");
        }

        if (w.CollapsingHeader("Equipped gear spells", "g.gear", defaultOpen: true))
            DrawGearSpells(w, r);

        if (w.CollapsingHeader("Overlap / stacking", "g.over", defaultOpen: true))
        {
            bool hide = _c.Settings.GearHideOverlap;
            w.Checkbox("Hide overlap", "g.hide", hide, v => { _c.Settings.GearHideOverlap = v; _c.Settings.Save(); });
            w.Tooltip("Hide the families where spells compete, to see what stacks.");
            DrawOverlapRows(w, r, hide);
        }
    }

    private static void DrawProtSection(UiWindow w, string title, List<ProtRow> rows, string id)
    {
        w.SeparatorText(title);
        w.TextColored(HeaderBlue, "Element");
        w.SameLine(90f); w.TextColored(HeaderBlue, "Tier");
        w.SameLine(175f); w.TextColored(HeaderBlue, "Active spell");
        w.SameLine(380f); w.TextColored(HeaderBlue, "Source");
        w.SameLine(540f); w.TextColored(HeaderBlue, "Beats");
        foreach (ProtRow row in rows)
        {
            w.TextColored(row.Missing ? UiColors.Red : ElementColor(row.Element), row.Element);
            w.SameLine(90f); w.TextColored(TierColor(row.Tier), GearAnalysis.TierLabel(row.Tier));
            if (row.Missing) continue;
            w.SameLine(175f); w.Text(Cut(row.Winner, 30));
            w.SameLine(380f); w.Text(Cut(row.Source, 22));
            if (row.Beaten.Length > 0) { w.SameLine(540f); w.TextDisabled(Cut(row.Beaten, 30)); }
        }
    }

    private static void GearHeader(UiWindow w)
    {
        w.TextColored(HeaderBlue, "Item");
        w.SameLine(200f); w.TextColored(HeaderBlue, "Layer");
        w.SameLine(280f); w.TextColored(HeaderBlue, "Spell");
        w.SameLine(490f); w.TextColored(HeaderBlue, "Family");
        w.SameLine(640f); w.TextColored(HeaderBlue, "P");
    }

    private static void GearSpellRow(UiWindow w, string item, SpellSource s, uint color)
    {
        w.TextColored(color, Cut(item, 26));
        w.SameLine(200f); w.TextColored(color, s.LayerLabel);
        w.SameLine(280f); w.TextColored(color, Cut(s.Spell.Name, 30));
        w.SameLine(490f); w.TextColored(color, Cut(GearNames.FamilyLabel(s.Spell.Category), 22));
        w.SameLine(640f); w.TextColored(color, s.Spell.Power.ToString(CultureInfo.InvariantCulture));
    }

    private void DrawGearSpells(UiWindow w, GearReport r)
    {
        if (r.Pieces.Count == 0)
        {
            w.TextDisabled("No equipped items found.");
            return;
        }
        GearHeader(w);
        if (r.Sets.Count > 0)
        {
            w.SeparatorText("Set bonuses");
            foreach (SetBonusGroup g in r.Sets)
            {
                bool first = true;
                foreach (SpellSource s in g.Spells)
                {
                    GearSpellRow(w, first ? g.Label : "", s, SetPurple);
                    if (first) w.Tooltip(g.PieceNames);
                    first = false;
                }
            }
        }
        if (r.Paragon.Count > 0)
        {
            w.SeparatorText($"Paragon spells ({r.Paragon.Count})");
            foreach (SpellSource s in r.Paragon) GearSpellRow(w, s.ItemName, s, ParagonTan);
        }

        w.SeparatorText("Equipped gear");
        foreach (GearPiece p in r.Pieces)
        {
            string label = $"{p.Name} [{GearAnalysis.CategoryOf(p.ItemType)}{(p.Appraised ? "" : ", not appraised")}]";
            List<SpellSource> spells = r.PieceSpells.TryGetValue(p.Id, out var l) ? l : new List<SpellSource>();
            if (spells.Count == 0)
            {
                w.Text(Cut(label, 26));
                w.Tooltip(label);
                w.SameLine(200f);
                w.TextDisabled(p.Appraised ? "(no spells)" : "(appraisal pending)");
                continue;
            }
            bool first = true;
            foreach (SpellSource s in spells)
            {
                GearSpellRow(w, first ? label : "", s, UiFormat.Rgb(255, 255, 255));
                if (first) w.Tooltip(label);
                first = false;
            }
        }

        // The character's buffs: often fifty or more rows, so they start folded.
        if (r.Buffs.Count > 0 && w.CollapsingHeader($"Character buffs ({r.Buffs.Count})", "g.buffs", defaultOpen: false))
        {
            GearHeader(w);
            foreach (SpellSource s in r.Buffs) GearSpellRow(w, s.ItemName, s, HeaderBlue);
        }
    }

    private static void DrawOverlapRows(UiWindow w, GearReport r, bool hideOverlap)
    {
        var rows = r.Rows.Where(x => x.Kind != OverlapKind.Ok && !(hideOverlap && x.Kind == OverlapKind.Overlap)).ToList();
        if (rows.Count == 0)
        {
            w.TextDisabled(r.Rows.Any(x => x.Kind == OverlapKind.Overlap) && hideOverlap
                ? "Overlap rows hidden (uncheck Hide overlap)."
                : r.SpellSources > 0 ? "No competing families: every spell counts." : "No spells read yet: Check again once the appraisals are in.");
            return;
        }
        OverlapKind? last = null;
        foreach (OverlapRow row in rows)
        {
            if (row.Kind != last)
            {
                w.SeparatorText(row.Kind switch
                {
                    OverlapKind.Overlap => "Overlap - highest power wins, the rest do nothing",
                    OverlapKind.Duplicate => "Duplicate - the same spell on more than one item",
                    _ => "Stacks - these combine",
                });
                w.TextColored(HeaderBlue, "Family");
                w.SameLine(170f); w.TextColored(HeaderBlue, "On");
                w.SameLine(300f); w.TextColored(HeaderBlue, "Winner");
                w.SameLine(490f); w.TextColored(HeaderBlue, "Sources");
                last = row.Kind;
            }
            uint color = row.Kind == OverlapKind.Stacks ? UiColors.Green : row.Kind == OverlapKind.Duplicate ? UiColors.Yellow : UiFormat.Rgb(255, 255, 255);
            w.TextColored(color, Cut(row.Family, 22));
            w.SameLine(170f); w.Text(row.Scope.Length > 0 ? Cut(row.Scope, 18) : "character");
            w.SameLine(300f); w.Text(Cut($"{row.Winner.Spell.Name} P{row.Winner.Spell.Power}", 28));
            w.SameLine(490f); w.TextWrapped(row.Summary);
        }
    }

    // ── Ratings ──────────────────────────────────────────────────────────────

    private void DrawGearRatings(UiWindow w)
    {
        bool pack = _c.Settings.GearIncludePack;
        w.Checkbox("Include pack items", "g.pack", pack, v =>
        {
            _c.Settings.GearIncludePack = v;
            _c.Settings.Save();
            StartGearCheck();
        });
        w.Tooltip("Also list the sets, levels and ratings of the gear in your packs (each one is appraised once).");
        bool mag = _c.Settings.GearRatingsMag;
        w.SameLine();
        w.ToggleButton("Mag-style", "g.fmt.mag", mag, () => { _c.Settings.GearRatingsMag = true; _c.Settings.Save(); }, small: true);
        w.Tooltip("Each item as one Mag-Tools ItemInfo line (what Mag-Tools prints when you identify it).");
        w.SameLine();
        w.ToggleButton("Columns", "g.fmt.col", !mag, () => { _c.Settings.GearRatingsMag = false; _c.Settings.Save(); }, small: true);
        w.Tooltip("Rated items in columns: Loc, Item, Type, Set, Level, Ratings.");
        w.TextDisabled(mag
            ? "Mag-Tools ItemInfo lines for your gear; [D, DR, C, CD, CR, CDR, HB, V ...] are its ratings, T11 [...] its modifiers."
            : "Loc | Item | Type | Set | Level | Ratings (T11 modifier lines under the item). Weapon rows are tinted by damage type.");

        RatingsReport? r = _c.Gear.Ratings;
        if (r == null)
        {
            w.TextDisabled("Press Check to read your gear's sets, levels and ratings.");
            return;
        }
        int rated = r.RatedRows.Count();
        w.Text($"Checked {r.CheckedLocal:HH:mm:ss}  |  {(mag ? r.Rows.Count : rated)} item(s)  |  {rated} rated, {r.EquippedRows} of them worn  |  "
               + (r.IncludePack ? "worn + packs" : "worn only"));
        if (r.IncludePack != pack) w.TextColored(UiColors.Yellow, "The pack option changed: Check again to apply it.");
        if (r.WornTotals.Count > 0)
            w.TextColored(UiColors.Gold, "Worn totals: " + string.Join("  ", r.WornTotals.Select(t => GearAnalysis.RatingShort(t.Prop) + " " + t.Value)));
        if (r.WornTotals.Count > 0 || r.T11Totals.Count > 0)
            w.Tooltip(string.Join("\n", r.WornTotals.Select(t => GearAnalysis.RatingLong(t.Prop) + " " + t.Value))
                      + (r.T11Rows > 0 ? "\nT11 modifier ratings are included (the appraisal lists them under Modifiers, not Ratings)." : ""));
        // The T11 modifiers (worn totals and each item's lines) are on the T11 Tracking tab (Infinite Leaftide, 2026-11-11).
        if (r.T11Rows > 0 && _c.Server.IsIlt)
            w.TextColored(T11Teal, $"{r.T11Rows} T11 item(s): their modifiers are on the T11 Tracking tab.");
        else if (r.T11Totals.Count > 0)
            w.TextColored(T11Teal, "T11 worn: " + string.Join("  |  ", r.T11Totals.Select(t => t.Name + " " + t.Value)));

        if (mag)
        {
            DrawMagLines(w, r);
            return;
        }
        if (rated == 0)
        {
            w.TextDisabled("No gear with a set, an item level or ratings.");
            return;
        }
        w.TextColored(HeaderBlue, "Loc");
        w.SameLine(45f); w.TextColored(HeaderBlue, "Item");
        w.SameLine(230f); w.TextColored(HeaderBlue, "Type");
        w.SameLine(330f); w.TextColored(HeaderBlue, "Set");
        w.SameLine(480f); w.TextColored(HeaderBlue, "Level");
        w.SameLine(550f); w.TextColored(HeaderBlue, "Ratings");
        foreach (RatingRow row in r.RatedRows)
        {
            GearPiece p = row.Piece;
            uint baseColor = p.Equipped ? WornGreen : PackGrey;
            uint tint = RowTint(p, baseColor);
            w.TextColored(baseColor, p.Equipped ? "Worn" : "Pack");
            w.SameLine(45f); w.TextColored(tint, Cut(p.Name, 26));
            w.Tooltip(p.Name);
            w.SameLine(230f); w.TextColored(tint, row.TypeLabel);
            w.SameLine(330f); w.TextColored(row.Paragon ? ParagonTan : baseColor, Cut(row.SetName, 20));
            w.SameLine(480f); w.TextColored(baseColor, p.MaxLevel > 0 ? $"{p.Level}/{p.MaxLevel}" : "-");
            w.SameLine(550f);
            if (row.Stats.Length > 0) w.TextWrapped(row.Stats);
            else w.TextDisabled("-");
            // T11 gear's modifier lines: the T11 Tracking tab (their rating lines are in Ratings above). Off Infinite
            // Leaftide (no such tab) they stay here.
            if (row.T11.Length > 0 && !_c.Server.IsIlt)
            {
                w.Dummy(1f, 0f);   // starts the line; the label then sits under the Item column
                w.SameLine(45f);
                w.TextColored(T11Teal, "T11:");
                w.SameLine(90f);
                w.TextWrapped(row.T11 + (p.ZoneLocked ? "  (zone locked: power reduced outside its zone)" : ""));
            }
        }
    }

    /// <summary>A weapon's row in its damage type's colour, anything else in the worn / pack colour.</summary>
    private static uint RowTint(GearPiece p, uint baseColor) =>
        (p.ItemType & GearAnalysis.ItemWeapons) != 0 && p.DamageType != 0 ? ElementColorFlags(p.DamageType) : baseColor;

    /// <summary>
    /// The Mag-style view: every item as its Mag-Tools ItemInfo line, worn first, weapons tinted by
    /// damage type, paragon pieces in tan; Print to chat writes the lines as Mag-Tools would.
    /// </summary>
    private void DrawMagLines(UiWindow w, RatingsReport r)
    {
        if (r.Rows.Count == 0)
        {
            w.TextDisabled("No gear found.");
            return;
        }
        RatingsReport report = r;
        w.SmallButton("Print to chat", "g.mag.print", () =>
        {
            foreach (RatingRow row in report.Rows) _c.Print((row.Piece.Equipped ? "" : "(pack) ") + row.Mag);
        });
        w.Tooltip("Writes every line below to your chat window (only you see it).");
        bool packHeader = false;
        foreach (RatingRow row in r.Rows)
        {
            GearPiece p = row.Piece;
            if (!p.Equipped && !packHeader)
            {
                w.SeparatorText("In your packs");
                packHeader = true;
            }
            uint baseColor = p.Equipped ? WornGreen : PackGrey;
            uint tint = row.Paragon ? ParagonTan : RowTint(p, baseColor);
            w.TextColored(baseColor, p.Equipped ? "Worn" : "Pack");
            w.SameLine(45f);
            w.PushStyleColor(0, tint);   // ImGuiCol_Text: TextWrapped has no colour of its own
            w.TextWrapped(row.Mag);
            w.PopStyleColor();
            if (!p.Appraised) w.Tooltip("Not appraised yet: Check again in a moment for its full line.");
        }
    }

    // ── Colours ──────────────────────────────────────────────────────────────

    private static uint TierColor(ProtTier tier) => tier switch
    {
        ProtTier.Missing => UiColors.Red,
        ProtTier.Minor => TierMinor,
        ProtTier.Major => TierMajor,
        ProtTier.Epic => TierEpic,
        ProtTier.Legendary => TierLegendary,
        _ => TierLower,
    };

    /// <summary>Damage-type colours (UB-ILT's Combat Tracker table) by the protection element's name.</summary>
    private static uint ElementColor(string element) => element switch
    {
        "Slashing" => ElementColorFlags(0x1),
        "Piercing" => ElementColorFlags(0x2),
        "Bludgeon" => ElementColorFlags(0x4),
        "Cold" => ElementColorFlags(0x8),
        "Fire" => ElementColorFlags(0x10),
        "Acid" => ElementColorFlags(0x20),
        "Storm" => ElementColorFlags(0x40),
        _ => UiFormat.Rgb(255, 255, 255),
    };

    /// <summary>The same colours by DamageType flags (Nether wins where it is set).</summary>
    private static uint ElementColorFlags(int flags)
    {
        if ((flags & 0x400) != 0) return UiFormat.Rgb(200, 160, 255);
        return flags switch
        {
            0x1 => UiFormat.Rgb(255, 180, 120),
            0x2 => UiFormat.Rgb(240, 200, 140),
            0x3 => UiFormat.Rgb(255, 160, 100),
            0x4 => UiFormat.Rgb(220, 190, 140),
            0x8 => UiFormat.Rgb(160, 210, 255),
            0x10 => UiFormat.Rgb(255, 130, 70),
            0x20 => UiFormat.Rgb(160, 255, 140),
            0x40 => UiFormat.Rgb(255, 240, 120),
            _ => UiFormat.Rgb(200, 200, 200),
        };
    }
}
