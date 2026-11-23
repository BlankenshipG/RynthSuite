// ============================================================================
//  RynthAi - Features/FeatureCatalog.cs
//  The feature catalog (2026-11-13): one entry per thing a player would look
//  for, across RynthSuite and RynthCore, read from Docs/features/<area>.json
//  (the website's search reads the same files; Docs/features/SCHEMA in the
//  repo's featsearch notes). Every file there is embedded in RynthAi
//  (RynthCore.Plugin.RynthAi.csproj: EmbeddedResource with a wildcard, logical
//  names "RynthAi.Features.<file>.json") and read once, through the
//  source-generated RynthAiJsonContext (NativeAOT: no reflection).
//
//  A file that doesn't parse logs one line and is skipped; an entry without an
//  id or a name is dropped (counted in the same line). Never throws.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace RynthCore.Plugin.RynthAi.Features;

/// <summary>One catalog file: Docs/features/&lt;area&gt;.json.</summary>
internal sealed class FeatureFile
{
    public string Area { get; set; } = string.Empty;
    public List<FeatureEntry> Features { get; set; } = new();
}

/// <summary>One thing a player would look for (the schema's fields; JSON names are the lower-case ones).</summary>
internal sealed class FeatureEntry
{
    /// <summary>"&lt;prefix&gt;.&lt;kebab-name&gt;", unique across every file.</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>The name the player sees in the UI, exactly.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>"RynthAi", "RynthOracle", ... or "RynthCore".</summary>
    public string Plugin { get; set; } = string.Empty;
    /// <summary>A short category for browsing (Combat, Buffs, Hunting, ...).</summary>
    public string Group { get; set; } = string.Empty;
    /// <summary>One or two plain sentences, 200 characters at most.</summary>
    public string Summary { get; set; } = string.Empty;
    /// <summary>Button paths to it, "&gt;" between steps.</summary>
    public List<string> Where { get; set; } = new();
    /// <summary>The typed command with its arguments, or empty.</summary>
    public string Command { get; set; } = string.Empty;
    /// <summary>A command that only opens where the feature lives (the window's Open button), or empty.</summary>
    public string Open { get; set; } = string.Empty;
    /// <summary>The LegacyUiSettings fields it covers (RynthAi only).</summary>
    public List<string> Settings { get; set; } = new();
    /// <summary>Words a player might type that aren't in the name or summary.</summary>
    public List<string> Keywords { get; set; } = new();
}

/// <summary>One embedded file as it was read: its resource name, the file (null when it didn't parse) and the error.</summary>
internal sealed record FeatureFileRead(string Resource, FeatureFile? File, string Error, int Dropped);

/// <summary>Every embedded catalog file, read once.</summary>
internal sealed class FeatureCatalog
{
    /// <summary>The logical-name prefix of the embedded files (the csproj's LogicalName).</summary>
    public const string ResourcePrefix = "RynthAi.Features.";

    private static readonly object Gate = new();
    private static FeatureCatalog? _loaded;

    /// <summary>Every entry, in file order (files by name).</summary>
    public IReadOnlyList<FeatureEntry> Entries { get; }

    /// <summary>What each embedded file gave (tests, and the load's log line).</summary>
    public IReadOnlyList<FeatureFileRead> Files { get; }

    public static readonly FeatureCatalog Empty = new(Array.Empty<FeatureEntry>(), Array.Empty<FeatureFileRead>());

    private FeatureCatalog(IReadOnlyList<FeatureEntry> entries, IReadOnlyList<FeatureFileRead> files)
    {
        Entries = entries;
        Files = files;
    }

    /// <summary>The embedded catalog, read on first use (any thread). <paramref name="log"/> gets one line per bad file.</summary>
    public static FeatureCatalog Shared(Action<string>? log)
    {
        lock (Gate)
        {
            return _loaded ??= LoadEmbedded(log);
        }
    }

    /// <summary>The logical names of the embedded catalog files, sorted.</summary>
    public static List<string> ResourceNames()
    {
        var names = new List<string>();
        foreach (string n in typeof(FeatureCatalog).Assembly.GetManifestResourceNames())
            if (n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                names.Add(n);
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>Reads every embedded catalog file. Never throws.</summary>
    public static FeatureCatalog LoadEmbedded(Action<string>? log)
    {
        var entries = new List<FeatureEntry>();
        var files = new List<FeatureFileRead>();
        List<string> names;
        try { names = ResourceNames(); }
        catch (Exception ex)
        {
            log?.Invoke($"[RynthAi] Find a Feature: couldn't list the catalog ({ex.GetType().Name}: {ex.Message}).");
            return Empty;
        }
        Assembly asm = typeof(FeatureCatalog).Assembly;
        foreach (string name in names)
        {
            FeatureFileRead read;
            try
            {
                using Stream? s = asm.GetManifestResourceStream(name);
                read = s == null ? new FeatureFileRead(name, null, "not found", 0) : Parse(name, s);
            }
            catch (Exception ex) { read = new FeatureFileRead(name, null, ex.GetType().Name + ": " + ex.Message, 0); }
            files.Add(read);
            string shown = name.Substring(ResourcePrefix.Length);
            if (read.File == null)
                log?.Invoke($"[RynthAi] Find a Feature: skipped {shown}, it doesn't read ({read.Error}).");
            else
            {
                entries.AddRange(read.File.Features);
                if (read.Dropped > 0)
                    log?.Invoke($"[RynthAi] Find a Feature: {shown}: {read.Dropped} entries without an id or a name left out.");
            }
        }
        return new FeatureCatalog(entries, files);
    }

    /// <summary>One file's JSON (the embedded stream, or a file on disk in the tests).</summary>
    public static FeatureFileRead Parse(string name, Stream json)
    {
        try
        {
            FeatureFile? f = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.FeatureFile);
            if (f == null) return new FeatureFileRead(name, null, "empty", 0);
            return Clean(name, f);
        }
        catch (JsonException ex) { return new FeatureFileRead(name, null, "line " + ((ex.LineNumber ?? -1) + 1) + ": " + ex.Message, 0); }
        catch (Exception ex) { return new FeatureFileRead(name, null, ex.GetType().Name + ": " + ex.Message, 0); }
    }

    /// <summary>Nulls (an explicit JSON null) become empty; entries without an id or a name are dropped.</summary>
    private static FeatureFileRead Clean(string name, FeatureFile f)
    {
        f.Area ??= string.Empty;
        var kept = new List<FeatureEntry>();
        int dropped = 0;
        foreach (FeatureEntry? e in f.Features ?? new List<FeatureEntry>())
        {
            if (e == null) { dropped++; continue; }
            e.Id ??= string.Empty;
            e.Name ??= string.Empty;
            e.Plugin ??= string.Empty;
            e.Group ??= string.Empty;
            e.Summary ??= string.Empty;
            e.Command ??= string.Empty;
            e.Open ??= string.Empty;
            e.Where = NoNulls(e.Where);
            e.Settings = NoNulls(e.Settings);
            e.Keywords = NoNulls(e.Keywords);
            if (e.Id.Trim().Length == 0 || e.Name.Trim().Length == 0) { dropped++; continue; }
            if (e.Plugin.Length == 0) e.Plugin = f.Area;
            kept.Add(e);
        }
        f.Features = kept;
        return new FeatureFileRead(name, f, string.Empty, dropped);
    }

    private static List<string> NoNulls(List<string>? list)
    {
        var r = new List<string>();
        if (list == null) return r;
        foreach (string? s in list) if (!string.IsNullOrWhiteSpace(s)) r.Add(s);
        return r;
    }
}
