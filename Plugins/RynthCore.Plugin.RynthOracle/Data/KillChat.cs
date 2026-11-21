// Kill task counts from chat (2026-11-21): the server says each kill of a kill task's creature as
// "You have killed 27 Empyrean Handlers! You must kill 250 to complete your task." (Silentkelpie's chat
// log), and some tasks only "You have killed some Coral Golem Viceroys". /myquests holds the same count on
// the task's counter flag, but may be read only once a minute; this keeps the last count each creature's
// line gave this session, so the Kill Tasks tab moves with every kill. Pump thread.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>One creature's last kill line: the count and the goal (0: the line gave none), and when.</summary>
internal readonly record struct KillCount(string Creature, int Kills, int Goal, DateTime AtUtc);

internal sealed class KillChat
{
    private static readonly Regex CountRx = new(
        @"^You have killed (?<n>\d{1,6}) (?<what>.+?)! You must kill (?<max>\d{1,6}) to complete your task",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex SomeRx = new(@"^You have killed (?:some|enough|all(?: the)?) (?<what>.+?)[.!]*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly Dictionary<string, KillCount> _byKey = new(StringComparer.Ordinal);

    /// <summary>Goes up with every kill line taken (the view redraws).</summary>
    public int Revision { get; private set; }

    public IReadOnlyDictionary<string, KillCount> ByCreature => _byKey;

    public void Clear()
    {
        if (_byKey.Count == 0) return;
        _byKey.Clear();
        Revision++;
    }

    /// <summary>
    /// Takes a chat line; true when it was a kill task's kill line (the counter flags then need reading
    /// again). A "some X" line keeps the count it had, so it only marks the creature as being killed.
    /// </summary>
    public bool OnChatLine(string? line, DateTime utcNow)
    {
        if (string.IsNullOrEmpty(line) || !line.StartsWith("You have killed ", StringComparison.OrdinalIgnoreCase)) return false;
        string text = line.Trim();
        Match m = CountRx.Match(text);
        if (m.Success)
        {
            string what = m.Groups["what"].Value.Trim();
            int n = int.Parse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            int max = int.Parse(m.Groups["max"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            _byKey[GuideQuest.KillKey(what)] = new KillCount(what, n, max, utcNow);
            Revision++;
            return true;
        }
        m = SomeRx.Match(text);
        if (!m.Success) return false;
        string some = m.Groups["what"].Value.Trim();
        string key = GuideQuest.KillKey(some);
        _byKey[key] = _byKey.TryGetValue(key, out KillCount had) ? had with { AtUtc = utcNow } : new KillCount(some, 0, 0, utcNow);
        Revision++;
        return true;
    }

    /// <summary>
    /// The last kill line for a task's creature: the same key, else one that holds the other
    /// ("Rare Game" tasks name many creatures, so a key shorter than 5 letters never matches loosely).
    /// </summary>
    public KillCount? For(string creatureKey)
    {
        if (creatureKey.Length == 0) return null;
        if (_byKey.TryGetValue(creatureKey, out KillCount k)) return k;
        if (creatureKey.Length < 5) return null;
        KillCount? best = null;
        foreach (KeyValuePair<string, KillCount> kv in _byKey)
            if (kv.Key.Length >= 5 && (kv.Key.Contains(creatureKey, StringComparison.Ordinal) || creatureKey.Contains(kv.Key, StringComparison.Ordinal))
                && (best == null || kv.Value.AtUtc > best.Value.AtUtc))
                best = kv.Value;
        return best;
    }
}
