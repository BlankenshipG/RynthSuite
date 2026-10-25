using System;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthChat;

// ChatMessageType values from ACE.Entity.Enum.ChatMessageType (ACE binary is
// byte-identical to retail, so these are authoritative for retail acclient.exe too).
internal static class ChatClassifier
{
    internal const string Chat     = "Chat";      // Local/Tell/Allegiance/Fellow — all player-to-player
    internal const string Channels = "Channels";  // General/Trade/LFG/Roleplay/Society/Admin — all 0x08/0x09
    internal const string System   = "System";
    internal const string Combat   = "Combat";
    internal const string Rynth    = "Rynth";     // Any RynthCore/RynthAi/RynthChat plugin output ("[Rynth…]")
    internal const string Items    = "Items";     // RynthAi's item info ("[RynthAi Item] ", on select or /ra iteminfo)
    internal const string Other    = "Other";

    internal static readonly string[] AllChannels =
        { Chat, Channels, System, Combat, Rynth, Items, Other };

    /// <summary>
    /// True for output emitted by the Rynth stack itself — every Rynth plugin tags
    /// its chat lines with a "[Rynth…]" prefix (e.g. "[RynthAi]", "[RynthChat]",
    /// "[RynthAi Craft]"). These are routed to the dedicated Rynth tab regardless of
    /// the chatType the plugin happened to write them with, so nothing gets scattered
    /// across System/Other/Chat and lost.
    /// </summary>
    internal static bool IsRynthOutput(string text) =>
        text.StartsWith("[Rynth", StringComparison.OrdinalIgnoreCase);

    /// <summary>RynthAi's item info (RynthAiPlugin.ItemPrefix): its own Items tab (2026-10-09).</summary>
    internal static bool IsItemInfo(string text) =>
        text.StartsWith("[RynthAi Item] ", StringComparison.Ordinal);

    // ── Automated system text (0.3.4) ──────────────────────────────────────
    // Lines the engine shows in the System tab only, not under All: replies to the
    // commands plugins send by themselves (/myquests, /qb list), the server's login
    // burst, and anything a plugin ate (the engine reports those, RynthChatNoteEaten).

    /// <summary>AdminTell: a person typed it, so it is never automated text.</summary>
    internal const int AdminTellType = 0x1F;

    // A /myquests flag line: "key - N solves (unixtime)"description" max repeat". Its own timeout:
    // static fields initialise in file order, and the shared Timeout below isn't set yet here.
    private static readonly Regex _questFlagRe = new(@"^\S+ - \d+ solves \(\d*\)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20));

    /// <summary>
    /// True for the fixed formats of automated replies, whoever sent the command:
    /// /myquests flag lines and their end lines, the /qb list headers and end lines,
    /// and ACE's login welcome. Only for system-type lines, so player speech that
    /// quotes them is untouched.
    /// </summary>
    internal static bool IsAutomatedText(string text, int chatType)
    {
        if (string.IsNullOrEmpty(text) || ChannelForType(chatType) != System || chatType == AdminTellType) return false;
        if (text.IndexOf(" solves (", StringComparison.Ordinal) > 0)
        {
            try { if (_questFlagRe.IsMatch(text)) return true; }
            catch (RegexMatchTimeoutException) { }
        }
        return text.StartsWith("Quest list is empty", StringComparison.Ordinal)
            || text.StartsWith("[MyQuests] ", StringComparison.Ordinal)
            || text.StartsWith("Your completed quest bonus list", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Your incomplete quest bonus list", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Your current quest bonus count", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Quest bonus list is empty", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[QB] ", StringComparison.Ordinal)
            || text.StartsWith("Welcome to Asheron's Call", StringComparison.Ordinal);
    }

    /// <summary>
    /// True when a line counts as the login burst: system text (not an admin's tell) that
    /// arrives while the character is entering the world (<paramref name="loginBurst"/>).
    /// </summary>
    internal static bool IsLoginBurst(string channel, int chatType, bool loginBurst) =>
        loginBurst && channel == System && chatType != AdminTellType;

    // ── Channel classification ─────────────────────────────────────────────

    /// <summary>
    /// Channel tab for a line. Prefer the text's [General]/[Trade]/… tag when present —
    /// ILT (and some ACE builds) deliver those as type 0x1C / 28 instead of 0x08/0x09, which
    /// used to dump Trade/General into Other and leave the Channels tab empty.
    /// </summary>
    internal static string ChannelFor(string? text, int chatType)
    {
        if (IsChannelTagged(text)) return Channels;
        return ChannelForType(chatType);
    }

    /// <summary>Legacy type-only map (tests / callers without the line text).</summary>
    internal static string ChannelFor(int chatType) => ChannelForType(chatType);

    internal static string ChannelForType(int chatType) => (uint)chatType switch
    {
        0x02 or 0x03 or 0x04 or 0x0A or 0x0B
            or 0x0C or 0x12 or 0x13                     => Chat,
        0x08 or 0x09 or 0x1C                            => Channels,
        0x00 or 0x05 or 0x0D or 0x14 or 0x17 or 0x18
            or 0x19 or 0x1F                             => System,
        0x06 or 0x07 or 0x11 or 0x15 or 0x16           => Combat,
        _                                               => Other,
    };

    /// <summary>True when the line opens with a retail/ILT channel tag ([Trade], [General], …).</summary>
    internal static bool IsChannelTagged(string? text)
    {
        if (string.IsNullOrEmpty(text) || text[0] != '[') return false;
        return text.StartsWith("[General]", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[Trade]", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[LFG]", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[Roleplay]", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[Society]", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[Celestial Hand]", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[Eldrytch Web]", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[Radiant Blood]", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[Admin]", StringComparison.OrdinalIgnoreCase);
    }

    internal static uint ColorFor(string channel) => channel switch
    {
        Chat     => 0xFFE0E0E0,
        Channels => 0xFF7AB8F5,
        System   => 0xFF8CA6BF,
        Combat   => 0xFFD93333,
        _        => 0xFFAAAAAA,
    };

    // ── Sender extraction ──────────────────────────────────────────────────
    // Bob says, "..."  /  Bob tells you, "..."  /  [General] Bob says, "..."
    // [Allegiance] Bob says, "..."  /  Bob says on the Fellowship channel, "..."
    // Before 0.2.0 the channel-tag forms captured "[General] Bob" as the sender.
    // The sender is metadata (mentions, rules); the engine no longer prints it
    // in front of the line, which already names the speaker.

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(20);
    private static readonly Regex _incomingRe = new(
        @"^(?:\[[^\]]{1,40}\] )?(.+?) (?:says|tells you)\b[^""]{0,60}?, """, RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex _tellOutRe  = new(@"^You tell (.+?), """, RegexOptions.CultureInvariant, Timeout);

    internal static string? SenderFor(string text, int chatType) => (uint)chatType switch
    {
        0x02 or 0x03 or 0x08 or 0x0A or 0x12 or 0x13 => Match(_incomingRe, text),
        0x04 => TellOutSender(text),
        _    => null,
    };

    private static string? Match(Regex re, string text)
    {
        try
        {
            var m = re.Match(text);
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    private static string? TellOutSender(string text)
    {
        string? name = Match(_tellOutRe, text);
        return name != null ? $"→{name}" : null;
    }
}
