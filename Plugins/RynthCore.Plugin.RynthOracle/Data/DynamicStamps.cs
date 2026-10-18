using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>
/// One Infinite Leaftide Dynamic Stamp (dynstamps.csv): an NPC hand-in that puts a
/// "Dynamic_N_..." quest flag on the character. List from the players' "ExtraDyn" sheet
/// (10.13.2026, one sheet per area, the wiki's Dynamic Stamps page); NPCs that give no stamp
/// are left out. A stamp is a one-time flag: having it in /myquests means it's done.
/// </summary>
internal sealed class DynamicStamp
{
    public string Area = "";
    /// <summary>Where in the area (a town, a dungeon, coordinates), or "".</summary>
    public string Place = "";
    public string Npc = "";
    /// <summary>What the NPC gives back (the sheet's "X gives you Y"), or "".</summary>
    public string Reward = "";
    /// <summary>The quest flag, lower case (as QuestFlagStore keys it).</summary>
    public string Stamp = "";
    public string Note = "";

    /// <summary>The NPC, else the place (a few sheet rows only name the spot).</summary>
    public string Label => Npc.Length > 0 ? Npc : Place;

    /// <summary>
    /// The flag this character has for the stamp, or null. The sheet has a few stamps copied
    /// without their closing "!" or cut short: those also match with "!" added and, when at
    /// least <see cref="MinPrefixLength"/> long, the one Dynamic flag on the character starting
    /// with them.
    /// </summary>
    public string? MatchedFlag(QuestFlagStore flags)
    {
        if (flags.TryGet(Stamp, out _)) return Stamp;
        if (Stamp.EndsWith('!')) return null;
        if (flags.TryGet(Stamp + "!", out _)) return Stamp + "!";
        if (Stamp.Length < MinPrefixLength) return null;
        string? only = null;
        foreach (string key in flags.Flags.Keys)
        {
            if (!key.StartsWith(Stamp, StringComparison.OrdinalIgnoreCase)) continue;
            if (only != null) return null;   // more than one: can't tell which
            only = key;
        }
        return only;
    }

    public bool Have(QuestFlagStore flags) => MatchedFlag(flags) != null;

    // Sheet areas that are groupings, not places on the map.
    private static readonly HashSet<string> NotPlaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "Looking Around", "Odd Stamps", "Odd Stamps 2",
    };

    /// <summary>
    /// Where RynthNav should go for this stamp, best first (RynthNav takes the first that is on its
    /// map): the coordinates in the note or the place, the place and its parts ("Mhoire Castle -
    /// Great Hall" also tries "Mhoire Castle"), the NPC, then the area. Empty when there is nothing.
    /// </summary>
    public List<string> NavCandidates()
    {
        var list = new List<string>();
        void Add(string s)
        {
            s = s.Trim().Trim('.', ',', ';', ':').Trim();
            if (s.Length >= 4 && !char.IsDigit(s[0]) && !list.Contains(s, StringComparer.OrdinalIgnoreCase)) list.Add(s);
        }

        foreach (string text in new[] { Note, Place })
            if (NavTargets.FirstCoords(text) is string c && !list.Contains(c)) list.Add(c);

        string place = NavTargets.WithoutCoords(Place);
        place = System.Text.RegularExpressions.Regex.Replace(place, @"\b(at|in the|nearby)\b", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        place = System.Text.RegularExpressions.Regex.Replace(place, @"\s+", " ").Trim();
        Add(place);
        foreach (string part in place.Split(new[] { " - ", " / ", "(", ")" }, StringSplitOptions.RemoveEmptyEntries))
            Add(part);
        Add(Npc);
        if (!NotPlaces.Contains(Area)) Add(Area);
        return list;
    }

    /// <summary>A truncated stamp shorter than this is matched exactly only (too many flags share a short stem).</summary>
    public const int MinPrefixLength = 14;
}

internal static class DynamicStampList
{
    /// <summary>Dynamic stamp quest flags start with this (lower case).</summary>
    public const string FlagPrefix = "dynamic_";

    public static List<DynamicStamp> All { get; } = Load();

    /// <summary>The areas in list order (Facility Hub, Looking Around, ...).</summary>
    public static List<string> Areas { get; } = BuildAreas();

    /// <summary>
    /// Dynamic stamps on this character that the list doesn't have (new or unlisted NPCs), so the
    /// tab can show them and the count isn't only what the sheet knows.
    /// </summary>
    public static List<string> Unlisted(QuestFlagStore flags)
    {
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DynamicStamp s in All)
            if (s.MatchedFlag(flags) is string f) matched.Add(f);
        var extra = new List<string>();
        foreach (string key in flags.Flags.Keys)
            if (key.StartsWith(FlagPrefix, StringComparison.OrdinalIgnoreCase) && !matched.Contains(key))
                extra.Add(key);
        extra.Sort(StringComparer.OrdinalIgnoreCase);
        return extra;
    }

    private static List<DynamicStamp> Load()
    {
        var list = new List<DynamicStamp>();
        using StreamReader r = Csv.OpenEmbedded("dynstamps.csv");
        Dictionary<string, int> col = Csv.MapColumns(r.ReadLine() ?? "");
        int cArea = Csv.Column(col, "Area"), cPlace = Csv.Column(col, "Place"), cNpc = Csv.Column(col, "Npc"),
            cReward = Csv.Column(col, "Reward"), cStamp = Csv.Column(col, "Stamp"), cNote = Csv.Column(col, "Note");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (r.ReadLine() is string line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] f = Csv.ParseLine(line);
            string stamp = Csv.Field(f, cStamp).Trim().ToLowerInvariant();
            if (!stamp.StartsWith(FlagPrefix, StringComparison.Ordinal) || !seen.Add(stamp)) continue;
            list.Add(new DynamicStamp
            {
                Area = Csv.Field(f, cArea),
                Place = Csv.Field(f, cPlace),
                Npc = Csv.Field(f, cNpc),
                Reward = Csv.Field(f, cReward),
                Stamp = stamp,
                Note = Csv.Field(f, cNote),
            });
        }
        return list;
    }

    private static List<string> BuildAreas()
    {
        var areas = new List<string>();
        foreach (DynamicStamp s in All)
            if (!areas.Contains(s.Area)) areas.Add(s.Area);
        return areas;
    }
}
