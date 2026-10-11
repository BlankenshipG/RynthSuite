using RynthCore.Plugin.RynthAi.IltHub;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Mini Remote Lum/h (2026-10-09). Lum/h read 0 while the bank climbed because the rate came only
// from "You've banked N Luminance" chat, which ACECustom prints for the killer's own Kill / Quest
// lum but not for fellowship / Zone Share shares. Bank luminance (PropertyInt64 9005) is now the
// primary source; chat only fills in until that property is seen.
internal static class LumIncomeTests
{
    public static void Register(Runner r)
    {
        r.Add("lum income: bank deltas count, drops ignored", BankDeltas);
        r.Add("lum income: chat counts only before the bank property", ChatFallback);
        r.Add("lum income: banked-luminance chat regex", ChatRegex);
    }

    private static void BankDeltas()
    {
        var lum = new IltLumIncome();
        lum.OnBankSample(0);
        Check.True(!lum.BankSeen, "0 (property missing) does not set the baseline");
        lum.OnBankSample(3_967_200_000_000);
        Check.Eq(lum.Total, 0L, "first sample is only the baseline");
        lum.OnBankSample(3_972_243_048_888);
        Check.Eq(lum.Total, 5_043_048_888L, "gain since the baseline counts");
        lum.OnBankSample(3_900_000_000_000);
        Check.Eq(lum.Total, 5_043_048_888L, "spending is not income");
        lum.OnBankSample(3_900_000_001_000);
        Check.Eq(lum.Total, 5_043_049_888L, "gain after a spend counts from the new low");
        lum.Reset();
        Check.Eq(lum.Total, 0L, "reset clears the total");
        Check.True(!lum.BankSeen, "reset clears the baseline");
    }

    private static void ChatFallback()
    {
        var lum = new IltLumIncome();
        lum.OnChatAmount(1_000);
        Check.Eq(lum.Total, 1_000L, "chat counts while the property is unseen");
        lum.OnBankSample(50_000);
        lum.OnChatAmount(2_000);
        lum.OnBankSample(52_000);
        Check.Eq(lum.Total, 3_000L, "after the property is seen, the bank delta counts and the chat line does not");
    }

    private static void ChatRegex()
    {
        var m = IltParse.LumBanked.Match("You've banked 1,234,567 Luminance.");
        Check.True(m.Success, "server line matches");
        Check.Eq(IltParse.ParseLeadingLong(m.Groups["amt"].Value), 1_234_567L, "amount parses with commas");
    }
}
