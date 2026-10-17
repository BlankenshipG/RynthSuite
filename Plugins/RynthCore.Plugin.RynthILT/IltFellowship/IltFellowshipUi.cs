// IltFellowshipUi.cs - The "ILT fellowship" view: Infinite Leaftide's /fship commands as buttons.
//
// Drawn in its own window ("/ilt fellow"). Clicks queue the chat command for the plugin tick.
// Render thread only.
using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthILT.IltFellowship;

internal sealed class IltFellowshipUi
{
    private const string WindowTitle = "ILT fellowship (RynthILT)##rynthilt_fellowship";

    private static readonly ServerCommandSet Set = IltFellowshipCommands.Set(active: true);

    private readonly ServerCommandPanel _panel = new("iltfship");
    private readonly Func<string, bool> _send;

    /// <summary>The standalone window is open ("/ilt fellow").</summary>
    public bool WindowOpen;

    /// <param name="send">Queues a chat command for the plugin tick; false when it can't.</param>
    public IltFellowshipUi(Func<string, bool> send) => _send = send;

    /// <summary>The standalone window, while open.</summary>
    public void RenderWindow()
    {
        if (!WindowOpen) return;
        ImGui.SetNextWindowSize(new Vector2(420, 170), ImGuiCond.FirstUseEver);
        bool open = WindowOpen;
        if (ImGui.Begin(WindowTitle, ref open)) RenderBody();
        ImGui.End();
        WindowOpen = open;
    }

    /// <summary>The view's content, into the current window.</summary>
    public void RenderBody()
    {
        SafeImGui.TextDisabled("Infinite Leaftide /fship (up to " + IltFellowshipCommands.MaxFellows + " members)");
        _panel.Render(Set, _send);
    }
}
