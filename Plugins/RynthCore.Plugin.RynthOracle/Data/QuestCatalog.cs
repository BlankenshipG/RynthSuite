// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>One quest: a base (retail) row, a server pack row, or a held flag no list knows.</summary>
internal sealed class Quest
{
    public string Flag = "";
    /// <summary>"" for a retail quest; the server tag for a row from that server's pack.</summary>
    public string Server = "";
    public string Name = "";
    public string Url = "";
    public string Info = "";
    public string Hint = "";
    /// <summary>The list's Repeatable column (known for every quest, earned or not).</summary>
    public bool Repeatable;
    /// <summary>A flag from /myquests that no list knows.</summary>
    public bool IsNew;
    /// <summary>From a server pack: the repeat timer in seconds (-1 unknown) and solve limit (null unknown).</summary>
    public long MinDelta = -1;
    public int? MaxSolves;
    /// <summary>From a server pack: who sets the flag ("set by Monroe").</summary>
    public string Source = "";
    /// <summary>The row had a Repeatable value (a pack row without one doesn't override the base).</summary>
    internal bool HasRepeatable;
    /// <summary>Pack row: the base quest's flag can't be read on this server (hide it there).</summary>
    internal bool NotOnServer;

    /// <summary>The Name column: the curated name, else the one-line info, else "Unknown quest" (upstream's rule).</summary>
    public string DisplayName => Name.Length > 0 ? Name : Info.Length > 0 ? Info : "Unknown quest";

    /// <summary>The server is the source of truth where it has spoken (the flag's timer); else the lists.</summary>
    public bool IsRepeatable(QuestFlagStore flags) =>
        flags.TryGet(Flag, out QuestFlag q) ? q.RepeatTime != TimeSpan.Zero : Repeatable;

    /// <summary>Holding the flag at all is the test: /myquests only reports flags the character earned.</summary>
    public bool IsComplete(QuestFlagStore flags) => flags.Flags.ContainsKey(Flag);

    /// <summary>"ready" (never earned or off cooldown), "completed" (one-time), "maxed" (solve limit) or the countdown.</summary>
    public string Status(QuestFlagStore flags, DateTime utcNow)
    {
        if (!flags.TryGet(Flag, out QuestFlag q)) return "ready";
        if (!IsRepeatable(flags)) return "completed";
        if (q.AtSolveLimit) return "maxed";
        return q.NextAvailable(utcNow);
    }

    public string SolvesText(QuestFlagStore flags) =>
        flags.TryGet(Flag, out QuestFlag q) && IsRepeatable(flags) ? q.Solves.ToString() : "";

    /// <summary>The info blurb, the walkthrough and (from a server pack) who sets it and its timer, as one line.</summary>
    public string Details()
    {
        string d;
        if (Info.Length == 0) d = Hint;
        else if (Hint.Length == 0 || string.Equals(Info, Hint, StringComparison.OrdinalIgnoreCase)) d = Info;
        else d = Info + " " + Hint;
        var extra = new List<string>();
        if (Source.Length > 0) extra.Add(Source);
        if (MinDelta > 0) extra.Add("repeats after " + TimeText.Friendly(TimeSpan.FromSeconds(MinDelta)));
        if (MaxSolves is int m && m > 0) extra.Add(m == 1 ? "once only" : $"at most {m} times");
        if (extra.Count > 0) d = (d.Length > 0 ? d + " " : "") + "(" + string.Join("; ", extra) + ")";
        return d;
    }
}

/// <summary>
/// The quest catalog: the base list (every retail quest: Oracle of Dereth's curated master
/// list, or a newer download of it) plus the current server's quest pack.
///
/// Packs: Resources\quests.&lt;tag&gt;.csv embedded as "RynthOracle.quests.&lt;tag&gt;.csv" (Conquest and
/// Levistras: Advis's server-specific rows; Aelrynth: generated from the world database by
/// Tools\RynthOracle.QuestPackGen). A file %APPDATA%\RynthCore\RynthOracle\packs\quests.&lt;tag&gt;.csv
/// replaces the embedded pack, so a regenerated pack can be tried without a rebuild. A new
/// server only needs a tag (ServerTags) and a pack file.
///
/// Pack columns: the base list's (QuestFlag, Quest, Url, Info, Hint, Repeatable) plus optional
/// MinDelta, MaxSolves, Source and NotOnServer. Merge: a pack row for a flag the base list has
/// keeps the base row's text (Advis's) and only fills its empty fields, adding the timer and
/// source; other pack rows are added; NotOnServer=TRUE hides a base row on that server (its
/// flag can't be read there). A base list row with a Server value (a downloaded master list)
/// is treated as that server's pack row.
/// </summary>
internal sealed class QuestCatalog
{
    private readonly List<Quest> _quests = new();
    private readonly Dictionary<string, Quest> _byFlag = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<Quest> Quests => _quests;
    public int RetailCount { get; private set; }
    public int ServerCount { get; private set; }
    public int HiddenCount { get; private set; }
    public string PackServer { get; private set; } = "";
    public string PackSource { get; private set; } = "";
    public bool UsingBundled { get; private set; } = true;
    public string Source => UsingBundled ? "bundled list" : "downloaded list";
    public int KnownCount => RetailCount + ServerCount;
    public int Revision { get; private set; }

    public static string DownloadedPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore", "RynthOracle", "quests.csv");

    public static string PackOverridePath(string tag) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore", "RynthOracle", "packs",
        $"quests.{tag.ToLowerInvariant()}.csv");

    /// <summary>The servers with an embedded quest pack.</summary>
    public static IEnumerable<string> EmbeddedPacks() =>
        Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Where(n => n.StartsWith("RynthOracle.quests.", StringComparison.Ordinal) && n.EndsWith(".csv", StringComparison.Ordinal)
                        && n.Length > "RynthOracle.quests..csv".Length)
            .Select(n => n["RynthOracle.quests.".Length..^4]);

    /// <summary>Loads the base list (downloaded when valid, else bundled) and the pack for <paramref name="serverTag"/>.</summary>
    public void Load(string serverTag, Action<string>? log)
    {
        List<Quest>? baseRows = null;
        UsingBundled = true;
        if (File.Exists(DownloadedPath))
        {
            try
            {
                using var reader = new StreamReader(DownloadedPath);
                baseRows = ReadRows(reader, "", out string? why);
                if (baseRows == null) log?.Invoke($"[RynthOracle] The downloaded quest list is unusable ({why}); using the bundled one.");
                else UsingBundled = false;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[RynthOracle] Couldn't read the downloaded quest list ({ex.Message}); using the bundled one.");
            }
        }
        if (baseRows == null)
        {
            using StreamReader bundled = Csv.OpenEmbedded("quests.csv");
            baseRows = ReadRows(bundled, "", out _) ?? new List<Quest>();
        }

        List<Quest>? packRows = null;
        PackServer = "";
        PackSource = "";
        if (serverTag.Length > 0)
        {
            string over = PackOverridePath(serverTag);
            try
            {
                if (File.Exists(over))
                {
                    using var r = new StreamReader(over);
                    packRows = ReadRows(r, serverTag, out string? why);
                    if (packRows != null) PackSource = "file " + over;
                    else log?.Invoke($"[RynthOracle] {over} is unusable ({why}); using the bundled {serverTag} pack.");
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"[RynthOracle] Couldn't read {over} ({ex.Message}).");
            }
            if (packRows == null && EmbeddedPacks().Any(t => t.Equals(serverTag, StringComparison.OrdinalIgnoreCase)))
            {
                using StreamReader r = Csv.OpenEmbedded($"quests.{serverTag.ToLowerInvariant()}.csv");
                packRows = ReadRows(r, serverTag, out _);
                if (packRows != null) PackSource = "bundled pack";
            }
            if (packRows != null) PackServer = serverTag;
        }

        Build(baseRows, packRows ?? new List<Quest>(), serverTag);
    }

    private void Build(List<Quest> baseRows, List<Quest> packRows, string serverTag)
    {
        _quests.Clear();
        _byFlag.Clear();
        int retail = 0, server = 0, hidden = 0;

        // Base rows tied to a server (a downloaded master list) are that server's pack rows.
        var pack = new List<Quest>();
        foreach (Quest q in baseRows)
        {
            if (q.Server.Length == 0)
            {
                if (_byFlag.TryAdd(q.Flag, q)) { _quests.Add(q); retail++; }
            }
            else if (serverTag.Length > 0 && q.Server.Equals(serverTag, StringComparison.OrdinalIgnoreCase))
            {
                q.Server = serverTag;
                pack.Add(q);
            }
        }
        pack.AddRange(packRows);

        var hide = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Quest p in pack)
        {
            if (p.NotOnServer) { hide.Add(p.Flag); continue; }
            if (_byFlag.TryGetValue(p.Flag, out Quest? b))
            {
                // Advis's text wins; the pack fills gaps and adds what only the server knows.
                if (b.Name.Length == 0) b.Name = p.Name;
                if (b.Url.Length == 0) b.Url = p.Url;
                if (b.Info.Length == 0) b.Info = p.Info;
                if (b.Hint.Length == 0) b.Hint = p.Hint;
                if (p.MinDelta >= 0) b.MinDelta = p.MinDelta;
                if (p.MaxSolves != null) b.MaxSolves = p.MaxSolves;
                if (p.Source.Length > 0) b.Source = p.Source;
                if (p.HasRepeatable) b.Repeatable = p.Repeatable;
            }
            else
            {
                p.Server = serverTag;
                _byFlag[p.Flag] = p;
                _quests.Add(p);
                server++;
            }
        }
        if (hide.Count > 0)
        {
            hidden = _quests.RemoveAll(q => q.Server.Length == 0 && hide.Contains(q.Flag));
            foreach (string f in hide) if (_byFlag.TryGetValue(f, out Quest? q) && q.Server.Length == 0) _byFlag.Remove(f);
            retail -= hidden;
        }

        RetailCount = retail;
        ServerCount = server;
        HiddenCount = hidden;
        Revision++;
    }

    /// <summary>Checks a CSV is a usable master list (header with a flag column, enough rows).</summary>
    public static bool Validate(string csv, out string why)
    {
        using var reader = new StringReader(csv);
        string? header = reader.ReadLine();
        if (header == null) { why = "empty"; return false; }
        if (Csv.Column(Csv.MapColumns(header), "questflag", "flag") < 0) { why = "no quest flag column"; return false; }
        int rows = 0;
        while (reader.ReadLine() is string line)
            if (!string.IsNullOrWhiteSpace(line)) rows++;
        if (rows < 1000) { why = $"only {rows} rows"; return false; }
        why = "";
        return true;
    }

    /// <summary>Reads quest rows; <paramref name="packServer"/> "" for a base list. Null when unusable.</summary>
    private static List<Quest>? ReadRows(TextReader reader, string packServer, out string? why)
    {
        string? header = reader.ReadLine();
        if (header == null) { why = "empty"; return null; }
        Dictionary<string, int> cols = Csv.MapColumns(header);
        int flagCol = Csv.Column(cols, "questflag", "flag");
        if (flagCol < 0) { why = "no quest flag column"; return null; }
        int serverCol = Csv.Column(cols, "server", "world");
        int nameCol = Csv.Column(cols, "quest", "questname", "name", "title");
        int urlCol = Csv.Column(cols, "url", "link", "wiki");
        int infoCol = Csv.Column(cols, "info", "notes", "description");
        int hintCol = Csv.Column(cols, "hint", "hints", "directions", "walkthrough");
        int repeatCol = Csv.Column(cols, "repeatable", "repeat");
        int deltaCol = Csv.Column(cols, "mindelta");
        int maxCol = Csv.Column(cols, "maxsolves");
        int sourceCol = Csv.Column(cols, "source");
        int notOnCol = Csv.Column(cols, "notonserver");

        var rows = new List<Quest>();
        while (reader.ReadLine() is string line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] f = Csv.ParseLine(line);
            string flag = Csv.Field(f, flagCol).ToLowerInvariant();
            if (flag.Length == 0) continue;
            string rep = Csv.Field(f, repeatCol);
            var q = new Quest
            {
                Flag = flag,
                Server = packServer.Length > 0 ? packServer : Csv.Field(f, serverCol),
                Name = Csv.Field(f, nameCol),
                Url = Csv.Field(f, urlCol),
                Info = Csv.Field(f, infoCol),
                Hint = Csv.Field(f, hintCol),
                Repeatable = Csv.IsTrue(rep),
                HasRepeatable = rep.Length > 0,
                Source = Csv.Field(f, sourceCol),
                NotOnServer = Csv.IsTrue(Csv.Field(f, notOnCol)),
            };
            if (long.TryParse(Csv.Field(f, deltaCol), out long d) && d >= 0) q.MinDelta = d;
            if (int.TryParse(Csv.Field(f, maxCol), out int m)) q.MaxSolves = m;
            rows.Add(q);
        }
        if (rows.Count == 0) { why = "no rows"; return null; }
        why = null;
        return rows;
    }

    public bool TryGet(string flag, out Quest q) => _byFlag.TryGetValue(flag, out q!);

    /// <summary>Adds rows for held flags no list knows, and drops such rows no longer held.</summary>
    public void SyncDiscoveries(QuestFlagStore flags)
    {
        bool changed = false;
        for (int i = _quests.Count - 1; i >= 0; i--)
        {
            Quest q = _quests[i];
            if (q.IsNew && !flags.Flags.ContainsKey(q.Flag))
            {
                _quests.RemoveAt(i);
                _byFlag.Remove(q.Flag);
                changed = true;
            }
        }
        foreach (QuestFlag f in flags.Flags.Values)
        {
            if (_byFlag.ContainsKey(f.Key)) continue;
            var q = new Quest
            {
                Flag = f.Key,
                Name = f.Description.Replace("\"", "").Replace("'", "").Trim(),
                Repeatable = f.RepeatTime != TimeSpan.Zero,
                IsNew = true,
            };
            _quests.Add(q);
            _byFlag[q.Flag] = q;
            changed = true;
        }
        if (changed) Revision++;
    }
}
