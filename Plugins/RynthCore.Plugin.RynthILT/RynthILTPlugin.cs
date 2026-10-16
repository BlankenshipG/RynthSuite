// RynthILTPlugin.cs - Infinite Leaftide (ACECustom) features, kept out of RynthAi and the engine core.
//
// Two features, both shown only on ILT:
//   * Bounties (Bounty/BountyTracker.cs): the engine's Bounties window (the bar's ILT button,
//     /rc bounties). Its reads run only while that window polls RynthILTGetBountiesJson, and the
//     engine polls only on ILT.
//   * The ILT Hub (Hub/, SilentKelpie's, moved here from RynthAi 2026-10-13): the Mini Remote's
//     ILT tabs (Gear, Games; RynthAi's Mini Remote hosts them through RynthILT.Hub,
//     HubInterface.cs; Character / Charms moved to RynthOracle's Character tab and Banking to
//     RynthInventory's Global Inventory, both fed by "RynthILT.Data", DataInterface.cs), the ILT
//     session extras in RynthCore's Session Stats section, its sections in RynthAi's Quests / Pets / Remote tabs, the
//     Guardian panel (RynthPluginRenderEmbed "guardian"), the Skills panel's Progression data
//     (RynthILTGetProgressionJson, "prog ..." through RynthILTCommand), server feature detection.
//     Every feature stays dormant off an ILT-like world (IltServerOptions: no probes, no chat).
//
// Second: a copy of the ILT settings (IltSettingsView/): RynthCore's verdict, this character's
// override (read only: it is set in RynthCore Options > Diagnostics), the ILT world names and a
// summary of the features the Hub read from the server (the full list is RynthOracle's About tab),
// in its own window ("/ilt settings"). The Hub follows RynthCore's verdict once one arrives
// (IltEngineVerdict).
//
// Third: ILT's fellowship commands (IltFellowship/, ACECustom /fship) as buttons in RynthCore's
// Fellowship section and their own window ("/ilt fellow"). While ILT is active the list is also
// sent to RynthAi ("servercommands"), whose Fellowship view draws them; an empty list clears it.
//
// Threads: OnTick, OnChatWindowText, OnKillNotification, login/logout and the engine's export calls
// run on the plugin pump. OnChatBarEnter runs on AC's main thread (the engine's dispatcher queues it
// there, our own InvokeChatParser too), and RynthAi forwards its "/ra hub ..." verbs from there too,
// so commands only queue and run on the next tick. OnRender / RenderEmbed and the RynthILT.Hub
// render functions run on the render thread and only read what the pump built; the settings and
// fellowship views queue their chat commands for the tick.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using ImGuiNET;
using RynthCore.Plugin.RynthILT.Bounty;
using RynthCore.Plugin.RynthILT.Hub;
using RynthCore.Plugin.RynthILT.IltFellowship;
using RynthCore.Plugin.RynthILT.IltSettingsView;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;

namespace RynthCore.Plugin.RynthILT;

public sealed class RynthILTPlugin : RynthPluginBase
{
    internal static readonly IntPtr NamePointer    = Marshal.StringToHGlobalAnsi("RynthILT");
    // Previous release: 0.2.8.
    internal static readonly IntPtr VersionPointer = Marshal.StringToHGlobalAnsi("0.2.9");

    /// <summary>
    /// The settings view's surface. No longer in the sections manifest (the force-server override
    /// lives only in RynthCore Options > Diagnostics), but still drawn for an engine that asks.
    /// </summary>
    internal const string SettingsSurface = "section:ilt-settings";

    /// <summary>The ILT session extras' surface in RynthCore's Mini Remote "Session Stats" section.</summary>
    internal const string RatesSurface = "section:ilt-rates";

    /// <summary>The fellowship commands' surface in RynthCore's Fellowship section.</summary>
    internal const string FellowshipSurface = "section:ilt-fellowship";

    /// <summary>RynthAi's name for the plugin-command broker (its Fellowship view draws our commands).</summary>
    private const string RynthAiPlugin = "RynthAi";

    private readonly IltSettingsModel _settingsModel = new();
    private readonly IltSettingsUi _settingsUi;
    private readonly IltFellowshipUi _fellowshipUi;
    // Chat commands from the settings and fellowship views (render thread), run on the next tick.
    private readonly ConcurrentQueue<string> _chatCommands = new();
    private const int MaxQueuedChatCommands = 32;

    public RynthILTPlugin()
    {
        _settingsUi = new IltSettingsUi(_settingsModel, cmd => QueueChatCommand(cmd), RefreshFeatures);
        _fellowshipUi = new IltFellowshipUi(QueueChatCommand);
        IltEngineVerdict.Attach(_settingsModel);
    }

    /// <summary>Render thread: queues a chat command for the tick; false when the engine can't run one.</summary>
    private bool QueueChatCommand(string command)
    {
        if (!Host.HasInvokeChatParser || _chatCommands.Count >= MaxQueuedChatCommands) return false;
        _chatCommands.Enqueue(command);
        return true;
    }

    /// <summary>Wait this long after login for the character's name before tracking without a save file.</summary>
    private const long NameWaitMs = 30_000;
    private const long NameRetryMs = 1_000;

    private bool _loginComplete;
    private long _loginAtMs;
    private long _nextNameTryMs;
    private long _nextHubTryMs;
    private BountyTracker? _bounties;
    private IltHubController? _hub;
    // An unfinished copy from RynthAi (IltMigration pending): the Hub writes nothing for this
    // character and the copy is retried every MigrationRetryMs; once it works the Hub restarts on
    // the copied files.
    private const long MigrationRetryMs = 120_000;
    private volatile bool _hubWritesBlocked;
    private long _nextMigrationRetryMs;
    private string _hubFolder = string.Empty, _hubWorld = string.Empty, _hubChar = string.Empty;
    private bool _forceOnRestart;
    private RynthAiClient? _ai;
    private IltSettings _settings = new();

    // "/ilt ..." lines from OnChatBarEnter and RynthAi's forwarded "/ra" verbs, run on the next tick.
    private readonly ConcurrentQueue<string[]> _commands = new();
    // "/bounty ..." lines from OnChatBarEnter (typed by the player, or our own coming back through
    // the engine's dispatcher), passed to the tracker before the next chat line or tick.
    private readonly ConcurrentQueue<string> _typedBounty = new();
    // Engine / plugin commands (RynthPluginApplyRemoteCommand: "prog ..." from RynthAi or an older engine).
    private readonly ConcurrentQueue<string> _engineCommands = new();

    /// <summary>The Bounties window's tracker (PluginExports); null until login.</summary>
    internal BountyTracker? Bounties => _bounties;

    /// <summary>The ILT Hub for the logged-in character (HubInterface, PluginExports); null until its name is known.</summary>
    internal IltHubController? Hub => _hub;

    public override int Initialize()
    {
        RynthLog.Init(Host);
        // The Hub's windows and RynthAi-hosted tabs draw through the engine's cimgui module.
        ImGuiNativeBinding.Ensure(msg => RynthLog.Write(LogCat.IltHub, msg));
        // Every use goes through Host.UseFor (Plugins/Shared/UseAudit.cs): one log line each.
        UseAudit.Reset("RynthILT", null);
        // Saves (Hub state, bank log, profiles, settings, bounty list, the copies from RynthAi) say
        // when they fail and why (Plugins/Shared/DataWriteGuard.cs): always in the log, in chat
        // once per folder, and only on an ILT world (PostDataWriteProblems).
        DataWriteGuard.Reset();
        DataWriteGuard.PluginName = "RynthILT";
        DataWriteGuard.LogSink = msg => RynthLog.Write(LogCat.IltStore,
            msg.StartsWith("[RynthILT] ", StringComparison.Ordinal) ? msg.Substring(11) : msg);
        _ai = new RynthAiClient(Host);
        _settings = IltSettingsStore.Load();
        Host.Log("[RynthILT] Plugin initialized.");
        return 0;
    }

    // Also called on a freshly (re)loaded plugin while already in game.
    public override void OnLoginComplete()
    {
        SaveBounties();
        StopHub();
        _bounties = null;
        _loginComplete = true;
        _loginAtMs = Environment.TickCount64;
        _nextNameTryMs = 0;
        _nextHubTryMs = 0;
        _ai?.Reset();
        PollServerState(force: true);
        TryStartBounties();
        TryStartHub();
    }

    public override void OnLogout()
    {
        SaveBounties();
        _bounties = null;
        StopHub();
        _ai?.Reset();
        _loginComplete = false;
        ClearSharedFellowshipCommands();
        while (_commands.TryDequeue(out _)) { }
        while (_typedBounty.TryDequeue(out _)) { }
        while (_engineCommands.TryDequeue(out _)) { }
        _settingsModel.Clear();
    }

    // Plugin unload (hot reload, update, client closing): keep the list and the Hub state for the
    // next copy. No broker call while the engine unloads us: RynthAi drops the fellowship commands
    // once they stop arriving (ServerCommandModel.StaleMs).
    public override void Shutdown()
    {
        SaveBounties();
        StopHub();
        HubInterface.Release();
        IltEngineVerdict.Attach(null);
        RynthLog.Shutdown();
    }

    public override void OnTick()
    {
        try { RynthLog.Pump(); } catch { }
        if (!_loginComplete) return;
        _ai?.Tick();
        PollServerState(force: false);
        if (_bounties == null) TryStartBounties();
        if (_hub == null) TryStartHub();
        DrainTypedBounty();
        while (_commands.TryDequeue(out string[]? parts)) RunCommand(parts);
        while (_engineCommands.TryDequeue(out string? engineCommand)) RunEngineCommand(engineCommand);
        DrainChatCommands();
        _bounties?.Tick();
        try { _hub?.Tick(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.IltHub, ex, "Tick"); }
        RetryPendingMigration();
        PostDataWriteProblems();
        try { ShareFellowshipCommands(); }
        catch (Exception ex) { Host.Log($"[RynthILT] Fellowship commands to RynthAi failed: {ex.Message}"); }
        try { PublishHubFeatures(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.IltHub, ex, "PublishHubFeatures"); }
    }

    /// <summary>Plugin tick: runs the settings and fellowship views' chat commands through the engine.</summary>
    private void DrainChatCommands()
    {
        while (_chatCommands.TryDequeue(out string? command))
        {
            if (!Host.HasInvokeChatParser || !Host.InvokeChatParser(command))
                ChatLine("[RynthILT] RynthCore didn't take: " + command);
        }
    }

    private const long FeaturePublishMs = 1000;
    private long _featurePublishNextAt;

    /// <summary>
    /// Plugin tick, at most once a second: the Hub's server read (world, last check, feature bits)
    /// for the ILT settings view (IltSettingsModel.PublishFeatures); cleared while there is no Hub.
    /// </summary>
    private void PublishHubFeatures()
    {
        long now = Environment.TickCount64;
        if (now < _featurePublishNextAt) return;
        _featurePublishNextAt = now + FeaturePublishMs;
        var hub = _hub;
        if (hub == null)
        {
            _settingsModel.PublishFeatures(null);
            return;
        }
        var options = hub.Options;
        DateTime last = options.LastRefreshUtc;
        string when = last == default ? string.Empty : last.ToLocalTime().ToString("HH:mm:ss");
        _settingsModel.PublishFeatures(new IltFeatureView(options.WorldName, options.Source ?? string.Empty, when,
            options.IsRefreshing, options.IsIltLikeWorld, BuildFeatureRows(options)));
    }

    /// <summary>
    /// The settings view's feature rows: every catalog feature with its state (IltFeatureCatalog),
    /// then each ILT charm ("charm:...", on with the world unless refused in chat), then any other
    /// bit chat or a probe set that the catalog doesn't show. Pump thread.
    /// </summary>
    private static KeyValuePair<string, int>[] BuildFeatureRows(IltServerOptions options)
    {
        bool ilt = options.IsIltLikeWorld;
        var rows = new List<KeyValuePair<string, int>>(IltFeatureCatalog.All.Length + 32);
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IltFeatureInfo f in IltFeatureCatalog.All)
        {
            rows.Add(new KeyValuePair<string, int>(f.Key, (int)IltFeatureCatalog.StateOf(f, ilt, options.Get)));
            if (f.ServerBit != null) shown.Add(f.ServerBit);
        }
        foreach (IltCharmDef def in IltInventory.Charms)
        {
            string key = IltFeature.Charm(def.Name);
            rows.Add(new KeyValuePair<string, int>(key, (int)IltFeatureCatalog.CharmStateOf(ilt, options.Get(key))));
            shown.Add(key);
        }
        foreach (var bit in options.SnapshotBits())
            if (shown.Add(bit.Key)) rows.Add(bit);
        return rows.ToArray();
    }

    /// <summary>
    /// The save failures queued by DataWriteGuard, in chat once the Hub knows this is an ILT world
    /// (or "Treat this world as ILT"). Off ILT they stay in the log only: nothing ILT shows there.
    /// </summary>
    private void PostDataWriteProblems()
    {
        if (DataWriteGuard.PendingCount == 0 || _hub?.OffersTabs != true || !Host.HasWriteToChat) return;
        DataWriteGuard.DrainChat(line => Host.WriteToChat(line, 2));
    }

    // ── ILT fellowship commands (IltFellowship/) ────────────────────────────

    private const long FellowshipShareCheckMs = 2000;
    private const long FellowshipShareResendMs = 10_000;
    private long _fellowshipShareNextAt;
    private long _fellowshipShareSentAt;
    // The last payload RynthAi took; empty when nothing was delivered (or after a clear).
    private string _fellowshipShareLast = string.Empty;

    /// <summary>
    /// Plugin tick: sends RynthAi ILT's fellowship commands while ILT is active (an empty list when
    /// it isn't), when that changes and every <see cref="FellowshipShareResendMs"/> (RynthAi may load
    /// later, and drops a list that stops arriving). Nothing on an engine without the broker.
    /// </summary>
    private void ShareFellowshipCommands()
    {
        if (!Host.HasSendPluginCommand) return;
        long now = Environment.TickCount64;
        if (now < _fellowshipShareNextAt) return;
        _fellowshipShareNextAt = now + FellowshipShareCheckMs;

        bool active = IltActive();
        // Nothing to clear when RynthAi never got a list.
        if (!active && _fellowshipShareLast.Length == 0) return;
        string payload = IltFellowshipCommands.Payload(active);
        if (payload == _fellowshipShareLast && now - _fellowshipShareSentAt < FellowshipShareResendMs) return;
        _fellowshipShareSentAt = now;
        // Not delivered (RynthAi not installed or not loaded yet): the resend timer tries again.
        if (Host.SendPluginCommand(RynthAiPlugin, ServerCommandShare.Action, payload))
            _fellowshipShareLast = active ? payload : string.Empty;
    }

    /// <summary>Clears RynthAi's copy of the fellowship commands (logout) if it has one.</summary>
    private void ClearSharedFellowshipCommands()
    {
        if (_fellowshipShareLast.Length == 0) return;
        _fellowshipShareLast = string.Empty;
        _fellowshipShareNextAt = 0;
        try
        {
            if (Host.HasSendPluginCommand)
                Host.SendPluginCommand(RynthAiPlugin, ServerCommandShare.Action, IltFellowshipCommands.Payload(active: false));
        }
        catch { /* RynthAi drops a list that stops arriving */ }
    }

    /// <summary>
    /// The Hub's world check (RynthCore's verdict once it has sent one, else RynthILT's server list
    /// and "Treat this world as ILT"); before the Hub exists, the engine verdict or the world name
    /// (IltFellowshipCommands.IsActive).
    /// </summary>
    private bool IltActive()
    {
        var hub = _hub;
        if (hub != null) return hub.Options.IsIltLikeWorld;
        bool? engine = IltEngineVerdict.Active;
        string world = engine == null && Host.HasGetWorldName && Host.TryGetWorldName(out string w) ? w : string.Empty;
        return IltFellowshipCommands.IsActive(engine, world);
    }

    // ── ILT settings ─────────────────────────────────────────────────────────

    /// <summary>
    /// The broker actions parsed on arrival rather than queued for the tick: RynthCore's
    /// "servermenu" push and the older "iltfeatures" text (IltSettingsModel has both formats).
    /// </summary>
    internal static bool IsParsedRemoteAction(string action) =>
        action.Equals("servermenu", StringComparison.OrdinalIgnoreCase)
        || action.Equals("iltfeatures", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// RynthPluginApplyRemoteCommand: "servermenu" from RynthCore, "iltfeatures" from an older
    /// sender. Any thread: parsed and published, nothing else. The Hub's next world check follows
    /// a changed verdict (IltEngineVerdict).
    /// </summary>
    internal void ApplyRemoteCommand(string action, string value)
    {
        if (action.Equals("servermenu", StringComparison.OrdinalIgnoreCase)) _settingsModel.ApplyEngine(value);
        else if (action.Equals("iltfeatures", StringComparison.OrdinalIgnoreCase)) _settingsModel.ApplyFeatures(value);
    }

    /// <summary>Asks the Hub to read the server's ILT features again (as "/ilt hub refresh", on the next tick). Render thread.</summary>
    private bool RefreshFeatures()
    {
        if (!_loginComplete || _hub == null) return false;
        _commands.Enqueue(new[] { "/ilt", "hub", "refresh" });
        return true;
    }

    /// <summary>
    /// RynthPluginRenderEmbed: <see cref="SettingsSurface"/> (the settings view, Char section) or
    /// <see cref="FellowshipSurface"/> (the fellowship commands, Fellowship section) in RynthCore's
    /// Mini Remote, drawn into the engine's current window and context (popped-out windows have
    /// their own). <paramref name="wantHeight"/> is the height it used. False until login or for
    /// another surface. Render thread.
    /// </summary>
    internal bool RenderEmbedded(string surface, out float wantHeight)
    {
        wantHeight = 0;
        if (!_loginComplete) return false;
        Action? body = surface switch
        {
            SettingsSurface => _settingsUi.RenderBody,
            FellowshipSurface => _fellowshipUi.RenderBody,
            RatesSurface => RenderRatesSurface,
            _ => null,
        };
        if (body == null) return false;
        IntPtr previous = ImGui.GetCurrentContext();
        IntPtr context = previous != IntPtr.Zero ? previous : Host.ImGuiContext;
        if (context == IntPtr.Zero) return false;
        ImGui.SetCurrentContext(context);
        try
        {
            float startY = ImGui.GetCursorPosY();
            body();
            wantHeight = MathF.Max(40f, ImGui.GetCursorPosY() - startY + 4f);
            return true;
        }
        catch (Exception ex)
        {
            Host.Log($"[RynthILT] View {surface} failed: {ex.Message}");
            return false;
        }
        finally { ImGui.SetCurrentContext(previous); }
    }

    /// <summary>The ILT session extras (render thread), or a line saying they start with the Hub.</summary>
    private void RenderRatesSurface()
    {
        IltHubController? hub = _hub;
        if (hub == null) { ImGui.TextDisabled("ILT session rates start once RynthILT's Hub is ready."); return; }
        hub.RenderRatesSection();
    }

    /// <summary>
    /// RynthPluginGetSectionsJson: the ILT session extras in RynthCore's Session Stats section
    /// (the "char" id) and the fellowship commands in its Fellowship section, Infinite Leaftide
    /// areas (shown while RynthCore treats this world as ILT). Null before login.
    /// </summary>
    internal string? BuildSectionsJson() => _loginComplete ? SectionsJson : null;

    private const string SectionsJson =
        "{\"version\":1,\"sections\":[],\"contributions\":[" +
        "{\"section\":\"char\",\"server\":\"ilt\"," +
        "\"label\":\"ILT session rates (RynthILT)\",\"surface\":\"" + RatesSurface + "\",\"primary\":false," +
        "\"height\":220,\"enabled\":true,\"tooltip\":\"Enlightened Coins, pyreals and item conversions this session, " +
        "on the same clock as RynthAi's session stats (RynthILT).\",\"order\":120}," +
        "{\"section\":\"fellowship\",\"server\":\"ilt\"," +
        "\"label\":\"ILT fellowship (RynthILT)\",\"surface\":\"" + FellowshipSurface + "\",\"primary\":false," +
        "\"height\":110,\"enabled\":true,\"tooltip\":\"Infinite Leaftide's /fship commands: create, invite, dismiss, " +
        "invite your landblock, list nearby, leave, disband (RynthILT; also /ilt fellow).\",\"order\":120}]}";

    public override void OnChatWindowText(string? text, int chatType, ref int eat)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            // The Hub eats its own probe / capture replies (IltChatCapture).
            if (_hub?.OnChat(text) == true) eat = 1;
        }
        catch (Exception ex) { RynthLog.Exception(LogCat.IltHub, ex, "OnChat"); }
        if (_bounties == null) return;
        DrainTypedBounty();
        try
        {
            // Eats only the reply to the tracker's own /bounty list (BountyTracker.OnChat).
            if (_bounties.OnChat(text)) eat = 1;
        }
        catch (Exception ex)
        {
            Host.Log($"[RynthILT] Bounty chat parse failed: {ex.Message}");
        }
    }

    public override void OnChatBarEnter(string? text, ref int eat)
    {
        if (!_loginComplete || string.IsNullOrEmpty(text)) return;
        string trimmed = text.Trim();
        // A /bounty going out: its reply must not be eaten (BountyTracker.NoteTypedCommand). Never eaten here.
        if (BountyTracker.IsBountyCommand(trimmed))
        {
            _typedBounty.Enqueue(trimmed);
            return;
        }
        if (!trimmed.Equals("/ilt", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("/ilt ", StringComparison.OrdinalIgnoreCase))
            return;
        eat = 1;
        _commands.Enqueue(trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // The server's killer notification: this character landed the killing blow.
    public override void OnKillNotification(string? deathMessage) => _bounties?.OnKill();

    private void DrainTypedBounty()
    {
        while (_typedBounty.TryDequeue(out string? typed))
            _bounties?.NoteTypedCommand(typed);
    }

    // ── Rendering (render thread) ────────────────────────────────────────────

    /// <summary>
    /// The Hub's floating extras (Games HUD, confirmations) and the standalone ILT settings and
    /// fellowship windows ("/ilt settings", "/ilt fellow") in the engine's ImGui frame.
    /// </summary>
    public override void OnRender()
    {
        var hub = _hub;
        if (!_loginComplete || Host.ImGuiContext == IntPtr.Zero) return;
        if (hub == null && !_settingsUi.WindowOpen && !_fellowshipUi.WindowOpen) return;
        IntPtr previous = ImGui.GetCurrentContext();
        ImGui.SetCurrentContext(Host.ImGuiContext);
        try
        {
            try { hub?.Render(); }
            catch (Exception ex) { RynthLog.Exception(LogCat.IltHub, ex, "OnRender"); }
            try { _settingsUi.RenderWindow(); }
            catch (Exception ex) { Host.Log($"[RynthILT] Settings window failed: {ex.Message}"); _settingsUi.WindowOpen = false; }
            try { _fellowshipUi.RenderWindow(); }
            catch (Exception ex) { Host.Log($"[RynthILT] Fellowship window failed: {ex.Message}"); _fellowshipUi.WindowOpen = false; }
        }
        finally { ImGui.SetCurrentContext(previous); }
    }

    /// <summary>The same while the engine's ImGui shell is off (RynthPluginRenderOverlay), in the dashboard colours.</summary>
    internal void OnRenderOverlay()
    {
        var hub = _hub;
        if (!_loginComplete || hub == null || Host.ImGuiContext == IntPtr.Zero) return;
        IntPtr previous = ImGui.GetCurrentContext();
        ImGui.SetCurrentContext(Host.ImGuiContext);
        int pushed = IltStyle.PushDashboardStyle();
        try { hub.Render(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.IltHub, ex, "OnRenderOverlay"); }
        finally
        {
            ImGui.PopStyleColor(pushed);
            ImGui.SetCurrentContext(previous);
        }
    }

    /// <summary>
    /// RynthPluginRenderEmbed "guardian": the Temple guardian tools inside RynthCore's Guardian
    /// panel (the bar's ILT button), drawn into the engine's current ImGui window and context
    /// (a popped-out panel has its own context, not Host.ImGuiContext; switching to the main one
    /// there crashed the client, 2026-10-11). False (nothing drawn) until login and the Hub exist.
    /// </summary>
    internal bool RenderEmbeddedGuardian()
    {
        var hub = _hub;
        if (!_loginComplete || hub == null || Host.ImGuiContext == IntPtr.Zero) return false;
        IntPtr previous = ImGui.GetCurrentContext();
        if (previous == IntPtr.Zero) ImGui.SetCurrentContext(Host.ImGuiContext);
        int pushed = IltStyle.PushDashboardStyle();
        try
        {
            hub.RenderGuardianPanel();
            return true;
        }
        catch (Exception ex)
        {
            RynthLog.Exception(LogCat.IltHub, ex, "RenderEmbeddedGuardian");
            return false;
        }
        finally
        {
            ImGui.PopStyleColor(pushed);
            if (previous == IntPtr.Zero) ImGui.SetCurrentContext(previous);
        }
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    /// <summary>
    /// RynthILT.Hub Command: an ILT "/ra" verb RynthAi forwards ("hub bank", "guardian handin",
    /// "bounty show", "rates reset"). Decides at once whether it is ours (RynthAi answers the rest)
    /// and queues it for the next tick. Any thread.
    /// </summary>
    internal bool RunForwardedCommand(string line)
    {
        string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || !IsIltVerb(words)) return false;
        var parts = new string[words.Length + 1];
        parts[0] = "/ra";
        Array.Copy(words, 0, parts, 1, words.Length);
        _commands.Enqueue(parts);
        return true;
    }

    /// <summary>
    /// The verbs RynthILT answers: the ILT "/ra hub" verbs (not RynthAi's Quests / Pets tabs or its
    /// Remote tab), "/ra guardian", "/ra bounty|bounties|bountys" and "rates".
    /// </summary>
    internal static bool IsIltVerb(string[] words)
    {
        string verb = words[0].ToLowerInvariant();
        switch (verb)
        {
            case "guardian": case "bounty": case "bounties": case "bountys": case "rates":
                return true;
            case "hub":
                if (words.Length < 2) return false;   // "/ra hub" alone toggles RynthAi's Mini Remote
                string sub = words[1].ToLowerInvariant();
                if (sub is "open")
                    return words.Length < 3 || !IltSections.IsUniversalTabName(words[2]);
                if (sub is "tab")
                    // Gear / Games, and the sections that moved out (redirected to RynthOracle / RynthInventory).
                    return words.Length >= 3 && IltSections.TryParse(words[2], out IltSection t)
                           && (IltSections.IsTab(t) || IltSections.MovedCommand(t) != null);
                return true;
            default:
                return false;
        }
    }

    private void RunCommand(string[] parts)
    {
        // RynthAi's forwarded "/ra <verb> ..." and "/ilt hub|guardian|bounty ..." reach the Hub alike.
        bool fromRa = parts.Length > 0 && parts[0].Equals("/ra", StringComparison.OrdinalIgnoreCase);
        string verb = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "help";
        string[] args = parts.Length > 2 ? parts[2..] : Array.Empty<string>();

        if (!fromRa && (verb == "bounty" || verb == "bounties" || verb == "bountys"))
        {
            // "/ilt bounty [list|refresh]": the tracker's own chat commands ("/ra bounty" opens the window).
            if (_bounties == null) ChatLine("[RynthILT] Bounties not ready (not logged in yet).");
            else _bounties.HandleChatCommand(parts);
            return;
        }
        switch (verb)
        {
            case "trace": HandleTrace(args); return;
            case "debug":
                RynthLog.DebugToChat = args.Length > 0 ? args[0].Equals("on", StringComparison.OrdinalIgnoreCase) : !RynthLog.DebugToChat;
                ChatLine($"[RynthILT] Debug-to-chat is {(RynthLog.DebugToChat ? "ON" : "off")} (traced: {RynthLog.TracedList()}).");
                return;
            case "servers":
                verb = "hub";
                args = Prepend("servers", args);
                break;
            case "bounty":
            case "bounties":
            case "bountys":
            case "guardian":
            case "hub":
            case "rates":
                break;
            case "settings":
            case "options":
            {
                // "/ilt settings [show|hide|toggle]": the ILT settings window.
                string mode = args.Length >= 1 ? args[0].ToLowerInvariant() : "toggle";
                _settingsUi.WindowOpen = mode switch { "show" or "on" => true, "hide" or "off" => false, _ => !_settingsUi.WindowOpen };
                return;
            }
            case "fellow":
            case "fellowship":
            case "fship":
            {
                // "/ilt fellow [show|hide|toggle]": the ILT fellowship commands window.
                string mode = args.Length >= 1 ? args[0].ToLowerInvariant() : "toggle";
                _fellowshipUi.WindowOpen = mode switch { "show" or "on" => true, "hide" or "off" => false, _ => !_fellowshipUi.WindowOpen };
                return;
            }
            case "help":
                ChatLine("[RynthILT] /ilt hub ... (the Mini Remote's ILT tabs; same as /ra hub ...), /ilt guardian ..., /ilt servers [list|<names>|default],");
                ChatLine("[RynthILT] /ilt bounty [list|refresh] - the tracked ACECustom bounties (the Bounties window: the ILT button on the RynthCore bar, or /rc bounties),");
                ChatLine("[RynthILT] /ilt settings [show|hide|toggle] - the ILT settings: the world verdict, the ILT world names and a feature summary (force a server: RynthCore Options > Diagnostics; all features: RynthOracle > About),");
                ChatLine("[RynthILT] /ilt fellow [show|hide|toggle] - ILT's /fship commands as buttons (also in the Mini Remote's Fellowship section and RynthOracle's Fellowship tab),");
                ChatLine("[RynthILT] /ilt trace <category|all|list> [on|off], /ilt debug [on|off].");
                return;
            default:
                // "/ilt bank", "/ilt attr run", ...: the Hub's verbs without "hub".
                args = Prepend(verb, args);
                verb = "hub";
                break;
        }
        if (_hub == null)
        {
            ChatLine("[RynthILT] ILT features not ready (log in first).");
            return;
        }
        try
        {
            if (!_hub.HandleCommand(verb, args))
                ChatLine($"[RynthILT] Unknown command: {string.Join(" ", parts)}. /ilt help");
        }
        catch (Exception ex) { RynthLog.Exception(LogCat.IltHub, ex, "command"); }
    }

    private static string[] Prepend(string first, string[] rest)
    {
        var a = new string[rest.Length + 1];
        a[0] = first;
        Array.Copy(rest, 0, a, 1, rest.Length);
        return a;
    }

    /// <summary>"/ilt trace &lt;category|all|list&gt; [on|off]".</summary>
    private void HandleTrace(string[] args)
    {
        string target = args.Length > 0 ? args[0] : "list";
        bool on = args.Length < 2 || !args[1].Equals("off", StringComparison.OrdinalIgnoreCase);
        if (target.Equals("list", StringComparison.OrdinalIgnoreCase) || target.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            ChatLine($"[RynthILT] Traced: {RynthLog.TracedList()}. Categories: {string.Join(", ", Enum.GetNames<LogCat>())}. Log folder: {RynthLog.Directory}");
            return;
        }
        if (target.Equals("all", StringComparison.OrdinalIgnoreCase) || target.Equals("ilt", StringComparison.OrdinalIgnoreCase))
        {
            int n = RynthLog.SetTracingAll(on);
            ChatLine($"[RynthILT] Tracing {(on ? "enabled" : "disabled")} for {n} categories.");
            return;
        }
        if (!RynthLog.TryParseCategory(target, out LogCat cat))
        {
            ChatLine($"[RynthILT] Unknown trace category '{target}'. /ilt trace list");
            return;
        }
        RynthLog.SetTracing(cat, on);
        ChatLine($"[RynthILT] Trace {cat}: {(on ? "ON" : "off")}");
    }

    /// <summary>RynthILTCommand: "prog ..." (the Skills panel's Progression tab) and the Hub verbs, from the engine. Pump thread.</summary>
    internal void RunEngineCommand(string text)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return;
        if (words[0].Equals("prog", StringComparison.OrdinalIgnoreCase))
        {
            if (_hub == null) { ChatLine("[RynthILT] ILT features not ready (log in first)."); return; }
            try { _hub.Progression.HandleRemote(words[1..]); }
            catch (Exception ex) { RynthLog.Exception(LogCat.IltProgression, ex, "prog command"); }
            return;
        }
        if (IsIltVerb(words)) RunForwardedCommand(text);
    }

    /// <summary>RynthPluginApplyRemoteCommand (any thread): "prog" + its arguments, or an ILT verb; run on the next tick.</summary>
    internal void EnqueueRemoteCommand(string action, string value)
    {
        if (_engineCommands.Count < 64) _engineCommands.Enqueue((action + " " + value).Trim());
    }

    private void ChatLine(string text)
    {
        if (Host.HasWriteToChat) Host.WriteToChat(text, 1);
    }

    // ── The ILT Hub ──────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the Hub once the character's name is known (its files are per character). The first
    /// time a character logs in with RynthILT its RynthAi ILT Hub files are copied over (IltMigration).
    /// </summary>
    private void TryStartHub()
    {
        long now = Environment.TickCount64;
        if (now < _nextHubTryMs || _hub != null || _ai == null) return;
        _nextHubTryMs = now + NameRetryMs;

        uint me = Host.GetPlayerId();
        if (me == 0 || !Host.HasGetObjectName || !Host.TryGetObjectName(me, out string charName) || string.IsNullOrWhiteSpace(charName))
            return;
        string world = Host.HasGetWorldName && Host.TryGetWorldName(out string w) && !string.IsNullOrWhiteSpace(w) ? w.Trim() : string.Empty;
        string folder = CharacterFolder(world, charName);
        bool force = false;
        try
        {
            var lines = new List<string>(IltMigration.MigrateShared(_settings, SettingsRoot));
            lines.AddRange(IltMigration.MigrateCharacter(folder, world, charName, _settings, out force));
            if (lines.Count > 0)
            {
                IltSettingsStore.Save(_settings);
                RynthLog.Write(LogCat.IltStore, $"Carried the ILT Hub's saved data over from RynthAi for {charName}: {string.Join(", ", lines)}.");
            }
        }
        catch (Exception ex) { RynthLog.Exception(LogCat.IltStore, ex, "migration"); }

        force |= _forceOnRestart;
        _forceOnRestart = false;
        bool pending = IltMigration.IsPending(_settings, folder);
        _hubWritesBlocked = pending;
        _hubFolder = folder;
        _hubWorld = world;
        _hubChar = charName;
        _nextMigrationRetryMs = now + MigrationRetryMs;
        if (pending)
            RynthLog.Write(LogCat.IltStore, $"{charName}'s Hub data isn't all copied from RynthAi yet: the Hub saves nothing for this character " +
                                            "until it is (read from RynthAi's files where it can; retried every 2 minutes and at each login).");
        try
        {
            _hub = new IltHubController(Host, folder, _settings, _ai, () => _hubWritesBlocked,
                                        pending ? IltMigration.FindLegacyCharFolder(world, charName) : null);
            _hub.OnLoginComplete(charName, force);
        }
        catch (Exception ex)
        {
            _hub = null;
            RynthLog.Exception(LogCat.IltHub, ex, "start");
        }
    }

    /// <summary>
    /// Pump: while this character's copy from RynthAi is unfinished, tries it again every
    /// MigrationRetryMs. When it completes the Hub is stopped (still writing nothing, so the copied
    /// files stay as they are) and started again on them.
    /// </summary>
    private void RetryPendingMigration()
    {
        if (_hub == null || !_hubWritesBlocked) return;
        long now = Environment.TickCount64;
        if (now < _nextMigrationRetryMs) return;
        _nextMigrationRetryMs = now + MigrationRetryMs;
        try
        {
            var lines = IltMigration.MigrateCharacter(_hubFolder, _hubWorld, _hubChar, _settings, out bool force);
            if (lines.Count > 0)
            {
                IltSettingsStore.Save(_settings);
                RynthLog.Write(LogCat.IltStore, $"Retried copying {_hubChar}'s ILT Hub data from RynthAi: {string.Join(", ", lines)}.");
            }
            if (IltMigration.IsPending(_settings, _hubFolder)) return;
            _forceOnRestart = force;
            StopHub();            // _hubWritesBlocked is still true: the logout save writes nothing
            _nextHubTryMs = 0;
            TryStartHub();        // loads the copied files; saves from now on
            RynthLog.Write(LogCat.IltStore, $"{_hubChar}'s ILT Hub data is copied; the Hub restarted on it.");
        }
        catch (Exception ex) { RynthLog.Exception(LogCat.IltStore, ex, "migration retry"); }
    }

    private void StopHub()
    {
        var hub = _hub;
        _hub = null;
        if (hub == null) return;
        try { hub.OnLogout(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.IltHub, ex, "logout"); }
    }

    /// <summary>%APPDATA%\RynthCore\RynthILT\{world}\{character} (the bounty list's folder too).</summary>
    internal static string CharacterFolder(string world, string charName)
        => Path.Combine(SettingsRoot, SafeName(world.Length > 0 ? world : "UnknownWorld"), SafeName(charName));

    // ── Bounties ─────────────────────────────────────────────────────────────

    /// <summary>Starts the tracker once the character's name is known (its save file is per character).</summary>
    private void TryStartBounties()
    {
        long now = Environment.TickCount64;
        if (now < _nextNameTryMs) return;
        _nextNameTryMs = now + NameRetryMs;

        string file = BountyFilePath(out bool named);
        if (!named && now - _loginAtMs < NameWaitMs) return;
        if (!named) Host.Log("[RynthILT] Bounties: no character name; the list won't be saved this session.");
        _bounties = new BountyTracker(SendCommand, ChatLine, Host.Log, file);
    }

    private void SaveBounties()
    {
        try { _bounties?.Save(); }
        catch (Exception ex) { Host.Log($"[RynthILT] Bounties: save failed: {ex.Message}"); }
    }

    /// <summary>The tracker's only way to the server: one chat command (Host.InvokeChatParser).</summary>
    private void SendCommand(string command)
    {
        if (!Host.HasInvokeChatParser || !Host.InvokeChatParser(command))
            throw new InvalidOperationException("the engine's chat parser is not available");
    }

    /// <summary>
    /// %APPDATA%\RynthCore\RynthILT\{world}\{character}\bounties.json, or "" (no save file) while
    /// the character's name is unknown. Both names are reduced to safe folder names.
    /// </summary>
    private string BountyFilePath(out bool named)
    {
        named = false;
        uint me = Host.GetPlayerId();
        if (me == 0 || !Host.HasGetObjectName || !Host.TryGetObjectName(me, out string charName)
            || string.IsNullOrWhiteSpace(charName))
            return string.Empty;
        string world = Host.HasGetWorldName && Host.TryGetWorldName(out string w) && !string.IsNullOrWhiteSpace(w)
            ? w : "UnknownWorld";
        named = true;
        return Path.Combine(SettingsRoot, SafeName(world), SafeName(charName), BountyTracker.FileName);
    }

    /// <summary>RynthILT's settings folder: %APPDATA%\RynthCore\RynthILT.</summary>
    internal static string SettingsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore", "RynthILT");

    /// <summary>A name safe as one folder: invalid characters become '_', no leading/trailing dots or spaces, at most 64 chars.</summary>
    internal static string SafeName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0 || chars[i] == '/' || chars[i] == '\\') chars[i] = '_';
        string s = new string(chars).Trim(' ', '.');
        if (s.Length > 64) s = s.Substring(0, 64).TrimEnd(' ', '.');
        return s.Length == 0 ? "_" : s;
    }
}
