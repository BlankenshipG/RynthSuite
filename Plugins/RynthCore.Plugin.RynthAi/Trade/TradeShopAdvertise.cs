// Auto-advertise helpers: which AC chat window and how often (pure; unit-testable).
using System;
using RynthCore.Plugin.RynthAi.Translate;

namespace RynthCore.Plugin.RynthAi.Trade;

/// <summary>Chat windows Trade Shop may auto-advertise into.</summary>
public enum TradeShopAdvertiseChannel
{
    Trade = 0,
    General = 1,
    Lfg = 2,
    Fellowship = 3,
    Allegiance = 4,
    Local = 5,
    Roleplay = 6,
    Society = 7,
}

/// <summary>Maps advertise channel → slash prefix / display name (no host deps).</summary>
public static class TradeShopAdvertise
{
    public const int MinIntervalSec = 30;
    public const int MaxIntervalSec = 3600;
    public const int DefaultIntervalSec = 180;

    public static string DisplayName(TradeShopAdvertiseChannel ch) => ch switch
    {
        TradeShopAdvertiseChannel.Trade => "Trade (/ct)",
        TradeShopAdvertiseChannel.General => "General (/cg)",
        TradeShopAdvertiseChannel.Lfg => "LFG (/clfg)",
        TradeShopAdvertiseChannel.Fellowship => "Fellowship (/f)",
        TradeShopAdvertiseChannel.Allegiance => "Allegiance (/a)",
        TradeShopAdvertiseChannel.Local => "Local (say)",
        TradeShopAdvertiseChannel.Roleplay => "Roleplay (/crp)",
        TradeShopAdvertiseChannel.Society => "Society (/cs)",
        _ => ch.ToString(),
    };

    /// <summary>Slash prefix for InvokeChatParser (empty = local say).</summary>
    public static string SendPrefix(TradeShopAdvertiseChannel ch)
    {
        TranslateChannel tc = ch switch
        {
            TradeShopAdvertiseChannel.Trade => TranslateChannel.Trade,
            TradeShopAdvertiseChannel.General => TranslateChannel.General,
            TradeShopAdvertiseChannel.Lfg => TranslateChannel.Lfg,
            TradeShopAdvertiseChannel.Fellowship => TranslateChannel.Fellowship,
            TradeShopAdvertiseChannel.Allegiance => TranslateChannel.Allegiance,
            TradeShopAdvertiseChannel.Local => TranslateChannel.Local,
            TradeShopAdvertiseChannel.Roleplay => TranslateChannel.Roleplay,
            TradeShopAdvertiseChannel.Society => TranslateChannel.Society,
            _ => TranslateChannel.Trade,
        };
        return TranslateChannels.SendPrefix(tc, null);
    }

    public static int ClampIntervalSec(int sec)
    {
        if (sec < MinIntervalSec) return MinIntervalSec;
        if (sec > MaxIntervalSec) return MaxIntervalSec;
        return sec;
    }

    public static bool TryParseChannel(string? text, out TradeShopAdvertiseChannel ch)
    {
        ch = TradeShopAdvertiseChannel.Trade;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (Enum.TryParse(text.Trim(), ignoreCase: true, out TradeShopAdvertiseChannel parsed))
        {
            ch = parsed;
            return true;
        }
        if (TranslateChannels.TryParse(text, out TranslateChannel tc))
        {
            ch = tc switch
            {
                TranslateChannel.Trade => TradeShopAdvertiseChannel.Trade,
                TranslateChannel.General => TradeShopAdvertiseChannel.General,
                TranslateChannel.Lfg => TradeShopAdvertiseChannel.Lfg,
                TranslateChannel.Fellowship => TradeShopAdvertiseChannel.Fellowship,
                TranslateChannel.Allegiance => TradeShopAdvertiseChannel.Allegiance,
                TranslateChannel.Local => TradeShopAdvertiseChannel.Local,
                TranslateChannel.Roleplay => TradeShopAdvertiseChannel.Roleplay,
                TranslateChannel.Society => TradeShopAdvertiseChannel.Society,
                _ => TradeShopAdvertiseChannel.Trade,
            };
            return true;
        }
        return false;
    }
}
