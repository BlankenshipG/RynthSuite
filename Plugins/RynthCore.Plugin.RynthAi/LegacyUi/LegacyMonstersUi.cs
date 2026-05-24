using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.Combat;
using RynthCore.Plugin.RynthAi;
using RynthCore.PluginSdk;
using RynthCore.Plugin.RynthAi.CreatureData;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

internal sealed class LegacyMonstersUi
{
    private readonly LegacyUiSettings _settings;
    private readonly RynthCoreHost _host;
    private readonly Action _onMonstersChanged;
    private readonly Action _onLaunchExternalEditor;
    private readonly Func<(uint Id, string Name)?> _getCurrentTarget;
    private WorldObjectCache? _worldFilter;

    /// <summary>Set by the dashboard so we can show captured stats per monster row.</summary>
    public Func<string, CreatureProfile?>? CreatureLookup { get; set; }

    private string _newMonsterName = string.Empty;
    private string _newMonsterCategory = string.Empty;
    private readonly Dictionary<string, bool> _categoryExpanded = new(StringComparer.OrdinalIgnoreCase);

    // Expression popup state
    private int _exprEditIndex = -1;
    private string _exprEditBuffer = string.Empty;

    // Category rename popup state
    private string _catRenameFrom = string.Empty;
    private string _catRenameBuffer = string.Empty;

    private static readonly string[] Elements =
        { "Slash", "Pierce", "Bludgeon", "Fire", "Cold", "Lightning", "Acid", "Nether" };

    private static readonly uint OnColor  = ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 1.0f, 0.2f, 1.0f));
    private static readonly uint OffColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.3f, 0.3f, 1.0f));
    private static readonly uint CatHeaderBg = ImGui.ColorConvertFloat4ToU32(new Vector4(0.10f, 0.18f, 0.26f, 1.0f));

    // Flat, dark frames for combo/int cells (less “pill / neon” noise than the default).
    private static readonly Vector4 FrameBgUi       = new(0.11f, 0.11f, 0.15f, 1.0f);
    private static readonly Vector4 FrameBgUiHover  = new(0.16f, 0.16f, 0.22f, 1.0f);
    private static readonly Vector4 FrameBgUiActive = new(0.18f, 0.18f, 0.26f, 1.0f);

    public LegacyMonstersUi(
        LegacyUiSettings settings,
        RynthCoreHost host,
        Action onMonstersChanged,
        Action onLaunchExternalEditor,
        Func<(uint Id, string Name)?> getCurrentTarget)
    {
        _settings = settings;
        _host = host;
        _onMonstersChanged = onMonstersChanged;
        _onLaunchExternalEditor = onLaunchExternalEditor;
        _getCurrentTarget = getCurrentTarget;
    }

    public void SetWorldFilter(WorldObjectCache cache) => _worldFilter = cache;

    public void Render()
    {
        if (!DashWindows.ShowMonsters) return;

        ImGui.SetNextWindowSize(new Vector2(1200, 580), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(960, 400), new Vector2(2000, 2000));
        if (!ImGui.Begin("Monsters##RynthAiMonsters", ref DashWindows.ShowMonsters))
        {
            ImGui.End();
            return;
        }

        if (ImGui.Button("External Editor", new Vector2(130, 26)))
            _onLaunchExternalEditor();
        ImGui.SameLine();
        if (ImGui.SmallButton("?##mcolHelpOpen"))
            ImGui.OpenPopup("mcolhelp");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("What the toggle columns and buttons mean (full names).");
        ImGui.SameLine();
        ImGui.TextDisabled("(opens standalone Avalonia editor — full editor window)");

        if (ImGui.BeginPopup("mcolhelp", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("Toggle columns (left to right) — click the dot; green = on.");
            ImGui.Separator();
            ImGui.Text("Curses / debuff:");
            ImGui.BulletText("Fst — Fester; Brd — Broadside; Grv — Gravity (Vulner. Other).");
            ImGui.BulletText("Imp — Imperil; Yld — Yield; Vul — Vulnerability (element).");
            ImGui.Spacing();
            ImGui.Text("Attack (War/Void):");
            ImGui.BulletText("Arc — Arc (also used as melee/missile attack).");
            ImGui.BulletText("Blt — Bolt  (if none of Arc / Blt / Rng / Stk, no attack).");
            ImGui.BulletText("Rng — Ring; Stk — Streak.");
            ImGui.Separator();
            ImGui.BulletText("Expr (E) — vTank-style match expression; optional; overrides name-only if set.");
            ImGui.BulletText("Del — remove row (the Default row cannot be deleted).");
            ImGui.EndPopup();
        }

        ImGui.TextDisabled("Curses: Fst Brd Grv Imp Yld Vul  ·  Attacks: Arc Blt Rng Stk  ·  Drag column edges to resize.");
        ImGui.Spacing();
        ImGui.Separator();

        DrawTable();

        ImGui.Separator();
        ImGui.TextDisabled("Add a row (name must be unique; category groups rows in the list).");
        DrawAddRow();

        ImGui.End();
    }

    private void DrawTable()
    {
        // 20 columns: 10 toggles + Name + P + DmgType + ExVuln + Weapon + Missile ammo + Offhand + PetDmg + [E] + Del
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(6, 4));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(5, 3));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6, 4));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3.0f);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, FrameBgUi);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, FrameBgUiHover);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, FrameBgUiActive);

        if (!ImGui.BeginTable(
                "MonstersGrid", 20,
                ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.BordersV | ImGuiTableFlags.BordersOuterH |
                ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable,
                new Vector2(0, 0f)))
        {
            ImGui.PopStyleColor(3);
            ImGui.PopStyleVar(4);
            return;
        }

        // Wider, 3-letter headers; stretch weights give Name/Weapon/Offhand room to breathe.
        // ImGui requires WidthFixed/WidthStretch when init_width_or_weight is set; NoResize alone triggers IM_ASSERT (imgui_tables.cpp).
        const ImGuiTableColumnFlags narrow = ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize;
        const float t = 24;
        ImGui.TableSetupColumn("Fst",      narrow, t);
        ImGui.TableSetupColumn("Brd",      narrow, t);
        ImGui.TableSetupColumn("Grv",      narrow, t);
        ImGui.TableSetupColumn("Imp",      narrow, t);
        ImGui.TableSetupColumn("Yld",      narrow, t);
        ImGui.TableSetupColumn("Vul",      narrow, t);
        ImGui.TableSetupColumn("Arc",      narrow, t);
        ImGui.TableSetupColumn("Blt",      narrow, t);
        ImGui.TableSetupColumn("Rng",      narrow, t);
        ImGui.TableSetupColumn("Stk",      narrow, t);
        ImGui.TableSetupColumn("Name",     ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Prio",     ImGuiTableColumnFlags.WidthFixed, 40f);
        ImGui.TableSetupColumn("Damage",   ImGuiTableColumnFlags.WidthFixed, 92f);
        ImGui.TableSetupColumn("ExVuln",   ImGuiTableColumnFlags.WidthFixed, 88f);
        ImGui.TableSetupColumn("Weapon",   ImGuiTableColumnFlags.WidthStretch, 1.0f);
        ImGui.TableSetupColumn("Ammo",     ImGuiTableColumnFlags.WidthFixed, 152f);
        ImGui.TableSetupColumn("Offhand",  ImGuiTableColumnFlags.WidthStretch, 0.9f);
        ImGui.TableSetupColumn("Pet",      ImGuiTableColumnFlags.WidthFixed, 78f);
        ImGui.TableSetupColumn("Expr",     ImGuiTableColumnFlags.WidthFixed, 34f);
        ImGui.TableSetupColumn("Del",      narrow, 32f);
        ImGui.TableHeadersRow();

        int deleteIndex = -1;
        string? currentCat = null;
        bool currentCatOpen = true;

        for (int i = 0; i < _settings.MonsterRules.Count; i++)
        {
            var rule = _settings.MonsterRules[i];
            bool isDefault = rule.Name.Equals("Default", StringComparison.OrdinalIgnoreCase);
            string cat = string.IsNullOrEmpty(rule.Category) ? "Default" : rule.Category;

            // Category header row
            if (!string.Equals(cat, currentCat, StringComparison.OrdinalIgnoreCase))
            {
                currentCat = cat;
                if (!_categoryExpanded.ContainsKey(cat))
                    _categoryExpanded[cat] = true;
                currentCatOpen = _categoryExpanded[cat];

                ImGui.TableNextRow();
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, CatHeaderBg);
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, CatHeaderBg);

                ImGui.TableNextColumn(); // col 0: expand toggle
                string arrow = currentCatOpen ? "v" : ">";
                ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1,1,1,0.1f));
                if (ImGui.SmallButton($"{arrow}##cat_{cat}"))
                {
                    currentCatOpen = !currentCatOpen;
                    _categoryExpanded[cat] = currentCatOpen;
                }
                ImGui.PopStyleColor(2);

                // Name col: category label + right-click rename
                ImGui.TableSetColumnIndex(10);
                ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), cat);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Right-click to rename category");
                if (ImGui.BeginPopupContextItem($"catctx_{cat}"))
                {
                    ImGui.Text("Rename category:");
                    if (_catRenameFrom != cat)
                    {
                        _catRenameFrom = cat;
                        _catRenameBuffer = cat;
                    }
                    ImGui.SetNextItemWidth(180);
                    ImGui.InputText("##catren", ref _catRenameBuffer, 64);
                    if (ImGui.Button("Apply") && !string.IsNullOrWhiteSpace(_catRenameBuffer))
                    {
                        string newName = _catRenameBuffer.Trim();
                        foreach (var r in _settings.MonsterRules)
                            if (string.Equals(r.Category, cat, StringComparison.OrdinalIgnoreCase))
                                r.Category = newName;
                        if (_categoryExpanded.TryGetValue(cat, out bool v))
                        {
                            _categoryExpanded.Remove(cat);
                            _categoryExpanded[newName] = v;
                        }
                        _onMonstersChanged();
                        ImGui.CloseCurrentPopup();
                    }
                    ImGui.EndPopup();
                }
            }

            if (!currentCatOpen) continue;

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            if (DrawToggleLight($"##F{i}", rule.Fester, OnColor, OffColor, "F: Fester (Decrepitude's Grasp / Fester Other I-VIII)"))
            { rule.Fester = !rule.Fester; _onMonstersChanged(); }

            ImGui.TableNextColumn();
            if (DrawToggleLight($"##B{i}", rule.Broadside, OnColor, OffColor, "B: Broadside (Broadside of a Barn / Missile Weapons Ineptitude I-VIII)"))
            { rule.Broadside = !rule.Broadside; _onMonstersChanged(); }

            ImGui.TableNextColumn();
            if (DrawToggleLight($"##G{i}", rule.GravityWell, OnColor, OffColor, "G: Gravity Well (Vulnerability Other I-VIII)"))
            { rule.GravityWell = !rule.GravityWell; _onMonstersChanged(); }

            ImGui.TableNextColumn();
            if (DrawToggleLight($"##I{i}", rule.Imperil, OnColor, OffColor, "I: Imperil (Gossamer Flesh / Imperil Other I-VIII)"))
            { rule.Imperil = !rule.Imperil; _onMonstersChanged(); }

            ImGui.TableNextColumn();
            if (DrawToggleLight($"##Y{i}", rule.Yield, OnColor, OffColor, "Y: Yield (Magic Yield Other I-VIII)"))
            { rule.Yield = !rule.Yield; _onMonstersChanged(); }

            ImGui.TableNextColumn();
            if (DrawToggleLight($"##V{i}", rule.Vuln, OnColor, OffColor, "V: Vulnerability (element-matched)"))
            { rule.Vuln = !rule.Vuln; _onMonstersChanged(); }

            ImGui.TableNextColumn();
            if (DrawToggleLight($"##A{i}", rule.UseArc, OnColor, OffColor, "A: Arc spells\nFor Melee/Missile: Attack toggle"))
            { rule.UseArc = !rule.UseArc; _onMonstersChanged(); }

            ImGui.TableNextColumn();
            if (DrawToggleLight($"##Bl{i}", rule.UseBolt, OnColor, OffColor, "Bl: Bolt spells (default)\nIf none of A/Bl/R/S = no attack (buff bot)"))
            { rule.UseBolt = !rule.UseBolt; _onMonstersChanged(); }

            ImGui.TableNextColumn();
            if (DrawToggleLight($"##R{i}", rule.UseRing, OnColor, OffColor, "R: Ring spells"))
            { rule.UseRing = !rule.UseRing; _onMonstersChanged(); }

            ImGui.TableNextColumn();
            if (DrawToggleLight($"##S{i}", rule.UseStreak, OnColor, OffColor, "S: Streak spells"))
            { rule.UseStreak = !rule.UseStreak; _onMonstersChanged(); }

            ImGui.TableNextColumn();
            CreatureProfile? profile = CreatureLookup?.Invoke(rule.Name);
            if (isDefault) ImGui.TextColored(new Vector4(1, 1, 0, 1), rule.Name);
            else if (profile != null) ImGui.TextColored(new Vector4(0.55f, 0.95f, 0.65f, 1f), rule.Name);
            else ImGui.TextUnformatted(rule.Name);
            if (profile != null && ImGui.IsItemHovered())
                ImGui.SetTooltip(BuildProfileTooltip(profile));

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            int pr = rule.Priority;
            if (ImGui.InputInt($"##P{i}", ref pr, 0)) { rule.Priority = pr; _onMonstersChanged(); }

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo($"##Dmg{i}", rule.DamageType, ImGuiComboFlags.None))
            {
                if (ImGui.Selectable("Auto", rule.DamageType == "Auto")) { rule.DamageType = "Auto"; _onMonstersChanged(); }
                foreach (var el in Elements)
                    if (ImGui.Selectable(el + $"##d{i}", rule.DamageType == el)) { rule.DamageType = el; _onMonstersChanged(); }
                ImGui.EndCombo();
            }

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo($"##Vuln{i}", rule.ExVuln, ImGuiComboFlags.None))
            {
                if (ImGui.Selectable("None", rule.ExVuln == "None")) { rule.ExVuln = "None"; _onMonstersChanged(); }
                foreach (var el in Elements)
                    if (ImGui.Selectable(el + $"##v{i}", rule.ExVuln == el)) { rule.ExVuln = el; _onMonstersChanged(); }
                ImGui.EndCombo();
            }

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            string wepLabel = "<AUTO>";
            if (rule.WeaponId != 0) { var w = _settings.ItemRules.FirstOrDefault(x => x.Id == rule.WeaponId); if (w != null) wepLabel = w.Name; }
            if (ImGui.BeginCombo($"##Wep{i}", wepLabel, ImGuiComboFlags.None))
            {
                if (ImGui.Selectable("<AUTO>", rule.WeaponId == 0)) { rule.WeaponId = 0; _onMonstersChanged(); }
                foreach (var item in _settings.ItemRules)
                    if (ImGui.Selectable(item.Name + $"##wep{i}_{item.Id}", rule.WeaponId == item.Id)) { rule.WeaponId = item.Id; _onMonstersChanged(); }
                ImGui.EndCombo();
            }

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            string ammoLabel = "<AUTO>";
            if (rule.PreferredAmmoItemId != 0)
            {
                var ar = _settings.AmmoRules.FirstOrDefault(a => a.Id == rule.PreferredAmmoItemId);
                if (ar != null) ammoLabel = ar.Name;
                else if (_worldFilter?[rule.PreferredAmmoItemId] != null)
                    ammoLabel = _worldFilter[rule.PreferredAmmoItemId]!.Name;
                else ammoLabel = $"0x{rule.PreferredAmmoItemId:X}";
            }
            if (ImGui.BeginCombo($"##AmmoPick{i}", ammoLabel, ImGuiComboFlags.None))
            {
                if (ImGui.Selectable("<AUTO>", rule.PreferredAmmoItemId == 0))
                {
                    rule.PreferredAmmoItemId = 0;
                    _onMonstersChanged();
                }
                foreach (var a in _settings.AmmoRules)
                {
                    if (ImGui.Selectable($"{a.Name}##am{i}_{a.Id}", rule.PreferredAmmoItemId == a.Id))
                    {
                        rule.PreferredAmmoItemId = a.Id;
                        _onMonstersChanged();
                    }
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton($"Sel##amsel{i}") && _host.HasGetSelectedItemId)
            {
                uint sid = _host.GetSelectedItemId();
                if (sid != 0 && _worldFilter != null && _worldFilter[(int)sid] is { } pick
                    && MissileAmmoHelper.GetAmmoKindFromName(pick.Name) != null)
                {
                    rule.PreferredAmmoItemId = (int)sid;
                    _onMonstersChanged();
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Per-monster loose ammo. Pick from list or click Sel with ammo selected in inventory.");

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            string offLabel = "<AUTO>";
            if (rule.OffhandId != 0) { var o = _settings.ItemRules.FirstOrDefault(x => x.Id == rule.OffhandId); if (o != null) offLabel = o.Name; }
            if (ImGui.BeginCombo($"##Off{i}", offLabel, ImGuiComboFlags.None))
            {
                if (ImGui.Selectable("<AUTO>", rule.OffhandId == 0)) { rule.OffhandId = 0; _onMonstersChanged(); }
                foreach (var item in _settings.ItemRules)
                    if (ImGui.Selectable(item.Name + $"##off{i}_{item.Id}", rule.OffhandId == item.Id)) { rule.OffhandId = item.Id; _onMonstersChanged(); }
                ImGui.EndCombo();
            }

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo($"##PetDmg{i}", rule.PetDamage, ImGuiComboFlags.None))
            {
                if (ImGui.Selectable("PAuto", rule.PetDamage == "PAuto")) { rule.PetDamage = "PAuto"; _onMonstersChanged(); }
                foreach (var el in Elements)
                    if (ImGui.Selectable(el + $"##p{i}", rule.PetDamage == el)) { rule.PetDamage = el; _onMonstersChanged(); }
                ImGui.EndCombo();
            }

            // [E] expression button
            ImGui.TableNextColumn();
            bool hasExpr = !string.IsNullOrWhiteSpace(rule.MatchExpression);
            if (hasExpr) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.1f, 0.5f, 0.2f, 1f));
            if (ImGui.SmallButton($"E##e{i}"))
            {
                _exprEditIndex = i;
                _exprEditBuffer = rule.MatchExpression ?? string.Empty;
                ImGui.OpenPopup($"ExprEdit{i}");
            }
            if (hasExpr)
            {
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Expression: {rule.MatchExpression}");
            }
            else if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Set a match expression.\n" +
                    "Examples:\n" +
                    "  range>5\n" +
                    "  range>5 && name==drudge ravener\n" +
                    "  metastate==combat\n" +
                    "Variables: name, range, typeid, maxhp, metastate, true, false");
            }

            // Expression edit popup (rendered outside table to avoid clipping)
            if (ImGui.BeginPopup($"ExprEdit{i}"))
            {
                ImGui.Text($"Expression for: {rule.Name}");
                ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f),
                    "Variables: name, range, typeid, maxhp, metastate, true, false");
                ImGui.SetNextItemWidth(360);
                ImGui.InputText("##exprval", ref _exprEditBuffer, 256);
                if (ImGui.Button("Apply", new Vector2(80, 22)))
                {
                    rule.MatchExpression = _exprEditBuffer.Trim();
                    _onMonstersChanged();
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("Clear", new Vector2(80, 22)))
                {
                    rule.MatchExpression = "";
                    _exprEditBuffer = "";
                    _onMonstersChanged();
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }

            // Del
            ImGui.TableNextColumn();
            if (isDefault)
            {
                ImGui.TextDisabled("-");
            }
            else
            {
                if (ImGui.SmallButton($"X##del{i}"))
                    deleteIndex = i;
            }
        }

        ImGui.EndTable();
        // Restore table-local styles (frames / padding).
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar(4);

        if (deleteIndex >= 0 && deleteIndex < _settings.MonsterRules.Count)
        {
            _settings.MonsterRules.RemoveAt(deleteIndex);
            _onMonstersChanged();
        }
    }

    private void DrawAddRow()
    {
        if (!ImGui.BeginTable("AddTable", 4))
            return;

        ImGui.TableSetupColumn("NameIn",    ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("CatIn",     ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableSetupColumn("AddBtn",    ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("AddSelBtn", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##NewMonName", "Monster name...", ref _newMonsterName, 64);

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##NewMonCat", "Category...", ref _newMonsterCategory, 64);

        ImGui.TableNextColumn();
        if (ImGui.Button("Add", new Vector2(60, 22)))
            TryAddMonster(_newMonsterName, _newMonsterCategory);

        ImGui.TableNextColumn();
        if (ImGui.Button("Add Sel", new Vector2(80, 22)))
        {
            var target = _getCurrentTarget();
            if (target.HasValue && !string.IsNullOrWhiteSpace(target.Value.Name))
                TryAddMonster(target.Value.Name, _newMonsterCategory);
        }

        ImGui.EndTable();
    }

    private void TryAddMonster(string name, string category)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (_settings.MonsterRules.Any(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
        _settings.MonsterRules.Add(new MonsterRule
        {
            Name = name.Trim(),
            Category = category.Trim(),
            DamageType = "Auto",
            WeaponId = 0,
        });
        _newMonsterName = string.Empty;
        _onMonstersChanged();
    }

    private static string BuildProfileTooltip(CreatureProfile p)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Captured: ").Append(p.Name);
        if (p.Wcid != 0) sb.Append(" (wcid ").Append(p.Wcid).Append(')');
        sb.Append('\n');

        if (p.MaxHealth > 0) sb.Append("HP: ").Append(p.MaxHealth).Append('\n');
        if (p.MaxStamina > 0) sb.Append("Stam: ").Append(p.MaxStamina).Append('\n');
        if (p.MaxMana > 0) sb.Append("Mana: ").Append(p.MaxMana).Append('\n');
        if (p.ArmorLevel > 0) sb.Append("Armor: ").Append(p.ArmorLevel).Append('\n');

        var (weakType, weakVal) = CreatureProfileStore.GetWeakest(p);
        if (weakVal < 1.0)
            sb.Append("Weak: ").Append(weakType).Append(" (").Append(weakVal.ToString("0.00")).Append(")\n");

        sb.Append("Resists: ")
          .Append("Sl ").Append(p.ResistSlash.ToString("0.00")).Append("  ")
          .Append("Pi ").Append(p.ResistPierce.ToString("0.00")).Append("  ")
          .Append("Bl ").Append(p.ResistBludgeon.ToString("0.00")).Append('\n')
          .Append("         ")
          .Append("Fi ").Append(p.ResistFire.ToString("0.00")).Append("  ")
          .Append("Co ").Append(p.ResistCold.ToString("0.00")).Append("  ")
          .Append("Ac ").Append(p.ResistAcid.ToString("0.00")).Append("  ")
          .Append("El ").Append(p.ResistElectric.ToString("0.00")).Append('\n');

        if (p.KnownSpellIds.Count > 0)
            sb.Append("Spells observed: ").Append(p.KnownSpellIds.Count).Append('\n');

        sb.Append("Samples: ").Append(p.Samples);
        if (!string.IsNullOrEmpty(p.LastSeen))
            sb.Append("  (last: ").Append(p.LastSeen).Append(')');

        return sb.ToString();
    }

    private static bool DrawToggleLight(string id, bool isOn, uint onColor, uint offColor, string? tooltip = null)
    {
        const float s = 15f; // Slightly larger hit + dot for the dense table.
        Vector2 pos = ImGui.GetCursorScreenPos();
        float pad = 1.5f;
        ImGui.GetWindowDrawList().AddRectFilled(
            pos + new Vector2(pad, pad),
            pos + new Vector2(pad + s, pad + s),
            isOn ? onColor : offColor,
            4.0f);
        ImGui.InvisibleButton(id, new Vector2(18, 18));
        if (tooltip != null && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
        return ImGui.IsItemClicked();
    }
}
