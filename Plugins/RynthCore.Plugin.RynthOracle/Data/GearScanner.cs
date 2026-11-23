using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>
/// The Gear tab's Check: reads the character's worn items (and, for the Ratings view, the packs),
/// gets each one appraised so its spells and properties are known, waits for the answers and for
/// the client's spell table, then builds the Overlap and Ratings reports. Nothing here is
/// server-specific: the items, their appraisals and the character's enchantments come from the
/// client, the spell facts from its portal.dat. Pump thread only.
/// </summary>
internal sealed class GearScanner
{
    public enum Phase { Idle, Identifying, Settling, WaitingForSpells, Done }

    /// <summary>One RequestId this often, so a full pack doesn't flood the server.</summary>
    private const long IdSpacingMs = 150;
    /// <summary>After the last RequestId, how long the answers get to arrive.</summary>
    private const long SettleMs = 1500;
    /// <summary>An item asked about within this long isn't asked about again (one with no spells never caches a list).</summary>
    private const long ReaskMs = 5 * 60_000;
    /// <summary>At most this many pack items are asked about in one Check.</summary>
    private const int MaxPackIds = 200;

    public Phase State { get; private set; } = Phase.Idle;
    public GearReport? Overlap { get; private set; }
    public RatingsReport? Ratings { get; private set; }
    /// <summary>Goes up when the state or a report changes (the window re-records).</summary>
    public int Revision { get; private set; }
    /// <summary>Why the last Check couldn't run, or "".</summary>
    public string Problem { get; private set; } = "";
    public int IdsQueued { get; private set; }
    public int IdsSent { get; private set; }

    /// <summary>The book the reports read spells from (tests put their own in).</summary>
    internal PortalSpellBook Book = PortalSpellBook.Shared;

    private readonly Queue<uint> _idQueue = new();
    private readonly Dictionary<uint, long> _askedAt = new();
    private List<uint> _equipped = new();
    private List<uint> _pack = new();
    private bool _includePack;
    private long _nextIdAt, _settleUntil;
    private Action<string>? _log;

    public bool Busy => State is Phase.Identifying or Phase.Settling or Phase.WaitingForSpells;

    /// <summary>Tests: show reports built by hand, as if a Check had just finished.</summary>
    internal void UseReports(GearReport? overlap, RatingsReport? ratings)
    {
        Overlap = overlap;
        Ratings = ratings;
        State = Phase.Done;
        Revision++;
    }

    /// <summary>Forget everything (logout): the next character starts with no report.</summary>
    public void Clear()
    {
        State = Phase.Idle;
        Overlap = null;
        Ratings = null;
        Problem = "";
        _idQueue.Clear();
        _askedAt.Clear();
        _equipped.Clear();
        _pack.Clear();
        IdsQueued = IdsSent = 0;
        Revision++;
    }

    /// <summary>
    /// Starts a Check: lists the items, queues an appraisal for each one the client hasn't seen
    /// appraised (or asked about lately), and starts the spell table read. False with
    /// <see cref="Problem"/> set when there is nothing to read from.
    /// </summary>
    public bool Start(RynthCoreHost host, long now, bool includePack, Action<string>? log = null)
    {
        _log = log;
        _includePack = includePack;
        Problem = "";
        Book.EnsureLoading(log);
        uint player = host.GetPlayerId();
        if (player == 0) { Problem = "Log in first."; Revision++; return false; }

        _equipped = WornItems(host, player);
        _pack = includePack ? PackItems(host, player) : new List<uint>();
        if (_equipped.Count == 0 && _pack.Count == 0)
        {
            Problem = host.HasGetWieldedItems || host.GetLiveObjectIds().Length > 0
                ? "Nothing worn or wielded was found."
                : "This RynthCore engine can't list worn items (it needs engine API 69 or newer).";
            State = Phase.Idle;
            Revision++;
            return false;
        }

        _idQueue.Clear();
        var buf = new uint[1];
        int packAsked = 0;
        foreach (uint id in _equipped) QueueIfNeeded(host, id, now, buf);
        foreach (uint id in _pack)
        {
            if (packAsked >= MaxPackIds) break;
            if (QueueIfNeeded(host, id, now, buf)) packAsked++;
        }
        IdsQueued = _idQueue.Count;
        IdsSent = 0;
        _nextIdAt = now;
        State = _idQueue.Count > 0 ? Phase.Identifying : Phase.WaitingForSpells;
        _settleUntil = now;
        Revision++;
        return true;
    }

    private bool QueueIfNeeded(RynthCoreHost host, uint id, long now, uint[] buf)
    {
        if (host.GetObjectSpellIds(id, buf, buf.Length) >= 0) return false;   // already appraised this session
        if (_askedAt.TryGetValue(id, out long at) && now - at < ReaskMs) return false;
        _idQueue.Enqueue(id);
        return true;
    }

    /// <summary>Moves the Check along; true when something the tab shows changed.</summary>
    public bool Tick(RynthCoreHost host, long now, string characterName)
    {
        switch (State)
        {
            case Phase.Identifying:
                if (now < _nextIdAt) return false;
                if (_idQueue.Count > 0)
                {
                    uint id = _idQueue.Dequeue();
                    if (host.HasRequestId) host.RequestId(id);
                    _askedAt[id] = now;
                    IdsSent++;
                    _nextIdAt = now + IdSpacingMs;
                    Revision++;
                    return true;
                }
                State = Phase.Settling;
                _settleUntil = now + SettleMs;
                Revision++;
                return true;
            case Phase.Settling:
                if (now < _settleUntil) return false;
                State = Phase.WaitingForSpells;
                Revision++;
                return true;
            case Phase.WaitingForSpells:
                if (!Book.Done) return false;
                Build(host, now, characterName);
                State = Phase.Done;
                Revision++;
                return true;
            default:
                return false;
        }
    }

    private void Build(RynthCoreHost host, long now, string characterName)
    {
        var worn = new List<GearPiece>();
        var all = new List<GearPiece>();
        var seen = new HashSet<uint>();
        var locations = new Dictionary<uint, uint>();
        if (host.TryGetWieldedItems(out var wielded))
            foreach (var (id, loc) in wielded) locations[id] = loc;
        foreach (uint id in _equipped)
        {
            if (!seen.Add(id)) continue;
            GearPiece p = Read(host, id, now, equipped: true, locations.TryGetValue(id, out uint l) ? l : 0);
            worn.Add(p);
            all.Add(p);
        }
        foreach (uint id in _pack)
            if (seen.Add(id)) all.Add(Read(host, id, now, equipped: false, 0));

        uint[] buffs = PlayerBuffs(host);
        DateTime local = DateTime.Now;
        Overlap = GearAnalysis.Analyze(worn, buffs, Lookup, SetOf, characterName, local);
        Ratings = GearAnalysis.Ratings(all, _includePack, local, Lookup);
        _log?.Invoke($"[RynthOracle] Gear check: {worn.Count} worn, {Overlap.SpellSources} spells, "
                     + $"{Overlap.Count(OverlapKind.Overlap)} overlap, {Overlap.Count(OverlapKind.Duplicate)} duplicate; "
                     + $"{Ratings.RatedRows.Count()} rated item(s), {Ratings.T11Rows} with T11 modifiers; spells from {(Book.HasDat ? "portal.dat" : "SpellData.txt (" + Book.Status + ")")}.");
    }

    /// <summary>The spell facts: the client's spell table, else SpellData.txt, else only the id.</summary>
    internal SpellMeta Lookup(uint id)
    {
        if (Book.TryGet(id, out PortalSpell s))
            return new SpellMeta(id, s.Name, s.Category, s.Power, s.IsEnchantment, s.IsItemTarget, true);
        if (SpellTable.TryGet(id, out SpellInfo info))
        {
            uint cat = (uint)Math.Max(0, info.Category);
            return new SpellMeta(id, info.Name, cat, (uint)Math.Max(0, info.Power), true, ItemFamilyGuess(cat), true);
        }
        return SpellMeta.Unknown(id);
    }

    private SpellSetDef? SetOf(int setId) => Book.TryGetSet((uint)setId, out SpellSetDef d) ? d : null;

    /// <summary>
    /// Without portal.dat there is no target type: the families cast on items (weapon buffs, armour,
    /// banes) by name, so their per-item rows still form.
    /// </summary>
    private static bool ItemFamilyGuess(uint category)
    {
        string fam = GearNames.FamilyRaw(category);
        if (fam.StartsWith("Extra", StringComparison.Ordinal) && fam.Contains("ResistanceRaising", StringComparison.Ordinal)) return true;
        return fam.StartsWith("DamageRaising", StringComparison.Ordinal) || fam.StartsWith("AttackModRaising", StringComparison.Ordinal)
            || fam.StartsWith("DefenseModRaising", StringComparison.Ordinal) || fam.StartsWith("WeaponTimeRaising", StringComparison.Ordinal)
            || fam.StartsWith("ManaConversionModRaising", StringComparison.Ordinal) || fam.StartsWith("SpellDamageRaising", StringComparison.Ordinal)
            || fam.StartsWith("ArmorValueRaising", StringComparison.Ordinal);
    }

    private GearPiece Read(RynthCoreHost host, uint id, long now, bool equipped, uint location)
    {
        var p = new GearPiece { Id = id, Equipped = equipped, Location = location };
        p.Name = host.TryGetObjectName(id, out string name) && name.Length > 0 ? name : $"Item 0x{id:X8}";
        if (host.TryGetItemType(id, out uint type)) p.ItemType = type;

        var buf = new uint[128];
        int n = host.GetObjectSpellIds(id, buf, buf.Length);
        // No cached list: never appraised, or appraised with no spells (the engine keeps no empty list).
        p.Appraised = n >= 0 || (_askedAt.TryGetValue(id, out long at) && now - at >= SettleMs);
        if (n > 0)
        {
            var inscribed = new List<uint>();
            var onItem = new List<uint>();
            for (int i = 0; i < Math.Min(n, buf.Length); i++)
            {
                uint raw = buf[i];
                if (raw == 0) continue;
                if ((raw & 0x8000_0000u) != 0) onItem.Add(raw & 0x7FFF_FFFFu);
                else inscribed.Add(raw);
            }
            p.Inscribed = inscribed.ToArray();
            p.OnItem = onItem.ToArray();
        }

        p.SetId = Int(host, id, 265);
        p.MaxLevel = Int(host, id, 319);
        p.XpStyle = Int(host, id, 320);
        p.DamageType = Int(host, id, 45);
        p.BaseXp = host.TryGetObjectQuadProperty(id, 5, out long b) ? b : 0;
        p.TotalXp = host.TryGetObjectQuadProperty(id, 4, out long t) ? t : 0;
        var ratings = new List<(int, int)>();
        foreach (var (prop, _, _) in GearAnalysis.RatingProps)
        {
            int v = Int(host, id, (uint)prop);
            if (v != 0) ratings.Add((prop, v));
        }
        p.Ratings = ratings.ToArray();

        // T11 modifier lines: the appraisal's "Modifiers:" section, in the Use text (14) on armour
        // and jewelry, in the long description (16) on weapons.
        string use = host.TryGetObjectStringProperty(id, 14, out string u) ? u : "";
        string longDesc = host.TryGetObjectStringProperty(id, 16, out string d) ? d : "";
        (p.Modifiers, p.ZoneLocked) = T11Modifiers.Parse(use, longDesc);

        // The Mag-style line's properties (only the ones the appraisal has).
        foreach (int prop in MagFormat.IntProps)
            if (host.TryGetObjectIntProperty(id, (uint)prop, out int iv)) p.Ints[prop] = iv;
        foreach (int prop in MagFormat.FloatProps)
            if (host.TryGetObjectDoubleProperty(id, (uint)prop, out double fv)) p.Floats[prop] = fv;
        return p;
    }

    private static int Int(RynthCoreHost host, uint id, uint stype) =>
        host.TryGetObjectIntProperty(id, stype, out int v) ? v : 0;

    /// <summary>
    /// What the character wears and wields: the engine's equipment record (API 87), else every
    /// object the client knows whose wielder is the character.
    /// </summary>
    private static List<uint> WornItems(RynthCoreHost host, uint player)
    {
        var list = new List<uint>();
        if (host.TryGetWieldedItems(out var items))
        {
            foreach (var (id, _) in items) if (id != 0 && !list.Contains(id)) list.Add(id);
            if (list.Count > 0) return list;
        }
        foreach (uint id in host.GetLiveObjectIds())
            if (host.TryGetObjectWielderInfo(id, out uint wielder, out uint loc) && wielder == player && loc != 0 && !list.Contains(id))
                list.Add(id);
        return list;
    }

    /// <summary>The wearables in the main pack and the packs inside it (one level, as AC nests them).</summary>
    private static List<uint> PackItems(RynthCoreHost host, uint player)
    {
        var list = new List<uint>();
        var buf = new uint[512];
        var packs = new List<uint>();
        int n = host.GetContainerContents(player, buf);
        for (int i = 0; i < n; i++) Consider(host, buf[i], list, packs);
        foreach (uint pack in packs)
        {
            int m = host.GetContainerContents(pack, buf);
            for (int i = 0; i < m; i++) Consider(host, buf[i], list, null);
        }
        return list;
    }

    private static void Consider(RynthCoreHost host, uint id, List<uint> items, List<uint>? packs)
    {
        if (id == 0 || !host.TryGetItemType(id, out uint type)) return;
        if ((type & GearAnalysis.ItemContainer) != 0) { packs?.Add(id); return; }
        if ((type & GearAnalysis.ItemWearables) != 0 && !items.Contains(id)) items.Add(id);
    }

    /// <summary>The character's enchantments by spell id (life wards, buffs, worn items' cantrips).</summary>
    private static uint[] PlayerBuffs(RynthCoreHost host)
    {
        var ids = new uint[256];
        var exp = new double[256];
        int n = host.ReadPlayerEnchantments(ids, exp, ids.Length);
        if (n <= 0) return Array.Empty<uint>();
        var list = new List<uint>(n);
        for (int i = 0; i < Math.Min(n, ids.Length); i++) if (ids[i] != 0) list.Add(ids[i]);
        return list.ToArray();
    }
}
