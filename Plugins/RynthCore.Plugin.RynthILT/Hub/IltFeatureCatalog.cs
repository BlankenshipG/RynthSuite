// IltFeatureCatalog.cs — Every Infinite Leaftide feature RynthILT supports, in one list.
//
// Each entry says what the feature is, where it lives in the Rynth tools, and how its state is
// known. Two kinds:
//   * Probed: the server's own answer (an IltServerOptions bit: /bank, /aug, /xp, ...). On, off,
//     or not known until the login check has run.
//   * Built in: part of ACECustom itself with no command to ask (the Temple guardians, Paragon
//     gear, split arrows, T11 items, the 29-member fellowship). On whenever the world is an ILT
//     world, unknown anywhere else.
// The list feeds the Mini Remote's Character tab ("Leaftide features"), "/ra hub features",
// and the "RynthILT.Server" profile JSON other plugins read (Plugins/Shared/RynthIltServerApi.cs:
// RynthOracle's Fellowship tab takes the fellowship rules from it, and its Quests tab the /qb list).
// Server facts copied from ACECustom: Fellowship.MaxFellows = 29 (Source/ACE.Server/Entity/
// Fellowship.cs), Zone Share (ZoneShareManager) and the fellowship luminance share.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthILT.Hub;

/// <summary>One Leaftide feature: id, display text, where it lives, and the server bit that says whether it's on.</summary>
/// <param name="Key">Stable id (the profile JSON's "key").</param>
/// <param name="Label">Display name.</param>
/// <param name="Group">Character, Items, Fellowship or Activities (the list's sections).</param>
/// <param name="Where">Where the Rynth tools handle it.</param>
/// <param name="Description">One line on what the server does.</param>
/// <param name="ServerBit">The IltServerOptions key the server answers for; null = built into ACECustom (on with the world).</param>
internal sealed record IltFeatureInfo(string Key, string Label, string Group, string Where, string Description, string? ServerBit);

/// <summary>The quest bonus list as the profile JSON carries it ("questBonus"), for RynthOracle's Quests tab.</summary>
/// <param name="Disabled">The server has /qb turned off.</param>
/// <param name="Generation">Bumped per parsed "/qb list"; 0 = not loaded this session.</param>
/// <param name="Count">The server's quest bonus count; -1 = unknown.</param>
/// <param name="Status">One line on the last request ("loading...", "12 completed, 3 incomplete", ...).</param>
/// <param name="Completed">Completed bonus flags, sorted.</param>
/// <param name="Incomplete">Incomplete bonus flags, sorted.</param>
internal sealed record IltQuestBonusInfo(bool Disabled, int Generation, long Count, string Status,
                                         IReadOnlyList<string> Completed, IReadOnlyList<string> Incomplete);

internal static class IltFeatureCatalog
{
    public const string GroupCharacter = "Character";
    public const string GroupItems = "Items";
    public const string GroupFellowship = "Fellowship";
    public const string GroupActivities = "Activities";

    /// <summary>The section order of the list.</summary>
    public static readonly string[] Groups = { GroupCharacter, GroupItems, GroupFellowship, GroupActivities };

    /// <summary>ACECustom's fellowship member limit (Fellowship.MaxFellows); retail is 9.</summary>
    public const int IltFellowshipMax = 29;

    /// <summary>Every Leaftide feature RynthILT supports, in display order.</summary>
    public static readonly IltFeatureInfo[] All =
    {
        // Character progression and money.
        new("bank", "Banking", GroupCharacter, "Mini Remote > Banking tab; Remote tab bank line",
            "Bank pyreals, luminance, keys and coins; deposit, withdraw and transfer (/bank).", IltFeature.Bank),
        new("guardian", "Guardian (infinite base attributes)", GroupCharacter, "Guardian panel (/rc guardian)",
            "Temple of Enlightenment guardians raise base attributes past the retail cap.", null),
        new("attributes", "Infinite raised attributes", GroupCharacter, "Skills > Progression > Attributes",
            "Spend unassigned XP on attributes past the retail max (/xp, /attr).", IltFeature.Xp),
        new("augmentations", "Infinite augmentations", GroupCharacter, "Skills > Progression > Augmentations",
            "Augmentation levels with no retail cap (/aug).", IltFeature.Aug),
        new("enlightenment", "Enlightenment", GroupCharacter, "Skills > Progression > Enlightenment",
            "Enlightenment tiers past retail, with medallions, sigils and crests (/enl).", IltFeature.Enl),

        // Items.
        new("paragon_armor", "Paragon armor", GroupItems, "Item info (RynthAi): Paragon sets and levels",
            "Paragon armor progression; the 50th Armor Paragon gates enlightenment past 300.", null),
        new("paragon_weapons", "Paragon weapons", GroupItems, "Item info (RynthAi): Paragon sets and levels",
            "Paragon weapon progression (Paragon Missile, Caster and Melee sets).", null),
        new("paragon_jewelry", "Paragon jewelry", GroupItems, "Item info (RynthAi): Paragon sets and levels",
            "Paragon jewelry progression; the 50th Paragon gates enlightenment past 150.", null),
        new("split_arrows", "Split arrows", GroupItems, "Mini Remote > Gear > Split arrows",
            "Missile weapons carry split arrows, added with White Quartz salvage.", null),
        new("t11", "T11 items", GroupItems, "Item info and loot rules (RynthAi, RynthCore Loot Editor)",
            "Tier 11+ gear: tiers, gear grades, property slots and modifiers.", null),

        // Fellowship.
        new("fellowship_large", "Large fellowships (up to 29)", GroupFellowship, "RynthOracle > Fellowship",
            "Fellowships hold up to 29 members instead of retail's 9.", null),
        new("zone_share", "Zone Share", GroupFellowship, "Mini Remote > Character > Session rates",
            "XP and luminance shared with everyone in a Zone Share area, capped at a fellowship's size.", null),
        new("fellowship_luminance", "Fellowship luminance share", GroupFellowship, "Mini Remote > Character > Session rates",
            "Luminance splits among fellows the way XP does.", null),
        new("fellowship_blackjack", "Fellowship Blackjack", GroupFellowship, "Mini Remote > Games tab",
            "Blackjack played as a fellowship (/fblackjack).", IltFeature.FBlackjack),

        // Other server features RynthILT drives.
        new("quests", "Quest tracker", GroupActivities, "RynthOracle > Quests tab",
            "Your quest timers from the server (/myquests).", IltFeature.Quests),
        new("quest_bonus", "Quest bonus", GroupActivities, "RynthOracle > Quests tab",
            "The quest bonus list (/qb).", IltFeature.Qb),
        new("pets", "Pet log", GroupActivities, "Mini Remote > Pets tab", "Captured pets (/pets).", IltFeature.Pets),
        new("shinies", "Shiny log", GroupActivities, "Mini Remote > Pets tab", "Shiny creatures (/shinies).", IltFeature.Shinies),
        new("charms", "Charms", GroupActivities, "Mini Remote > Charms tab",
            "Registry charms and the Guardian Hand, with their toggles.", null),
        new("bounties", "Bounties", GroupActivities, "Bounties window (/rc bounties)", "ACECustom bounties and your progress.", null),
        new("clap", "Chunk clap (auto craft)", GroupActivities, "Mini Remote > Gear tab", "Auto-crafting chunks (/clap).", IltFeature.Clap),
        new("powerball", "Powerball", GroupActivities, "Mini Remote > Games tab", "The Powerball lottery (/pb).", IltFeature.Powerball),
        new("blackjack", "Blackjack", GroupActivities, "Mini Remote > Games tab", "Blackjack against the house (/blackjack).", IltFeature.Blackjack),
    };

    /// <summary>The entry with <paramref name="key"/>, or null.</summary>
    public static IltFeatureInfo? Find(string key)
    {
        foreach (IltFeatureInfo f in All)
            if (f.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return f;
        return null;
    }

    /// <summary>
    /// A feature's state: the server's bit for a probed feature (unknown off ILT, where nothing is
    /// asked), On for a built-in one on an ILT world, Unknown anywhere else.
    /// </summary>
    public static IltTri StateOf(IltFeatureInfo f, bool iltWorld, Func<string, IltTri> bit)
    {
        if (!iltWorld) return IltTri.Unknown;
        return f.ServerBit == null ? IltTri.On : bit(f.ServerBit);
    }

    /// <summary>Short state text for the list and chat: "on", "off" or "checking".</summary>
    public static string StateText(IltTri state) => state switch
    {
        IltTri.On => "on",
        IltTri.Off => "off",
        _ => "checking",
    };

    /// <summary>The fellowship member limit: ACECustom's 29 on an ILT world, retail's 9 elsewhere.</summary>
    public static int FellowshipMax(bool iltWorld) => iltWorld ? IltFellowshipMax : RynthIltServer.RetailFellowshipMax;

    /// <summary>
    /// The "RynthILT.Server" profile JSON (schema in Plugins/Shared/RynthIltServerApi.cs). Pure:
    /// everything comes in as arguments, so the tests build it without a client. Plain ASCII.
    /// </summary>
    public static string BuildProfileJson(bool iltWorld, string world, bool forced, string source, DateTime checkedUtc,
                                          Func<string, IltTri> bit, IltQuestBonusInfo? questBonus = null)
    {
        var sb = new StringBuilder(4096);
        sb.Append("{\"ilt\":").Append(iltWorld ? "true" : "false");
        sb.Append(",\"world\":\"").Append(Escape(world)).Append('"');
        sb.Append(",\"forced\":").Append(forced ? "true" : "false");
        sb.Append(",\"source\":\"").Append(Escape(source)).Append('"');
        sb.Append(",\"checked\":\"")
          .Append(checkedUtc == DateTime.MinValue ? "" : checkedUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))
          .Append('"');

        sb.Append(",\"features\":[");
        for (int i = 0; i < All.Length; i++)
        {
            IltFeatureInfo f = All[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"key\":\"").Append(Escape(f.Key))
              .Append("\",\"label\":\"").Append(Escape(f.Label))
              .Append("\",\"group\":\"").Append(Escape(f.Group))
              .Append("\",\"state\":").Append(((int)StateOf(f, iltWorld, bit)).ToString(CultureInfo.InvariantCulture))
              .Append(",\"where\":\"").Append(Escape(f.Where)).Append("\"}");
        }
        sb.Append(']');

        int State(string key) => (int)StateOf(Find(key)!, iltWorld, bit);
        sb.Append(",\"fellowship\":{\"maxMembers\":").Append(FellowshipMax(iltWorld).ToString(CultureInfo.InvariantCulture))
          .Append(",\"zoneShare\":").Append(State("zone_share").ToString(CultureInfo.InvariantCulture))
          .Append(",\"blackjack\":").Append(State("fellowship_blackjack").ToString(CultureInfo.InvariantCulture))
          .Append(",\"luminanceShare\":").Append(State("fellowship_luminance").ToString(CultureInfo.InvariantCulture))
          .Append('}');

        // The /qb list (ILT worlds only): RynthOracle's Quests tab draws it.
        if (questBonus != null && iltWorld)
        {
            sb.Append(",\"questBonus\":{\"disabled\":").Append(questBonus.Disabled ? "true" : "false")
              .Append(",\"generation\":").Append(questBonus.Generation.ToString(CultureInfo.InvariantCulture))
              .Append(",\"count\":").Append(questBonus.Count.ToString(CultureInfo.InvariantCulture))
              .Append(",\"status\":\"").Append(Escape(questBonus.Status)).Append('"');
            AppendList(sb, "completed", questBonus.Completed);
            AppendList(sb, "incomplete", questBonus.Incomplete);
            sb.Append('}');
        }
        sb.Append('}');
        return sb.ToString();

        static void AppendList(StringBuilder b, string name, IReadOnlyList<string> items)
        {
            b.Append(",\"").Append(name).Append("\":[");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) b.Append(',');
                b.Append('"').Append(Escape(items[i])).Append('"');
            }
            b.Append(']');
        }
    }

    /// <summary>The chat listing for "/ra hub features": one line per feature, ASCII only.</summary>
    public static List<string> BuildChatLines(bool iltWorld, Func<string, IltTri> bit)
    {
        var lines = new List<string>(All.Length + Groups.Length);
        foreach (string g in Groups)
        {
            var parts = new List<string>();
            foreach (IltFeatureInfo f in All)
                if (f.Group == g) parts.Add($"{f.Label} [{StateText(StateOf(f, iltWorld, bit))}]");
            lines.Add($"{g}: {string.Join(", ", parts)}");
        }
        return lines;
    }

    /// <summary>JSON string body: quotes, backslashes and control characters escaped, anything non-ASCII as \uXXXX.</summary>
    private static string Escape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c < 0x20 || c > 0x7E) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
