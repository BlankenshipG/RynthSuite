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
//   3. its spell is a fixed recall the Atlas knows (Aerlinthe Recall...): the Atlas's landing.
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

    // gems.txt: server, wcid, name, where (map|dungeon), ns, ew, label — tab-separated, '#' comments.
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
              .Append(Clean(v.Spot.Label)).Append('\n');
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
        GemMemory learned, string server, double gemCostUnits, List<string>? notes)
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
                        spot = new RecallSpot { OnMap = r.OnMap, Ns = r.Ns, Ew = r.Ew, Label = r.Name };
                        from = r.Name;
                        break;
                    }
            if (spot == null) { notes?.Add($"{g.Name} (gem): where it lands isn't known yet (use one once)."); continue; }
            if (!spot.OnMap) { notes?.Add($"{g.Name} (gem) lands in a dungeon: not used for routes."); continue; }
            list.Add(new RecallOption
            {
                Name = g.Name + " (gem)", Command = UseCommand(g.Name), Ns = spot.Ns, Ew = spot.Ew,
                CastSeconds = GemUseSeconds, ExtraCostUnits = Math.Max(0, gemCostUnits),
                ItemWcid = g.Wcid, ItemName = g.Name, ItemCount = g.Count, Learned = from == "learned", Via = from,
            });
        }
        return list;
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
