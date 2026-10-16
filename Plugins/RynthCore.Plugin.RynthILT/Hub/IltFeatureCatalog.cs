// IltFeatureCatalog.cs — Every Infinite Leaftide feature RynthILT supports, in one list.
//
// Each entry says what the feature is, where it lives in the Rynth tools, and how its state is
// known. Detection is client-side only: the server type comes from the login (the world name,
// RynthCore's "ilt" verdict), and the ILT profile below is the known set of what Infinite
// Leaftide runs, taken from ACECustom (its ServerConfig defaults and deploy/PROD_DEPLOY_PLAN.md,
// e.g. pet bond and potency on in prod, pet strain off since launch). The server is never asked
// for a list; what it returns on its own (probe replies, chat) refines the profile:
//   * Profile (IltKnown.Profile): on in ILT (T11, split arrows, pet breeding, ...). On with the
//     ILT world; a server refusal seen in chat turns it off.
//   * Profile off (IltKnown.ProfileOff): an ACECustom option ILT runs with off (pet strain,
//     defense scaling, the pyreal refill). Off with the ILT world; a chat line only the feature
//     sends (e.g. a pyreal refill) turns it on.
//   * Checked (IltKnown.Checked): the login probe answers it (/bank, /aug, /xp, /pets, ...).
//     "checking" until the reply arrives, then on or off.
//   * No server bit at all (the Temple guardians, Paragon gear): content, on with the ILT world.
// Off an ILT world nothing is claimed: every state is unknown.
// The list feeds the ILT settings window ("/ilt settings", IltSettingsView/) and "/ra hub features".
// Server facts copied from ACECustom: Fellowship.MaxFellows = 29 (Source/ACE.Server/Entity/
// Fellowship.cs), Zone Share (ZoneShareManager) and the fellowship luminance share.
using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthILT.Hub;

/// <summary>One Leaftide feature: id, display text, where it lives, and the server bit that says whether it's on.</summary>
/// <param name="Key">Stable id.</param>
/// <param name="Label">Display name.</param>
/// <param name="Group">Character, Items, Fellowship or Activities (the list's sections).</param>
/// <param name="Where">Where the Rynth tools handle it.</param>
/// <param name="Description">One line on what the server does.</param>
/// <param name="ServerBit">The IltServerOptions key probes and chat refusals set; null = content built into ACECustom (on with the world).</param>
/// <param name="Known">How the state is known while the bit hasn't been set (see <see cref="IltKnown"/>).</param>
internal sealed record IltFeatureInfo(string Key, string Label, string Group, string Where, string Description, string? ServerBit,
                                      IltKnown Known = IltKnown.Profile);

/// <summary>How a feature's state is known on an ILT world before (or without) a server answer.</summary>
internal enum IltKnown
{
    /// <summary>On in ILT: on with the ILT world unless chat shows it off.</summary>
    Profile,
    /// <summary>The login probe answers it: "checking" until the reply arrives.</summary>
    Checked,
    /// <summary>Off in ILT: off with the ILT world unless chat shows it on.</summary>
    ProfileOff,
}

internal static class IltFeatureCatalog
{
    public const string GroupCharacter = "Character";
    public const string GroupItems = "Items";
    public const string GroupPets = "Pets";
    public const string GroupFellowship = "Fellowship";
    public const string GroupCombat = "Combat";
    public const string GroupActivities = "Activities";

    /// <summary>The section order of the list (ACECustom's "/ilt features" uses the same).</summary>
    public static readonly string[] Groups = { GroupCharacter, GroupItems, GroupPets, GroupFellowship, GroupCombat, GroupActivities };

    /// <summary>ACECustom's fellowship member limit (Fellowship.MaxFellows); retail is 9.</summary>
    public const int IltFellowshipMax = 29;

    /// <summary>A retail fellowship's member limit.</summary>
    public const int RetailFellowshipMax = 9;

    /// <summary>Every Leaftide feature RynthILT supports, in display order.</summary>
    public static readonly IltFeatureInfo[] All =
    {
        // Character progression and money.
        new("bank", "Banking", GroupCharacter, "Mini Remote > Banking tab; Remote tab bank line",
            "Bank pyreals, luminance, keys and coins; deposit, withdraw and transfer (/bank).", IltFeature.Bank, IltKnown.Checked),
        new("bank_notes", "Trade note banking", GroupCharacter, "Mini Remote > Banking tab (trade notes)",
            "Deposit trade notes as pyreals, withdraw any note from I to MMD, transfer note value (/bank d n).", "bank_notes"),
        new("guardian", "Guardian (infinite base attributes)", GroupCharacter, "Guardian panel (/rc guardian)",
            "Temple of Enlightenment guardians raise base attributes past the retail cap.", null),
        new("attributes", "Infinite raised attributes", GroupCharacter, "Skills > Progression > Attributes",
            "Spend unassigned XP on attributes past the retail max (/xp, /attr).", IltFeature.Xp, IltKnown.Checked),
        new("augmentations", "Infinite augmentations", GroupCharacter, "Skills > Progression > Augmentations",
            "Luminance augmentations far past retail: Creature 6000, Item / Life / War / Void 4000 (/aug).", IltFeature.Aug, IltKnown.Checked),
        new("aug_charms", "Augmentation counter charms", GroupCharacter, "Skills > Progression > Augmentations",
            "Triune Weave and the school charms add to your aug counts past the purchase cap (/aug).", "aug_charms"),
        new("enlightenment", "Enlightenment", GroupCharacter, "Skills > Progression > Enlightenment",
            "Enlightenment tiers past retail, with medallions, sigils and crests (/enl).", IltFeature.Enl, IltKnown.Checked),
        new("ilt_skills", "Spend XP and train skills in one go", GroupCharacter, "Chat: /ilt xp, /ilt train",
            "Spends all unspent XP into trained skills, or trains every affordable skill, in priority order.", "ilt_skills"),
        new("leaderboards", "Leaderboards", GroupCharacter, "Chat: /top",
            "Server leaderboards: quest bonus, level, enlightenment, augs, bank, pets and more (/top).", "leaderboards"),

        // Items, loot and crafting.
        new("paragon_armor", "Paragon armor", GroupItems, "Item info (RynthAi): Paragon sets and levels",
            "Paragon armor progression; the 50th Armor Paragon gates enlightenment past 300.", null),
        new("paragon_weapons", "Paragon weapons", GroupItems, "Item info (RynthAi): Paragon sets and levels",
            "Paragon weapon progression (Paragon Missile, Caster and Melee sets).", null),
        new("paragon_jewelry", "Paragon jewelry", GroupItems, "Item info (RynthAi): Paragon sets and levels",
            "Paragon jewelry progression; the 50th Paragon gates enlightenment past 150.", null),
        new("t11", "T11 items", GroupItems, "Item info and loot rules (RynthAi, RynthCore Loot Editor)",
            "Tier 11 to 25 gear (Zone Control tiers): tiers, gear grades, property slots and modifiers.", "t11"),
        new("t11_scaling", "T11+ weapon scaling", GroupItems, "Item info (RynthAi)",
            "T11+ weapons scale with your augs and have aug wield floors; from T16, charm counts too.", "t11_scaling"),
        new("t11_craft_gate", "T11+ crafting rules (crafting uniqueness)", GroupItems, "Mini Remote > Gear tab",
            "On T11+ items the server refuses an imbue or tink that would replace a better value, and limits materials per item type.",
            "t11_craft_gate"),
        new("split_arrows", "Split arrows", GroupItems, "Mini Remote > Gear > Split arrows",
            "Missile weapons carry split arrows, added with White Quartz salvage.", "split_arrows"),
        new("charms", "Charms", GroupItems, "Mini Remote > Charms tab",
            "Registry charms and the Guardian Hand, with their toggles; each charm is listed in the Charms section.", "charms"),
        new("clap", "Chunk clap (auto craft)", GroupItems, "Mini Remote > Gear tab",
            "Turns aetheria, chunks and trinkets into banked enlightened coins (/clap all).", IltFeature.Clap, IltKnown.Checked),

        // Pets.
        new("pets", "Pet log", GroupPets, "Mini Remote > Pets tab", "Captured pets, registered with Professor Ruggan (/pets).", IltFeature.Pets, IltKnown.Checked),
        new("shinies", "Shiny log", GroupPets, "Mini Remote > Pets tab", "Rare shiny creature variants you captured (/shinies).", IltFeature.Shinies, IltKnown.Checked),
        new("monster_capture", "Monster capture", GroupPets, "Mini Remote > Pets tab",
            "Siphon lenses capture a creature\'s look into a pet essence (<=20% health or under 20 HP; Asheron\'s Lens skips the gate). Auto-capture is the checkbox on the Pets roster.", "monster_capture"),
        new("pet_breeding", "Pet breeding", GroupPets, "Mini Remote > Pets > Breeding",
            "Breed two combat pet devices at the Seedy Motel for inherited and mutated offspring (/dance).", "pet_breeding"),
        new("pet_maturity", "Pet maturity", GroupPets, "Mini Remote > Pets > Breeding",
            "Bred pets start small and weak and grow up with kills.", "pet_maturity"),
        new("pet_bond", "Pet bond", GroupPets, "Mini Remote > Pets tab",
            "Pets gain bond XP from kills, which scales their stats (/top bonds). On in ILT (breeding needs bond 100).", IltFeature.PetBond),
        new("pet_potency", "Pet potency", GroupPets, "Mini Remote > Pets tab",
            "Savage Echo residue raises a pet's potency (/echo-notify). On in ILT.", IltFeature.PetPotency),
        new("pet_strain", "Pet strain", GroupPets, "Mini Remote > Pets tab",
            "High pet potency lowers the owner's damage rating. Off in ILT since launch.", "pet_strain", IltKnown.ProfileOff),
        new("pet_refill", "Summon essence refill", GroupPets, "Mini Remote > Pets tab",
            "Pays pyreals to refill an empty essence when you summon (needs its charm). Off by default; a refill message in chat shows it on.", IltFeature.PetRefill, IltKnown.ProfileOff),
        new("universal_mastery", "Universal summoning mastery", GroupPets, "Mini Remote > Pets tab",
            "Use pet essences of any summoning mastery (needs its charm).", IltFeature.UniversalMastery),
        new("pet_naming", "Pet names", GroupPets, "Chat: /pet-name",
            "Ask for a name for your pet; staff approve it (/pet-name).", "pet_naming"),

        // Fellowship.
        new("fellowship_large", "Large fellowships (up to 29)", GroupFellowship, "The game's fellowship panel",
            "Fellowships hold up to 29 members instead of retail's 9.", "fellowship_size"),
        new("fship", "Fellowship commands", GroupFellowship, "ILT fellowship window (/ilt fellow)",
            "Create, invite, invite your landblock, list nearby, leave and disband from chat (/fship).", "fship"),
        new("zone_share", "Zone Share", GroupFellowship, "Mini Remote > Session Stats (luminance, pass-up)",
            "XP and luminance shared with everyone in a Zone Share area, capped at a fellowship's size.", "zone_share"),
        new("fellowship_luminance", "Fellowship luminance share", GroupFellowship, "Mini Remote > Session Stats (luminance, pass-up)",
            "Luminance splits among fellows the way XP does.", "fellowship_luminance"),
        new("fellowship_blackjack", "Fellowship Blackjack", GroupFellowship, "Mini Remote > Games tab",
            "Blackjack played as a fellowship (/fblackjack).", IltFeature.FBlackjack, IltKnown.Checked),

        // Combat.
        new("smart_ring", "Smart ring spells", GroupCombat, "Chat: /ilt smartring",
            "Ring spells hit everything in their radius, with double and triple procs (/ilt smartring on|off).", "smart_ring"),
        new("dmgformat", "Damage number format", GroupCombat, "Chat: /ilt dmgformat",
            "Damage shown plain, with commas, or short (K/M/B) (/ilt dmgformat).", "dmgformat"),
        new("overkill", "Overkill on kill messages", GroupCombat, "Chat: /ilt showoverkill",
            "Kill and death messages show the overkill amount (/ilt showoverkill).", "overkill"),
        new("missile_power", "Missile power bar damage", GroupCombat, "Combat",
            "The missile accuracy bar scales damage from 1x to 2.5x.", "missile_power"),
        new("t11_hit_gate", "T11+ hit gate", GroupCombat, "Combat",
            "Attacks on v11+ monsters miss unless your aug counts meet that tier's requirement.", "t11_hit_gate"),
        new("v11_endgame", "v11+ endgame scaling", GroupCombat, "Combat",
            "Percent-of-max-HP damage, compressed vulnerabilities and tougher v11+ monsters.", "v11_endgame"),
        new("defense_scaling", "Defense scaling", GroupCombat, "Combat",
            "Curbs extreme evasion at very high defense skill. Off by default in ACECustom.", "defense_scaling", IltKnown.ProfileOff),

        // Activities.
        new("quests", "Quest tracker", GroupActivities, "Mini Remote > Quests tab",
            "Your quest timers from the server (/myquests).", IltFeature.Quests, IltKnown.Checked),
        new("quest_bonus", "Quest bonus", GroupActivities, "Mini Remote > Quests tab (quest bonus)",
            "Each quest completed adds to your XP bonus; the list (/qb).", IltFeature.Qb, IltKnown.Checked),
        new("bounties", "Bounties", GroupActivities, "Bounties window (/rc bounties)", "ACECustom bounties and your progress.", "bounties"),
        new("rifts", "Greater Rifts", GroupActivities, "Chat: /rift", "Private, timed dungeon runs that scale by tier (/rift).", "rifts"),
        new("room_assign", "Room Assign dungeons", GroupActivities, "Portals and recalls",
            "One-player rooms with their own spawns.", "room_assign"),
        new("portal_requirements", "Aug / ENL / QB portals", GroupActivities, "Portals",
            "Portals that need augmentations, an enlightenment level or a quest bonus.", "portal_requirements"),
        new("powerball", "Powerball", GroupActivities, "Mini Remote > Games tab", "The Powerball lottery (/pb).", IltFeature.Powerball, IltKnown.Checked),
        new("blackjack", "Blackjack", GroupActivities, "Mini Remote > Games tab", "Blackjack against the house (/blackjack).", IltFeature.Blackjack, IltKnown.Checked),
        new("unstuck", "Unstuck", GroupActivities, "Chat: /unstuck", "Gets you out of a stuck portal or spot (/unstuck).", "unstuck"),
    };

    /// <summary>The entry with <paramref name="key"/>, or null.</summary>
    public static IltFeatureInfo? Find(string key)
    {
        foreach (IltFeatureInfo f in All)
            if (f.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return f;
        return null;
    }

    /// <summary>
    /// A feature's state on an ILT world: what the server returned (a probe reply or a refusal in
    /// chat) when it did; else the ILT profile: On for content and <see cref="IltKnown.Profile"/>,
    /// Off for <see cref="IltKnown.ProfileOff"/>, Unknown ("checking") for probed features.
    /// Off ILT nothing is claimed: Unknown.
    /// </summary>
    public static IltTri StateOf(IltFeatureInfo f, bool iltWorld, Func<string, IltTri> bit)
    {
        if (!iltWorld) return IltTri.Unknown;
        if (f.ServerBit == null) return IltTri.On;
        IltTri reported = bit(f.ServerBit);
        if (reported != IltTri.Unknown) return reported;
        return f.Known switch
        {
            IltKnown.Profile => IltTri.On,
            IltKnown.ProfileOff => IltTri.Off,
            _ => IltTri.Unknown,
        };
    }

    /// <summary>
    /// A charm's state: charms are part of the ILT profile, so on with the ILT world unless the
    /// server refused one in chat (e.g. "Auto-Rebuff Charm is currently disabled globally").
    /// </summary>
    public static IltTri CharmStateOf(bool iltWorld, IltTri reported)
        => !iltWorld ? IltTri.Unknown : reported != IltTri.Unknown ? reported : IltTri.On;

    /// <summary>Prefix of a charm's server key (IltFeature.Charm): "charm:manabarrier".</summary>
    public const string CharmKeyPrefix = "charm:";

    /// <summary>
    /// Display text for a key the settings view lists: a catalog feature (its label, group and
    /// description with where it lives and how its state is known), a charm ("charm:..." under
    /// Charms, named from the charm registry), or any other server bit as itself under "Other".
    /// </summary>
    public static (string Label, string Group, string Tooltip) Describe(string key)
    {
        IltFeatureInfo? f = Find(key);
        if (f != null) return (f.Label, f.Group, f.Description + "\nWhere: " + f.Where + "\n" + HowKnown(f));
        if (key.StartsWith(CharmKeyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            const string charmNote = "Part of ILT: on unless the server refuses it in chat.";
            foreach (IltCharmDef def in IltInventory.Charms)
                if (IltFeature.Charm(def.Name).Equals(key, StringComparison.OrdinalIgnoreCase))
                    return (def.Name, GroupCharms, (def.Effect.Length > 0 ? def.Effect + "\n" : string.Empty)
                                                   + "Where: Mini Remote > Charms tab\n" + charmNote);
            return (key.Substring(CharmKeyPrefix.Length), GroupCharms, charmNote);
        }
        return (key, GroupOther, "A server switch RynthILT reads from chat.");
    }

    /// <summary>One line on where a feature's state comes from (tooltips).</summary>
    private static string HowKnown(IltFeatureInfo f) => f.ServerBit == null
        ? "Built into ILT: on with the world."
        : f.Known switch
        {
            IltKnown.Checked => "Checked at login: the server's reply decides.",
            IltKnown.ProfileOff => "Off in ILT: on only if the server shows it in chat.",
            _ => "Part of ILT: on unless the server refuses it in chat.",
        };

    /// <summary>The settings view's extra sections after <see cref="Groups"/>.</summary>
    public const string GroupCharms = "Charms";
    public const string GroupOther = "Other";

    /// <summary>Short state text for the list and chat: "on", "off" or "checking".</summary>
    public static string StateText(IltTri state) => state switch
    {
        IltTri.On => "on",
        IltTri.Off => "off",
        _ => "checking",
    };

    /// <summary>The fellowship member limit: ACECustom's 29 on an ILT world, retail's 9 elsewhere.</summary>
    public static int FellowshipMax(bool iltWorld) => iltWorld ? IltFellowshipMax : RetailFellowshipMax;

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
}
