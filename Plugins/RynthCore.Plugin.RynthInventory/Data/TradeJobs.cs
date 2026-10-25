using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RynthCore.Plugin.RynthInventory.Data;

/// <summary>
/// A started trade, shared by every client through files (2026-10-25). Each account plays in its
/// own AC client, so the client that built the list can't give another account's items: it
/// writes the job, and the client logged into each account gives that account's items (logging
/// onto the character that holds them with /rc switchchar) and writes back how each went.
///
/// Files, in %APPDATA%\RynthCore\inventory\trades\:
///   &lt;id&gt;.job.json               the job; written only by the client that started it (start, cancel)
///   &lt;id&gt;.&lt;account&gt;.status.json   one per giving account; written only by that account's client
/// One writer per file, every write a temp file moved into place, so nothing is ever half read.
/// Hand-written JSON read with JsonDocument, like the other RynthInventory files.
/// </summary>
internal sealed class TradeJob
{
    public string Id = string.Empty;
    public string Server = string.Empty;
    public string Recipient = string.Empty;
    public string StartedBy = string.Empty;
    public DateTime StartedUtc;
    public bool Cancelled;
    public readonly List<TradeItem> Items = new();
}

/// <summary>One account's report on a job: where its client is and each of its items.</summary>
internal sealed class TradeAccountStatus
{
    public string Account = string.Empty;
    /// <summary>The character that account's client is on (or switching to).</summary>
    public string Character = string.Empty;
    public string Message = string.Empty;
    public DateTime UpdatedUtc;
    /// <summary>Item index in the job -> (status, text).</summary>
    public readonly Dictionary<int, (TradeItemStatus Status, string Text)> Items = new();
}

internal static class TradeJobs
{
    /// <summary>Jobs (and their status files) this old are deleted by whichever client sees them.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    public static string Folder(string inventoryRoot) => Path.Combine(inventoryRoot, "trades");

    public static string NewId() =>
        DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" +
        Guid.NewGuid().ToString("N").Substring(0, 6);

    private static string JobPath(string folder, string id) => Path.Combine(folder, id + ".job.json");

    private static string StatusPath(string folder, string id, string account) =>
        Path.Combine(folder, id + "." + SafeName(account) + ".status.json");

    private static string SafeName(string s)
    {
        var sb = new StringBuilder();
        foreach (char ch in s.Trim().ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        return sb.Length > 0 ? sb.ToString() : "unknown";
    }

    public static bool SameAccount(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    // ── Writing ───────────────────────────────────────────────────────────

    public static void WriteJob(string folder, TradeJob job)
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"id\": "); InventoryJson.Str(sb, job.Id);
        sb.Append(",\n  \"server\": "); InventoryJson.Str(sb, job.Server);
        sb.Append(",\n  \"recipient\": "); InventoryJson.Str(sb, job.Recipient);
        sb.Append(",\n  \"startedBy\": "); InventoryJson.Str(sb, job.StartedBy);
        sb.Append(",\n  \"startedUtc\": "); InventoryJson.Str(sb, job.StartedUtc.ToString("o", CultureInfo.InvariantCulture));
        sb.Append(",\n  \"cancelled\": ").Append(job.Cancelled ? "true" : "false");
        sb.Append(",\n  \"items\": [");
        for (int i = 0; i < job.Items.Count; i++)
        {
            TradeItem it = job.Items[i];
            sb.Append(i == 0 ? "\n    {" : ",\n    {");
            sb.Append("\"characterKey\": "); InventoryJson.Str(sb, it.CharacterKey);
            sb.Append(", \"character\": "); InventoryJson.Str(sb, it.CharacterName);
            sb.Append(", \"account\": "); InventoryJson.Str(sb, it.Account);
            sb.Append(", \"server\": "); InventoryJson.Str(sb, it.Server);
            sb.Append(", \"name\": "); InventoryJson.Str(sb, it.ItemName);
            sb.Append(", \"wcid\": ").Append(it.Wcid.ToString(CultureInfo.InvariantCulture));
            sb.Append(", \"qty\": ").Append(it.Quantity.ToString(CultureInfo.InvariantCulture));
            sb.Append(", \"itemId\": ").Append(it.ItemId.ToString(CultureInfo.InvariantCulture));
            sb.Append('}');
        }
        sb.Append("\n  ]\n}\n");
        WriteAtomic(JobPath(folder, job.Id), sb.ToString());
    }

    public static void WriteStatus(string folder, string jobId, TradeAccountStatus st)
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"account\": "); InventoryJson.Str(sb, st.Account);
        sb.Append(",\n  \"character\": "); InventoryJson.Str(sb, st.Character);
        sb.Append(",\n  \"message\": "); InventoryJson.Str(sb, st.Message);
        sb.Append(",\n  \"updatedUtc\": "); InventoryJson.Str(sb, st.UpdatedUtc.ToString("o", CultureInfo.InvariantCulture));
        sb.Append(",\n  \"items\": [");
        bool first = true;
        foreach (var kv in st.Items)
        {
            sb.Append(first ? "\n    {" : ",\n    {");
            first = false;
            sb.Append("\"i\": ").Append(kv.Key.ToString(CultureInfo.InvariantCulture));
            sb.Append(", \"status\": "); InventoryJson.Str(sb, kv.Value.Status.ToString());
            sb.Append(", \"text\": "); InventoryJson.Str(sb, kv.Value.Text);
            sb.Append('}');
        }
        sb.Append("\n  ]\n}\n");
        WriteAtomic(StatusPath(folder, jobId, st.Account), sb.ToString());
    }

    private static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Deletes a job and every status file of it (Clear / New on the client that started it).</summary>
    public static void Delete(string folder, string id)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (string f in Directory.GetFiles(folder, id + ".*"))
                try { File.Delete(f); } catch { }
        }
        catch { }
    }

    // ── Reading ───────────────────────────────────────────────────────────

    /// <summary>Every job on <paramref name="server"/>, oldest first. Deletes jobs older than a day.</summary>
    public static List<TradeJob> ReadJobs(string folder, string server)
    {
        var jobs = new List<TradeJob>();
        try
        {
            if (!Directory.Exists(folder)) return jobs;
            foreach (string path in Directory.GetFiles(folder, "*.job.json"))
            {
                TradeJob? job = ReadJob(path);
                if (job == null) continue;
                if (DateTime.UtcNow - job.StartedUtc > MaxAge)
                {
                    Delete(folder, job.Id);
                    continue;
                }
                if (InventoryIndex.SameName(job.Server, server)) jobs.Add(job);
            }
        }
        catch { }
        jobs.Sort((a, b) => a.StartedUtc.CompareTo(b.StartedUtc));
        return jobs;
    }

    public static TradeJob? ReadJob(string folder, string id) => ReadJob(JobPath(folder, id));

    private static TradeJob? ReadJob(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            var job = new TradeJob
            {
                Id = Str(r, "id"),
                Server = Str(r, "server"),
                Recipient = Str(r, "recipient"),
                StartedBy = Str(r, "startedBy"),
                Cancelled = r.TryGetProperty("cancelled", out JsonElement c) && c.ValueKind == JsonValueKind.True,
            };
            if (DateTime.TryParse(Str(r, "startedUtc"), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTime started))
                job.StartedUtc = started.ToUniversalTime();
            if (job.Id.Length == 0) return null;
            if (r.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in items.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    job.Items.Add(new TradeItem
                    {
                        CharacterKey = Str(e, "characterKey"),
                        CharacterName = Str(e, "character"),
                        Account = Str(e, "account"),
                        Server = Str(e, "server"),
                        ItemName = Str(e, "name"),
                        Wcid = (uint)Long(e, "wcid"),
                        Quantity = (int)Long(e, "qty"),
                        ItemId = (uint)Long(e, "itemId"),
                    });
                }
            }
            return job;
        }
        catch
        {
            return null;   // half-moved or broken: read again next time
        }
    }

    /// <summary>Every account's status file for a job.</summary>
    public static List<TradeAccountStatus> ReadStatuses(string folder, string id)
    {
        var list = new List<TradeAccountStatus>();
        try
        {
            if (!Directory.Exists(folder)) return list;
            foreach (string path in Directory.GetFiles(folder, id + ".*.status.json"))
            {
                TradeAccountStatus? st = ReadStatus(path);
                if (st != null) list.Add(st);
            }
        }
        catch { }
        return list;
    }

    public static TradeAccountStatus? ReadStatus(string folder, string id, string account) =>
        ReadStatus(StatusPath(folder, id, account));

    private static TradeAccountStatus? ReadStatus(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            var st = new TradeAccountStatus
            {
                Account = Str(r, "account"),
                Character = Str(r, "character"),
                Message = Str(r, "message"),
            };
            if (DateTime.TryParse(Str(r, "updatedUtc"), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTime updated))
                st.UpdatedUtc = updated.ToUniversalTime();
            if (r.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in items.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    int i = (int)Long(e, "i");
                    if (Enum.TryParse(Str(e, "status"), out TradeItemStatus s))
                        st.Items[i] = (s, Str(e, "text"));
                }
            }
            return st;
        }
        catch
        {
            return null;
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l : 0;
}
