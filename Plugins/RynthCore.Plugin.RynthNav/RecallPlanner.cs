using System;
using System.Collections.Generic;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

/// <summary>One recall the route planner may use: a spell to cast, or a command to type.</summary>
internal sealed class RecallOption
{
    public string Name = "";
    /// <summary>The spell to cast (0 = a command).</summary>
    public int SpellId;
    /// <summary>The command to send (@lifestone, @house recall) when SpellId is 0.</summary>
    public string Command = "";
    public double Ns, Ew;
    public double CastSeconds;
    /// <summary>Learned in game (RecallMemory), not fixed by the world data.</summary>
    public bool Learned;

    public RecallLink ToLink() => new(Ns, Ew, Name, PortalRoute.RecallCostUnits(CastSeconds));
}

/// <summary>
/// Which recalls this character can use, and where they land: the fixed recall spells
/// from the Atlas (only the ones in the character's spellbook), plus the personal ones
/// RynthNav has learned (RecallMemory). A recall whose landing isn't known, or is off the
/// map (a dungeon), is left out with a note saying why. No host calls: the offline tests
/// compile this file.
/// </summary>
internal static class RecallPlanner
{
    public const int PrimaryPortalTieRecall = 48;
    public const int PrimaryPortalTie = 47;
    public const int LifestoneRecall = 1635;
    public const int LifestoneTie = 2644;
    public const int PortalRecall = 2645;
    public const int SecondaryPortalTie = 2646;
    public const int SecondaryPortalTieRecall = 2647;

    /// <summary>A recall spell: the magic stance (and a wand) plus the cast. Estimated.</summary>
    public const double SpellCastSeconds = 5.0;
    /// <summary>@lifestone, @house recall and the like: a long recall animation. Estimated.</summary>
    public const double CommandRecallSeconds = 14.0;

    /// <param name="known">The character's spellbook; null while it hasn't been read.</param>
    public static List<RecallOption> Build(Atlas atlas, ISet<int>? known, RecallMemory memory, string charKey, List<string>? notes)
    {
        var list = new List<RecallOption>();

        // Fixed-destination recall spells (Aerlinthe Recall, Lyceum Recall, ...).
        if (known == null)
        {
            notes?.Add("Spellbook not read yet: recall spells left out.");
        }
        else
        {
            var seen = new HashSet<int>();
            foreach (AtlasRecall r in atlas.Recalls)
            {
                if (!known.Contains(r.SpellId) || !seen.Add(r.SpellId)) continue;
                if (!r.OnMap)
                {
                    notes?.Add(r.Name + " lands " + (r.In.Length > 0 ? "in " + r.In : "in a dungeon") + ": not used for routes.");
                    continue;
                }
                list.Add(new RecallOption { Name = r.Name, SpellId = r.SpellId, Ns = r.Ns, Ew = r.Ew, CastSeconds = SpellCastSeconds });
            }
        }

        // Personal recall spells: the landing is the character's own.
        if (known != null)
        {
            AddLearnedSpell(list, notes, known, memory, charKey, LifestoneRecall, "Lifestone Recall", RecallSlot.Lifestone,
                "cast Lifestone Tie on a lifestone");
            AddLearnedSpell(list, notes, known, memory, charKey, PortalRecall, "Portal Recall", RecallSlot.LastPortal,
                "walk through a portal");
            AddLearnedSpell(list, notes, known, memory, charKey, PrimaryPortalTieRecall, "Primary Portal Tie Recall", RecallSlot.Tie1,
                "cast Primary Portal Tie on a portal");
            AddLearnedSpell(list, notes, known, memory, charKey, SecondaryPortalTieRecall, "Secondary Portal Tie Recall", RecallSlot.Tie2,
                "cast Secondary Portal Tie on a portal");
        }

        // Command recalls (no spell): @lifestone always; house, mansion and hometown
        // once the character has used them (only then is it known that they have one).
        AddLearnedCommand(list, notes, memory, charKey, RecallSlot.Sanctuary, "Lifestone (@lifestone)", alwaysAvailable: true,
            "use a lifestone");
        AddLearnedCommand(list, notes, memory, charKey, RecallSlot.House, "House recall", alwaysAvailable: false, "");
        AddLearnedCommand(list, notes, memory, charKey, RecallSlot.Mansion, "Allegiance mansion recall", alwaysAvailable: false, "");
        AddLearnedCommand(list, notes, memory, charKey, RecallSlot.Hometown, "Allegiance hometown recall", alwaysAvailable: false, "");
        return list;
    }

    private static void AddLearnedSpell(List<RecallOption> list, List<string>? notes, ISet<int> known, RecallMemory memory,
        string charKey, int spellId, string name, RecallSlot slot, string howToLearn)
    {
        if (!known.Contains(spellId)) return;
        RecallSpot? spot = memory.Get(charKey, slot);
        if (spot == null) { notes?.Add(name + ": where it lands isn't known yet (" + howToLearn + ")."); return; }
        if (!spot.OnMap) { notes?.Add(name + " lands " + Where(spot) + ": not used for routes."); return; }
        list.Add(new RecallOption
        {
            Name = name, SpellId = spellId, Ns = spot.Ns, Ew = spot.Ew, CastSeconds = SpellCastSeconds, Learned = true,
        });
    }

    private static void AddLearnedCommand(List<RecallOption> list, List<string>? notes, RecallMemory memory, string charKey,
        RecallSlot slot, string name, bool alwaysAvailable, string howToLearn)
    {
        RecallSpot? spot = memory.Get(charKey, slot);
        if (spot == null)
        {
            if (alwaysAvailable) notes?.Add(name + ": where it lands isn't known yet (" + howToLearn + ").");
            return;
        }
        if (!spot.OnMap) { notes?.Add(name + " lands " + Where(spot) + ": not used for routes."); return; }
        list.Add(new RecallOption
        {
            Name = name,
            Command = spot.Command.Length > 0 ? spot.Command : RecallMemory.DefaultCommand(slot),
            Ns = spot.Ns, Ew = spot.Ew, CastSeconds = CommandRecallSeconds, Learned = true,
        });
    }

    private static string Where(RecallSpot s) => s.Label.Length > 0 ? "in " + s.Label : "in a dungeon";

    public static List<RecallLink> Links(List<RecallOption> options)
    {
        var links = new List<RecallLink>(options.Count);
        foreach (var o in options) links.Add(o.ToLink());
        return links;
    }
}
