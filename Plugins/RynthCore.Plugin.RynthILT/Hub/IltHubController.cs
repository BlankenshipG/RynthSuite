// IltHubController.cs — Owns the ILT features behind the Mini Remote's ILT tabs: state, services,
// feature modules, commands.
// Originally SilentKelpie's ILT Hub controller (RynthAi IltHub/); moved to RynthILT 2026-10-13.
//
// RynthILTPlugin creates one controller per login session and forwards:
//   OnTick → Tick()             (pump thread)
//   OnChatWindowText → OnChat() (pump thread; returns true to eat the line)
//   OnRender → Render()         (render thread: Games HUD, confirmations)
//   the ILT "/ra hub ...", "/ra guardian ...", "/ra bounty ..." verbs (RynthAi forwards them
//   through RynthILT.Hub Command) and "/ilt ..." → HandleCommand()
//   OnLogout / teardown → OnLogout()
// The ILT sections are tabs of RynthAi's Mini Remote (RenderTab, through RynthILT.Hub); the
// Guardian is RynthCore's ILT panel (RenderGuardianPanel, RynthPluginRenderEmbed "guardian"),
// and bounties are RynthILT's Bounties window (RynthCore's ILT button), so "/ra bounty" and
// "/ra guardian window" open those engine panels. The Mini Remote's Quests and Pets tabs are
// RynthAi's (every world); RynthILT adds their ILT sections (the /qb list, the /pets and
// /shinies logs). On any world that isn't ILT-like every feature here stays dormant (no probes,
// no chat, no tabs).
using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthILT.Hub;

internal sealed class IltHubController
{
    private const long SaveIntervalMs = 5000;

    private readonly IltHubContext _ctx;
    private readonly IltHubUi _ui;
    private readonly List<IIltFeature> _features = new();
    private long _lastSaveAt;
    private int _questGeneration;        // RynthAi's /myquests refreshes seen (NoteQuestTrackerOutcome)
    private bool _availabilityAnnounced; // "open the Mini Remote" hint printed this session
    private volatile int _forceRequest = -1; // SetForceIlt from another thread, applied on the next tick

    /// <summary>How often the "RynthILT.Server" profile snapshot is rebuilt (pump thread).</summary>
    private const long ProfileRebuildMs = 1000;
    private long _profileBuiltAt;
    private volatile string _profileJson = string.Empty;
    private volatile IltTri[] _featureStates = Array.Empty<IltTri>();

    public IltBanking Banking { get; }
    public IltSessionRates Rates { get; }
    /// <summary>The quest bonus list (/qb): the Quests tab's section and the Temple tracker's stamps.</summary>
    public IltQuestBonus QuestBonus { get; }
    /// <summary>The /pets and /shinies logs (the Pets tab's section).</summary>
    public IltPetLogs PetLogs { get; }
    public IltProgression Progression { get; }
    public IltGear Gear { get; }
    public IltGames Games { get; }
    /// <summary>Registry charm acquired / active / server state (the Charms tab and RynthILTGetCharmsJson).</summary>
    public IltCharmTracker Charms { get; }
    /// <summary>The Mini Remote's Charms tab (registry charms with toggles, the Guardian Hand).</summary>
    public IltCharms CharmsTab { get; }
    /// <summary>Temple guardians: riddle translator, hand-in, attribute turn-in tracker.</summary>
    public IltGuardian Guardian { get; }

    /// <param name="writesBlocked">True while this character's copy from RynthAi is unfinished (IltHubStore.WritesBlocked).</param>
    /// <param name="legacyFolder">The character's RynthAi folder while that copy is unfinished (read-only fallback).</param>
    public IltHubController(RynthCoreHost host, string charFolder, IltSettings settings, RynthAiClient ai,
                            Func<bool>? writesBlocked = null, string? legacyFolder = null)
    {
        var store = new IltHubStore(host, charFolder);
        if (writesBlocked != null) store.WritesBlocked = writesBlocked;
        store.LegacyFolder = legacyFolder;
        var state = store.LoadState();
        var capture = new IltChatCapture(host);
        var inventory = new IltInventory(host, new IltObjects(host));
        IltHubContext? ctxRef = null;
        var options = new IltServerOptions(host, capture, state, text => ctxRef?.Chat(text), () => settings);
        _ctx = new IltHubContext(host, state, store, capture, options, inventory, settings, ai);
        ctxRef = _ctx;

        Banking = new IltBanking(_ctx);
        PetLogs = new IltPetLogs(_ctx);
        Rates = new IltSessionRates(_ctx);
        QuestBonus = new IltQuestBonus(_ctx);
        Progression = new IltProgression(_ctx);
        Gear = new IltGear(_ctx);
        Games = new IltGames(_ctx);
        Charms = new IltCharmTracker(_ctx);
        CharmsTab = new IltCharms(_ctx, Charms);
        Guardian = new IltGuardian(_ctx, QuestBonus);
        _features.AddRange(new IIltFeature[] { Banking, PetLogs, Rates, QuestBonus, Progression, Gear, CharmsTab, Games, Guardian });

        Banking.BalanceChanged += Rates.OnBankBalanceChanged;
        Rates.MovingMoney = () => Banking.MovingMoney;
        options.ProbeReplyTap = OnProbeReply;
        _ui = new IltHubUi(this, _ctx);
        _ctx.ShowTab = section => _ctx.Ai.MiniRemoteTab(IltSections.TabId(section), "show");
        RebuildProfile();
    }

    /// <summary>
    /// The latest "RynthILT.Server" profile JSON: the Leaftide feature list with the server's state
    /// for each, and the fellowship rules (Plugins/Shared/RynthIltServerApi.cs). Rebuilt on the pump
    /// about once a second, so any thread may read it.
    /// </summary>
    public string ProfileJson => _profileJson;

    /// <summary>
    /// Each IltFeatureCatalog.All entry's state, in the same order, from the same rebuild as
    /// <see cref="ProfileJson"/>. The render thread draws the feature list from this copy rather than
    /// the option bits the pump is writing. Any thread.
    /// </summary>
    public IltTri[] FeatureStates => _featureStates;

    /// <summary>Rebuilds the profile JSON and the feature-state copy from the current server options. Pump thread.</summary>
    private void RebuildProfile()
    {
        IltServerOptions o = _ctx.Options;
        bool ilt = o.IsIltLikeWorld;
        var states = new IltTri[IltFeatureCatalog.All.Length];
        for (int i = 0; i < states.Length; i++) states[i] = IltFeatureCatalog.StateOf(IltFeatureCatalog.All[i], ilt, o.Get);
        _featureStates = states;
        _profileJson = IltFeatureCatalog.BuildProfileJson(ilt, o.WorldName, _ctx.State.ForceLeaftideFeatures,
                                                          o.Source ?? string.Empty, o.LastRefreshUtc, o.Get,
                                                          QuestBonus.Snapshot());
    }

    public IltHubState State => _ctx.State;
    public IltServerOptions Options => _ctx.Options;
    internal IltHubContext Context => _ctx;

    /// <summary>World is ILT-like and the server has at least one ILT feature on.</summary>
    public bool Available => _ctx.Options.IsIltLikeWorld && _ctx.Options.AnyFeatureOn;

    /// <summary>
    /// Whether a section can be shown now. Character always can (it explains why the ILT tabs are
    /// idle and holds the Force-ILT override); the rest need server features.
    /// </summary>
    public bool SectionAvailable(IltSection section) => section switch
    {
        IltSection.Character => true,
        IltSection.Banking => Available && !_ctx.Options.IsOff(IltFeature.Bank),
        IltSection.Games => Available && Games.AnyGameAvailable,
        _ => Available,
    };

    /// <summary>
    /// The Mini Remote offers the Infinite Leaftide tabs: an ILT-like world (or the Force-ILT
    /// override). The Character tab then always shows; the others follow <see cref="SectionAvailable"/>.
    /// </summary>
    public bool OffersTabs => _ctx.Options.IsIltLikeWorld;

    /// <summary>Whether the Mini Remote's tab bar shows <paramref name="section"/>.</summary>
    public bool TabShown(IltSection section) => OffersTabs && IltSections.IsTab(section) && SectionAvailable(section);

    /// <summary>Render thread: one Mini Remote ILT tab's body (RynthAi's MiniRemoteHud draws the tab bar).</summary>
    public void RenderTab(IltSection section) => _ui.RenderTab(section);

    /// <summary>Render thread: the Guardian panel's body (RynthPluginRenderEmbed "guardian").</summary>
    public void RenderGuardianPanel() => _ui.RenderGuardianPanel();

    /// <summary>Render thread: one of the sections RynthAi draws (RynthILT.Hub RenderSection); false = nothing drawn.</summary>
    public bool RenderSection(string id) => _ui.RenderSection(id);

    /// <summary>The Force-ILT world override (world identity only; features still come from the server). Set from any thread.</summary>
    public bool ForceIltWorld
    {
        get => _ctx.State.ForceLeaftideFeatures;
        set => _forceRequest = value ? 1 : 0;
    }

    /// <summary>Re-asks the server which features are on (safe from the render thread: posted to the pump).</summary>
    public void RequestOptionsRefresh() => _ctx.Post(() => _ctx.Options.Refresh(manual: true));

    // ── Lifecycle ───────────────────────────────────────────────────────────

    /// <summary>
    /// Login: remember the character, schedule the options check, import UB data once.
    /// <paramref name="forceFromMigration"/>: the character's RynthAi profile had "Force enable" ticked.
    /// </summary>
    public void OnLoginComplete(string charName, bool forceFromMigration)
    {
        _ctx.CharName = charName ?? string.Empty;
        if (forceFromMigration) _ctx.State.ForceLeaftideFeatures = true;
        RynthLog.Event(LogEvents.IltLogin, $"login: char='{_ctx.CharName}' world='{_ctx.Options.WorldName}'");
        _ctx.Options.OnLoginComplete();
        _ctx.Inventory.Reset();
        Rates.Reset();
        _questGeneration = 0;

        if (!_ctx.State.UbSidecarsImported && _ctx.CharName.Length > 0)
        {
            _ctx.State.UbSidecarsImported = true;
            string world = _ctx.Options.WorldName;
            Banking.ImportUb(tx => _ctx.Store.ImportUbSidecars(_ctx.State, tx, world, _ctx.CharName));
        }
    }

    /// <summary>A RynthAi /myquests refresh ended since the last look: tell the options (the "quests" bit).</summary>
    private void PollQuestOutcome()
    {
        if (!_ctx.Ai.TryGetQuestStatus(out var st) || st.Generation == _questGeneration) return;
        _questGeneration = st.Generation;
        if (st.Generation > 0) _ctx.Options.NoteQuestTrackerOutcome(st.Disabled != 0, st.Count);
    }

    /// <summary>Pump-thread tick.</summary>
    public void Tick()
    {
        long now = IltHubContext.NowMs;
        int force = _forceRequest;
        if (force >= 0)
        {
            _forceRequest = -1;
            _ctx.State.ForceLeaftideFeatures = force == 1;
        }
        _ctx.DrainPosted();
        _ctx.Capture.Tick();
        _ctx.Options.Tick();
        PollQuestOutcome();

        // The profile other plugins read (RynthILT.Server): world, feature states, fellowship rules.
        if (now - _profileBuiltAt >= ProfileRebuildMs)
        {
            _profileBuiltAt = now;
            RebuildProfile();
        }

        // "Mini Remote on screen" gates bank auto-refresh and the gear / split-arrow scans.
        _ctx.State.WindowVisible = _ctx.Ai.MiniRemoteVisible;

        // One hint per session once the server confirms ILT features. The Mini Remote is separate
        // from the main RynthAi panel, so say how to open it.
        if (!_availabilityAnnounced && Available)
        {
            _availabilityAnnounced = true;
            if (!_ctx.State.WindowVisible)
                _ctx.Chat("[Mini Remote] ILT server features detected. Open the Mini Remote (bar button, /rc miniremote) for the ILT tabs.");
        }

        // Every feature here is Infinite Leaftide's: dormant on any other world.
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
            RynthLog.Event(LogEvents.IltConfirmation, $"confirmation expired: {_pendingLabel}");
            _ctx.Chat($"[Mini Remote] Confirmation for '{_pendingLabel}' expired.");
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
        IltPetLogs => LogCat.IltPets,
        IltSessionRates => LogCat.IltRates,
        IltQuestBonus => LogCat.IltQuests,
        IltProgression => LogCat.IltProgression,
        IltGear or IltCharms => LogCat.IltGear,
        IltGames => LogCat.IltGames,
        _ => LogCat.IltHub,
    };

    /// <summary>Logout / teardown: stop automation and flush to disk.</summary>
    public void OnLogout()
    {
        RynthLog.Event(LogEvents.IltLogout, $"logout: stopping features for '{_ctx.CharName}'");
        ClearPending();
        foreach (var f in _features)
        {
            try { f.OnLogout(); }
            catch (Exception ex) { RynthLog.Exception(CategoryOf(f), ex, $"{f.GetType().Name}.OnLogout"); }
        }
        _ctx.Capture.Clear();
        _ctx.Store.SaveStateIfDirty(_ctx.State);
    }

    /// <summary>
    /// JSON for the engine Skills panel's Progression tab (RynthILTGetProgressionJson).
    /// "available" is false off ILT-like worlds; the panel then hides the ILT sections. Pump thread.
    /// </summary>
    public string BuildProgressionJson()
    {
        var sb = new System.Text.StringBuilder(2048);
        sb.Append("{\"available\":").Append(Available ? "true" : "false");
        sb.Append(",\"world\":\"").Append(IltStyle.JsonEscape(_ctx.Options.WorldName ?? string.Empty)).Append("\",");
        Progression.AppendSnapshotJson(sb);
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>JSON for RynthILTGetCharmsJson / RynthPluginGetCharmsJson (an older engine's Settings "Charms Tracking" tab). Pump thread.</summary>
    public string BuildCharmsJson() => Charms.BuildJson();

    /// <summary>Render thread: Games HUD and confirmation popups.</summary>
    public void Render() => _ui.Render();

    // ── Probe taps: reuse login-probe replies so tabs fill without extra commands ──

    private void OnProbeReply(string key, IltChatResult r)
    {
        if (r.Lines.Count == 0) return;
        switch (key)
        {
            case IltFeature.Aug: Progression.ApplyAugLines(r.Lines); break;
            case IltFeature.Pets: PetLogs.ApplyRoster(false, r.Lines); break;
            case IltFeature.Shinies: PetLogs.ApplyRoster(true, r.Lines); break;
            case IltFeature.Qb: QuestBonus.ApplyQbProbe(r.Lines); break;
            // Bank lines are parsed passively by IltBanking.OnChat as they arrive.
        }
    }

    // ── Commands ────────────────────────────────────────────────────────────

    /// <summary>
    /// The ILT verbs: "/ra hub [open &lt;ILT tab&gt; [show|hide|toggle]|tab &lt;ILT tab&gt;|refresh|bank|status|
    /// force on|off|servers [list]|profile save|load|list ...|suit list|test|load ...|clap|attr ...|
    /// confirm|cancel]" (RynthAi keeps show/hide/toggle and its own Quests / Pets tabs, and forwards
    /// the rest here; "/ilt hub ..." comes straight here), "/ra bounty [show|hide|toggle]" (aliases
    /// bounties / bountys: RynthCore's ILT Bounties panel), "/ra guardian ..." (window: RynthCore's
    /// ILT Guardian panel; the rest IltGuardian.HandleCommand) and "rates reset" (the Mini Remote's
    /// Reset XP). Pump thread. Returns false if not handled.
    /// </summary>
    public bool HandleCommand(string verb, string[] args)
    {
        RynthLog.Event(LogEvents.IltCommand, $"command: /ra {verb} {string.Join(" ", args)}".TrimEnd());
        if (verb.Equals("rates", StringComparison.OrdinalIgnoreCase))
        {
            // "rates reset": the coin / pyreal session starts over with RynthAi's session stats.
            Rates.Reset();
            return true;
        }
        if (IsBountyVerb(verb))
        {
            // Bounties are RynthILT's Bounties window (RynthCore's ILT button); open that panel.
            OpenEnginePanel("bounties", "Bounties", args.Length > 0 ? args[0] : "toggle");
            return true;
        }
        if (verb.Equals("guardian", StringComparison.OrdinalIgnoreCase))
        {
            // "/ra guardian window [show|hide|toggle]" opens RynthCore's ILT Guardian panel;
            // everything else is the Guardian's.
            if (args.Length > 0 && args[0].Equals("window", StringComparison.OrdinalIgnoreCase))
                OpenEnginePanel("guardian", "Guardian", args.Length > 1 ? args[1] : "toggle");
            else
                Guardian.HandleCommand(args);
            return true;
        }
        if (!verb.Equals("hub", StringComparison.OrdinalIgnoreCase)) return false;

        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        string rest = args.Length > 1 ? string.Join(" ", args.Skip(1)) : string.Empty;
        switch (cmd)
        {
            case "show":
            case "hide":
            case "toggle":
                // The Mini Remote is RynthAi's ("/ilt hub show" lands here).
                _ctx.Chat("[Mini Remote] " + _ctx.Ai.MiniRemoteTab(IltSections.RemoteTab, cmd));
                break;
            case "open":
                if (args.Length > 1 && IsBountyVerb(args[1]))
                {
                    OpenEnginePanel("bounties", "Bounties", args.Length > 2 ? args[2] : "toggle");
                    break;
                }
                if (args.Length < 2 || !IltSections.TryParse(args[1], out IltSection section))
                {
                    // Quests and Pets are RynthAi's tabs: not ours to open.
                    if (args.Length >= 2 && IltSections.IsUniversalTabName(args[1])) return false;
                    _ctx.Chat("[Mini Remote] Usage: /ra hub open character|quests|pets|banking|gear|charms|games|guardian|bounties [show|hide|toggle]");
                    break;
                }
                OpenSection(section, args.Length > 2 ? args[2] : "toggle");
                break;
            case "tab":
                // The engine's dashboard: it opens its Mini Remote window itself, so only switch tabs.
                if (args.Length > 1 && IltSections.TryParse(args[1], out IltSection tab) && IltSections.IsTab(tab))
                    _ctx.Ai.MiniRemoteTab(IltSections.TabId(tab), "select");
                else
                    return false;   // the Remote tab or a RynthAi tab: RynthAi's
                break;
            case "refresh": _ctx.Options.Refresh(manual: true); break;
            case "bank": Banking.RequestRefresh(quiet: false); break;
            // "/ilt qb": RynthOracle's "Load /qb list" button (the list reaches it through the profile JSON).
            case "qb": _ctx.Chat("[RynthILT] " + QuestBonus.RefreshQb()); break;
            case "status": PrintStatus(); break;
            case "features": PrintFeatures(); break;
            case "force":
                _ctx.State.ForceLeaftideFeatures = !rest.Equals("off", StringComparison.OrdinalIgnoreCase);
                _ctx.Chat(IltServerOptions.EngineDecides
                    ? $"[Mini Remote] Force ILT world: {(_ctx.State.ForceLeaftideFeatures ? "on" : "off")} for this character (RynthCore counts it unless /rc server ilt off). Use /ra hub refresh to re-check the server."
                    : $"[Mini Remote] Force ILT world: {(_ctx.State.ForceLeaftideFeatures ? "on" : "off")}. Use /ra hub refresh to re-check the server.");
                break;
            case "servers": HandleServers(rest); break;
            case "profile": HandleProfile(args.Skip(1).ToArray()); break;
            case "suit": HandleSuit(args.Skip(1).ToArray()); break;
            case "clap": Gear.ChunkClap.ClapNow(manual: true); break;
            case "attr": HandleAttr(args.Skip(1).ToArray()); break;
            case "confirm": RunPending(); break;
            case "cancel":
                _ctx.Chat(_pendingAction != null ? $"[Mini Remote] Cancelled '{_pendingLabel}'." : "[Mini Remote] Nothing to cancel.");
                ClearPending();
                break;
            default:
                _ctx.Chat("[Mini Remote] /ra hub [show|hide|toggle] (Mini Remote)|refresh|bank|status|features|force on|off|servers [list]|clap|confirm|cancel");
                _ctx.Chat("[Mini Remote] /ra hub open character|quests|pets|banking|gear|charms|games [show|hide|toggle]  (Mini Remote tabs)");
                _ctx.Chat("[Mini Remote] /ra hub profile list|save <name> [shared]|load <name>   /ra hub suit list|test|load [name]");
                _ctx.Chat("[Mini Remote] /ra hub attr run|stop|costs|status|auto on|off|every <min>|raise <stat> <n>  (Skills panel > Progression)");
                _ctx.Chat("[Mini Remote] /ra quests [refresh|check <regex>|window [show|hide]|favhud [show|hide]]   /ra pets [show|hide]");
                _ctx.Chat("[Mini Remote] /ra guardian [window|translate <text>|handin|stop|chat on|off|auto on|off|buy on|off|refresh|status]  (window: ILT button > Guardian)");
                _ctx.Chat("[Mini Remote] /ra bounty [show|hide|toggle]  (RynthCore's ILT button > Bounties, /rc bounties)");
                _ctx.Chat("[Mini Remote] The ILT verbs also work as /ilt hub ..., /ilt guardian ... (RynthILT).");
                break;
        }
        return true;
    }

    /// <summary>"/ra hub features": every Leaftide feature RynthILT supports, with the server's state for each.</summary>
    private void PrintFeatures()
    {
        IltServerOptions o = _ctx.Options;
        string world = o.WorldName.Length > 0 ? o.WorldName : "unknown world";
        _ctx.Chat($"[Mini Remote] Leaftide features on {world} ({(o.IsIltLikeWorld ? "ILT world" : "not an ILT world")}, "
                  + $"fellowships up to {IltFeatureCatalog.FellowshipMax(o.IsIltLikeWorld)}):");
        foreach (string line in IltFeatureCatalog.BuildChatLines(o.IsIltLikeWorld, o.Get))
            _ctx.Chat("[Mini Remote]   " + line);
    }

    /// <summary>"/ra hub servers [list | &lt;names, comma separated&gt; | default]": which world names count as Infinite Leaftide.</summary>
    private void HandleServers(string rest)
    {
        string arg = rest.Trim();
        if (arg.Length > 0 && !arg.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            var list = arg.Equals("default", StringComparison.OrdinalIgnoreCase)
                ? new List<string>(ServerFeatureGate.DefaultServerNames)
                : ServerFeatureGate.ParseList(arg);
            if (list.Count == 0) { _ctx.Chat("[Mini Remote] Usage: /ra hub servers <name, *pattern*, ...> | default | list"); return; }
            _ctx.Settings.ServerNames = list;
            _ctx.SaveSettings();
        }
        _ctx.Chat("[Mini Remote] ILT world names: " + ServerFeatureGate.FormatList(ServerFeatureGate.Patterns(_ctx.Settings)));
        _ctx.Chat("[Mini Remote] " + ServerFeatureGate.Describe(_ctx.Options.WorldName, _ctx.Settings, _ctx.State.ForceLeaftideFeatures));
    }

    /// <summary>"/ra hub attr ...": the attribute raiser from chat (same verbs as the Progression tab).</summary>
    private void HandleAttr(string[] a)
    {
        var raiser = Progression.Attributes;
        string sub = a.Length > 0 ? a[0].ToLowerInvariant() : "status";
        switch (sub)
        {
            case "run": raiser.HandleRemote("attrrun", new[] { "attrrun" }); break;
            case "stop": raiser.HandleRemote("attrstop", new[] { "attrstop" }); break;
            case "costs": raiser.HandleRemote("attrcosts", new[] { "attrcosts" }); break;
            case "auto": raiser.HandleRemote("attrauto", new[] { "attrauto", a.Length > 1 ? a[1] : "off" }); break;
            case "every": raiser.HandleRemote("attrevery", new[] { "attrevery", a.Length > 1 ? a[1] : "5" }); break;
            case "raise" when a.Length > 2: raiser.HandleRemote("attrraise", new[] { "attrraise", a[1], a[2] }); break;
            default:
                var c = _ctx.State.Character;
                string blocked = raiser.Blocker() ?? "ready";
                _ctx.Chat($"[Mini Remote] Attribute raiser: {blocked}; auto={(c.AttrAutoRaise ? $"on every {c.AttrAutoRaiseMinutes}m" : "off")} "
                          + $"mode={(IltAttributeRaiser.Mode)c.AttrRaiseMode} keep={IltParse.Compact(c.AttrKeepXp)} "
                          + $"stats={(c.AttrRaiseStats.Count > 0 ? string.Join(",", c.AttrOrder.Where(c.AttrRaiseStats.Contains)) : "none ticked")}");
                return;
        }
    }

    /// <summary>"/ra bounty", "/ra bounties" and the "/ra bountys" spelling all reach the Bounties panel.</summary>
    public static bool IsBountyVerb(string verb)
        => verb.Equals("bounty", StringComparison.OrdinalIgnoreCase)
           || verb.Equals("bounties", StringComparison.OrdinalIgnoreCase)
           || verb.Equals("bountys", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Applies show / hide / toggle to a section: a Mini Remote tab, or for the Guardian
    /// RynthCore's ILT panel. Says what happened.
    /// </summary>
    private void OpenSection(IltSection section, string mode)
    {
        if (!IltSections.IsTab(section))
        {
            OpenEnginePanel(IltSections.TabId(section), IltSections.Label(section), mode);
            return;
        }
        string reply = _ctx.Ai.MiniRemoteTab(IltSections.TabId(section), mode);
        if (!SectionAvailable(section) && !mode.Equals("hide", StringComparison.OrdinalIgnoreCase))
            reply = $"The {IltSections.Label(section)} tab shows once the server reports that feature on.";
        _ctx.Chat("[Mini Remote] " + reply);
    }

    /// <summary>
    /// Opens / closes one of RynthCore's ILT panels ("/rc bounties|guardian [show|hide|toggle]")
    /// through the engine's chat dispatcher. Pump thread.
    /// </summary>
    public void OpenEnginePanel(string rcVerb, string label, string mode)
    {
        string m = (mode ?? string.Empty).ToLowerInvariant() switch
        {
            "show" or "on" or "open" => "show",
            "hide" or "off" or "close" => "hide",
            _ => "toggle",
        };
        if (_ctx.Host.HasInvokeChatParser && _ctx.Host.InvokeChatParser($"/rc {rcVerb} {m}"))
            return;
        _ctx.Chat($"[Mini Remote] {label} is a RynthCore ILT panel: the bar's ILT button, or /rc {rcVerb}.");
    }

    /// <summary>"show"/"on" → true, "hide"/"off" → false, anything else flips <paramref name="current"/>.</summary>
    private static bool ResolveToggle(string mode, bool current) => mode switch
    {
        "show" or "on" or "true" or "1" => true,
        "hide" or "off" or "false" or "0" => false,
        _ => !current,
    };

    private void PrintStatus()
    {
        var o = _ctx.Options;
        _ctx.Chat($"[Mini Remote] World '{o.WorldName}' ILT={o.IsIltLikeWorld} force={_ctx.State.ForceLeaftideFeatures} source={(o.Source.Length > 0 ? o.Source : "none")} "
                  + $"refreshed={(o.LastRefreshUtc == DateTime.MinValue ? "never" : o.LastRefreshUtc.ToLocalTime().ToString("t"))}");
        var bits = o.SnapshotBits();
        string on = string.Join(", ", bits.Where(b => b.Value == 1).Select(b => b.Key));
        string off = string.Join(", ", bits.Where(b => b.Value == 0).Select(b => b.Key));
        _ctx.Chat("[Mini Remote] on: " + (on.Length > 0 ? on : "-"));
        _ctx.Chat("[Mini Remote] off: " + (off.Length > 0 ? off : "-"));
    }

    private void HandleProfile(string[] a)
    {
        string sub = a.Length > 0 ? a[0].ToLowerInvariant() : "list";
        if (sub == "list")
        {
            var names = _ctx.Store.ListProfiles();
            _ctx.Chat("[Mini Remote] Profiles: " + (names.Count > 0 ? string.Join(", ", names) : "(none)"));
            return;
        }
        if (a.Length < 2) { _ctx.Chat("[Mini Remote] Usage: /ra hub profile save <name> [shared] | load <name>"); return; }
        if (sub == "save")
        {
            bool shared = a.Length > 2 && a[2].Equals("shared", StringComparison.OrdinalIgnoreCase);
            _ctx.Chat(_ctx.Store.SaveProfile(_ctx.State, a[1], shared, out string err)
                ? $"[Mini Remote] Saved {(shared ? "shared " : "")}profile '{a[1]}'." : "[Mini Remote] Save failed: " + err);
        }
        else if (sub == "load")
        {
            // Loading replaces the Hub's current settings, so it needs an explicit confirm.
            string profile = a[1];
            RequestConfirm($"load Hub profile '{profile}' (replaces current Hub settings)", () =>
                _ctx.Chat(_ctx.Store.LoadProfile(_ctx.State, profile, out string err)
                    ? $"[Mini Remote] Loaded profile '{profile}'." : "[Mini Remote] Load failed: " + err));
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
        RynthLog.Event(LogEvents.IltConfirmation, $"confirmation requested: {label}");
        _ctx.Chat($"[Mini Remote] About to {label}. Type /ra hub confirm within {ConfirmWindowMs / 1000} s, or /ra hub cancel.");
    }

    /// <summary>Runs the pending action if one is armed and still inside its window.</summary>
    private void RunPending()
    {
        var action = _pendingAction;
        string? label = _pendingLabel;
        bool fresh = action != null && IltHubContext.NowMs - _pendingAt <= ConfirmWindowMs;
        ClearPending();
        if (!fresh || action == null) { _ctx.Chat("[Mini Remote] Nothing to confirm."); return; }

        RynthLog.Event(LogEvents.IltConfirmation, $"confirmed: {label}");
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
                _ctx.Chat("[Mini Remote] Suits: " + (list.Count > 0 ? string.Join(", ", list.Select(p => p.Name)) : "(none)"));
                break;
            case "test":
                foreach (string l in Gear.Suits.Test(name)) _ctx.Chat("[Mini Remote] " + l);
                break;
            case "load":
                // Equipping a suit swaps gear mid-session, so it needs an explicit confirm.
                RequestConfirm($"equip suit '{name ?? "(auto)"}'", () => _ctx.Chat("[Mini Remote] " + Gear.Suits.Load(name)));
                break;
            default:
                _ctx.Chat("[Mini Remote] Usage: /ra hub suit list|test [name]|load [name]");
                break;
        }
    }
}
