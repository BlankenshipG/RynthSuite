// IltSettingsUi.cs - The "ILT settings" view: RynthILT's copy of the Infinite Leaftide settings.
//
// Drawn in its own window ("/ilt settings"). Shows RynthCore's verdict and reason, this character's
// Auto / On / Off override, the ILT world names (edited through RynthCore), and every ILT feature
// with the state RynthILT's Hub read from the server (IltSettingsModel has where each comes from).
// Edits are RynthCore commands, queued for the plugin tick; the next server-list poll shows the
// result. Render thread only.
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
            ImGui.TextWrapped("Waiting for RynthCore's server list (it needs plugin API v86). Until then RynthILT's own "
                              + "world list and this character's 'Treat this world as ILT' (/ra hub force on|off) decide.");
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

            // Read only: a server is forced in one place, RynthCore Options > Diagnostics (2026-10-13).
            string force = s.Force switch { "on" => "forced on", "off" => "forced off", _ => "auto (detected)" };
            SafeImGui.TextDisabled("This character: " + force + "  (change it in RynthCore Options > Diagnostics)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("RynthCore's server selection (Options > Diagnostics) forces a server for this character,\nor /rc server ilt auto|on|off in chat.");
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("ILT world names");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Worlds whose name matches one of these count as Infinite Leaftide ('*' matches any text).\nThe launch host is checked too. Kept by RynthCore for every character.");
        ImGui.SameLine();
        if (ImGui.SmallButton("List in chat##iltnames"))
        {
            _runCommand(IltSettingsModel.NamesListCommand);
            _status = "RynthCore lists the names in chat (" + IltSettingsModel.NamesListCommand + ").";
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
        ImGui.SameLine();
        if (ImGui.Button("Remove##iltname"))
        {
            string cmd = IltSettingsModel.NamesCommand(false, _newName, out string err);
            if (Send(cmd, err, "Removing '" + _newName.Trim() + "'...")) _newName = string.Empty;
        }
    }

    /// <summary>
    /// The server's ILT features, one collapsible section per group (Character, Items, Pets,
    /// Fellowship, Combat, Activities, then the charms and any other bit chat or a probe set), with
    /// how many are on and each feature's state; the tooltip says where the Rynth tools handle it.
    /// </summary>
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

        var groups = new Dictionary<string, List<(string Label, string Tooltip, int State)>>(StringComparer.Ordinal);
        foreach (var kv in f.Features)
        {
            var (label, group, tooltip) = IltFeatureCatalog.Describe(kv.Key);
            if (!groups.TryGetValue(group, out var rows)) groups[group] = rows = new();
            rows.Add((label, tooltip, kv.Value));
        }
        foreach (string group in FeatureGroups())
        {
            if (!groups.TryGetValue(group, out var rows)) continue;
            int on = 0, off = 0;
            foreach (var r in rows) { if (r.State == 1) on++; else if (r.State == 0) off++; }
            if (!ImGui.CollapsingHeader($"{group}  ({on} of {rows.Count} on)##iltfg_{group}")) continue;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                SafeImGui.TextColored(r.State == 1 ? Teal : r.State == 0 ? Warn : Mute, IltSettingsModel.StateText(r.State));
                ImGui.SameLine(80);
                SafeImGui.Text(r.Label);
                if (ImGui.IsItemHovered()) SafeImGui.SetTooltip(r.Tooltip);
            }
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
