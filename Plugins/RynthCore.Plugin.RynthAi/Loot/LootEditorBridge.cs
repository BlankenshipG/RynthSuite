using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using RynthCore.Loot.Editing;

namespace RynthCore.Plugin.RynthAi.Loot;

/// <summary>
/// RynthAi's side of the engine's ImGui Loot Editor (RynthCore
/// docs/IMGUI_LOOT_EDITOR.md): one LootEditSession, the profile paths, and the
/// hot reload after a save. The engine calls the exports (PluginExports, "Loot
/// editor bridge") from its UI data hub on the plugin pump thread, the same
/// thread as the plugin tick; the lock only guards against any other caller.
/// Nothing here touches AC.
/// </summary>
internal sealed class LootEditorBridge
{
    public const string LootFolder = @"C:\Games\RynthSuite\RynthAi\LootProfiles";

    private readonly object _sync = new();
    private readonly LootEditSession _session = new();
    private readonly Func<string> _inUsePath;
    private readonly Action<string> _onSaved;
    private readonly string[] _vendorFolders;

    // Follow RynthAi's loot profile while the editor shows it and has no edits.
    private bool _followInUse = true;
    private long _nextDiskCheck;
    private string? _vocabJson;

    public LootEditorBridge(Func<string> inUsePath, Action<string> onSaved, params string[] vendorFolders)
    {
        _inUsePath = inUsePath;
        _onSaved = onSaved;
        _vendorFolders = vendorFolders;
    }

    private string InUse => (_inUsePath() ?? string.Empty).Trim().Trim('"');

    /// <summary>
    /// Cheap: the session's revision. Every ~2 s it also looks at the file on
    /// disk and follows a change of RynthAi's loot profile.
    /// </summary>
    public int Revision()
    {
        lock (_sync)
        {
            long now = Stopwatch.GetTimestamp();
            if (now >= _nextDiskCheck)
            {
                _nextDiskCheck = now + Stopwatch.Frequency * 2;
                _session.CheckDisk();
                string inUse = InUse;
                if (_followInUse && !_session.IsDirty && inUse.Length > 0 && !LootEditSession.SamePath(inUse, _session.Path))
                    _session.Open(inUse);
            }
            return unchecked((int)_session.Revision);
        }
    }

    public string StateJson()
    {
        lock (_sync)
        {
            string inUse = InUse;
            if (!_session.IsOpen && _followInUse && inUse.Length > 0) _session.Open(inUse);
            LootEditState state = _session.BuildState(inUse, ListFiles());
            return JsonSerializer.Serialize(state, LootEditJsonContext.Default.LootEditState);
        }
    }

    public string RuleJson(int index)
    {
        lock (_sync)
        {
            LootEditRule? rule = _session.BuildRule(index);
            return rule == null ? "{\"Index\":-1}" : JsonSerializer.Serialize(rule, LootEditJsonContext.Default.LootEditRule);
        }
    }

    public string VocabJson() =>
        _vocabJson ??= JsonSerializer.Serialize(LootRuleText.BuildVocab(), LootEditJsonContext.Default.LootEditVocab);

    public void Command(string json)
    {
        LootEditCommand? cmd = JsonSerializer.Deserialize(json, LootEditJsonContext.Default.LootEditCommand);
        if (cmd == null) return;
        string? saved = null;
        lock (_sync)
        {
            if (cmd.Op == "open")
            {
                string inUse = InUse;
                string path = cmd.Path.Trim().Trim('"');
                if (path.Length == 0) path = inUse;
                // Showing the panel again on the profile already open keeps its edits.
                if (_session.IsOpen && !cmd.Force && LootEditSession.SamePath(path, _session.Path))
                    return;
                cmd.Path = path;
                if (path.Length == 0)
                {
                    _followInUse = true;
                    _session.Apply(cmd);   // "No loot profile selected."
                    return;
                }
                if (_session.Apply(cmd) == LootEditSession.Outcome.Opened)
                    _followInUse = LootEditSession.SamePath(path, inUse);
                return;
            }
            if (_session.Apply(cmd) == LootEditSession.Outcome.Saved)
                saved = _session.Path;
        }
        if (saved != null) _onSaved(saved);
    }

    /// <summary>LootProfiles (*.utl, *.json), then AutoVendor profiles, then the open file if it is elsewhere.</summary>
    private List<LootEditFile> ListFiles()
    {
        var files = new List<LootEditFile>();
        try
        {
            if (Directory.Exists(LootFolder))
            {
                var names = new List<string>();
                names.AddRange(Directory.GetFiles(LootFolder, "*.utl"));
                names.AddRange(Directory.GetFiles(LootFolder, "*.json"));
                names.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string f in names) files.Add(new LootEditFile { Path = f, Display = Path.GetFileName(f) });
            }
            foreach (string folder in _vendorFolders)
            {
                if (!Directory.Exists(folder)) continue;
                var names = new List<string>(Directory.GetFiles(folder, "*.utl", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2 }));
                names.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string f in names)
                    files.Add(new LootEditFile { Path = f, Display = "AutoVendor: " + Path.GetRelativePath(folder, f) });
            }
        }
        catch { }
        if (_session.IsOpen && !files.Exists(f => LootEditSession.SamePath(f.Path, _session.Path)))
            files.Add(new LootEditFile { Path = _session.Path, Display = Path.GetFileName(_session.Path) });
        return files;
    }
}
