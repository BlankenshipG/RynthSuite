// FandomFeed.cs - RynthOracle's own quest guide from the AC Community Wiki, asheron.fandom.com (2026-11-21,
// Silentkelpie: "under oracle duplicate itl functions for pulling in overall quests ... pull a dynamic feed from
// https://asheron.fandom.com/wiki/Category:Quests for the quests, .../Category:Kill_Tasks for kill task,
// .../Category:Society_Quests for socity quests, .../Category:Contracts for contracts ... should not be gated
// behind any server check"). The same reading as RynthILT's WikiFeed (leaftidewiki.com, a fork of this wiki
// with the same templates), but anonymous and on every server:
//  - the quests: every page with the {{Quest Summary}} box, and the pages under Category:Quests (its
//    sub-categories followed, the item ones left out);
//  - the kill tasks: Category:Kill_Tasks and its sub-categories;
//  - the society quests: Category:Society_Quests, each page's rank from the sub-category it is in
//    ("Society Quests: Initiate Rank" -> Initiate; "Joining a Society Quests" -> Joining);
//  - the contracts: Category:Contracts' item pages (WikiQuestParser.ParseContract).
// Only pages changed since the last read are fetched (their revision ids), unless the page reader is newer
// (WikiQuestParser.Version). Saved to %APPDATA%\RynthCore\RynthOracle\fandom-quests.json, read at load. The
// read runs on a pool thread and only builds a new QuestGuide and writes the file; the pump takes it
// (TakeResult). Read at login when the saved one is a day old (or older reader, or none), and on Refresh.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RynthCore.Plugin.RynthOracle.Data;

internal sealed class FandomFeed
{
    public const string Api = "https://asheron.fandom.com/api.php";
    public const string WikiBase = "https://asheron.fandom.com/wiki/";
    public const string QuestTemplate = "Template:Quest_Summary";
    public const string QuestsCategory = "Category:Quests";
    public const string KillTasksCategory = "Category:Kill_Tasks";
    public const string SocietyCategory = "Category:Society_Quests";
    public const string ContractsCategory = "Category:Contracts";
    public const int RefreshHours = 24;
    /// <summary>Category:Quests is a tree; this many categories at most are followed.</summary>
    internal const int MaxCategories = 400;
    private const int RequestGapMs = 250;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore", "RynthOracle", "fandom-quests.json");

    private readonly string _path;
    private CancellationTokenSource? _cts;
    private Task? _task;
    private volatile QuestGuide? _ready;     // a new guide for the pump
    private volatile string _status = "";
    private bool _autoTried;

    /// <summary>The guide in use (pump thread).</summary>
    public QuestGuide Guide { get; private set; } = new();
    /// <summary>Goes up when <see cref="Guide"/> is replaced.</summary>
    public int Revision { get; private set; }
    public bool Running => _task is { IsCompleted: false };
    public string Status => _status;

    public FandomFeed(string? path = null)
    {
        _path = path ?? DefaultPath;
        try
        {
            if (File.Exists(_path)) Guide = QuestGuide.Parse(File.ReadAllText(_path));
        }
        catch { Guide = new QuestGuide(); }
        _status = Guide.Quests.Count > 0
            ? $"{Guide.Quests.Count} quests, {Guide.Contracts.Count} contracts (read {Guide.UpdatedUtc.ToLocalTime():MM-dd HH:mm})"
            : "not read yet";
    }

    /// <summary>The saved guide wants reading: none yet, a day old, or made by an older page reader.</summary>
    internal static bool Due(QuestGuide g, DateTime utcNow) =>
        g.Quests.Count == 0 || g.Parser < WikiQuestParser.Version || utcNow - g.UpdatedUtc >= TimeSpan.FromHours(RefreshHours);

    /// <summary>Pump thread, in the world: the read at login, once a session, when the saved guide is due.</summary>
    public void Tick(DateTime utcNow)
    {
        if (_autoTried) return;
        _autoTried = true;
        if (Due(Guide, utcNow)) Start();
    }

    /// <summary>Starts a read unless one is running. Pump thread.</summary>
    public bool Start()
    {
        if (Running) return false;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        QuestGuide old = Guide;
        _status = "reading...";
        _task = Task.Run(async () =>
        {
            try { _status = await RefreshAsync(old, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { _status = "stopped"; }
            catch (Exception ex) { _status = "couldn't read the wiki: " + ex.GetType().Name + ": " + ex.Message; }
        }, token);
        return true;
    }

    /// <summary>Pump thread: takes a finished read's guide; true when the guide changed.</summary>
    public bool TakeResult()
    {
        QuestGuide? g = _ready;
        if (g == null) return false;
        _ready = null;
        Guide = g;
        Revision++;
        return true;
    }

    /// <summary>Uses a guide (the tests).</summary>
    internal void Use(QuestGuide g)
    {
        Guide = g;
        Revision++;
    }

    public void Shutdown()
    {
        try { _cts?.Cancel(); _task?.Wait(2000); } catch { }
    }

    // ── The read (pool thread) ──────────────────────────────────────────────

    private static readonly Regex RankRx = new(@"(?:^|:\s*)(\w+)\s+Rank\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The society rank a society sub-category stands for ("Society Quests: Knight Rank" -> Knight), or "".</summary>
    internal static string RankOf(string category)
    {
        Match m = RankRx.Match(category);
        if (m.Success) return char.ToUpperInvariant(m.Groups[1].Value[0]) + m.Groups[1].Value.Substring(1).ToLowerInvariant();
        return category.Contains("Joining", StringComparison.OrdinalIgnoreCase) ? "Joining" : "";
    }

    /// <summary>A Category:Quests sub-category not followed: item and NPC lists, not quests.</summary>
    internal static bool SkipCategory(string category) =>
        category.Contains("Quest Items", StringComparison.OrdinalIgnoreCase)
        || category.Contains("Items", StringComparison.OrdinalIgnoreCase) && !category.Contains("Quest", StringComparison.OrdinalIgnoreCase)
        || category.Contains("NPC", StringComparison.OrdinalIgnoreCase)
        || category.Contains("Brokers", StringComparison.OrdinalIgnoreCase)
        || category.Contains("Images", StringComparison.OrdinalIgnoreCase)
        || category.Contains("Maps", StringComparison.OrdinalIgnoreCase);

    private async Task<string> RefreshAsync(QuestGuide old, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RynthOracle/1.0 (RynthCore quest guide; Asheron's Call)");

        var titles = new SortedSet<string>(StringComparer.Ordinal);
        var killTasks = new SortedSet<string>(StringComparer.Ordinal);
        var contracts = new SortedSet<string>(StringComparer.Ordinal);
        var society = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        _status = "listing the quest pages...";
        await ListAsync(http, "embeddedin", new[] { ("list", "embeddedin"), ("eititle", QuestTemplate), ("einamespace", "0"), ("eilimit", "max") },
            p => titles.Add(p), _ => { }, token).ConfigureAwait(false);
        await WalkAsync(http, QuestsCategory, (p, _) => titles.Add(p), SkipCategory, token).ConfigureAwait(false);
        _status = "listing the kill tasks...";
        await WalkAsync(http, KillTasksCategory, (p, _) => { killTasks.Add(p); titles.Add(p); }, SkipCategory, token).ConfigureAwait(false);
        _status = "listing the society quests...";
        await WalkAsync(http, SocietyCategory, (p, cat) =>
        {
            titles.Add(p);
            string rank = RankOf(cat);
            if (rank.Length > 0 && !society.ContainsKey(p)) society[p] = rank;
        }, SkipCategory, token).ConfigureAwait(false);
        _status = "listing the contracts...";
        await ListAsync(http, "categorymembers", new[] { ("list", "categorymembers"), ("cmtitle", ContractsCategory), ("cmlimit", "max"), ("cmtype", "page") },
            p => { contracts.Add(p); titles.Add(p); }, _ => { }, token).ConfigureAwait(false);
        if (titles.Count == 0) return "the wiki listed no quest pages";

        // Which pages changed since the saved guide.
        var list = new List<string>(titles);
        var revs = new Dictionary<string, long>(StringComparer.Ordinal);
        int done = 0;
        foreach (string batch in MediaWikiJson.Batches(list))
        {
            _status = $"checking pages ({done}/{list.Count})...";
            string json = await GetAsync(http, MediaWikiJson.Url(Api, ("action", "query"), ("prop", "info"), ("titles", batch)), token).ConfigureAwait(false);
            foreach (var kv in MediaWikiJson.LastRevisions(json)) revs[kv.Key] = kv.Value;
            done += MediaWikiJson.TitlesPerRequest;
        }
        List<string> changed = PagesToRead(old, revs);

        var fresh = new Dictionary<string, GuideQuest>(StringComparer.Ordinal);
        var freshContracts = new Dictionary<string, GuideContract>(StringComparer.Ordinal);
        var notQuest = new HashSet<string>(StringComparer.Ordinal);
        int read = 0;
        foreach (string batch in MediaWikiJson.Batches(changed))
        {
            _status = $"reading pages ({read}/{changed.Count})...";
            string json = await GetAsync(http, MediaWikiJson.Url(Api, ("action", "query"), ("prop", "revisions"), ("rvprop", "content|ids"),
                ("rvslots", "main"), ("titles", batch)), token).ConfigureAwait(false);
            foreach (var (title, rev, text) in MediaWikiJson.Contents(json))
            {
                if (WikiQuestParser.IsQuestPage(text)) fresh[title] = WikiQuestParser.Parse(title, text, rev, WikiBase);
                else if (contracts.Contains(title) && WikiQuestParser.IsContractPage(title, text)) freshContracts[title] = WikiQuestParser.ParseContract(title, text, rev, WikiBase);
                else notQuest.Add(title);
            }
            read += MediaWikiJson.TitlesPerRequest;
        }

        QuestGuide g = Merge(old, revs, fresh, freshContracts);
        g.KillTasks = new List<string>(killTasks);
        g.Society = society;
        token.ThrowIfCancellationRequested();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", g.ToJson());
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        catch (Exception ex) { _status = "read, but couldn't save: " + ex.Message; }
        _ready = g;
        return $"{g.Quests.Count} quests ({fresh.Count} read now), {g.KillTasks.Count} kill tasks, {g.Society.Count} society quests, {g.Contracts.Count} contracts";
    }

    /// <summary>The pages to read: new or edited since the saved guide; every one when it came from an older reader.</summary>
    internal static List<string> PagesToRead(QuestGuide old, IReadOnlyDictionary<string, long> revs)
    {
        var known = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (GuideQuest q in old.Quests) known[q.Title] = q.Rev;
        foreach (GuideContract c in old.Contracts) known[c.Title] = c.Rev;
        bool whole = old.Parser < WikiQuestParser.Version;
        var changed = new List<string>();
        foreach (var kv in revs)
            if (whole || kv.Value == 0 || !known.TryGetValue(kv.Key, out long rev) || rev != kv.Value) changed.Add(kv.Key);
        return changed;
    }

    /// <summary>
    /// The new guide: the pages listed now, each the fresh read when there is one, else the saved one (a page that
    /// is no longer listed goes). Pages that aren't quests or contracts aren't kept, so they are read again next
    /// time (cheap: they are few once the item categories are left out).
    /// </summary>
    internal static QuestGuide Merge(QuestGuide old, IReadOnlyDictionary<string, long> listed,
        IReadOnlyDictionary<string, GuideQuest> fresh, IReadOnlyDictionary<string, GuideContract> freshContracts)
    {
        var oldQuests = new Dictionary<string, GuideQuest>(StringComparer.Ordinal);
        foreach (GuideQuest q in old.Quests) oldQuests[q.Title] = q;
        var oldContracts = new Dictionary<string, GuideContract>(StringComparer.Ordinal);
        foreach (GuideContract c in old.Contracts) oldContracts[c.Title] = c;
        var g = new QuestGuide { Source = "fandom", UpdatedUtc = DateTime.UtcNow, Parser = WikiQuestParser.Version, KillTasks = old.KillTasks, Society = old.Society };
        foreach (string title in listed.Keys)
        {
            if (fresh.TryGetValue(title, out GuideQuest? f)) g.Quests.Add(f);
            else if (oldQuests.TryGetValue(title, out GuideQuest? o)) g.Quests.Add(o);
            if (freshContracts.TryGetValue(title, out GuideContract? fc)) g.Contracts.Add(fc);
            else if (oldContracts.TryGetValue(title, out GuideContract? oc)) g.Contracts.Add(oc);
        }
        g.Quests.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
        g.Contracts.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return g;
    }

    /// <summary>A category's pages and, followed breadth first, its sub-categories' (each page with the category it was in).</summary>
    private async Task WalkAsync(HttpClient http, string root, Action<string, string> page, Func<string, bool> skip, CancellationToken token)
    {
        var queue = new Queue<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.Replace('_', ' '), root };
        queue.Enqueue(root);
        while (queue.Count > 0 && seen.Count <= MaxCategories)
        {
            string cat = queue.Dequeue();
            await ListAsync(http, "categorymembers", new[] { ("list", "categorymembers"), ("cmtitle", cat), ("cmlimit", "max"), ("cmtype", "page|subcat") },
                p => page(p, cat), sub =>
                {
                    if (!skip(sub) && seen.Add(sub)) queue.Enqueue(sub);
                }, token).ConfigureAwait(false);
        }
    }

    private static async Task ListAsync(HttpClient http, string listName, (string, string)[] args, Action<string> page, Action<string> sub, CancellationToken token)
    {
        var cont = new List<(string, string)>();
        for (int i = 0; i < 100; i++)
        {
            var all = new List<(string, string)> { ("action", "query") };
            all.AddRange(args);
            all.AddRange(cont);
            string json = await GetAsync(http, MediaWikiJson.Url(Api, all.ToArray()), token).ConfigureAwait(false);
            var (pages, subs, next) = MediaWikiJson.Members(json, listName);
            foreach (string p in pages) page(p);
            foreach (string s in subs) sub(s);
            if (next.Count == 0) return;
            cont = next;
        }
    }

    private static async Task<string> GetAsync(HttpClient http, string url, CancellationToken token)
    {
        await Task.Delay(RequestGapMs, token).ConfigureAwait(false);
        using HttpResponseMessage resp = await http.GetAsync(url + "&maxlag=5", token).ConfigureAwait(false);
        return await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
    }
}
