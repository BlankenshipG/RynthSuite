using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthILT.Hub;
using RynthCore.RynthAiTests.Harness;

namespace RynthILT.Tests;

// The Rings inscription chain's pure parts (Hub/IltRingChain.cs, SilentKelpie's RynthAi
// Crafting > Rings moved to RynthILT): ingredient names, the craft queue and the scroll pairing.
internal static class RingChainTests
{
    public static void Register(Runner r)
    {
        r.Add("rings: ingredients match by name, with alternates and an exclude", Ingredients);
        r.Add("rings: the queue runs every Step 1, then Step 2, then the glyphs, capped per recipe", Queue);
        r.Add("rings: scrolls are known by their glyph and paired with targets the UB-IT way", Scrolls);
        r.Add("rings: the recipe list holds the three steps and eight glyphs", RecipeList);
    }

    private static IltRingRecipe Recipe(string id) => IltRingChain.Recipes.Single(r => r.Id == id);

    private static void Ingredients()
    {
        var step1 = Recipe("ring2_step1_quill_trophy");
        Check.True(IltRingChain.Matches("Quill of Infliction", step1.Tool), "the quill");
        Check.True(IltRingChain.Matches("infliction quill", step1.Tool), "an alternate name, any case");
        Check.False(IltRingChain.Matches("Enlightened Quill of Infliction", step1.Tool), "the enlightened quill is Step 2's, not Step 1's");
        Check.True(IltRingChain.Matches("Empyrean Trophy", step1.Target), "a trophy");
        Check.False(IltRingChain.Matches("Trophy Scroll", step1.Target), "plain 'Trophy' isn't enough (scrolls carry the word)");
        Check.False(IltRingChain.Matches(null, step1.Tool), "no name");
        Check.False(IltRingChain.Matches("", step1.Tool), "empty name");

        var step2 = Recipe("ring2_step2_ink");
        Check.True(IltRingChain.Matches("Enlightened Quill of Infliction", step2.Tool), "Step 2 takes Step 1's quill");
        Check.False(IltRingChain.Matches("Enlightened Infliction Quill of Partition", step2.Tool), "but not the partition quill");

        var flame = Recipe("ring2_glyph_flame");
        Check.True(IltRingChain.Matches("Enlightened Infliction Quill of Partition", flame.Tool), "Step 3 takes the partition quill");
        Check.True(IltRingChain.Matches("Glyph of Flame", flame.Target), "the full glyph name");
        Check.False(IltRingChain.Matches("Glyph of Frost", flame.Target), "another glyph is not");
    }

    private static void Queue()
    {
        var runs = new Dictionary<string, int>
        {
            ["ring2_glyph_frost"] = 1,
            ["ring2_step2_ink"] = 2,
            ["ring2_step1_quill_trophy"] = 2,
            ["ring2_glyph_bludgeoning"] = 1,
        };
        List<IltRingRecipe> q = IltRingChain.Expand(id => runs.TryGetValue(id, out int n) ? n : 0);
        Check.Eq(string.Join(",", q.Select(r => r.Id)),
                 "ring2_step1_quill_trophy,ring2_step1_quill_trophy,ring2_step2_ink,ring2_step2_ink,ring2_glyph_bludgeoning,ring2_glyph_frost",
                 "tier order, then the definition order within a tier");
        Check.Eq(IltRingChain.Expand(_ => 0).Count, 0, "nothing queued");
        Check.Eq(IltRingChain.Expand(id => id == "ring2_step2_ink" ? -5 : 0).Count, 0, "negative runs count as none");
        Check.Eq(IltRingChain.Expand(id => id == "ring2_step2_ink" ? 500 : 0).Count, IltRingChain.MaxRuns, "capped at MaxRuns");
    }

    private static void Scrolls()
    {
        Check.True(IltRingChain.IsInscriptionScroll("Inscription of Halo of Frost"), "an inscription scroll");
        Check.False(IltRingChain.IsInscriptionScroll("Scroll of Frost Bolt"), "a spell scroll is not");
        Check.False(IltRingChain.IsInscriptionScroll(null), "no name");
        Check.Eq(IltRingChain.GlyphOf("Inscription of Halo of Frost")?.Id, "ring2_glyph_frost", "known by its scroll name");
        Check.Eq(IltRingChain.GlyphOf("Inscription of Cassius' Ring of Fire")?.Id, "ring2_glyph_flame", "apostrophes too");
        Check.True(IltRingChain.GlyphOf("Inscription of Something New") == null, "an unknown scroll has no glyph");

        var scrolls = new[]
        {
            (Id: 50, Name: "Inscription of Something New"),
            (Id: 40, Name: "Inscription of Halo of Frost"),
            (Id: 30, Name: "Inscription of Tectonic Rifts"),
            (Id: 20, Name: "Inscription of Halo of Frost"),
        };
        var pairs = IltRingChain.PairScrolls(scrolls, new[] { 900, 700, 800, 700 });
        // Scrolls by glyph id (bludgeoning < frost; unknown last), then by id; targets by id, once each.
        Check.Eq(string.Join(" ", pairs.Select(p => p.Scroll + ">" + p.Target)), "30>700 20>800 40>900", "pairing order");
        Check.Eq(IltRingChain.PairScrolls(scrolls.Take(1), new[] { 1, 2, 3 }).Count, 1, "one scroll per target, no more pairs than scrolls");
        Check.Eq(IltRingChain.PairScrolls(scrolls, Array.Empty<int>()).Count, 0, "no targets, no pairs");
    }

    private static void RecipeList()
    {
        Check.Eq(IltRingChain.Recipes.Length, 10, "Step 1, Step 2 and eight glyphs");
        Check.Eq(IltRingChain.Recipes.Count(r => r.Tier == 3), 8, "eight glyphs");
        Check.True(IltRingChain.Recipes.Select(r => r.Id).Distinct().Count() == IltRingChain.Recipes.Length, "ids are unique");
        foreach (var r in IltRingChain.Recipes.Where(r => r.Tier == 3))
            Check.True(r.ScrollMarkers.Length > 0, r.Id + " knows its scroll's name");
    }
}
