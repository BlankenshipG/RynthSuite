// RynthAiPlugin.EngineServers.cs - handing the Infinite Leaftide world check to RynthCore.
//
// A RynthCore that sends server verdicts (the "servermenu" push with V lines, 2026.10.5.46+)
// owns the ILT world list (engine.json IltServerNames) and the per-character Auto / On / Off
// override. Once it does, RynthAi copies its own settings over once:
//   * Settings > Misc "Server Features" patterns that aren't the defaults ->
//     /rc server names ilt add <pattern> (once per pattern, across characters);
//   * the Settings > Misc override or the Mini Remote's "Treat this world as ILT" while RynthCore
//     is on Auto -> /rc server ilt on (once per character).
// What was copied is remembered in %LOCALAPPDATA%\RynthCore\rynthai-engine-servers.txt, so a
// later "auto" set in RynthCore isn't undone. The check and the commands run on the plugin tick.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    private const string EngineServersFile = "rynthai-engine-servers.txt";
    private const int MaxMigratedPattern = 64;

    private const long MigrationCheckMs = 2000;

    private readonly ConcurrentQueue<string> _engineCommands = new();
    // Checked this session (reset at logout, so the next character is checked too). Plugin tick only.
    private bool _engineMigrationChecked;
    private long _engineMigrationNextAt;

    /// <summary>Plugin tick: copies RynthAi's ILT settings over once RynthCore decides (once a session).</summary>
    private void CheckEngineMigration()
    {
        if (_engineMigrationChecked || !_loginComplete) return;
        long now = Environment.TickCount64;
        if (now < _engineMigrationNextAt) return;
        _engineMigrationNextAt = now + MigrationCheckMs;
        if (!IltHub.IltServerOptions.EngineDecides) return;
        var settings = _dashboard?.Settings;
        if (settings == null) return;
        string character = CurrentCharacterName();
        string world = IltHub.ServerFeatureGate.CurrentWorld;
        if (character.Length == 0 || world.Length == 0) return;
        _engineMigrationChecked = true;

        try
        {
            string path = EngineServersPath();
            HashSet<string> done = LoadMigrated(path);
            var added = new List<string>();

            foreach (string pattern in PatternsToCopy(settings.FeatureServerNames))
            {
                string key = "names\t" + pattern.ToLowerInvariant();
                if (!done.Add(key)) continue;
                _engineCommands.Enqueue("/rc server names " + IltHub.IltServerOptions.EngineServerId + " add " + pattern);
                added.Add(key);
            }

            bool localForce = settings.ForceServerFeatures || _iltHub?.LocalForce == true;
            string engineForce = Huds.ServerMenuModel.Server(IltHub.IltServerOptions.EngineServerId)?.Force ?? "auto";
            string forceKey = "force\t" + world.ToLowerInvariant() + "\t" + character.ToLowerInvariant();
            if (localForce && engineForce.Equals("auto", StringComparison.OrdinalIgnoreCase) && done.Add(forceKey))
            {
                _engineCommands.Enqueue("/rc server " + IltHub.IltServerOptions.EngineServerId + " on");
                added.Add(forceKey);
            }

            if (added.Count > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllLines(path, added);
                Log($"[RynthAi] Copied {added.Count} ILT server setting(s) to RynthCore.");
            }
        }
        catch (Exception ex)
        {
            Log($"[RynthAi] ILT settings copy to RynthCore failed: {ex.Message}");
        }
    }

    /// <summary>Plugin tick: the migration check, then the queued RynthCore commands through the chat parser.</summary>
    private void DrainEngineCommands()
    {
        CheckEngineMigration();
        while (_engineCommands.TryDequeue(out string? command))
        {
            if (Host.HasInvokeChatParser && Host.InvokeChatParser(command)) continue;
            Log($"[RynthAi] RynthCore didn't take: {command}");
        }
    }

    /// <summary>
    /// The non-default patterns of <paramref name="patterns"/> that fit a chat command: one line, no
    /// control characters, at most <see cref="MaxMigratedPattern"/> characters (engine.json's cap).
    /// </summary>
    internal static IEnumerable<string> PatternsToCopy(IEnumerable<string>? patterns)
    {
        if (patterns == null) yield break;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in patterns)
        {
            string p = (raw ?? string.Empty).Trim();
            if (p.Length == 0 || p.Length > MaxMigratedPattern || p.Any(char.IsControl)) continue;
            if (IltHub.ServerFeatureGate.DefaultServerNames.Contains(p, StringComparer.OrdinalIgnoreCase)) continue;
            if (seen.Add(p)) yield return p;
        }
    }

    private static string EngineServersPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RynthCore", EngineServersFile);

    private static HashSet<string> LoadMigrated(string path)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return set;
        foreach (string line in File.ReadLines(path))
            if (line.Length > 0 && line.Length < 512) set.Add(line);
        return set;
    }
}
