using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace RynthOracle.QuestPackGen;

/// <summary>
/// Generates a server quest pack for RynthOracle from an ACE world-database dump.
///
/// Every row of the world's `quest` table (the flags /myquests can show: ACE leaves out flags
/// without a row) becomes a pack row with its timer (min_Delta, seconds, before the server's
/// quest_mindelta_rate), its solve limit, its message, and who sets it (NPCs whose emotes
/// stamp, increment or update the flag; else NPCs that check it). The curated text stays
/// Advis's: for a flag in the base list the pack leaves Quest, Url and Hint empty (the plugin
/// keeps the base row's text and only fills empty fields), and a flag that only another
/// server's pack knows takes that pack's text. Base flags missing from the quest table get a
/// NotOnServer row, so the plugin hides them on this server (their status can never be read).
/// </summary>
internal static class Program
{
    // EmoteType (ACE.Entity.Enum.EmoteType)
    private static readonly HashSet<int> SetTypes = new() { 20, 22, 33, 60, 61, 70, 79, 81, 85, 86 };
    private static readonly HashSet<int> CheckTypes = new() { 21, 30, 58, 80, 82 };

    private static int Main(string[] args)
    {
        try
        {
            string server = Arg(args, "-server") ?? "Aelrynth";
            string resources = Arg(args, "-resources") ?? FindResources();
            string dump = Arg(args, "-dump") ?? NewestDump();
            string output = Arg(args, "-out") ?? Path.Combine(resources, $"quests.{server.ToLowerInvariant()}.csv");
            Console.WriteLine($"dump:      {dump}");
            Console.WriteLine($"resources: {resources}");
            Console.WriteLine($"out:       {output}");

            var db = WorldDump.Read(dump);
            Console.WriteLine($"read {db.Quests.Count} quests, {db.Names.Count} weenie names, {db.EmoteOwner.Count} emotes, {db.Actions.Count} quest emote actions");

            Dictionary<string, CuratedRow> baseRows = Curated.Load(Path.Combine(resources, "quests.csv"));
            var otherPacks = new Dictionary<string, CuratedRow>(StringComparer.OrdinalIgnoreCase);
            foreach (string pack in Directory.GetFiles(resources, "quests.*.csv"))
            {
                if (Path.GetFullPath(pack).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var kv in Curated.Load(pack))
                    otherPacks.TryAdd(kv.Key, kv.Value);
            }

            // Who sets / checks each flag.
            var setters = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            var checkers = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach ((uint emoteId, int type, string message) in db.Actions)
            {
                var map = SetTypes.Contains(type) ? setters : CheckTypes.Contains(type) ? checkers : null;
                if (map == null || !db.EmoteOwner.TryGetValue(emoteId, out uint wcid)) continue;
                string flag = QuestName(message);
                if (flag.Length == 0) continue;
                string who = db.Names.TryGetValue(wcid, out string? n) && n.Length > 0 ? n
                    : db.ClassNames.TryGetValue(wcid, out string? c) ? c : $"wcid {wcid}";
                if (!map.TryGetValue(flag, out var set)) map[flag] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(who);
            }

            var sb = new StringBuilder();
            sb.Append("QuestFlag,Server,Quest,Url,Info,Hint,Repeatable,MinDelta,MaxSolves,Source,NotOnServer\n");
            int fromBase = 0, fromOther = 0, generated = 0, notOn = 0;
            foreach (var q in db.Quests.OrderBy(q => q.Key, StringComparer.Ordinal))
            {
                string flag = q.Key;
                (uint minDelta, int maxSolves, string message) = q.Value;
                string name = "", url = "", info = message, hint = "";
                if (baseRows.ContainsKey(flag)) fromBase++;
                else if (otherPacks.TryGetValue(flag, out CuratedRow? o))
                {
                    fromOther++;
                    name = o.Name; url = o.Url; hint = o.Hint;
                    if (o.Info.Length > 0) info = o.Info;
                }
                else generated++;

                bool repeatable = minDelta > 0 || maxSolves == -1 || maxSolves > 1;
                string source = setters.TryGetValue(flag, out var s) ? "set by " + Join(s)
                    : checkers.TryGetValue(flag, out var c) ? "checked by " + Join(c) : "";
                Row(sb, flag, server, name, url, info, hint, repeatable ? "TRUE" : "FALSE",
                    minDelta.ToString(CultureInfo.InvariantCulture), maxSolves.ToString(CultureInfo.InvariantCulture), source, "");
            }
            foreach (string flag in baseRows.Keys.Where(f => !db.Quests.ContainsKey(f)).OrderBy(f => f, StringComparer.Ordinal))
            {
                notOn++;
                Row(sb, flag, server, "", "", "", "", "", "", "", "", "TRUE");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine($"wrote {db.Quests.Count} quests: {fromBase} in the base list (Advis's text kept), {fromOther} named from another server's pack, {generated} new; {notOn} base flags not on {server}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    /// <summary>ACE QuestManager.GetQuestName: everything before the first '@', lower case.</summary>
    private static string QuestName(string message)
    {
        int at = message.IndexOf('@');
        return (at >= 0 ? message[..at] : message).Trim().ToLowerInvariant();
    }

    private static string Join(SortedSet<string> names) =>
        names.Count <= 3 ? string.Join(", ", names) : string.Join(", ", names.Take(3)) + $" and {names.Count - 3} more";

    private static void Row(StringBuilder sb, params string[] fields)
    {
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0) sb.Append(',');
            string f = fields[i].Replace("\r", " ").Replace("\n", " ");
            if (f.IndexOfAny(new[] { ',', '"' }) >= 0) sb.Append('"').Append(f.Replace("\"", "\"\"")).Append('"');
            else sb.Append(f);
        }
        sb.Append('\n');
    }

    private static string? Arg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private static string FindResources()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                string p = Path.Combine(dir.FullName, "Plugins", "RynthCore.Plugin.RynthOracle", "Resources");
                if (Directory.Exists(p)) return p;
                dir = dir.Parent;
            }
        }
        throw new InvalidOperationException("can't find Plugins\\RynthCore.Plugin.RynthOracle\\Resources; pass -resources");
    }

    private static string NewestDump()
    {
        const string dir = @"C:\Aeshnidae\backups";
        string? f = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "aeshnidae_world-*.sql.gz").OrderByDescending(p => p, StringComparer.Ordinal).FirstOrDefault()
            : null;
        return f ?? throw new InvalidOperationException($"no aeshnidae_world-*.sql.gz in {dir}; pass -dump");
    }
}

internal sealed record CuratedRow(string Name, string Url, string Info, string Hint);

internal static class Curated
{
    public static Dictionary<string, CuratedRow> Load(string path)
    {
        var map = new Dictionary<string, CuratedRow>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return map;
        using var r = new StreamReader(path);
        string? header = r.ReadLine();
        if (header == null) return map;
        string[] h = CsvLine.Parse(header);
        int Col(string n) => Array.FindIndex(h, x => x.Trim().Equals(n, StringComparison.OrdinalIgnoreCase));
        int flag = Col("QuestFlag"), name = Col("Quest"), url = Col("Url"), info = Col("Info"), hint = Col("Hint");
        if (flag < 0) return map;
        while (r.ReadLine() is string line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] f = CsvLine.Parse(line);
            string F(int i) => i >= 0 && i < f.Length ? f[i].Trim() : "";
            string key = F(flag).ToLowerInvariant();
            if (key.Length > 0) map.TryAdd(key, new CuratedRow(F(name), F(url), F(info), F(hint)));
        }
        return map;
    }
}

internal static class CsvLine
{
    public static string[] Parse(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (q)
            {
                if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; }
                else sb.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields.ToArray();
    }
}

/// <summary>The few world tables the pack needs, read from a mysqldump file (.sql or .sql.gz).</summary>
internal sealed class WorldDump
{
    public readonly Dictionary<string, (uint MinDelta, int MaxSolves, string Message)> Quests = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<uint, string> ClassNames = new();
    public readonly Dictionary<uint, string> Names = new();          // PropertyString 1 (Name)
    public readonly Dictionary<uint, uint> EmoteOwner = new();       // emote id -> wcid
    public readonly List<(uint EmoteId, int Type, string Message)> Actions = new();

    public static WorldDump Read(string path)
    {
        var db = new WorldDump();
        using Stream file = File.OpenRead(path);
        using Stream s = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress) : file;
        using var reader = new StreamReader(s, Encoding.UTF8);

        var columns = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? creating = null;
        string? inserting = null;
        while (reader.ReadLine() is string line)
        {
            if (line.StartsWith("CREATE TABLE `", StringComparison.Ordinal))
            {
                creating = line.Substring(14, line.IndexOf('`', 14) - 14);
                columns[creating] = new List<string>();
                continue;
            }
            if (creating != null)
            {
                string t = line.TrimStart();
                if (t.StartsWith("`", StringComparison.Ordinal)) columns[creating].Add(t.Substring(1, t.IndexOf('`', 1) - 1));
                else if (t.StartsWith(")", StringComparison.Ordinal)) creating = null;
                continue;
            }
            if (line.StartsWith("INSERT INTO `", StringComparison.Ordinal))
            {
                string table = line.Substring(13, line.IndexOf('`', 13) - 13);
                inserting = IsWanted(table) ? table : null;
                int v = line.IndexOf(" VALUES", StringComparison.Ordinal);
                if (inserting != null && v >= 0) db.Take(inserting, columns, line[(v + 7)..]);
                if (line.TrimEnd().EndsWith(";", StringComparison.Ordinal)) inserting = null;
                continue;
            }
            if (inserting != null)
            {
                db.Take(inserting, columns, line);
                if (line.TrimEnd().EndsWith(";", StringComparison.Ordinal)) inserting = null;
            }
        }
        return db;
    }

    private static bool IsWanted(string t) =>
        t is "quest" or "weenie" or "weenie_properties_string" or "weenie_properties_emote" or "weenie_properties_emote_action";

    private void Take(string table, Dictionary<string, List<string>> columns, string text)
    {
        if (!columns.TryGetValue(table, out List<string>? cols)) return;
        foreach (List<string?> row in Tuples(text))
        {
            string? Get(string c) { int i = cols.IndexOf(c); return i >= 0 && i < row.Count ? row[i] : null; }
            switch (table)
            {
                case "quest":
                    string name = (Get("name") ?? "").Trim().ToLowerInvariant();
                    if (name.Length == 0) break;
                    uint.TryParse(Get("min_Delta"), NumberStyles.None, CultureInfo.InvariantCulture, out uint md);
                    int.TryParse(Get("max_Solves"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int ms);
                    Quests[name] = (md, ms, (Get("message") ?? "").Trim());
                    break;
                case "weenie":
                    if (uint.TryParse(Get("class_Id"), out uint id)) ClassNames[id] = Get("class_Name") ?? "";
                    break;
                case "weenie_properties_string":
                    if (Get("type") == "1" && uint.TryParse(Get("object_Id"), out uint oid)) Names[oid] = Get("value") ?? "";
                    break;
                case "weenie_properties_emote":
                    if (uint.TryParse(Get("id"), out uint eid) && uint.TryParse(Get("object_Id"), out uint owner)) EmoteOwner[eid] = owner;
                    break;
                case "weenie_properties_emote_action":
                    string? msg = Get("message");
                    if (msg == null || !int.TryParse(Get("type"), out int type)) break;
                    if (uint.TryParse(Get("emote_Id"), out uint emote)) Actions.Add((emote, type, msg));
                    break;
            }
        }
    }

    /// <summary>The value tuples in a VALUES list fragment: "(1,'a\'b',NULL),(2,...);".</summary>
    private static IEnumerable<List<string?>> Tuples(string text)
    {
        int i = 0;
        while (true)
        {
            while (i < text.Length && text[i] != '(') i++;
            if (i >= text.Length) yield break;
            i++;
            var row = new List<string?>();
            var sb = new StringBuilder();
            bool quoted = false, wasQuoted = false;
            for (; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '\\' && i + 1 < text.Length)
                    {
                        char n = text[++i];
                        sb.Append(n switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '0' => '\0', _ => n });
                    }
                    else if (c == '\'') quoted = false;
                    else sb.Append(c);
                }
                else if (c == '\'') { quoted = true; wasQuoted = true; }
                else if (c == ',' || c == ')')
                {
                    string v = sb.ToString();
                    row.Add(!wasQuoted && v.Trim() == "NULL" ? null : v);
                    sb.Clear();
                    wasQuoted = false;
                    if (c == ')') { i++; break; }
                }
                else sb.Append(c);
            }
            yield return row;
        }
    }
}
