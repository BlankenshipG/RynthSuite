using System;
using System.IO;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

/// <summary>
/// Route file helpers behind the nav bridge and /ra nav: save, autosave, and the active
/// waypoint after a delete. (The plugin-drawn Navigation window is gone; the engine's Nav
/// panel edits the route through LegacyDashboardRenderer.HandleNavCommand.)
/// </summary>
internal sealed class LegacyNavigationUi
{
    private readonly LegacyUiSettings _settings;
    private readonly RynthCoreHost _host;

    public LegacyNavigationUi(LegacyUiSettings settings, RynthCoreHost host)
    {
        _settings = settings;
        _host = host;
    }

    /// <summary>
    /// The active waypoint after deleting waypoint <paramref name="deleted"/> (the route now
    /// has <paramref name="newCount"/> points). Deleting the active one moves on to the one
    /// that followed it; this used to jump back to [0], which on a Once route re-ran its
    /// opening recall/portal and could walk the bot back across the map.
    /// </summary>
    internal static int IndexAfterDelete(int active, int deleted, int newCount)
    {
        if (newCount <= 0) return 0;
        if (active > deleted) active--;
        return Math.Clamp(active, 0, newCount - 1);
    }

    public const string NavFolder = @"C:\Games\RynthSuite\RynthAi\NavProfiles";

    public void TryAutoSaveNav()
    {
        string path = _settings.CurrentNavPath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (TryGetEmbeddedKey(path, out string key))
                _settings.EmbeddedNavs[key] = _settings.CurrentRoute.ToLines();
            else
                _settings.CurrentRoute.Save(path);
        }
        catch (Exception ex) { _host.Log($"[RynthAi] Nav auto-save to {path} failed: {ex.Message}"); }
    }

    /// <summary>
    /// Saves the current route and returns the line to show in chat. With a name it
    /// writes NavProfiles\&lt;name&gt;.nav and makes that the current nav. Without one it
    /// writes the current nav file; a nav from the loaded meta is updated in the meta
    /// (saving the meta keeps it); a route never saved gets a new "Route N" file.
    /// These used to do nothing: the button only wrote to an existing file, and a
    /// meta's nav path ("&lt;embedded:name&gt;") failed without a word.
    /// </summary>
    public string SaveRoute(string? name = null)
    {
        NavRouteParser route = _settings.CurrentRoute;
        string path = _settings.CurrentNavPath ?? string.Empty;
        try
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                string file = name.Trim();
                if (file.EndsWith(".nav", StringComparison.OrdinalIgnoreCase)) file = file[..^4].TrimEnd();
                if (file.Length == 0 || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    return $"[RynthAi] '{name}' can't be used as a file name.";
                path = Path.Combine(NavFolder, file + ".nav");
            }
            else if (TryGetEmbeddedKey(path, out string key))
            {
                _settings.EmbeddedNavs[key] = route.ToLines();
                return $"[RynthAi] '{key}' is a nav inside the loaded meta; updated it there ({route.Points.Count} waypoints). Save the meta to keep it, or /ra nav save <name> for a separate file.";
            }
            else if (path.Length == 0)
            {
                if (route.Points.Count == 0) return "[RynthAi] No waypoints to save.";
                int n = 1;
                do path = Path.Combine(NavFolder, $"Route {n++}.nav"); while (File.Exists(path));
            }

            route.Save(path);
            _settings.CurrentNavPath = path;
            return $"[RynthAi] Saved {route.Points.Count} waypoints to {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            _host.Log($"[RynthAi] Nav save to {path} failed: {ex}");
            return $"[RynthAi] Nav save failed: {ex.Message}";
        }
    }

    private static bool TryGetEmbeddedKey(string path, out string key)
    {
        key = string.Empty;
        if (!path.StartsWith("<embedded:", StringComparison.Ordinal) || !path.EndsWith('>')) return false;
        key = path.Substring(10, path.Length - 11);
        return key.Length > 0;
    }
}
