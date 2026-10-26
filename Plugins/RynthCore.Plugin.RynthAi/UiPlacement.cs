// UiPlacement.cs - Shared first-use placement and off-screen rescue for RynthAi's floating
// ImGui windows (render thread only).
//
// Windows with no imgui.ini entry open near the middle of the game view instead of ImGui's
// default (60,60), where a popped-out panel above the game frame would hide them. A window
// whose saved position leaves it (almost) off the game view is moved back to the middle.
using System.Numerics;
using ImGuiNET;

namespace RynthCore.Plugin.RynthAi;

internal static class UiPlacement
{
    /// <summary>Pixels of a window that must stay inside the game view to count as on-screen.</summary>
    private const float MinVisible = 40f;

    private static readonly Vector2 CenterPivot = new(0.5f, 0.5f);

    /// <summary>
    /// Before Begin: if the window has no saved position, centre it in the game view, shifted by
    /// <paramref name="offset"/> so windows opened together don't stack exactly.
    /// </summary>
    public static void CenterFirstUse(Vector2 offset = default)
    {
        Vector2 display = ImGui.GetIO().DisplaySize;
        if (display.X <= 1 || display.Y <= 1) return; // no display size yet: ImGui's default spot
        ImGui.SetNextWindowPos(display * 0.5f + offset, ImGuiCond.FirstUseEver, CenterPivot);
    }

    /// <summary>
    /// Between Begin and End, once per show (<paramref name="checkedThisShow"/> is cleared by the
    /// caller while the window is hidden): moves the window to the middle of the game view if
    /// less than 40 px of it is on screen. Returns true when it moved the window.
    /// </summary>
    public static bool RescueOncePerShow(ref bool checkedThisShow)
    {
        if (checkedThisShow) return false;
        checkedThisShow = true;

        Vector2 pos = ImGui.GetWindowPos();
        Vector2 size = ImGui.GetWindowSize();
        Vector2 display = ImGui.GetIO().DisplaySize;
        if (display.X <= MinVisible * 2 || display.Y <= MinVisible * 2) return false;
        if (IsOnScreen(pos, size, display)) return false;

        ImGui.SetWindowPos(Vector2.Max(Vector2.Zero, (display - size) * 0.5f));
        return true;
    }

    /// <summary>True when at least 40 px of the window overlaps the game view on each axis.</summary>
    public static bool IsOnScreen(Vector2 pos, Vector2 size, Vector2 display) =>
        pos.X + size.X >= MinVisible && pos.Y + size.Y >= MinVisible
        && pos.X <= display.X - MinVisible && pos.Y <= display.Y - MinVisible;
}

/// <summary>
/// A floating window's spot (and size) as the player left it, kept in the plugin's own per-character
/// state instead of imgui.ini (2026-10-26). imgui.ini is one file all running clients share, and each
/// rewrites the whole file from what it loaded at start, so a move in one client was undone by
/// another client's next save. Render thread only; one instance per window.
/// </summary>
internal sealed class SavedPlacement
{
    private bool _seen;
    private bool _editing;
    private Vector2 _lastPos, _lastSize;

    /// <summary>Before Begin: puts the window where it was saved, each time it appears.</summary>
    public static void Apply(bool placed, Vector2 pos, Vector2 size)
    {
        if (!placed) return;
        ImGui.SetNextWindowPos(pos, ImGuiCond.Appearing);
        if (size.X > 0 && size.Y > 0) ImGui.SetNextWindowSize(size, ImGuiCond.Appearing);
    }

    /// <summary>The window is hidden: the next show starts fresh.</summary>
    public void Reset() { _seen = false; _editing = false; }

    /// <summary>
    /// Between Begin and End: true once the player has finished moving (or, with
    /// <paramref name="trackSize"/>, resizing) the window: the mouse went up after the window changed
    /// while it was focused and held. A move the code makes (first-use centring, off-screen rescue,
    /// auto-resize) is never reported.
    /// </summary>
    public bool PlayerChanged(bool trackSize, out Vector2 pos, out Vector2 size)
    {
        pos = ImGui.GetWindowPos();
        size = ImGui.GetWindowSize();
        bool changed = pos != _lastPos || (trackSize && size != _lastSize);
        bool first = !_seen;
        _seen = true;
        _lastPos = pos;
        _lastSize = size;
        if (first) return false;
        bool down = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        if (down && changed && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)) _editing = true;
        if (!down && _editing)
        {
            _editing = false;
            return true;
        }
        return false;
    }
}
