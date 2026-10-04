// IltHubController.cs — Owns the ILT Hub: state, services, feature modules, commands.
//
// RynthAiPlugin creates one controller per login session and forwards:
//   OnTick → Tick()          (pump thread)
//   OnChatWindowText → OnChat() (pump thread; returns true to eat the line)
//   OnKillNotification → RecordKill()
//   OnRender → Render()      (render thread)
//   /ra hub ..., /ra quests ... → HandleCommand()
//   OnLogout / teardown → OnLogout()
// The Hub only appears on ACECustom/ILT worlds where at least one server feature is on;
// on every other world it stays dormant (no probes, no chat, no window).
using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.IltHub;

internal sealed class IltHubController
{
    private const long SaveIntervalMs = 5000;

    private readonly IltHubContext _ctx;
    private readonly IltHubUi _ui;
    private readonly Func<QuestTracker?> _quests;
    private readonly List<IIltFeature> _features = new();
    private long _lastSaveAt;
    private bool _questOutcomeHooked;
    private bool _availabilityAnnounced; // "/ra hub to open" hint printed this session

    public IltBanking Banking { get; }
    public IltPets Pets { get; }
    public IltSessionRates Rates { get; }
    public IltQuests Quests { get; }
    public IltProgression Progression { get; }
    public IltGear Gear { get; }
    public IltGames Games { get; }

    public IltHubController(RynthCoreHost host, string charFolder, Func<WorldObjectCache?> cache,
                            Func<LegacyUiSettings?> settings, Func<QuestTracker?> quests, Action saveCombatSettings)
    {
        var store = new IltHubStore(host, charFolder);
        var state = store.LoadState();
        var capture = new IltChatCapture(host);
        var inventory = new IltInventory(host, cache);
        IltHubContext? ctxRef = null;
        var options = new IltServerOptions(host, capture, state, text => ctxRef?.Chat(text));
        _ctx = new IltHubContext(host, state, store, capture, options, inventory, settings) { SaveCombatSettings = saveCombatSettings };
        ctxRef = _ctx;
        _quests = quests;

        Banking = new IltBanking(_ctx);
        Pets = new IltPets(_ctx);
        Rates = new IltSessionRates(_ctx);
        Quests = new IltQuests(_ctx, quests);
        Progression = new IltProgression(_ctx);
        Gear = new IltGear(_ctx);
        Games = new IltGames(_ctx);
        _features.AddRange(new IIltFeature[] { Banking, Pets, Rates, Quests, Progression, Gear, Games });

        Banking.BalanceChanged += Rates.OnBankBalanceChanged;
        options.ProbeReplyTap = OnProbeReply;
        _ui = new IltHubUi(this, _ctx);
    }

    public IltHubState State => _ctx.State;
    public IltServerOptions Options => _ctx.Options;

    /// <summary>World is ILT-like and the server has at least one Hub feature on.</summary>
    public bool Available => _ctx.Options.IsIltLikeWorld && _ctx.Options.AnyFeatureOn;

    /// <summary>True when the cached options already say /myquests is off (skip the login refresh).</summary>
    public bool SkipLoginQuestRefresh => _ctx.Options.IsIltLikeWorld && _ctx.Options.IsOff(IltFeature.Quests);

    // ── Lifecycle ───────────────────────────────────────────────────────────

    /// <summary>Login: remember the character, hook PetManager, schedule the options check, import UB data once.</summary>
    public void OnLoginComplete(string charName, PetManager? petManager)
    {
        _ctx.CharName = charName ?? string.Empty;
        RynthLog.Trace(LogCat.IltHub, $"login: char='{_ctx.CharName}' world='{_ctx.Options.WorldName}'");
        _ctx.Options.OnLoginComplete();
        _ctx.Inventory.Reset();
        Rates.Reset();
        DashWindows.ShowIltHub = _ctx.State.WindowVisible; // reopen where the player left it

        if (petManager != null)
        {
            petManager.AllowSummonOnEmpty = Pets.AllowSummonOnEmpty;
            petManager.HoldSummons = Pets.HoldCombatSummons;
        }

        HookQuestOutcome();

        if (!_ctx.State.UbSidecarsImported && _ctx.CharName.Length > 0)
        {
            _ctx.State.UbSidecarsImported = true;
            string world = _ctx.Options.WorldName;
            Banking.ImportUb(tx => _ctx.Store.ImportUbSidecars(_ctx.State, tx, world, _ctx.CharName));
        }
    }

    private void HookQuestOutcome()
    {
        if (_questOutcomeHooked) return;
        var t = _quests();
        if (t == null) return;
        t.RefreshCompleted += () => _ctx.Options.NoteQuestTrackerOutcome(t.Disabled, t.Count);
        _questOutcomeHooked = true;
    }

    /// <summary>Pump-thread tick.</summary>
    public void Tick()
    {
        long now = IltHubContext.NowMs;
        _ctx.DrainPosted();
        _ctx.Capture.Tick();
        _ctx.Options.Tick();
        HookQuestOutcome();

        // One hint per session once the server confirms Hub features. The window is separate
        // from the main RynthAi panel, so say how to open it.
        if (!_availabilityAnnounced && Available)
        {
            _availabilityAnnounced = true;
            if (!_ctx.State.WindowVisible)
                _ctx.Chat("[ILT Hub] ILT server features detected. Type /ra hub to open the ILT Hub window.");
        }

        // Features run only on ILT-like worlds; elsewhere the Hub is fully dormant.
        if (_ctx.Options.IsIltLikeWorld)
        {
            foreach (var f in _features)
            {
                try { f.Tick(now); }
                catch (Exception ex) { RynthLog.Exception(CategoryOf(f), ex, $"{f.GetType().Name}.Tick"); }
            }
        }

        // Expire an unanswered chat confirmation so a stale "/ra hub confirm" can't fire it later.
        if (_pendingAction != null && now - _pendingAt > ConfirmWindowMs)
        {
            RynthLog.Trace(LogCat.IltHub, $"confirmation expired: {_pendingLabel}");
            _ctx.Chat($"[ILT Hub] Confirmation for '{_pendingLabel}' expired.");
            ClearPending();
        }

        if (now - _lastSaveAt >= SaveIntervalMs)
        {
            _lastSaveAt = now;
            _ctx.Store.SaveStateIfDirty(_ctx.State);
        }
    }

    /// <summary>Pump-thread chat line. Returns true when the line should be hidden.</summary>
    public bool OnChat(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        string line = text.TrimEnd('\r', '\n');
        _ctx.Options.OnChatLine(line);
        bool eat = _ctx.Capture.OnChatLine(line);
        if (!_ctx.Options.IsIltLikeWorld) return eat;
        foreach (var f in _features)
        {
            try { eat |= f.OnChat(line); }
            catch (Exception ex) { RynthLog.Exception(CategoryOf(f), ex, $"{f.GetType().Name}.OnChat"); }
        }
        return eat;
    }

    /// <summary>Maps a feature module to its diagnostics category so its trace/exception lines land in its own file.</summary>
    private static LogCat CategoryOf(IIltFeature f) => f switch
    {
        IltBanking => LogCat.IltBanking,
        IltPets => LogCat.IltPets,
        IltSessionRates => LogCat.IltRates,
        IltQuests => LogCat.IltQuests,
        IltProgression => LogCat.IltProgression,
        IltGear => LogCat.IltGear,
        IltGames => LogCat.IltGames,
        _ => LogCat.IltHub,
    };

    public void RecordKill() => Rates.RecordKill();

    /// <summary>Logout / teardown: stop automation and flush to disk.</summary>
    public void OnLogout()
    {
        RynthLog.Trace(LogCat.IltHub, $"logout: stopping features for '{_ctx.CharName}'");
        ClearPending();
        foreach (var f in _features)
        {
            try { f.OnLogout(); }
            catch (Exception ex) { RynthLog.Exception(CategoryOf(f), ex, $"{f.GetType().Name}.OnLogout"); }
        }
        _ctx.Capture.Clear();
        _ctx.Store.SaveStateIfDirty(_ctx.State);
    }

    /// <summary>Render thread: Hub window, confirm popups, games HUD.</summary>
    public void Render() => _ui.Render();

    /// <summary>Opens/closes the Hub window (render or pump thread).</summary>
    public void SetVisible(bool visible)
    {
        _ctx.State.WindowVisible = visible;
        DashWindows.ShowIltHub = visible;
    }

    // ── Probe taps: reuse login-probe replies so tabs fill without extra commands ──

    private void OnProbeReply(string key, IltChatResult r)
    {
        if (r.Lines.Count == 0) return;
        switch (key)
        {
            case IltFeature.Aug: Progression.ApplyAugLines(r.Lines); break;
            case IltFeature.Pets: Pets.ApplyRoster(false, r.Lines); break;
            case IltFeature.Shinies: Pets.ApplyRoster(true, r.Lines); break;
            case IltFeature.Qb: Quests.ApplyQbProbe(r.Lines); break;
            // Bank lines are parsed passively by IltBanking.OnChat as they arrive.
        }
    }

    // ── Commands ────────────────────────────────────────────────────────────

    /// <summary>
    /// "/ra hub [show|hide|toggle|refresh|bank|status|force on|off|profile save|load|list ...|suit list|test|load ...]"
    /// and "/ra quests [refresh|check &lt;regex&gt;]". Pump thread. Returns false if not handled.
    /// </summary>
    public bool HandleCommand(string verb, string[] args)
    {
        RynthLog.Trace(LogCat.IltHub, $"command: /ra {verb} {string.Join(" ", args)}".TrimEnd());
        if (verb.Equals("quests", StringComparison.OrdinalIgnoreCase))
        {
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "refresh";
            if (sub == "check" && args.Length > 1) Quests.CheckCommand(string.Join(" ", args.Skip(1)));
            else _ctx.Chat("[ILT Hub] " + Quests.RefreshQuests());
            return true;
        }
        if (!verb.Equals("hub", StringComparison.OrdinalIgnoreCase)) return false;

        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "toggle";
        string rest = args.Length > 1 ? string.Join(" ", args.Skip(1)) : string.Empty;
        switch (cmd)
        {
            case "show": SetVisible(true); break;
            case "hide": SetVisible(false); break;
            case "toggle": SetVisible(!_ctx.State.WindowVisible); break;
            case "refresh": _ctx.Options.Refresh(manual: true); break;
            case "bank": Banking.RequestRefresh(quiet: false); break;
            case "status": PrintStatus(); break;
            case "force":
                _ctx.State.ForceLeaftideFeatures = !rest.Equals("off", StringComparison.OrdinalIgnoreCase);
                _ctx.Chat($"[ILT Hub] Force ILT world: {(_ctx.State.ForceLeaftideFeatures ? "on" : "off")}. Use /ra hub refresh to re-check the server.");
                break;
            case "profile": HandleProfile(args.Skip(1).ToArray()); break;
            case "suit": HandleSuit(args.Skip(1).ToArray()); break;
            case "clap": Gear.ChunkClap.ClapNow(manual: true); break;
            case "confirm": RunPending(); break;
            case "cancel":
                _ctx.Chat(_pendingAction != null ? $"[ILT Hub] Cancelled '{_pendingLabel}'." : "[ILT Hub] Nothing to cancel.");
                ClearPending();
                break;
            default:
                _ctx.Chat("[ILT Hub] /ra hub show|hide|toggle|refresh|bank|status|force on|off|clap|confirm|cancel");
                _ctx.Chat("[ILT Hub] /ra hub profile list|save <name> [shared]|load <name>   /ra hub suit list|test|load [name]");
                _ctx.Chat("[ILT Hub] /ra quests [refresh|check <regex>]");
                break;
        }
        return true;
    }

    private void PrintStatus()
    {
        var o = _ctx.Options;
        _ctx.Chat($"[ILT Hub] World '{o.WorldName}' ILT={o.IsIltLikeWorld} force={_ctx.State.ForceLeaftideFeatures} source={(o.Source.Length > 0 ? o.Source : "none")} "
                  + $"refreshed={(o.LastRefreshUtc == DateTime.MinValue ? "never" : o.LastRefreshUtc.ToLocalTime().ToString("t"))}");
        var bits = o.SnapshotBits();
        string on = string.Join(", ", bits.Where(b => b.Value == 1).Select(b => b.Key));
        string off = string.Join(", ", bits.Where(b => b.Value == 0).Select(b => b.Key));
        _ctx.Chat("[ILT Hub] on: " + (on.Length > 0 ? on : "-"));
        _ctx.Chat("[ILT Hub] off: " + (off.Length > 0 ? off : "-"));
    }

    private void HandleProfile(string[] a)
    {
        string sub = a.Length > 0 ? a[0].ToLowerInvariant() : "list";
        if (sub == "list")
        {
            var names = _ctx.Store.ListProfiles();
            _ctx.Chat("[ILT Hub] Profiles: " + (names.Count > 0 ? string.Join(", ", names) : "(none)"));
            return;
        }
        if (a.Length < 2) { _ctx.Chat("[ILT Hub] Usage: /ra hub profile save <name> [shared] | load <name>"); return; }
        if (sub == "save")
        {
            bool shared = a.Length > 2 && a[2].Equals("shared", StringComparison.OrdinalIgnoreCase);
            _ctx.Chat(_ctx.Store.SaveProfile(_ctx.State, a[1], shared, out string err)
                ? $"[ILT Hub] Saved {(shared ? "shared " : "")}profile '{a[1]}'." : "[ILT Hub] Save failed: " + err);
        }
        else if (sub == "load")
        {
            // Loading replaces the Hub's current settings, so it needs an explicit confirm.
            string profile = a[1];
            RequestConfirm($"load Hub profile '{profile}' (replaces current Hub settings)", () =>
                _ctx.Chat(_ctx.Store.LoadProfile(_ctx.State, profile, out string err)
                    ? $"[ILT Hub] Loaded profile '{profile}'." : "[ILT Hub] Load failed: " + err));
        }
    }

    // ── Chat confirmations for destructive commands ─────────────────────────

    /// <summary>How long a "/ra hub confirm" stays valid after the prompt.</summary>
    private const long ConfirmWindowMs = 20000;

    private string? _pendingLabel;
    private Action? _pendingAction;
    private long _pendingAt;

    /// <summary>Arms a single pending action; a newer request replaces an older one.</summary>
    private void RequestConfirm(string label, Action action)
    {
        _pendingLabel = label;
        _pendingAction = action;
        _pendingAt = IltHubContext.NowMs;
        RynthLog.Trace(LogCat.IltHub, $"confirmation requested: {label}");
        _ctx.Chat($"[ILT Hub] About to {label}. Type /ra hub confirm within {ConfirmWindowMs / 1000} s, or /ra hub cancel.");
    }

    /// <summary>Runs the pending action if one is armed and still inside its window.</summary>
    private void RunPending()
    {
        var action = _pendingAction;
        string? label = _pendingLabel;
        bool fresh = action != null && IltHubContext.NowMs - _pendingAt <= ConfirmWindowMs;
        ClearPending();
        if (!fresh || action == null) { _ctx.Chat("[ILT Hub] Nothing to confirm."); return; }

        RynthLog.Trace(LogCat.IltHub, $"confirmed: {label}");
        try { action(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.IltHub, ex, $"confirmed action '{label}'"); }
    }

    private void ClearPending()
    {
        _pendingAction = null;
        _pendingLabel = null;
        _pendingAt = 0;
    }

    private void HandleSuit(string[] a)
    {
        string sub = a.Length > 0 ? a[0].ToLowerInvariant() : "list";
        string? name = a.Length > 1 ? string.Join(" ", a.Skip(1)) : null;
        switch (sub)
        {
            case "list":
                var list = Gear.Suits.ListProfiles();
                _ctx.Chat("[ILT Hub] Suits: " + (list.Count > 0 ? string.Join(", ", list.Select(p => p.Name)) : "(none)"));
                break;
            case "test":
                foreach (string l in Gear.Suits.Test(name)) _ctx.Chat("[ILT Hub] " + l);
                break;
            case "load":
                // Equipping a suit swaps gear mid-session, so it needs an explicit confirm.
                RequestConfirm($"equip suit '{name ?? "(auto)"}'", () => _ctx.Chat("[ILT Hub] " + Gear.Suits.Load(name)));
                break;
            default:
                _ctx.Chat("[ILT Hub] Usage: /ra hub suit list|test [name]|load [name]");
                break;
        }
    }
}
