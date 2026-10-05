using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthAi.Meta;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

/// <summary>
/// Loads a .met / .af meta into the settings (rules, embedded navs, start state) for the
/// meta bridge and /ra meta. (The plugin-drawn Macro Rules window is gone; the engine's
/// Meta panel edits rules through the meta bridge.)
/// </summary>
internal sealed class LegacyMetaUi
{
    private readonly LegacyUiSettings _settings;
    private string _statusMessage = "";

    /// <summary>Warnings from the most recent LoadMacroFile, so the Avalonia
    /// bridge can surface them too (it doesn't see the LoadedMeta).</summary>
    internal IReadOnlyList<string> LastLoadWarnings { get; private set; } = System.Array.Empty<string>();

    public LegacyMetaUi(LegacyUiSettings settings)
    {
        _settings = settings;
    }

    /// <summary>Loads a .met/.af (empty path clears). False when nothing was loaded; <see cref="LastLoadStatus"/> says why.</summary>
    internal bool LoadMacroFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            lock (_settings.MetaRulesLock)
            {
                _settings.MetaRules.Clear();
                _settings.EmbeddedNavs.Clear();
            }
            _settings.CurrentMetaPath = string.Empty;
            _settings.CurrentState = "Default";
            _settings.ForceStateReset = true;
            _statusMessage = "Macro cleared.";
            return true;
        }

        try
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            LoadedMeta loaded;

            if (ext == ".met")
                loaded = MetFileParser.Load(filePath);
            else if (ext == ".af")
                loaded = AfFileParser.Load(filePath);
            else
            {
                _statusMessage = "Unsupported file type";
                return false;
            }

            LastLoadWarnings = loaded.Warnings;
            if (loaded.Rules.Count == 0)
            {
                _statusMessage = loaded.Warnings.Count > 0
                    ? $"No rules parsed — {loaded.Warnings.Count} warning(s): {loaded.Warnings[0]}"
                    : "No rules found (profile?)";
                return false;
            }

            lock (_settings.MetaRulesLock)
            {
                _settings.MetaRules = loaded.Rules;
                _settings.EmbeddedNavs.Clear();
                foreach (var kvp in loaded.EmbeddedNavs)
                    _settings.EmbeddedNavs[kvp.Key] = kvp.Value;
            }
            _settings.CurrentState = loaded.StartState;
            _settings.ForceStateReset = true;
            _settings.MetaCallStackReset = true;
            _settings.CurrentMetaPath = filePath;

            string name = Path.GetFileNameWithoutExtension(filePath);
            int stateCount = loaded.Rules.Select(r => r.State).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            _statusMessage = loaded.Warnings.Count == 0
                ? $"Loaded {loaded.Rules.Count} rules / {stateCount} states / {loaded.EmbeddedNavs.Count} navs from {name}"
                : $"Loaded {loaded.Rules.Count} rules / {stateCount} states from {name} — {loaded.Warnings.Count} warning(s): {loaded.Warnings[0]}";
            return true;
        }
        catch (Exception ex)
        {
            _statusMessage = $"Load error: {ex.Message}";
            return false;
        }
    }

    /// <summary>The status line of the last load (why it failed, or what it loaded).</summary>
    internal string LastLoadStatus => _statusMessage ?? string.Empty;
}
