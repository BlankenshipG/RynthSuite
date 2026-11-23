using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthOracle.Data;

// The T11 Tracking tab's data (Infinite Leaftide, ACECustom upstream to 8d2460ff2, read 2026-11-11):
//   * item bounties: the character's slots R1 R2 B1 B2 from PropertyString 57702 ItemBountyDisplay, which the server keeps
//     current on the client ("v2|R1,R2,B1,B2;R1|tier|kind|progress|kills|target|rewardWcid|amount|icon|overlay|underlay|
//     bountyName|rewardName|rewardUse|placeShort|placeName;B2|empty"), and bounty items carried, not yet applied (their
//     description: "... Use it on a ring or bracelet you have to start it");
//   * the levelled charms (1-100, T11+): their level and the Prestige Coins put in, from the charm's appraisal ("Charm [Level
//     41 (95% to 42)]", "Prestige Coins put in: 14,210 - next level in 46"), and the Charm Infusion Gems carried (one gem
//     puts its number of coins in: Charm Infusion Gem (1) ... (5,000));
//   * the school gems (T15-T25, +1 to a school past the cap): Battlemage's Wrath (war), Nether Veil (void), Crashing Steel
//     (melee), True Shot (missile), with the tier each was earned at (PropertyInt 50651 EarnedGemTier) when the client has it;
//   * the lum tokens the bank takes from the pack: the ten Bounty Reward Tokens and the Event Lum Tokens (1B / 3B / 5B).

/// <summary>One item bounty slot (R1, R2, B1, B2) as ItemBountyDisplay gives it.</summary>
internal sealed record ItemBountySlot(string Key, bool Empty, int Tier, int Kind, double Progress, int Kills, string Target,
    uint RewardWcid, int RewardAmount, string Name, string RewardName, string RewardUse, string PlaceShort, string PlaceName)
{
    /// <summary>Kind 1: any kill in the place counts. Kind 2 (Species): only <see cref="Target"/>.</summary>
    public bool Species => Kind == 2;

    /// <summary>Kills still needed (0 when the count is hidden or done).</summary>
    public double Left => Kills > 0 ? Math.Max(0, Kills - Progress) : 0;

    /// <summary>How far along, 0-1 (0 when the kill count is a mystery until the bounty is applied).</summary>
    public float Fraction => Kills > 0 ? (float)Math.Clamp(Progress / Kills, 0, 1) : 0f;

    /// <summary>"Kill 250 Banderlings in Tier 15", "Kill 400 creatures in Valley of Death".</summary>
    public string Task => $"Kill {(Kills > 0 ? Kills.ToString("N0", CultureInfo.InvariantCulture) : "?")} {(Species && Target.Length > 0 ? Target : "creatures")} in {(PlaceName.Length > 0 ? PlaceName : "Tier " + Tier)}";

    /// <summary>"5 x Bounty Reward Token" (or the reward's name alone for one).</summary>
    public string Reward => RewardName.Length == 0 ? "" : RewardAmount > 1 ? $"{RewardAmount:N0} x {RewardName}" : RewardName;
}

/// <summary>A carried item the tab counts: what it is, how many, and what its appraisal says.</summary>
internal sealed record T11Item(uint Id, uint Wcid, string Name, int Stack, string Use, string Desc, int GemTier);

/// <summary>A levelled charm carried, with its level from the appraisal.</summary>
internal sealed record LevelledCharmRow(uint Id, string Name, int Level, int PercentToNext, long CoinsIn, long CoinsToNext, bool Active, bool Known);

internal static class T11Tracking
{
    /// <summary>PropertyString ItemBountyDisplay on the player.</summary>
    public const uint StrItemBountyDisplay = 57702;
    /// <summary>PropertyInt EarnedGemTier on a school gem.</summary>
    public const uint IntEarnedGemTier = 50651;

    /// <summary>The slot keys, in the order /bounty lists them.</summary>
    public static readonly string[] SlotKeys = { "R1", "R2", "B1", "B2" };

    /// <summary>Charm Infusion Gems (777700089-097): the coins one gem puts into a levelled charm.</summary>
    public static readonly IReadOnlyDictionary<uint, int> InfusionGems = new Dictionary<uint, int>
    {
        [777700089] = 1, [777700090] = 5, [777700091] = 10, [777700092] = 25, [777700093] = 50,
        [777700094] = 100, [777700095] = 500, [777700096] = 1_000, [777700097] = 5_000,
    };

    /// <summary>The school gems (SchoolGems.cs): one per school, reused at every tier.</summary>
    public static readonly (uint Wcid, string School, string Kind)[] SchoolGems =
    {
        (777700052, "Battlemage's Wrath", "War"),
        (777700057, "Nether Veil", "Void"),
        (777700062, "Crashing Steel", "Melee"),
        (777700067, "True Shot", "Missile"),
    };

    /// <summary>The levelled charms (777700035-040): level 1-100, T11+ only.</summary>
    public static readonly IReadOnlyDictionary<uint, string> LevelledCharms = new Dictionary<uint, string>
    {
        [777700035] = "Split Arrow", [777700036] = "Chain", [777700037] = "Pandemic",
        [777700038] = "Improved Cleave", [777700039] = "Armor Breaker", [777700040] = "Shield Arc",
    };

    /// <summary>The lum tokens the bank takes from the pack (a double click banks one): Bounty Reward Tokens and Event Lum Tokens.</summary>
    public static bool IsLumToken(uint wcid) => wcid is >= 777708001 and <= 777708010 or 99980076 or 99980078 or 99980079;

    /// <summary>An unapplied bounty item: its description ends with how to start it.</summary>
    public static bool IsBountyItem(string desc) =>
        desc.Contains("Use it on a ring or bracelet", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ItemBountyDisplay ("v2|R1,R2,B1,B2;R1|...;B2|empty") as slots; empty for no text, another format version, or junk.
    /// Fields past the ones known are ignored, missing trailing ones are blank.
    /// </summary>
    public static List<ItemBountySlot> ParseDisplay(string? text)
    {
        var list = new List<ItemBountySlot>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        string[] parts = text.Split(';');
        string[] head = parts[0].Split('|');
        if (head.Length < 1 || !head[0].StartsWith("v", StringComparison.Ordinal)) return list;
        for (int i = 1; i < parts.Length; i++)
        {
            string[] f = parts[i].Split('|');
            if (f.Length < 2 || f[0].Length == 0) continue;
            string key = f[0].Trim();
            if (f[1] == "empty") { list.Add(new ItemBountySlot(key, true, 0, 0, 0, 0, "", 0, 0, "", "", "", "", "")); continue; }
            string F(int n) => n < f.Length ? f[n] : "";
            int I(int n) => int.TryParse(F(n), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
            double progress = double.TryParse(F(3), NumberStyles.Float, CultureInfo.InvariantCulture, out double p) ? p : 0;
            uint reward = uint.TryParse(F(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint r) ? r : 0;
            list.Add(new ItemBountySlot(key, false, I(1), I(2), progress, I(4), F(5), reward, I(7), F(11), F(12), F(13), F(14), F(15)));
        }
        return list;
    }

    private static readonly Regex LevelText = new(@"Charm \[Level (\d+)(?: \((\d+)% to \d+\)| \(max\))?", RegexOptions.CultureInvariant);
    private static readonly Regex CoinsText = new(@"Prestige Coins put in: ([\d,]+)(?: - next level in ([\d,]+))?", RegexOptions.CultureInvariant);

    /// <summary>A levelled charm's appraisal: level, share to the next, coins put in, coins to the next; Known false without the text.</summary>
    public static (int Level, int Pct, long Coins, long ToNext, bool Known) ParseCharm(string text)
    {
        var m = LevelText.Match(text ?? "");
        if (!m.Success) return (0, 0, 0, 0, false);
        int level = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int pct = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        var c = CoinsText.Match(text ?? "");
        long coins = c.Success ? long.Parse(c.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture) : 0;
        long next = c.Success && c.Groups[2].Success ? long.Parse(c.Groups[2].Value.Replace(",", ""), CultureInfo.InvariantCulture) : 0;
        return (level, pct, coins, next, true);
    }
}

/// <summary>
/// Reads what the T11 Tracking tab shows from the client: the packs (two levels deep, as AC nests them) and the player's
/// bounty slots. Items whose appraisal the tab needs (charms, school gems, bounty items) and the client doesn't have yet are
/// asked about, a few at a time and not again for a while. Pump thread.
/// </summary>
internal sealed class T11Scanner
{
    private const long IdSpacingMs = 400, ReaskMs = 60_000;
    private readonly Dictionary<uint, long> _askedAt = new();
    private readonly Queue<uint> _idQueue = new();
    private long _nextIdAt;

    public List<ItemBountySlot> Slots { get; private set; } = new();
    public List<T11Item> Infusion { get; private set; } = new();
    public List<T11Item> SchoolGems { get; private set; } = new();
    public List<T11Item> Bounties { get; private set; } = new();
    public List<T11Item> LumTokens { get; private set; } = new();
    public List<LevelledCharmRow> Charms { get; private set; } = new();
    /// <summary>The player's raw ItemBountyDisplay ("" when the server never sent it).</summary>
    public string BountyText { get; private set; } = "";
    public long ScannedAt { get; private set; } = -1;
    /// <summary>Goes up when anything the tab shows changed.</summary>
    public int Revision { get; private set; }
    private string _lastKey = "";

    public long InfusionCoins => Infusion.Sum(g => (long)g.Stack * (T11Tracking.InfusionGems.TryGetValue(g.Wcid, out int c) ? c : 0));

    public void Clear()
    {
        Slots = new(); Infusion = new(); SchoolGems = new(); Bounties = new(); LumTokens = new(); Charms = new();
        BountyText = ""; ScannedAt = -1; _lastKey = ""; _askedAt.Clear(); _idQueue.Clear();
        Revision++;
    }

    /// <summary>Reads everything again; true when something changed.</summary>
    public bool Scan(RynthCoreHost host, long now, Func<uint, bool>? charmActive = null)
    {
        uint player = host.GetPlayerId();
        if (player == 0) return false;
        ScannedAt = now;
        string bounty = host.HasGetObjectStringProperty && host.TryGetObjectStringProperty(player, T11Tracking.StrItemBountyDisplay, out string b) ? b ?? "" : "";
        var infusion = new List<T11Item>();
        var gems = new List<T11Item>();
        var items = new List<T11Item>();
        var tokens = new List<T11Item>();
        var charms = new List<LevelledCharmRow>();
        foreach (uint id in Carried(host, player))
        {
            if (!host.HasGetObjectWcid || !host.TryGetObjectWcid(id, out uint wcid)) continue;
            bool infusionGem = T11Tracking.InfusionGems.ContainsKey(wcid);
            bool schoolGem = Array.Exists(T11Tracking.SchoolGems, g => g.Wcid == wcid);
            bool charm = T11Tracking.LevelledCharms.ContainsKey(wcid);
            bool token = T11Tracking.IsLumToken(wcid);
            string desc = Str(host, id, 16);
            bool bountyItem = !infusionGem && !schoolGem && !charm && !token && T11Tracking.IsBountyItem(desc);
            if (!infusionGem && !schoolGem && !charm && !token && !bountyItem)
            {
                // a bounty item the client hasn't been told about yet: a name with "Bounty" in it is asked about
                if (desc.Length == 0 && Name(host, id).Contains("Bounty", StringComparison.OrdinalIgnoreCase) && !Name(host, id).Contains("Token", StringComparison.OrdinalIgnoreCase))
                    Ask(id, now);
                continue;
            }
            string use = Str(host, id, 14);
            int stack = host.TryGetObjectIntProperty(id, 12, out int st) && st > 0 ? st : 1;
            int tier = host.TryGetObjectIntProperty(id, T11Tracking.IntEarnedGemTier, out int t) ? t : 0;
            var item = new T11Item(id, wcid, Name(host, id), stack, use, desc, tier);
            if (infusionGem) infusion.Add(item);
            else if (schoolGem) { gems.Add(item); if (use.Length == 0 && desc.Length == 0) Ask(id, now); }
            else if (token) tokens.Add(item);
            else if (bountyItem) items.Add(item);
            else
            {
                var (level, pct, coins, next, known) = T11Tracking.ParseCharm(use.Length > 0 ? use : desc);
                if (!known) Ask(id, now);
                bool active = (use + desc).Contains("Status: ON", StringComparison.OrdinalIgnoreCase) || charmActive?.Invoke(id) == true;
                charms.Add(new LevelledCharmRow(id, T11Tracking.LevelledCharms[wcid], level, pct, coins, next, active, known));
            }
        }
        infusion.Sort((a, x) => a.Wcid.CompareTo(x.Wcid));
        gems.Sort((a, x) => a.Wcid != x.Wcid ? a.Wcid.CompareTo(x.Wcid) : a.GemTier.CompareTo(x.GemTier));
        charms.Sort((a, x) => string.CompareOrdinal(a.Name, x.Name));

        string key = string.Join("#", bounty,
            string.Join(",", infusion.Select(i => i.Wcid + "x" + i.Stack)),
            string.Join(",", gems.Select(i => i.Id + "x" + i.Stack + "t" + i.GemTier)),
            string.Join(",", items.Select(i => i.Id + i.Desc)),
            string.Join(",", tokens.Select(i => i.Wcid + "x" + i.Stack)),
            string.Join(",", charms.Select(c => c.Id + ":" + c.Level + ":" + c.PercentToNext + ":" + c.CoinsIn + ":" + c.Active)));
        if (key == _lastKey) return false;
        _lastKey = key;
        BountyText = bounty;
        Slots = T11Tracking.ParseDisplay(bounty);
        Infusion = infusion; SchoolGems = gems; Bounties = items; LumTokens = tokens; Charms = charms;
        Revision++;
        return true;
    }

    /// <summary>One queued appraisal request, spaced out (pump thread, each tick).</summary>
    public void Tick(RynthCoreHost host, long now)
    {
        if (_idQueue.Count == 0 || now < _nextIdAt || !host.HasRequestId) return;
        host.RequestId(_idQueue.Dequeue());
        _nextIdAt = now + IdSpacingMs;
    }

    /// <summary>Forgets when the items were asked about, so Refresh asks again at once.</summary>
    public void ForgetAsked() => _askedAt.Clear();

    private void Ask(uint id, long now)
    {
        if (_askedAt.TryGetValue(id, out long at) && now - at < ReaskMs) return;
        _askedAt[id] = now;
        _idQueue.Enqueue(id);
    }

    private static string Str(RynthCoreHost host, uint id, uint key) =>
        host.HasGetObjectStringProperty && host.TryGetObjectStringProperty(id, key, out string s) ? s ?? "" : "";

    private static string Name(RynthCoreHost host, uint id) => host.TryGetObjectName(id, out string n) ? n ?? "" : "";

    /// <summary>Every item in the main pack and the packs inside it.</summary>
    private static List<uint> Carried(RynthCoreHost host, uint player)
    {
        var list = new List<uint>();
        var buf = new uint[512];
        var packs = new List<uint>();
        int n = host.GetContainerContents(player, buf);
        for (int i = 0; i < n; i++)
        {
            uint id = buf[i];
            if (id == 0) continue;
            if (host.TryGetItemType(id, out uint type) && (type & GearAnalysis.ItemContainer) != 0) packs.Add(id);
            else list.Add(id);
        }
        foreach (uint pack in packs)
        {
            int m = host.GetContainerContents(pack, buf);
            for (int i = 0; i < m; i++) if (buf[i] != 0) list.Add(buf[i]);
        }
        return list;
    }
}
