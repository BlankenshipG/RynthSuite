// IltSettingsUi.cs - The "ILT settings" view: RynthILT's copy of the Infinite Leaftide settings.
//
// Drawn in two places: RynthCore's Mini Remote, Char section, Infinite Leaftide area (the surface
// "section:ilt-settings" through RynthPluginRenderEmbed), and its own window ("/ilt settings").
// Shows RynthCore's verdict and reason, this character's Auto / On / Off override, the ILT world
// names, and the ILT features RynthILT's Hub read from the server (IltSettingsModel has where each
// comes from). Edits are RynthCore commands, queued for the plugin tick; the next push shows the result.
// Render thread only.
using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthILT.IltSettingsView;

internal sealed class IltSettingsUi
{
    private static readonly Vector4 Teal = new(0.30f, 0.85f, 0.80f, 1f);
    private static readonly Vector4 Mute = new(0.60f, 0.62f, 0.66f, 1f);
    private static readonly Vector4 Warn = new(0.95f, 0.55f, 0.45f, 1f);

    private const string WindowTitle = "ILT settings (RynthILT)##rynthilt_settings";

    private readonly IltSettingsModel _model;
    private readonly Action<string> _runCommand;
    private readonly Func<bool> _refreshFeatures;
    private string _newName = string.Empty;
    private string _status = string.Empty;

    /// <summary>The standalone window is open ("/ilt settings").</summary>
    public bool WindowOpen;

    /// <param name="runCommand">Queues a RynthCore command (run on the plugin tick).</param>
    /// <param name="refreshFeatures">Asks the Hub to read the server's features again; false before it exists.</param>
    public IltSettingsUi(IltSettingsModel model, Action<string> runCommand, Func<bool> refreshFeatures)
    {
        _model = model;
        _runCommand = runCommand;
        _refreshFeatures = refreshFeatures;
    }

    /// <summary>The standalone window, while open.</summary>
    public void RenderWindow()
    {
        if (!WindowOpen) return;
        ImGui.SetNextWindowSize(new Vector2(440, 480), ImGuiCond.FirstUseEver);
        bool open = WindowOpen;
        if (ImGui.Begin(WindowTitle, ref open)) RenderBody();
        ImGui.End();
        WindowOpen = open;
    }

    /// <summary>The view's content, into the current window.</summary>
    public void RenderBody()
    {
        RenderVerdict();
        ImGui.Separator();
        RenderFeatures();
        if (_status.Length > 0)
        {
            ImGui.Separator();
            SafeImGui.TextWrapped(_status);
        }
    }

    private void RenderVerdict()
    {
        IltEngineView? engine = _model.Engine;
        if (engine == null)
        {
            ImGui.TextWrapped("Waiting for RynthCore to send the server verdicts (it needs RynthCore 2026.10.5.47 or later).");
            return;
        }

        IltServerView? s = engine.Server;
        if (s == null)
        {
            ImGui.TextDisabled("RynthCore doesn't list Infinite Leaftide.");
        }
        else
        {
            SafeImGui.TextColored(s.Active ? Teal : Mute, s.Active ? s.Name + ": on for this world" : s.Name + ": not this world");
            if (s.Reason.Length > 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Mute);
                SafeImGui.TextWrapped(s.Reason);
                ImGui.PopStyleColor();
            }

            ImGui.TextUnformatted("This character:");
            ForceRadio("Auto", "auto", s.Force);
            ForceRadio("On", "on", s.Force);
            ForceRadio("Off", "off", s.Force);
        }

        ImGui.Spacing();
        ImGui.TextUnformatted(engine.NamesDefault ? "ILT world names (built in)" : "ILT world names (engine.json)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Worlds whose name matches one of these count as Infinite Leaftide ('*' matches any text).\nThe launch host is checked too. Kept by RynthCore for every character.");
        if (engine.Names.Length == 0) ImGui.TextDisabled("(none: the world name isn't checked)");
        for (int i = 0; i < engine.Names.Length; i++)
        {
            string name = engine.Names[i];
            ImGui.PushID(i);
            if (ImGui.SmallButton("x")) Send(IltSettingsModel.NamesCommand(false, name, out string err), err, "Removing '" + name + "'...");
            if (ImGui.IsItemHovered()) SafeImGui.SetTooltip("Remove '" + name + "'");
            ImGui.PopID();
            ImGui.SameLine();
            SafeImGui.Text(name);
        }
        ImGui.SetNextItemWidth(220);
        bool enter = ImGui.InputTextWithHint("##iltnewname", "world name, * = any text", ref _newName, (uint)IltSettingsModel.MaxPattern,
                                             ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if (ImGui.Button("Add##iltname") || enter)
        {
            string cmd = IltSettingsModel.NamesCommand(true, _newName, out string err);
            if (Send(cmd, err, "Adding '" + _newName.Trim() + "'...")) _newName = string.Empty;
        }
    }

    private void ForceRadio(string label, string mode, string current)
    {
        ImGui.SameLine();
        if (ImGui.RadioButton(label + "##iltforce", current == mode) && current != mode)
            Send(IltSettingsModel.ForceCommand(mode), "Unknown mode.", "Setting this character to " + label + "...");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(mode switch
            {
                "on" => "Treat this world as Infinite Leaftide for this character (/rc server ilt on).",
                "off" => "Never treat this world as Infinite Leaftide for this character (/rc server ilt off).",
                _ => "RynthCore decides from the world name and launch host (/rc server ilt auto).",
            });
    }

    private void RenderFeatures()
    {
        IltFeatureView? f = _model.Features;
        ImGui.TextUnformatted("Server features");
        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh##iltfeatures"))
            _status = _refreshFeatures() ? "Checking the server again..." : "Not ready yet (log in first).";
        if (f == null)
        {
            ImGui.TextWrapped("RynthILT reads which ILT features the server has on once this character is logged in. Not read yet.");
            return;
        }

        string when = f.Checking ? "checking the server..." : f.LastCheck.Length == 0 ? "not checked yet" : "checked " + f.LastCheck;
        SafeImGui.TextDisabled((f.World.Length > 0 ? f.World : "unknown world") + "  -  " + when
                               + (f.Source.Length > 0 ? "  (" + f.Source + ")" : string.Empty));
        if (!f.IltLike) ImGui.TextDisabled("This world isn't treated as Infinite Leaftide, so the server isn't asked.");
        if (f.Features.Length == 0)
        {
            ImGui.TextDisabled("The server hasn't reported any ILT features yet.");
            return;
        }
        if (ImGui.BeginTable("##iltfeatures", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Feature");
            ImGui.TableSetupColumn("State");
            ImGui.TableHeadersRow();
            foreach (var kv in f.Features)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                SafeImGui.Text(kv.Key);
                ImGui.TableNextColumn();
                SafeImGui.TextColored(kv.Value == 1 ? Teal : kv.Value == 0 ? Warn : Mute, IltSettingsModel.StateText(kv.Value));
            }
            ImGui.EndTable();
        }
    }

    /// <summary>Queues <paramref name="command"/>, or shows <paramref name="error"/> when it is empty.</summary>
    private bool Send(string command, string error, string pending)
    {
        if (command.Length == 0)
        {
            _status = error;
            return false;
        }
        _runCommand(command);
        _status = pending;
        return true;
    }
}
