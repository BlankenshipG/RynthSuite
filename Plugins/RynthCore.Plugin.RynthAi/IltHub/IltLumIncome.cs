// IltLumIncome.cs — Session luminance income for IltSessionRates (2026-10-09).
//
// Primary source: the bank's luminance (PropertyInt64.BankedLuminance = 9005), which ACECustom
// pushes to the client with every XP batch. Positive deltas between samples are income; drops
// (aug / enlightenment spending, transfers out) are ignored so the next gain still counts.
//
// Fallback: "You've banked N Luminance" chat lines. The server only prints that line for the
// killer's own Kill / Quest luminance — fellowship and Zone Share splits (XpType.Fellowship) bank
// silently — so chat alone under-counts badly. Chat is counted only until the bank property is
// first seen; after that the property covers every source and chat would double-count.
namespace RynthCore.Plugin.RynthAi.IltHub;

internal sealed class IltLumIncome
{
    private long _lastBank = -1;   // last bank sample; -1 = property not seen yet
    private long _fromBank;        // positive bank deltas since the baseline
    private long _chatBeforeBank;  // chat amounts seen before the property showed up

    /// <summary>True once a bank-luminance sample has set the baseline.</summary>
    public bool BankSeen => _lastBank >= 0;

    /// <summary>Luminance earned this session.</summary>
    public long Total => _fromBank + _chatBeforeBank;

    /// <summary>
    /// One bank-luminance read. 0 means "not received" (the engine returns 0 for a missing
    /// property), so it never sets the baseline; the first non-zero read only sets it.
    /// </summary>
    public void OnBankSample(long banked)
    {
        if (banked <= 0) return;
        if (_lastBank < 0) { _lastBank = banked; return; }
        long delta = banked - _lastBank;
        _lastBank = banked;
        if (delta > 0) _fromBank += delta;
    }

    /// <summary>A "You've banked N Luminance" amount. Ignored once the bank property is tracked.</summary>
    public void OnChatAmount(long amount)
    {
        if (amount > 0 && !BankSeen) _chatBeforeBank += amount;
    }

    public void Reset()
    {
        _lastBank = -1;
        _fromBank = 0;
        _chatBeforeBank = 0;
    }
}
