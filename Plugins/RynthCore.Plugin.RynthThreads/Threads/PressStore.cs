using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace RynthCore.Plugin.RynthThreads.Threads;

/// <summary>
/// Every press, one JSON object per line, in one file shared by all characters and clients on the
/// PC (%LOCALAPPDATA%\RynthCore\RynthThreads\presses.jsonl). Appends open the file for writing
/// alone, so two clients never mix their lines: a writer that finds it busy tries again a few
/// times, and a press that still can't be written waits for <see cref="FlushPending"/>.
/// Reading follows the file's growth (<see cref="Poll"/>), so other clients' presses show up.
/// Written by hand with Utf8JsonWriter and read with JsonDocument: plugin types never go through
/// System.Text.Json's serializer (its cache would keep an old plugin alive across a hot reload).
/// </summary>
internal sealed class PressStore
{
    public const string FileName = "presses.jsonl";
    private const int AppendTries = 4;
    private const int AppendWaitMs = 15;
    private const int MaxPending = 200;

    private readonly List<Press> _presses = new();
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private readonly List<Press> _pending = new();
    private long _readOffset;

    public readonly string Folder;
    public string FilePath => Path.Combine(Folder, FileName);

    /// <summary>Every press read or recorded, oldest first.</summary>
    public IReadOnlyList<Press> Presses => _presses;
    /// <summary>Presses recorded here that couldn't be written yet.</summary>
    public int PendingCount => _pending.Count;
    /// <summary>Lines in the file that couldn't be read (kept in the file, skipped here).</summary>
    public int BadLines { get; private set; }
    /// <summary>Bumped whenever <see cref="Presses"/> changes.</summary>
    public int Version { get; private set; }

    public PressStore(string folder) => Folder = folder;

    /// <summary>%LOCALAPPDATA%\RynthCore\RynthThreads.</summary>
    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RynthCore", "RynthThreads");

    // ── Writing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Keeps the press and appends it to the file. Returns null when written, else why not (it
    /// stays pending and <see cref="FlushPending"/> tries again).
    /// </summary>
    public string? Record(Press p)
    {
        if (p.Id.Length == 0) p.Id = Press.NewId();
        Add(p);
        string? error = TryAppend(ToJson(p));
        if (error != null && _pending.Count < MaxPending) _pending.Add(p);
        return error;
    }

    /// <summary>Writes presses that found the file busy before. Returns how many are still waiting.</summary>
    public int FlushPending()
    {
        while (_pending.Count > 0)
        {
            if (TryAppend(ToJson(_pending[0])) != null) break;
            _pending.RemoveAt(0);
        }
        return _pending.Count;
    }

    /// <summary>Appends one line, retrying briefly while another writer holds the file.</summary>
    internal string? TryAppend(string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
        string? last = null;
        for (int attempt = 0; attempt < AppendTries; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                // FileShare.Read: readers may look, no other writer gets in until this line is down.
                using var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                fs.Write(bytes, 0, bytes.Length);
                return null;
            }
            catch (IOException ex)
            {
                last = ex.Message;
            }
            catch (UnauthorizedAccessException ex)
            {
                return ex.Message;
            }
            if (attempt < AppendTries - 1) Thread.Sleep(AppendWaitMs * (attempt + 1));   // at most 90 ms on the pump
        }
        return last ?? "the file is busy";
    }

    // ── Reading ─────────────────────────────────────────────────────────────

    /// <summary>Reads what was added to the file since the last read (everything the first time). Returns the number of new presses.</summary>
    public int Poll()
    {
        string path = FilePath;
        long length;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return 0;
            length = info.Length;
        }
        catch { return 0; }

        if (length < _readOffset) _readOffset = 0;   // replaced or cut short: read it all again (ids keep it from doubling)
        if (length == _readOffset) return 0;

        byte[] data;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(_readOffset, SeekOrigin.Begin);
            data = new byte[length - _readOffset];
            int got = 0;
            while (got < data.Length)
            {
                int n = fs.Read(data, got, data.Length - got);
                if (n <= 0) break;
                got += n;
            }
            if (got < data.Length) Array.Resize(ref data, got);
        }
        catch { return 0; }

        // Only whole lines; a line still being written is read next time.
        int lastNewline = Array.LastIndexOf(data, (byte)'\n');
        if (lastNewline < 0) return 0;
        _readOffset += lastNewline + 1;

        int added = 0;
        foreach (string line in Encoding.UTF8.GetString(data, 0, lastNewline).Split('\n'))
        {
            string l = line.Trim();
            if (l.Length == 0) continue;
            Press? p = FromJson(l);
            if (p == null) { BadLines++; continue; }
            if (Add(p)) added++;
        }
        return added;
    }

    private bool Add(Press p)
    {
        if (p.Id.Length > 0 && !_ids.Add(p.Id)) return false;
        // Kept in time order (other clients' lines can land out of order).
        int at = _presses.Count;
        while (at > 0 && _presses[at - 1].TimeUtc > p.TimeUtc) at--;
        _presses.Insert(at, p);
        Version++;
        return true;
    }

    // ── JSON ────────────────────────────────────────────────────────────────

    internal static string ToJson(Press p)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteString("id", p.Id);
            w.WriteString("time", p.TimeUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
            w.WriteString("server", p.Server);
            w.WriteString("character", p.Character);
            w.WriteString("fragment", p.Fragment);
            WriteInt(w, "fragmentLevel", p.FragmentLevel);
            w.WriteString("tool", p.Tool);
            w.WriteString("outcome", p.Outcome);
            w.WriteBoolean("componentsSeen", p.ComponentsSeen);
            w.WriteStartArray("components");
            foreach (string c in p.Components) w.WriteStringValue(c);
            w.WriteEndArray();
            w.WriteString("random", p.RandomNote);
            w.WriteStartArray("modifiers");
            foreach (GemModifier m in p.Modifiers)
            {
                w.WriteStartObject();
                w.WriteString("name", m.Name);
                w.WriteString("description", m.Description);
                w.WriteStartArray("numbers");
                foreach (ModNumber n in m.Numbers)
                {
                    w.WriteStartObject();
                    w.WriteNumber("value", n.Value);
                    w.WriteString("kind", n.Kind);
                    w.WriteString("text", n.Text);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            WriteInt(w, "resultLevel", p.ResultLevel);
            WriteInt(w, "entries", p.Entries);
            w.WriteBoolean("complete", p.Complete);
            w.WriteString("end", p.End);
            WriteStrings(w, "raw", p.Raw);
            WriteStrings(w, "extra", p.Extra);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteInt(Utf8JsonWriter w, string name, int? v)
    {
        if (v.HasValue) w.WriteNumber(name, v.Value);
        else w.WriteNull(name);
    }

    private static void WriteStrings(Utf8JsonWriter w, string name, List<string> list)
    {
        w.WriteStartArray(name);
        foreach (string s in list) w.WriteStringValue(s);
        w.WriteEndArray();
    }

    /// <summary>One line back into a press, or null when it isn't one. Unknown fields are ignored, missing ones left empty.</summary>
    internal static Press? FromJson(string line)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            var p = new Press
            {
                Id = Str(r, "id"),
                Server = Str(r, "server"),
                Character = Str(r, "character"),
                Fragment = Str(r, "fragment"),
                FragmentLevel = IntOrNull(r, "fragmentLevel"),
                Tool = Str(r, "tool"),
                Outcome = Str(r, "outcome"),
                ComponentsSeen = Bool(r, "componentsSeen"),
                Components = Strings(r, "components"),
                RandomNote = Str(r, "random"),
                ResultLevel = IntOrNull(r, "resultLevel"),
                Entries = IntOrNull(r, "entries"),
                Complete = Bool(r, "complete"),
                End = Str(r, "end"),
                Raw = Strings(r, "raw"),
                Extra = Strings(r, "extra"),
            };
            p.Components.Sort(StringComparer.OrdinalIgnoreCase);
            if (DateTime.TryParse(Str(r, "time"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime t))
                p.TimeUtc = t.ToUniversalTime();
            if (r.TryGetProperty("modifiers", out JsonElement mods) && mods.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement m in mods.EnumerateArray())
                {
                    if (m.ValueKind != JsonValueKind.Object) continue;
                    var gm = new GemModifier { Name = Str(m, "name"), Description = Str(m, "description") };
                    if (gm.Name.Length == 0) continue;
                    if (m.TryGetProperty("numbers", out JsonElement nums) && nums.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement n in nums.EnumerateArray())
                            if (n.ValueKind == JsonValueKind.Object && n.TryGetProperty("value", out JsonElement v) && v.TryGetDouble(out double d))
                                gm.Numbers.Add(new ModNumber(d, Str(n, "kind"), Str(n, "text")));
                    p.Modifiers.Add(gm);
                }
            }
            return p;
        }
        catch
        {
            return null;
        }
    }

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static bool Bool(JsonElement o, string name) =>
        o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

    private static int? IntOrNull(JsonElement o, string name) =>
        o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : null;

    private static List<string> Strings(JsonElement o, string name)
    {
        var list = new List<string>();
        if (o.TryGetProperty(name, out JsonElement a) && a.ValueKind == JsonValueKind.Array)
            foreach (JsonElement e in a.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String) list.Add(e.GetString() ?? string.Empty);
        return list;
    }

    // ── CSV ─────────────────────────────────────────────────────────────────

    /// <summary>Writes every press as CSV into this folder (presses-yyyyMMdd-HHmmss.csv). Returns the path.</summary>
    public string ExportCsv(DateTime localNow)
    {
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, "presses-" + localNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv");
        File.WriteAllText(path, Csv(_presses), new UTF8Encoding(true));
        return path;
    }

    internal static string Csv(IEnumerable<Press> presses)
    {
        var sb = new StringBuilder();
        sb.Append("time_utc,server,character,fragment,fragment_level,outcome,components,result_level,entries,complete,modifier_count,modifiers\r\n");
        foreach (Press p in presses)
        {
            var mods = new List<string>();
            foreach (GemModifier m in p.Modifiers) mods.Add(m.Name + ": " + m.Description);
            Cell(sb, p.TimeUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)); sb.Append(',');
            Cell(sb, p.Server); sb.Append(',');
            Cell(sb, p.Character); sb.Append(',');
            Cell(sb, p.Fragment); sb.Append(',');
            Cell(sb, p.FragmentLevel?.ToString(CultureInfo.InvariantCulture) ?? string.Empty); sb.Append(',');
            Cell(sb, p.Outcome); sb.Append(',');
            Cell(sb, string.Join("; ", p.Components)); sb.Append(',');
            Cell(sb, p.ResultLevel?.ToString(CultureInfo.InvariantCulture) ?? string.Empty); sb.Append(',');
            Cell(sb, p.Entries?.ToString(CultureInfo.InvariantCulture) ?? string.Empty); sb.Append(',');
            Cell(sb, p.Complete ? "yes" : "no (" + p.End + ")"); sb.Append(',');
            Cell(sb, p.Modifiers.Count.ToString(CultureInfo.InvariantCulture)); sb.Append(',');
            Cell(sb, string.Join(" | ", mods));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    private static void Cell(StringBuilder sb, string s)
    {
        // A spreadsheet would run a cell starting with = + - @ as a formula: start it with ' instead.
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@') s = "'" + s;
        bool quote = s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
        if (!quote) { sb.Append(s); return; }
        sb.Append('"').Append(s.Replace("\"", "\"\"")).Append('"');
    }
}
