using RynthCore.Plugin.RynthAi.IltHub;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Enlightenment 301-325 sigil craft chain (2026-10-07). Each target level T costs T - 5 sigils and
// T x 2B luminance (ACECustom tier 5). Sigil = Lifeblood Elixir + Ascension Coin; Ascension Coin =
// E-coin + Ancient Yalani Trinket; Elixir = Lifebloom Resin + Necrolytic Extract; Resin = Life
// Splitter + Plant. Stock is used from the top of the chain down.
internal static class SigilChainTests
{
    public static void Register(Runner r)
    {
        r.Add("sigil chain: 301-325 tier rates and sigil counts", TierRates);
        r.Add("sigil chain: shortfall uses stock top-down", ShortfallTopDown);
        r.Add("sigil chain: fulfillable count and shared e-coin pool", FulfillableAndCoins);
        r.Add("sigil chain: item names (plural stacks, Plant exact)", Names);
    }

    private static void TierRates()
    {
        var t301 = IltProgression.EnlStepCost(301);
        Check.Eq(t301.Item, "Enlightenment Sigils", "301 takes sigils");
        Check.Eq(t301.Count, 296, "301 takes T - 5 sigils");
        Check.Eq(t301.Lum, 301 * 2_000_000_000m, "301 costs T x 2B luminance (no step multiplier yet)");
        Check.Eq(IltProgression.EnlStepCost(325).Lum, 325 * 2_000_000_000m, "325 still has no step multiplier");
        Check.Eq(IltProgression.EnlStepCost(326).Item, "Enlightenment Crests", "326 moves to crests");
        Check.Eq(IltSigilChain.SigilsForRange(300, 302), 296L + 297L, "range 300 -> 302 sums both levels");
        Check.Eq(IltSigilChain.SigilsForRange(324, 330), 320L, "range clamps at 325");
        Check.Eq(IltSigilChain.SigilsForRange(200, 300), 0L, "no sigils below 301");
    }

    private static void ShortfallTopDown()
    {
        var empty = IltSigilChain.Shortfall(10, new IltSigilStock());
        Check.Eq(empty.CoinsShort, 10L, "empty pack: one e-coin per sigil");
        Check.Eq(empty.PlantsShort, 10L, "empty pack: one plant per sigil");
        Check.True(!empty.Covered, "empty pack is not covered");

        var stock = new IltSigilStock
        {
            Sigils = 2, AscensionCoins = 3, LifebloodElixirs = 1, LifebloomResins = 4,
            EnlightenedCoins = 100, YalaniTrinkets = 1, NecrolyticExtracts = 2, LifeSplitters = 0, Plants = 5,
        };
        var s = IltSigilChain.Shortfall(10, stock);
        Check.Eq(s.SigilsToCraft, 8L, "2 finished sigils used first");
        Check.Eq(s.AscensionToCraft, 5L, "3 Ascension Coins on hand");
        Check.Eq(s.ElixirsToCraft, 7L, "1 Elixir on hand");
        Check.Eq(s.CoinsShort, 0L, "100 e-coins cover 5 crafts");
        Check.Eq(s.TrinketsShort, 4L, "1 trinket on hand");
        Check.Eq(s.ExtractsShort, 5L, "2 extracts on hand");
        Check.Eq(s.ResinsToCraft, 3L, "4 resins on hand");
        Check.Eq(s.SplittersShort, 3L, "no splitters");
        Check.Eq(s.PlantsShort, 0L, "5 plants cover 3 resins");

        Check.True(IltSigilChain.Shortfall(5, new IltSigilStock { Sigils = 9 }).Covered, "finished sigils cover the demand");
    }

    private static void FulfillableAndCoins()
    {
        var stock = new IltSigilStock
        {
            Sigils = 1, AscensionCoins = 1, EnlightenedCoins = 10, YalaniTrinkets = 2,
            LifebloomResins = 1, LifeSplitters = 10, Plants = 10, NecrolyticExtracts = 4,
        };
        Check.Eq(IltSigilChain.Fulfillable(stock), 4L, "1 finished + min(3 coins, 4 elixirs)");

        IltSigilChain.EcoinDemand(50, 30, 2, 20, out long fromCoins, out long still, out long need);
        Check.Eq(fromCoins, 1L, "20 coins left after Ascension crafts make 1 token");
        Check.Eq(still, 1L, "one token still short");
        Check.Eq(need, 20L, "one more token's worth of coins");
        IltSigilChain.EcoinDemand(10, 25, 0, 20, out _, out _, out need);
        Check.Eq(need, 15L, "Ascension crafts alone exceed the pool");
    }

    private static void Names()
    {
        Check.True(IltSigilChain.NameIs("Ascension Coins", IltSigilChain.AscensionCoin), "plural stack name");
        Check.True(IltSigilChain.NameIs(" plant ", IltSigilChain.Plant), "case and spaces ignored");
        Check.True(!IltSigilChain.NameIs("Plant Fiber", IltSigilChain.Plant), "Plant must be the whole name");
        Check.True(!IltSigilChain.NameIs("Lifebloom Resin", IltSigilChain.LifebloodElixir), "Lifebloom is not Lifeblood");
    }
}
