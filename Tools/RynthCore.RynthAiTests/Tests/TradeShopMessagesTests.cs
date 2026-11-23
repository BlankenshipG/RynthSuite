using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.Trade;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiTests.Tests;

internal static class TradeShopMessagesTests
{
    public static void Register(Runner runner)
    {
        runner.Add("tradeshop messages: price selector slot vs value", SelectorForms);
        runner.Add("tradeshop messages: resolve by slot name value", ResolveForms);
        runner.Add("tradeshop messages: sheet chunks include rates", SheetChunks);
        runner.Add("tradeshop advertise: channel prefix", AdvertisePrefix);
    }

    private static void SelectorForms()
    {
        var slot = TradeShopTellParse.ParsePriceSelector("#3");
        Check.Eq(slot.slot, 3, "#3 slot");
        Check.Null(slot.valuePy, "#3 not value");

        var bare = TradeShopTellParse.ParsePriceSelector("2");
        Check.Eq(bare.slot, 2, "bare small = slot");

        var val = TradeShopTellParse.ParsePriceSelector("250000");
        Check.Eq(val.valuePy, 250000L, "bare large = value");
        Check.Null(val.slot, "large not slot");

        var tagged = TradeShopTellParse.ParsePriceSelector("value:1000");
        Check.Eq(tagged.valuePy, 1000L, "value: tag");

        Check.True(TradeShopTellParse.IsShopVerb("!rates"), "rates verb");
        Check.True(TradeShopTellParse.IsShopVerb("sheet"), "sheet verb");
        Check.True(TradeShopTellParse.IsShopVerb("hi"), "hi verb");
    }

    private static void ResolveForms()
    {
        var list = new List<TradeShopListing>
        {
            new() { Name = "Alpha", Sku = "A1", PricePyreals = 100, Enabled = true },
            new() { Name = "Sword of Lost Light", Sku = "SOLL", PricePyreals = 250_000, Enabled = true },
            new() { Name = "Beta Key", Sku = "B2", PricePyreals = 500_000, Enabled = true },
        };
        Check.Eq(TradeShopMessages.ResolveListing(list, "#2")!.Name, "Sword of Lost Light", "slot");
        Check.Eq(TradeShopMessages.ResolveListing(list, "soll")!.Sku, "SOLL", "sku partial");
        Check.Eq(TradeShopMessages.ResolveListing(list, "250000")!.Name, "Sword of Lost Light", "value exact");
        Check.Eq(TradeShopMessages.ResolveListing(list, "value:400000")!.Name, "Beta Key", "value at-or-above");
    }

    private static void SheetChunks()
    {
        var list = new List<TradeShopListing>
        {
            new() { Name = "One", PricePyreals = 1000, Enabled = true },
            new() { Name = "Two", PricePyreals = 2000, Enabled = true },
        };
        var chunks = TradeShopMessages.BuildPriceSheetChunks(list, new TradeShopRates(), includeRates: true);
        Check.True(chunks.Count >= 1, "at least one chunk");
        Check.True(chunks[0].Contains("Rates:", System.StringComparison.Ordinal), "rates first chunk");
        string joined = string.Join(" ", chunks);
        Check.True(joined.Contains("#1 One", System.StringComparison.Ordinal), "row 1");
        Check.True(joined.Contains("#2 Two", System.StringComparison.Ordinal), "row 2");
    }

    private static void AdvertisePrefix()
    {
        Check.Eq(TradeShopAdvertise.SendPrefix(TradeShopAdvertiseChannel.Trade), "/ct ", "trade");
        Check.Eq(TradeShopAdvertise.SendPrefix(TradeShopAdvertiseChannel.General), "/cg ", "general");
        Check.Eq(TradeShopAdvertise.ClampIntervalSec(5), TradeShopAdvertise.MinIntervalSec, "clamp low");
        Check.True(TradeShopAdvertise.TryParseChannel("ct", out var ch), "parse ct");
        Check.Eq(ch, TradeShopAdvertiseChannel.Trade, "ct → Trade");
    }
}
