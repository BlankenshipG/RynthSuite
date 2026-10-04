// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>A labelled value from a server reply ("Pyreals" = "1,039,678,533 (4,158 MMDs)").</summary>
internal sealed class NamedValue
{
    public string Name = "";
    public string Value = "";
}

/// <summary>One of ConquestAC's advanced augmentations (counts from "/augs").</summary>
internal sealed class ConquestAug
{
    public readonly string Name;
    public readonly int CoinCost;
    public readonly double LumBase, LumPercent;
    public int Count;

    public ConquestAug(string name, int coinCost, double lumBase, double lumPercent)
    {
        Name = name; CoinCost = coinCost; LumBase = lumBase; LumPercent = lumPercent;
    }

    /// <summary>The next purchase's luminance (Conquest-ACE's tiered cost, as upstream mirrors it).</summary>
    public long NextLuminanceCost()
    {
        long idx = Count;
        (double tierBase, long pos) = idx >= 65 ? (LumBase * 30.0, idx - 65)
            : idx >= 60 ? (LumBase * 12.0, idx - 60)
            : idx >= 30 ? (LumBase * 4.8, idx - 30)
            : idx >= 15 ? (LumBase * 2.4, idx - 15)
            : (LumBase, idx);
        // The server truncates to long; the epsilon keeps 6,229,499.9999 (binary floating point) at the
        // confirmed in-game 6,229,500 (Creature at 25).
        return (long)(tierBase * (1.0 + pos * LumPercent) + 1e-6);
    }

    public string NextCostText() =>
        $"{CoinCost} coins, {(NextLuminanceCost() / 1_000_000.0).ToString("0.##", CultureInfo.InvariantCulture)}M lum";

    private double ResistPercent()
    {
        const double MaxBonus = 0.32, LinearRate = 0.003, Tuning = 0.0034597;
        const int LinearCap = 10;
        if (Count <= 0) return 0.0;
        double bonus = Count <= LinearCap
            ? Count * LinearRate
            : LinearCap * LinearRate + (MaxBonus - LinearCap * LinearRate) * (1.0 - Math.Pow(1.0 - Tuning, Count - LinearCap));
        return Math.Min(bonus, MaxBonus) * 100.0;
    }

    /// <summary>The effect at the current count (upstream's reading of the Conquest-ACE source).</summary>
    public string Effect() => Name switch
    {
        "Creature" => $"+{Count} to creature buffs, -{Count} to debuffs",
        "Item" => $"+{Count}% attack/melee, +{Count * 0.5:0.#} blood/spirit, +{Count} AL",
        "Life" => $"+{ResistPercent():0.##}% prot, +{ResistPercent():0.##}% vuln, +{Count * 0.10:0.##} regen, +{Count / 10} surge rating",
        "War" => $"+{Count * 2}% war magic potency",
        "Void" => $"+{Count * 1.5:0.##}% void magic potency",
        "Melee" => $"+{Count} melee damage, +{Count * 1.5:0.#}% crit damage",
        "Missile" => $"+{Count} missile damage, +{Count * 1.5:0.#}% crit damage",
        "Duration" => $"+{Count * 5}% spell duration",
        "Specialization" => $"+{Count} skill spec cap (now {70 + Count})",
        _ => "",
    };
}

/// <summary>A fellowship from "/fship list".</summary>
internal sealed class ConquestFellowship
{
    public string Name = "", Leader = "", Members = "", Location = "";

    public int MemberCount
    {
        get
        {
            int slash = Members.IndexOf('/');
            return int.TryParse(slash >= 0 ? Members[..slash] : Members, out int n) ? n : 0;
        }
    }
}

/// <summary>A row of a leaderboard.</summary>
internal sealed class BoardRow
{
    public int Rank;
    public string Name = "";
    public string Value = "";
    public string Extra = "";
    public bool IsYou;
}

/// <summary>One of ConquestAC's /top boards.</summary>
internal sealed class ConquestBoard
{
    public readonly string Key, Label, Header;
    public readonly List<BoardRow> Rows = new();
    public BoardRow? You;
    public DateTime UpdatedUtc = DateTime.MinValue;

    public ConquestBoard(string key, string label, string header) { Key = key; Label = label; Header = header; }

    public string Command => "/top " + Key;
}

/// <summary>
/// Everything ConquestAC-only (tag Conquest): bank balances and bank commands ("/b", "/bank"),
/// advanced augmentations ("/augs"), enlightenment augmentations ("/enl augs"), the XP bonus
/// breakdown ("/bonus"), fellowships looking for members ("/fship list"), the /top boards, and
/// the void damage-over-time duration scaling. The plugin only feeds it chat and only shows it
/// while the server is ConquestAC; every reply regex is anchored at the start of the line (after
/// an optional chat timestamp), as upstream does, so a pasted copy in a chat channel can't
/// overwrite your values.
/// </summary>
internal sealed class ConquestData
{
    private const string Ts = @"^\s*(?:\[[^\]]*\][\s:]*)?";

    // /b balances
    private static readonly Regex BankLine = new(Ts + @"\[BANK\]\s+(.+?):\s*(.*\S)\s*$");
    private static readonly Regex BankChrome = new(Ts + @"\[BANK\]\s+[^:]*:\s*$");
    private static readonly Regex EventToken = new(@"Event Tokens \[(.+?)\]");
    private static readonly Regex DepositReply = new(Ts + @"(?:No .+? (?:found|available) to deposit|Deposited\b.*)\s*$", RegexOptions.IgnoreCase);
    // /augs
    private static readonly Regex AugLine = new(Ts + @"(Creature|Item|Life|War|Void|Duration|Specialization|Melee|Missile):\s*([\d,]+)\b");
    private static readonly Regex AugChrome = new(Ts + @"(?:-{3,}|Advanced Augmentation Levels:|Use /aug info for per-level effect details\.)\s*$", RegexOptions.IgnoreCase);
    // /enl augs
    private static readonly Regex EnlLine = new(Ts + @"([A-Za-z][A-Za-z '\-]*?):\s*(([\d,]+)\s*\(\s*[\d,]+\s+max\s*\))\s*$");
    private static readonly Regex EnlChrome = new(Ts + @"Enlightenment Augmentations:\s*$");
    // /bonus
    private static readonly Regex BonusLine = new(Ts + @"(Quest|Enlightenment|PK Dungeon|Augmentation|Equipment|Total) Bonus(?:\s*\(([^)]+)\))?:\s*(.+\S)\s*$");
    private static readonly Regex BonusChrome = new(Ts + @"={2,}\s*XP Bonuses\s*={2,}\s*$");
    // /fship list
    private static readonly Regex FshipHeader = new(Ts + @"Fellowships looking for members");
    private static readonly Regex FshipLine = new(Ts + @"-\s+(.+?)\s+\(Leader:\s+(.+?)\)\s+\[(\d+/\d+)\](?:\s+@\s+(.+?))?\s*$");
    // /top
    private static readonly Regex TopHeader = new(Ts + @"Top\s+(\d+)\s+Players?\s+by\s+(.+?):\s*$");
    private static readonly Regex TopEntry = new(Ts + @"(\d+):\s+([\d,]+)\s+-\s+(.+?)\s*$");
    private static readonly Regex TopSeparator = new(Ts + @"\.\.\.\s*$");

    private static readonly string[] EnlOrder =
    {
        "Damage", "Damage Reduction", "Crit Damage", "Crit Damage Reduction", "Imbue", "Salvage", "Skill Credits",
        "Stamina Benediction", "Mana Benediction", "Cleave", "Arrow Split", "Spell Chain", "Aetheria Surge", "Void Contagion",
    };
    private static readonly Dictionary<string, string> EnlCosts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Cleave"] = "1 Pristine Token", ["Arrow Split"] = "1 Pristine Token", ["Spell Chain"] = "1 Pristine Token",
        ["Aetheria Surge"] = "1 Pristine Token", ["Void Contagion"] = "1 Pristine Token",
        ["Damage"] = "2 Tokens", ["Damage Reduction"] = "2 Tokens", ["Crit Damage"] = "2 Tokens",
        ["Crit Damage Reduction"] = "2 Tokens", ["Imbue"] = "2 Tokens", ["Salvage"] = "2 Tokens",
        ["Skill Credits"] = "4 Tokens", ["Stamina Benediction"] = "4 Tokens", ["Mana Benediction"] = "4 Tokens",
    };

    public const int PyrealsPerMmd = 250_000;

    public readonly List<NamedValue> Bank = new();
    public readonly List<ConquestAug> Augs = new()
    {
        new("Creature", 25, 2_500_000, 0.003825), new("Item", 100, 3_000_000, 0.006825), new("Life", 75, 2_500_000, 0.0095),
        new("War", 50, 1_750_000, 0.00425), new("Void", 50, 1_800_000, 0.00425), new("Duration", 30, 1_400_000, 0.00625),
        new("Specialization", 125, 3_000_000, 0.02), new("Melee", 50, 1_750_000, 0.00425), new("Missile", 50, 1_750_000, 0.00425),
    };
    public List<NamedValue> EnlAugs { get; private set; } = new();
    public readonly List<NamedValue> Bonuses = new()
    {
        new() { Name = "Quest" }, new() { Name = "Enlightenment" }, new() { Name = "PK Dungeon" }, new() { Name = "Augmentation" },
        new() { Name = "Equipment" }, new() { Name = "Total (Kills)" }, new() { Name = "Total (Quest/Lum)" },
    };
    public readonly List<ConquestFellowship> Fellowships = new();
    public readonly List<ConquestBoard> Boards = new()
    {
        new("augs", "Augs", "Total Augmentations"), new("deaths", "Deaths", "Deaths"), new("enl", "Enlightens", "Enlightenment"),
        new("level", "Levels", "Level"), new("lum", "Luminance", "Banked Luminance"), new("bank", "Pyreals", "Banked Pyreals"),
        new("qb", "QB", "Quest Bonus"), new("titles", "Titles", "Titles"),
    };

    public DateTime BankUtc = DateTime.MinValue, AugsUtc = DateTime.MinValue, EnlUtc = DateTime.MinValue,
        BonusUtc = DateTime.MinValue, FshipUtc = DateTime.MinValue;
    public int Revision { get; private set; }

    private readonly ChatRequest _bankReq = new(), _augReq = new(), _enlReq = new(), _bonusReq = new(), _fshipReq = new(),
        _topReq = new(), _depositReq = new();
    private ConquestBoard? _collecting;
    private long _collectingMs;
    private int _collectingExpected = int.MaxValue;
    private bool _collectingCleared;

    public static string EnlCost(string name) => EnlCosts.TryGetValue(name, out string? c) ? c : "";

    public int AugTotal => Augs.Sum(a => a.Count);
    public int DurationAugs => Augs.First(a => a.Name == "Duration").Count;

    public void Clear()
    {
        Bank.Clear();
        foreach (ConquestAug a in Augs) a.Count = 0;
        EnlAugs = new List<NamedValue>();
        foreach (NamedValue b in Bonuses) b.Value = "";
        Fellowships.Clear();
        foreach (ConquestBoard b in Boards) { b.Rows.Clear(); b.You = null; b.UpdatedUtc = DateTime.MinValue; }
        BankUtc = AugsUtc = EnlUtc = BonusUtc = FshipUtc = DateTime.MinValue;
        _collecting = null;
        Revision++;
    }

    // ── Requests (the plugin sends the command; each returns the command text) ──

    public string RequestBank(long now) { _bankReq.Sent(now); return "/b"; }
    public string RequestAugs(long now) { _augReq.Sent(now); return "/augs"; }
    public string RequestEnl(long now) { _enlReq.Sent(now); return "/enl augs"; }
    public string RequestBonus(long now) { _bonusReq.Sent(now); return "/bonus"; }
    public string RequestFships(long now) { _fshipReq.Sent(now); Fellowships.Clear(); Revision++; return "/fship list"; }

    public string RequestBoard(ConquestBoard board, long now)
    {
        _topReq.Sent(now);
        Open(board, now);
        return "/top " + board.Key;
    }

    /// <summary>An automatic deposit: its reply is ours to hide.</summary>
    public string AutoDeposit(long now) { _depositReq.Sent(now); return "/bank deposit"; }

    /// <summary>
    /// A chat line: true when it was a reply this module reads; <paramref name="ours"/> = it
    /// answers a request RynthOracle sent (eligible to be hidden).
    /// </summary>
    public bool OnChatLine(string text, long now, out bool ours)
    {
        ours = false;
        Match m;

        if (_depositReq.Awaiting(now) && DepositReply.IsMatch(text)) { ours = true; _depositReq.Extend(now); return true; }

        if ((m = BankLine.Match(text)).Success)
        {
            string name = EventToken.Replace(m.Groups[1].Value.Trim(), "$1");
            string value = m.Groups[2].Value.Trim();
            if (name == "Pyreals" && long.TryParse(value.Replace(",", ""), out long py))
                value += $" ({(py / PyrealsPerMmd).ToString("N0", CultureInfo.InvariantCulture)} MMDs)";
            NamedValue? e = Bank.FirstOrDefault(b => b.Name == name);
            if (e != null) e.Value = value; else Bank.Add(new NamedValue { Name = name, Value = value });
            BankUtc = DateTime.UtcNow;
            return Done(_bankReq, now, out ours);
        }
        if (_bankReq.Awaiting(now) && BankChrome.IsMatch(text)) return Done(_bankReq, now, out ours);

        if ((m = AugLine.Match(text)).Success)
        {
            ConquestAug? a = Augs.FirstOrDefault(x => x.Name == m.Groups[1].Value);
            if (a != null && int.TryParse(m.Groups[2].Value.Replace(",", ""), out int c)) a.Count = c;
            AugsUtc = DateTime.UtcNow;
            return Done(_augReq, now, out ours);
        }
        if (_augReq.Awaiting(now) && AugChrome.IsMatch(text)) return Done(_augReq, now, out ours);

        if (EnlChrome.IsMatch(text))
        {
            EnlAugs = new List<NamedValue>();
            return Done(_enlReq, now, out ours);
        }
        if ((m = EnlLine.Match(text)).Success)
        {
            // "Label: N (M max)" is specific to the "/enl augs" reply (its header starts a fresh list).
            string name = m.Groups[1].Value.Trim();
            var list = new List<NamedValue>(EnlAugs.Where(x => x.Name != name)) { new() { Name = name, Value = m.Groups[2].Value.Trim() } };
            list.Sort((x, y) => Rank(x.Name).CompareTo(Rank(y.Name)));
            EnlAugs = list;
            EnlUtc = DateTime.UtcNow;
            return Done(_enlReq, now, out ours);
        }

        if ((m = BonusLine.Match(text)).Success)
        {
            string q = m.Groups[2].Value.Trim();
            string name = q.Length > 0 ? $"{m.Groups[1].Value} ({q})" : m.Groups[1].Value;
            NamedValue? b = Bonuses.FirstOrDefault(x => x.Name == name);
            if (b != null) b.Value = m.Groups[3].Value.Trim();
            BonusUtc = DateTime.UtcNow;
            return Done(_bonusReq, now, out ours);
        }
        if (_bonusReq.Awaiting(now) && BonusChrome.IsMatch(text)) return Done(_bonusReq, now, out ours);

        if (FshipHeader.IsMatch(text))
        {
            if (!_fshipReq.Awaiting(now)) Fellowships.Clear();
            FshipUtc = DateTime.UtcNow;
            return Done(_fshipReq, now, out ours);
        }
        if ((m = FshipLine.Match(text)).Success)
        {
            Fellowships.Add(new ConquestFellowship
            {
                Name = m.Groups[1].Value.Trim(), Leader = m.Groups[2].Value.Trim(), Members = m.Groups[3].Value.Trim(),
                Location = m.Groups[4].Success ? m.Groups[4].Value.Trim() : "",
            });
            FshipUtc = DateTime.UtcNow;
            return Done(_fshipReq, now, out ours);
        }

        if ((m = TopHeader.Match(text)).Success)
        {
            ConquestBoard? board = Active(now) ?? Boards.FirstOrDefault(b => b.Header.Equals(m.Groups[2].Value.Trim(), StringComparison.OrdinalIgnoreCase));
            if (board == null) return false;
            Open(board, now);
            ClearOnce();
            _collectingExpected = int.TryParse(m.Groups[1].Value, out int n) ? n : int.MaxValue;
            return Done(_topReq, now, out ours);
        }
        if ((m = TopEntry.Match(text)).Success && Active(now) is ConquestBoard cb)
        {
            ClearOnce();
            int.TryParse(m.Groups[1].Value, out int rank);
            string name = m.Groups[3].Value.Trim();
            var row = new BoardRow { Rank = rank, Value = m.Groups[2].Value.Trim(), Name = name };
            if (name.EndsWith("(You)", StringComparison.OrdinalIgnoreCase))
            {
                row.IsYou = true;
                row.Name = name[..^5].Trim();
                cb.You = row;
            }
            if (rank <= _collectingExpected && cb.Rows.All(r => r.Rank != rank)) cb.Rows.Add(row);
            cb.Rows.Sort((x, y) => x.Rank.CompareTo(y.Rank));
            cb.UpdatedUtc = DateTime.UtcNow;
            _collectingMs = now;
            return Done(_topReq, now, out ours);
        }
        if (Active(now) != null && TopSeparator.IsMatch(text)) return Done(_topReq, now, out ours);

        return false;
    }

    private bool Done(ChatRequest req, long now, out bool ours)
    {
        ours = req.Awaiting(now);
        req.Extend(now);
        Revision++;
        return true;
    }

    private static int Rank(string name)
    {
        int i = Array.FindIndex(EnlOrder, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? EnlOrder.Length : i;
    }

    private void Open(ConquestBoard board, long now)
    {
        if (_collecting == board && now - _collectingMs < 15_000) return;
        _collecting = board;
        _collectingMs = now;
        _collectingExpected = int.MaxValue;
        _collectingCleared = false;
    }

    private ConquestBoard? Active(long now) => _collecting != null && now - _collectingMs < 15_000 ? _collecting : null;

    private void ClearOnce()
    {
        if (_collectingCleared || _collecting == null) return;
        _collecting.Rows.Clear();
        _collecting.You = null;
        _collectingCleared = true;
    }

    // ── Bank commands (Conquest-ACE's /bank; tokens are its one-letter currency codes) ──

    public sealed record Currency(string Label, string Token, long Multiplier = 1, long Max = long.MaxValue);

    public static readonly Currency[] Withdrawable =
    {
        new("Conquest Coins", "c"), new("Event Tokens", "e"), new("Legendary Keys", "k"), new("Pyreals", "p"),
        new("Soul Fragments", "s"), new("MMD Notes (250k)", "n"), new("MM Notes (200k)", "n MM"), new("M Notes (100k)", "n M"),
        new("D Notes (50k)", "n D"), new("C Notes (10k)", "n C"), new("L Notes (5k)", "n L"), new("X Notes (1k)", "n X"),
        new("V Notes (500)", "n V"), new("I Notes (100)", "n I"),
    };
    public const int DefaultWithdraw = 5; // MMD notes

    public static readonly Currency[] Transferable =
    {
        new("Legendary Keys", "k"), new("Luminance", "l"), new("MMD Notes (250k)", "p", 250_000, 1_000), new("Pyreals", "p"),
    };
    public const int DefaultTransfer = 1; // Luminance

    /// <summary>"/bank withdraw &lt;token&gt; &lt;n&gt;", or null when the amount isn't a whole number above 0.</summary>
    public static string? WithdrawCommand(Currency c, string amount) =>
        long.TryParse((amount ?? "").Replace(",", "").Trim(), out long n) && n > 0 ? $"/bank withdraw {c.Token} {n}" : null;

    /// <summary>"/bank transfer &lt;token&gt; &lt;n&gt; "name"", or null when the amount or name isn't usable.</summary>
    public static string? TransferCommand(Currency c, string amount, string target)
    {
        target = (target ?? "").Trim().Replace("\"", "");
        if (target.Length == 0 || !long.TryParse((amount ?? "").Replace(",", "").Trim(), out long n) || n <= 0 || n > c.Max) return null;
        try { return $"/bank transfer {c.Token} {checked(n * c.Multiplier)} \"{target}\""; }
        catch (OverflowException) { return null; }
    }

    // ── Levels and enlightenment (ConquestAC's levels 276-300, upstream's table) ──

    private static readonly long[] Levels276To300 =
    {
        194665023377, 198152685223, 201689992550, 205277652043, 208916380445,
        212606904701, 216349962105, 220146300445, 223996678153, 227901864460,
        231862639544, 235879794689, 239954132442, 244086466776, 248277623248,
        252528439169, 256839763767, 261212458359, 265647396522, 270145464270,
        274707560226, 279334595807, 284027495403, 288787196561, 293614650176,
    };
    public const int MaxLevel = 300;

    /// <summary>Total XP to reach a level 276-300 (0 otherwise).</summary>
    public static long XpForLevel(int level) => level >= 276 && level <= 300 ? Levels276To300[level - 276] : 0;

    private static double EnlMultiplier(int target) => target >= 75 ? 7.5 : target >= 50 ? 5.0 : target >= 25 ? 2.5 : 1.0;
    public static long EnlCoinCost(int target) => (long)(target * 100 * EnlMultiplier(target));
    public static long EnlLuminanceCost(int target) => (long)(target * 1_000_000L * EnlMultiplier(target));

    /// <summary>Void DoT duration on ConquestAC: × (1 + Archmage's Endurance × 0.2 + Duration augs × 0.05).</summary>
    public double VoidDurationMultiplier(int archmagesEndurance) => 1.0 + archmagesEndurance * 0.2 + DurationAugs * 0.05;
}
