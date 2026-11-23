// /ra vtimport — open the VirindiTank Import window (pick Monsters / Loot / Nav / Meta / Profiles folders).
// Settings → Game Client runs the same. /ra vtimport go imports from default VT folders without the window.
using System;
using RynthCore.Plugin.RynthAi.ProfileImport;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    private VTankImportWindow? _vtImportUi;

    /// <summary>Ensure the import window exists (after login / dashboard ready).</summary>
    private void EnsureVTankImportWindow()
    {
        if (_vtImportUi != null) return;
        _vtImportUi = new VTankImportWindow(
            () => _dashboard?.Settings,
            () => _dashboard?.CharFolder,
            () => _dashboard?.RefreshSettingsProfileList(),
            ChatLine,
            msg => Host.Log("[RynthAi] " + msg),
            () => _dashboard?.SaveSettings());
    }

    /// <summary>
    /// /ra vtimport — open the folder-picker window.
    /// /ra vtimport go [files] — import immediately from default VirindiTank roots (no window).
    /// </summary>
    private void HandleVTankImportCommand(string[] parts)
    {
        EnsureVTankImportWindow();
        // "go" / "now" / "run": headless import from default folders (scripts / old behaviour).
        bool go = parts.Length >= 3 && (
            parts[2].Equals("go", StringComparison.OrdinalIgnoreCase)
            || parts[2].Equals("now", StringComparison.OrdinalIgnoreCase)
            || parts[2].Equals("run", StringComparison.OrdinalIgnoreCase));
        if (!go)
        {
            _vtImportUi!.Show();
            ChatLine("[RynthAi] VirindiTank Import window opened — pick Monsters, Loot, Nav, Meta and Settings profiles folders, then Import.");
            return;
        }

        var dash = _dashboard;
        if (dash == null) { ReplyFail("[RynthAi] Settings not ready."); return; }

        // Optional "files" after go = copy only (no monster merge).
        bool merge = !(parts.Length >= 4 && parts[3].Equals("files", StringComparison.OrdinalIgnoreCase))
                     && !(parts.Length >= 3 && parts[2].Equals("files", StringComparison.OrdinalIgnoreCase));
        // /ra vtimport go files  OR legacy /ra vtimport files (treat as go files)
        if (parts.Length >= 3 && parts[2].Equals("files", StringComparison.OrdinalIgnoreCase))
            merge = false;

        Host.Log("[RynthAi] VTank import: scanning default VirindiTank folders"
                 + (merge ? " (loot, nav, meta, MyMonsters)." : " (loot, nav, meta files only)."));

        VirindiTankProfileImporter.Result result;
        try
        {
            result = VirindiTankProfileImporter.ImportAll(dash.Settings, mergeMonsterRules: merge);
        }
        catch (Exception ex)
        {
            ReplyFail("[RynthAi] VTank import failed: " + ex.Message);
            Host.Log("[RynthAi] VTank import exception: " + ex);
            return;
        }

        if (merge && (result.MonsterRulesAdded > 0 || result.RingSettingsSet.Length > 0))
        {
            try { dash.SaveSettings(); }
            catch (Exception ex) { Host.Log("[RynthAi] VTank import: save settings failed: " + ex.Message); }
        }

        ReplyOk("[RynthAi] " + result.SummaryLine());
        foreach (string line in result.LogLines)
            Host.Log("[RynthAi] VTank import: " + line);
        if (result.LootFilesCopied + result.NavFilesCopied + result.MetaFilesCopied + result.MonsterRulesAdded == 0)
            ChatLine("[RynthAi] Nothing new found. Put VirindiTank under C:\\Games\\VirindiPlugins\\VirindiTank (or use the Import window to pick folders).");
    }

    /// <summary>Render thread: the VirindiTank Import folder window.</summary>
    private void RenderVTankImportWindow()
    {
        try { _vtImportUi?.Render(); }
        catch (Exception ex) { Host.Log("[RynthAi] VTank import window: " + ex.Message); }
    }
}
