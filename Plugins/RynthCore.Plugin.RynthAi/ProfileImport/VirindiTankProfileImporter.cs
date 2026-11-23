using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi.ProfileImport;

/// <summary>
/// Imports VirindiTank profiles from the common VT install folders into RynthAi:
/// loot (.utl) → LootProfiles, nav (.nav) → NavProfiles, metas (.met) → MetaFiles,
/// merges MyMonsters from text .usd / monsters.json into the live combat settings,
/// and can write each .usd as a named SettingsProfiles JSON for this character.
/// Used by Settings → Game Client (every server) and /ra vtimport — not ILT-only.
/// </summary>
public static class VirindiTankProfileImporter
{
    /// <summary>Standard VirindiTank install locations (wiki / community bundles).</summary>
    public static IReadOnlyList<string> DefaultVirindiTankRoots()
    {
        var list = new List<string>
        {
            @"C:\Games\VirindiPlugins\VirindiTank",
        };

        try
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrEmpty(docs))
            {
                list.Add(Path.Combine(docs, "VirindiPlugins", "VirindiTank"));
                list.Add(Path.Combine(docs, "Decal", "VirindiPlugins", "VirindiTank"));
            }
        }
        catch { }

        try
        {
            string loc = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(loc))
                list.Add(Path.Combine(loc, "VirindiPlugins", "VirindiTank"));
        }
        catch { }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
    }

    public sealed class Result
    {
        public int LootFilesCopied;
        public int NavFilesCopied;
        public int MetaFilesCopied;
        public int MonsterRulesAdded;
        /// <summary>Named SettingsProfiles written from VTank .usd character profiles.</summary>
        public int SettingsProfilesWritten;
        /// <summary>Full paths of .utl files copied into the Rynth loot folder this run (for activating loot).</summary>
        public List<string> CopiedLootDestPaths { get; } = new();
        public List<string> LogLines { get; } = new();
        /// <summary>The ring options of each text .usd read (file name, values); see <see cref="ApplyRingSettings"/>.</summary>
        internal List<(string File, VtRingSettings Ring)> RingSettingsFound { get; } = new();
        /// <summary>"Min Ring Targets 2, Ring Range 5 (from the profile)" when they were set; else "".</summary>
        public string RingSettingsSet { get; internal set; } = string.Empty;

        /// <summary>One chat line: counts of what this run brought in.</summary>
        public string SummaryLine()
        {
            string ring = string.IsNullOrEmpty(RingSettingsSet) ? "" : $"; {RingSettingsSet}";
            string profiles = SettingsProfilesWritten > 0 ? $", {SettingsProfilesWritten} settings profile(s)" : "";
            return $"VTank import: {LootFilesCopied} loot, {NavFilesCopied} nav, {MetaFilesCopied} meta, {MonsterRulesAdded} monster rule(s){profiles}{ring}.";
        }
    }

    /// <summary>Source folders the import window (or a script) chooses; null / empty skips that kind.</summary>
    public sealed class ImportSources
    {
        public string? MonstersFolder;
        public string? LootFolder;
        public string? NavFolder;
        public string? MetaFolder;
        /// <summary>VTank .usd character profiles → RynthAi SettingsProfiles (named JSON).</summary>
        public string? ProfilesFolder;
    }

    /// <summary>
    /// Full import into the live RynthAi folders (LootProfiles, NavProfiles, MetaFiles) and optional
    /// monster/ring merge into <paramref name="settings"/>. Destinations default to
    /// <see cref="RynthCore.Install.RynthInstallPaths.RynthAiDir"/>. Scans
    /// <see cref="DefaultVirindiTankRoots"/> (no folder picker).
    /// </summary>
    public static Result ImportAll(LegacyUiSettings? settings, bool mergeMonsterRules = true,
        string? lootDestDir = null, string? navDestDir = null, string? metaDestDir = null)
    {
        string ai = RynthCore.Install.RynthInstallPaths.RynthAiDir;
        lootDestDir ??= Path.Combine(ai, "LootProfiles");
        navDestDir ??= Path.Combine(ai, "NavProfiles");
        metaDestDir ??= Path.Combine(ai, "MetaFiles");
        return Import(lootDestDir, navDestDir, metaDestDir, settings, mergeMonsterRules);
    }

    /// <summary>
    /// Import from the folders the player picked (blank = skip). Destinations stay RynthAi's.
    /// <paramref name="settingsProfilesCharDir"/> is SettingsProfiles\&lt;server&gt;\&lt;character&gt; for .usd → named profiles.
    /// </summary>
    public static Result ImportFromSources(ImportSources sources, LegacyUiSettings? settings, bool mergeMonsterRules,
        string? settingsProfilesCharDir = null)
    {
        string ai = RynthCore.Install.RynthInstallPaths.RynthAiDir;
        string lootDest = Path.Combine(ai, "LootProfiles");
        string navDest = Path.Combine(ai, "NavProfiles");
        string metaDest = Path.Combine(ai, "MetaFiles");
        return Import(lootDest, navDest, metaDest, settings, mergeMonsterRules, sources, settingsProfilesCharDir);
    }

    /// <param name="lootDestDir">RynthAi LootProfiles folder (.utl).</param>
    /// <param name="navDestDir">RynthAi NavProfiles folder (.nav); null skips navs.</param>
    /// <param name="metaDestDir">RynthAi MetaFiles folder (.met); null skips metas.</param>
    /// <param name="mergeMonsterRules">When true and <paramref name="settings"/> is set, merge MyMonsters from .usd / monsters.json.</param>
    public static Result Import(string lootDestDir, LegacyUiSettings settings, bool mergeMonsterRules) =>
        Import(lootDestDir, Path.Combine(RynthCore.Install.RynthInstallPaths.RynthAiDir, "NavProfiles"),
            Path.Combine(RynthCore.Install.RynthInstallPaths.RynthAiDir, "MetaFiles"), settings, mergeMonsterRules);

    public static Result Import(string lootDestDir, string? navDestDir, string? metaDestDir,
        LegacyUiSettings? settings, bool mergeMonsterRules, ImportSources? sources = null,
        string? settingsProfilesCharDir = null)
    {
        var result = new Result();
        if (string.IsNullOrEmpty(lootDestDir))
        {
            result.LogLines.Add("Loot destination folder is empty.");
            return result;
        }

        if (!TryEnsureDir(lootDestDir, "loot", result)) return result;
        if (!string.IsNullOrEmpty(navDestDir) && !TryEnsureDir(navDestDir, "nav", result)) navDestDir = null;
        if (!string.IsNullOrEmpty(metaDestDir) && !TryEnsureDir(metaDestDir, "meta", result)) metaDestDir = null;

        var seenSrc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Picked folders: each kind has its own root (import window). Else scan default VT installs.
        if (sources != null)
        {
            ScanOne(sources.LootFolder, "*.utl", lootDestDir, "Loot", seenSrc, result,
                () => result.LootFilesCopied++, dest => result.CopiedLootDestPaths.Add(dest));
            if (!string.IsNullOrEmpty(navDestDir))
                ScanOne(sources.NavFolder, "*.nav", navDestDir, "Nav", seenSrc, result, () => result.NavFilesCopied++);
            if (!string.IsNullOrEmpty(metaDestDir))
                ScanOne(sources.MetaFolder, "*.met", metaDestDir, "Meta", seenSrc, result, () => result.MetaFilesCopied++);

            if (mergeMonsterRules && settings != null && !string.IsNullOrEmpty(sources.MonstersFolder)
                && Directory.Exists(sources.MonstersFolder))
            {
                result.LogLines.Add($"Scanning monsters {sources.MonstersFolder}");
                foreach (string jsonPath in EnumerateFilesRecursive(sources.MonstersFolder, "monsters.json", maxDepth: 8))
                    TryMergeMonsterFile(jsonPath, settings, result);
                foreach (string usdPath in EnumerateFilesRecursive(sources.MonstersFolder, "*.usd", maxDepth: 6))
                    TryMergeUsdMyMonstersScf(usdPath, settings, result);
            }

            // VTank .usd character profiles → named SettingsProfiles for this character (does not switch the active one).
            if (!string.IsNullOrWhiteSpace(sources.ProfilesFolder) && Directory.Exists(sources.ProfilesFolder))
                ImportUsdFolderAsSettingsProfiles(sources.ProfilesFolder, settingsProfilesCharDir, settings, result);
        }
        else
        {
            foreach (string root in DefaultVirindiTankRoots())
            {
                result.LogLines.Add($"Scanning {root}");
                CopyPattern(root, "*.utl", lootDestDir, "Loot", seenSrc, result, () => result.LootFilesCopied++,
                    dest => result.CopiedLootDestPaths.Add(dest));
                if (!string.IsNullOrEmpty(navDestDir))
                    CopyPattern(root, "*.nav", navDestDir, "Nav", seenSrc, result, () => result.NavFilesCopied++);
                if (!string.IsNullOrEmpty(metaDestDir))
                    CopyPattern(root, "*.met", metaDestDir, "Meta", seenSrc, result, () => result.MetaFilesCopied++);

                if (mergeMonsterRules && settings != null)
                {
                    foreach (string jsonPath in EnumerateFilesRecursive(root, "monsters.json", maxDepth: 8))
                        TryMergeMonsterFile(jsonPath, settings, result);

                    foreach (string usdPath in EnumerateFilesRecursive(root, "*.usd", maxDepth: 6))
                        TryMergeUsdMyMonstersScf(usdPath, settings, result);
                }
            }
        }

        if (mergeMonsterRules && settings != null)
            ApplyRingSettings(settings, result);

        if (result.LootFilesCopied == 0 && result.NavFilesCopied == 0 && result.MetaFilesCopied == 0
            && result.MonsterRulesAdded == 0 && result.SettingsProfilesWritten == 0)
            result.LogLines.Add("No .utl / .nav / .met / monsters.json / text MyMonsters .usd found under the chosen paths.");

        return result;
    }

    /// <summary>
    /// Each text .usd in <paramref name="usdFolder"/> (top-level files only) becomes a named settings
    /// profile under <paramref name="settingsProfilesCharDir"/>: clone of the live template with that
    /// profile's MyMonsters + ring options. Does not change the active profile.
    /// </summary>
    private static void ImportUsdFolderAsSettingsProfiles(string usdFolder, string? settingsProfilesCharDir,
        LegacyUiSettings? template, Result result)
    {
        if (string.IsNullOrWhiteSpace(settingsProfilesCharDir))
        {
            result.LogLines.Add("Profiles: skipped (not logged in — no SettingsProfiles character folder).");
            return;
        }
        if (!TryEnsureDir(settingsProfilesCharDir, "settings profiles", result)) return;

        result.LogLines.Add($"Scanning profiles {usdFolder}");
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(usdFolder, "*.usd"); }
        catch (Exception ex)
        {
            result.LogLines.Add($"Profiles: cannot list {usdFolder}: {ex.Message}");
            return;
        }

        foreach (string usdPath in files)
        {
            if (LooksLikeSqliteFile(usdPath))
            {
                result.LogLines.Add($"Profiles: skip SQLite .usd (use Monster Editor): {Path.GetFileName(usdPath)}");
                continue;
            }

            string profileName = SanitizeProfileName(Path.GetFileNameWithoutExtension(usdPath));
            if (profileName.Length == 0)
            {
                result.LogLines.Add($"Profiles: skip empty name from {Path.GetFileName(usdPath)}");
                continue;
            }
            // Reserved: Default is the live character profile; don't overwrite it from a VTank dump.
            if (profileName.Equals("Default", StringComparison.OrdinalIgnoreCase)
                || profileName.Equals("active_profile", StringComparison.OrdinalIgnoreCase))
                profileName = "VT_" + profileName;

            string dest = Path.Combine(settingsProfilesCharDir, profileName + ".json");
            int suffix = 0;
            while (File.Exists(dest))
            {
                suffix++;
                dest = Path.Combine(settingsProfilesCharDir, $"VT_{suffix}_{profileName}.json");
            }
            string writtenName = Path.GetFileNameWithoutExtension(dest);

            LegacyUiSettings? clone = CloneSettings(template);
            if (clone == null)
            {
                result.LogLines.Add($"Profiles: skip {Path.GetFileName(usdPath)} (could not clone settings).");
                continue;
            }

            // This profile is that VTank .usd: replace monsters with its MyMonsters (keep a Default row).
            clone.MonsterRules = new List<MonsterRule>();
            clone.EnsureDefaultRule();
            clone.SelectedProfile = writtenName;

            string allText;
            try { allText = File.ReadAllText(usdPath, Encoding.UTF8); }
            catch (Exception ex)
            {
                result.LogLines.Add($"Profiles: skip read {Path.GetFileName(usdPath)}: {ex.Message}");
                continue;
            }

            var tmp = new List<MonsterRule>();
            VirindiTankUsdMyMonstersScfParser.TryParseMyMonsters(allText, tmp, out string? note);
            int monstersBefore = result.MonsterRulesAdded;
            if (tmp.Count > 0)
                MergeVtMonsterRows(tmp, clone, result);
            // MonsterRulesAdded counts live merge; for profiles we track SettingsProfilesWritten separately.
            result.MonsterRulesAdded = monstersBefore;

            var ring = VtRingSettings.Read(allText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));
            if (ring.Any) ring.ApplyTo(clone);

            string json;
            try { json = JsonSerializer.Serialize(clone, RynthAiJsonContext.Default.LegacyUiSettings); }
            catch (Exception ex)
            {
                result.LogLines.Add($"Profiles: skip serialize {writtenName}: {ex.Message}");
                continue;
            }

            try
            {
                File.WriteAllText(dest, json, Encoding.UTF8);
                result.SettingsProfilesWritten++;
                result.LogLines.Add(
                    $"Profiles: {Path.GetFileName(usdPath)} -> {writtenName}.json"
                    + (tmp.Count > 0 ? $" ({tmp.Count} monster rule(s)" + (ring.Any ? $", {ring.Describe()}" : "") + ")" : "")
                    + (string.IsNullOrEmpty(note) ? "" : "; " + note));
            }
            catch (Exception ex)
            {
                result.LogLines.Add($"Profiles: write failed {writtenName}: {ex.Message}");
            }
        }
    }

    /// <summary>Safe settings-profile file stem (letters, digits, apostrophe, hyphen; else underscore).</summary>
    private static string SanitizeProfileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        // VTank dumps often prefix with "--"; strip leading dashes/underscores for a cleaner profile name.
        name = name.Trim().TrimStart('-', '_', ' ');
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) || c == '\'' || c == '-' || c == '_' || c == ' ' ? c : '_');
        string cleaned = sb.ToString().Trim();
        // Spaces → underscores so the dashboard list stays tidy.
        return cleaned.Replace(' ', '_');
    }

    private static LegacyUiSettings? CloneSettings(LegacyUiSettings? template)
    {
        try
        {
            if (template == null) return new LegacyUiSettings();
            string json = JsonSerializer.Serialize(template, RynthAiJsonContext.Default.LegacyUiSettings);
            return JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.LegacyUiSettings) ?? new LegacyUiSettings();
        }
        catch
        {
            return new LegacyUiSettings();
        }
    }

    private static void ScanOne(string? root, string pattern, string destDir, string label,
        HashSet<string> seenSrc, Result result, Action onCopied, Action<string>? onDest = null)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        result.LogLines.Add($"Scanning {label.ToLowerInvariant()} {root}");
        CopyPattern(root, pattern, destDir, label, seenSrc, result, onCopied, onDest);
    }

    private static bool TryEnsureDir(string dir, string label, Result result)
    {
        try { Directory.CreateDirectory(dir); return true; }
        catch (Exception ex)
        {
            result.LogLines.Add($"Cannot create {label} folder ({dir}): {ex.Message}");
            return false;
        }
    }

    /// <summary>Copies every matching file under <paramref name="root"/> into <paramref name="destDir"/> (no overwrite; VT_n_ prefix on clash).</summary>
    private static void CopyPattern(string root, string pattern, string destDir, string label,
        HashSet<string> seenSrc, Result result, Action onCopied, Action<string>? onDest = null)
    {
        foreach (string src in EnumerateFilesRecursive(root, pattern, maxDepth: 6))
        {
            if (!seenSrc.Add(src)) continue;
            string baseName = Path.GetFileName(src);
            // Skip VTank's empty placeholder nav name if it shows up as a file.
            if (baseName.Equals("[None].nav", StringComparison.OrdinalIgnoreCase)) continue;
            string dest = Path.Combine(destDir, baseName);
            int suffix = 0;
            while (File.Exists(dest))
            {
                suffix++;
                dest = Path.Combine(destDir, $"VT_{suffix}_{baseName}");
            }

            try
            {
                File.Copy(src, dest, overwrite: false);
                onCopied();
                onDest?.Invoke(dest);
                result.LogLines.Add($"{label}: {baseName} -> {Path.GetFileName(dest)}");
            }
            catch (Exception ex)
            {
                result.LogLines.Add($"{label} skip {baseName}: {ex.Message}");
            }
        }
    }

    /// <summary>SQLite-backed VT profiles are skipped here; use Monster Editor import for those.</summary>
    private static bool LooksLikeSqliteFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var hdr = new byte[16];
            int n = fs.Read(hdr, 0, hdr.Length);
            const string sig = "SQLite format 3\0";
            if (n < sig.Length)
                return false;
            for (int i = 0; i < sig.Length; i++)
            {
                if (hdr[i] != sig[i])
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Text SCF .usd with MyMonsters — same subset as Monster Editor import for the current character merge.</summary>
    internal static void TryMergeUsdMyMonstersScf(string usdPath, LegacyUiSettings settings, Result result)
    {
        if (string.IsNullOrWhiteSpace(usdPath) || !File.Exists(usdPath))
            return;
        if (LooksLikeSqliteFile(usdPath))
        {
            result.LogLines.Add($"Monsters: skip SQLite .usd (use Monster Editor): {Path.GetFileName(usdPath)}");
            return;
        }

        string allText;
        try
        {
            allText = File.ReadAllText(usdPath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            result.LogLines.Add($"Monsters: skip .usd read {Path.GetFileName(usdPath)}: {ex.Message}");
            return;
        }

        var ring = VtRingSettings.Read(allText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));
        if (ring.Any) result.RingSettingsFound.Add((Path.GetFileName(usdPath), ring));

        var tmp = new List<MonsterRule>();
        if (!VirindiTankUsdMyMonstersScfParser.TryParseMyMonsters(allText, tmp, out string? note) || tmp.Count == 0)
            return;

        int addedBefore = result.MonsterRulesAdded;
        MergeVtMonsterRows(tmp, settings, result);
        int newNamed = result.MonsterRulesAdded - addedBefore;
        result.LogLines.Add(
            $"Monsters: VT MyMonsters from {Path.GetFileName(usdPath)} — parsed {tmp.Count} row(s), +{newNamed} new rule(s).{(string.IsNullOrEmpty(note) ? "" : " " + note)}");
    }

    /// <summary>
    /// The ring options of the .usd profiles read (VtRingSettings) into Min Ring Targets and Ring
    /// Range: the import is the player's, so these two settings may change here and only here. This
    /// import reads every profile under the VirindiTank folders, so it sets them only when every
    /// profile that has ring options agrees; profiles that differ leave both alone and say so.
    /// None found: the settings are left as they are.
    /// </summary>
    internal static void ApplyRingSettings(LegacyUiSettings settings, Result result)
    {
        var found = result.RingSettingsFound;
        if (found.Count == 0) return;
        var first = found[0].Ring;
        foreach (var (file, ring) in found)
        {
            if (ring.SameAs(first)) continue;
            result.LogLines.Add($"Ring settings: the profiles differ ({found[0].File}: {first.Describe()}; {file}: {ring.Describe()}): " +
                                "Min Ring Targets and Ring Range left alone. Import one .usd from the Mini Remote / Hub importer to take its values.");
            return;
        }
        foreach (string c in first.Clamped) result.LogLines.Add($"Ring settings: {c} (the settings' range)");
        first.ApplyTo(settings);
        result.RingSettingsSet = first.Describe();
        result.LogLines.Add($"Ring settings: set {result.RingSettingsSet}" +
                            (found.Count > 1 ? $", the same in {found.Count} profiles." : $", {found[0].File}."));
    }

    /// <summary>Merges VT-style rows into <paramref name="settings"/> like Monster Editor external import.</summary>
    private static void MergeVtMonsterRows(IReadOnlyList<MonsterRule> src, LegacyUiSettings settings, Result result)
    {
        settings.EnsureDefaultRule();
        List<MonsterRule> rules = settings.MonsterRules;

        MonsterRule? srcDefault = src.FirstOrDefault(x => x.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));
        if (srcDefault != null && rules.Count > 0 && rules[0].Name.Equals("Default", StringComparison.OrdinalIgnoreCase))
            CopyCombatOntoDefault(rules[0], srcDefault);

        var nameSet = new HashSet<string>(rules.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
        var exprSet = new HashSet<string>(
            rules.Where(x => !string.IsNullOrWhiteSpace(x.MatchExpression)).Select(x => x.MatchExpression),
            StringComparer.Ordinal);

        foreach (MonsterRule row in src)
        {
            if (row.Name.Equals("Default", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrEmpty(row.Name))
            {
                if (nameSet.Contains(row.Name))
                    continue;
            }
            else if (!string.IsNullOrEmpty(row.MatchExpression))
            {
                if (exprSet.Contains(row.MatchExpression))
                    continue;
            }
            else
            {
                continue;
            }

            var c = CloneMonsterRule(row);
            if (!string.IsNullOrEmpty(c.Name))
            {
                if (!nameSet.Add(c.Name))
                    continue;
            }

            if (!string.IsNullOrEmpty(c.MatchExpression))
                exprSet.Add(c.MatchExpression);

            rules.Add(c);
            result.MonsterRulesAdded++;
        }
    }

    private static void CopyCombatOntoDefault(MonsterRule dst, MonsterRule src)
    {
        dst.Category = src.Category;
        dst.MatchExpression = src.MatchExpression;
        dst.Priority = src.Priority;
        dst.DamageType = src.DamageType;
        dst.WeaponId = src.WeaponId;
        dst.Fester = src.Fester;
        dst.Broadside = src.Broadside;
        dst.GravityWell = src.GravityWell;
        dst.Imperil = src.Imperil;
        dst.Yield = src.Yield;
        dst.Vuln = src.Vuln;
        dst.Corrosion = src.Corrosion;
        dst.Corruption = src.Corruption;
        dst.DestructiveCurse = src.DestructiveCurse;
        dst.WeakeningCurse = src.WeakeningCurse;
        dst.FesteringCurse = src.FesteringCurse;
        dst.UseArc = src.UseArc;
        dst.UseBolt = src.UseBolt;
        dst.UseRing = src.UseRing;
        dst.UseStreak = src.UseStreak;
        dst.ExVuln = src.ExVuln;
        dst.OffhandId = src.OffhandId;
        dst.PreferredAmmoItemId = src.PreferredAmmoItemId;
        dst.PetDamage = src.PetDamage;
        dst.AttackPower = src.AttackPower;
        dst.Kite = src.Kite;
    }

    private static MonsterRule CloneMonsterRule(MonsterRule row) =>
        new()
        {
            Name = row.Name,
            Category = row.Category,
            MatchExpression = row.MatchExpression,
            Priority = row.Priority,
            DamageType = row.DamageType,
            WeaponId = row.WeaponId,
            Fester = row.Fester,
            Broadside = row.Broadside,
            GravityWell = row.GravityWell,
            Imperil = row.Imperil,
            Yield = row.Yield,
            Vuln = row.Vuln,
            Corrosion = row.Corrosion,
            Corruption = row.Corruption,
            DestructiveCurse = row.DestructiveCurse,
            WeakeningCurse = row.WeakeningCurse,
            FesteringCurse = row.FesteringCurse,
            UseArc = row.UseArc,
            UseBolt = row.UseBolt,
            UseRing = row.UseRing,
            UseStreak = row.UseStreak,
            ExVuln = row.ExVuln,
            OffhandId = row.OffhandId,
            PreferredAmmoItemId = row.PreferredAmmoItemId,
            PetDamage = row.PetDamage,
            AttackPower = row.AttackPower,
            Kite = row.Kite,
            PreferredDamage = row.PreferredDamage,
        };

    private static void TryMergeMonsterFile(string jsonPath, LegacyUiSettings settings, Result result)
    {
        try
        {
            string json = File.ReadAllText(jsonPath);
            var imported = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.MonsterRuleList);
            if (imported == null || imported.Count == 0) return;

            int added = 0;
            foreach (var rule in imported)
            {
                if (string.IsNullOrWhiteSpace(rule.Name)) continue;
                if (rule.Name.Equals("Default", StringComparison.OrdinalIgnoreCase)) continue;
                if (settings.MonsterRules.Any(m => m.Name.Equals(rule.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                settings.MonsterRules.Add(rule);
                added++;
            }

            if (added > 0)
            {
                result.MonsterRulesAdded += added;
                result.LogLines.Add($"Monsters: merged {added} rule(s) from {Path.GetFileName(jsonPath)}");
            }
        }
        catch (Exception ex)
        {
            result.LogLines.Add($"Monsters skip {jsonPath}: {ex.Message}");
        }
    }

    private static IEnumerable<string> EnumerateFilesRecursive(string root, string pattern, int maxDepth)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) yield break;
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, pattern); }
            catch { continue; }

            foreach (string f in files)
                yield return f;

            if (depth >= maxDepth) continue;

            IEnumerable<string> subdirs;
            try { subdirs = Directory.EnumerateDirectories(dir); }
            catch { continue; }

            foreach (string sub in subdirs)
                stack.Push((sub, depth + 1));
        }
    }
}
