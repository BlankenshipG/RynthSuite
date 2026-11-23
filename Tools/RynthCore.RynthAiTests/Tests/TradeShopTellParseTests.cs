using RynthCore.Plugin.RynthAi.Trade;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiTests.Tests;

internal static class TradeShopTellParseTests
{
    public static void Register(Runner runner)
    {
        runner.Add("tradeshop tell: plain Name tells you", PlainTell);
        runner.Add("tradeshop tell: IIDString link", LinkedTell);
        runner.Add("tradeshop buy selector: id forms", BuyIds);
        runner.Add("tradeshop buy selector: name forms", BuyNames);
    }

    private static void PlainTell()
    {
        Check.True(TradeShopTellParse.TryParseTell("Bob tells you, \"!list\"", out string who, out string msg), "parse");
        Check.Eq(who, "Bob", "sender");
        Check.Eq(msg, "!list", "body");
    }

    private static void LinkedTell()
    {
        Check.True(TradeShopTellParse.TryParseTell(
            "<Tell:IIDString:1342177281:Alice>Alice<\\Tell> tells you, \"!buy 2\"",
            out string who, out string msg), "parse link");
        Check.Eq(who, "Alice", "link name");
        Check.Eq(msg, "!buy 2", "body");
    }

    private static void BuyIds()
    {
        Check.Eq(TradeShopTellParse.ParseBuySelector("3").id, 3, "bare id");
        Check.Eq(TradeShopTellParse.ParseBuySelector("#4").id, 4, "hash id");
        Check.Eq(TradeShopTellParse.ParseBuySelector("id:5").id, 5, "id: prefix");
        Check.Eq(TradeShopTellParse.ParseBuySelector("id:5").text, "", "id has no text");
    }

    private static void BuyNames()
    {
        var q = TradeShopTellParse.ParseBuySelector("\"Sword of Lost Light\"");
        Check.Null(q.id, "quoted is name");
        Check.Eq(q.text, "Sword of Lost Light", "quoted text");
        var p = TradeShopTellParse.ParseBuySelector("Singularity Key");
        Check.Null(p.id, "words are name");
        Check.Eq(p.text, "Singularity Key", "partial name");
    }
}
