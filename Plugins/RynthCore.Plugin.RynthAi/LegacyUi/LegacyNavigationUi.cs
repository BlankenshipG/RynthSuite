using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using ImGuiNET;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

/// <summary>
/// Navigation window: route file actions, waypoint editor (UB-IT nav builder parity: add
/// point / pause / chat / recall / portal, multi-select move / duplicate / delete, snap to
/// path, reverse, per-step edit), route recording + breadcrumb trail, cleanup tools, and the
/// marker / HUD display options. Structural edits go through <see cref="NavRouteEditing.Apply"/>
/// (copy-on-write) because the nav engine reads the route on the tick thread.
/// </summary>
internal sealed class LegacyNavigationUi
{
    private readonly LegacyUiSettings _settings;
    private readonly RynthCoreHost _host;

    /// <summary>Settings persisted (route path / active index / display options changed).</summary>
    public Action? OnSettingsChanged;
    /// <summary>Nav folder content or current file changed (Save As / New): refresh the dashboard combo.</summary>
    public Action? OnNavFilesChanged;
    /// <summary>Dashboard's nearest-waypoint search (same rule as loading a route).</summary>
    public Func<int>? FindNearestWaypoint;
    /// <summary>NavProfiles folder for Save As.</summary>
    public string NavFolder = string.Empty;
    /// <summary>Trail + recording state; null until the plugin session is up.</summary>
    public NavBreadcrumbTracker? Breadcrumbs;

    private readonly string[] _routeTypes = { "Once", "Circular", "Linear", "Follow" };
    private readonly string[] _addModes = { "End", "Above", "Below" };
    private int _addModeIdx = 0;

    // Selection is kept by NavPoint reference so it survives reorders and copy-on-write swaps.
    private readonly HashSet<NavPoint> _sel = new();
    private NavPoint? _anchor;

    // Popup / tool inputs.
    private float _pauseSeconds = 3f;
    private string _chatText = string.Empty;
    private bool _chatAutoSlash = true;
    private int _manualRecallId;
    private string _saveAsName = string.Empty;
    private bool _overwriteArmed;
    private float _simplifyYards = 1.0f;

    // One-line feedback under the toolbar.
    private string _status = string.Empty;
    private long _statusAt;

    private static readonly Vector4 ColYellow = new(1f, 1f, 0f, 1f);
    private static readonly Vector4 ColMuted  = new(0.6f, 0.62f, 0.66f, 1f);
    private static readonly Vector4 ColActive = new(1f, 0.45f, 0.45f, 1f);

    // Recall spells offered in the Add Recall popup (names resolved from SpellDatabase).
    private static readonly int[] RecallSpellIds =
    {
        48, 2645, 2647,
        1635, 1636,
        157, 158, 1637,
        2648, 2649, 2650,
        2931, 2023, 2041, 2358, 2813, 2941, 2943,
        3865, 3929, 3930, 4084, 4198, 4213,
        4907, 4908, 4909,
        5175, 5330, 5541, 6150, 6321, 6322,
    };

    public LegacyNavigationUi(LegacyUiSettings settings, RynthCoreHost host)
    {
        _settings = settings;
        _host = host;
    }

    private NavRouteParser Route => _settings.CurrentRoute;

    public void Render()
    {
        ImGui.SetNextWindowSize(new Vector2(540, 640), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Navigation##RynthAiNav", ref DashWindows.ShowNavigation))
        {
            ImGui.End();
            return;
        }

        PruneSelection();
        RenderHeader();
        RenderFileRow();
        ImGui.Separator();

        if (ImGui.BeginTabBar("##NavTabs"))
        {
            if (ImGui.BeginTabItem("Route")) { RenderRouteTab(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Record")) { RenderRecordTab(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Tools")) { RenderToolsTab(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Display")) { RenderDisplayTab(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Header: active nav, status, start / stop, route type
    // ═══════════════════════════════════════════════════════════════════

    private void RenderHeader()
    {
        string activeNavName = string.IsNullOrEmpty(_settings.CurrentNavPath) ? "None (Unsaved)" : Path.GetFileName(_settings.CurrentNavPath);
        ImGui.TextColored(ColYellow, $"Active Nav: {activeNavName}");
        if (_settings.IsRecordingNav)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "  [REC]");
        }

        if (!string.IsNullOrEmpty(_settings.NavStatusLine))
        {
            var statusColor = _settings.NavIsStuck
                ? new Vector4(0.91f, 0.70f, 0.20f, 1.00f)   // amber
                : new Vector4(0.25f, 0.85f, 0.45f, 1.00f);  // green
            ImGui.TextColored(statusColor, _settings.NavStatusLine);
        }

        ImGui.Spacing();
        bool navActive = _settings.IsMacroRunning && _settings.EnableNavigation;
        if (navActive)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.50f, 0.10f, 0.10f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.70f, 0.15f, 0.15f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.35f, 0.08f, 0.08f, 1f));
            if (ImGui.Button("Stop Navigation", new Vector2(-1, 28)))
                _settings.EnableNavigation = false;
            ImGui.PopStyleColor(3);
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.10f, 0.38f, 0.10f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.15f, 0.55f, 0.15f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.08f, 0.28f, 0.08f, 1f));
            if (ImGui.Button("Start Navigation", new Vector2(-1, 28)))
            {
                _settings.IsMacroRunning  = true;
                _settings.EnableNavigation = true;
                if (_settings.BotAction != "Navigating")
                    _settings.BotAction = "Default";
            }
            ImGui.PopStyleColor(3);
        }

        int rTypeIdx = Route.RouteType switch
        {
            NavRouteType.Circular => 1,
            NavRouteType.Linear   => 2,
            NavRouteType.Follow   => 3,
            _                     => 0,
        };
        ImGui.SetNextItemWidth(100);
        if (ImGui.Combo("Route Type", ref rTypeIdx, _routeTypes, _routeTypes.Length))
        {
            Route.RouteType = rTypeIdx switch
            {
                1 => NavRouteType.Circular,
                2 => NavRouteType.Linear,
                3 => NavRouteType.Follow,
                _ => NavRouteType.Once,
            };
            TryAutoSaveNav();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        ImGui.Combo("Insert", ref _addModeIdx, _addModes, _addModes.Length);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Where new steps go: end of the route, or above / below the selected step.");

        var pts = Route.Points;
        ImGui.SameLine();
        ImGui.TextDisabled($"{pts.Count} steps, {NavRouteEditing.PathLengthYards(pts):F0} yd");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  File row: New / Save / Save As / Reload
    // ═══════════════════════════════════════════════════════════════════

    private void RenderFileRow()
    {
        if (ImGui.Button("New")) ImGui.OpenPopup("NavNewConfirm");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Start an empty, unsaved route (the current file stays on disk).");
        ImGui.SameLine();
        if (ImGui.Button("Save"))
        {
            if (string.IsNullOrEmpty(_settings.CurrentNavPath)) OpenSaveAs();
            else SaveTo(_settings.CurrentNavPath);
        }
        ImGui.SameLine();
        if (ImGui.Button("Save As...")) OpenSaveAs();
        ImGui.SameLine();
        ImGui.BeginDisabled(string.IsNullOrEmpty(_settings.CurrentNavPath));
        if (ImGui.Button("Reload")) ReloadFromDisk();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Re-read the route file from disk.");

        if (ImGui.BeginPopup("NavNewConfirm"))
        {
            ImGui.Text("Start a new empty route?");
            if (ImGui.Button("New route##navNewYes"))
            {
                _settings.IsRecordingNav = false;
                _settings.CurrentRoute = new NavRouteParser { RouteType = NavRouteType.Circular };
                _settings.CurrentNavPath = string.Empty;
                _settings.ActiveNavIndex = 0;
                _sel.Clear();
                _anchor = null;
                OnNavFilesChanged?.Invoke();
                OnSettingsChanged?.Invoke();
                SetStatus("New empty route. Use Save As to name it.");
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel##navNewNo")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopup("NavSaveAs"))
        {
            ImGui.Text("Save route as (NavProfiles folder):");
            ImGui.SetNextItemWidth(240);
            if (ImGui.InputText("##navSaveAsName", ref _saveAsName, 64)) _overwriteArmed = false;
            string name = SanitizeFileName(_saveAsName);
            string path = name.Length > 0 && NavFolder.Length > 0 ? Path.Combine(NavFolder, name + ".nav") : string.Empty;
            bool exists = path.Length > 0 && File.Exists(path);
            ImGui.BeginDisabled(path.Length == 0);
            if (ImGui.Button(exists && !_overwriteArmed ? "Save (file exists)##navSaveAsGo" : "Save##navSaveAsGo"))
            {
                if (exists && !_overwriteArmed && !path.Equals(_settings.CurrentNavPath, StringComparison.OrdinalIgnoreCase))
                {
                    _overwriteArmed = true; // second click overwrites
                }
                else
                {
                    if (SaveTo(path))
                    {
                        _settings.CurrentNavPath = path;
                        OnNavFilesChanged?.Invoke();
                        OnSettingsChanged?.Invoke();
                    }
                    ImGui.CloseCurrentPopup();
                }
            }
            ImGui.EndDisabled();
            if (_overwriteArmed) { ImGui.SameLine(); ImGui.TextColored(ColActive, "Click again to overwrite."); }
            ImGui.SameLine();
            if (ImGui.Button("Cancel##navSaveAsNo")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    private void OpenSaveAs()
    {
        _saveAsName = string.IsNullOrEmpty(_settings.CurrentNavPath) ? "NewRoute" : Path.GetFileNameWithoutExtension(_settings.CurrentNavPath);
        _overwriteArmed = false;
        ImGui.OpenPopup("NavSaveAs");
    }

    private bool SaveTo(string path)
    {
        try
        {
            Route.Save(path);
            SetStatus($"Saved {Path.GetFileName(path)} ({Route.Points.Count} steps).");
            RynthLog.Write(LogCat.Navigation, $"Route saved: {path}");
            return true;
        }
        catch (Exception ex)
        {
            SetStatus("Save failed: " + ex.Message);
            RynthLog.Write(LogCat.Navigation, $"Failed to save route: {ex.Message}");
            return false;
        }
    }

    private void ReloadFromDisk()
    {
        string path = _settings.CurrentNavPath;
        var route = NavRouteParser.Load(path);
        if (route.LoadWarning != null) _host.WriteToChat($"[RynthAi] {route.LoadWarning}", 4);
        _settings.CurrentRoute = route;
        _settings.ActiveNavIndex = Math.Clamp(_settings.ActiveNavIndex, 0, Math.Max(0, route.Points.Count - 1));
        _sel.Clear();
        _anchor = null;
        OnSettingsChanged?.Invoke();
        SetStatus($"Reloaded {Path.GetFileName(path)}.");
    }

    private static string SanitizeFileName(string name)
    {
        name = name.Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), string.Empty);
        if (name.EndsWith(".nav", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Route tab: add / edit toolbars, step list, selected-step editor
    // ═══════════════════════════════════════════════════════════════════

    private void RenderRouteTab()
    {
        RenderAddRow();
        RenderEditRow();
        if (!string.IsNullOrEmpty(_status) && Environment.TickCount64 - _statusAt < 8000)
            ImGui.TextColored(ColMuted, _status);

        RenderStepList();
        RenderSelectedEditor();
    }

    private void RenderAddRow()
    {
        ImGui.TextDisabled("Add:");
        ImGui.SameLine();
        if (ImGui.Button("Point"))
        {
            // Stop any active turn motion so the character doesn't keep spinning.
            _host.SetMotion(0x6500000D, false); // TurnRight
            _host.SetMotion(0x6500000E, false); // TurnLeft
            if (TryHere(out var p)) InsertPoint(p);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Waypoint at your position.");

        ImGui.SameLine();
        if (ImGui.Button("Snap to path"))
        {
            if (TryHere(out var here))
            {
                NavPoint? inserted = null;
                string error = string.Empty;
                Edit(list => { NavRouteEditing.TrySnapInsert(list, here.NS, here.EW, 0.5, out inserted, out error); return list; });
                if (inserted != null) { SelectOnly(inserted); SetStatus("Inserted a point on the path."); }
                else SetStatus(error);
            }
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Insert a point on the route line at the spot closest to you.");

        ImGui.SameLine();
        if (ImGui.Button("Pause...")) ImGui.OpenPopup("NavAddPause");
        ImGui.SameLine();
        if (ImGui.Button("Chat...")) ImGui.OpenPopup("NavAddChat");
        ImGui.SameLine();
        if (ImGui.Button("Recall...")) ImGui.OpenPopup("NavAddRecall");

        ImGui.TextDisabled("    ");
        ImGui.SameLine();
        if (ImGui.Button("Portal (selected)")) AddPortalFromSelection();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Select the portal in the world first. Adds a Portal step that uses it by name.");
        ImGui.SameLine();
        if (ImGui.Button("Point at selected")) AddPointAtSelection();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Waypoint at the selected object's position (walk to an NPC, door, chest…).");

        if (ImGui.BeginPopup("NavAddPause"))
        {
            ImGui.SetNextItemWidth(120);
            ImGui.InputFloat("seconds##navPause", ref _pauseSeconds, 0.5f, 5f, "%.1f");
            _pauseSeconds = Math.Clamp(_pauseSeconds, 0.1f, 600f);
            if (ImGui.Button("Add pause##navPauseGo"))
            {
                if (TryHere(out var p))
                {
                    p.Type = NavPointType.Pause;
                    p.PauseTimeMs = (int)(_pauseSeconds * 1000);
                    InsertPoint(p);
                }
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopup("NavAddChat"))
        {
            ImGui.SetNextItemWidth(300);
            ImGui.InputText("##navChat", ref _chatText, 256);
            ImGui.Checkbox("Auto-prefix '/'##navChatSlash", ref _chatAutoSlash);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Turns \"ra start\" into \"/ra start\".");
            ImGui.BeginDisabled(string.IsNullOrWhiteSpace(_chatText));
            if (ImGui.Button("Add chat##navChatGo"))
            {
                string cmd = _chatText.Trim();
                if (_chatAutoSlash && !cmd.StartsWith('/') && !cmd.StartsWith('@')) cmd = "/" + cmd;
                if (TryHere(out var p))
                {
                    p.Type = NavPointType.Chat;
                    p.ChatCommand = cmd;
                    InsertPoint(p);
                }
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopup("NavAddRecall"))
        {
            foreach (int id in RecallSpellIds)
                if (ImGui.Selectable($"{SpellLabel(id)}##navRecall{id}")) AddRecall(id);
            ImGui.Separator();
            ImGui.SetNextItemWidth(100);
            ImGui.InputInt("Spell id##navRecallId", ref _manualRecallId);
            ImGui.SameLine();
            ImGui.BeginDisabled(_manualRecallId <= 0);
            if (ImGui.Button("Add##navRecallManual")) { AddRecall(_manualRecallId); ImGui.CloseCurrentPopup(); }
            ImGui.EndDisabled();
            ImGui.EndPopup();
        }
    }

    private void RenderEditRow()
    {
        bool any = _sel.Count > 0;
        ImGui.TextDisabled("Edit:");
        ImGui.SameLine();
        ImGui.BeginDisabled(!any);
        if (ImGui.Button("Up")) Edit(l => NavRouteEditing.Move(l, _sel, -1));
        ImGui.SameLine();
        if (ImGui.Button("Down")) Edit(l => NavRouteEditing.Move(l, _sel, +1));
        ImGui.SameLine();
        if (ImGui.Button("Dup")) DuplicateSelected();
        ImGui.SameLine();
        if (ImGui.Button("Delete")) DeleteSelected();
        ImGui.SameLine();
        if (ImGui.Button("Set active")) { int i = PrimaryIndex(); if (i >= 0) SetActive(i); }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Navigate from this step (double-click a row does the same).");
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Nearest"))
        {
            int i = FindNearestWaypoint?.Invoke() ?? -1;
            if (i >= 0) { SetActive(i); SelectOnly(Route.Points[i]); }
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Make the waypoint closest to you the active one.");
        ImGui.SameLine();
        ImGui.BeginDisabled(Route.Points.Count < 2);
        if (ImGui.Button("Reverse")) { Edit(NavRouteEditing.Reverse); SetStatus("Route reversed."); }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.SmallButton("All")) { foreach (var p in Route.Points) _sel.Add(p); }
        ImGui.SameLine();
        if (ImGui.SmallButton("None")) { _sel.Clear(); _anchor = null; }

        // Delete key while the window has focus.
        if (any && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && !ImGui.GetIO().WantTextInput
            && ImGui.IsKeyPressed(ImGuiKey.Delete))
            DeleteSelected();
    }

    private void RenderStepList()
    {
        var pts = Route.Points; // snapshot reference — edits swap in a new list
        bool haveHere = NavCoordinateHelper.TryGetNavCoords(_host, out double hereNS, out double hereEW);
        float listHeight = Math.Max(140f, ImGui.GetContentRegionAvail().Y - (_sel.Count == 1 ? 150f : 10f));

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit;
        if (!ImGui.BeginTable("##NavSteps", 3, flags, new Vector2(0, listHeight))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 48);
        ImGui.TableSetupColumn("Step", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Dist", ImGuiTableColumnFlags.WidthFixed, 56);
        ImGui.TableHeadersRow();

        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            bool active = i == _settings.ActiveNavIndex;
            bool selected = _sel.Contains(p);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            if (active) ImGui.PushStyleColor(ImGuiCol.Text, ColActive);
            bool clicked = ImGui.Selectable($"{(active ? ">" : " ")}{i}##navRow{i}", selected,
                ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick);
            if (active) ImGui.PopStyleColor();
            if (clicked) OnRowClicked(pts, i);
            if (ImGui.BeginPopupContextItem($"navCtx{i}"))
            {
                if (!selected) SelectOnly(p);
                RenderRowContextMenu(i);
                ImGui.EndPopup();
            }

            ImGui.TableNextColumn();
            ImGui.TextColored(TypeColor(p.Type), StepLabel(p));

            ImGui.TableNextColumn();
            if (haveHere && p.Type == NavPointType.Point)
                ImGui.TextDisabled($"{NavRouteEditing.DistanceYards(hereNS, hereEW, p.NS, p.EW):F0}yd");
        }

        ImGui.EndTable();
    }

    private void OnRowClicked(List<NavPoint> pts, int i)
    {
        var io = ImGui.GetIO();
        var p = pts[i];
        if (io.KeyShift && _anchor != null && pts.IndexOf(_anchor) is int a and >= 0)
        {
            if (!io.KeyCtrl) _sel.Clear();
            for (int k = Math.Min(a, i); k <= Math.Max(a, i); k++) _sel.Add(pts[k]);
        }
        else if (io.KeyCtrl)
        {
            if (!_sel.Remove(p)) _sel.Add(p);
            _anchor = p;
        }
        else
        {
            SelectOnly(p);
        }
        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) SetActive(i);
    }

    private void RenderRowContextMenu(int i)
    {
        if (ImGui.MenuItem("Set active")) SetActive(i);
        if (ImGui.MenuItem("Move to my position", string.Empty, false, _sel.Count == 1)) MoveSelectedHere();
        if (ImGui.MenuItem("Duplicate")) DuplicateSelected();
        if (ImGui.MenuItem("Move up")) Edit(l => NavRouteEditing.Move(l, _sel, -1));
        if (ImGui.MenuItem("Move down")) Edit(l => NavRouteEditing.Move(l, _sel, +1));
        ImGui.Separator();
        if (ImGui.MenuItem($"Delete ({_sel.Count})")) DeleteSelected();
    }

    /// <summary>Type-specific fields for a single selected step; edits apply in place and save on release.</summary>
    private void RenderSelectedEditor()
    {
        if (_sel.Count != 1) return;
        NavPoint? p = null;
        foreach (var s in _sel) p = s;
        if (p == null) return;
        int idx = Route.Points.IndexOf(p);
        if (idx < 0) return;

        ImGui.Separator();
        ImGui.TextColored(TypeColor(p.Type), $"Step {idx}: {p.Type}");

        bool saved = false;
        if (p.Type is NavPointType.Point or NavPointType.Pause or NavPointType.Chat or NavPointType.Recall)
        {
            double ns = p.NS, ew = p.EW;
            ImGui.SetNextItemWidth(130);
            if (ImGui.InputDouble("NS##navEdNs", ref ns, 0.001, 0.01, "%.4f")) p.NS = ns;
            saved |= ImGui.IsItemDeactivatedAfterEdit();
            ImGui.SameLine();
            ImGui.SetNextItemWidth(130);
            if (ImGui.InputDouble("EW##navEdEw", ref ew, 0.001, 0.01, "%.4f")) p.EW = ew;
            saved |= ImGui.IsItemDeactivatedAfterEdit();
            ImGui.SameLine();
            float height = (float)(p.Z * NavCoordinateHelper.NavZScale);
            ImGui.SetNextItemWidth(90);
            if (ImGui.InputFloat("Height##navEdZ", ref height, 0f, 0f, "%.2f")) p.Z = height / NavCoordinateHelper.NavZScale;
            saved |= ImGui.IsItemDeactivatedAfterEdit();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("World height (stored in the .nav file as height / 240).");
            if (ImGui.Button("Set to my position##navEdHere")) MoveSelectedHere();
        }

        switch (p.Type)
        {
            case NavPointType.Pause:
            {
                float secs = p.PauseTimeMs / 1000f;
                ImGui.SetNextItemWidth(120);
                if (ImGui.InputFloat("Pause (s)##navEdPause", ref secs, 0.5f, 5f, "%.1f"))
                    p.PauseTimeMs = (int)(Math.Clamp(secs, 0.1f, 600f) * 1000);
                saved |= ImGui.IsItemDeactivatedAfterEdit();
                break;
            }
            case NavPointType.Chat:
            {
                string text = p.ChatCommand;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##navEdChat", ref text, 256)) p.ChatCommand = text;
                saved |= ImGui.IsItemDeactivatedAfterEdit();
                break;
            }
            case NavPointType.Recall:
            {
                int id = p.SpellId;
                ImGui.SetNextItemWidth(120);
                if (ImGui.InputInt("Spell id##navEdRecall", ref id) && id > 0) p.SpellId = id;
                saved |= ImGui.IsItemDeactivatedAfterEdit();
                ImGui.SameLine();
                ImGui.TextDisabled(SpellLabel(p.SpellId));
                break;
            }
            case NavPointType.PortalNPC:
            case NavPointType.Npc:
            case NavPointType.OpenVendor:
            {
                string name = p.TargetName;
                ImGui.SetNextItemWidth(260);
                if (ImGui.InputText("Target name##navEdTarget", ref name, 128)) p.TargetName = name;
                saved |= ImGui.IsItemDeactivatedAfterEdit();
                if (p.Type == NavPointType.PortalNPC)
                {
                    if (ImGui.Button("Use selected object##navEdTargetSel") && TryDescribeSelection(out string selName, out double ons, out double oew, out double oz))
                    {
                        p.TargetName = selName;
                        p.PortalExitNS = ons; p.PortalExitEW = oew; p.PortalExitZ = oz;
                        saved = true;
                    }
                }
                else
                {
                    ImGui.TextDisabled("Vendor / NPC steps are kept for VTank but not run by RynthAi nav.");
                }
                break;
            }
        }

        if (saved) AfterEdit();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Record tab: route recording + breadcrumb trail
    // ═══════════════════════════════════════════════════════════════════

    private void RenderRecordTab()
    {
        var o = _settings.NavOverlay;

        ImGui.TextColored(ColYellow, "Record route");
        bool rec = _settings.IsRecordingNav;
        ImGui.PushStyleColor(ImGuiCol.Button, rec ? new Vector4(0.55f, 0.10f, 0.10f, 1f) : new Vector4(0.15f, 0.30f, 0.45f, 1f));
        if (ImGui.Button(rec ? "Stop recording" : "Start recording", new Vector2(160, 26)))
            _settings.IsRecordingNav = !rec;
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.TextDisabled(rec ? $"+{Breadcrumbs?.RecordedCount ?? 0} steps this session" : "Walk the route; points are added for you.");

        ImGui.SetNextItemWidth(140);
        if (ImGui.SliderFloat("Point spacing (yd)##navRecSpacing", ref o.RecordSpacingYards, 1f, 30f, "%.1f")) o.Sanitize();
        ImGui.Checkbox("Add Portal step when I teleport through a selected portal##navRecPortal", ref o.RecordPortals);
        ImGui.BeginDisabled(!o.RecordPortals);
        ImGui.SetNextItemWidth(140);
        ImGui.InputInt("Pause after portal (ms)##navRecPause", ref o.RecordPauseAfterPortalMs, 250, 1000);
        ImGui.EndDisabled();
        o.Sanitize();
        ImGui.TextDisabled("Steps are appended to the current route and saved every 10 steps / when you stop.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(ColYellow, "Breadcrumb trail");
        ImGui.Checkbox("Track trail##navCrumbTrack", ref o.TrackBreadcrumbs);
        ImGui.SameLine();
        ImGui.Checkbox("Show trail##navCrumbShow", ref o.ShowBreadcrumbs);
        ImGui.SameLine();
        ImGui.Checkbox("Fade older##navCrumbFade", ref o.BreadcrumbFade);

        ImGui.SetNextItemWidth(140);
        ImGui.SliderFloat("Sample spacing (yd)##navCrumbSpacing", ref o.BreadcrumbSpacingYards, 0.5f, 20f, "%.1f");
        ImGui.SetNextItemWidth(140);
        ImGui.SliderInt("Max points##navCrumbMax", ref o.BreadcrumbMaxPoints, 20, 2000);
        ImGui.SetNextItemWidth(140);
        ImGui.SliderFloat("Draw range (yd)##navCrumbRange", ref o.BreadcrumbDrawRangeYards, 20f, 500f, "%.0f");
        o.Sanitize();

        var trail = Breadcrumbs?.Trail ?? Array.Empty<NavCrumb>();
        ImGui.TextDisabled($"{trail.Length} points, {Breadcrumbs?.TrailYards ?? 0:F0} yd walked");

        ImGui.BeginDisabled(Breadcrumbs == null);
        if (ImGui.Button("Clear trail")) { Breadcrumbs?.RequestClear(); SetStatus("Trail cleared."); }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(trail.Length < 2);
        if (ImGui.Button("Trail -> route")) ImGui.OpenPopup("NavTrailToRoute");
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Turn the trail (since the last teleport) into a new Once route, in walking order.");
        ImGui.SameLine();
        if (ImGui.Button("Backtrack route")) ImGui.OpenPopup("NavTrailBacktrack");
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Same, reversed: a Once route that walks you back the way you came.");
        ImGui.EndDisabled();

        TrailExportPopup("NavTrailToRoute", trail, reverse: false);
        TrailExportPopup("NavTrailBacktrack", trail, reverse: true);
    }

    private void TrailExportPopup(string id, NavCrumb[] trail, bool reverse)
    {
        if (!ImGui.BeginPopup(id)) return;
        ImGui.Text("Replace the loaded route with the trail?");
        ImGui.TextDisabled("The current file on disk is not changed; use Save As afterwards.");
        if (ImGui.Button("Create route##" + id))
        {
            var pts = NavBreadcrumbTracker.TrailToPoints(trail, _settings.NavOverlay.RecordSpacingYards, reverse);
            _settings.IsRecordingNav = false;
            _settings.CurrentRoute = new NavRouteParser { RouteType = NavRouteType.Once, Points = pts };
            _settings.CurrentNavPath = string.Empty;
            _settings.ActiveNavIndex = 0;
            _sel.Clear();
            _anchor = null;
            OnNavFilesChanged?.Invoke();
            OnSettingsChanged?.Invoke();
            SetStatus($"{(reverse ? "Backtrack" : "Trail")} route created: {pts.Count} points.");
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##" + id)) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Tools tab: simplify, height repair
    // ═══════════════════════════════════════════════════════════════════

    private void RenderToolsTab()
    {
        ImGui.TextColored(ColYellow, "Simplify");
        ImGui.SetNextItemWidth(140);
        ImGui.SliderFloat("Tolerance (yd)##navSimplifyTol", ref _simplifyYards, 0.2f, 10f, "%.1f");
        ImGui.SameLine();
        if (ImGui.Button("Simplify route"))
        {
            int removed = 0;
            int ai = _settings.ActiveNavIndex;
            NavPoint? keep = ai >= 0 && ai < Route.Points.Count ? Route.Points[ai] : null;
            Edit(l => { removed = NavRouteEditing.Simplify(l, _simplifyYards, keep); return l; });
            SetStatus($"Simplify removed {removed} points.");
        }
        ImGui.TextDisabled("Removes points that lie within the tolerance of a straight line between their\n" +
                           "neighbours (recorded routes get much shorter). Action steps are never touched.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(ColYellow, "Repair waypoint heights");
        ImGui.TextDisabled("Waypoints added from this window before RynthAi 0.6.30 stored the raw height\n" +
                           "instead of height / 240, so their rings float high above the ground.");
        if (ImGui.Button("Select raw-height steps"))
        {
            _sel.Clear();
            foreach (var p in Route.Points)
                if (Math.Abs(p.Z) > 12) _sel.Add(p); // > 2880 world height can only be a raw value
            SetStatus($"{_sel.Count} steps look like raw heights.");
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(_sel.Count == 0);
        if (ImGui.Button($"Divide Z by 240 ({_sel.Count} selected)"))
        {
            foreach (var p in _sel) p.Z /= NavCoordinateHelper.NavZScale;
            AfterEdit();
            SetStatus($"Fixed {_sel.Count} heights.");
        }
        ImGui.EndDisabled();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Display tab: markers, colours, HUD, labels, guide line
    // ═══════════════════════════════════════════════════════════════════

    private void RenderDisplayTab()
    {
        var o = _settings.NavOverlay;

        ImGui.TextColored(ColYellow, "Route markers");
        ImGui.Checkbox("Show route rings and lines##navShowMarkers", ref o.ShowRouteMarkers);
        ImGui.SetNextItemWidth(140);
        ImGui.SliderInt("Markers around active##navMaxMarkers", ref o.MaxRouteMarkers, 8, 128);
        ImGui.SetNextItemWidth(140);
        ImGui.SliderFloat("Ring thickness##navRingT", ref _settings.NavRingThickness, 1.0f, 16.0f, "%.0f");
        ImGui.SetNextItemWidth(140);
        ImGui.SliderFloat("Line thickness##navLineT", ref _settings.NavLineThickness, 1.0f, 16.0f, "%.0f");
        ImGui.SetNextItemWidth(140);
        ImGui.SliderFloat("Height offset##navHOff", ref _settings.NavHeightOffset, -5.0f, 5.0f, "%.2f");

        ColorEdit("Ring##navColRing", ref o.ColorRing);
        ImGui.SameLine();
        ColorEdit("Active ring##navColActive", ref o.ColorActiveRing);
        ImGui.SameLine();
        ColorEdit("Line##navColLine", ref o.ColorLine);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(ColYellow, "Waypoint overlay");
        ImGui.Checkbox("Waypoint HUD##navHud", ref o.ShowHud);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Movable box: active waypoint, distance, direction arrow, next step, remaining length.");
        ImGui.SameLine();
        ImGui.Checkbox("Only while navigating##navHudNav", ref o.HudOnlyWhileNavigating);
        ImGui.Checkbox("Waypoint labels##navLabels", ref o.ShowWaypointLabels);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("\"#12  34yd\" over the upcoming waypoints in the world.");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        ImGui.SliderInt("ahead##navLabelsAhead", ref o.LabelsAhead, 1, 10);
        ImGui.Checkbox("Guide line to active waypoint##navGuide", ref o.ShowGuideLine);
        ImGui.SameLine();
        ColorEdit("##navColGuide", ref o.ColorGuide);
        ImGui.Text("Trail colour");
        ImGui.SameLine();
        ColorEdit("##navColTrail", ref o.ColorBreadcrumb);

        ImGui.Spacing();
        if (ImGui.Button("Reset colours")) o.ResetColors();
        o.Sanitize();
    }

    /// <summary>ARGB colour swatch editor.</summary>
    private static void ColorEdit(string label, ref uint argb)
    {
        var v = new Vector4(((argb >> 16) & 0xFF) / 255f, ((argb >> 8) & 0xFF) / 255f, (argb & 0xFF) / 255f, ((argb >> 24) & 0xFF) / 255f);
        if (!ImGui.ColorEdit4(label, ref v, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar)) return;
        static uint B(float f) => (uint)Math.Clamp(MathF.Round(f * 255f), 0f, 255f);
        argb = (B(v.W) << 24) | (B(v.X) << 16) | (B(v.Y) << 8) | B(v.Z);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Actions
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>A Point at the player's position (Z in nav units), or false when the position is unknown.</summary>
    private bool TryHere(out NavPoint p)
    {
        p = new NavPoint();
        if (!NavCoordinateHelper.TryGetNavPosition(_host, out double ns, out double ew, out double z))
        {
            SetStatus("Position unavailable.");
            return false;
        }
        p.NS = ns; p.EW = ew; p.Z = z;
        return true;
    }

    private void AddRecall(int spellId)
    {
        if (!TryHere(out var p)) return;
        p.Type = NavPointType.Recall;
        p.SpellId = spellId;
        InsertPoint(p);
        RynthLog.Write(LogCat.Navigation, $"Added Recall: {SpellLabel(spellId)}");
    }

    /// <summary>Name + nav position of the selected world object.</summary>
    private bool TryDescribeSelection(out string name, out double ns, out double ew, out double navZ)
    {
        name = string.Empty; ns = ew = navZ = 0;
        if (!_host.HasGetSelectedItemId) { SetStatus("Selection unavailable."); return false; }
        uint sel = _host.GetSelectedItemId();
        if (sel == 0) { SetStatus("Select an object first."); return false; }
        if (!_host.HasGetObjectName || !_host.TryGetObjectName(sel, out name) || string.IsNullOrWhiteSpace(name))
        {
            SetStatus("Selected object has no name yet.");
            return false;
        }
        if (_host.HasGetObjectPosition
            && _host.TryGetObjectPosition(sel, out uint cell, out float x, out float y, out float z)
            && NavCoordinateHelper.TryConvertPoseToCoords(cell, x, y, out ns, out ew))
        {
            navZ = z / NavCoordinateHelper.NavZScale;
            return true;
        }
        SetStatus($"Could not read the position of '{name}'.");
        return false;
    }

    private void AddPortalFromSelection()
    {
        if (!TryDescribeSelection(out string name, out double ons, out double oew, out double oz)) return;
        if (!TryHere(out var p)) return;
        p.Type = NavPointType.PortalNPC;
        p.TargetName = name;
        p.ObjectClass = 14; // Decal ObjectClass.Portal, as VTank writes it
        p.PortalExitNS = ons; p.PortalExitEW = oew; p.PortalExitZ = oz;
        InsertPoint(p);
        SetStatus($"Added Portal step '{name}'.");
    }

    private void AddPointAtSelection()
    {
        if (!TryDescribeSelection(out string name, out double ns, out double ew, out double z)) return;
        InsertPoint(new NavPoint { Type = NavPointType.Point, NS = ns, EW = ew, Z = z });
        SetStatus($"Added point at '{name}'.");
    }

    private void MoveSelectedHere()
    {
        if (_sel.Count != 1 || !TryHere(out var here)) return;
        foreach (var p in _sel) { p.NS = here.NS; p.EW = here.EW; p.Z = here.Z; }
        AfterEdit();
        SetStatus("Step moved to your position.");
    }

    private void DuplicateSelected()
    {
        var copies = new List<NavPoint>();
        Edit(l => NavRouteEditing.Duplicate(l, _sel, copies));
        _sel.Clear();
        foreach (var c in copies) _sel.Add(c);
        SetStatus($"Duplicated {copies.Count} steps.");
    }

    private void DeleteSelected()
    {
        int n = _sel.Count;
        if (n == 0) return;
        Edit(l => NavRouteEditing.Remove(l, _sel));
        _sel.Clear();
        _anchor = null;
        SetStatus($"Deleted {n} steps.");
    }

    private void SetActive(int index)
    {
        if (index < 0 || index >= Route.Points.Count) return;
        _settings.ActiveNavIndex = index;
        OnSettingsChanged?.Invoke();
        SetStatus($"Active waypoint: {index}.");
    }

    /// <summary>Adds a step per the Insert mode (end, or above / below the selected step) and selects it.</summary>
    public void InsertPoint(NavPoint newPt)
    {
        int primary = PrimaryIndex();
        Edit(list =>
        {
            if (_addModeIdx == 1 && primary >= 0 && primary < list.Count) list.Insert(primary, newPt);
            else if (_addModeIdx == 2 && primary >= 0 && primary < list.Count) list.Insert(primary + 1, newPt);
            else list.Add(newPt);
            return list;
        });
        SelectOnly(newPt);
    }

    private void Edit(Func<List<NavPoint>, List<NavPoint>> edit)
    {
        NavRouteEditing.Apply(_settings, edit);
        AfterEdit();
    }

    private void AfterEdit()
    {
        TryAutoSaveNav();
        OnSettingsChanged?.Invoke();
    }

    public void TryAutoSaveNav()
    {
        if (!string.IsNullOrEmpty(_settings.CurrentNavPath))
        {
            try { _settings.CurrentRoute.Save(_settings.CurrentNavPath); } catch { }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Selection + labels
    // ═══════════════════════════════════════════════════════════════════

    private void SelectOnly(NavPoint p)
    {
        _sel.Clear();
        _sel.Add(p);
        _anchor = p;
    }

    /// <summary>Anchor (last clicked) when selected, else the lowest selected index, else -1.</summary>
    private int PrimaryIndex()
    {
        var pts = Route.Points;
        if (_anchor != null && _sel.Contains(_anchor))
        {
            int a = pts.IndexOf(_anchor);
            if (a >= 0) return a;
        }
        for (int i = 0; i < pts.Count; i++)
            if (_sel.Contains(pts[i])) return i;
        return -1;
    }

    /// <summary>Drops selected points that are no longer in the route (deleted, route swapped, recorder edits).</summary>
    private void PruneSelection()
    {
        if (_sel.Count == 0) return;
        var live = new HashSet<NavPoint>(Route.Points);
        _sel.RemoveWhere(p => !live.Contains(p));
        if (_anchor != null && !live.Contains(_anchor)) _anchor = null;
    }

    private void SetStatus(string text)
    {
        _status = text;
        _statusAt = Environment.TickCount64;
    }

    private static string SpellLabel(int id)
    {
        string name = SpellDatabase.HasSpell(id) ? SpellDatabase.GetSpellName(id) : string.Empty;
        return string.IsNullOrEmpty(name) ? $"Spell {id}" : $"{name} ({id})";
    }

    private static string StepLabel(NavPoint p) => p.Type switch
    {
        NavPointType.Point  => $"Point  {p.NS:F3}, {p.EW:F3}",
        NavPointType.Recall => $"Recall  {SpellLabel(p.SpellId)}",
        _                   => p.ToString(),
    };

    private static Vector4 TypeColor(NavPointType t) => t switch
    {
        NavPointType.Point      => new Vector4(0.85f, 0.90f, 0.95f, 1f),
        NavPointType.Recall     => new Vector4(0.75f, 0.55f, 1.00f, 1f),
        NavPointType.PortalNPC  => new Vector4(1.00f, 0.45f, 1.00f, 1f),
        NavPointType.Pause      => new Vector4(0.65f, 0.65f, 0.65f, 1f),
        NavPointType.Chat       => new Vector4(1.00f, 0.90f, 0.40f, 1f),
        _                       => new Vector4(1.00f, 0.65f, 0.30f, 1f),
    };
}
