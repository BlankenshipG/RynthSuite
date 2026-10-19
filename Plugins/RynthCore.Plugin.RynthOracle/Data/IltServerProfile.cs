using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>One Leaftide feature from RynthILT's profile: its state on this server and where it lives.</summary>
internal sealed record IltFeature(string Key, string Label, string Group, int State, string Where);

/// <summary>
/// The ACECustom quest bonus list ("/qb list") as RynthILT last read it (the profile's "questBonus"
/// object). <see cref="Generation"/> 0 = not loaded this session; <see cref="Count"/> -1 = unknown.
/// <see cref="CompletedCount"/> / <see cref="IncompleteCount"/> are the list sizes RynthILT reports
/// (-1 = an older RynthILT that sent the lists inline); from RynthILT 0.2.22 the lists themselves
/// arrive separately (<see cref="IltServerLink.QuestBonus"/>).
/// </summary>
internal sealed record IltQuestBonusList(bool Disabled, int Generation, long Count, string Status,
                                         IReadOnlyList<string> Completed, IReadOnlyList<string> Incomplete,
                                         int CompletedCount = -1, int IncompleteCount = -1)
{
    /// <summary>The profile names flags whose lists it didn't carry: they come from GetQuestBonusJson.</summary>
    public bool ListsElsewhere => Completed.Count == 0 && Incomplete.Count == 0 && CompletedCount + IncompleteCount > 0;
}

/// <summary>
/// What RynthILT reports about the Infinite Leaftide server (the "RynthILT.Server" profile JSON,
/// Plugins/Shared/RynthIltServerApi.cs): whether this is an ILT world, the Leaftide features with
/// their server state, and the fellowship rules. <see cref="None"/> without RynthILT; every field is
/// read leniently so an older or newer RynthILT still parses.
/// </summary>
internal sealed class IltServerProfile
{
    /// <summary>No RynthILT (or nothing parsed): a retail fellowship and no features.</summary>
    public static readonly IltServerProfile None = new();

    /// <summary>RynthILT answered with a profile.</summary>
    public bool Available { get; private init; }
    public bool Ilt { get; private init; }
    public string World { get; private init; } = "";
    public bool Forced { get; private init; }
    public string Source { get; private init; } = "";
    /// <summary>Last server-options check (UTC); MinValue = never.</summary>
    public DateTime CheckedUtc { get; private init; } = DateTime.MinValue;
    public IReadOnlyList<IltFeature> Features { get; private init; } = Array.Empty<IltFeature>();

    /// <summary>The server's fellowship size limit (retail 9 without RynthILT or off ILT).</summary>
    public int FellowshipMax { get; private init; } = RynthIltServer.RetailFellowshipMax;
    /// <summary>Fellowship feature states (RynthIltServer.State*): Zone Share, Fellowship Blackjack, luminance share.</summary>
    public int ZoneShare { get; private init; } = RynthIltServer.StateUnknown;
    public int Blackjack { get; private init; } = RynthIltServer.StateUnknown;
    public int LuminanceShare { get; private init; } = RynthIltServer.StateUnknown;

    /// <summary>The /qb list (ILT worlds with RynthILT 0.2.3 or later); null = none offered.</summary>
    public IltQuestBonusList? QuestBonus { get; private init; }

    /// <summary>The features in one group, in RynthILT's order.</summary>
    public IEnumerable<IltFeature> InGroup(string group)
    {
        foreach (IltFeature f in Features)
            if (string.Equals(f.Group, group, StringComparison.OrdinalIgnoreCase)) yield return f;
    }

    /// <summary>Parses the profile JSON; <see cref="None"/> when it is empty or not JSON.</summary>
    public static IltServerProfile Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return None;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return None;

            var features = new List<IltFeature>();
            if (root.TryGetProperty("features", out JsonElement fs) && fs.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement f in fs.EnumerateArray())
                {
                    if (f.ValueKind != JsonValueKind.Object) continue;
                    string key = Str(f, "key");
                    if (key.Length == 0) continue;
                    string label = Str(f, "label");
                    features.Add(new IltFeature(key, label.Length > 0 ? label : key, Str(f, "group"),
                                                Int(f, "state", RynthIltServer.StateUnknown), Str(f, "where")));
                }
            }

            bool ilt = Bool(root, "ilt");
            int max = RynthIltServer.RetailFellowshipMax;
            int zone = RynthIltServer.StateUnknown, bj = RynthIltServer.StateUnknown, lum = RynthIltServer.StateUnknown;
            if (root.TryGetProperty("fellowship", out JsonElement fel) && fel.ValueKind == JsonValueKind.Object)
            {
                max = Int(fel, "maxMembers", max);
                zone = Int(fel, "zoneShare", zone);
                bj = Int(fel, "blackjack", bj);
                lum = Int(fel, "luminanceShare", lum);
            }
            // A bogus size would let the tab claim room nobody has; stay within what the reader handles.
            if (max < 1 || max > 64) max = RynthIltServer.RetailFellowshipMax;

            DateTime checkedUtc = DateTime.MinValue;
            string when = Str(root, "checked");
            if (when.Length > 0
                && DateTime.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime t))
                checkedUtc = t;

            return new IltServerProfile
            {
                Available = true,
                Ilt = ilt,
                World = Str(root, "world"),
                Forced = Bool(root, "forced"),
                Source = Str(root, "source"),
                CheckedUtc = checkedUtc,
                Features = features,
                FellowshipMax = max,
                ZoneShare = zone,
                Blackjack = bj,
                LuminanceShare = lum,
                QuestBonus = ParseQuestBonus(root),
            };
        }
        catch (JsonException)
        {
            return None;
        }
    }

    /// <summary>The "questBonus" object, or null when the profile has none.</summary>
    private static IltQuestBonusList? ParseQuestBonus(JsonElement root)
    {
        if (!root.TryGetProperty("questBonus", out JsonElement q) || q.ValueKind != JsonValueKind.Object) return null;
        long count = q.TryGetProperty("count", out JsonElement c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt64(out long n) ? n : -1;
        return new IltQuestBonusList(Bool(q, "disabled"), Int(q, "generation", 0), count, Str(q, "status"),
                                     Strings(q, "completed"), Strings(q, "incomplete"),
                                     Int(q, "completedCount", -1), Int(q, "incompleteCount", -1));
    }

    /// <summary>
    /// The GetQuestBonusJson payload ({"generation":n,"completed":[...],"incomplete":[...]}), or null
    /// when it doesn't parse.
    /// </summary>
    internal static (int Generation, string[] Completed, string[] Incomplete)? ParseQuestBonusLists(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            return (Int(root, "generation", 0), Strings(root, "completed"), Strings(root, "incomplete"));
        }
        catch (JsonException) { return null; }
    }

    private static string[] Strings(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out JsonElement a) || a.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        var list = new List<string>(a.GetArrayLength());
        foreach (JsonElement e in a.EnumerateArray())
            if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s) list.Add(s);
        return list.ToArray();
    }

    /// <summary>"on", "off" or "checking" for a feature state.</summary>
    public static string StateText(int state) => state switch
    {
        RynthIltServer.StateOn => "on",
        RynthIltServer.StateOff => "off",
        _ => "checking",
    };

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement o, string name) =>
        o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

    private static int Int(JsonElement o, string name, int fallback) =>
        o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : fallback;
}

/// <summary>
/// Reads RynthILT's "RynthILT.Server" interface on the plugin pump. Looks the table up through the
/// host (re-checked while RynthILT is missing or after it unloads, Version 0) and copies the JSON
/// out every <see cref="PollMs"/>; <see cref="Revision"/> goes up when the profile changes.
/// </summary>
internal sealed unsafe class IltServerLink
{
    private const long PollMs = 2000;
    private const long RequeryMs = 10_000;
    // The profile: features and fellowship rules, plus the /qb lists from an older RynthILT.
    private const int MaxBuffer = 512 * 1024;
    // The /qb lists (GetQuestBonusJson): 25,000 flags are about 1 MB; room for well past that.
    private const int MaxQuestBonusBuffer = 8 * 1024 * 1024;

    private RynthIltServerApiV1* _table;
    private long _nextPollAt, _nextQueryAt;
    private byte[] _buffer = new byte[8192];
    private string _lastJson = "";
    // The /qb lists read through GetQuestBonusJson, and the profile generation they belong to.
    private string[] _qbCompleted = Array.Empty<string>(), _qbIncomplete = Array.Empty<string>();
    private int _qbListGeneration = -1;

    public IltServerProfile Profile { get; private set; } = IltServerProfile.None;
    public int Revision { get; private set; }

    /// <summary>
    /// The /qb summary with its flag lists filled in: inline from an older RynthILT, otherwise the
    /// ones read through GetQuestBonusJson for this generation (empty until that read lands).
    /// </summary>
    public IltQuestBonusList? QuestBonus
    {
        get
        {
            IltQuestBonusList? qb = Profile.QuestBonus;
            if (qb == null || !qb.ListsElsewhere || qb.Generation != _qbListGeneration) return qb;
            return qb with { Completed = _qbCompleted, Incomplete = _qbIncomplete };
        }
    }

    /// <summary>Forget the profile (logout); the next poll reads it again.</summary>
    public void Clear()
    {
        _nextPollAt = 0;
        SetJson("");
        _qbCompleted = _qbIncomplete = Array.Empty<string>();
        _qbListGeneration = -1;
    }

    /// <summary>Pump thread. Cheap when not due.</summary>
    public void Poll(RynthCoreHost host, long now)
    {
        if (now < _nextPollAt) return;
        _nextPollAt = now + PollMs;
        RynthIltServerApiV1* t = Table(host, now);
        SetJson(t == null ? "" : Read(t, t->GetProfileJson, MaxBuffer));

        // The /qb lists move only when RynthILT parses a new "/qb list" (generation), so the
        // ~1 MB read happens once per list rather than every poll.
        if (Profile.QuestBonus is { ListsElsewhere: true } qb && qb.Generation != _qbListGeneration
            && RynthIltServerApiV1.HasQuestBonus(t))
            SetQuestBonusJson(Read(t, t->GetQuestBonusJson, MaxQuestBonusBuffer));
    }

    /// <summary>The table, re-queried when it is missing or RynthILT has shut down (Version 0).</summary>
    private RynthIltServerApiV1* Table(RynthCoreHost host, long now)
    {
        if (_table != null && _table->Version >= 1) return _table;
        _table = null;
        if (now < _nextQueryAt || !host.HasGetPluginInterface) return null;
        _nextQueryAt = now + RequeryMs;
        // Base version and a version-1-sized table, so an older RynthILT still answers.
        var p = (RynthIltServerApiV1*)host.GetPluginInterface(RynthIltServer.PluginName, RynthIltServerApiV1.InterfaceName, RynthIltServerApiV1.BaseVersion);
        if (p != null && p->Version >= 1 && p->StructSize >= RynthIltServerApiV1.V1Size && p->GetProfileJson != null)
            _table = p;
        return _table;
    }

    /// <summary>Copies one JSON export out, growing the buffer once when RynthILT says it didn't fit.</summary>
    private string Read(RynthIltServerApiV1* t, delegate* unmanaged[Cdecl]<byte*, int, int> get, int maxBuffer)
    {
        if (t == null || get == null) return "";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            int len;
            fixed (byte* b = _buffer) len = get(b, _buffer.Length);
            if (len <= 0) return "";
            if (len < _buffer.Length) return Encoding.UTF8.GetString(_buffer, 0, len);
            if (len + 1 > maxBuffer) return "";
            _buffer = new byte[len + 1024];
        }
        return "";
    }

    /// <summary>Takes a profile JSON (internal: the tests feed one without RynthILT).</summary>
    internal void SetJson(string json)
    {
        if (json == _lastJson) return;
        _lastJson = json;
        Profile = IltServerProfile.Parse(json);
        Revision++;
    }

    /// <summary>Takes a GetQuestBonusJson payload (internal: the tests feed one without RynthILT).</summary>
    internal void SetQuestBonusJson(string json)
    {
        if (IltServerProfile.ParseQuestBonusLists(json) is not { } lists) return;
        _qbCompleted = lists.Completed;
        _qbIncomplete = lists.Incomplete;
        _qbListGeneration = lists.Generation;
        Revision++;
    }
}
