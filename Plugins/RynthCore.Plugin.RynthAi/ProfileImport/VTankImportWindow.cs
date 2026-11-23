// VTankImportWindow.cs — ImGui window: pick VirindiTank source folders then Import.
// Rows: Monsters, Loot, Nav, Meta, and Settings profiles (.usd → SettingsProfiles).
// Opened from Settings → Game Client → VirindiTank → Import…, or /ra vtimport.
// Render thread only.
using System;
using System.IO;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.ProfileImport;

internal sealed class VTankImportWindow
{
    private const string Title = "VirindiTank Import##rynthvtimport";

    private readonly SavedWindow _spot = new("RynthAi.VTankImport");
    private readonly Func<LegacyUiSettings?> _settings;
    private readonly Func<string?> _charFolder;
    private readonly Action? _refreshProfiles;
    private readonly Action<string> _chat;
    private readonly Action<string> _log;
    private readonly Action? _afterImportSave;

    private bool _open;
    private string _monsters = string.Empty;
    private string _loot = string.Empty;
    private string _nav = string.Empty;
    private string _meta = string.Empty;
    private string _profiles = string.Empty;
    private bool _mergeMonsters = true;
    private string _status = string.Empty;
    private bool _busy;

    public VTankImportWindow(
        Func<LegacyUiSettings?> settings,
        Func<string?> charFolder,
        Action? refreshProfiles,
        Action<string> chat,
        Action<string> log,
        Action? afterImportSave = null)
    {
        _settings = settings;
        _charFolder = charFolder;
        _refreshProfiles = refreshProfiles;
        _chat = chat;
        _log = log;
        _afterImportSave = afterImportSave;
    }

    public bool IsOpen => _open;

    /// <summary>Show the window; fill blank rows from the first existing VirindiTank install folder.</summary>
    public void Show()
    {
        _open = true;
        _status = string.Empty;
        PrefillDefaultsIfEmpty();
    }

    public void Hide() => _open = false;

    /// <summary>Render thread: draw while open.</summary>
    public void Render()
    {
        if (!_open) return;

        ImGui.SetNextWindowSize(new Vector2(680, 380), ImGuiCond.FirstUseEver);
        bool open = _open;
        _spot.Apply();
        if (!ImGui.Begin(Title, ref open, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            if (!open) _open = false;
            return;
        }
        _spot.Track();
        if (!open) { _open = false; ImGui.End(); return; }

        SafeImGui.TextWrapped(
            "Choose where to import from. Each row is optional (blank = skip that kind). "
            + "Browse opens a folder picker. Defaults fill from C:\\Games\\VirindiPlugins\\VirindiTank when it exists.");

        ImGui.Spacing();
        FolderRow("Monsters (.usd / monsters.json)", ref _monsters, "Choose Monsters folder");
        FolderRow("Loot profiles (.utl)", ref _loot, "Choose Loot profiles folder");
        FolderRow("Nav profiles (.nav)", ref _nav, "Choose Nav folder");
        FolderRow("Meta files (.met)", ref _meta, "Choose Meta folder");
        FolderRow("Settings profiles (.usd → Profiles)", ref _profiles, "Choose VTank profiles folder");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Each top-level .usd becomes a named settings profile under this character's SettingsProfiles "
                + "(MyMonsters + ring options on a copy of your current settings). Does not switch the active profile.");

        ImGui.Spacing();
        ImGui.Checkbox("Merge MyMonsters into this character's combat settings", ref _mergeMonsters);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Off: copy loot / nav / meta files only (same as /ra vtimport files). Settings-profile import still runs when that folder is set.");

        ImGui.Spacing();
        ImGui.BeginDisabled(_busy);
        if (ImGui.Button("Fill from VirindiTank default"))
        {
            string? root = FirstDefaultRoot();
            if (root == null)
                _status = "No VirindiTank folder found under Games / Documents.";
            else
            {
                _monsters = _loot = _nav = _meta = _profiles = root;
                _status = "Filled from " + root;
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Import"))
            RunImport();
        ImGui.SameLine();
        if (ImGui.Button("Close"))
            _open = false;
        ImGui.EndDisabled();

        if (_status.Length > 0)
        {
            ImGui.Spacing();
            SafeImGui.TextWrapped(_status);
        }

        ImGui.End();
    }

    private void FolderRow(string label, ref string path, string browseTitle)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        ImGui.SameLine(280f);
        ImGui.SetNextItemWidth(Math.Max(160f, ImGui.GetContentRegionAvail().X - 80f));
        string id = "##vt_" + label;
        ImGui.InputText(id, ref path, 512u);
        ImGui.SameLine();
        if (ImGui.SmallButton("Browse##" + label))
        {
            string? start = Directory.Exists(path) ? path : FirstDefaultRoot();
            string? picked = FolderPicker.Pick(browseTitle, start);
            if (picked != null) path = picked;
        }
    }

    private void PrefillDefaultsIfEmpty()
    {
        if (_monsters.Length + _loot.Length + _nav.Length + _meta.Length + _profiles.Length > 0) return;
        string? root = FirstDefaultRoot();
        if (root == null) return;
        _monsters = _loot = _nav = _meta = _profiles = root;
    }

    private static string? FirstDefaultRoot()
    {
        foreach (string root in VirindiTankProfileImporter.DefaultVirindiTankRoots())
            if (Directory.Exists(root)) return root;
        const string games = @"C:\Games\VirindiPlugins\VirindiTank";
        return Directory.Exists(games) ? games : null;
    }

    private void RunImport()
    {
        if (_busy) return;
        var sources = new VirindiTankProfileImporter.ImportSources
        {
            MonstersFolder = BlankToNull(_monsters),
            LootFolder = BlankToNull(_loot),
            NavFolder = BlankToNull(_nav),
            MetaFolder = BlankToNull(_meta),
            ProfilesFolder = BlankToNull(_profiles),
        };
        if (sources.MonstersFolder == null && sources.LootFolder == null
            && sources.NavFolder == null && sources.MetaFolder == null
            && sources.ProfilesFolder == null)
        {
            _status = "Pick at least one folder (or Fill from VirindiTank default).";
            return;
        }

        _busy = true;
        _status = "Importing…";
        try
        {
            LegacyUiSettings? settings = _settings();
            string? charFolder = _charFolder();
            var result = VirindiTankProfileImporter.ImportFromSources(
                sources, settings, _mergeMonsters, charFolder);

            if (_mergeMonsters && settings != null
                && (result.MonsterRulesAdded > 0 || result.RingSettingsSet.Length > 0))
            {
                try { _afterImportSave?.Invoke(); }
                catch (Exception ex) { _log("VTank import: save settings failed: " + ex.Message); }
            }

            if (result.SettingsProfilesWritten > 0)
            {
                try { _refreshProfiles?.Invoke(); }
                catch (Exception ex) { _log("VTank import: refresh profiles failed: " + ex.Message); }
            }

            _status = result.SummaryLine();
            _chat("[RynthAi] " + result.SummaryLine());
            foreach (string line in result.LogLines)
                _log("VTank import: " + line);
            if (result.LootFilesCopied + result.NavFilesCopied + result.MetaFilesCopied
                + result.MonsterRulesAdded + result.SettingsProfilesWritten == 0)
                _status += " Nothing new found in the chosen folders.";
        }
        catch (Exception ex)
        {
            _status = "Import failed: " + ex.Message;
            _chat("[RynthAi] VTank import failed: " + ex.Message);
            _log("VTank import exception: " + ex);
        }
        finally
        {
            _busy = false;
        }
    }

    private static string? BlankToNull(string s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
