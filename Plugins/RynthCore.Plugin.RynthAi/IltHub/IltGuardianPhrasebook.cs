// IltGuardianPhrasebook.cs - Temple of Enlightenment spell words and Guardian of Attribute
// Enlightenment riddles mapped to the item each guardian wants (Infinite Leaftide).
//
// Pure functions only (no host calls), so the Guardian window may translate on the render thread.
// The riddle table matches on the closing question line; lead-in lines alone return "No match".
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthAi.IltHub;

internal static class IltGuardianPhrasebook
{
    public const string TempleGuardian = "Guardian of the Temple of Enlightenment";
    public const string AttributeGuardian = "Guardian of Attribute Enlightenment";

    /// <summary>Chat prefixes of an incoming guardian line ("tells you" or ACE's environmental "says").</summary>
    private static readonly string[] IncomingPrefixes =
    {
        TempleGuardian + " tells you",
        AttributeGuardian + " tells you",
        TempleGuardian + " says",
        AttributeGuardian + " says",
    };

    /// <summary>Help text returned when nothing was pasted.</summary>
    public const string PasteHint = "Paste a spell word or riddle text, then Translate.";

    /// <summary>Help text returned when the text didn't match any spell word or riddle.</summary>
    public const string NoMatchHint = "No match: paste more lines from the guardian (especially the question line ending with ?).";

    private static readonly Dictionary<string, string> TempleSpells = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Boquar", "Mugwort" }, { "Feazh", "Damiana" }, { "Kenrak", "Amaranth" },
        { "Puish", "Saffron" }, { "Shurov", "Comfrey" }, { "Volae", "Eyebright" },
        { "Cruath", "Ginseng" }, { "Helkas", "Wormwood" }, { "Malar", "Hyssop" },
        { "Quavosh", "Dragonsblood" }, { "Tugak", "Vervain" }, { "Yanoi", "Bistort" },
        { "Equin", "Mandrake" }, { "Jevak", "Myrrh" }, { "Ozhur", "Frankincense" },
        { "Roiga", "Yarrow" }, { "Uthoi", "Henbane" }, { "Zojak", "Hawthorn" },
    };

    /// <summary>Spell words longest first so a short word never shadows a longer one.</summary>
    private static readonly KeyValuePair<string, string>[] TempleSpellsByLength =
        TempleSpells.OrderByDescending(p => p.Key.Length).ToArray();

    /// <summary>Normalized riddle phrases (longest first) and the item each answers.</summary>
    private static readonly (string Needle, string Item)[] AttributeClues = BuildAttributeClues();

    /// <summary>The same riddles split into meaningful words for the fuzzy fallback.</summary>
    private static readonly (string Item, string[] Words)[] AttributeClueWords =
        AttributeClues.Select(c => (c.Item, TokenizeMeaningfulWords(c.Needle))).ToArray();

    private static (string, string)[] BuildAttributeClues()
    {
        var raw = new (string Phrase, string Item)[]
        {
            ("what stone of shadow wields such arm", "Powdered Onyx"),
            ("what stone defends the mortal clay", "Powdered Agate"),
            ("what heavy stone keeps adventurers healed", "Powdered Hematite"),
            ("what heavy stone keeps adventure's healed", "Powdered Hematite"),
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
            ("whate pale draught prevents acid's writhe", "Gypsum"),
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
        return raw.Select(t => (NormalizePhrase(t.Phrase), t.Item))
                  .OrderByDescending(t => t.Item1.Length)
                  .ToArray();
    }

    /// <summary>Lowercase, curly apostrophes folded, punctuation collapsed to single spaces.</summary>
    private static string NormalizePhrase(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        string lower = s.Trim().ToLowerInvariant().Replace('\u2019', '\'');
        var sb = new StringBuilder(lower.Length);
        bool pendingSpace = false;
        foreach (char c in lower)
        {
            bool wordCh = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '\'';
            if (!wordCh) { pendingSpace = true; continue; }
            if (pendingSpace && sb.Length > 0) { sb.Append(' '); pendingSpace = false; }
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Words of 3+ letters minus question / filler words.</summary>
    private static string[] TokenizeMeaningfulWords(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                  .Where(w => w.Length >= 3 && w is not ("what" or "which" or "the" or "and" or "for"))
                  .ToArray();

    /// <summary>
    /// Typo-tolerant fallback: the riddle whose words overlap the input best, needing at least four
    /// shared words and 72% of the riddle's words so a near-miss never maps to the wrong item.
    /// </summary>
    private static string? TryFuzzyAttributeMatch(string blob)
    {
        var inputWords = TokenizeMeaningfulWords(blob);
        if (inputWords.Length == 0) return null;
        var inputSet = new HashSet<string>(inputWords, StringComparer.Ordinal);
        string? bestItem = null;
        double bestScore = 0d;
        foreach (var (item, words) in AttributeClueWords)
        {
            if (words.Length == 0) continue;
            int matches = words.Count(inputSet.Contains);
            double score = (double)matches / words.Length;
            if (matches >= 4 && score >= 0.72d && score > bestScore)
            {
                bestScore = score;
                bestItem = item;
            }
        }
        return bestItem;
    }

    /// <summary>Resolves guardian text to an item name, or one of the help strings.</summary>
    public static string Translate(string raw)
    {
        string blob = NormalizePhrase(raw);
        if (blob.Length == 0) return PasteHint;

        var tokens = Regex.Split(blob, @"[^a-z0-9']+").Where(t => t.Length > 0).ToList();
        foreach (var pair in TempleSpellsByLength)
            if (tokens.Any(t => t.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)))
                return pair.Value;
        foreach (var pair in TempleSpellsByLength)
            if (blob.Contains(pair.Key.ToLowerInvariant(), StringComparison.Ordinal))
                return pair.Value;
        foreach (var (needle, item) in AttributeClues)
            if (needle.Length > 0 && blob.Contains(needle, StringComparison.Ordinal))
                return item;
        return TryFuzzyAttributeMatch(blob) ?? NoMatchHint;
    }

    /// <summary>False for the help strings Translate returns when nothing matched.</summary>
    public static bool IsAnswer(string translation) =>
        !string.IsNullOrWhiteSpace(translation) && translation != PasteHint && translation != NoMatchHint;

    /// <summary>True when the line starts like an incoming guardian tell / say.</summary>
    public static bool LooksLikeIncomingGuardianLine(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string t = raw.TrimStart();
        foreach (string prefix in IncomingPrefixes)
            if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>The guardian to give the item to, read from an incoming line (Attribute when unclear).</summary>
    public static string TurnInNpcFromLine(string raw)
    {
        if (!string.IsNullOrEmpty(raw) && raw.Contains(TempleGuardian, StringComparison.OrdinalIgnoreCase))
            return TempleGuardian;
        return AttributeGuardian;
    }

    /// <summary>
    /// The guardian for manually pasted text: the name in a pasted chat line, else a Temple spell
    /// word means the Temple guardian, else the Attribute guardian.
    /// </summary>
    public static string TurnInNpcFromInput(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return AttributeGuardian;
        if (raw.Contains(TempleGuardian, StringComparison.OrdinalIgnoreCase)) return TempleGuardian;
        if (raw.Contains(AttributeGuardian, StringComparison.OrdinalIgnoreCase)) return AttributeGuardian;
        foreach (string word in TempleSpells.Keys)
            if (raw.Contains(word, StringComparison.OrdinalIgnoreCase))
                return TempleGuardian;
        return AttributeGuardian;
    }

    /// <summary>The quoted message of a guardian line ("... tells you, \"text\"" → text); the line itself otherwise.</summary>
    public static string MessageOf(string line)
    {
        int open = line.IndexOf('"');
        int close = line.LastIndexOf('"');
        return open >= 0 && close > open ? line.Substring(open + 1, close - open - 1).Trim() : line.Trim();
    }
}
