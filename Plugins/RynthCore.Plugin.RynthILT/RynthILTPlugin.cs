// RynthILTPlugin.cs - Infinite Leaftide (ACECustom) features, kept out of RynthAi and the engine core.
//
// The engine shows RynthILT's panels only on ILT (RynthCore Compatibility/IltInfo.cs: the bar's ILT
// button, /rc bounties). First feature: the Bounties window's tracker (Bounty/BountyTracker.cs).
// Its reads run only while that window polls RynthILTGetBountiesJson, and the engine polls only on
// ILT, so on any other server this plugin sends nothing.
//
// Second: a copy of the ILT settings (IltSettings/): RynthCore's verdict, this character's override,
// the ILT world names and the features RynthAi read from the server, in RynthCore's Mini Remote
// (Char section) and its own window ("/ilt settings").
//
// Threads: OnTick, OnChatWindowText, OnKillNotification, login/logout and the engine's export calls
// run on the plugin pump. OnChatBarEnter runs on AC's main thread (the engine's dispatcher queues it
// there, our own InvokeChatParser too), so it only queues. The settings view draws on the render
// thread (OnRender, RynthPluginRenderEmbed) and queues its commands for the tick.
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using ImGuiNET;
using RynthCore.Plugin.RynthILT.Bounty;
using RynthCore.Plugin.RynthILT.IltSettings;
using RynthCore.PluginCore;

namespace RynthCore.Plugin.RynthILT;

public sealed class RynthILTPlugin : RynthPluginBase
{
    internal static readonly IntPtr NamePointer    = Marshal.StringToHGlobalAnsi("RynthILT");
    // Previous release: 0.1.1.
    internal static readonly IntPtr VersionPointer = Marshal.StringToHGlobalAnsi("0.1.2");

    /// <summary>The settings view's surface in RynthCore's Mini Remote sections.</summary>
    internal const string SettingsSurface = "section:ilt-settings";

    /// <summary>RynthAi's name for the plugin-command broker (it reads the server's ILT features).</summary>
    private const string RynthAiPlugin = "RynthAi";

    private readonly IltSettingsModel _settingsModel = new();
    private readonly IltSettingsUi _settingsUi;
    // RynthCore commands from the settings view (render thread), run on the next tick.
    private readonly ConcurrentQueue<string> _engineCommands = new();

    public RynthILTPlugin()
    {
        _settingsUi = new IltSettingsUi(_settingsModel, _engineCommands.Enqueue, RefreshFeatures);
    }

    /// <summary>Wait this long after login for the character's name before tracking without a save file.</summary>
    private const long NameWaitMs = 30_000;
    private const long NameRetryMs = 1_000;

    private bool _loginComplete;
    private long _loginAtMs;
    private long _nextNameTryMs;
    private BountyTracker? _bounties;

    // "/ilt ..." lines from OnChatBarEnter, run on the next tick.
    private readonly ConcurrentQueue<string[]> _commands = new();
    // "/bounty ..." lines from OnChatBarEnter (typed by the player, or our own coming back through
    // the engine's dispatcher), passed to the tracker before the next chat line or tick.
    private readonly ConcurrentQueue<string> _typedBounty = new();

    /// <summary>The Bounties window's tracker (PluginExports); null until login.</summary>
    internal BountyTracker? Bounties => _bounties;

    public override int Initialize()
    {
        Host.Log("[RynthILT] Plugin initialized.");
        return 0;
    }

    // Also called on a freshly (re)loaded plugin while already in game.
    public override void OnLoginComplete()
    {
        SaveBounties();
        _bounties = null;
        _loginComplete = true;
        _loginAtMs = Environment.TickCount64;
        _nextNameTryMs = 0;
        TryStartBounties();
    }

    public override void OnLogout()
    {
        SaveBounties();
        _bounties = null;
        _loginComplete = false;
        while (_commands.TryDequeue(out _)) { }
        while (_typedBounty.TryDequeue(out _)) { }
        while (_engineCommands.TryDequeue(out _)) { }
        _settingsModel.Clear();
    }

    // Plugin unload (hot reload, update, client closing): keep the list for the next copy.
    public override void Shutdown() => SaveBounties();

    public override void OnTick()
    {
        if (!_loginComplete) return;
        if (_bounties == null) TryStartBounties();
        DrainTypedBounty();
        while (_commands.TryDequeue(out string[]? parts)) RunCommand(parts);
        while (_engineCommands.TryDequeue(out string? command))
        {
            if (!Host.HasInvokeChatParser || !Host.InvokeChatParser(command))
                ChatLine("[RynthILT] RynthCore didn't take: " + command);
        }
        _bounties?.Tick();
    }

    // ── ILT settings ─────────────────────────────────────────────────────────

    /// <summary>
    /// RynthPluginApplyRemoteCommand: "servermenu" from RynthCore, "iltfeatures" from RynthAi
    /// (IltSettingsModel has both formats). Any thread: parsed and published, nothing else.
    /// </summary>
    internal void ApplyRemoteCommand(string action, string value)
    {
        if (action.Equals("servermenu", StringComparison.OrdinalIgnoreCase)) _settingsModel.ApplyEngine(value);
        else if (action.Equals("iltfeatures", StringComparison.OrdinalIgnoreCase)) _settingsModel.ApplyFeatures(value);
    }

    /// <summary>Asks RynthAi to read the server's ILT features again ("/ra hub refresh"). Render thread.</summary>
    private bool RefreshFeatures() =>
        Host.HasSendPluginCommand && Host.SendPluginCommand(RynthAiPlugin, "hub", "refresh");

    /// <summary>The standalone settings window. Render thread.</summary>
    public override void OnRender()
    {
        if (!_loginComplete || !_settingsUi.WindowOpen || Host.ImGuiContext == IntPtr.Zero) return;
        IntPtr previous = ImGui.GetCurrentContext();
        ImGui.SetCurrentContext(Host.ImGuiContext);
        try { _settingsUi.RenderWindow(); }
        catch (Exception ex) { Host.Log($"[RynthILT] Settings window failed: {ex.Message}"); _settingsUi.WindowOpen = false; }
        finally { ImGui.SetCurrentContext(previous); }
    }

    /// <summary>
    /// RynthPluginRenderEmbed "section:ilt-settings": the settings view in RynthCore's Mini Remote,
    /// drawn into the engine's current window and context (popped-out windows have their own).
    /// <paramref name="wantHeight"/> is the height it used. False until login. Render thread.
    /// </summary>
    internal bool RenderEmbeddedSettings(out float wantHeight)
    {
        wantHeight = 0;
        if (!_loginComplete) return false;
        IntPtr previous = ImGui.GetCurrentContext();
        IntPtr context = previous != IntPtr.Zero ? previous : Host.ImGuiContext;
        if (context == IntPtr.Zero) return false;
        ImGui.SetCurrentContext(context);
        try
        {
            float startY = ImGui.GetCursorPosY();
            _settingsUi.RenderBody();
            wantHeight = MathF.Max(40f, ImGui.GetCursorPosY() - startY + 4f);
            return true;
        }
        catch (Exception ex)
        {
            Host.Log($"[RynthILT] Settings view failed: {ex.Message}");
            return false;
        }
        finally { ImGui.SetCurrentContext(previous); }
    }

    /// <summary>
    /// RynthPluginGetSectionsJson: the settings view in RynthCore's Char section, Infinite Leaftide
    /// area (shown while RynthCore treats this world as ILT). Null before login.
    /// </summary>
    internal string? BuildSectionsJson() => _loginComplete ? SectionsJson : null;

    private const string SectionsJson =
        "{\"version\":1,\"sections\":[],\"contributions\":[{\"section\":\"char\",\"server\":\"ilt\"," +
        "\"label\":\"ILT settings (RynthILT)\",\"surface\":\"" + SettingsSurface + "\",\"primary\":false," +
        "\"height\":300,\"enabled\":true,\"tooltip\":\"The Infinite Leaftide verdict, this character's override, " +
        "the ILT world names and the server's ILT features (RynthILT; also /ilt settings).\",\"order\":120}]}";

    public override void OnChatWindowText(string? text, int chatType, ref int eat)
    {
        if (_bounties == null || string.IsNullOrEmpty(text)) return;
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

    // ── Commands ─────────────────────────────────────────────────────────────

    private void RunCommand(string[] parts)
    {
        string verb = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "help";
        switch (verb)
        {
            case "bounty":
            case "bounties":
            case "bountys":
                if (_bounties == null) ChatLine("[RynthILT] Bounties not ready (not logged in yet).");
                else _bounties.HandleChatCommand(parts);
                break;
            case "settings":
            case "options":
            {
                // "/ilt settings [show|hide|toggle]": the ILT settings window.
                string mode = parts.Length >= 3 ? parts[2].ToLowerInvariant() : "toggle";
                _settingsUi.WindowOpen = mode switch { "show" or "on" => true, "hide" or "off" => false, _ => !_settingsUi.WindowOpen };
                break;
            }
            default:
                ChatLine("[RynthILT] /ilt bounty [list|refresh] - the tracked ACECustom bounties. "
                         + "The Bounties window: the ILT button on the RynthCore bar, or /rc bounties.");
                ChatLine("[RynthILT] /ilt settings [show|hide|toggle] - the ILT settings: the world verdict, this character's override, "
                         + "the ILT world names and the server's ILT features (also in the Mini Remote's Char section).");
                break;
        }
    }

    private void ChatLine(string text)
    {
        if (Host.HasWriteToChat) Host.WriteToChat(text, 1);
    }

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
