// IltTempleAttributes.cs - Temple of Enlightenment attribute turn-in tracker (Guardian window).
//
// One row per Fiun attribute NPC plus the Guardian of Attribute Enlightenment riddle. Each row
// prefers the timed flag from /myquests (solves + exact cooldown). When /myquests lacks the flag
// it falls back to the "/qb list" wait stamp: a stamp appearing between two lists counts one
// turn-in and starts an estimated cooldown (CooldownHoursAssumed). Fallback counts and timers
// persist in IltGuardianState.
//
// Rows come from "infi attribute.csv" (RynthAi folder, then Documents\Asheron's Call - the UB
// location) when present: npc, attribute, stamp [, myQuestsKey]. Built-in rows missing from the
// file are appended. Pump thread computes; the render thread only reads Snapshot.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Install;
using RynthCore.Plugin.RynthAi.Meta;

namespace RynthCore.Plugin.RynthAi.IltHub;

/// <summary>One tracked turn-in: the NPC, its attribute, the /qb wait flag and the /myquests keys to try.</summary>
internal sealed class IltTempleAttrRow
{
    public IltTempleAttrRow(string npc, string attribute, string stamp, string? myQuestsKey = null)
    {
        Npc = npc.Trim();
        Attribute = attribute.Trim();
        QbWaitFlag = ExtractQbFlag(stamp);
        var keys = new List<string>();
        void Add(string? k)
        {
            if (string.IsNullOrWhiteSpace(k)) return;
            string t = k.Trim().ToLowerInvariant();
            if (!keys.Contains(t)) keys.Add(t);
        }
        Add(myQuestsKey);
        Add(QbWaitFlag);
        if (QbWaitFlag.EndsWith("Wait", StringComparison.OrdinalIgnoreCase) && QbWaitFlag.Length > 4)
            Add(QbWaitFlag[..^4]);
        MyQuestsKeys = keys.ToArray();
    }

    public string Npc { get; }
    public string Attribute { get; }
    /// <summary>/qb flag name (e.g. TempleAttributeStrengthWait).</summary>
    public string QbWaitFlag { get; }
    /// <summary>Lowercase /myquests keys in lookup order: CSV override, the wait flag, the flag without "Wait".</summary>
    public string[] MyQuestsKeys { get; }

    /// <summary>"You've stamped X!" → "X"; a bare flag name is returned trimmed.</summary>
    private static string ExtractQbFlag(string stamp)
    {
        string s = (stamp ?? string.Empty).Trim();
        const string prefix = "You've stamped ";
        if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) s = s[prefix.Length..];
        return s.Trim().TrimEnd('!', ' ', '\t', '\r', '\n');
    }
}

/// <summary>What the Guardian window shows for one row.</summary>
internal sealed record IltTempleAttrStatus(string Attribute, string Npc, int Count, string Status, string Eta, bool Ready, bool FromMyQuests);

internal sealed class IltTempleAttributes
{
    private const long RebuildIntervalMs = 1000;
    private const string CsvName = "infi attribute.csv";

    private static readonly IltTempleAttrRow[] DefaultRows =
    {
        new("Fiun Luunere", "Strength", "TempleAttributeStrengthWait"),
        new("Fiun Bayaas", "Coordination", "TempleAttributeCoordinationWait"),
        new("Fiun Ruun", "Endurance", "TempleAttributeEnduranceWait"),
        new("Fiun Riish", "Quickness", "TempleAttributeQuicknessWait"),
        new("Fiun Vasherr", "Focus", "TempleAttributeFocusWait"),
        new("Fiun Noress", "Willpower", "TempleAttributeSelfWait"),
        new("Fiun Vaelesh", "Health", "TempleAttributeHealthWait"),
        new("Fiun Torrun", "Stamina", "TempleAttributeStamWait"),
        new("Fiun Saeluun", "Mana", "TempleAttributeManaWait"),
        new("Guardian (Attr)", "Riddle", "TempleAttributeRiddleWait"),
    };

    private readonly IltHubContext _ctx;
    private readonly Func<QuestTracker?> _tracker;
    private readonly IltQuests _quests;
    private readonly IltTempleAttrRow[] _rows;
    // Wait-stamp presence per attribute at the previous "/qb list" (session only: a transition
    // needs two lists in the same session).
    private readonly Dictionary<string, bool> _lastHadWait = new(StringComparer.OrdinalIgnoreCase);
    private int _seenQbGeneration;
    private long _lastRebuildAt = -RebuildIntervalMs;

    private volatile IltTempleAttrStatus[] _snapshot = Array.Empty<IltTempleAttrStatus>();
    private DateTime _syncedUtc = DateTime.MinValue;

    public IltTempleAttributes(IltHubContext ctx, Func<QuestTracker?> tracker, IltQuests quests)
    {
        _ctx = ctx;
        _tracker = tracker;
        _quests = quests;
        _rows = LoadRows(out string source);
        RynthLog.Write(LogCat.IltHub, $"[IltGuardian] attribute rows: {_rows.Length} from {source}");
    }

    /// <summary>Rows as last computed (render thread reads this).</summary>
    public IltTempleAttrStatus[] Snapshot => _snapshot;

    /// <summary>When the quest data behind the rows last changed (MinValue = never this session).</summary>
    public DateTime SyncedUtc => _syncedUtc;

    private IltGuardianState S => _ctx.State.Guardian;

    // ── Rows file ───────────────────────────────────────────────────────────

    /// <summary>The CSV in the RynthAi folder, else UB's Documents location; built-in rows otherwise.</summary>
    private static IltTempleAttrRow[] LoadRows(out string source)
    {
        string[] candidates =
        {
            Path.Combine(RynthInstallPaths.RynthAiDir, CsvName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Asheron's Call", CsvName),
        };
        foreach (string path in candidates)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var rows = ParseCsv(File.ReadAllLines(path));
                if (rows.Count == 0) continue;
                source = path;
                return MergeMissingDefaults(rows);
            }
            catch (Exception ex) { RynthLog.Exception(LogCat.IltHub, ex, $"read {path}"); }
        }
        source = "built-in table";
        return DefaultRows;
    }

    /// <summary>npc, attribute, stamp [, myQuestsKey]; a header row and blank lines are skipped.</summary>
    private static List<IltTempleAttrRow> ParseCsv(string[] lines)
    {
        var rows = new List<IltTempleAttrRow>();
        foreach (string raw in lines)
        {
            string line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0) continue;
            if (line.StartsWith("npc", StringComparison.OrdinalIgnoreCase) && line.Contains("attribute", StringComparison.OrdinalIgnoreCase))
                continue;
            string[] seg = line.Split(',');
            if (seg.Length < 3) continue;
            string npc = seg[0].Trim(), attr = seg[1].Trim();
            string stamp;
            string? key = null;
            if (seg.Length == 4) { stamp = seg[2].Trim(); key = seg[3].Trim(); }
            else stamp = string.Join(",", seg.Skip(2)).Trim(); // a full stamp chat line may contain commas
            if (npc.Length == 0 || attr.Length == 0 || stamp.Length == 0) continue;
            rows.Add(new IltTempleAttrRow(npc, attr, stamp, key));
        }
        return rows;
    }

    private static IltTempleAttrRow[] MergeMissingDefaults(List<IltTempleAttrRow> loaded)
    {
        var seen = new HashSet<string>(loaded.Select(r => r.QbWaitFlag), StringComparer.OrdinalIgnoreCase);
        foreach (var def in DefaultRows)
            if (seen.Add(def.QbWaitFlag)) loaded.Add(def);
        return loaded.ToArray();
    }

    // ── Pump thread ─────────────────────────────────────────────────────────

    public void Tick(long nowMs)
    {
        int gen = _quests.QbGeneration;
        if (gen != _seenQbGeneration)
        {
            _seenQbGeneration = gen;
            if (gen > 0) ApplyQbTransitions();
            _syncedUtc = DateTime.UtcNow;
        }
        if (nowMs - _lastRebuildAt < RebuildIntervalMs) return;
        _lastRebuildAt = nowMs;
        Rebuild();
    }

    /// <summary>Re-asks the server for /myquests and "/qb list" (each limited to once a minute).</summary>
    public string Refresh() => $"{_quests.RefreshQuests()} {_quests.RefreshQb()}";

    /// <summary>Forgets the counted turn-ins and estimated cooldowns (the /qb fallback only).</summary>
    public void ResetCounts()
    {
        S.TurnInCounts.Clear();
        S.CooldownUntilUtc.Clear();
        _lastRebuildAt = long.MinValue / 2;
    }

    public void OnLogout()
    {
        _lastHadWait.Clear();
        _seenQbGeneration = 0;
        _syncedUtc = DateTime.MinValue;
    }

    private bool TryQuest(IltTempleAttrRow row, out QuestRecord rec)
    {
        rec = null!;
        var t = _tracker();
        if (t == null) return false;
        foreach (string key in row.MyQuestsKeys)
            if (t.TryGetFlag(key, out rec)) return true;
        return false;
    }

    /// <summary>A new "/qb list" arrived: count wait stamps that appeared since the previous list.</summary>
    private void ApplyQbTransitions()
    {
        int hours = Math.Max(1, S.CooldownHoursAssumed);
        foreach (var row in _rows)
        {
            string key = row.Attribute;
            bool hasWait = _quests.QbHasFlag(row.QbWaitFlag);
            if (!TryQuest(row, out _))
            {
                if (_lastHadWait.TryGetValue(key, out bool had) && !had && hasWait)
                {
                    S.TurnInCounts[key] = (S.TurnInCounts.TryGetValue(key, out int n) ? n : 0) + 1;
                    S.CooldownUntilUtc[key] = DateTime.UtcNow.AddHours(hours);
                    RynthLog.Write(LogCat.IltHub, $"[IltGuardian] {key} turn-in counted from /qb (now {S.TurnInCounts[key]})");
                }
                if (!hasWait) S.CooldownUntilUtc.Remove(key);
            }
            _lastHadWait[key] = hasWait;
        }
    }

    private void Rebuild()
    {
        bool qbLoaded = _quests.QbGeneration > 0;
        int hours = Math.Max(1, S.CooldownHoursAssumed);
        var list = new IltTempleAttrStatus[_rows.Length];
        for (int i = 0; i < _rows.Length; i++)
        {
            var row = _rows[i];
            if (TryQuest(row, out var rec))
            {
                list[i] = FromQuest(row, rec);
                continue;
            }

            string key = row.Attribute;
            int count = S.TurnInCounts.TryGetValue(key, out int c) ? c : 0;
            if (!qbLoaded)
            {
                list[i] = new(key, row.Npc, count, "Unknown", "Refresh", false, false);
                continue;
            }
            if (!_quests.QbHasFlag(row.QbWaitFlag))
            {
                list[i] = new(key, row.Npc, count, "Ready", "-", true, false);
                continue;
            }

            // Wait stamp present: show the estimated cooldown, starting one if none is known.
            if (!S.CooldownUntilUtc.TryGetValue(key, out DateTime until))
                S.CooldownUntilUtc[key] = until = DateTime.UtcNow.AddHours(hours);
            until = AsUtc(until);
            TimeSpan left = until - DateTime.UtcNow;
            list[i] = left > TimeSpan.Zero
                ? new(key, row.Npc, count, "Wait stamp", $"~{IltParse.Duration(left)} (est.)", false, false)
                : new(key, row.Npc, count, "Wait stamp", "ready? (est.)", false, false);
        }
        _snapshot = list;
    }

    /// <summary>A row backed by its /myquests flag: exact solves and cooldown.</summary>
    private static IltTempleAttrStatus FromQuest(IltTempleAttrRow row, QuestRecord rec)
    {
        switch (IltQuests.Classify(rec))
        {
            case IltQuests.QuestKind.KillTask:
                return new(row.Attribute, row.Npc, rec.Solves, "Kill task", $"{rec.Solves}/{rec.MaxSolves}", false, true);
            case IltQuests.QuestKind.Once:
                return new(row.Attribute, row.Npc, rec.Solves, "Once", rec.Solves >= 1 ? "done" : "-", false, true);
            default:
                if (rec.IsReady()) return new(row.Attribute, row.Npc, rec.Solves, "Ready", "now", true, true);
                TimeSpan left = rec.TimeUntilReady();
                string at = DateTime.Now.Add(left).ToString("ddd HH:mm");
                return new(row.Attribute, row.Npc, rec.Solves, "Cooldown", $"{IltParse.Duration(left)} ({at})", false, true);
        }
    }

    /// <summary>Stored times may load as Unspecified; they were written as UTC.</summary>
    private static DateTime AsUtc(DateTime dt) => dt.Kind switch
    {
        DateTimeKind.Utc => dt,
        DateTimeKind.Local => dt.ToUniversalTime(),
        _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
    };
}
