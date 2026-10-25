// IltPetBreeding.cs - The pet breeding tracker: the Breeding sub-tab of RynthAi's Mini Remote Pets tab,
// drawn here (RynthILT.Hub section "pets-breeding"; RynthAi only hosts it), and the same data as JSON
// for other plugins ("RynthILT.Data" v2 GetPetBreedingJson / PetBreedingCommand).
// Originally SilentKelpie's (2026-10-15, his RynthILT 0.2.7); ported into RynthILT 0.3.1 without his
// RynthOracle Pet Breeding tab: breeding is Leaftide's, so only this plugin draws it.
//
// What it does, on Infinite Leaftide only:
//   * reads every carried combat essence (appraisal qualities + the LongDesc breeding block) and
//     evaluates it with IltBreedingEssence: stud / dam, each gate that fails, when it clears, growth;
//   * counts Ivo's supplies and applies a breeding booster to an essence after the player confirms;
//   * runs "@breed-debug" (quietly) for the room check and "@dance" to start the ritual;
//   * logs breeding chat (ritual, charge spent, birth, guardian, growth, adulthood, imprint);
//   * learns this server's rules from its own text (IltBreedingRules) and reports what it sees to
//     the Leaftide feature profile (the pet_breeding / pet_maturity bits).
// Essences are read only while someone is looking (the section drawn, or a reader asking for the
// JSON within WatchMs), so an unwatched tracker costs nothing.
// Threading: Tick / OnChat / actions on the pump; Render on the ImGui thread reads only the
// immutable snapshot and posts actions.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using ImGuiNET;
using RynthCore.Loot;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthILT.Hub;

/// <summary>Everything the breeding UI and reader JSON show, rebuilt whole on the pump.</summary>
/// <remarks><see cref="RulesView"/> is a frozen copy of the rules: the render thread plans with it while the pump learns.</remarks>
internal sealed record BreedingSnapshot(
    IReadOnlyList<BreedEssence> Essences, IReadOnlyList<(BreedSupplyDef Def, long Count)> Supplies,
    IReadOnlyList<BreedRuleValue> Rules, IReadOnlyList<IltBreedingEvent> Events, BreedDebugInfo? Debug, DateTime DebugUtc,
    IltTri Enabled, IltTri Maturity, IltTri Bond, string Room, int FreeSlots, string Message, double NowUnix, bool Built,
    IltBreedingRules RulesView)
{
    public static readonly BreedingSnapshot Empty = new(Array.Empty<BreedEssence>(), Array.Empty<(BreedSupplyDef, long)>(),
        Array.Empty<BreedRuleValue>(), Array.Empty<IltBreedingEvent>(), null, default, IltTri.Unknown, IltTri.Unknown,
        IltTri.Unknown, string.Empty, -1, string.Empty, 0, false, new IltBreedingRules(new IltBreedingState()));
}

internal sealed class IltPetBreeding : IIltFeature
{
    /// <summary>Feature bits this tracker reports (IltFeatureCatalog's pet_breeding / pet_maturity rows).</summary>
    public const string BitBreeding = RynthIltHub.FeaturePetBreeding;
    public const string BitMaturity = "pet_maturity";

    /// <summary>ACE commands: the diagnostics reply, and the courtship dance.</summary>
    public const string DebugCommand = "@breed-debug";
    public const string DanceCommand = "@dance";

    // ACECustom pet device qualities (AssessmentProperty, sent with the appraisal).
    private const uint StrLongDesc = 16;
    private const uint IntUseRequiresLevel = 369;
    private const uint IntUseLevelRequirement = 364;
    private const uint IntBondLevel = 9053;
    private const uint IntPotencyStored = 9056;
    private const uint IntMaleCharges = 9057;
    private const uint IntMutDamage = 9070, IntMutDamageResist = 9071, IntMutCrit = 9072, IntMutVitality = 9073, IntMutPotency = 9074;
    private const uint IntMutationCount = 9075;
    private const uint IntMaturityKills = 9077;
    private const uint FloatNextBreeding = 9056;
    private const uint FloatMaleRefresh = 9057;
    private const uint FloatIncense = 9058;
    private const uint FloatMaturityXpMult = 9059;
    private const uint BoolBondAttuned = 9047;
    private const uint BoolNeutered = 9051;
    private const uint BoolMaleOverride = 50053;
    private const uint BoolJuvenile = 50054;
    private const uint BoolCatalyst = 50055;
    private const uint BoolOffering = 50056;

    /// <summary>The tracker reads essences while it was looked at within this long.</summary>
    private const long WatchMs = 6000;
    /// <summary>How often a watched tracker re-reads the essences.</summary>
    private const long ScanMs = 2000;
    /// <summary>Appraisal requests per scan for essences not yet appraised.</summary>
    private const int AppraisePerScan = 2;

    private readonly IltHubContext _ctx;
    private readonly IltBreedingRules _rules;
    private long _watchedAt;              // Interlocked: render thread and readers mark it
    private long _scannedAt;
    private bool _refreshAll;             // re-appraise every essence on the next scan (after an event)
    private string _message = string.Empty;
    private volatile BreedingSnapshot _snap = BreedingSnapshot.Empty;
    private volatile string _readerJson = string.Empty;

    // Render-thread selections (ids of carried essences; 0 = none).
    private int _studId, _damId, _targetId;

    public IltPetBreeding(IltHubContext ctx)
    {
        _ctx = ctx;
        _rules = new IltBreedingRules(ctx.State.Pet.Breeding);
    }

    /// <summary>The rules (tests and the controller).</summary>
    internal IltBreedingRules Rules => _rules;

    /// <summary>The latest snapshot (any thread).</summary>
    public BreedingSnapshot Snapshot => _snap;

    /// <summary>Someone is looking: keep the essences fresh for a while (any thread).</summary>
    public void MarkWatched() => System.Threading.Interlocked.Exchange(ref _watchedAt, IltHubContext.NowMs);

    /// <summary>The reader JSON ("RynthILT.Data" GetPetBreedingJson); reading it marks the tracker watched. Any thread.</summary>
    public string ReaderJson
    {
        get { MarkWatched(); return _readerJson; }
    }

    private static double UnixNow => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    // ── IIltFeature ─────────────────────────────────────────────────────────

    public void Tick(long nowMs)
    {
        if (nowMs - System.Threading.Interlocked.Read(ref _watchedAt) > WatchMs) return;
        if (_scannedAt != 0 && nowMs - _scannedAt < ScanMs) return;
        _scannedAt = nowMs;
        Scan();
    }

    public bool OnChat(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        DateTime utc = DateTime.UtcNow;
        bool changed = _rules.LearnAll(IltBreedingRules.FromChat(text), utc);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            string? kind = EventKind(line);
            if (kind == null) continue;
            _rules.AddEvent(kind, line, utc);
            changed = true;
            if (kind is "ritual" or "birth" or "charge") _ctx.Options.Set(BitBreeding, IltTri.On);
            if (kind is "growth" or "adult")
            {
                _ctx.Options.Set(BitMaturity, IltTri.On);
                if (IltBreedingRules.TryParseGrownInto(line, out int stage, out string name)) _rules.LearnStageName(stage, name);
            }
            // The essences changed on the server (charges, rest, a baby, growth): read them again soon.
            if (kind is not "dance") { _refreshAll = true; _scannedAt = 0; }
        }
        if (changed) RynthLog.Trace(LogCat.IltPets, "breeding: chat updated rules / events");
        return false;
    }

    public void OnLogout()
    {
        _snap = BreedingSnapshot.Empty;
        _readerJson = string.Empty;
        _scannedAt = 0;
        _message = string.Empty;
    }

    /// <summary>The event kind of a breeding chat line, or null for any other line.</summary>
    internal static string? EventKind(string line)
    {
        if (line.Length == 0) return null;
        if (line.StartsWith("The mating ritual has begun between", StringComparison.OrdinalIgnoreCase)) return "ritual";
        if (line.Contains("spent a breeding charge", StringComparison.OrdinalIgnoreCase)) return "charge";
        if (line.Contains("A baby pet has been born", StringComparison.OrdinalIgnoreCase)) return "birth";
        if (line.StartsWith("The union stirs something", StringComparison.OrdinalIgnoreCase)) return "guardian";
        if (line.Contains("yields to its parents", StringComparison.OrdinalIgnoreCase)) return "guardian";
        if (line.Contains("Awakened Blessing", StringComparison.OrdinalIgnoreCase)) return "mutation";
        if (line.Contains("spectral guardian dissolves", StringComparison.OrdinalIgnoreCase)) return "guardian";
        if (line.Contains("has grown into a", StringComparison.OrdinalIgnoreCase)) return "growth";
        if (line.Contains("has reached adulthood", StringComparison.OrdinalIgnoreCase)) return "adult";
        if (line.Contains("has imprinted on you", StringComparison.OrdinalIgnoreCase)) return "imprint";
        if (line.StartsWith("[Breeding] Your pet performs the courtship dance", StringComparison.OrdinalIgnoreCase)) return "dance";
        if (line.StartsWith("You administer the Nurturing Draught", StringComparison.OrdinalIgnoreCase)) return "supply";
        if (line.StartsWith("You have permanently spayed/neutered", StringComparison.OrdinalIgnoreCase)) return "supply";
        return null;
    }

    // ── Scan (pump) ─────────────────────────────────────────────────────────

    /// <summary>Reads every carried combat essence and the supplies, then rebuilds the snapshot and reader JSON.</summary>
    private void Scan()
    {
        IltInventory inv = _ctx.Inventory;
        RynthCoreHost host = _ctx.Host;
        double now = UnixNow;
        DateTime utc = DateTime.UtcNow;
        var essences = new List<BreedEssence>();
        int appraised = 0;
        bool sawBlock = false, sawGrowth = false;

        foreach (WorldObject wo in inv.Items())
        {
            uint wcid = inv.Wcid(wo);
            if (!IltBreedingEssence.IsCombatEssence(wcid) && !LooksLikeEssence(wo, inv)) continue;

            BreedEssenceFacts f = ReadFacts(host, inv, wo, wcid);
            if (f.LongDesc.Contains("Sex:", StringComparison.Ordinal)) sawBlock = true;
            if (f.LongDesc.Contains("Growth:", StringComparison.Ordinal)) sawGrowth = true;
            _rules.LearnAll(IltBreedingRules.FromAppraisal(f.LongDesc, f.MaleRefreshUnix, now), utc);
            if (IltBreedingRules.TryParseGrowth(f.LongDesc, out string stageName, out int stage, out _, out _, out _, out _))
                _rules.LearnStageName(stage, stageName);
            essences.Add(IltBreedingEssence.Evaluate(f, _rules, now));

            // Appraise what isn't loaded (a few per scan), or everything after a breeding event.
            if ((_refreshAll || !f.HasId) && appraised < (_refreshAll ? 32 : AppraisePerScan) && inv.RequestAppraisal(wo))
                appraised++;
        }
        _refreshAll = false;

        // The server only writes the breeding block while breeding is on: seeing it is the server saying so.
        if (sawBlock) _ctx.Options.Set(BitBreeding, IltTri.On);
        if (sawGrowth) _ctx.Options.Set(BitMaturity, IltTri.On);

        essences.Sort((a, b) =>
        {
            int c = b.Ready.CompareTo(a.Ready);
            if (c != 0) return c;
            c = b.Male.CompareTo(a.Male);
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        var supplies = new List<(BreedSupplyDef, long)>(IltBreedingSupplies.All.Length);
        foreach (BreedSupplyDef s in IltBreedingSupplies.All) supplies.Add((s, inv.CountByWcid(s.Wcid)));

        IltBreedingState st = _rules.State;
        BreedDebugInfo? debug = st.LastDebug.Count > 0 ? IltBreedingRules.ParseDebug(st.LastDebug) : null;
        var events = st.Log.Select(e => new IltBreedingEvent { Utc = e.Utc, Kind = e.Kind, Text = e.Text }).ToArray();

        var snap = new BreedingSnapshot(essences, supplies, _rules.All(), events, debug, st.LastDebugUtc,
            EnabledState(), _ctx.Options.Get(BitMaturity), _ctx.Options.Get(IltFeature.PetBond),
            _rules.RoomText(), inv.FreeMainPackSlots(), _message, now, true, _rules.Frozen());
        _snap = snap;
        _readerJson = BuildJson(snap, _rules, _ctx.Options.WorldName);
    }

    /// <summary>The pet_breeding state the Leaftide profile shows (reported, else the ILT profile's "on").</summary>
    private IltTri EnabledState()
    {
        IltTri t = _ctx.Options.Get(BitBreeding);
        return t != IltTri.Unknown ? t : _ctx.Options.IsIltLikeWorld ? IltTri.On : IltTri.Unknown;
    }

    /// <summary>An essence outside the WCID table (a new family) that the server gave a breeding block.</summary>
    private static bool LooksLikeEssence(WorldObject wo, IltInventory inv)
        => wo.Name.EndsWith("Essence", StringComparison.OrdinalIgnoreCase)
           && inv.Str(wo, StrLongDesc).Contains("Sex:", StringComparison.Ordinal);

    private static BreedEssenceFacts ReadFacts(RynthCoreHost host, IltInventory inv, WorldObject wo, uint wcid)
    {
        uint id = unchecked((uint)wo.Id);
        var f = new BreedEssenceFacts
        {
            Id = wo.Id,
            Name = wo.Name,
            Wcid = wcid,
            HasId = inv.HasAppraisal(wo),
            Bond = inv.Int(wo, IntBondLevel),
            PotencyStored = inv.Int(wo, IntPotencyStored),
            MutDamage = inv.Int(wo, IntMutDamage),
            MutDamageResist = inv.Int(wo, IntMutDamageResist),
            MutCrit = inv.Int(wo, IntMutCrit),
            MutVitality = inv.Int(wo, IntMutVitality),
            MutPotency = inv.Int(wo, IntMutPotency),
            MutationCount = inv.Int(wo, IntMutationCount),
            Neutered = TryBool(host, id, BoolNeutered, out bool n) && n,
            JuvenileFlag = TryBool(host, id, BoolJuvenile, out bool j) && j,
            Catalyst = TryBool(host, id, BoolCatalyst, out bool c) && c,
            Offering = TryBool(host, id, BoolOffering, out bool o) && o,
            Attuned = TryBool(host, id, BoolBondAttuned, out bool a) && a,
            NextBreedUnix = TryDouble(host, id, FloatNextBreeding),
            MaleRefreshUnix = TryDouble(host, id, FloatMaleRefresh),
            Incense = TryDouble(host, id, FloatIncense),
            MaturityXpMult = TryDouble(host, id, FloatMaturityXpMult),
            LongDesc = inv.Str(wo, StrLongDesc),
        };
        f.UseLevel = inv.Int(wo, IntUseRequiresLevel);
        if (f.UseLevel <= 0) f.UseLevel = inv.Int(wo, IntUseLevelRequirement);
        if (TryBool(host, id, BoolMaleOverride, out bool male)) f.MaleOverride = male;
        // Absent and 0 differ for these two: absent = never bred / a full stud.
        f.MaturityKills = TryInt(host, id, IntMaturityKills, out int kills) ? kills : -1;
        f.MaleCharges = TryInt(host, id, IntMaleCharges, out int charges) ? charges : -1;
        return f;
    }

    private static bool TryBool(RynthCoreHost host, uint id, uint stype, out bool v)
    {
        v = false;
        return host.HasGetObjectBoolProperty && host.TryGetObjectBoolProperty(id, stype, out v);
    }

    private static bool TryInt(RynthCoreHost host, uint id, uint stype, out int v)
    {
        v = 0;
        return host.HasGetObjectIntProperty && host.TryGetObjectIntProperty(id, stype, out v);
    }

    private static double TryDouble(RynthCoreHost host, uint id, uint stype)
        => host.HasGetObjectDoubleProperty && host.TryGetObjectDoubleProperty(id, stype, out double v) ? v : 0;

    // ── Actions (pump) ──────────────────────────────────────────────────────

    /// <summary>Re-appraises every essence and rescans.</summary>
    public void RefreshAll()
    {
        _refreshAll = true;
        _scannedAt = 0;
        MarkWatched();
    }

    /// <summary>Runs "@breed-debug" quietly and keeps its reply (the room check, partners nearby).</summary>
    public void RequestDebug()
    {
        if (_ctx.Capture.IsPending(DebugCommand)) return;
        _message = "Asking the server for breeding diagnostics...";
        _ctx.Capture.Enqueue(new IltChatRequest
        {
            Command = DebugCommand,
            IsResponseLine = IltBreedingRules.IsDebugLine,
            IsTerminator = IltBreedingRules.IsDebugEnd,
            IdleEndMs = 1500,
            FirstLineTimeoutMs = 8000,
            Eat = true,
            OnComplete = ApplyDebug,
        });
        _scannedAt = 0;
    }

    private void ApplyDebug(IltChatResult r)
    {
        if (r.UnknownCommand) { _message = "This server has no @breed-debug (no pet breeding here)."; _ctx.Options.Set(BitBreeding, IltTri.Off); return; }
        if (r.NotSent) { _message = "This RynthCore can't send chat commands."; return; }
        if (r.Lines.Count == 0) { _message = "No reply to @breed-debug."; return; }
        DateTime utc = DateTime.UtcNow;
        IltBreedingState st = _rules.State;
        st.LastDebug = new List<string>(r.Lines);
        st.LastDebugUtc = utc;
        foreach (string l in r.Lines) _rules.LearnAll(IltBreedingRules.FromChat(l), utc);
        BreedDebugInfo d = IltBreedingRules.ParseDebug(r.Lines);
        if (d.Enabled.HasValue) _ctx.Options.Set(BitBreeding, d.Enabled.Value ? IltTri.On : IltTri.Off);
        _message = d.Enabled == false ? "The server says breeding is switched off."
            : d.LocationOk == true ? $"You're in the breeding room. Partners nearby: {Math.Max(0, d.Partners)}."
            : d.LocationOk == false ? $"Not in the breeding room ({_rules.RoomText()})."
            : "Diagnostics read.";
        _rules.AddEvent("debug", _message, utc);
        _scannedAt = 0;
    }

    /// <summary>Starts the courtship dance ("@dance"); the breed happens when a partner dances in time.</summary>
    public void Dance()
    {
        if (!_ctx.Host.HasInvokeChatParser) { _message = "This RynthCore can't send chat commands."; return; }
        _ctx.Host.InvokeChatParser(DanceCommand);
        _message = $"Danced. Your partner must *dance* within {_rules.Get(IltBreedingRules.DanceWindow):0.#}s with their pet in the same room.";
        _scannedAt = 0;
    }

    /// <summary>
    /// Uses a supply on a carried essence, re-checking both are still here and that the server would
    /// take it. Returns a message for the UI. Called after the player confirmed.
    /// </summary>
    public string ApplySupply(string key, int essenceId)
    {
        BreedSupplyDef? def = IltBreedingSupplies.Find(key);
        if (def == null || !def.Apply) return _message = "That supply isn't applied from here.";
        IltInventory inv = _ctx.Inventory;
        WorldObject? item = inv.FindByWcid(def.Wcid);
        if (item == null) return _message = $"No {def.Name} in your pack.";
        BreedEssence? e = _snap.Essences.FirstOrDefault(x => x.Id == essenceId);
        if (e == null || !inv.Items().Any(wo => wo.Id == essenceId)) return _message = "That essence isn't in your pack any more.";
        string? refuse = IltBreedingSupplies.RefuseReason(def, e, _rules);
        if (refuse != null) return _message = refuse;
        if (inv.IsBusy) return _message = "You're busy; try again in a moment.";
        bool sent = inv.UseOn(item, unchecked((uint)essenceId));
        _message = sent ? $"Used {def.Name} on {e.Name}." : $"Couldn't use {def.Name} (the engine refused).";
        if (sent) _rules.AddEvent("supply", _message, DateTime.UtcNow);
        RefreshAll();
        return _message;
    }

    /// <summary>
    /// A reader's verb line ("RynthILT.Data" PetBreedingCommand): refresh, debug, dance,
    /// or "apply &lt;supplyKey&gt; &lt;essenceId&gt;" (the reader confirmed it with the player). Pump thread.
    /// </summary>
    public void RunVerb(string line)
    {
        string[] parts = (line ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        switch (parts[0].ToLowerInvariant())
        {
            case "refresh": RefreshAll(); break;
            case "debug": RequestDebug(); break;
            case "dance": Dance(); break;
            case "apply" when parts.Length == 3 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id):
                ApplySupply(parts[1], id);
                break;
            default:
                RynthLog.Trace(LogCat.IltPets, $"breeding: unknown reader verb '{line}'");
                break;
        }
    }

    // ── Reader JSON ─────────────────────────────────────────────────────────

    /// <summary>The reader JSON (Plugins/Shared/RynthIltDataApi.cs header has the shape).</summary>
    internal static string BuildJson(BreedingSnapshot s, IltBreedingRules r, string world)
    {
        var sb = new StringBuilder(4096);
        sb.Append("{\"available\":true");
        Str(sb, "world", world);
        Str(sb, "enabled", TriText(s.Enabled));
        Str(sb, "maturity", TriText(s.Maturity));
        Str(sb, "bond", TriText(s.Bond));
        Str(sb, "room", s.Room);
        sb.Append(",\"freeSlots\":").Append(s.FreeSlots);
        Str(sb, "message", s.Message);

        sb.Append(",\"essences\":[");
        for (int i = 0; i < s.Essences.Count; i++)
        {
            BreedEssence e = s.Essences[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(e.Id);
            Str(sb, "name", e.Name);
            Str(sb, "sex", e.Male ? "M" : "F");
            sb.Append(",\"tier\":").Append(e.Tier).Append(",\"bond\":").Append(e.Bond);
            sb.Append(",\"ready\":").Append(e.Ready ? "true" : "false");
            Str(sb, "status", e.Status);
            double readyIn = e.ReadyAtUnix > s.NowUnix ? e.ReadyAtUnix - s.NowUnix : 0;
            sb.Append(",\"readyIn\":").Append((long)Math.Ceiling(readyIn));
            sb.Append(",\"charges\":").Append(e.Charges).Append(",\"chargesMax\":").Append(e.ChargesMax);
            sb.Append(",\"juvenile\":").Append(e.Juvenile ? "true" : "false");
            Str(sb, "stage", e.StageName);
            sb.Append(",\"kills\":").Append(e.Kills).Append(",\"killsRequired\":").Append(e.KillsRequired);
            sb.Append(",\"mutations\":").Append(e.StatMutations).Append(",\"potencyMutations\":").Append(e.MutP);
            Arr(sb, "problems", e.Problems);
            Arr(sb, "boosters", e.Boosters);
            sb.Append('}');
        }
        sb.Append(']');

        // Ready pairings, best odds first (the fewest inherited mutations keeps the decay low).
        var studs = s.Essences.Where(e => e.Male && e.Ready).ToList();
        var dams = s.Essences.Where(e => !e.Male && e.Ready).ToList();
        var pairs = new List<(BreedEssence Stud, BreedEssence Dam, BreedPairPlan Plan)>();
        foreach (BreedEssence st in studs)
            foreach (BreedEssence d in dams)
                pairs.Add((st, d, IltBreedingPlanner.Plan(st, d, r)));
        pairs.Sort((a, b) => b.Plan.ChanceBest.CompareTo(a.Plan.ChanceBest));
        sb.Append(",\"pairs\":[");
        for (int i = 0; i < pairs.Count && i < 6; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('{');
            Str(sb, "stud", pairs[i].Stud.Name, first: true);
            Str(sb, "dam", pairs[i].Dam.Name);
            sb.Append(",\"chanceBest\":").Append(pairs[i].Plan.ChanceBest.ToString("0.####", CultureInfo.InvariantCulture));
            sb.Append(",\"chanceWorst\":").Append(pairs[i].Plan.ChanceWorst.ToString("0.####", CultureInfo.InvariantCulture));
            sb.Append(",\"inheritMin\":").Append(pairs[i].Plan.InheritMin).Append(",\"inheritMax\":").Append(pairs[i].Plan.InheritMax);
            sb.Append('}');
        }
        sb.Append(']');

        sb.Append(",\"supplies\":[");
        for (int i = 0; i < s.Supplies.Count; i++)
        {
            (BreedSupplyDef def, long count) = s.Supplies[i];
            if (i > 0) sb.Append(',');
            sb.Append('{');
            Str(sb, "key", def.Key, first: true);
            Str(sb, "name", def.Name);
            Str(sb, "effect", def.Effect);
            sb.Append(",\"count\":").Append(count).Append(",\"price\":").Append(def.Price);
            sb.Append(",\"apply\":").Append(def.Apply ? "true" : "false");
            // Essences the server would take this on now (the reader's Use buttons).
            var targets = def.Apply && count > 0
                ? s.Essences.Where(e => IltBreedingSupplies.RefuseReason(def, e, r) == null).Select(e => e.Id).ToList()
                : new List<int>();
            sb.Append(",\"targets\":[").Append(string.Join(',', targets)).Append(']');
            sb.Append('}');
        }
        sb.Append(']');

        sb.Append(",\"rules\":[");
        for (int i = 0; i < s.Rules.Count; i++)
        {
            BreedRuleValue v = s.Rules[i];
            if (i > 0) sb.Append(',');
            sb.Append('{');
            Str(sb, "label", v.Def.Label, first: true);
            Str(sb, "value", IltBreedingRules.Format(v));
            Str(sb, "source", IltBreedingRules.SourceText(v.Source));
            Str(sb, "help", v.Def.Help);
            sb.Append('}');
        }
        sb.Append(']');

        sb.Append(",\"events\":[");
        int start = Math.Max(0, s.Events.Count - 20);
        for (int i = s.Events.Count - 1; i >= start; i--)
        {
            IltBreedingEvent e = s.Events[i];
            if (i != s.Events.Count - 1) sb.Append(',');
            sb.Append('{');
            Str(sb, "when", e.Utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), first: true);
            Str(sb, "kind", e.Kind);
            Str(sb, "text", e.Text);
            sb.Append('}');
        }
        sb.Append(']');

        if (s.Debug != null)
        {
            BreedDebugInfo d = s.Debug;
            sb.Append(",\"debug\":{");
            Str(sb, "when", s.DebugUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), first: true);
            Str(sb, "enabled", d.Enabled == null ? "unknown" : d.Enabled.Value ? "on" : "off");
            Str(sb, "location", d.Location);
            Str(sb, "match", d.LocationOk == null ? "" : d.LocationOk.Value ? "valid" : "invalid");
            Str(sb, "pet", d.ActivePet);
            Str(sb, "sex", d.PetSex);
            sb.Append(",\"partners\":").Append(d.Partners);
            sb.Append('}');
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static string TriText(IltTri t) => t switch { IltTri.On => "on", IltTri.Off => "off", _ => "unknown" };

    private static void Str(StringBuilder sb, string name, string? value, bool first = false)
    {
        if (!first) sb.Append(',');
        sb.Append('"').Append(name).Append("\":\"").Append(IltStyle.JsonEscape(value ?? string.Empty)).Append('"');
    }

    private static void Arr(StringBuilder sb, string name, IReadOnlyList<string> values)
    {
        sb.Append(",\"").Append(name).Append("\":[");
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"').Append(IltStyle.JsonEscape(values[i])).Append('"');
        }
        sb.Append(']');
    }

    // ── UI (render thread): Mini Remote > Pets > Breeding ───────────────────

    /// <summary>The Breeding tab's body (RynthIltHub.SectionPetBreeding).</summary>
    public void RenderSection()
    {
        MarkWatched();
        BreedingSnapshot s = _snap;

        RenderHeader(s);
        if (!s.Built)
        {
            ImGui.TextDisabled("Reading your essences...");
            return;
        }
        ImGui.Separator();
        RenderEssences(s);
        RenderPlanner(s);
        RenderSupplies(s);
        RenderRules(s);
        RenderEvents(s);
        RenderGuide(s);
    }

    private void RenderHeader(BreedingSnapshot s)
    {
        Vector4 col = s.Enabled == IltTri.Off ? IltStyle.ColHp : IltStyle.ColTeal;
        SafeImGui.TextColored(col, s.Enabled switch
        {
            IltTri.Off => "Pet breeding is off on this server.",
            IltTri.On => $"Pet breeding: on. Breeding room: {s.Room}.",
            _ => "Pet breeding: not confirmed yet (run Diagnostics in the breeding room).",
        });
        if (s.Bond == IltTri.Off) SafeImGui.TextColored(IltStyle.ColAmber, "Pet bond is off here, so bond can't reach the breeding minimum.");

        if (ImGui.SmallButton("Refresh##brrefresh")) _ctx.Post(RefreshAll);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Re-appraise every essence (charges, rest timers, bond, growth).");
        ImGui.SameLine();
        if (ImGui.SmallButton("Diagnostics##brdebug")) _ctx.Post(RequestDebug);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("@breed-debug: are you in the breeding room, your summoned pet, partners nearby (reply hidden, shown here).");
        ImGui.SameLine();
        if (ImGui.SmallButton("Dance##brdance"))
            _ctx.Confirm("Courtship dance",
                "Dance now? If your partner dances within the window with both pets summoned in the same room, the breed happens:" +
                " the stud spends a charge, the dam rests, incense is used up and the baby goes to the dam's owner.",
                Dance, "Dance");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("@dance (the same as typing *dance*). Both owners must dance within the window.");

        if (s.Message.Length > 0) SafeImGui.TextDisabled(s.Message);
        if (s.FreeSlots == 0) SafeImGui.TextColored(IltStyle.ColAmber, "Your main pack is full: as the dam's owner you couldn't receive the baby.");
    }

    private void RenderEssences(BreedingSnapshot s)
    {
        int ready = s.Essences.Count(e => e.Ready);
        if (!ImGui.CollapsingHeader($"Essences ({ready} ready of {s.Essences.Count})###bress", ImGuiTreeNodeFlags.DefaultOpen)) return;
        if (s.Essences.Count == 0)
        {
            ImGui.TextDisabled("No combat pet essences in your pack.");
            return;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY;
        float h = Math.Min(260f, 26f + s.Essences.Count * ImGui.GetTextLineHeightWithSpacing());
        if (!ImGui.BeginTable("##brtable", 6, flags, new Vector2(0, h))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Essence", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Sex", ImGuiTableColumnFlags.WidthFixed, 34);
        ImGui.TableSetupColumn("Tier", ImGuiTableColumnFlags.WidthFixed, 34);
        ImGui.TableSetupColumn("Bond", ImGuiTableColumnFlags.WidthFixed, 38);
        ImGui.TableSetupColumn("Breeding", ImGuiTableColumnFlags.WidthFixed, 130);
        ImGui.TableSetupColumn("Muts", ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableHeadersRow();
        for (int i = 0; i < s.Essences.Count; i++)
        {
            BreedEssence e = s.Essences[i];
            ImGui.PushID(i);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            bool picked = e.Id == _studId || e.Id == _damId;
            if (ImGui.Selectable(SafeText(e.Name) + "##sel", picked, ImGuiSelectableFlags.SpanAllColumns))
            {
                if (e.Male) _studId = e.Id; else _damId = e.Id;
                _targetId = e.Id;
            }
            if (ImGui.IsItemHovered()) SafeImGui.SetTooltip(Tooltip(e));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(e.Male ? "M" : "F");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(e.Tier > 0 ? e.Tier.ToString(CultureInfo.InvariantCulture) : "?");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(e.HasId ? e.Bond.ToString(CultureInfo.InvariantCulture) : "?");
            ImGui.TableNextColumn();
            Vector4 col = e.Ready ? IltStyle.ColGreen
                : e.Gate is BreedGate.Resting or BreedGate.NoCharges or BreedGate.Growing or BreedGate.NeedsId ? IltStyle.ColAmber
                : IltStyle.ColTextMute;
            SafeImGui.TextColored(col, e.Status);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(e.MutP > 0 ? $"{e.StatMutations}+{e.MutP}p" : e.StatMutations.ToString(CultureInfo.InvariantCulture));
            ImGui.PopID();
        }
        ImGui.EndTable();
        ImGui.TextDisabled("Click a male to pick the stud, a female for the dam (and the supply target). Hover for details.");
    }

    private static string Tooltip(BreedEssence e)
    {
        var sb = new StringBuilder();
        sb.Append(e.Name).Append('\n').Append(e.Role).Append(", tier ").Append(e.Tier > 0 ? e.Tier.ToString(CultureInfo.InvariantCulture) : "?")
          .Append(", bond ").Append(e.Bond);
        if (e.Male) sb.Append("\nCharges ").Append(e.Charges).Append('/').Append(e.ChargesMax);
        if (e.Bred) sb.Append(e.Juvenile ? $"\nGrowing: {e.StageName}, {e.KillsToNext} kills to the next stage, {Math.Max(0, e.KillsRequired - e.Kills)} to Adult" : "\nBred: Adult");
        if (e.Unbound) sb.Append("\nUnbound: imprints on the first character to summon it.");
        sb.Append($"\nMutations: damage {e.MutD}, damage resist {e.MutDR}, crit {e.MutC}, vitality {e.MutV}, potency {e.MutP}");
        if (e.Boosters.Count > 0) sb.Append("\nBoosters: ").Append(string.Join(", ", e.Boosters));
        foreach (string p in e.Problems) sb.Append("\n- ").Append(p);
        return sb.ToString();
    }

    private void RenderPlanner(BreedingSnapshot s)
    {
        if (!ImGui.CollapsingHeader("Pair planner###brplan")) return;
        BreedEssence? stud = s.Essences.FirstOrDefault(e => e.Id == _studId && e.Male);
        BreedEssence? dam = s.Essences.FirstOrDefault(e => e.Id == _damId && !e.Male);
        PickCombo("Stud##brstud", s.Essences.Where(e => e.Male).ToList(), stud, id => _studId = id);
        PickCombo("Dam##brdam", s.Essences.Where(e => !e.Male).ToList(), dam, id => _damId = id);
        if (stud == null || dam == null)
        {
            ImGui.TextDisabled("Pick a stud and a dam (yours, or an alt's carried here) to see the odds.");
            ImGui.TextDisabled("Breeding needs two owners: each dances with their own summoned pet.");
            return;
        }
        BreedPairPlan p = IltBreedingPlanner.Plan(stud, dam, s.RulesView);
        if (p.CanBreed) SafeImGui.TextColored(IltStyle.ColGreen, "Both are ready to breed.");
        foreach (string b in p.Blockers) SafeImGui.TextColored(IltStyle.ColAmber, b);
        SafeImGui.Text($"Stat mutation chance: {Pct(p.ChanceWorst)} to {Pct(p.ChanceBest)} (baby inherits {p.InheritMin}-{p.InheritMax} stat mutations)");
        SafeImGui.Text($"Potency mutation chance: {Pct(p.PotencyChance)}" + (p.Incense > 0 ? $"; incense +{p.Incense * 100:0.#}%" : ""));
        foreach (string n in p.Notes) SafeImGui.BulletText(n);
    }

    private static void PickCombo(string label, List<BreedEssence> list, BreedEssence? current, Action<int> set)
    {
        ImGui.SetNextItemWidth(260);
        if (!ImGui.BeginCombo(label, current != null ? SafeText(current.Name) : "(none)")) return;
        foreach (BreedEssence e in list)
        {
            if (ImGui.Selectable($"{SafeText(e.Name)} - {SafeText(e.Status)}##{e.Id}", current != null && current.Id == e.Id)) set(e.Id);
        }
        ImGui.EndCombo();
    }

    private void RenderSupplies(BreedingSnapshot s)
    {
        long carried = s.Supplies.Sum(x => x.Count);
        if (!ImGui.CollapsingHeader($"Supplies ({carried} carried)###brsup")) return;
        BreedEssence? target = s.Essences.FirstOrDefault(e => e.Id == _targetId);
        PickCombo("Use on##brtarget", s.Essences.ToList(), target, id => _targetId = id);
        ImGui.TextDisabled("Ivo sells these at the Seedy Motel. Boosters are spent by the essence's next breed.");
        for (int i = 0; i < s.Supplies.Count; i++)
        {
            (BreedSupplyDef def, long count) = s.Supplies[i];
            ImGui.PushID("sup" + i);
            Vector4 col = count > 0 ? IltStyle.ColGreen : IltStyle.ColTextMute;
            SafeImGui.TextColored(col, $"{count,3}  {def.Name}");
            if (ImGui.IsItemHovered()) SafeImGui.SetTooltip($"{def.Effect}\nIvo's price: {IltBreedingSupplies.PriceText(def.Price)} pyreals");
            if (def.Apply && count > 0 && target != null)
            {
                ImGui.SameLine(300);
                string? refuse = IltBreedingSupplies.RefuseReason(def, target, s.RulesView);
                if (refuse != null)
                {
                    ImGui.TextDisabled("can't");
                    if (ImGui.IsItemHovered()) SafeImGui.SetTooltip(refuse);
                }
                else if (ImGui.SmallButton("Use"))
                {
                    string key = def.Key;
                    int id = target.Id;
                    _ctx.Confirm("Use " + def.Name,
                        $"Use {def.Name} on {target.Name}? {def.Effect}{IltBreedingSupplies.Warning(def, target)}",
                        () => ApplySupply(key, id), "Use");
                }
            }
            ImGui.PopID();
        }
    }

    private void RenderRules(BreedingSnapshot s)
    {
        if (!ImGui.CollapsingHeader("Server rules###brrules")) return;
        ImGui.TextDisabled("Values this server showed win over the ILT profile, which wins over ACE's defaults.");
        foreach (BreedRuleValue v in s.Rules)
        {
            SafeImGui.Text(v.Def.Label);
            if (ImGui.IsItemHovered()) SafeImGui.SetTooltip(v.Def.Help);
            ImGui.SameLine(200);
            SafeImGui.Text(IltBreedingRules.Format(v));
            ImGui.SameLine(280);
            SafeImGui.TextColored(v.Source == BreedRuleSource.Server ? IltStyle.ColGreen : IltStyle.ColTextMute,
                IltBreedingRules.SourceText(v.Source));
        }
        if (s.Debug != null)
        {
            ImGui.Separator();
            SafeImGui.TextDisabled($"Last diagnostics ({s.DebugUtc.ToLocalTime():g}):");
            BreedDebugInfo d = s.Debug;
            if (d.Location.Length > 0) SafeImGui.BulletText("You: " + d.Location);
            if (d.LocationOk.HasValue) SafeImGui.BulletText(d.LocationOk.Value ? "In the breeding room" : "Not in the breeding room");
            if (d.ActivePet.Length > 0) SafeImGui.BulletText("Pet: " + d.ActivePet + (d.PetSex.Length > 0 ? " (" + d.PetSex + ")" : ""));
            if (d.Partners >= 0) SafeImGui.BulletText($"Partners nearby: {d.Partners}");
        }
    }

    private static void RenderEvents(BreedingSnapshot s)
    {
        if (!ImGui.CollapsingHeader($"Recent events ({s.Events.Count})###brevents")) return;
        if (s.Events.Count == 0) ImGui.TextDisabled("Nothing yet: rituals, births, growth and charges land here.");
        for (int i = s.Events.Count - 1, shown = 0; i >= 0 && shown < 20; i--, shown++)
        {
            IltBreedingEvent e = s.Events[i];
            SafeImGui.TextDisabled(e.Utc.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture));
            ImGui.SameLine(90);
            SafeImGui.TextWrapped(e.Text);
        }
    }

    private static void RenderGuide(BreedingSnapshot s)
    {
        if (!ImGui.CollapsingHeader("How to breed###brguide")) return;
        SafeImGui.BulletText($"Go to the Seedy Motel (portal by Prof. Ruggan in Lin, 54.7S 73.5E); breeding room {s.Room}.");
        SafeImGui.BulletText("Two owners: one summons a male (stud), the other a female (dam). Both pets in the same room.");
        SafeImGui.BulletText("Both parents: tier 100+, bond 100+, adult, not shiny, not neutered. The stud needs a charge, the dam must have rested.");
        SafeImGui.BulletText("Both owners *dance* (or @dance) within a few seconds. The baby goes to the dam's owner (free pack slot needed).");
        SafeImGui.BulletText("A mating guardian may rise: both parents kill it in time for an Awakened Blessing mutation.");
        SafeImGui.BulletText("The baby imprints on whoever summons it first, and grows up with kills (Nurturing Draught doubles the credit).");
    }

    private static string Pct(double v) => (v * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    /// <summary>Item names go into ImGui labels, where "##" would start the hidden id.</summary>
    private static string SafeText(string s) => (s ?? string.Empty).Replace("#", "");
}
