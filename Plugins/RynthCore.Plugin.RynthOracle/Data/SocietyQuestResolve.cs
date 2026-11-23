// Society quest wiki pages list all three factions (CH / EW / RB NPCs, forts, strongholds).
// Running one without picking the player's society sends RynthNav / portal-enter at the wrong
// faction forever (Town Network's three "Stronghold" portals, Palm Fort's three coords, …).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>
/// Filters a society wiki quest down to the player's Celestial Hand / Eldrytch Web / Radiant Blood
/// alternatives before QuestRunner walks it.
/// </summary>
internal static class SocietyQuestResolve
{
    // "Aldrion of the Celestial Hand (CH)", "92.1S, 50.3W (CH)", "… (Eldrytch Web)".
    private static readonly Regex TaggedRx = new(
        @"(?<body>(?:\d+(?:\.\d+)?\s*[NnSs][\s,]+\d+(?:\.\d+)?\s*[EeWw]|[^()]+?))\s*\((?<tag>CH|EW|RB|Celestial\s+Hand|Eldrytch\s+Web|Radiant\s+Blood)\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CoordsRx = new(
        @"(\d+(?:\.\d+)?)\s*([NnSs])[\s,]+(\d+(?:\.\d+)?)\s*([EeWw])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex StrongholdPlaceRx = new(
        @"\b(?:your\s+)?(?:faction|society)\s+stronghold\b|\bsociety\s+chapterhouse\b|\bfaction\s+stronghold\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>"CH", "EW", "RB", or "" when not in a society.</summary>
    public static string Code(SocietyStanding s) => s.SocietyName switch
    {
        "Celestial Hand" => "CH",
        "Eldrytch Web" => "EW",
        "Radiant Blood" => "RB",
        _ => "",
    };

    /// <summary>"Celestial Hand Stronghold" (empty when not a member).</summary>
    public static string StrongholdName(SocietyStanding s) =>
        s.IsMember ? s.SocietyName + " Stronghold" : "";

    /// <summary>True when the wiki page is a society quest or its start lists CH/EW/RB alternatives.</summary>
    public static bool NeedsFilter(GuideQuest q) =>
        q.SocietyRank.Length > 0
        || TaggedRx.IsMatch(q.StartsWith ?? "")
        || TaggedRx.IsMatch(q.StartsAt ?? "")
        || StrongholdPlaceRx.IsMatch(q.StartsAt ?? "")
        || q.Steps.Exists(st => StrongholdPlaceRx.IsMatch(st.Text ?? "") || TaggedRx.IsMatch(st.Text ?? ""));

    /// <summary>
    /// A copy of <paramref name="q"/> with only this player's society NPCs / coords / stronghold name.
    /// Unchanged when not a member or the quest has no faction alternatives.
    /// </summary>
    public static GuideQuest ForPlayer(GuideQuest q, SocietyStanding s)
    {
        if (!s.IsMember || !NeedsFilter(q)) return q;

        string code = Code(s);
        string stronghold = StrongholdName(s);
        var copy = new GuideQuest
        {
            Title = q.Title,
            Url = q.Url,
            Rev = q.Rev,
            Level = q.Level,
            RecommendedLevel = q.RecommendedLevel,
            Type = q.Type,
            KillTask = q.KillTask,
            StartsWith = FilterTaggedParts(q.StartsWith, code, s.SocietyName),
            StartsAt = FilterStartsAt(q.StartsAt, code, s.SocietyName, stronghold),
            Route = q.Route,
            Repeat = q.Repeat,
            RepeatSec = q.RepeatSec,
            TimeToComplete = q.TimeToComplete,
            Contracts = q.Contracts,
            Qb = q.Qb,
            SocietyRank = q.SocietyRank,
            Categories = new List<string>(q.Categories),
            Overview = q.Overview,
            ExtraStamps = new List<string>(q.ExtraStamps),
            Loot = new List<string>(q.Loot),
            Rewards = new List<string>(q.Rewards),
        };

        // Coords tagged in StartsAt map which of a step's untagged list belongs to this society.
        var societyCoords = TaggedCoords(q.StartsAt, code, s.SocietyName);
        foreach (GuideStep st in q.Steps)
        {
            copy.Steps.Add(new GuideStep
            {
                Text = RewriteStrongholdText(st.Text, stronghold),
                Notes = st.Notes.ConvertAll(n => RewriteStrongholdText(n, stronghold)),
                Coords = FilterStepCoords(st.Coords, societyCoords, st.Text, code, s.SocietyName),
                Stamps = new List<string>(st.Stamps),
                Items = new List<string>(st.Items),
            });
        }
        return copy;
    }

    /// <summary>
    /// Extra portal-name words for RynthAi's "dungeon enter": the player's stronghold when the step
    /// says faction/society stronghold (Town Network has three portals all named "Stronghold").
    /// </summary>
    public static string PortalHintExtra(GuideStep s, SocietyStanding standing)
    {
        if (!standing.IsMember) return "";
        if (!StrongholdPlaceRx.IsMatch(s.Text ?? "")
            && !StrongholdPlaceRx.IsMatch(string.Join(" ", s.Notes)))
            return "";
        // Full society name words + Stronghold so scoring prefers "Celestial Hand Stronghold" over the others.
        return " " + string.Join(" || ", standing.SocietyName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Concat(new[] { "Stronghold" }));
    }

    private static string FilterStartsAt(string startsAt, string code, string societyName, string stronghold)
    {
        string raw = startsAt ?? "";
        if (raw.Length == 0) return raw;
        if (StrongholdPlaceRx.IsMatch(raw) && !TaggedRx.IsMatch(raw))
        {
            // "Faction Stronghold (east wing)" → "Celestial Hand Stronghold (east wing)".
            string wing = "";
            var m = Regex.Match(raw, @"\(([^)]+)\)");
            if (m.Success) wing = " (" + m.Groups[1].Value.Trim() + ")";
            return stronghold + wing;
        }
        string filtered = FilterTaggedParts(raw, code, societyName);
        return filtered.Length > 0 ? filtered : raw;
    }

    /// <summary>Keep only "(CH)" / "(EW)" / "(RB)" (or full society-name) segments for this player.</summary>
    private static string FilterTaggedParts(string text, string code, string societyName)
    {
        if (string.IsNullOrWhiteSpace(text) || code.Length == 0) return text ?? "";
        var kept = new List<string>();
        foreach (Match m in TaggedRx.Matches(text))
        {
            if (!TagMatches(m.Groups["tag"].Value, code, societyName)) continue;
            string body = m.Groups["body"].Value.Trim().TrimStart(':', '-', ' ');
            if (body.Length > 0) kept.Add(body);
        }
        if (kept.Count == 0) return text; // no tags → leave alone (single-society page)
        return string.Join(" / ", kept);
    }

    private static List<string> TaggedCoords(string startsAt, string code, string societyName)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(startsAt) || code.Length == 0) return list;
        foreach (Match m in TaggedRx.Matches(startsAt))
        {
            if (!TagMatches(m.Groups["tag"].Value, code, societyName)) continue;
            Match c = CoordsRx.Match(m.Groups["body"].Value);
            if (c.Success) list.Add(NormalizeCoords(c));
        }
        return list;
    }

    private static List<string> FilterStepCoords(List<string> coords, List<string> societyCoords,
        string stepText, string code, string societyName)
    {
        if (coords == null || coords.Count == 0) return new List<string>();

        // Coords written with a tag in the step text itself.
        var fromText = new List<string>();
        foreach (Match m in TaggedRx.Matches(stepText ?? ""))
        {
            if (!TagMatches(m.Groups["tag"].Value, code, societyName)) continue;
            Match c = CoordsRx.Match(m.Groups["body"].Value);
            if (c.Success)
            {
                string n = NormalizeCoords(c);
                if (coords.Exists(x => SameCoords(x, n))) fromText.Add(coords.First(x => SameCoords(x, n)));
            }
        }
        if (fromText.Count > 0) return fromText;

        // Palm Fort: step lists all three forts; StartsAt tags say which is ours.
        if (societyCoords.Count > 0)
        {
            var matched = coords.Where(c => societyCoords.Exists(sc => SameCoords(c, sc))).ToList();
            if (matched.Count > 0) return matched;
        }

        return new List<string>(coords);
    }

    private static string RewriteStrongholdText(string text, string stronghold)
    {
        if (string.IsNullOrEmpty(text) || stronghold.Length == 0) return text ?? "";
        return StrongholdPlaceRx.Replace(text, stronghold);
    }

    private static bool TagMatches(string tag, string code, string societyName)
    {
        tag = (tag ?? "").Trim();
        if (tag.Equals(code, StringComparison.OrdinalIgnoreCase)) return true;
        return tag.Equals(societyName, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeCoords(Match m) =>
        $"{m.Groups[1].Value}{char.ToUpperInvariant(m.Groups[2].Value[0])}, {m.Groups[3].Value}{char.ToUpperInvariant(m.Groups[4].Value[0])}";

    private static bool SameCoords(string a, string b)
    {
        Match ma = CoordsRx.Match(a ?? ""), mb = CoordsRx.Match(b ?? "");
        if (!ma.Success || !mb.Success) return false;
        return string.Equals(NormalizeCoords(ma), NormalizeCoords(mb), StringComparison.OrdinalIgnoreCase);
    }
}
