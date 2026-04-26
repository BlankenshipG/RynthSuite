using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace RynthCore.MonsterEditor;

public partial class MainWindow : Window
{
    public ObservableCollection<MonsterRule> Rules         { get; } = new();
    public ObservableCollection<WeaponOption> WeaponOptions { get; } = new();

    public string[] DmgOptions { get; } =
        { "Auto", "Slash", "Pierce", "Bludgeon", "Fire", "Cold", "Lightning", "Acid", "Nether" };
    public string[] VulnOptions { get; } =
        { "None", "Slash", "Pierce", "Bludgeon", "Fire", "Cold", "Lightning", "Acid", "Nether" };
    public string[] PetOptions { get; } =
        { "PAuto", "Slash", "Pierce", "Bludgeon", "Fire", "Cold", "Lightning", "Acid", "Nether" };

    private string _charFolder   = string.Empty;
    private string _monstersPath = string.Empty;
    private bool   _initialized;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        // Explicit save for users who expect Ctrl+S (in addition to auto-save on edit).
        KeyDown += (_, e) =>
        {
            if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.S)
            {
                Save();
                e.Handled = true;
            }
        };

        Opened += MainWindow_Opened;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void MainWindow_Opened(object? sender, EventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        string[] args = Environment.GetCommandLineArgs();
        if (args.Length > 2)
            OpenCharFolder(args[1].Trim('"'), args[2].Trim('"'));
        else if (args.Length > 1)
            OpenCharFolder(args[1].Trim('"'), null);
        else
            await PromptOpenFileAsync();
    }

    // ── Open / Load ──────────────────────────────────────────────────────────

    private async System.Threading.Tasks.Task PromptOpenFileAsync()
    {
        var sp = StorageProvider;
        if (sp == null) return;

        var pick = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title                  = "Open monsters.json",
            AllowMultiple          = false,
            FileTypeFilter         = new[]
            {
                new FilePickerFileType("Monster Rules") { Patterns = new[] { "monsters.json" } },
                new FilePickerFileType("All Files")     { Patterns = new[] { "*" } },
            }
        });

        var file = pick.FirstOrDefault();
        if (file == null)
        {
            SetStatus("No file opened.", error: true);
            return;
        }

        string? path = file.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            OpenCharFolder(Path.GetDirectoryName(path)!, null);
    }

    /// <param name="settingsCharFolder">Per-character folder under SettingsProfiles (weapon ids from profile JSON).</param>
    /// <param name="monsterJsonPath">Full path to MonsterProfiles JSON when launched from RynthAi; null derives MonsterProfiles from the settings folder layout or falls back to monsters.json.</param>
    private void OpenCharFolder(string settingsCharFolder, string? monsterJsonPath)
    {
        _charFolder = settingsCharFolder;
        if (!string.IsNullOrEmpty(monsterJsonPath))
        {
            _monstersPath = monsterJsonPath;
        }
        else
        {
            // RynthAi layout: ...\RynthAi\SettingsProfiles\ACEmulator\<Char> → MonsterProfiles\<Char>.json
            try
            {
                string key = Path.GetFileName(settingsCharFolder.TrimEnd(Path.DirectorySeparatorChar));
                string rynthAiRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(settingsCharFolder)! )! )!;
                _monstersPath = Path.Combine(rynthAiRoot, "MonsterProfiles", key + ".json");
            }
            catch
            {
                _monstersPath = Path.Combine(settingsCharFolder, "monsters.json");
            }
        }

        Title = $"RynthAi — Monster Rules — {Path.GetFileName(settingsCharFolder)}";

        LoadWeaponOptions();
        LoadRules();
    }

    // Saves monsters.json / MonsterProfiles file (same as auto-save on edit).
    private void SaveToolbar_Click(object? sender, RoutedEventArgs e) => Save();

    /// <summary>Merge rules from a Virindi Tank <c>.usd</c> user settings file (see <see cref="VirindiTankUsdImport"/>).</summary>
    private async void ImportVirindiUsd_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_monstersPath))
        {
            SetStatus("Open or create a character monster profile first.", error: true);
            return;
        }

        var sp = StorageProvider;
        if (sp == null) return;

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Virindi Tank user profile (.usd)",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Virindi user settings") { Patterns = ["*.usd", "*.USD"] },
                new FilePickerFileType("All") { Patterns = ["*"] }
            ],
        });
        var f = files.FirstOrDefault();
        if (f == null) { SetStatus("Import cancelled.", error: true); return; }
        string? local = f.Path.LocalPath;
        if (string.IsNullOrEmpty(local)) { SetStatus("No local path for file.", error: true); return; }

        VirindiTankUsdImport.Result r = VirindiTankUsdImport.TryImport(local);
        if (!r.Ok || r.Rules.Count == 0)
        {
            SetStatus(r.Message, error: true);
            return;
        }

        MergeVirindiImportedRows(r.Rules, out int added, out int mergedDefault);
        EnsureDefault();
        Save();
        string tail = added > 0
            ? $" Merged: +{added} new row(s).{(mergedDefault > 0 ? " Default row combat settings were updated from VT default." : "")}"
            : (mergedDefault > 0 ? " Default row was updated from VT; no new named rows (duplicates skipped). " : " No new rows (duplicates skipped). ");
        SetStatus(r.Message + tail, error: false);
    }

    private void MergeVirindiImportedRows(IReadOnlyList<MonsterRule> src, out int added, out int mergedDefault)
    {
        added         = 0;
        mergedDefault = 0;

        MonsterRule? srcDefault = src.FirstOrDefault(x => x.IsDefault);
        if (srcDefault != null && Rules.Count > 0 && Rules[0].IsDefault)
        {
            Rules[0].CopyCombatSettingsFrom(srcDefault);
            mergedDefault = 1;
        }

        var nameSet = new HashSet<string>(Rules.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
        var exprSet = new HashSet<string>(
            Rules.Where(x => !string.IsNullOrWhiteSpace(x.MatchExpression)).Select(x => x.MatchExpression), StringComparer.Ordinal);
        foreach (var row in src)
        {
            if (row.IsDefault) continue;
            if (!string.IsNullOrEmpty(row.Name))
            {
                if (nameSet.Contains(row.Name)) continue;
            }
            else if (!string.IsNullOrEmpty(row.MatchExpression))
            {
                if (exprSet.Contains(row.MatchExpression)) continue;
            }
            else
            {
                continue;
            }

            var c = row.Clone();
            if (string.IsNullOrEmpty(c.Name) && !string.IsNullOrEmpty(c.MatchExpression))
            {
                // Row shows blank name but expression; keep empty so RynthAi does expression-only (see CombatManager.GetRuleForTarget).
            }
            else
            {
                if (!nameSet.Add(c.Name)) continue;
            }

            if (!string.IsNullOrEmpty(c.MatchExpression)) exprSet.Add(c.MatchExpression);
            AddRule(c);
            added++;
        }
    }

    private void LoadWeaponOptions()
    {
        WeaponOptions.Clear();
        WeaponOptions.Add(new WeaponOption { Id = 0, DisplayName = "<AUTO>" });

        try
        {
            string markerPath = Path.Combine(_charFolder, "active_profile.txt");
            string profile    = File.Exists(markerPath) ? File.ReadAllText(markerPath).Trim() : "Default";
            string profilePath = Path.Combine(_charFolder, profile + ".json");

            if (File.Exists(profilePath))
            {
                string json     = File.ReadAllText(profilePath);
                var    settings = JsonSerializer.Deserialize(json, EditorJsonContext.Default.EditorSettings);
                if (settings?.ItemRules != null)
                {
                    foreach (var item in settings.ItemRules)
                        WeaponOptions.Add(new WeaponOption { Id = item.Id, DisplayName = item.Name });
                }
            }
        }
        catch { /* weapon dropdowns will just show <AUTO> */ }

        IdToNameConverter.Instance.Options    = WeaponOptions;
        OptionFromIdConverter.Instance.Options = WeaponOptions;
    }

    private void LoadRules()
    {
        foreach (var r in Rules) r.PropertyChanged -= Rule_Changed;
        Rules.Clear();

        if (File.Exists(_monstersPath))
        {
            try
            {
                string json  = File.ReadAllText(_monstersPath);
                var    rules = JsonSerializer.Deserialize(json, EditorJsonContext.Default.ListMonsterRule);
                if (rules != null)
                    foreach (var r in rules) AddRule(r);
            }
            catch (Exception ex) { SetStatus($"Load error: {ex.Message}", error: true); return; }
        }

        EnsureDefault();

        SetStatus(Rules.Count > 0
            ? $"Loaded {Rules.Count} rule(s) from {_monstersPath}"
            : $"No monsters.json found — starting empty. Saving will create {_monstersPath}");
    }

    private void EnsureDefault()
    {
        for (int i = Rules.Count - 1; i >= 1; i--)
            if (Rules[i].IsDefault) { Rules[i].PropertyChanged -= Rule_Changed; Rules.RemoveAt(i); }

        if (Rules.Count > 0 && Rules[0].IsDefault)
            return;

        int existing = -1;
        for (int i = 0; i < Rules.Count; i++)
            if (Rules[i].IsDefault) { existing = i; break; }

        if (existing > 0)
            Rules.Move(existing, 0);
        else
        {
            // RynthAi CombatManager falls back to Name == "Default" when no other row matches the target.
            Rules.Insert(0, new MonsterRule
            {
                Name = "Default",
                Priority   = 1,
                DamageType = "Auto",
                Category   = string.Empty,
                MatchExpression = string.Empty,
            });
        }

        Rules[0].PropertyChanged -= Rule_Changed;
        Rules[0].PropertyChanged += Rule_Changed;
    }

    private void AddRule(MonsterRule rule)
    {
        rule.PropertyChanged += Rule_Changed;
        Rules.Add(rule);
    }

    // ── Save ─────────────────────────────────────────────────────────────────

    private void Rule_Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => Save();

    private void MonsterGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.UIThread.Post(Save, DispatcherPriority.Background);
    }

    private void Save()
    {
        if (string.IsNullOrEmpty(_monstersPath)) return;
        try
        {
            Directory.CreateDirectory(_charFolder);
            string json = JsonSerializer.Serialize(new List<MonsterRule>(Rules), EditorJsonContext.Default.ListMonsterRule);
            File.WriteAllText(_monstersPath, json);
            SetStatus($"Saved {Rules.Count} rule(s) — {DateTime.Now:HH:mm:ss}");
        }
        catch (Exception ex) { SetStatus($"Save error: {ex.Message}", error: true); }
    }

    // ── Row actions ──────────────────────────────────────────────────────────

    private void AddMonster_Click(object? sender, RoutedEventArgs e)
    {
        var nameBox = this.FindControl<TextBox>("NewMonsterBox");
        if (nameBox == null) return;

        string name = (nameBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        // RynthAi reserves this name for the single catch-all row (GetRuleForTarget / weapon equip).
        if (name.Equals("Default", StringComparison.OrdinalIgnoreCase) &&
            Rules.Any(r => r.IsDefault))
        {
            SetStatus("A 'Default' row already exists. That row is the fallback when no other rule matches.", error: true);
            return;
        }

        var rule = new MonsterRule { Name = name };
        AddRule(rule);

        var grid = this.FindControl<DataGrid>("MonsterGrid");
        if (grid != null)
        {
            grid.ScrollIntoView(rule, null);
            grid.SelectedItem = rule;
        }
        nameBox.Text = string.Empty;
        Save();
    }

    private void NewMonsterBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddMonster_Click(sender, e);
    }

    private void DeleteRow_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MonsterRule rule } && !rule.IsDefault)
        {
            rule.PropertyChanged -= Rule_Changed;
            Rules.Remove(rule);
            Save();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SetStatus(string msg, bool error = false)
    {
        var status = this.FindControl<TextBlock>("StatusText");
        if (status == null) return;

        status.Foreground = error
            ? new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8))
            : new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1));
        status.Text = msg;
    }
}

// ── Converters ────────────────────────────────────────────────────────────────

/// <summary>int weapon/offhand id → display name (read-only).</summary>
public class IdToNameConverter : IValueConverter
{
    public static readonly IdToNameConverter Instance = new();
    public IReadOnlyList<WeaponOption> Options { get; set; } = Array.Empty<WeaponOption>();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int id)
        {
            if (id == 0) return "<AUTO>";
            foreach (var opt in Options)
                if (opt.Id == id) return opt.DisplayName;
            return $"<ID:{id}>";
        }
        return "<AUTO>";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>int id ↔ WeaponOption. Used for ComboBox SelectedItem on an int-backed property.</summary>
public class OptionFromIdConverter : IValueConverter
{
    public static readonly OptionFromIdConverter Instance = new();
    public IReadOnlyList<WeaponOption> Options { get; set; } = Array.Empty<WeaponOption>();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int id)
            foreach (var opt in Options)
                if (opt.Id == id) return opt;
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is WeaponOption opt ? opt.Id : 0;
}

/// <summary>IsDefault → yellow; else default Foreground colour. WPF DataTriggers have no Avalonia equivalent.</summary>
public class DefaultFgConverter : IValueConverter
{
    public static readonly DefaultFgConverter Instance = new();
    private static readonly IBrush DefaultYellow = new SolidColorBrush(Color.FromRgb(0xF9, 0xE2, 0xAF));
    private static readonly IBrush Normal        = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? DefaultYellow : Normal;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class DefaultWeightConverter : IValueConverter
{
    public static readonly DefaultWeightConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? FontWeight.SemiBold : FontWeight.Normal;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>True → false, False → true (IsVisible on Delete button for the Default row).</summary>
public class InvertBoolConverter : IValueConverter
{
    public static readonly InvertBoolConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}
