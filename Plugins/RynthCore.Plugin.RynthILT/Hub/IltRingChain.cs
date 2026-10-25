// IltRingChain.cs - Infinite Leaftide's ring inscription chain (Gear tab > Rings), from UB-IT's
// ImbueSpellCraftDefinitions / SpellcraftInventoryPolicy. SilentKelpie's RynthAi Crafting > Rings
// (MiniRemote/Crafting/RingChain.cs), moved to RynthILT 2026-10-15: it is Leaftide-only.
//
//   Step 1: Quill of Infliction used on a Trophy        -> Enlightened Quill of Infliction
//   Step 2: Enlightened Quill used on Ink of Partition  -> partition quill
//   Step 3: partition quill used on a Glyph             -> "Inscription of ..." scroll
// Then each scroll is used on a ring / jewelry / weapon.
//
// Pure: ingredient matching (case-insensitive substrings with alternates and one exclude), the
// craft queue order and the scroll-to-target pairing. IltRingChainRunner does the using.
using System;
using System.Collections.Generic;
using System.Linq;

namespace RynthCore.Plugin.RynthILT.Hub;

/// <summary>One ingredient: the first of <see cref="Names"/> found in an item's name, unless it also has <see cref="Exclude"/>.</summary>
internal sealed record IltRingIngredient(string Label, string[] Names, string Exclude = "");

/// <summary>One combine: <see cref="Tool"/> is used on <see cref="Target"/>.</summary>
/// <param name="Tier">1 quill + trophy, 2 + ink, 3 + glyph: the queue runs every tier-1 run first.</param>
/// <param name="ScrollMarkers">Step 3: the words the resulting inscription scroll's name carries.</param>
internal sealed record IltRingRecipe(string Id, int Tier, string Name, string Notes,
    IltRingIngredient Tool, IltRingIngredient Target, string[] ScrollMarkers);

internal static class IltRingChain
{
    /// <summary>Every inscription scroll's name has this.</summary>
    public const string InscriptionMarker = "Inscription of";
    /// <summary>Most runs of one recipe the tab queues.</summary>
    public const int MaxRuns = 99;

    private static readonly IltRingIngredient PartitionQuill = new("Enlightened Infliction Quill of Partition",
        new[] { "Enlightened Infliction Quill of Partition", "Enlightened Quill of Infliction of Partition" });

    private static IltRingRecipe Glyph(string id, string element, string scroll) => new(
        id, 3, $"Step 3: {element} glyph - {scroll}",
        $"Partition quill used on Glyph of {element} -> Inscription of {scroll} scroll.",
        PartitionQuill,
        // The full glyph name: a short one can match the wrong stack.
        new IltRingIngredient($"Glyph of {element}", new[] { $"Glyph of {element}" }),
        new[] { scroll, element });

    /// <summary>The chain, in UB-IT's order.</summary>
    public static readonly IltRingRecipe[] Recipes =
    {
        new("ring2_step1_quill_trophy", 1, "Step 1: Quill of Infliction + Trophy",
            "Quill of Infliction used on a Trophy -> Enlightened Quill of Infliction.",
            new IltRingIngredient("Quill of Infliction", new[] { "Quill of Infliction", "Infliction Quill" }, Exclude: "Enlightened"),
            // Not plain "Trophy": scrolls and kits carry that word too.
            new IltRingIngredient("Trophy", new[] { "Royal Summons", "Empyrean Trophy", "Summoning Gem" }),
            Array.Empty<string>()),
        new("ring2_step2_ink", 2, "Step 2: Enlightened Quill + Ink of Partition",
            "Enlightened Quill of Infliction (from Step 1) used on Ink of Partition -> partition quill.",
            new IltRingIngredient("Enlightened Quill of Infliction",
                new[] { "Enlightened Quill of Infliction", "Enlightened Infliction Quill", "Enlightened Infliction Quill of" }, Exclude: "Partition"),
            new IltRingIngredient("Ink of Partition", new[] { "Ink of Partition", "Partition Ink" }),
            Array.Empty<string>()),
        Glyph("ring2_glyph_bludgeoning", "Bludgeoning", "Tectonic Rifts"),
        Glyph("ring2_glyph_lightning", "Lightning", "Eye of the Storm"),
        Glyph("ring2_glyph_piercing", "Piercing", "Nuhmudira's Spines"),
        Glyph("ring2_glyph_nether", "Nether", "Clouded Soul"),
        Glyph("ring2_glyph_corrosion", "Corrosion", "Searing Disc"),
        Glyph("ring2_glyph_frost", "Frost", "Halo of Frost"),
        Glyph("ring2_glyph_slashing", "Slashing", "Horizon's Blades"),
        Glyph("ring2_glyph_flame", "Flame", "Cassius' Ring of Fire"),
    };

    /// <summary>True when an item's name is this ingredient.</summary>
    public static bool Matches(string? name, IltRingIngredient ing)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (ing.Exclude.Length > 0 && name.IndexOf(ing.Exclude, StringComparison.OrdinalIgnoreCase) >= 0) return false;
        foreach (string n in ing.Names)
            if (n.Length > 0 && name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>
    /// The craft queue: every Step 1 run, then every Step 2 run, then the glyphs, each tier in
    /// definition order (a later tier's ingredients come out of the earlier one).
    /// </summary>
    public static List<IltRingRecipe> Expand(Func<string, int> runs)
    {
        var queue = new List<IltRingRecipe>();
        foreach (var r in Recipes.OrderBy(r => r.Tier))
        {
            int n = Math.Clamp(runs(r.Id), 0, MaxRuns);
            for (int i = 0; i < n; i++) queue.Add(r);
        }
        return queue;
    }

    /// <summary>The glyph recipe a scroll came from (null when its name has no known marker).</summary>
    public static IltRingRecipe? GlyphOf(string? scrollName)
    {
        if (string.IsNullOrEmpty(scrollName)) return null;
        foreach (var r in Recipes)
            foreach (string m in r.ScrollMarkers)
                if (scrollName.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0) return r;
        return null;
    }

    public static bool IsInscriptionScroll(string? name)
        => !string.IsNullOrEmpty(name) && name.IndexOf(InscriptionMarker, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>The order scrolls are listed and used in: by glyph recipe id, unknown ones last by name.</summary>
    public static string ScrollOrderKey(string? scrollName) => GlyphOf(scrollName)?.Id ?? "zzz_" + scrollName;

    /// <summary>
    /// Pairs scrolls with targets the way UB-IT does: scrolls by glyph recipe id (unknown ones
    /// last, by name), targets by object id, one scroll per target.
    /// </summary>
    public static List<(int Scroll, int Target)> PairScrolls(IEnumerable<(int Id, string Name)> scrolls, IEnumerable<int> targets)
    {
        var ordered = scrolls
            .OrderBy(s => ScrollOrderKey(s.Name), StringComparer.Ordinal)
            .ThenBy(s => s.Id)
            .ToList();
        var pairs = new List<(int, int)>();
        int i = 0;
        foreach (int t in targets.Distinct().OrderBy(x => x))
        {
            if (i >= ordered.Count) break;
            pairs.Add((ordered[i++].Id, t));
        }
        return pairs;
    }
}
