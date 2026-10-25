using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.UbRythai;

/// <summary>
/// Port of UB-ILT <c>UtilityBelt.Tools.Leaftide.TempleGuardianPhrasebook</c> for RynthCore chat parsing.
/// Maps Temple spell-words and Attribute riddles to required commodity names (Leaftide / ILT parity).
/// </summary>
internal static class UbRythaiTempleGuardianPhrasebook
{
    /// <summary>Incoming tell prefix for the Temple guardian (client chat log shape).</summary>
    public const string IncomingTempleTellPrefix = "Guardian of the Temple of Enlightenment tells you";

    /// <summary>Incoming tell prefix for the Attribute guardian (client chat log shape).</summary>
    public const string IncomingAttributeTellPrefix = "Guardian of Attribute Enlightenment tells you";

    private static readonly Dictionary<string, string> TempleSpells = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Boquar", "Mugwort" },
        { "Feazh", "Damiana" },
        { "Kenrak", "Amaranth" },
        { "Puish", "Saffron" },
        { "Shurov", "Comfrey" },
        { "Volae", "Eyebright" },
        { "Cruath", "Ginseng" },
        { "Helkas", "Wormwood" },
        { "Malar", "Hyssop" },
        { "Quavosh", "Dragonsblood" },
        { "Tugak", "Vervain" },
        { "Yanoi", "Bistort" },
        { "Equin", "Mandrake" },
        { "Jevak", "Myrrh" },
        { "Ozhur", "Frankincense" },
        { "Roiga", "Yarrow" },
        { "Uthoi", "Henbane" },
        { "Zojak", "Hawthorn" },
    };

    private static readonly (string needle, string item)[] AttributeClues = BuildAttributeClues();

    private static (string needle, string item)[] BuildAttributeClues()
    {
        var raw = new (string phrase, string item)[]
        {
            ("what stone of shadow wields such arm", "Powdered Onyx"),
            ("what stone defends the mortal clay", "Powdered Agate"),
            ("what heavy stone keeps adventurers healed", "Powdered Hematite"),
            ("what honeyed stone bids pain to leave", "Powdered Amber"),
            ("what draught unweaves that mages send", "Chorizite"),
            ("what gem of life beats deep within", "Powdered Bloodstone"),
            ("what ruddy gem calls vigor nigh", "Powdered Carnelian"),
            ("what lunar gem gives peace to mind", "Powdered Moonstone"),
            ("what verdant gem reveals the way", "Powdered Malachite"),
            ("what crystal tunes the life it soothes", "Powdered Quartz"),
            ("what gem of sky makes items nice", "Powdered Turquoise"),
            ("what vial holds my burning scar", "Brimstone"),
            ("which draught brings forth the fire's scent", "Turpeth"),
            ("race the wind yet leave no trace", "Cadmia"),
            ("grant the hand a dancer grace", "Cadmia"),
            ("no wings I bear yet fleet I flee", "Cadmia"),
            ("what draught awakens speed in thee", "Cadmia"),
            ("what deaught awakens speed in thee", "Cadmia"),
            ("what potion turns mana-use to one", "Cinnabar"),
            ("what azure draught makes thunder weep", "Cobalt"),
            ("what draught commands the winter's tide", "Colcothar"),
            ("what pale draught prevents acid's writhe", "Gypsum"),
            ("what liquid turns heart grey between", "Quicksilver"),
            ("what potion pierces the unseen end", "Realgar"),
            ("what shadowed draught makes swiftness lie", "Stibnite"),
            ("what verdant vial hides cuts unseen", "Verdigris"),
            ("what potion shakes both flesh and floor", "Vitriol"),
            ("what river wood trades life as art", "Alder Talisman"),
            ("what wood enchants both bane and skill", "Ashwood Talisman"),
            ("what pale wood casts the striker's bane", "Birch Talisman"),
            ("what sacred stem keeps harm outside", "Cedar Talisman"),
            ("what dark wood diminishes the foe's own tune", "Blackthorn Talisman"),
            ("what hardwood charm makes others stand", "Ebony Talisman"),
            ("what aged branch makes circles run", "Elder Talisman"),
            ("what mystic bough binds here to yore", "Hazel Talisman"),
            ("what poisoned branch brings creeping death", "Hemlock Talisman"),
            ("what steadfast wood makes volleys rained", "Oak Talisman"),
            ("what living wand makes allies mend", "Poplar Talisman"),
            ("what sacred wood the self amends", "Rowan Talisman"),
            ("what supple wood restores me here", "Willow Talisman"),
            ("what ancient bough lets lifeblood grow", "Yew Talisman"),
        };

        return raw
            .Select(t => (NormalizePhrase(t.phrase), t.item))
            .OrderByDescending(t => t.Item1.Length)
            .ToArray();
    }

    private static string NormalizePhrase(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return "";

        string lower = s.Trim().ToLowerInvariant().Replace('’', '\'');
        var sb = new StringBuilder(lower.Length);
        bool pendingSpace = false;
        foreach (char c in lower)
        {
            bool wordCh = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '\'';
            if (!wordCh)
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace && sb.Length > 0)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString().Trim();
    }

    /// <summary>Resolves pasted guardian dialog to a commodity name, or a short help string.</summary>
    public static string Translate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Paste a spell word or riddle text, then Translate.";

        string blob = NormalizePhrase(raw);
        if (blob.Length == 0)
            return "Paste a spell word or riddle text, then Translate.";

        List<string> tokens = Regex.Split(blob, @"[^a-z0-9']+")
            .Where(t => t.Length > 0)
            .ToList();

        foreach (KeyValuePair<string, string> pair in TempleSpells.OrderByDescending(p => p.Key.Length))
        {
            if (tokens.Any(t => string.Equals(t, pair.Key, StringComparison.OrdinalIgnoreCase)))
                return pair.Value;
        }

        foreach (KeyValuePair<string, string> pair in TempleSpells.OrderByDescending(p => p.Key.Length))
        {
            if (blob.IndexOf(pair.Key.ToLowerInvariant(), StringComparison.Ordinal) >= 0)
                return pair.Value;
        }

        foreach ((string needle, string item) in AttributeClues)
        {
            if (needle.Length > 0 && blob.Contains(needle))
                return item;
        }

        return "No match: paste more lines from the guardian (especially the question line ending with ?).";
    }

    /// <summary>True when <paramref name="raw"/> looks like a full incoming tell line (paste from chat).</summary>
    public static bool LooksLikeIncomingGuardianTellLine(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string t = raw.TrimStart();
        return t.StartsWith(IncomingTempleTellPrefix, StringComparison.OrdinalIgnoreCase)
            || t.StartsWith(IncomingAttributeTellPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// NPC to give the resolved item to after the phrasebook resolves an item (from the incoming tell prefix).
    /// </summary>
    public static string TurnInNpcNameFromTell(string rawTell)
    {
        if (string.IsNullOrEmpty(rawTell))
            return "Guardian of Attribute Enlightenment";
        if (rawTell.IndexOf("Guardian of the Temple of Enlightenment", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Guardian of the Temple of Enlightenment";
        if (rawTell.IndexOf("Guardian of Attribute Enlightenment", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Guardian of Attribute Enlightenment";
        return "Guardian of Attribute Enlightenment";
    }

    /// <summary>Resolves the turn-in NPC for manual Translate + auto hand-in.</summary>
    public static string HandInNpcNameFromInput(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Guardian of Attribute Enlightenment";
        if (LooksLikeIncomingGuardianTellLine(raw))
            return TurnInNpcNameFromTell(raw);
        if (raw.IndexOf("Guardian of the Temple of Enlightenment", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Guardian of the Temple of Enlightenment";
        if (raw.IndexOf("Guardian of Attribute Enlightenment", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Guardian of Attribute Enlightenment";
        foreach (string spellWord in TempleSpells.Keys)
        {
            if (raw.IndexOf(spellWord, StringComparison.OrdinalIgnoreCase) >= 0)
                return "Guardian of the Temple of Enlightenment";
        }

        return "Guardian of Attribute Enlightenment";
    }

    /// <summary>True when chat text likely contains a Temple spell word or attribute riddle.</summary>
    public static bool LooksLikeGuardianChatLine(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string blob = NormalizePhrase(raw);
        if (blob.Contains('?') && blob.Length >= 18)
            return true;

        foreach (string spellWord in TempleSpells.Keys)
        {
            if (raw.IndexOf(spellWord, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        foreach ((string needle, _) in AttributeClues)
        {
            if (needle.Length > 0 && blob.Contains(needle))
                return true;
        }

        return false;
    }
}
