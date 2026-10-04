using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.ItemInfo;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

/// <summary>
/// "RynthAi Item Info" window: every option of the Mag-style item info line (/ra iteminfo) with
/// a live preview of the selected item. Edits go straight into
/// <see cref="LegacyUiSettings.ItemInfoSettings"/> and are saved by the dashboard autosave.
/// Opened from Advanced Settings → Display or with /ra iteminfo settings.
/// </summary>
internal sealed class LegacyItemInfoUi
{
    private readonly LegacyUiSettings _settings;

    // Supplied by RynthAiPlugin (it owns the host + object cache).
    private Func<string?>? _describeSelected; // selected item's line (no prefix) or null when nothing is selected
    private Action? _printSelected;           // same path as /ra iteminfo (requests an ID when needed)
    private Action<int>? _testChatType;       // prints a sample line with the given chat type

    // Preview is rebuilt on a timer or when an output option changes, not every frame.
    private const long PreviewRefreshMs = 500;
    private string? _previewText;
    private long _previewAt;
    private MagItemInfoOptions _previewOptions;

    private static readonly Vector4 ExampleColor = new(0.60f, 0.66f, 0.74f, 1f);

    public LegacyItemInfoUi(LegacyUiSettings settings) => _settings = settings;

    public void SetHooks(Func<string?> describeSelected, Action printSelected, Action<int> testChatType)
    {
        _describeSelected = describeSelected;
        _printSelected = printSelected;
        _testChatType = testChatType;
    }

    public void Render()
    {
        // Re-read every frame: loading a character profile replaces the settings object.
        MagItemInfoSettings s = _settings.ItemInfoSettings;
        if (!s.ShowWindow) return;

        ImGui.SetNextWindowSize(new Vector2(560, 600), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("RynthAi Item Info##ItemInfoSettings", ref s.ShowWindow))
        {
            RenderPreview(s);
            RenderWhen(s);
            RenderFields(s);
            RenderSpellsAndRatings(s);
            RenderChat(s);

            ImGui.Spacing();
            ImGui.Separator();
            if (ImGui.Button("Reset all to defaults##ii"))
            {
                s.ResetToDefaults();
                _previewAt = 0;
            }
            ImGui.SameLine();
            if (ImGui.Button("Close##ii")) s.ShowWindow = false;
        }
        ImGui.End();
    }

    // ── Preview ──────────────────────────────────────────────────────────────

    private void RenderPreview(MagItemInfoSettings s)
    {
        if (!ImGui.CollapsingHeader("Preview (selected item)##ii", ImGuiTreeNodeFlags.DefaultOpen)) return;

        var options = MagItemInfoOptions.From(s);
        long now = Environment.TickCount64;
        if (_describeSelected != null && (now - _previewAt >= PreviewRefreshMs || options != _previewOptions))
        {
            try { _previewText = _describeSelected(); }
            catch (Exception ex) { _previewText = "Preview failed: " + ex.Message; }
            _previewAt = now;
            _previewOptions = options;
        }

        ImGui.BeginChild("iiPreview", new Vector2(0, 70), ImGuiChildFlags.Borders);
        if (string.IsNullOrEmpty(_previewText))
            ImGui.TextDisabled("Select an item to preview its info line.");
        else
            ImGui.TextWrapped(s.Prefix + _previewText);
        ImGui.EndChild();

        if (ImGui.Button("Print to chat##iiPrint")) _printSelected?.Invoke();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Same as /ra iteminfo: IDs the selected item first when needed.");
        ImGui.SameLine();
        if (ImGui.Button("Refresh##iiRefresh")) _previewAt = 0;
    }

    // ── When to print ────────────────────────────────────────────────────────

    private static void RenderWhen(MagItemInfoSettings s)
    {
        if (!ImGui.CollapsingHeader("When to print##ii", ImGuiTreeNodeFlags.DefaultOpen)) return;

        ImGui.Checkbox("Describe items when selected##iiOnSel", ref s.OnSelect);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Print the line every time you click an item.\n/ra iteminfo always works, whatever is set here.");

        ImGui.Indent();
        ImGui.BeginDisabled(!s.OnSelect);
        ImGui.Checkbox("Weapons##iiSelW", ref s.OnSelectWeapons);      ImGui.SameLine();
        ImGui.Checkbox("Armor & clothing##iiSelA", ref s.OnSelectArmor); ImGui.SameLine();
        ImGui.Checkbox("Jewelry##iiSelJ", ref s.OnSelectJewelry);      ImGui.SameLine();
        ImGui.Checkbox("Other items##iiSelO", ref s.OnSelectOther);
        ImGui.EndDisabled();
        ImGui.Unindent();

        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("ID wait (seconds)##iiTimeout", ref s.IdTimeoutSec, 0.5f, 15f, "%.1f s"))
            s.Sanitize();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How long to wait for an unidentified item's appraisal\nbefore printing what is already known.");
    }

    // ── Field toggles ────────────────────────────────────────────────────────

    private void RenderFields(MagItemInfoSettings s)
    {
        if (!ImGui.CollapsingHeader("Fields##ii", ImGuiTreeNodeFlags.DefaultOpen)) return;

        if (ImGui.SmallButton("All on##iiFields"))  s.HiddenFields = 0;
        ImGui.SameLine();
        if (ImGui.SmallButton("All off##iiFields"))
        {
            foreach (var f in MagItemInfoCatalog.Fields) s.SetHidden(f.Field, true);
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(the item name is always printed)");

        if (ImGui.BeginTable("iiFieldTable", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Field", ImGuiTableColumnFlags.WidthStretch, 0.45f);
            ImGui.TableSetupColumn("Example", ImGuiTableColumnFlags.WidthStretch, 0.55f);
            foreach (var f in MagItemInfoCatalog.Fields)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                bool shown = !s.IsHidden(f.Field);
                if (ImGui.Checkbox($"{f.Label}##iiF{(int)f.Field}", ref shown))
                    s.SetHidden(f.Field, !shown);
                ImGui.TableNextColumn();
                ImGui.TextColored(ExampleColor, f.Example);
            }
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Checkbox("Value & burden##iiValBu", ref s.ShowValueAndBurden);
            ImGui.TableNextColumn();
            ImGui.TextColored(ExampleColor, "Value 12,500, BU 220");
            ImGui.EndTable();
        }
    }

    // ── Spells + per-rating toggles ──────────────────────────────────────────

    private static void RenderSpellsAndRatings(MagItemInfoSettings s)
    {
        if (!ImGui.CollapsingHeader("Spells & ratings##ii")) return;

        ImGui.BeginDisabled(s.IsHidden(MagItemInfoField.Spells));
        ImGui.Text("Spell list:");
        ImGui.SameLine();
        ImGui.RadioButton("Mag filter##iiSpMag", ref s.SpellMode, MagItemInfoSettings.SpellModeMag);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Cantrips, level 7+ weapon auras, Augmented spells,\nand Impen/banes only on unenchantable gear.");
        ImGui.SameLine();
        ImGui.RadioButton("All spells##iiSpAll", ref s.SpellMode, MagItemInfoSettings.SpellModeAll);
        if (s.SpellMode == MagItemInfoSettings.SpellModeAll)
        {
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderInt("Max spells listed##iiSpMax", ref s.MaxListedSpells, 1, 64))
                s.Sanitize();
        }
        ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.BeginDisabled(s.IsHidden(MagItemInfoField.Ratings));
        ImGui.Text("Ratings shown:");
        ImGui.SameLine();
        if (ImGui.SmallButton("All##iiRat")) s.HiddenRatings = 0;
        ImGui.SameLine();
        if (ImGui.SmallButton("None##iiRat")) s.HiddenRatings = (1 << MagItemInfoCatalog.Ratings.Length) - 1;
        if (ImGui.BeginTable("iiRatingTable", 3))
        {
            var ratings = MagItemInfoCatalog.Ratings;
            for (int i = 0; i < ratings.Length; i++)
            {
                ImGui.TableNextColumn();
                bool shown = !s.IsRatingHidden(i);
                if (ImGui.Checkbox($"{ratings[i].Tag} - {ratings[i].Name}##iiR{i}", ref shown))
                    s.SetRatingHidden(i, !shown);
            }
            ImGui.EndTable();
        }
        ImGui.EndDisabled();
    }

    // ── Chat output ──────────────────────────────────────────────────────────

    private void RenderChat(MagItemInfoSettings s)
    {
        if (!ImGui.CollapsingHeader("Chat output##ii")) return;

        string current = $"{s.ChatType}";
        foreach (var (type, label) in MagItemInfoCatalog.ChatTypes)
            if (type == s.ChatType) { current = label; break; }

        ImGui.SetNextItemWidth(220);
        if (ImGui.BeginCombo("Chat type / colour##iiChat", current))
        {
            foreach (var (type, label) in MagItemInfoCatalog.ChatTypes)
                if (ImGui.Selectable(label, type == s.ChatType)) s.ChatType = type;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("Test##iiChatTest")) _testChatType?.Invoke(s.ChatType);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Print a sample line with this chat type to see its colour.");

        ImGui.SetNextItemWidth(220);
        ImGui.InputText("Line prefix##iiPrefix", ref s.Prefix, 32);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Text put in front of every item info line (may be empty).");
    }
}
