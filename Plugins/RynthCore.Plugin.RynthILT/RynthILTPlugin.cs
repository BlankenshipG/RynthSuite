// RynthILTPlugin.cs - Infinite Leaftide (ACECustom) features, kept out of RynthAi and the engine core.
//
// The engine shows RynthILT's panels only on ILT (RynthCore Compatibility/IltInfo.cs: the bar's ILT
// button, /rc bounties). First feature: the Bounties window's tracker (Bounty/BountyTracker.cs).
// Its reads run only while that window polls RynthILTGetBountiesJson, and the engine polls only on
// ILT, so on any other server this plugin sends nothing.
//
// Threads: OnTick, OnChatWindowText, OnKillNotification, login/logout and the engine's export calls
// run on the plugin pump. OnChatBarEnter runs on AC's main thread (the engine's dispatcher queues it
// there, our own InvokeChatParser too), so it only queues.
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using RynthCore.Plugin.RynthILT.Bounty;
using RynthCore.PluginCore;

namespace RynthCore.Plugin.RynthILT;

public sealed class RynthILTPlugin : RynthPluginBase
{
    internal static readonly IntPtr NamePointer    = Marshal.StringToHGlobalAnsi("RynthILT");
    internal static readonly IntPtr VersionPointer = Marshal.StringToHGlobalAnsi("0.1.0");

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
    }

    // Plugin unload (hot reload, update, client closing): keep the list for the next copy.
    public override void Shutdown() => SaveBounties();

    public override void OnTick()
    {
        if (!_loginComplete) return;
        if (_bounties == null) TryStartBounties();
        DrainTypedBounty();
        while (_commands.TryDequeue(out string[]? parts)) RunCommand(parts);
        _bounties?.Tick();
    }

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
            default:
                ChatLine("[RynthILT] /ilt bounty [list|refresh] - the tracked ACECustom bounties. "
                         + "The Bounties window: the ILT button on the RynthCore bar, or /rc bounties.");
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
