// IltBankExport.cs - The bank JSON of "RynthILT.Data" (Plugins/Shared/RynthIltDataApi.cs): the
// Banking tab's balances, form choices and log for another plugin's view (every character here).
//
// Every character RynthILT has run on this server keeps its balances in its own hub.json
// (<RynthILT folder>\<world>\<character>\hub.json, IltHubStore); the export lists them all, the
// logged-in character from its live state and the rest from their saved files (read on the pump
// at most every SiblingReadMs, so a reader polling every couple of seconds costs one directory
// listing now and then). Plus the form choices (IltBankCommands), the last action's reply and
// the recent transfers. Build is pure: the tests feed it rows.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthILT.Hub;

internal static class IltBankExport
{
    /// <summary>How many recent transfers the JSON carries (newest first).</summary>
    public const int MaxTransactions = 50;

    /// <summary>One character's balances as exported.</summary>
    internal sealed record Row(string Name, bool Current, IltBankState Bank);

    /// <summary>
    /// The bank JSON. <paramref name="enabled"/>: 1 the server has /bank, 0 not, -1 not known yet.
    /// Rows are listed current character first, then by name. Plain ASCII.
    /// </summary>
    public static string Build(bool available, int enabled, string world, string character, IReadOnlyList<Row> rows,
                               string result, IReadOnlyList<IltBankTransaction> transactions)
    {
        var sb = new StringBuilder(4096);
        sb.Append("{\"available\":").Append(available ? "true" : "false");
        sb.Append(",\"enabled\":").Append(Math.Clamp(enabled, -1, 1).ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"world\":\"").Append(IltStyle.JsonEscape(world ?? string.Empty)).Append('"');
        sb.Append(",\"character\":\"").Append(IltStyle.JsonEscape(character ?? string.Empty)).Append('"');

        var ordered = new List<Row>(rows);
        ordered.Sort((a, b) => a.Current != b.Current ? (a.Current ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        sb.Append(",\"characters\":[");
        for (int i = 0; i < ordered.Count; i++)
        {
            Row r = ordered[i];
            IltBankState b = r.Bank;
            if (i > 0) sb.Append(',');
            sb.Append("{\"name\":\"").Append(IltStyle.JsonEscape(r.Name)).Append('"');
            sb.Append(",\"current\":").Append(r.Current ? "true" : "false");
            Num(sb, "pyreals", b.Pyreals);
            Num(sb, "luminance", b.Luminance);
            Num(sb, "legendaryKeys", b.LegendaryKeys);
            Num(sb, "mythicalKeys", b.MythicalKeys);
            Num(sb, "enlightenedCoins", b.EnlightenedCoins);
            Num(sb, "weaklyEnlightenedCoins", b.WeaklyEnlightenedCoins);
            Num(sb, "prestigeCoins", b.PrestigeCoins);
            sb.Append(",\"updated\":\"").Append(Utc(b.LastUpdated)).Append("\"}");
        }
        sb.Append(']');

        sb.Append(",\"deposits\":[");
        for (int i = 0; i < IltBankCommands.Deposits.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"').Append(IltStyle.JsonEscape(IltBankCommands.Deposits[i].Label)).Append('"');
        }
        sb.Append(']');
        Choices(sb, "withdrawals", IltBankCommands.Withdrawals, w => w.Code == IltBankCommands.NotesCode);
        Choices(sb, "transfers", IltBankCommands.Transfers, t => t.Code == IltBankCommands.NotesCode);
        sb.Append(",\"notes\":[");
        for (int i = 0; i < IltBankCommands.NoteDenominations.Length; i++)
        {
            var n = IltBankCommands.NoteDenominations[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"label\":\"").Append(IltStyle.JsonEscape(n.Label)).Append('"');
            Num(sb, "value", n.Value);
            sb.Append('}');
        }
        sb.Append(']');

        sb.Append(",\"result\":\"").Append(IltStyle.JsonEscape(result ?? string.Empty)).Append('"');
        sb.Append(",\"transactions\":[");
        int written = 0;
        for (int i = transactions.Count - 1; i >= 0 && written < MaxTransactions; i--, written++)
        {
            IltBankTransaction t = transactions[i];
            if (written > 0) sb.Append(',');
            sb.Append("{\"when\":\"").Append(t.Timestamp == default ? "" : t.Timestamp.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append('"');
            sb.Append(",\"type\":\"").Append(IltStyle.JsonEscape(t.Type)).Append('"');
            Num(sb, "amount", t.Amount);
            sb.Append(",\"currency\":\"").Append(IltStyle.JsonEscape(t.Currency)).Append('"');
            sb.Append(",\"player\":\"").Append(IltStyle.JsonEscape(t.OtherPlayer)).Append("\"}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static void Num(StringBuilder sb, string name, long value)
        => sb.Append(",\"").Append(name).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));

    private static void Choices(StringBuilder sb, string name, (string Label, string Code)[] items, Func<(string Label, string Code), bool> notes)
    {
        sb.Append(",\"").Append(name).Append("\":[");
        for (int i = 0; i < items.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"label\":\"").Append(IltStyle.JsonEscape(items[i].Label)).Append("\",\"notes\":")
              .Append(notes(items[i]) ? "true" : "false").Append('}');
        }
        sb.Append(']');
    }

    /// <summary>A local DateTime as ISO UTC ("" = never).</summary>
    private static string Utc(DateTime local) => local == DateTime.MinValue || local == default
        ? ""
        : local.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>
    /// The other characters' saved balances: every <c>&lt;world folder&gt;\*\hub.json</c> except
    /// <paramref name="currentFolder"/>. A file that can't be read is skipped (its character just
    /// isn't listed). Characters never banked (no LastUpdated) are left out.
    /// </summary>
    public static List<Row> ReadSiblings(string currentFolder)
    {
        var rows = new List<Row>();
        if (string.IsNullOrEmpty(currentFolder)) return rows;
        string? worldDir = Path.GetDirectoryName(currentFolder.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(worldDir) || !Directory.Exists(worldDir)) return rows;
        string self = Path.GetFullPath(currentFolder).TrimEnd('\\', '/');
        foreach (string dir in Directory.EnumerateDirectories(worldDir))
        {
            if (string.Equals(Path.GetFullPath(dir).TrimEnd('\\', '/'), self, StringComparison.OrdinalIgnoreCase)) continue;
            string file = Path.Combine(dir, IltHubStore.StateFile);
            if (!File.Exists(file)) continue;
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var state = JsonSerializer.Deserialize(fs, IltHubJsonContext.Default.IltHubState);
                IltBankState? bank = state?.Bank;
                if (bank == null || bank.LastUpdated == DateTime.MinValue) continue;
                rows.Add(new Row(Path.GetFileName(dir), false, bank));
            }
            catch (Exception ex)
            {
                RynthLog.Trace(LogCat.IltBanking, $"bank export: skipped {file} ({ex.Message})");
            }
        }
        return rows;
    }
}
