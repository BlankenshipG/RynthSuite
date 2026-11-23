using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.Trade;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiTests.Tests;

/// <summary>UB-IT conversion table math used by the Trade Shop payment check.</summary>
internal static class TradeShopRatesTests
{
    public static void Register(Runner runner)
    {
        runner.Add("tradeshop rates: defaults match UB-IT", DefaultsMatchUbIt);
        runner.Add("tradeshop rates: EC units ceil from pyreals", EcUnitsCeil);
        runner.Add("tradeshop rates: MMD / Lum / EC required amounts", RequiredAmounts);
        runner.Add("tradeshop rates: single-currency payment meets price", SingleCurrencyMeets);
        runner.Add("tradeshop rates: mixed pyreal-equivalent meets price", MixedMeets);
        runner.Add("tradeshop rates: rejected currency does not count", RejectedCurrency);
        runner.Add("tradeshop rates: format alternatives lists accepted", FormatAlternatives);
    }

    private static void DefaultsMatchUbIt()
    {
        var r = new TradeShopRates();
        Check.Eq(r.PyrealPerMmd, 250_000L, "PyrealPerMmd default");
        Check.Eq(r.ConvBuyEcMmd, 4L, "ConvBuyEcMmd");
        Check.Eq(r.ConvBuyEcLum, 1000L, "ConvBuyEcLum");
        Check.Eq(r.ConvBuyEcLk, 2L, "ConvBuyEcLk");
        Check.Eq(r.ConvBuyEcMk, 1L, "ConvBuyEcMk");
        Check.Eq(r.ConvBuyEcWec, 10L, "ConvBuyEcWec");
        Check.Eq(r.PyrealsPerEcBuy(), 1_000_000L, "1 EC = 4 MMD * 250k Py");
    }

    private static void EcUnitsCeil()
    {
        var r = new TradeShopRates();
        Check.Eq(r.EcUnitsForPyreals(500_001), 1L, "half EC rounds up to 1");
        Check.Eq(r.EcUnitsForPyreals(1_000_000), 1L, "exact 1 EC");
        Check.Eq(r.EcUnitsForPyreals(1_000_001), 2L, "just over 1 EC → 2");
        Check.Eq(r.EcUnitsForPyreals(0), 0L, "zero price");
    }

    private static void RequiredAmounts()
    {
        var r = new TradeShopRates();
        long basePy = 2_000_000; // 2 EC
        Check.Eq(r.RequiredAmount(basePy, TradeShopCurrency.Pyreals), 2_000_000L, "Py face");
        Check.Eq(r.RequiredAmount(basePy, TradeShopCurrency.EnlightenedCoins), 2L, "2 EC");
        Check.Eq(r.RequiredAmount(basePy, TradeShopCurrency.Mmd), 8L, "2 EC * 4 MMD");
        Check.Eq(r.RequiredAmount(basePy, TradeShopCurrency.Luminance), 2000L, "2 EC * 1000 Lum");
        Check.Eq(r.RequiredAmount(basePy, TradeShopCurrency.LegendaryKeys), 4L, "2 EC * 2 LK");
        Check.Eq(r.RequiredAmount(basePy, TradeShopCurrency.WeaklyEnlightenedCoins), 20L, "2 EC * 10 WEC");
    }

    private static void SingleCurrencyMeets()
    {
        var r = new TradeShopRates();
        long price = 1_000_000;
        Check.True(r.PaymentMeetsPrice(price, new Dictionary<TradeShopCurrency, long>
        {
            [TradeShopCurrency.EnlightenedCoins] = 1,
        }), "1 EC covers 1M py");

        Check.True(r.PaymentMeetsPrice(price, new Dictionary<TradeShopCurrency, long>
        {
            [TradeShopCurrency.Mmd] = 4,
        }), "4 MMD covers 1M py");

        Check.False(r.PaymentMeetsPrice(price, new Dictionary<TradeShopCurrency, long>
        {
            [TradeShopCurrency.Mmd] = 3,
        }), "3 MMD does not cover 1M py");
    }

    private static void MixedMeets()
    {
        var r = new TradeShopRates();
        Check.True(r.PaymentMeetsPrice(1_000_000, new Dictionary<TradeShopCurrency, long>
        {
            [TradeShopCurrency.Mmd] = 2,
            [TradeShopCurrency.Pyreals] = 500_000,
        }), "mixed MMD+Py covers");
    }

    private static void RejectedCurrency()
    {
        var r = new TradeShopRates { AcceptEnlightenedCoins = false };
        Check.False(r.PaymentMeetsPrice(1, new Dictionary<TradeShopCurrency, long>
        {
            [TradeShopCurrency.EnlightenedCoins] = 100,
        }), "EC rejected when AcceptEnlightenedCoins is false");
    }

    private static void FormatAlternatives()
    {
        var r = new TradeShopRates();
        string alt = r.FormatAlternatives(1_000_000);
        Check.True(alt.Contains("Py"), "includes Py: " + alt);
        Check.True(alt.Contains("EC"), "includes EC: " + alt);
        Check.True(alt.Contains("MMD"), "includes MMD: " + alt);
    }
}
