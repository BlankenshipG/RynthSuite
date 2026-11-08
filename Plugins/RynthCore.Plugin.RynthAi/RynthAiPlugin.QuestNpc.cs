using System;
using System.Collections.Generic;
using RynthCore.Loot;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

// The quest run's NPCs (2026-11-07). RynthOracle's Run this quest goes to the person a wiki step names
// ("Talk to Xsao Lann in Fort Tethana", "Return the collar to Xsao") and, there, sends through the
// "questloot" broker action:
//   "talk <name> || <name> ..."                      use the first of them standing near you (talk to them)
//   "handin <name> || <name> ... | <item> | <item>"  give them every carried item of those names (AC's give,
//                                                    the give queue); none carried: talk to them instead
// The names are the step's candidates; the NPC that answers one best is taken (Mara al-Luq herself before a
// "Yaraq Guard" for the name Yaraq), only an NPC, a vendor or a creature that can't be attacked, within QuestNpcRange
// (a portal or a player of that name never is). An item's name matches as the wiki writes it or without
// a note in brackets at the end ("Darling's Collar (Quest Item)" is "Darling's Collar" in the game).
// RynthAi's own items, attuned and equipped ones are never handed over.
//   "have <item> | <item> ..."                     (2026-11-08) each one already carried says "Quest: got <item>
//                                                    (already in your pack)." for the run's step; the others are
//                                                    picked up off corpses and the ground (the quest loot list:
//                                                    GroundLootController takes quest items with Ground Loot off)
// Every quest item picked up says "[RynthAi] Quest: got <name>." (NoteQuestItemGot): the run's item steps end on it.
// Pump thread.
public sealed partial class RynthAiPlugin
{
    /// <summary>How near the NPC must be (metres).</summary>
    internal const double QuestNpcRange = 60.0;

    /// <summary>
    /// A talk / hand-in whose NPC wasn't in sight yet (2026-11-07: RynthNav arrives before the NPC is
    /// drawn, or a few metres short): tried again every QuestNpcRetryMs for QuestNpcRetryForMs.
    /// </summary>
    private string _questNpcPending = string.Empty;
    private long _questNpcNextAt, _questNpcUntil;
    internal const long QuestNpcRetryMs = 2000, QuestNpcRetryForMs = 20000;

    /// <summary>Each tick: the pending talk / hand-in, tried again when it is due. Pump thread.</summary>
    private void TickQuestNpcRetry()
    {
        if (_questNpcPending.Length == 0) return;
        long now = Environment.TickCount64;
        if (now < _questNpcNextAt) return;
        string line = _questNpcPending;
        bool last = now >= _questNpcUntil;
        _questNpcPending = string.Empty;
        var r = TryQuestNpc(line, retrying: !last);
        if (r is { } res && (res.Ok || last)) ChatLine(res.Text);
    }

    /// <summary>A quest NPC verb's answer, or null when <paramref name="line"/> isn't one.</summary>
    private (bool Ok, string Text)? TryQuestNpc(string line) => TryQuestNpc(line, retrying: false);

    private (bool Ok, string Text)? TryQuestNpc(string line, bool retrying)
    {
        line = (line ?? "").Trim();
        int sp = line.IndexOf(' ');
        string verb = (sp < 0 ? line : line.Substring(0, sp)).ToLowerInvariant();
        string rest = sp < 0 ? "" : line.Substring(sp + 1).Trim();
        if (verb is not ("talk" or "handin" or "have")) return null;
        if (verb == "have") return QuestHave(rest);

        ParseQuestNpcLine(rest, out List<string> names, out List<string> items);
        if (names.Count == 0)
            return (false, "[RynthAi] questloot talk <name> || <name> | questloot handin <name> || <name> | <item> | <item> (RynthOracle's quest run sends these)");
        if (_objectCache == null) return (false, "[RynthAi] Quest: the object cache isn't ready.");

        WorldObject? npc = FindQuestNpc(names);
        if (npc == null)
        {
            // Not in sight yet: look again for a while (the first try starts the clock).
            long now = Environment.TickCount64;
            if (!retrying) _questNpcUntil = now + QuestNpcRetryForMs;
            if (now < _questNpcUntil)
            {
                _questNpcPending = line;
                _questNpcNextAt = now + QuestNpcRetryMs;
                return (true, $"[RynthAi] Quest: looking for {string.Join(" or ", names)} (trying for {QuestNpcRetryForMs / 1000} s).");
            }
            _questNpcUntil = 0;
            return (false, $"[RynthAi] Quest: no {string.Join(" or ", names)} near you to {(verb == "talk" ? "talk to" : "hand the items to")}.");
        }
        _questNpcPending = string.Empty;
        _questNpcUntil = 0;

        if (verb == "handin" && items.Count > 0)
        {
            var given = new List<string>();
            var seen = new HashSet<int>();
            foreach (string item in items)
            {
                foreach (WorldObject wo in GiveCandidates(n => QuestItemNameMatches(n, item)))
                {
                    if (!seen.Add(wo.Id)) continue;
                    string? why = ItemProtectionReason(wo, includeLootKeep: false);
                    if (why != null) { Log($"[Quest] not handing 0x{(uint)wo.Id:X8} '{wo.Name}' to {npc.Name}: {why}"); continue; }
                    EnqueueGive((uint)wo.Id, (uint)npc.Id, 0);   // the whole stack
                    Log($"[Quest] queued 0x{(uint)wo.Id:X8} '{wo.Name}' -> {npc.Name}");
                    given.Add(wo.Name);
                }
            }
            if (given.Count > 0)
                return (true, $"[RynthAi] Quest: handing {string.Join(", ", given)} to {npc.Name}.");
            // Nothing of the quest's carried: talking is what's left to do there.
            Host.UseFor((uint)npc.Id, "Quest", "quest talk", UseKind.Asked);
            return (true, $"[RynthAi] Quest: none of {string.Join(", ", items)} is in your pack; talking to {npc.Name}.");
        }
        Host.UseFor((uint)npc.Id, "Quest", "quest talk", UseKind.Asked);
        return (true, $"[RynthAi] Quest: talking to {npc.Name}.");
    }

    /// <summary>
    /// "have item | item": the ones carried say so ("Quest: got X (already in your pack).", a line each, for
    /// RynthOracle's run); the reply names the rest, which the quest loot list picks up.
    /// </summary>
    private (bool Ok, string Text) QuestHave(string rest)
    {
        var items = new List<string>();
        foreach (string part in (rest ?? "").Replace("||", "|").Split('|'))
        {
            string it = part.Trim();
            if (it.Length > 0 && !items.Exists(x => x.Equals(it, StringComparison.OrdinalIgnoreCase))) items.Add(it);
        }
        if (items.Count == 0) return (false, "[RynthAi] questloot have <item> | <item> (RynthOracle's quest run sends this)");
        if (_objectCache == null) return (false, "[RynthAi] Quest: the object cache isn't ready.");
        var missing = new List<string>();
        foreach (string it in items)
        {
            if (GiveCandidates(n => QuestItemNameMatches(n, it)).Count > 0) ChatLine($"{QuestGotPrefix}{it} (already in your pack).");
            else missing.Add(it);
        }
        if (missing.Count == 0) return (true, "[RynthAi] Quest: the step's items are all in your pack.");
        return (true, $"[RynthAi] Quest: {string.Join(", ", missing)} not in your pack yet: picking {(missing.Count == 1 ? "it" : "them")} up off corpses and the ground near you (within {QuestGroundRange:F0} m).");
    }

    /// <summary>The line a quest item got starts with (RynthOracle's QuestRunner reads it).</summary>
    internal const string QuestGotPrefix = "[RynthAi] Quest: got ";

    /// <summary>A quest loot list item came into the pack (a corpse or the ground): say so, for the run's step.</summary>
    private void NoteQuestItemGot(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name) && QuestLootList.Wants(name)) ChatLine($"{QuestGotPrefix}{name.Trim()}.");
    }

    /// <summary>"A || B | item | item": the names before the first single '|', the items after it.</summary>
    internal static void ParseQuestNpcLine(string rest, out List<string> names, out List<string> items)
    {
        names = new List<string>();
        items = new List<string>();
        string[] parts = (rest ?? "").Replace("||", "\u0001").Split('|');
        foreach (string n in parts[0].Split('\u0001'))
            if (n.Trim().Length > 0 && !names.Contains(n.Trim())) names.Add(n.Trim());
        for (int i = 1; i < parts.Length; i++)
        {
            string it = parts[i].Replace("\u0001", "").Trim();
            if (it.Length > 0 && !items.Contains(it)) items.Add(it);
        }
    }

    /// <summary>An item's name as the game shows it against the name the wiki writes (any case; a bracketed note optional).</summary>
    internal static bool QuestItemNameMatches(string gameName, string wikiName)
    {
        if (string.Equals(gameName, wikiName, StringComparison.OrdinalIgnoreCase)) return true;
        string bare = BareQuestName(wikiName);
        return bare.Length > 0 && string.Equals(BareQuestName(gameName), bare, StringComparison.OrdinalIgnoreCase);
    }

    private static string BareQuestName(string name)
    {
        name = (name ?? "").Trim();
        int open = name.LastIndexOf(" (", StringComparison.Ordinal);
        return open > 0 && name.EndsWith(")", StringComparison.Ordinal) ? name.Substring(0, open).Trim() : name;
    }

    /// <summary>
    /// How well a landscape object's name answers a step's name: 3 the same, 2 the object's name starts with
    /// it ("Xsao" is Xsao Lann), 1 it is a whole word of it, 0 no.
    /// </summary>
    internal static int QuestNpcScore(string objectName, string name)
    {
        if (string.Equals(objectName, name, StringComparison.OrdinalIgnoreCase)) return 3;
        if (name.Length < 3) return 0;
        if (objectName.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase)) return 2;
        int at = objectName.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        while (at >= 0)
        {
            bool startOk = at == 0 || !char.IsLetterOrDigit(objectName[at - 1]);
            int end = at + name.Length;
            bool endOk = end >= objectName.Length || !char.IsLetterOrDigit(objectName[end]);
            if (startOk && endOk) return 1;
            at = objectName.IndexOf(name, at + 1, StringComparison.OrdinalIgnoreCase);
        }
        return 0;
    }

    /// <summary>An NPC, a vendor, or a creature that can't be attacked (how AC files most quest givers).</summary>
    private bool IsQuestPerson(WorldObject wo) =>
        wo.ObjectClass is AcObjectClass.Npc or AcObjectClass.Vendor
        || (wo.ObjectClass == AcObjectClass.Monster && Host.HasObjectIsAttackable && !Host.ObjectIsAttackable(unchecked((uint)wo.Id)));

    /// <summary>
    /// The NPC the names mean: the best match over all of them (the same name before one that starts with
    /// it, before a word of it; then the earlier name, then the nearest), an NPC or a vendor in range.
    /// </summary>
    private WorldObject? FindQuestNpc(List<string> names)
    {
        if (_objectCache == null) return null;
        WorldObject? best = null;
        int bestScore = 0, bestOrder = int.MaxValue;
        double bestDist = double.MaxValue;
        foreach (WorldObject wo in _objectCache.GetLandscapeObjects())
        {
            if (!IsQuestPerson(wo)) continue;
            int score = 0, order = int.MaxValue;
            for (int i = 0; i < names.Count; i++)
            {
                int sc = QuestNpcScore(wo.Name, names[i]);
                if (sc > score) { score = sc; order = i; }
            }
            if (score == 0) continue;
            double dist = _playerId != 0 ? _objectCache.Distance((int)_playerId, wo.Id) : double.MaxValue;
            if (dist != double.MaxValue && dist > QuestNpcRange) continue;
            bool better = score > bestScore
                || (score == bestScore && order < bestOrder)
                || (score == bestScore && order == bestOrder && dist < bestDist);
            if (better) { best = wo; bestScore = score; bestOrder = order; bestDist = dist; }
        }
        return best;
    }
}
