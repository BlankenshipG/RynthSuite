using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RynthCore.Plugin.UbRythai;

/// <summary>
/// ub-IT's PeriodicCommandsTool list ("/ub periodic add|remove|toggle|list|clear"): each entry is
/// a chat command with its own interval in minutes. Stored in
/// <see cref="UbRythaiSettings.PeriodicEntriesText"/>, one "minutes|on|command" line per entry, so
/// it saves with the character's other ub-Rythai settings. This is separate from the
/// settings-window periodic batch (all lines on one interval), which keeps working as before.
/// </summary>
internal sealed class UbRythaiPeriodicEntries
{
    internal readonly record struct Entry(int IntervalMinutes, bool Enabled, string Command);

    private readonly Func<UbRythaiSettings> _getSettings;
    private readonly Func<string, string, bool> _dispatch;
    private readonly Action<string> _say;
    private readonly Action _save;

    // When each entry (by its text) last ran. Entries start their clock at login (or when added),
    // as ub-IT's timer does, rather than all firing on the first tick.
    private readonly Dictionary<string, DateTime> _lastRunUtc = new(StringComparer.Ordinal);

    public UbRythaiPeriodicEntries(Func<UbRythaiSettings> getSettings, Func<string, string, bool> dispatch,
        Action<string> say, Action save)
    {
        _getSettings = getSettings;
        _dispatch = dispatch;
        _say = say;
        _save = save;
    }

    /// <summary>Restarts every entry's clock (login).</summary>
    public void Reset() => _lastRunUtc.Clear();

    /// <summary>Runs every enabled entry whose interval has passed. Plugin tick.</summary>
    public void OnTick(DateTime nowUtc)
    {
        List<Entry> list = Parse(_getSettings().PeriodicEntriesText);
        for (int i = 0; i < list.Count; i++)
        {
            Entry e = list[i];
            string key = Key(i, e);
            if (!_lastRunUtc.TryGetValue(key, out DateTime last))
            {
                _lastRunUtc[key] = nowUtc;
                continue;
            }
            if (!e.Enabled || (nowUtc - last).TotalMinutes < e.IntervalMinutes)
                continue;
            _lastRunUtc[key] = nowUtc;
            _dispatch(e.Command, "periodic entry");
        }
    }

    /// <summary>"/ub periodic &lt;verb&gt; [args]" for the list verbs. Returns false for any other verb.</summary>
    public bool HandleCommand(string verb, string args)
    {
        switch (verb.ToLowerInvariant())
        {
            case "add": Add(args); return true;
            case "remove": Remove(args); return true;
            case "toggle": Toggle(args); return true;
            case "list": ListAll(); return true;
            case "clear": Write(new List<Entry>()); _say("periodic clear: all periodic commands removed"); return true;
            default: return false;
        }
    }

    private void Add(string args)
    {
        string[] p = (args ?? "").Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 2 || !int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes) || minutes < 1)
        {
            _say("Usage: /ub periodic add <minutes> <command> (minutes is a positive whole number)");
            return;
        }
        List<Entry> list = Parse(_getSettings().PeriodicEntriesText);
        list.Add(new Entry(minutes, true, p[1].Trim()));
        Write(list);
        _say($"periodic add: added [{minutes}m] {p[1].Trim()} (entry #{list.Count})");
    }

    private void Remove(string args)
    {
        List<Entry> list = Parse(_getSettings().PeriodicEntriesText);
        if (!TryIndex(args, list.Count, "remove", out int idx)) return;
        Entry removed = list[idx];
        list.RemoveAt(idx);
        Write(list);
        _say($"periodic remove: removed entry #{idx + 1}: [{removed.IntervalMinutes}m] {removed.Command}");
    }

    private void Toggle(string args)
    {
        List<Entry> list = Parse(_getSettings().PeriodicEntriesText);
        if (!TryIndex(args, list.Count, "toggle", out int idx)) return;
        Entry e = list[idx] with { Enabled = !list[idx].Enabled };
        list[idx] = e;
        Write(list);
        _say($"periodic toggle: entry #{idx + 1} is now {(e.Enabled ? "enabled" : "disabled")}");
    }

    private void ListAll()
    {
        List<Entry> list = Parse(_getSettings().PeriodicEntriesText);
        if (list.Count == 0) { _say("periodic list: no commands configured"); return; }
        _say($"periodic list ({list.Count} entries):");
        for (int i = 0; i < list.Count; i++)
            _say($"  [{i + 1}] every {list[i].IntervalMinutes}m  {(list[i].Enabled ? "[ON]" : "[OFF]")}  {list[i].Command}");
    }

    /// <summary>A 1-based index from the command, as ub-IT takes it; 0-based out.</summary>
    private bool TryIndex(string args, int count, string verb, out int idx)
    {
        idx = -1;
        if (!int.TryParse((args ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1)
        {
            _say($"Usage: /ub periodic {verb} <N> (1-based index)");
            return false;
        }
        if (n > count)
        {
            _say($"periodic {verb}: index {n} out of range (1-{count})");
            return false;
        }
        idx = n - 1;
        return true;
    }

    private void Write(List<Entry> list)
    {
        _getSettings().PeriodicEntriesText = Format(list);
        _lastRunUtc.Clear();
        _save();
    }

    private static string Key(int index, Entry e) => index.ToString(CultureInfo.InvariantCulture) + "|" + e.Command;

    /// <summary>Parses "minutes|on|command" lines; malformed lines are skipped.</summary>
    internal static List<Entry> Parse(string? text)
    {
        var list = new List<Entry>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (string raw in text.Split('\n'))
        {
            string[] p = raw.Trim().Split('|', 3);
            if (p.Length < 3 || p[2].Trim().Length == 0) continue;
            if (!int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes) || minutes < 1) continue;
            list.Add(new Entry(minutes, p[1].Trim().Equals("on", StringComparison.OrdinalIgnoreCase), p[2].Trim()));
        }
        return list;
    }

    internal static string Format(List<Entry> list)
    {
        var sb = new StringBuilder();
        foreach (Entry e in list)
            sb.Append(e.IntervalMinutes.ToString(CultureInfo.InvariantCulture)).Append('|')
              .Append(e.Enabled ? "on" : "off").Append('|').Append(e.Command).Append('\n');
        return sb.ToString();
    }
}
