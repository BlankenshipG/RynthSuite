using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RynthCore.Plugin.RynthNav;

// Portal gems in your pack (2026-10-29): an item whose use casts a teleport (a recall gem, a portal
// gem, a custom server's town gem) is a recall the route planner can take, used with UseObject, no
// magic stance. Where one lands:
//   1. learned in game: you (or a route) used one and a teleport followed (GemMemory, per server and
//      weenie class, so the same gem on another character is known too);
//   2. its spell is a personal recall (Lifestone Recall, Portal Recall, a portal tie recall): the
//      character's own landing (RecallMemory);
//   3. its spell is a fixed recall the Atlas knows (Aerlinthe Recall...): the Atlas's landing;
//   4. guessed from its name: "Abandoned Mines Portal Sending Gem" is the Atlas's dungeon "Abandoned
//      Mine", landing where the world data's portal into it does; "Timaru Portal Sending Gem" the town.
//      Using one teaches its real landing, which wins from then on.
// A gem landing in a dungeon is routed when RynthNav has learned walks there (TownNet.Merge): the
// planner lands at that dungeon's arrival and goes on by its portals.
// A gem costs the planner its use plus GemCostUnits on top (rynthnav.json "gemCostYards"), so the
// same recall cast as a spell, which spends nothing, comes first, and a gem is only spent when it
// saves real walking. Saved in NavData\gems.txt. No host calls: the offline tests compile this file.

/// <summary>One kind of usable teleport item in the pack (stacks of the same weenie class added up).</summary>
internal sealed class PackGem
{
    public uint Id;          // one object of it (the one used)
    public uint Wcid;
    public string Name = "";
    public int SpellId;      // its use spell (DID Spell), 0 = not known
    public int Count;        // how many, stacks added up
}

/// <summary>Where gems land, learned in game: per server and weenie class.</summary>
internal sealed class GemMemory
{
    private readonly Dictionary<string, (string Name, RecallSpot Spot)> _map = new(StringComparer.OrdinalIgnoreCase);
    public int Version { get; private set; }
    public int Count => _map.Count;

    private static string Key(string server, uint wcid) =>
        (string.IsNullOrWhiteSpace(server) ? "?" : server.Trim()) + "|" + wcid.ToString(CultureInfo.InvariantCulture);

    public RecallSpot? Get(string server, uint wcid) => _map.TryGetValue(Key(server, wcid), out var v) ? v.Spot : null;

    public void Set(string server, uint wcid, string name, RecallSpot spot)
    {
        _map[Key(server, wcid)] = (Clean(name), spot);
        Version++;
    }

    public bool Forget(string server, uint wcid)
    {
        bool gone = _map.Remove(Key(server, wcid));
        if (gone) Version++;
        return gone;
    }

    public IEnumerable<(uint Wcid, string Name, RecallSpot Spot)> For(string server)
    {
        string prefix = (string.IsNullOrWhiteSpace(server) ? "?" : server.Trim()) + "|";
        foreach (var (k, v) in _map)
            if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(k.AsSpan(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint w))
                yield return (w, v.Name, v.Spot);
    }

    private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();

    // gems.txt: server, wcid, name, where (map|dungeon), ns, ew, label, cell (hex), x, y, z (in a dungeon; - = not known)
    // — tab-separated, '#' comments.
    public string Serialize()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("# RynthNav: where portal gems land, per server and weenie class (learned by using them)\n");
        foreach (var (k, v) in _map)
        {
            int bar = k.LastIndexOf('|');
            sb.Append(k, 0, bar).Append('\t').Append(k, bar + 1, k.Length - bar - 1).Append('\t').Append(v.Name).Append('\t')
              .Append(v.Spot.OnMap ? "map" : "dungeon").Append('\t')
              .Append(v.Spot.Ns.ToString("0.##", ci)).Append('\t').Append(v.Spot.Ew.ToString("0.##", ci)).Append('\t')
              .Append(Clean(v.Spot.Label)).Append('\t').Append(v.Spot.Cell.ToString("X8", ci)).Append('\t')
              .Append(v.Spot.HasPos ? v.Spot.X.ToString("0.##", ci) + "\t" + v.Spot.Y.ToString("0.##", ci) + "\t" + v.Spot.Z.ToString("0.##", ci) : "-\t-\t-")
              .Append('\n');
        }
        return sb.ToString();
    }

    public static GemMemory Parse(string? text)
    {
        var ci = CultureInfo.InvariantCulture;
        var m = new GemMemory();
        foreach (string raw in (text ?? "").Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;
            string[] f = line.Split('\t');
            if (f.Length < 6 || !uint.TryParse(f[1], NumberStyles.Integer, ci, out uint wcid)) continue;
            var spot = new RecallSpot { OnMap = f[3].Trim().Equals("map", StringComparison.OrdinalIgnoreCase), Label = f.Length > 6 ? f[6] : "" };
            if (spot.OnMap && (!double.TryParse(f[4], NumberStyles.Float, ci, out spot.Ns) || !double.TryParse(f[5], NumberStyles.Float, ci, out spot.Ew))) continue;
            if (f.Length > 7) uint.TryParse(f[7], NumberStyles.HexNumber, ci, out spot.Cell);
            if (f.Length > 10 && double.TryParse(f[8], NumberStyles.Float, ci, out spot.X) && double.TryParse(f[9], NumberStyles.Float, ci, out spot.Y))
            {
                double.TryParse(f[10], NumberStyles.Float, ci, out spot.Z);
                spot.HasPos = true;
            }
            m._map[Key(f[0], wcid)] = (f[2].Trim(), spot);
        }
        m.Version = 0;
        return m;
    }
}

internal static class GemPlanner
{
    /// <summary>Using a gem: no stance, no wand, a short animation. Estimated.</summary>
    public const double GemUseSeconds = 3.0;
    /// <summary>On top of the use: a gem is spent (rynthnav.json "gemCostYards"; 1 /loc degree = 240).</summary>
    public const double GemCostUnitsDefault = 240.0;

    /// <summary>The chat command "save as nav route" writes for a gem (RynthAi's /mt usei: use from inventory by name).</summary>
    public static string UseCommand(string name) => "/mt usei " + name;

    /// <summary>Which recall spell a gem's spell is for the character's own landings, or null.</summary>
    public static RecallSlot? PersonalSlot(int spellId) => spellId switch
    {
        RecallPlanner.LifestoneRecall => RecallSlot.Lifestone,
        RecallPlanner.PortalRecall => RecallSlot.LastPortal,
        RecallPlanner.PrimaryPortalTieRecall => RecallSlot.Tie1,
        RecallPlanner.SecondaryPortalTieRecall => RecallSlot.Tie2,
        _ => null,
    };

    /// <summary>
    /// The pack's gems as recalls the planner may take (one per weenie class), each with where it
    /// lands; a gem whose landing isn't known, or is in a dungeon, is left out with a note.
    /// </summary>
    public static List<RecallOption> Build(IReadOnlyList<PackGem> gems, Atlas atlas, RecallMemory memory, string charKey,
        GemMemory learned, string server, double gemCostUnits, List<string>? notes, ArrivalResolver? arrivalOf = null)
    {
        var list = new List<RecallOption>();
        var seen = new HashSet<uint>();
        foreach (PackGem g in gems)
        {
            if (g.Count <= 0 || !seen.Add(g.Wcid)) continue;
            RecallSpot? spot = learned.Get(server, g.Wcid);
            string from = "learned";
            if (spot == null && g.SpellId != 0 && PersonalSlot(g.SpellId) is RecallSlot slot)
            {
                spot = memory.Get(charKey, slot);
                from = "your " + RecallMemory.SlotName(slot);
                if (spot == null) { notes?.Add($"{g.Name} (gem): casts {RecallMemory.SlotName(slot)}, whose landing isn't known yet."); continue; }
            }
            if (spot == null && g.SpellId != 0)
                foreach (AtlasRecall r in atlas.Recalls)
                    if (r.SpellId == g.SpellId)
                    {
                        spot = new RecallSpot { OnMap = r.OnMap, Ns = r.Ns, Ew = r.Ew, Label = r.OnMap ? r.Name : (r.In.Length > 0 ? r.In : r.Name), Cell = r.Cell };
                        from = r.Name;
                        break;
                    }
            if (spot == null && GuessFromName(g.Name, atlas, out RecallSpot? guess, out string what))
            {
                spot = guess;
                from = "guessed from its name: " + what;
            }
            if (spot == null) { notes?.Add($"{g.Name} (gem): where it lands isn't known yet (use one once)."); continue; }
            var o = new RecallOption
            {
                Name = g.Name + " (gem)", Command = UseCommand(g.Name), Ns = spot.Ns, Ew = spot.Ew,
                CastSeconds = GemUseSeconds, ExtraCostUnits = Math.Max(0, gemCostUnits),
                ItemWcid = g.Wcid, ItemName = g.Name, ItemCount = g.Count, Learned = from == "learned", Via = from,
            };
            if (!spot.OnMap)
            {
                int a = spot.Cell != 0 && arrivalOf != null ? arrivalOf(spot.Cell, spot.HasPos, spot.X, spot.Y, spot.Z) : -1;
                if (a < 0)
                {
                    notes?.Add($"{g.Name} (gem) lands in {(spot.Label.Length > 0 ? spot.Label : "a dungeon")}: not used for routes until RynthNav has learned a way out of it (walk to one of its portals and take it).");
                    continue;
                }
                o.Ns = o.Ew = 0;
                o.ToArrival = a;
                o.Into = spot.Label.Length > 0 ? spot.Label : $"dungeon {spot.Cell >> 16:X4}";
            }
            list.Add(o);
        }
        return list;
    }

    // Words a gem's name adds to the place it sends you to.
    private static readonly string[] GemWords = { "gem", "gems", "portal", "sending", "recall", "orb", "everlasting", "summoning", "attuned" };
    private static readonly string[] GemLeadWords = { "unlimited", "infinite", "everlasting", "attuned" };

    /// <summary>The place a gem's name points at: "Abandoned Mines Portal Sending Gem" → "Abandoned Mines".</summary>
    public static string PlaceInName(string gemName)
    {
        var words = new List<string>((gemName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        while (words.Count > 0 && Array.IndexOf(GemWords, words[^1].ToLowerInvariant()) >= 0) words.RemoveAt(words.Count - 1);
        while (words.Count > 0 && Array.IndexOf(GemLeadWords, words[0].ToLowerInvariant()) >= 0) words.RemoveAt(0);
        if (words.Count > 0 && words[0].Equals("to", StringComparison.OrdinalIgnoreCase)) words.RemoveAt(0);
        return string.Join(" ", words);
    }

    /// <summary>A name for matching: lower case, letters and digits only, each word's trailing s dropped ("Mines" = "Mine").</summary>
    public static string Norm(string s)
    {
        var sb = new StringBuilder();
        foreach (string w0 in (s ?? "").ToLowerInvariant().Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var w = new StringBuilder();
            foreach (char c in w0) if (char.IsLetterOrDigit(c)) w.Append(c);
            string x = w.ToString();
            if (x.Length > 3 && x.EndsWith('s')) x = x[..^1];
            if (x.Length == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(x);
        }
        return sb.ToString();
    }

    /// <summary>
    /// A gem's landing guessed from its name and the Atlas: a dungeon of that name (where the world
    /// data's portal into it lands: its cell), else a town of that name (the town's spot). False when
    /// the name matches nothing, or several dungeon landings.
    /// </summary>
    public static bool GuessFromName(string gemName, Atlas atlas, out RecallSpot? spot, out string what)
    {
        spot = null; what = "";
        string place = PlaceInName(gemName);
        string key = Norm(place);
        if (key.Length < 3) return false;
        foreach (AtlasLocation d in atlas.Locations)
        {
            if (!d.Type.Equals("Dungeon", StringComparison.Ordinal) || Norm(d.Name) != key) continue;
            // Where the world data's portal into it lands: a portal whose landing is named this dungeon, in its landblock.
            uint cell = 0;
            foreach (AtlasLocation p in atlas.Locations)
            {
                if (!p.Type.Equals("Portal", StringComparison.Ordinal) || !p.HasDest || p.DestOnMap) continue;
                if (!uint.TryParse(p.DestCell, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint dc)) continue;
                if (!d.Lb.Equals(((dc >> 16) & 0xFFFF).ToString("X4", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)) continue;
                if (p.OnMap || Norm(p.DestName) == key) { cell = dc; if (p.OnMap) break; }
            }
            if (cell == 0) continue;
            spot = new RecallSpot { OnMap = false, Cell = cell, Label = d.Name };
            what = d.Name;
            return true;
        }
        foreach (AtlasLocation tl in atlas.Locations)
        {
            if (!tl.Type.Equals("Town", StringComparison.Ordinal) || !tl.OnMap || Norm(tl.Name) != key) continue;
            spot = new RecallSpot { OnMap = true, Ns = tl.Ns, Ew = tl.Ew, Label = tl.Name };
            what = tl.Name;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Worth watching as a possible teleport gem: a gem (item type), or anything whose use spell is a
    /// recall, or whose name says so. Used to keep the pack's list (and spot one being used up).
    /// </summary>
    public static bool LooksLikeTeleportItem(uint itemType, int spellId, string name, Atlas atlas, GemMemory learned, string server, uint wcid)
    {
        if (learned.Get(server, wcid) != null) return true;
        if (spellId == 0) return false;
        if ((itemType & ItemTypeGem) != 0) return true;
        if (PersonalSlot(spellId) != null) return true;
        foreach (AtlasRecall r in atlas.Recalls) if (r.SpellId == spellId) return true;
        return name.IndexOf("portal", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("recall", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("teleport", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("sending", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public const uint ItemTypeGem = 0x00000800;
}
