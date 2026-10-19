using System;
using System.Runtime.InteropServices;
using RynthCore.PluginCore;

namespace RynthCore.Plugin.RynthChat;

public sealed class RynthChatPlugin : RynthPluginBase
{
    internal static readonly IntPtr NamePointer    = Marshal.StringToHGlobalAnsi("RynthChat");
    // Previous release: 0.3.3.
    internal static readonly IntPtr VersionPointer = Marshal.StringToHGlobalAnsi("0.3.4");

    /// <summary>How long after login completes the server's system text still counts as the login burst.</summary>
    internal const long LoginBurstMs = 30_000;

    private readonly ChatBuffer _buffer = new();

    // The login burst: on until a login completes, then for LoginBurstMs more (the welcome,
    // MOTD, house / allegiance notices and the plugins' login probes). On again at logout.
    private volatile bool _loggedIn;
    private long _loginBurstUntilMs;

    public override int Initialize()
    {
        Host.Log("[RynthChat] Plugin initialized.");
        return 0;
    }

    public override void OnLoginComplete()
    {
        _loggedIn = true;
        System.Threading.Interlocked.Exchange(ref _loginBurstUntilMs, Environment.TickCount64 + LoginBurstMs);
        Host.Log("[RynthChat] Login complete.");
    }

    public override void OnLogout()
    {
        _loggedIn = false;
        Host.Log("[RynthChat] Logout.");
    }

    /// <summary>Entering the world, or within LoginBurstMs of the login completing.</summary>
    private bool InLoginBurst =>
        !_loggedIn || Environment.TickCount64 < System.Threading.Interlocked.Read(ref _loginBurstUntilMs);

    public override void OnChatWindowText(string? text, int chatType, ref int eat)
    {
        // Always buffer for RynthChat, even when an earlier plugin ate the line for retail.
        _buffer.Add(text, chatType, InLoginBurst);
    }

    public override void OnChatBarEnter(string? text, ref int eat)
    {
        // Phase 5: intercept + own outgoing chat.
    }

    internal string BuildScrollbackJson(ulong sinceSeq) => _buffer.BuildJson(sinceSeq);

    internal int SetScrollbackCapacity(int lines) => _buffer.SetCapacity(lines);

    /// <summary>The engine reports that a plugin ate the line just dispatched (RynthChatNoteEaten).</summary>
    internal bool NoteEaten(string? text) => _buffer.NoteEaten(text);

    // Called on the game thread from the engine's deferred chat dispatch
    // (Win32Backend posts WM_RYNTHCORE_CHAT so this runs on a fresh, non-reentrant
    // message dispatch rather than inline inside the Enter keystroke handler).
    internal void SendLine(string text)
    {
        if (!Host.HasInvokeChatParser || string.IsNullOrWhiteSpace(text)) return;
        Host.InvokeChatParser(text);
    }
}
