// IltServerOptions.cs — Per-feature enable bits for the ILT Hub, read from the server.
//
// UB gated every Leaftide tool on a single test (WorldName == "InfiniteLeaftide" or a
// Force checkbox). That lies on shards where the operator turned Powerball, /myquests or
// a charm off. This module instead keeps one tri-state bit per feature:
//   * World identity: ServerFeatureGate (Settings > Misc server list, or its manual
//     override / the Hub's ForceLeaftideFeatures) decides whether we even ASK — it never
//     switches a feature on by itself.
//   * Preferred source: a structured "/ilt features" dump (ILTFEATURE key=0|1 lines) once
//     ACECustom ships it. Today the server prints "Coming Soon", so:
//   * Fallback: a one-shot login probe of read-only commands (/bank, /aug, /qb, /pets,
//     /shinies, /xp, /pb, /blackjack, /fblackjack, /clap with no args). Replies are eaten.
//     /enl is NEVER probed — it opens the enlightenment confirmation dialog.
//   * Passive: explicit server refusals seen in chat at any time flip a bit to Off
//     ("Powerball lottery is not currently active", "myquests is not currently enabled", ...).
// Unknown bits are treated as OFF by anything that spends currency or runs automation.
using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.IltHub;

/// <summary>Feature keys used by the gate table.</summary>
internal static class IltFeature
{
    public const string Bank = "bank";
    public const string Aug = "aug";
    public const string Enl = "enl";
    public const string Qb = "qb";
    public const string Pets = "pets";
    public const string Shinies = "shinies";
    public const string Xp = "xp";
    public const string Blackjack = "blackjack";
    public const string FBlackjack = "fblackjack";
    public const string Powerball = "powerball";
    public const string Quests = "quests";
    public const string Clap = "clap";
    public const string PetRefill = "pet_refill";
    public const string UniversalMastery = "universal_mastery";
    public const string RequireComps = "require_spell_comps";
    public const string PetBond = "pet_bond";
    public const string PetPotency = "pet_potency";

    /// <summary>Key for a per-charm "enabled" bit, e.g. Charm("Guardian Hand") → "charm:guardianhand".</summary>
    public static string Charm(string charmName)
    {
        var chars = (charmName ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
        return "charm:" + new string(chars);
    }
}

/// <summary>Tri-state feature value.</summary>
internal enum IltTri { Unknown = -1, Off = 0, On = 1 }

internal sealed class IltServerOptions
{
    private readonly RynthCoreHost _host;
    private readonly IltChatCapture _capture;
    private readonly IltHubState _state;
    private readonly Action<string> _chat;
    private readonly Func<LegacyUiSettings?> _settings;

    /// <summary>Feeds probe replies to features that can reuse them (e.g. /bank → Banking balances).</summary>
    public Action<string, IltChatResult>? ProbeReplyTap;

    private bool _refreshInFlight;
    private int _probesOutstanding;
    private long _loginAt;
    private bool _loginRefreshDone;

    /// <summary>Wait this long after login before the one-shot refresh so chat/world are settled.</summary>
    private const long LoginRefreshDelayMs = 6000;

    /// <summary>
    /// Upper bound on waiting for the world name. The engine hook / launch-context file can report
    /// it well after the 6 s delay (late inject, Decal coexistence); deciding "not ILT" on an empty
    /// name would leave every feature bit Unknown and the Hub hidden for the whole session.
    /// </summary>
    private const long WorldNameWaitMs = 60000;

    public IltServerOptions(RynthCoreHost host, IltChatCapture capture, IltHubState state, Action<string> chat,
                            Func<LegacyUiSettings?>? settings = null)
    {
        _host = host;
        _capture = capture;
        _state = state;
        _chat = chat;
        _settings = settings ?? (() => null);
    }

    // ── Identity ────────────────────────────────────────────────────────────

    /// <summary>World name as reported by the engine login hook (empty if unknown).</summary>
    public string WorldName
    {
        get
        {
            if (_host.HasGetWorldName && _host.TryGetWorldName(out string n) && !string.IsNullOrWhiteSpace(n))
                return n.Trim();
            return _state.ServerOptions.WorldName ?? string.Empty;
        }
    }

    /// <summary>
    /// True when the world is (or is declared to be) an ACECustom/ILT shard: its name is in the
    /// Settings > Misc server list, or an override is on (ServerFeatureGate).
    /// </summary>
    public bool IsIltLikeWorld
    {
        get
        {
            if (_state.ForceLeaftideFeatures) return true;
            LegacyUiSettings? s;
            try { s = _settings(); } catch { s = null; }
            return ServerFeatureGate.IsEnabled(WorldName, s);
        }
    }

    /// <summary>True while a refresh (dump or probe) is running.</summary>
    public bool IsRefreshing => _refreshInFlight;

    public string Source => _state.ServerOptions.Source;
    public DateTime LastRefreshUtc => _state.ServerOptions.LastRefreshUtc;

    // ── Bit access ──────────────────────────────────────────────────────────

    public IltTri Get(string key)
        => _state.ServerOptions.Bits.TryGetValue(key, out int v) ? (IltTri)Math.Clamp(v, -1, 1) : IltTri.Unknown;

    /// <summary>True only when the server positively reported the feature as available.</summary>
    public bool IsOn(string key) => Get(key) == IltTri.On;

    /// <summary>True when the server positively reported the feature as unavailable.</summary>
    public bool IsOff(string key) => Get(key) == IltTri.Off;

    public void Set(string key, IltTri value)
    {
        // Every feature gate reads these bits, so each transition is traced (IltOptions).
        IltTri old = Get(key);
        _state.ServerOptions.Bits[key] = (int)value;
        if (old != value) RynthLog.Trace(LogCat.IltOptions, $"feature '{key}': {old} -> {value}");
    }

    /// <summary>At least one feature bit is On (Hub window shows only then, unless Force is on).</summary>
    public bool AnyFeatureOn => _state.ServerOptions.Bits.Values.Any(v => v == 1);

    /// <summary>All known bits for the status display.</summary>
    public IReadOnlyList<KeyValuePair<string, int>> SnapshotBits()
        => _state.ServerOptions.Bits.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).ToList();

    // ── Lifecycle ───────────────────────────────────────────────────────────

    /// <summary>Called at login: schedules the one-shot refresh (never a recurring timer).</summary>
    public void OnLoginComplete()
    {
        _loginAt = Environment.TickCount64;
        _loginRefreshDone = false;
    }

    /// <summary>
    /// Pump-thread tick: fires the delayed login refresh once. While the world name is still
    /// unknown it keeps checking (a cheap string compare per tick) until the name arrives or
    /// WorldNameWaitMs expires; only a known non-ILT world, or the timeout, ends the wait.
    /// </summary>
    public void Tick()
    {
        if (_loginRefreshDone || _loginAt == 0) return;
        long sinceLogin = Environment.TickCount64 - _loginAt;
        if (sinceLogin < LoginRefreshDelayMs) return;

        if (IsIltLikeWorld)
        {
            _loginRefreshDone = true;
            RynthLog.Event(LogEvents.IltWorldCheck,
                $"world check: '{WorldName}' is ILT-like (force={_state.ForceLeaftideFeatures}) - refreshing server options");
            Refresh(manual: false);
            return;
        }

        // World not reported yet: try again next tick rather than concluding "not ILT".
        if (string.IsNullOrEmpty(WorldName) && sinceLogin < WorldNameWaitMs)
            return;

        _loginRefreshDone = true;
        RynthLog.Event(LogEvents.IltWorldCheck,
            $"world check: '{WorldName}' is not ILT-like - login refresh skipped (waited {sinceLogin / 1000}s)");
    }

    /// <summary>
    /// Runs the dump-then-probe refresh. Manual refreshes report progress in chat; the
    /// login refresh is silent.
    /// </summary>
    public void Refresh(bool manual)
    {
        if (_refreshInFlight)
        {
            if (manual) _chat("[ILT Hub] Server options refresh already running.");
            return;
        }
        if (!IsIltLikeWorld)
        {
            if (manual) _chat("[ILT Hub] This world is not in the Server Features list (Settings > Misc: add it, or tick the override).");
            return;
        }

        _refreshInFlight = true;
        _state.ServerOptions.WorldName = WorldName;
        RynthLog.Event(LogEvents.IltOptionsRefreshStart, $"refresh start (manual={manual}, world='{WorldName}')");
        if (manual) _chat("[ILT Hub] Refreshing server options...");

        // Step 1: structured dump. Lines start with "=== ILT Custom Features ===".
        _capture.Enqueue(new IltChatRequest
        {
            Command = "/ilt features",
            IsResponseLine = t => t.StartsWith("ILTFEATURE", StringComparison.OrdinalIgnoreCase)
                               || t.Contains("ILT Custom Features", StringComparison.OrdinalIgnoreCase)
                               || t.Equals("Coming Soon", StringComparison.OrdinalIgnoreCase),
            IdleEndMs = 1200,
            FirstLineTimeoutMs = 8000,
            Eat = true,
            OnComplete = r => OnFeatureDump(r, manual),
        });
    }

    private void OnFeatureDump(IltChatResult r, bool manual)
    {
        var fromDump = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in r.Lines)
        {
            if (!line.StartsWith("ILTFEATURE", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var kv in ParseFeatureLine(line))
            {
                Set(kv.Key, kv.Value);
                fromDump.Add(kv.Key);
            }
        }

        _state.ServerOptions.Source = fromDump.Count > 0 ? "ilt-features" : "probe";
        RynthLog.Trace(LogCat.IltOptions, $"feature dump: {fromDump.Count} key(s) (timedOut={r.TimedOut}, unknown={r.UnknownCommand}) → source={_state.ServerOptions.Source}");
        RunProbes(fromDump, manual);
    }

    /// <summary>
    /// Parses "ILTFEATURE bank=1 powerball=0 charms=infinitecasting:1,guardianhand:0".
    /// ServerConfig-style names ("powerball_enabled") are mapped onto Hub keys.
    /// </summary>
    internal static IEnumerable<KeyValuePair<string, IltTri>> ParseFeatureLine(string line)
    {
        string body = line.Substring("ILTFEATURE".Length).Trim();
        foreach (string token in body.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = token.IndexOf('=');
            if (eq <= 0) continue;
            string key = token.Substring(0, eq).Trim();
            string val = token.Substring(eq + 1).Trim();

            if (key.Equals("charms", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string pair in val.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    int c = pair.IndexOf(':');
                    if (c <= 0) continue;
                    yield return new(IltFeature.Charm(pair.Substring(0, c)), ParseTri(pair.Substring(c + 1)));
                }
                continue;
            }
            yield return new(MapServerKey(key), ParseTri(val));
        }
    }

    private static IltTri ParseTri(string v)
    {
        v = v.Trim().ToLowerInvariant();
        return v is "1" or "on" or "true" or "yes" ? IltTri.On
             : v is "0" or "off" or "false" or "no" ? IltTri.Off
             : IltTri.Unknown;
    }

    private static string MapServerKey(string key)
    {
        string k = key.Trim().ToLowerInvariant();
        return k switch
        {
            "powerball_enabled" => IltFeature.Powerball,
            "quest_info_enabled" => IltFeature.Quests,
            "pet_device_pyreal_auto_refill_enabled" => IltFeature.PetRefill,
            "pet_charm_universal_summoning_mastery_enabled" => IltFeature.UniversalMastery,
            "pet_bond_enabled" => IltFeature.PetBond,
            "pet_potency_enabled" => IltFeature.PetPotency,
            _ => k.EndsWith("_enabled", StringComparison.Ordinal) ? k.Substring(0, k.Length - "_enabled".Length) : k,
        };
    }

    // ── Probe ───────────────────────────────────────────────────────────────

    /// <summary>One read-only probe: command, reply recogniser, feature key.</summary>
    private readonly record struct Probe(string Key, string Command, Func<string, bool> IsReply);

    private static readonly Probe[] Probes =
    {
        new(IltFeature.Bank,      "/bank",       t => t.StartsWith("[BANK]", StringComparison.OrdinalIgnoreCase)
                                                    || t.Contains("not permitted to use bank", StringComparison.OrdinalIgnoreCase)),
        new(IltFeature.Aug,       "/aug",        t => t.StartsWith("---") || t.Contains("Augmentation Levels", StringComparison.OrdinalIgnoreCase)
                                                    || IltParse.AugLine.IsMatch(t)),
        new(IltFeature.Qb,        "/qb",         t => t.StartsWith("Your current quest bonus count", StringComparison.OrdinalIgnoreCase)
                                                    || t.StartsWith("[QB]", StringComparison.OrdinalIgnoreCase)),
        new(IltFeature.Pets,      "/pets",       t => t.Contains("Your Pet Log", StringComparison.OrdinalIgnoreCase)
                                                    || IltParse.RosterLine.IsMatch(t) || t.Contains("Professor Ruggan", StringComparison.OrdinalIgnoreCase)),
        new(IltFeature.Shinies,   "/shinies",    t => t.Contains("Your Shiny Log", StringComparison.OrdinalIgnoreCase)
                                                    || IltParse.RosterLine.IsMatch(t) || t.Contains("Shiny creatures", StringComparison.OrdinalIgnoreCase)),
        new(IltFeature.Xp,        "/xp",         t => t.StartsWith("[XP]", StringComparison.OrdinalIgnoreCase)),
        new(IltFeature.Powerball, "/pb",         t => t.StartsWith("[Powerball]", StringComparison.OrdinalIgnoreCase)),
        new(IltFeature.Blackjack, "/blackjack",  IsParamErrorOr("blackjack", "bet amount")),
        new(IltFeature.FBlackjack,"/fblackjack", IsParamErrorOr("blackjack", "bet amount")),
        new(IltFeature.Clap,      "/clap",       IsParamErrorOr("clap", "AutoCraftingEnabled")),
    };

    /// <summary>Recognises ACE's wrong-parameter-count reply (command exists) or a keyword reply.</summary>
    private static Func<string, bool> IsParamErrorOr(string keyword, string keyword2) => t =>
        t.StartsWith("Invalid parameter count", StringComparison.OrdinalIgnoreCase)
        || t.StartsWith("Usage:", StringComparison.OrdinalIgnoreCase)
        || t.StartsWith("@" + keyword, StringComparison.OrdinalIgnoreCase)
        || t.Contains(keyword2, StringComparison.OrdinalIgnoreCase)
        || (t.Contains(keyword, StringComparison.OrdinalIgnoreCase) && !t.StartsWith("<Tell", StringComparison.OrdinalIgnoreCase));

    private void RunProbes(HashSet<string> alreadyKnown, bool manual)
    {
        var todo = Probes.Where(p => !alreadyKnown.Contains(p.Key)).ToList();

        // /enl is never probed (it pops the enlightenment confirmation). On an ILT-like
        // world the command is part of ACECustom, so it is assumed present unless the
        // dump said otherwise.
        if (!alreadyKnown.Contains(IltFeature.Enl))
            Set(IltFeature.Enl, IltTri.On);

        _probesOutstanding = todo.Count;
        if (_probesOutstanding == 0) { CompleteRefresh(manual); return; }

        foreach (var p in todo)
        {
            var probe = p;
            _capture.Enqueue(new IltChatRequest
            {
                Command = probe.Command,
                IsResponseLine = probe.IsReply,
                IdleEndMs = 1500,
                FirstLineTimeoutMs = 8000,
                Eat = true,
                OnComplete = r =>
                {
                    // Unknown command → Off. Any recognised reply → On. Silence → leave Unknown.
                    if (r.UnknownCommand) Set(probe.Key, IltTri.Off);
                    else if (r.Lines.Count > 0) Set(probe.Key, IltTri.On);
                    RynthLog.Trace(LogCat.IltOptions, $"probe {probe.Command}: lines={r.Lines.Count} unknown={r.UnknownCommand} timedOut={r.TimedOut}");
                    try { ProbeReplyTap?.Invoke(probe.Key, r); }
                    catch (Exception ex) { RynthLog.Exception(LogCat.IltOptions, ex, $"probe tap {probe.Key}"); }
                    if (--_probesOutstanding <= 0) CompleteRefresh(manual);
                },
            });
        }
    }

    private void CompleteRefresh(bool manual)
    {
        _refreshInFlight = false;
        _state.ServerOptions.LastRefreshUtc = DateTime.UtcNow;
        int on = _state.ServerOptions.Bits.Values.Count(v => v == 1);
        int off = _state.ServerOptions.Bits.Values.Count(v => v == 0);
        // Building the per-bit list is skipped unless the event is actually going to be recorded.
        if (RynthLog.IsEventEnabled(LogEvents.IltOptionsRefreshed))
            RynthLog.Event(LogEvents.IltOptionsRefreshed, $"options refreshed via {Source} (manual={manual}): {on} on, {off} off - " +
                string.Join(" ", _state.ServerOptions.Bits.Select(b => $"{b.Key}={b.Value}")));
        if (manual)
            _chat($"[ILT Hub] Server options refreshed via {Source}: {on} on, {off} off.");
    }

    // ── Passive refusals ────────────────────────────────────────────────────

    /// <summary>
    /// Watches every chat line for explicit server refusals and flips the matching bit Off.
    /// Cheap substring checks; runs on the pump thread.
    /// </summary>
    public void OnChatLine(string text)
    {
        if (text.Contains("Powerball lottery is not currently active", StringComparison.OrdinalIgnoreCase))
            Set(IltFeature.Powerball, IltTri.Off);
        else if (text.Contains("\"myquests\" is not currently enabled", StringComparison.OrdinalIgnoreCase))
            Set(IltFeature.Quests, IltTri.Off);
        else if (text.Contains("\"qb\" is not currently enabled", StringComparison.OrdinalIgnoreCase))
            Set(IltFeature.Qb, IltTri.Off);
        else if (text.Contains("must have received the AutoCraftingEnabled quest stamp", StringComparison.OrdinalIgnoreCase))
            Set(IltFeature.Clap, IltTri.Off);
        else if (text.Contains("Universal Summoning Mastery is not enabled on this server", StringComparison.OrdinalIgnoreCase))
            Set(IltFeature.UniversalMastery, IltTri.Off);
        else if (text.Contains("Auto-Rebuff Charm is currently disabled globally", StringComparison.OrdinalIgnoreCase))
            Set(IltFeature.Charm("Auto-Rebuff"), IltTri.Off);
        else if (text.Contains("not permitted to use bank commands", StringComparison.OrdinalIgnoreCase))
            Set(IltFeature.Bank, IltTri.Off);
    }

    /// <summary>Records what the login /myquests refresh learned (called by the Hub after the Meta tracker finishes).</summary>
    public void NoteQuestTrackerOutcome(bool disabled, int recordCount)
    {
        if (disabled) Set(IltFeature.Quests, IltTri.Off);
        else if (recordCount > 0) Set(IltFeature.Quests, IltTri.On);
    }
}
