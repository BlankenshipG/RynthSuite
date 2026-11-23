using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RynthCore.Plugin.RynthAi.Raycasting;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>
/// One spell of the client's spell table (portal.dat 0x0E00000E, ACE.DatLoader SpellBase): what
/// the Gear tab needs to tell which spells compete. <see cref="Category"/> is the stacking family
/// (ACE SpellCategory): enchantments of one family don't stack, the highest <see cref="Power"/> wins.
/// </summary>
internal readonly record struct PortalSpell(uint Id, string Name, uint School, uint Category, uint Bitfield,
                                            uint Power, uint MetaType, uint TargetType)
{
    /// <summary>SpellType Enchantment (1) or FellowEnchantment (12): the kinds that sit in an enchantment registry.</summary>
    public bool IsEnchantment => MetaType == 1 || MetaType == 12;

    /// <summary>
    /// Cast on an item (a bane on armour, Blood Drinker on a weapon), not on a creature: its
    /// NonComponentTargetType names item types and not Creature (ItemType 0x10).
    /// </summary>
    public bool IsItemTarget => TargetType != 0 && (TargetType & 0x10) == 0;
}

/// <summary>
/// An equipment set's bonus spells by tier (portal.dat SpellTable.SpellSet). The tier key is the
/// set's level: the number of pieces worn, or for leveled sets the sum of the pieces' item levels.
/// </summary>
internal sealed class SpellSetDef
{
    public readonly SortedDictionary<uint, uint[]> Tiers = new();

    public uint HighestTier => Tiers.Count == 0 ? 0 : LastKey();

    private uint LastKey()
    {
        uint last = 0;
        foreach (uint k in Tiers.Keys) last = k;
        return last;
    }

    /// <summary>
    /// The spells active at <paramref name="level"/>, as ACE's GetSpellSet: the level is capped at
    /// the highest tier and a level between two tiers takes the lower one (SpellSetTiersNoGaps).
    /// Level 0 (or below the first tier) has none.
    /// </summary>
    public uint[] SpellsAt(uint level)
    {
        uint tier = Math.Min(level, HighestTier);
        if (tier == 0) return Array.Empty<uint>();
        uint[]? best = null;
        foreach (var kv in Tiers)
        {
            if (kv.Key > tier) break;
            best = kv.Value;
        }
        return best ?? Array.Empty<uint>();
    }

    /// <summary>The tier key <see cref="SpellsAt"/> resolves to (for the label), or 0.</summary>
    public uint TierAt(uint level)
    {
        uint tier = Math.Min(level, HighestTier);
        uint found = 0;
        foreach (uint k in Tiers.Keys)
        {
            if (k > tier) break;
            found = k;
        }
        return tier == 0 ? 0 : found;
    }
}

/// <summary>
/// The client's own spell table and equipment-set spells, read once from portal.dat in the
/// background. Every server's client carries the spells it can show (custom ones included), so
/// this works on any server; SpellData.txt (retail) only fills in when the dat can't be read.
/// </summary>
internal sealed class PortalSpellBook
{
    public const uint SpellTableFileId = 0x0E00000E;

    /// <summary>The one book the plugin shares (the dat is read once per process).</summary>
    public static PortalSpellBook Shared { get; } = new();

    private volatile Dictionary<uint, PortalSpell>? _spells;
    private volatile Dictionary<uint, SpellSetDef>? _sets;
    private int _started;

    /// <summary>"" until the load ends; then what it found, or why it couldn't (for the tab and the log).</summary>
    public volatile string Status = "";

    /// <summary>True once the load has finished, whether or not the dat was read.</summary>
    public volatile bool Done;

    public bool HasDat => _spells != null;
    public int SpellCount => _spells?.Count ?? 0;
    public int SetCount => _sets?.Count ?? 0;

    /// <summary>Starts the one background read (any later call does nothing).</summary>
    public void EnsureLoading(Action<string>? log = null)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _ = Task.Run(() => Load(log));
    }

    /// <summary>Tests: a book from a hand-built table, already loaded.</summary>
    internal static PortalSpellBook FromParsed(Dictionary<uint, PortalSpell> spells, Dictionary<uint, SpellSetDef> sets)
    {
        var b = new PortalSpellBook { _spells = spells, _sets = sets, Done = true, _started = 1, Status = "test" };
        return b;
    }

    public bool TryGet(uint id, out PortalSpell spell)
    {
        var t = _spells;
        if (t != null && t.TryGetValue(id, out spell)) return true;
        spell = default;
        return false;
    }

    public bool TryGetSet(uint setId, out SpellSetDef set)
    {
        var t = _sets;
        if (t != null && t.TryGetValue(setId, out SpellSetDef? s)) { set = s; return true; }
        set = null!;
        return false;
    }

    private void Load(Action<string>? log)
    {
        try
        {
            string? path = FindPortalDat();
            if (path == null) { Status = "portal.dat not found next to the game client"; return; }
            using var dat = new DatDatabase();
            if (!dat.Open(path)) { Status = "portal.dat could not be opened"; return; }
            byte[]? raw = dat.GetFileData(SpellTableFileId);
            if (raw == null || raw.Length < 8) { Status = "the spell table isn't in portal.dat"; return; }
            var (spells, sets) = Parse(raw);
            _sets = sets;
            _spells = spells;
            Status = $"{spells.Count:N0} spells and {sets.Count:N0} equipment sets from {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            Status = $"spell table read failed: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            Done = true;
            log?.Invoke("[RynthOracle] Gear: " + Status);
        }
    }

    /// <summary>
    /// The SpellTable file: id, a PackedHashTable of SpellBase, then a PackedHashTable of SpellSet
    /// (ACE.DatLoader FileTypes/SpellTable.cs). Every SpellBase field is read so the table stays
    /// aligned; a cut-short table keeps what was read before the overrun.
    /// </summary>
    internal static (Dictionary<uint, PortalSpell> Spells, Dictionary<uint, SpellSetDef> Sets) Parse(byte[] raw)
    {
        var spells = new Dictionary<uint, PortalSpell>();
        var sets = new Dictionary<uint, SpellSetDef>();
        var r = new Reader(raw);
        r.U32();                                   // file id
        int total = r.U16();
        r.U16();                                   // bucket size
        for (int i = 0; i < total && r.Ok; i++)
        {
            uint id = r.U32();
            string name = r.ObfuscatedString();
            r.ObfuscatedString();                  // description
            uint school = r.U32();
            r.U32();                               // icon
            uint category = r.U32();
            uint bitfield = r.U32();
            r.U32();                               // base mana
            r.Skip(8);                             // base range constant, range mod
            uint power = r.U32();
            r.Skip(12);                            // economy mod, formula version, component loss
            uint meta = r.U32();
            r.U32();                               // meta spell id
            if (meta == 1 || meta == 12) r.Skip(16);   // duration (f64), degrade modifier, degrade limit
            else if (meta == 7) r.Skip(8);            // portal lifetime (f64)
            r.Skip(8 * 4);                         // the component formula
            r.Skip(12);                            // caster, target and fizzle effects
            r.Skip(8 + 4 + 4);                     // recovery interval (f64), recovery amount, display order
            uint target = r.U32();                 // NonComponentTargetType (ItemType)
            r.U32();                               // mana mod
            if (!r.Ok) break;
            spells[id] = new PortalSpell(id, name, school, category, bitfield, power, meta, target);
        }

        // SpellSet: setId -> PackedHashTable<tier, List<uint> spells>.
        if (r.Ok)
        {
            int setCount = r.U16();
            r.U16();
            for (int i = 0; i < setCount && r.Ok; i++)
            {
                uint setId = r.U32();
                var def = new SpellSetDef();
                int tiers = r.U16();
                r.U16();
                for (int t = 0; t < tiers && r.Ok; t++)
                {
                    uint tier = r.U32();
                    int n = (int)r.U32();
                    if (n < 0 || n > 4096) { r.Fail(); break; }
                    var list = new uint[n];
                    for (int k = 0; k < n; k++) list[k] = r.U32();
                    if (r.Ok) def.Tiers[tier] = list;
                }
                if (r.Ok) sets[setId] = def;
            }
        }
        return (spells, sets);
    }

    /// <summary>The client's portal.dat: beside acclient.exe (we run inside it), else the usual install folders.</summary>
    private static string? FindPortalDat()
    {
        var dirs = new List<string>();
        try
        {
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && Path.GetDirectoryName(exe) is string d) dirs.Add(d);
        }
        catch { /* no process path: the fixed folders below */ }
        dirs.Add(@"C:\Turbine\Asheron's Call");
        dirs.Add(@"C:\Program Files (x86)\Turbine\Asheron's Call");
        dirs.Add(@"C:\Program Files\Turbine\Asheron's Call");
        foreach (string dir in dirs)
            foreach (string name in new[] { "client_portal.dat", "portal.dat" })
            {
                string full = Path.Combine(dir, name);
                if (File.Exists(full)) return full;
            }
        return null;
    }

    /// <summary>Bounds-checked little-endian reader; <see cref="Ok"/> turns false on an overrun.</summary>
    private sealed class Reader
    {
        private readonly byte[] _b;
        private int _p;
        public bool Ok = true;

        public Reader(byte[] b) => _b = b;

        private bool Has(int n)
        {
            if (Ok && n >= 0 && _p + n <= _b.Length) return true;
            Ok = false;
            return false;
        }

        public void Fail() => Ok = false;
        public uint U32() { if (!Has(4)) return 0; uint v = BitConverter.ToUInt32(_b, _p); _p += 4; return v; }
        public ushort U16() { if (!Has(2)) return 0; ushort v = BitConverter.ToUInt16(_b, _p); _p += 2; return v; }
        public void Skip(int n) { if (Has(n)) _p += n; }

        /// <summary>u16 length, bytes with their nibbles swapped, then aligned to 4 from the file start (as ACE reads it).</summary>
        public string ObfuscatedString()
        {
            int n = U16();
            if (!Has(n)) return "";
            var chars = new char[n];
            for (int i = 0; i < n; i++)
            {
                byte x = _b[_p + i];
                chars[i] = (char)(byte)((x >> 4) | (x << 4));   // Latin-1: byte == code point
            }
            _p += n;
            _p = (_p + 3) & ~3;
            if (_p > _b.Length) Ok = false;
            return new string(chars);
        }
    }
}
