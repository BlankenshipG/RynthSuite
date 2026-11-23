// MediaWikiJson.cs - The MediaWiki API's answers, for RynthOracle's own wiki reader (FandomFeed.cs, 2026-11-21).
// A copy of RynthILT's IltWiki/WikiApi.cs (Silentkelpie: "under oracle duplicate ilt functions for pulling in
// overall quests"), without the login: the AC Community Wiki (asheron.fandom.com) reads without one.
//   list=categorymembers  a category's pages and sub-categories
//   list=embeddedin       eititle=Template:Quest_Summary (every page with the quest box)
//   prop=info             the pages' last revision ids (an unchanged page isn't fetched again)
//   prop=revisions        the changed pages' wikitext (50 a request)
// Pure JSON reading here (the offline tests feed it answers); FandomFeed.cs does the HTTP.
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace RynthCore.Plugin.RynthOracle.Data;

internal static class MediaWikiJson
{
    /// <summary>Titles per prop=info / prop=revisions request (the API's limit for an anonymous reader).</summary>
    public const int TitlesPerRequest = 50;

    /// <summary>The URL for a GET with these parameters (format=json, formatversion=2 added).</summary>
    public static string Url(string api, params (string Key, string Value)[] args)
    {
        var sb = new StringBuilder(api);
        sb.Append(api.Contains('?') ? '&' : '?').Append("format=json&formatversion=2");
        foreach (var (k, v) in args) sb.Append('&').Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(v));
        return sb.ToString();
    }

    /// <summary>
    /// A list=categorymembers or list=embeddedin page: the members' titles (pages, namespace 0), the
    /// sub-categories, and the "continue" parameters for the next page (empty = done).
    /// </summary>
    public static (List<string> Pages, List<string> SubCats, List<(string Key, string Value)> Continue) Members(string json, string listName)
    {
        var pages = new List<string>();
        var cats = new List<string>();
        var cont = new List<(string, string)>();
        try
        {
            using JsonDocument d = JsonDocument.Parse(json);
            JsonElement root = d.RootElement;
            if (root.TryGetProperty("query", out JsonElement q) && q.TryGetProperty(listName, out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                foreach (JsonElement m in list.EnumerateArray())
                {
                    string title = Str(m, "title");
                    if (title.Length == 0) continue;
                    int ns = m.TryGetProperty("ns", out JsonElement n) && n.TryGetInt32(out int v) ? v : 0;
                    if (ns == 14) cats.Add(title);
                    else if (ns == 0) pages.Add(title);
                }
            if (root.TryGetProperty("continue", out JsonElement c) && c.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty p in c.EnumerateObject())
                    cont.Add((p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText()));
        }
        catch (JsonException) { }
        return (pages, cats, cont);
    }

    /// <summary>prop=info: each page's title and last revision id (missing pages left out).</summary>
    public static Dictionary<string, long> LastRevisions(string json)
    {
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (JsonElement p in Pages(json))
        {
            if (p.TryGetProperty("missing", out _)) continue;
            string title = Str(p, "title");
            long rev = p.TryGetProperty("lastrevid", out JsonElement r) && r.TryGetInt64(out long v) ? v : 0;
            if (title.Length > 0) map[title] = rev;
        }
        return map;
    }

    /// <summary>prop=revisions (rvprop=content|ids, rvslots=main): each page's title, revision id and wikitext.</summary>
    public static List<(string Title, long Rev, string Text)> Contents(string json)
    {
        var list = new List<(string, long, string)>();
        foreach (JsonElement p in Pages(json))
        {
            string title = Str(p, "title");
            if (title.Length == 0 || !p.TryGetProperty("revisions", out JsonElement revs) || revs.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement r in revs.EnumerateArray())
            {
                long id = r.TryGetProperty("revid", out JsonElement i) && i.TryGetInt64(out long v) ? v : 0;
                string text = "";
                if (r.TryGetProperty("slots", out JsonElement slots) && slots.TryGetProperty("main", out JsonElement main))
                    text = Str(main, "content");
                else
                    text = Str(r, "content");
                list.Add((title, id, text));
                break;
            }
        }
        return list;
    }

    /// <summary>query.pages (formatversion=2: an array), copied out so the document can go.</summary>
    private static List<JsonElement> Pages(string json)
    {
        var list = new List<JsonElement>();
        try
        {
            using JsonDocument d = JsonDocument.Parse(json);
            if (d.RootElement.TryGetProperty("query", out JsonElement q) && q.TryGetProperty("pages", out JsonElement pages))
            {
                if (pages.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement p in pages.EnumerateArray()) list.Add(p.Clone());
                else if (pages.ValueKind == JsonValueKind.Object)   // formatversion=1: keyed by page id
                    foreach (JsonProperty p in pages.EnumerateObject()) list.Add(p.Value.Clone());
            }
        }
        catch (JsonException) { }
        return list;
    }

    /// <summary>Titles split into requests of at most <see cref="TitlesPerRequest"/>, joined with '|'.</summary>
    public static List<string> Batches(IReadOnlyList<string> titles)
    {
        var list = new List<string>();
        for (int i = 0; i < titles.Count; i += TitlesPerRequest)
        {
            int n = Math.Min(TitlesPerRequest, titles.Count - i);
            var part = new string[n];
            for (int k = 0; k < n; k++) part[k] = titles[i + k];
            list.Add(string.Join("|", part));
        }
        return list;
    }

    private static string Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
