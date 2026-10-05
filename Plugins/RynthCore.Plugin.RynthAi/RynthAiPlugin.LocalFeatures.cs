// RynthAiPlugin.LocalFeatures.cs — SK-local features wired onto the aelrynth 2026.10.5.1 plugin.
//
// The engine draws the main panels. These features still draw their own ImGui windows from
// RynthPluginRender: ILT Hub, floating HUDs, chat translation, item info, and the nav overlay.
// Lifecycle methods on RynthAiPlugin call into this file; they do not replace upstream combat,
// loot, or navigation.
using System;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    private NavBreadcrumbTracker? _navBreadcrumbs;
    private NavOverlayRenderer? _navOverlay;
    private IltHub.IltHubController? _iltHub;
    private Huds.HudController? _huds;
    private Huds.HudIconCache? _hudIcons;
    private Translate.ChatTranslator? _translator;
    private Translate.TranslateUi? _translateUi;
    private LegacyItemInfoUi? _itemInfoUi;
    private CreatureData.UbMobSeedStore? _mobSeedStore;

    // One-time overlay diagnostics (render thread only).
    private string? _overlaySkipReasonLogged;
    private bool _overlayEnteredLogged;

    /// <summary>Daily diagnostics log. Feature code writes through <see cref="RynthLog"/>.</summary>
    private void InitLocalDiagnostics()
    {
        try { RynthLog.Init(Host); }
        catch (Exception ex) { Host.Log($"[RynthAi] diagnostics init failed: {ex.Message}"); }
        try { _mobSeedStore = new CreatureData.UbMobSeedStore(); }
        catch (Exception ex) { Host.Log($"[RynthAi] mob seed store failed: {ex.Message}"); }
    }

    /// <summary>Chat translator survives logout; session language resets on each login.</summary>
    private void EnsureTranslator()
    {
        try
        {
            if (_translator == null)
            {
                _translator = new Translate.ChatTranslator(Host, CurrentCharacterName);
                _translateUi = new Translate.TranslateUi(_translator);
            }
            _translator.ResetSession();
            if (_huds != null) _huds.Translate = _translateUi;
        }
        catch (Exception ex) { RynthLog.Exception(LogCat.Chat, ex, "translator init"); }
    }

    /// <summary>The logged-in character's name, or empty before login.</summary>
    private string CurrentCharacterName()
        => _playerId != 0 && Host.HasGetObjectName && Host.TryGetObjectName(_playerId, out string name) ? name ?? string.Empty : string.Empty;

    /// <summary>Waypoint HUD, labels, guide line, and the breadcrumb recorder.</summary>
    private void CreateNavOverlay()
    {
        if (_dashboard == null) return;
        _navBreadcrumbs = new NavBreadcrumbTracker(Host, _dashboard.Settings, DescribeNavPortal, ChatLine);
        _navOverlay = new NavOverlayRenderer(Host, _dashboard.Settings, _navBreadcrumbs);
        _dashboard.NavBreadcrumbs = _navBreadcrumbs;   // Nav panel: trail -> route, trail stats
    }

    /// <summary>Nav recorder hook: (name, object class) when <paramref name="id"/> is a portal.</summary>
    private (string Name, int ObjectClass)? DescribeNavPortal(uint id)
    {
        WorldObject? wo = _objectCache?[unchecked((int)id)];
        if (wo == null || wo.ObjectClass != AcObjectClass.Portal || string.IsNullOrEmpty(wo.Name)) return null;
        return (wo.Name, (int)AcObjectClass.Portal);
    }

    /// <summary>
    /// ILT Hub and floating HUDs for the loaded character. No-op until the per-character
    /// folder exists, so login and the deferred settings load in OnTick both call this.
    /// </summary>
    private void CreateIltHub()
    {
        if (_dashboard == null || string.IsNullOrEmpty(_dashboard.CharFolder) || _iltHub != null)
            return;

        var dash = _dashboard;
        _iltHub = new IltHub.IltHubController(Host, dash.CharFolder,
            () => _objectCache, () => dash.Settings, () => _questTracker,
            () => dash.SaveSettings());
        _hudIcons ??= new Huds.HudIconCache(Host, () => _raycast?.GeometryLoader?.PortalDat);
        _huds = new Huds.HudController(Host, dash.CharFolder, () => _objectCache, dash, () => _iltHub, _hudIcons,
            () => _combatManager?.activeTargetId ?? 0);
        if (_translateUi != null) _huds.Translate = _translateUi;

        // The Mini Remote is the Hub's main window: "/ra hub [show|hide|toggle]" drives it, and
        // while it is up the Hub keeps its bank / gear data fresh.
        var huds = _huds;
        _iltHub.MiniRemoteCommand = mode => "[ILT Hub] " + huds.HandleCommand("remote", mode);
        _iltHub.MiniRemoteVisible = () => huds.State.ShowMiniRemote;
        EnsureItemInfoUi();
    }

    /// <summary>Item Info window, created once settings exist.</summary>
    private void EnsureItemInfoUi()
    {
        if (_dashboard == null || _itemInfoUi != null) return;
        _itemInfoUi = new LegacyItemInfoUi(_dashboard.Settings);
        _itemInfoUi.SetHooks(DescribeSelectedItemForPreview, PrintSelectedItemInfo, TestItemInfoChatType);
    }

    /// <summary>Pump-thread work for the ported features. Safe before login (each call is null-checked).</summary>
    private void TickLocalFeatures()
    {
        try { RynthLog.Pump(); }
        catch (Exception ex) { Host.Log($"[RynthAi] diagnostics pump: {ex.Message}"); }
        try { _iltHub?.Tick(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.IltHub, ex, "Tick"); }
        try { _huds?.Tick(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.Huds, ex, "Tick"); }
        try { _translator?.Tick(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.Chat, ex, "translator Tick"); }
        try { TickItemInfo(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.General, ex, "item info tick"); }
        try { _navBreadcrumbs?.Tick(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.Navigation, ex, "nav breadcrumbs tick"); }
    }

    /// <summary>Drop per-character feature objects. The translator is disposed only on plugin shutdown.</summary>
    private void TeardownLocalFeatures(bool disposeTranslator)
    {
        try { _navBreadcrumbs?.Shutdown(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.Navigation, ex, "nav recorder shutdown"); }
        _navBreadcrumbs = null;
        if (_dashboard != null) _dashboard.NavBreadcrumbs = null;
        _navOverlay = null;
        try { _iltHub?.OnLogout(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.IltHub, ex, "logout"); }
        _iltHub = null;
        try { _huds?.OnLogout(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.Huds, ex, "logout"); }
        _huds = null;
        _itemInfoUi = null;
        ResetGroundLoot(clearCaches: true);
        if (!disposeTranslator) return;
        try { _translator?.Dispose(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.Chat, ex, "translator dispose"); }
        _translator = null;
        _translateUi = null;
    }

    /// <summary>ILT Hub may eat a server reply; the translator records the line either way.</summary>
    private void OnChatLocalFeatures(string text, int chatType, ref int eat)
    {
        try
        {
            if (_iltHub?.OnChat(text) == true) eat = 1;
        }
        catch (Exception ex) { RynthLog.Exception(LogCat.IltHub, ex, "OnChat"); }
        try { _translator?.OnChatWindowText(text, chatType); }
        catch (Exception ex) { RynthLog.Exception(LogCat.Chat, ex, "translator inbound"); }
    }

    /// <summary>True when the translator swallowed an outbound chat line (it will resend the translation).</summary>
    private bool TryEatOutboundTranslation(string trimmed)
    {
        try
        {
            return _translator != null && _translator.TryInterceptOutbound(trimmed);
        }
        catch (Exception ex)
        {
            RynthLog.Exception(LogCat.Chat, ex, "translator outbound");
            return false;
        }
    }

    /// <summary>ImGui windows the engine panels do not draw.</summary>
    public override void OnRender()
    {
        if (!_initialized || !_loginComplete || Host.ImGuiContext == IntPtr.Zero)
            return;

        IntPtr previousContext = ImGui.GetCurrentContext();
        ImGui.SetCurrentContext(Host.ImGuiContext);
        try
        {
            DetectItemInfoClick();
            _navOverlay?.Render();
            _itemInfoUi?.Render();
            _iltHub?.Render();
            _huds?.Render();
            RenderTranslateWindow();
        }
        catch (Exception ex)
        {
            RynthLog.Exception(LogCat.UI, ex, "OnRender");
        }
        finally
        {
            ImGui.SetCurrentContext(previousContext);
        }
    }

    /// <summary>
    /// Called when the engine's ImGui shell is off. Same local windows as <see cref="OnRender"/>;
    /// the engine panels are absent in that mode, so these windows are the only UI for them.
    /// </summary>
    public void OnRenderOverlay()
    {
        if (!_initialized || !_loginComplete || Host.ImGuiContext == IntPtr.Zero)
        {
            string reason = !_initialized ? "not initialized" : !_loginComplete ? "login not complete" : "no ImGui context";
            if (reason != _overlaySkipReasonLogged)
            {
                _overlaySkipReasonLogged = reason;
                RynthLog.Write(LogCat.UI, $"[Overlay] OnRenderOverlay skipped: {reason}.");
            }
            return;
        }

        if (!_overlayEnteredLogged)
        {
            _overlayEnteredLogged = true;
            RynthLog.Write(LogCat.UI, $"[Overlay] OnRenderOverlay drawing (iltHub={(_iltHub != null ? "ready" : "null")}).");
        }

        IntPtr previousContext = ImGui.GetCurrentContext();
        ImGui.SetCurrentContext(Host.ImGuiContext);
        try
        {
            DetectItemInfoClick();
            int pushedColors = LegacyDashboardRenderer.PushDashboardStyle();
            try { _iltHub?.Render(); }
            finally { ImGui.PopStyleColor(pushedColors); }
            _huds?.Render();
            RenderTranslateWindow();
            _navOverlay?.Render();
            _itemInfoUi?.Render();
        }
        catch (Exception ex)
        {
            RynthLog.Exception(LogCat.UI, ex, "OnRenderOverlay");
        }
        finally
        {
            ImGui.SetCurrentContext(previousContext);
        }
    }

    /// <summary>Chat Translate window in the dashboard colours (no-op while hidden).</summary>
    private void RenderTranslateWindow()
    {
        var ui = _translateUi;
        if (ui == null || _translator?.Settings.ShowWindow != true) return;
        int pushedColors = LegacyDashboardRenderer.PushDashboardStyle();
        try { ui.RenderWindow(); }
        finally { ImGui.PopStyleColor(pushedColors); }
    }

    /// <summary>"/ra huds|itemhud|remote|miniremote [show|hide|toggle]".</summary>
    private void HandleHudCommand(string which, string mode)
    {
        if (_huds == null) { ChatLine("[RynthAi] HUDs not ready (log in first)."); return; }
        ChatLine("[RynthAi] " + _huds.HandleCommand(which, mode));
    }

    /// <summary>Skills panel Progression tab snapshot (RynthPluginGetProgressionJson). Pump thread.</summary>
    internal string BuildProgressionJson() => _iltHub?.BuildProgressionJson() ?? "{\"available\":false}";

    /// <summary>Settings panel Charms Tracking tab snapshot (RynthPluginGetCharmsJson). Pump thread.</summary>
    internal string BuildCharmsJson() => _iltHub?.BuildCharmsJson() ?? "{\"available\":false}";

    /// <summary>"prog ..." remote command from the Skills panel's Progression tab. Pump thread.</summary>
    private void HandleProgressionRemote(string value)
    {
        if (_iltHub == null) { ChatLine("[RynthAi] ILT Hub not ready (log in first)."); return; }
        _iltHub.Progression.HandleRemote(value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>"/ra itemhud add &lt;name&gt;" and the inventory panel's "Add to item count HUD". Pump thread.</summary>
    private void HandleItemHudAdd(string name)
    {
        if (_huds == null) { ChatLine("[RynthAi] HUDs not ready (log in first)."); return; }
        if (string.IsNullOrWhiteSpace(name)) { ChatLine("[RynthAi] Usage: /ra itemhud add <item name>"); return; }
        ChatLine("[RynthAi] " + _huds.AddToItemHudAndShow(name));
    }

    /// <summary>/ra translate ... — forwards to the chat translator.</summary>
    private void HandleTranslateCommand(string[] parts)
    {
        if (_translator == null) { ChatLine("[RynthAi] Chat translator not ready (log in first)."); return; }
        _translator.HandleCommand(parts.Length > 2 ? parts[2..] : Array.Empty<string>(), ChatLine);
    }

    /// <summary>Parses an on/off word; returns null for anything else (caller treats it as status).</summary>
    private static bool? ParseOnOff(string? word) => word?.ToLowerInvariant() switch
    {
        "on" or "true" or "1" or "enable" => true,
        "off" or "false" or "0" or "disable" => false,
        _ => null,
    };

    /// <summary>"/ra debug [on|off|status]" — echoes trace and exception lines to chat.</summary>
    private void HandleDebugCommand(string[] parts)
    {
        string arg = parts.Length > 2 ? parts[2] : "status";
        bool? state = arg.Equals("toggle", StringComparison.OrdinalIgnoreCase) ? !RynthLog.DebugToChat : ParseOnOff(arg);
        if (state.HasValue) RynthLog.DebugToChat = state.Value;
        ChatLine($"[RynthAi] Debug-to-chat is {(RynthLog.DebugToChat ? "ON" : "off")}."
                 + (RynthLog.DebugToChat && RynthLog.TracedCategories().Count == 0 ? " (no categories traced — see /ra trace list)" : ""));
    }

    /// <summary>"/ra trace &lt;cat|all|ilt|list&gt; [on|off|status]".</summary>
    private void HandleTraceCommand(string[] parts)
    {
        string target = parts.Length > 2 ? parts[2] : "status";
        string? action = parts.Length > 3 ? parts[3] : null;

        if (target.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string l in RynthLog.StatusLines()) ChatLine(l);
            return;
        }

        if (target.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            var names = new System.Collections.Generic.List<string>();
            foreach (LogCat c in Enum.GetValues<LogCat>())
                names.Add(RynthLog.IsTracing(c) ? c + "*" : c.ToString());
            ChatLine("[RynthAi] Trace categories (* = on): " + string.Join(", ", names));
            return;
        }

        bool? state = ParseOnOff(action);
        bool isAll = target.Equals("all", StringComparison.OrdinalIgnoreCase);
        bool isIlt = target.Equals("ilt", StringComparison.OrdinalIgnoreCase);
        if (isAll || isIlt)
        {
            if (!state.HasValue) { foreach (string l in RynthLog.StatusLines()) ChatLine(l); return; }
            int n = RynthLog.SetTracingByPrefix(isIlt ? "Ilt" : string.Empty, state.Value);
            ChatLine($"[RynthAi] Tracing {(state.Value ? "enabled" : "disabled")} for {n} {(isIlt ? "ILT Hub " : "")}categories.");
            return;
        }

        if (!RynthLog.TryParseCategory(target, out LogCat cat))
        {
            ChatLine($"[RynthAi] Unknown trace category '{target}'. Use /ra trace list.");
            return;
        }

        if (action != null && action.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            ChatLine($"[RynthAi] Trace {cat}: {(RynthLog.IsTracing(cat) ? "ON" : "off")}");
            return;
        }

        bool enable = state ?? !RynthLog.IsTracing(cat);
        RynthLog.SetTracing(cat, enable);
        ChatLine($"[RynthAi] Trace {cat}: {(enable ? "ON" : "off")} → {RynthLog.TraceDirectory}");
    }

    /// <summary>"/ra logs [open|prune|flush|file on|off]".</summary>
    private void HandleLogsCommand(string[] parts)
    {
        string sub = parts.Length > 2 ? parts[2].ToLowerInvariant() : "status";
        switch (sub)
        {
            case "open":
                try
                {
                    RynthLog.Flush();
                    System.IO.Directory.CreateDirectory(RynthLog.Directory);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{RynthLog.Directory}\"") { UseShellExecute = true });
                }
                catch (Exception ex) { RynthLog.Exception(LogCat.Commands, ex, "logs open"); ChatLine($"[RynthAi] Could not open {RynthLog.Directory}: {ex.Message}"); }
                break;
            case "prune":
                ChatLine($"[RynthAi] Pruned {RynthLog.Prune()} old diagnostics file(s).");
                break;
            case "flush":
                RynthLog.Flush();
                ChatLine("[RynthAi] Diagnostics flushed to disk.");
                break;
            case "file":
                bool? state = ParseOnOff(parts.Length > 3 ? parts[3] : null);
                if (state.HasValue) RynthLog.FileLogAll = state.Value;
                ChatLine($"[RynthAi] Daily RynthAi log file is {(RynthLog.FileLogAll ? "ON" : "off")}.");
                break;
            default:
                foreach (string l in RynthLog.StatusLines()) ChatLine(l);
                break;
        }
    }

    /// <summary>/ra navrec [on|off] — toggles route recording.</summary>
    private void HandleNavRecordCommand(string[] parts)
    {
        if (_dashboard == null) return;
        var s = _dashboard.Settings;
        string arg = parts.Length > 2 ? parts[2].ToLowerInvariant() : "";
        s.IsRecordingNav = arg switch { "on" => true, "off" => false, _ => !s.IsRecordingNav };
        ChatLine($"[RynthAi] Nav recording {(s.IsRecordingNav ? "ON — walk the route; portals are captured" : "OFF")}.");
    }

    /// <summary>/ra navhud [on|off] — shows or hides the navigation HUD window.</summary>
    private void HandleNavHudCommand(string[] parts)
    {
        if (_dashboard == null) return;
        var o = _dashboard.Settings.NavOverlay;
        string arg = parts.Length > 2 ? parts[2].ToLowerInvariant() : "";
        o.ShowHud = arg switch { "on" => true, "off" => false, _ => !o.ShowHud };
        ChatLine($"[RynthAi] Nav HUD {(o.ShowHud ? "shown" : "hidden")}.");
    }

    /// <summary>/ra navtrail [on|off|toggle|clear] — breadcrumb tracking switch, wipe, or a status report.</summary>
    private void HandleNavTrailCommand(string[] parts)
    {
        string arg = parts.Length > 2 ? parts[2].ToLowerInvariant() : "";
        if (arg == "clear")
        {
            _navBreadcrumbs?.RequestClear();
            ChatLine("[RynthAi] Breadcrumb trail cleared.");
            return;
        }
        if (arg is "on" or "off" or "toggle")
        {
            if (_dashboard == null) return;
            var o = _dashboard.Settings.NavOverlay;
            o.TrackBreadcrumbs = arg switch { "on" => true, "off" => false, _ => !o.TrackBreadcrumbs };
            _dashboard.SaveSettings();
            ChatLine($"[RynthAi] Breadcrumb tracking {(o.TrackBreadcrumbs ? "on" : "off")}.");
            return;
        }
        bool tracking = _dashboard?.Settings.NavOverlay.TrackBreadcrumbs ?? false;
        int n = _navBreadcrumbs?.Trail.Length ?? 0;
        double yd = _navBreadcrumbs?.TrailYards ?? 0;
        ChatLine($"[RynthAi] Breadcrumbs {(tracking ? "on" : "off")}: {n} points, {yd:F0} yd. Use /ra navtrail on|off|clear.");
    }

    /// <summary>/ra navoverlay [on|off] — route overlay (rings / lines, waypoint labels, guide line); no argument toggles.</summary>
    private void HandleNavOverlayCommand(string[] parts)
    {
        if (_dashboard == null) return;
        var o = _dashboard.Settings.NavOverlay;
        string arg = parts.Length > 2 ? parts[2].ToLowerInvariant() : "";
        o.ShowRouteMarkers = arg switch { "on" => true, "off" => false, _ => !o.ShowRouteMarkers };
        _dashboard.SaveSettings();
        ChatLine($"[RynthAi] Route overlay {(o.ShowRouteMarkers ? "on" : "off")}.");
    }
}
