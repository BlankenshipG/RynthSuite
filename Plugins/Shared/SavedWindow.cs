// ============================================================================
//  RynthSuite - Plugins/Shared/SavedWindow.cs
//  Keeps a plugin's own floating ImGui window where the player left it, per
//  character, through the engine (plugin API v91, Get/SetWindowPlacement).
//
//  Plugin windows saved their spot only in imgui.ini, which every running client
//  shares and rewrites whole, so a window moved in one client came back where
//  another client's next save had it (the Item HUD's spot, the Mini Remote's size;
//  2026-10-26). The engine keeps one file per character instead
//  (%LocalAppData%\RynthCore\window_state.<server>.<character>.txt).
//
//  Use (linked source, like SafeImGui): the plugin sets SavedWindow.Host once at
//  start (each plugin has its own copy of the static), then one per window:
//      private readonly SavedWindow _spot = new("RynthAi.ItemHud", trackSize: false);
//      _spot.Apply();                      // immediately before ImGui.Begin
//      if (ImGui.Begin(...)) { _spot.Track(); ... }   // after Begin, before End
//      _spot.Reset();                      // when the window is hidden (optional)
//  trackSize false: a window that sizes itself (AlwaysAutoResize); only its spot is kept.
//  An engine older than v91 has no store: the window keeps imgui.ini's behaviour.
//  Render thread only.
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.Shared;

internal sealed class SavedWindow
{
    private const long RetryMs = 1000;

    /// <summary>This plugin's host; set once at start. Null: windows keep imgui.ini's behaviour.</summary>
    public static RynthCoreHost? Host { get; set; }

    private readonly string _key;
    private readonly bool _trackSize;
    private bool _applied;          // the saved spot was put on the window (or the player placed it since)
    private long _nextTryMs;
    private bool _seen;
    private bool _editing;
    private Vector2 _lastPos, _lastSize;

    /// <param name="key">The window's name in the store: "Plugin.Window", unique across plugins, up to 96 characters, no '='.</param>
    /// <param name="trackSize">Keep the size too (false for a window that sizes itself).</param>
    public SavedWindow(string key, bool trackSize = true)
    {
        _key = key;
        _trackSize = trackSize;
    }

    /// <summary>
    /// Immediately before Begin (nothing else may Begin in between): puts the window where this character left it. Looks it up once (again each
    /// second while no character is logged in yet, until the player moves the window themselves).
    /// </summary>
    public void Apply()
    {
        if (_applied || Host is not RynthCoreHost host || !host.HasWindowPlacements) return;
        long now = Environment.TickCount64;
        if (now < _nextTryMs) return;
        _nextTryMs = now + RetryMs;
        try
        {
            if (!host.TryGetWindowPlacement(_key, out Vector2 pos, out Vector2 size)) return;
            _applied = true;
            ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
            if (_trackSize && size.X > 0 && size.Y > 0) ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        }
        catch { _applied = true; }
    }

    /// <summary>
    /// Between Begin and End: once the player has finished moving (or resizing) the window - the mouse
    /// went up after it changed while it was focused and held - saves the spot for this character. A
    /// move the code makes (first-use centring, auto-resize, an off-screen rescue) is never saved.
    /// </summary>
    public void Track()
    {
        Vector2 pos = ImGui.GetWindowPos();
        Vector2 size = ImGui.GetWindowSize();
        bool changed = pos != _lastPos || (_trackSize && size != _lastSize);
        bool first = !_seen;
        _seen = true;
        _lastPos = pos;
        _lastSize = size;
        if (first) return;
        bool down = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        if (down && changed && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)) _editing = true;
        if (down || !_editing) return;
        _editing = false;
        _applied = true;   // the player's own spot from now on, even if the lookup never found one
        if (Host is not RynthCoreHost host || !host.HasWindowPlacements) return;
        try { host.SetWindowPlacement(_key, pos, _trackSize ? size : Vector2.Zero); }
        catch { }
    }

    /// <summary>The window was hidden: the next show doesn't count its first frame as a player move.</summary>
    public void Reset()
    {
        _seen = false;
        _editing = false;
    }
}
