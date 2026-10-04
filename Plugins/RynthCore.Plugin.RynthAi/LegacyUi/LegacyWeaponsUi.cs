using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.Combat;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

internal sealed class LegacyWeaponsUi
{
    private readonly LegacyUiSettings _settings;
    private readonly RynthCoreHost _host;
    private WorldObjectCache? _worldFilter;

    private static readonly string[] Elements =
        { "Slash", "Pierce", "Bludgeon", "Fire", "Cold", "Lightning", "Acid", "Nether" };

    private static readonly string[] ConsumableTypes =
        { "General", "Lockpick", "HealthKit", "ManaStone", "Stamina", "Pet" };

    private static readonly string[] AmmoCategories =
        { "Auto", "Bow", "Crossbow", "Atlatl" };

    public LegacyWeaponsUi(LegacyUiSettings settings, RynthCoreHost host)
    {
        _settings = settings;
        _host = host;
    }

    public void SetWorldFilter(WorldObjectCache cache) => _worldFilter = cache;

    /// <summary>Persists monster rules (monsters.json) after a shield delete clears their Offhand.</summary>
    public Action? OnMonstersChanged { get; set; }

    private Action? _openLootEditor;
    private Action? _openMonsterEditor;

    /// <summary>Wires the "Tools" row at the top of the Items window (Loot Editor / Monster Editor buttons).</summary>
    public void SetToolLaunchers(Action openLootEditor, Action openMonsterEditor)
    {
        _openLootEditor = openLootEditor;
        _openMonsterEditor = openMonsterEditor;
    }

    public void Render()
    {
        if (!DashWindows.ShowWeapons) return;

        ImGui.SetNextWindowSize(new Vector2(480, 440), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Items##RynthAiWeapons", ref DashWindows.ShowWeapons))
        {
            ImGui.End();
            return;
        }

        ExternalTool.DrawToolButtons("Items", _openLootEditor, _openMonsterEditor);

        // ── Weapons Section ─────────────────────────────────────────────────
        ImGui.TextColored(LegacyDashboardRenderer.ColAmber, "Weapons");
        ImGui.Spacing();

        if (ImGui.BeginTable("WeaponsTable", 3,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Item Name", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Element",   ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("",          ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            for (int i = 0; i < _settings.ItemRules.Count; i++)
            {
                var rule = _settings.ItemRules[i];
                if (rule.IsShield()) continue; // listed in the Shields section below
                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                DrawItemName(rule.Id, rule.Name, "weapon");

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                int elemIdx = Array.IndexOf(Elements, rule.Element);
                if (elemIdx < 0) elemIdx = 0;
                if (ImGui.Combo($"##Elem{i}", ref elemIdx, Elements, Elements.Length))
                    rule.Element = Elements[elemIdx];

                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"Del##w{i}"))
                {
                    foreach (var mr in _settings.MonsterRules)
                        if (mr.WeaponId == rule.Id) mr.WeaponId = 0;
                    _settings.ItemRules.RemoveAt(i);
                    ImGui.EndTable();
                    ImGui.End();
                    return;
                }
            }
            ImGui.EndTable();
        }

        bool canAdd = _host.HasGetSelectedItemId;
        if (!canAdd) ImGui.BeginDisabled();

        if (ImGui.Button("Add Selected Weapon", new Vector2(160, 24)))
        {
            AddSelectedWeapon();
        }

        if (!canAdd) ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextDisabled("(Click a weapon in inventory first)");

        // ── Shields (secondary hand) ────────────────────────────────────────
        // Stored in ItemRules with Action="Shield" so the Monsters / Damage off-hand pickers
        // can select them; combat never wields them as the main weapon.
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(LegacyDashboardRenderer.ColAmber, "Shields (off-hand)");
        ImGui.Checkbox("Auto-equip with one-handed melee weapons", ref _settings.AutoEquipShield);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "On: in melee with a one-handed weapon, combat wields the first shield below\n" +
                "unless the monster has its own off-hand (Damage panel or Monsters 'Offhand').\n" +
                "Off: only per-monster off-hand choices are equipped.\n" +
                "Never used with two-handed weapons, bows, crossbows, atlatls or casters.");
        ImGui.Spacing();

        if (ImGui.BeginTable("ShieldsTable", 2,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Shield", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("",       ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            for (int i = 0; i < _settings.ItemRules.Count; i++)
            {
                var rule = _settings.ItemRules[i];
                if (!rule.IsShield()) continue;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                DrawItemName(rule.Id, rule.Name, "shield");

                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"Del##s{i}"))
                {
                    // Drop per-monster references so no rule points at a removed shield.
                    bool monstersChanged = false;
                    foreach (var mr in _settings.MonsterRules)
                        if (mr.OffhandId == rule.Id) { mr.OffhandId = 0; monstersChanged = true; }
                    _settings.ItemRules.RemoveAt(i);
                    if (monstersChanged) OnMonstersChanged?.Invoke();
                    ImGui.EndTable();
                    ImGui.End();
                    return;
                }
            }
            ImGui.EndTable();
        }

        if (!canAdd) ImGui.BeginDisabled();
        if (ImGui.Button("Add Selected Shield", new Vector2(160, 24)))
            AddSelectedShield();
        if (!canAdd) ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled("(Click a shield in inventory first)");

        // ── Consumable Items Section ────────────────────────────────────────
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(LegacyDashboardRenderer.ColAmber, "Consumable Items");
        ImGui.Spacing();

        if (ImGui.BeginTable("ConsumablesTable", 3,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Item Name", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Type",      ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("",          ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            for (int i = 0; i < _settings.ConsumableRules.Count; i++)
            {
                var rule = _settings.ConsumableRules[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                bool inCache = _worldFilter?[rule.Id] != null;
                if (inCache)
                {
                    ImGui.TextUnformatted(rule.Name);
                }
                else
                {
                    ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), rule.Name);
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 0.7f), "(Gone)");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Item not in inventory.\nRemove and re-add the correct item.");
                }

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                int typeIdx = Array.IndexOf(ConsumableTypes, rule.Type);
                if (typeIdx < 0) typeIdx = 0;
                if (ImGui.Combo($"##CType{i}", ref typeIdx, ConsumableTypes, ConsumableTypes.Length))
                    rule.Type = ConsumableTypes[typeIdx];

                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"Del##c{i}"))
                {
                    _settings.ConsumableRules.RemoveAt(i);
                    ImGui.EndTable();
                    ImGui.End();
                    return;
                }
            }
            ImGui.EndTable();
        }

        if (!canAdd) ImGui.BeginDisabled();

        if (ImGui.Button("Add Selected Consumable", new Vector2(180, 24)))
        {
            AddSelectedConsumable();
        }

        if (!canAdd) ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextDisabled("(Click an item in inventory first)");

        // ── Missile ammunition (optional manual list) ───────────────────────
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(LegacyDashboardRenderer.ColAmber, "Missile ammunition");
        ImGui.Checkbox("Inventory rules only (no auto-scan for loose ammo)", ref _settings.MissileAmmoInventoryRulesOnly);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Off (default): auto-pick loose arrows / quarrels / darts that match your wielded bow, crossbow, or atlatl.\n" +
                "On: only stacks listed below (and per-monster Preferred ammo) are used.");

        ImGui.Spacing();
        if (ImGui.BeginTable("AmmoTable", 3,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Ammo stack", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Launcher", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            for (int i = 0; i < _settings.AmmoRules.Count; i++)
            {
                var rule = _settings.AmmoRules[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                bool inCache = _worldFilter?[rule.Id] != null;
                if (inCache) ImGui.TextUnformatted(rule.Name);
                else
                {
                    ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), rule.Name);
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 0.7f), "(Gone)");
                }

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                int catIdx = Array.IndexOf(AmmoCategories, rule.Category);
                if (catIdx < 0) catIdx = 0;
                if (ImGui.Combo($"##AmmoCat{i}", ref catIdx, AmmoCategories, AmmoCategories.Length))
                    rule.Category = AmmoCategories[catIdx];

                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"Del##a{i}"))
                {
                    _settings.AmmoRules.RemoveAt(i);
                    foreach (var mr in _settings.MonsterRules)
                        if (mr.PreferredAmmoItemId == rule.Id) mr.PreferredAmmoItemId = 0;
                    ImGui.EndTable();
                    ImGui.End();
                    return;
                }
            }
            ImGui.EndTable();
        }

        if (!canAdd) ImGui.BeginDisabled();
        if (ImGui.Button("Add selected as ammo", new Vector2(200, 24)))
            AddSelectedAmmo();
        if (!canAdd) ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled("(select loose arrows / quarrels / darts in inventory)");

        // ── Mana Stone Tapping ──────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(LegacyDashboardRenderer.ColAmber, "Mana Stone Tapping");
        ImGui.Spacing();
        ImGui.Text("Keep up to:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(70);
        ImGui.DragInt("##StoneKeep", ref _settings.ManaStoneKeepCount, 0.2f, 1, 999);
        if (_settings.ManaStoneKeepCount < 1)  _settings.ManaStoneKeepCount = 1;
        if (_settings.ManaStoneKeepCount > 999) _settings.ManaStoneKeepCount = 999;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Maximum number of mana stones to keep in inventory.\nStones on corpses are skipped once this count is reached.");
        ImGui.SameLine(0, 16);
        ImGui.Checkbox("Enable Tapping", ref _settings.EnableManaTapping);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Use mana stones on inventory items with high MaxMana,\nthen apply the charged stone to the player to recharge gear.");
        if (_settings.EnableManaTapping)
        {
            ImGui.SameLine(0, 16);
            ImGui.Text("Min MaxMana:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
            ImGui.DragInt("##TapThresh", ref _settings.ManaTapMinMana, 50f, 100, 99999);
            if (_settings.ManaTapMinMana < 100)  _settings.ManaTapMinMana = 100;
            if (_settings.ManaTapMinMana > 99999) _settings.ManaTapMinMana = 99999;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Only tap items whose MaxMana is at or above this value.");
        }

        ImGui.End();
    }

    public void AddSelectedWeapon()
    {
        uint selId = _host.GetSelectedItemId();
        if (selId == 0)
        {
            _host.WriteToChat("[RynthAi] No item selected — click a weapon in your inventory first.", 1);
            return;
        }
        if (_worldFilter == null) { _host.WriteToChat("[RynthAi] Object cache not ready.", 1); return; }

        var wo = _worldFilter[(int)selId];
        if (wo == null)
            _host.WriteToChat($"[RynthAi] Item 0x{selId:X8} not in cache — try again.", 1);
        else if (ShieldHelper.IsShieldObject(wo))
            _host.WriteToChat("[RynthAi] That is a shield — use \"Add Selected Shield\" (Items → Shields).", 1);
        else if (!IsWeapon(wo))
            _host.WriteToChat("[RynthAi] Selected item is not a weapon or wand.", 1);
        else if (_settings.ItemRules.Any(x => x.Id == wo.Id))
            _host.WriteToChat($"[RynthAi] {wo.Name} is already in the list.", 1);
        else
        {
            string element = DetectWeaponElement(wo);
            _settings.ItemRules.Add(new ItemRule
            {
                Id      = wo.Id,
                Name    = wo.Name,
                Element = element,
                Action  = ItemRule.WeaponAction,
            });
            _host.WriteToChat($"[RynthAi] Added weapon: {wo.Name} (0x{(uint)wo.Id:X8}) [{element}]", 1);
        }
    }

    /// <summary>Adds the inventory-selected shield as an off-hand entry (ItemRule, Action="Shield").</summary>
    public void AddSelectedShield()
    {
        uint selId = _host.GetSelectedItemId();
        if (selId == 0)
        {
            _host.WriteToChat("[RynthAi] No item selected — click a shield in your inventory first.", 1);
            return;
        }
        if (_worldFilter == null) { _host.WriteToChat("[RynthAi] Object cache not ready.", 1); return; }

        var wo = _worldFilter[(int)selId];
        if (wo == null)
        {
            _host.WriteToChat($"[RynthAi] Item 0x{selId:X8} not in cache — try again.", 1);
            return;
        }
        if (!ShieldHelper.IsShieldObject(wo))
        {
            _host.WriteToChat("[RynthAi] Selected item is not a shield.", 1);
            return;
        }

        var existing = _settings.ItemRules.FirstOrDefault(x => x.Id == wo.Id);
        if (existing != null && existing.IsShield())
        {
            _host.WriteToChat($"[RynthAi] {wo.Name} is already in the list.", 1);
            return;
        }
        if (existing != null)
        {
            // Imported profiles could list a shield as a weapon; re-tag it instead of duplicating.
            existing.Action = ItemRule.ShieldAction;
            _host.WriteToChat($"[RynthAi] {wo.Name} moved from Weapons to Shields.", 1);
            return;
        }

        _settings.ItemRules.Add(new ItemRule
        {
            Id     = wo.Id,
            Name   = wo.Name,
            Action = ItemRule.ShieldAction,
        });
        _host.WriteToChat($"[RynthAi] Added shield: {wo.Name} (0x{(uint)wo.Id:X8})", 1);
    }

    /// <summary>Item name cell; red "(Gone)" when the item is no longer in the object cache.</summary>
    private void DrawItemName(int id, string name, string kind)
    {
        if (_worldFilter?[id] != null)
        {
            ImGui.TextUnformatted(name);
            return;
        }
        ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), name);
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 0.7f), "(Gone)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Item not in inventory.\nRemove and re-add the correct {kind}.");
    }

    public void AddSelectedConsumable()
    {
        uint selId = _host.GetSelectedItemId();
        if (selId == 0)
        {
            _host.WriteToChat("[RynthAi] No item selected — click an item in your inventory first.", 1);
            return;
        }
        if (_worldFilter == null) { _host.WriteToChat("[RynthAi] Object cache not ready.", 1); return; }

        var wo = _worldFilter[(int)selId];
        if (wo == null)
            _host.WriteToChat($"[RynthAi] Item 0x{selId:X8} not in cache — try again.", 1);
        else if (_settings.ConsumableRules.Any(x => x.Id == wo.Id))
            _host.WriteToChat($"[RynthAi] {wo.Name} is already in the list.", 1);
        else
        {
            string type = DetectConsumableType(wo);
            _settings.ConsumableRules.Add(new ConsumableRule
            {
                Id   = wo.Id,
                Name = wo.Name,
                Type = type,
            });
            _host.WriteToChat($"[RynthAi] Added consumable: {wo.Name} (0x{(uint)wo.Id:X8}) [{type}]", 1);
        }
    }

    private void AddSelectedAmmo()
    {
        uint selId = _host.GetSelectedItemId();
        if (selId == 0)
        {
            _host.WriteToChat("[RynthAi] No item selected — click loose ammo in inventory first.", 1);
            return;
        }
        if (_worldFilter == null) { _host.WriteToChat("[RynthAi] Object cache not ready.", 1); return; }

        var wo = _worldFilter[(int)selId];
        if (wo == null)
            _host.WriteToChat($"[RynthAi] Item 0x{selId:X8} not in cache — try again.", 1);
        else if (MissileAmmoHelper.GetAmmoKindFromName(wo.Name) == null)
            _host.WriteToChat("[RynthAi] Selected item does not look like loose missile ammo.", 1);
        else if (_settings.AmmoRules.Any(x => x.Id == wo.Id))
            _host.WriteToChat($"[RynthAi] {wo.Name} is already listed as ammo.", 1);
        else
        {
            var kind = MissileAmmoHelper.GetAmmoKindFromName(wo.Name)!.Value;
            string cat = kind switch
            {
                MissileWeaponKind.Bow      => "Bow",
                MissileWeaponKind.Crossbow => "Crossbow",
                MissileWeaponKind.Atlatl   => "Atlatl",
                _                          => "Auto",
            };
            _settings.AmmoRules.Add(new AmmoRule { Id = wo.Id, Name = wo.Name, Category = cat });
            _host.WriteToChat($"[RynthAi] Added missile ammo: {wo.Name} [{cat}]", 1);
        }
    }

    private static bool IsWeapon(WorldObject wo) =>
        wo.ObjectClass == AcObjectClass.MeleeWeapon   ||
        wo.ObjectClass == AcObjectClass.MissileWeapon ||
        wo.ObjectClass == AcObjectClass.WandStaffOrb;

    /// <summary>Auto-detect weapon element from the DamageType int property.</summary>
    private static string DetectWeaponElement(WorldObject wo)
    {
        // DamageType (STypeInt 45) — AC damage type flags
        int dt = wo.Values(LongValueKey.DamageType, 0);
        if (dt == 0) return "Slash"; // no data — default

        // Check flags in order of specificity (elemental first, then physical)
        if ((dt & 1024) != 0) return "Nether";
        if ((dt & 16)   != 0) return "Fire";
        if ((dt & 8)    != 0) return "Cold";
        if ((dt & 64)   != 0) return "Lightning";
        if ((dt & 32)   != 0) return "Acid";
        if ((dt & 1)    != 0) return "Slash";
        if ((dt & 2)    != 0) return "Pierce";
        if ((dt & 4)    != 0) return "Bludgeon";

        return "Slash";
    }

    /// <summary>Auto-detect consumable type from item name.</summary>
    private static string DetectConsumableType(WorldObject wo)
    {
        string name = wo.Name;
        if (string.IsNullOrEmpty(name)) return "General";

        // Health items
        if (name.Contains("Heal", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Health", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Elixir of Healing", StringComparison.OrdinalIgnoreCase))
            return "HealthKit";

        // Mana items
        if (name.Contains("Mana", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Mana Stone", StringComparison.OrdinalIgnoreCase))
            return "ManaStone";

        // Stamina items
        if (name.Contains("Stamina", StringComparison.OrdinalIgnoreCase))
            return "Stamina";

        // Lockpicks
        if (name.Contains("Lockpick", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Lock Pick", StringComparison.OrdinalIgnoreCase))
            return "Lockpick";

        // Combat-pet summoning essences (PetDevices) — consumed by PetManager.
        if (name.Contains("Essence", StringComparison.OrdinalIgnoreCase))
            return "Pet";

        return "General";
    }
}
