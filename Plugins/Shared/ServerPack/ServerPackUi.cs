// ServerPackUi.cs - A server plugin's "server card".
//
// Drawn in two places: RynthCore's Mini Remote, the shared "Server" section, this server's area
// (the surface "section:server-<id>" through RynthPluginRenderEmbed), and its own window ("/rserver").
// Shows RynthCore's verdict and this character's Auto / On / Off override (read only: it is set in
// RynthCore Options > Diagnostics, 2026-10-13), what is known about the server (address, rule set,
// account limit, botting policy, its features), the commands it adds, its links, and how to set an
// account up for it in the launcher. Render thread only.
using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.ServerPack;

internal sealed class ServerPackUi
{
    private static readonly Vector4 Teal = new(0.30f, 0.85f, 0.80f, 1f);
    private static readonly Vector4 Mute = new(0.60f, 0.62f, 0.66f, 1f);
    private static readonly Vector4 Warn = new(0.95f, 0.55f, 0.45f, 1f);
    private static readonly Vector4 Good = new(0.55f, 0.85f, 0.45f, 1f);

    private readonly ServerPackModel _model;
    private readonly string _pluginLabel;
    private readonly Func<string?> _world;
    private readonly string _windowTitle;
    private string _status = string.Empty;

    /// <summary>The standalone window is open ("/rserver").</summary>
    public bool WindowOpen;

    /// <param name="pluginLabel">"RynthColdeve 0.1.0", for the title and the card.</param>
    /// <param name="world">The world name the client reports now (null when unknown).</param>
    public ServerPackUi(ServerPackModel model, string pluginLabel, Func<string?> world)
    {
        _model = model;
        _pluginLabel = pluginLabel;
        _world = world;
        _windowTitle = model.Info.Name + " server (" + pluginLabel + ")##server_" + model.Info.Id;
    }

    /// <summary>The standalone window, while open.</summary>
    public void RenderWindow()
    {
        if (!WindowOpen) return;
        ImGui.SetNextWindowSize(new Vector2(460, 520), ImGuiCond.FirstUseEver);
        bool open = WindowOpen;
        if (ImGui.Begin(_windowTitle, ref open)) RenderBody();
        ImGui.End();
        WindowOpen = open;
    }

    /// <summary>The card's content, into the current window.</summary>
    public void RenderBody()
    {
        ServerPackInfo info = _model.Info;
        RenderVerdict(info);
        ImGui.Separator();
        RenderFacts(info);
        if (info.Commands.Length > 0)
        {
            ImGui.Separator();
            RenderCommands(info);
        }
        ImGui.Separator();
        RenderLinks(info);
        ImGui.Separator();
        RenderSetup(info);
        if (info.Source.Length > 0)
        {
            ImGui.Spacing();
            SafeImGui.TextDisabled("Server data: " + info.Source);
        }
        if (_status.Length > 0)
        {
            ImGui.Separator();
            SafeImGui.TextWrapped(_status);
        }
    }

    private void RenderVerdict(ServerPackInfo info)
    {
        ServerVerdictView? v = _model.Engine;
        if (v == null)
        {
            bool byName = _model.IsActive(_world());
            SafeImGui.TextColored(byName ? Teal : Mute, byName
                ? info.Name + ": this world (by its name)"
                : info.Name + ": not this world (by its name)");
            ImGui.PushStyleColor(ImGuiCol.Text, Mute);
            ImGui.TextWrapped("Waiting for RynthCore's verdict and the override (RynthCore 2026.10.5.48 or later sends them).");
            ImGui.PopStyleColor();
            return;
        }

        SafeImGui.TextColored(v.Active ? Teal : Mute, v.Active ? info.Name + ": on for this world" : info.Name + ": not this world");
        if (v.Reason.Length > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Mute);
            SafeImGui.TextWrapped(v.Reason);
            ImGui.PopStyleColor();
        }
        // Read only: a server is forced in one place, RynthCore Options > Diagnostics (2026-10-13).
        SafeImGui.TextDisabled("This character: " + ForceText(ServerPackModel.NormalizeForce(v.Force))
                               + "  (change it in RynthCore Options > Diagnostics)");
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip("RynthCore's server selection (Options > Diagnostics) forces a server for this character,\n"
                                 + "or /rc server " + info.Id + " auto|on|off in chat.");
    }

    /// <summary>The override as words: Auto (detected), On (forced on) or Off (forced off).</summary>
    private static string ForceText(string force) => force switch
    {
        "on" => "forced on",
        "off" => "forced off",
        _ => "auto (detected)",
    };

    private static void RenderFacts(ServerPackInfo info)
    {
        if (ImGui.BeginTable("##facts_" + info.Id, 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("Value");
            Row("Address", info.Address.Length > 0 ? info.Address : "(not published)");
            Row("Emulator", info.Emulator);
            Row("Rule set", info.Ruleset);
            if (info.Status.Length > 0) Row("Status", info.Status);
            Row("Account limit", info.AccountLimit.Length > 0 ? info.AccountLimit : "(not published)");
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted("Botting");
            ImGui.TableNextColumn();
            Vector4 color = info.Botting switch
            {
                BottingPolicy.Forbidden => Warn,
                BottingPolicy.Restricted => Warn,
                BottingPolicy.Allowed => Good,
                _ => Mute,
            };
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            SafeImGui.TextWrapped(info.BottingText);
            ImGui.PopStyleColor();
            ImGui.EndTable();
        }

        if (info.Botting == BottingPolicy.Forbidden)
            SafeImGui.TextColored(Warn, "This server forbids bots: don't run RynthAi's combat, navigation or looting here.");

        if (info.Summary.Length > 0)
        {
            ImGui.Spacing();
            SafeImGui.TextWrapped(info.Summary);
        }
        foreach (string fact in info.Facts)
            SafeImGui.BulletText(fact);
    }

    private static void Row(string label, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        SafeImGui.Text(label);
        ImGui.TableNextColumn();
        SafeImGui.TextWrapped(value);
    }

    private void RenderCommands(ServerPackInfo info)
    {
        ImGui.TextUnformatted("Server commands");
        for (int i = 0; i < info.Commands.Length; i++)
        {
            ServerCommand c = info.Commands[i];
            ImGui.PushID(i);
            if (ImGui.SmallButton("Copy")) Copy(c.Command);
            ImGui.PopID();
            ImGui.SameLine();
            SafeImGui.Text(c.Command);
            ImGui.SameLine();
            SafeImGui.TextDisabled("- " + c.What);
        }
    }

    private void RenderLinks(ServerPackInfo info)
    {
        ImGui.TextUnformatted("Links");
        bool any = false;
        any |= Link("Discord", info.Discord);
        any |= Link("Website", info.Website);
        any |= Link("Wiki", info.Wiki);
        if (!any) ImGui.TextDisabled("(none published)");
    }

    private bool Link(string label, string url)
    {
        if (url.Length == 0) return false;
        ImGui.PushID(label);
        if (ImGui.SmallButton("Copy")) Copy(url);
        ImGui.PopID();
        ImGui.SameLine();
        SafeImGui.Text(label + ": " + url);
        return true;
    }

    private static void RenderSetup(ServerPackInfo info)
    {
        ImGui.TextUnformatted("Account setup (RynthCore launcher)");
        foreach (string step in ServerPackModel.LauncherSteps(info))
            SafeImGui.TextWrapped(step);
    }

    private void Copy(string text)
    {
        ImGui.SetClipboardText(text);
        _status = "Copied: " + text;
    }
}
