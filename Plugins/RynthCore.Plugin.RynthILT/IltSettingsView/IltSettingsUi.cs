// IltSettingsUi.cs - The "ILT settings" view: RynthILT's copy of the Infinite Leaftide settings.
//
// Drawn in two places: RynthCore's Mini Remote, Char section, Infinite Leaftide area (the surface
// "section:ilt-settings" through RynthPluginRenderEmbed), and its own window ("/ilt settings").
// Shows RynthCore's verdict and reason, this character's Auto / On / Off override, the ILT world
// names, and the ILT features RynthILT's Hub read from the server (IltSettingsModel has where each
// comes from). Edits are RynthCore commands, queued for the plugin tick; the next push shows the result.
// Render thread only.
using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthILT.Hub;
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
        if (f.IltLike)
            SafeImGui.TextWrapped("The ILT profile: what Infinite Leaftide runs beyond retail, from ACECustom. Detected on "
                                  + "this client only: the login check and the server's replies in chat can turn a feature "
                                  + "on or off; hover a row for where its state comes from.");

        // One collapsible section per group (Character, Items, Pets, Fellowship, Combat,
        // Activities), then the charms and any other bit chat or a probe set.
        var byGroup = new Dictionary<string, List<(string Label, string Tooltip, int State, string Text)>>(StringComparer.Ordinal);
        foreach (var kv in f.Features)
        {
            var (label, group, tooltip) = IltFeatureCatalog.Describe(kv.Key);
            string text = IltFeatureCatalog.StateText((IltTri)Math.Clamp(kv.Value, -1, 1));
            if (!byGroup.TryGetValue(group, out var list)) byGroup[group] = list = new();
            list.Add((label, tooltip, kv.Value, text));
        }
        foreach (string group in FeatureGroups())
        {
            if (!byGroup.TryGetValue(group, out var rows)) continue;
            int on = 0;
            foreach (var r in rows) if (r.State == 1) on++;
            if (!ImGui.CollapsingHeader($"{group} ({on} of {rows.Count} on)##iltfg_{group}",
                    group == IltFeatureCatalog.GroupOther ? ImGuiTreeNodeFlags.None : ImGuiTreeNodeFlags.DefaultOpen))
                continue;
            if (!ImGui.BeginTable("##iltft_" + group, 2,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
                continue;
            ImGui.TableSetupColumn("Feature", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 70f);
            foreach (var (label, tooltip, state, text) in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                SafeImGui.Text(label);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
                ImGui.TableNextColumn();
                SafeImGui.TextColored(state == 1 ? Teal : state == 0 ? Warn : Mute, text);
            }
            ImGui.EndTable();
        }
    }

    /// <summary>The feature sections in display order: the catalog's groups, then Charms and Other.</summary>
    private static IEnumerable<string> FeatureGroups()
    {
        foreach (string g in IltFeatureCatalog.Groups) yield return g;
        yield return IltFeatureCatalog.GroupCharms;
        yield return IltFeatureCatalog.GroupOther;
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
