// QuestsTab.cs — The Mini Remote's Quests tab: the /myquests tracker and the floating favorites HUD.
// Originally SilentKelpie's IltQuests (ILT Hub). The tab is universal (every server has /myquests),
// so it stayed in RynthAi when the ILT Hub went to the RynthILT plugin (2026-10-13); its Quest bonus
// (/qb) list is ACECustom's and moved with RynthILT, which draws it at the top of this tab
// (RynthILT.Hub section "quests-qb") on Infinite Leaftide.
//
// Quest Tracker reuses the meta QuestTracker (the same /myquests cache the meta
// expression engine reads) — it never runs a second parser. Friendly names come from:
//   1. an optional user-supplied quests.xml (<SuiteDir>\RynthAi\quests.xml),
//   2. the description the server prints in /myquests,
//   3. the raw flag key.
// UB's bundled quests.xml is NOT embedded (no license on UB content).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Xml;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.Install;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.MiniRemote;

internal sealed class QuestsTab
{
    /// <summary>Optional user quest-name file (UB-style or simple key/name XML).</summary>
    public static readonly string QuestsXmlPath = System.IO.Path.Combine(RynthInstallPaths.RynthAiDir, @"quests.xml");

    private static readonly Regex KillTaskRegex = new(@"(killtask|killcount|slayerquest|totalgolem.*dead|(kills$))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private const long QuestRefreshCooldownMs = 60_000;

    private readonly MiniRemoteContext _ctx;
    private readonly Func<QuestTracker?> _tracker;
    private Dictionary<string, string> _friendlyNames = new(StringComparer.OrdinalIgnoreCase);
    private long _lastQuestRefreshAt = -QuestRefreshCooldownMs;
    // Render-thread UI state.
    private int _questView; // 0 timed, 1 kill tasks, 2 once, 3 all

    public QuestsTab(MiniRemoteContext ctx, Func<QuestTracker?> tracker)
    {
        _ctx = ctx;
        _tracker = tracker;
        LoadFriendlyNames();
    }

    public enum QuestKind { Timed, KillTask, Once }

    public static QuestKind Classify(QuestRecord r)
    {
        if (KillTaskRegex.IsMatch(r.Key) && r.MaxSolves >= 0) return QuestKind.KillTask;
        if (r.MaxSolves == 1 && r.Solves <= 1) return QuestKind.Once;
        return QuestKind.Timed;
    }

    public string FriendlyName(QuestRecord r)
    {
        if (_friendlyNames.TryGetValue(r.Key, out string? n) && !string.IsNullOrWhiteSpace(n)) return n;
        if (!string.IsNullOrWhiteSpace(r.Description)) return r.Description;
        return r.Key;
    }

    // ── quests.xml (optional) ───────────────────────────────────────────────

    /// <summary>Reads any element carrying a key/flag attribute plus a name/description.</summary>
    public void LoadFriendlyNames()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(QuestsXmlPath))
            {
                using var reader = XmlReader.Create(QuestsXmlPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true });
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || !reader.HasAttributes) continue;
                    string? key = null, name = null;
                    while (reader.MoveToNextAttribute())
                    {
                        string a = reader.Name.ToLowerInvariant();
                        if (a is "key" or "flag" or "questflag" or "id") key = reader.Value;
                        else if (a is "name" or "description" or "title" or "friendlyname") name ??= reader.Value;
                    }
                    reader.MoveToElement();
                    if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(name))
                        map[key.Trim().ToLowerInvariant()] = name.Trim();
                }
            }
        }
        catch (Exception ex) { RynthLog.Exception(LogCat.Quests, ex, "quests.xml load"); }
        _friendlyNames = map;
    }

    // ── Commands ────────────────────────────────────────────────────────────

    /// <summary>Quiet /myquests refresh with a 60 s client-side cooldown (server throttle).</summary>
    public string RefreshQuests()
    {
        RynthLog.Trace(LogCat.Quests, $"RefreshQuests()");
        var t = _tracker();
        if (t == null) return "Quest tracker not ready.";
        if (_ctx.Ilt.IsOff(RynthIltHub.FeatureQuests) || t.Disabled) return "/myquests is disabled on this server.";
        long now = MiniRemoteContext.NowMs;
        if (now - _lastQuestRefreshAt < QuestRefreshCooldownMs)
            return $"Wait {(QuestRefreshCooldownMs - (now - _lastQuestRefreshAt)) / 1000}s (server allows one /myquests per minute).";
        _lastQuestRefreshAt = now;
        t.Refresh(quiet: true);
        return "Refreshing quests...";
    }

    /// <summary>"/ra quests check &lt;regex&gt;" — lists up to 5 matching flags in chat.</summary>
    public void CheckCommand(string pattern)
    {
        var t = _tracker();
        if (t == null) { _ctx.Chat("[Mini Remote] Quest tracker not ready."); return; }
        Regex rx;
        try { rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); }
        catch (ArgumentException ex) { _ctx.Chat("[Mini Remote] Bad pattern: " + ex.Message); return; }

        var hits = t.Snapshot().Where(r => rx.IsMatch(r.Key) || rx.IsMatch(FriendlyName(r))).OrderBy(r => r.Key).ToList();
        if (hits.Count == 0) { _ctx.Chat($"[Mini Remote] No quest flags match '{pattern}' ({t.Count} cached)."); return; }
        foreach (var r in hits.Take(5))
            _ctx.Chat($"[Mini Remote] {FriendlyName(r)} [{r.Key}] solves {r.Solves}{(r.MaxSolves > 0 ? "/" + r.MaxSolves : "")} - {MiniRemoteText.Duration(r.TimeUntilReady())}");
        if (hits.Count > 5) _ctx.Chat($"[Mini Remote] ...and {hits.Count - 5} more.");
    }

    // ── UI (render thread) ──────────────────────────────────────────────────

    /// <summary>Quest tracker body, filling the Mini Remote's Quests tab (the table uses the remaining height).</summary>
    public void RenderQuestTracker()
    {
        var t = _tracker();
        if (t == null) { ImGui.TextDisabled("Quest tracker not ready."); return; }
        if (_ctx.Ilt.IsOff(RynthIltHub.FeatureQuests) || t.Disabled)
        {
            ImGui.TextColored(LegacyDashboardRenderer.ColTextMute, "/myquests is disabled on this server.");
            return;
        }
        var cs = _ctx.State.Quests;

        if (ImGui.SmallButton("Refresh##quests")) _ctx.Post(() => _ctx.Chat("[Mini Remote] " + RefreshQuests()));
        ImGui.SameLine();
        if (ImGui.SmallButton("Reload quests.xml")) _ctx.Post(LoadFriendlyNames);
        ImGui.SameLine();
        SafeImGui.TextDisabled(t.IsRefreshing ? "refreshing..." : t.LastStatus
            + (t.LastRefreshUtc == DateTime.MinValue ? "" : " @ " + t.LastRefreshUtc.ToLocalTime().ToString("t")));

        string filter = cs.QuestFilter;
        ImGui.SetNextItemWidth(220);
        if (ImGui.InputTextWithHint("##qfilter", "filter by name or key", ref filter, 64u))
            cs.QuestFilter = filter;
        ImGui.SameLine();
        ImGui.RadioButton("Timed", ref _questView, 0); ImGui.SameLine();
        ImGui.RadioButton("Kill tasks", ref _questView, 1); ImGui.SameLine();
        ImGui.RadioButton("Once", ref _questView, 2); ImGui.SameLine();
        ImGui.RadioButton("All", ref _questView, 3);

        bool favOnly = cs.QuestFavoritesOnly;
        if (ImGui.Checkbox("Favorites only", ref favOnly)) cs.QuestFavoritesOnly = favOnly;
        ImGui.SameLine();
        bool favHud = cs.ShowQuestFavoritesHud;
        if (ImGui.Checkbox("Show floating favorites HUD", ref favHud)) cs.ShowQuestFavoritesHud = favHud;
        ImGui.SameLine();
        ImGui.TextDisabled("(click * to star a quest)");

        var favorites = FavoriteSet();
        var rows = t.Snapshot()
            .Where(r => favOnly ? favorites.Contains(r.Key) : _questView == 3 || (int)Classify(r) == ViewToKind(_questView))
            .Where(r => filter.Length == 0 || r.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || FriendlyName(r).Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.TimeUntilReady())
            .ThenBy(r => FriendlyName(r), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (ImGui.BeginTable("##iltquests", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable,
                new Vector2(0, -1)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("*", ImGuiTableColumnFlags.WidthFixed, 18);
            ImGui.TableSetupColumn("Quest");
            ImGui.TableSetupColumn("Solves", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Ready", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("Key");
            ImGui.TableHeadersRow();
            foreach (var r in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                bool starred = favorites.Contains(r.Key);
                ImGui.PushStyleColor(ImGuiCol.Text, starred ? FavStarOn : FavStarOff);
                if (ImGui.Selectable($"*##fav{r.Key}", false))
                {
                    string key = r.Key;
                    _ctx.Post(() => ToggleFavorite(key));
                }
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered()) SafeImGui.SetTooltip(starred ? "Remove from favorites" : "Add to favorites");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(FriendlyName(r));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(r.MaxSolves > 0 ? $"{r.Solves}/{r.MaxSolves}" : r.Solves.ToString());
                ImGui.TableNextColumn();
                var left = r.TimeUntilReady();
                if (Classify(r) == QuestKind.Once) ImGui.TextDisabled("done");
                else if (left <= TimeSpan.Zero) ImGui.TextColored(LegacyDashboardRenderer.ColGreen, "ready");
                else ImGui.TextUnformatted(MiniRemoteText.Duration(left));
                ImGui.TableNextColumn(); SafeImGui.TextDisabled(r.Key);
            }
            ImGui.EndTable();
        }
    }

    private static int ViewToKind(int view) => view switch { 0 => (int)QuestKind.Timed, 1 => (int)QuestKind.KillTask, _ => (int)QuestKind.Once };

    // ── Favorites / the Quests tab ──────────────────────────────────────────

    private static readonly Vector4 FavStarOn = new(1.00f, 0.82f, 0.25f, 1f);
    private static readonly Vector4 FavStarOff = new(0.45f, 0.48f, 0.52f, 1f);
    private const string FavHudPopup = "##questfavhudopts";

    /// <summary>Starred keys as a set (render thread; the list itself is only edited on the pump thread).</summary>
    private HashSet<string> FavoriteSet()
        => new(_ctx.State.Quests.QuestFavorites.ToArray(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Stars / unstars a quest flag (pump thread).</summary>
    private void ToggleFavorite(string key)
    {
        var list = _ctx.State.Quests.QuestFavorites;
        if (list.RemoveAll(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) == 0)
            list.Add(key.ToLowerInvariant());
    }

    /// <summary>The floating favorites HUD (the tracker itself is the Mini Remote's Quests tab). Called every frame.</summary>
    public void RenderFloatingWindows()
    {
        var cs = _ctx.State.Quests;
        if (cs.ShowQuestFavoritesHud) RenderFavoritesHud(cs);
    }

    /// <summary>The Mini Remote's Quests tab: the quest bonus (collapsed; RynthILT's, ILT only), then the tracker filling the rest.</summary>
    public void RenderTab()
    {
        // The tab is universal (/myquests); the quest bonus list (/qb) is ILT's and RynthILT's.
        _ctx.Ilt.RenderSection(RynthIltHub.SectionQuestBonus);
        RenderQuestTracker();
    }

    /// <summary>Compact always-on list of starred quests and when each is ready.</summary>
    private void RenderFavoritesHud(QuestsTabState cs)
    {
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar
                    | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;
        if (cs.QuestFavoritesHudLocked) flags |= ImGuiWindowFlags.NoMove;
        ImGui.SetNextWindowPos(new Vector2(260, 120), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(0.55f);
        if (!ImGui.Begin("Quest favorites##iltquestfavhud", flags)) { ImGui.End(); return; }

        ImGui.TextColored(LegacyDashboardRenderer.ColTeal, "Quest favorites");
        var t = _tracker();
        var favorites = FavoriteSet();
        if (t == null || favorites.Count == 0)
        {
            SafeImGui.TextDisabled(t == null ? "Quest tracker not ready." : "No favorites yet - star quests in the tracker.");
        }
        else
        {
            var rows = t.Snapshot().Where(r => favorites.Contains(r.Key))
                        .OrderBy(r => r.TimeUntilReady()).ThenBy(r => FriendlyName(r), StringComparer.OrdinalIgnoreCase).ToArray();
            if (rows.Length == 0) ImGui.TextDisabled("Starred quests not in /myquests yet.");
            if (ImGui.BeginTable("##questfavrows", 2, ImGuiTableFlags.SizingFixedFit))
            {
                foreach (var r in rows)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(FriendlyName(r));
                    ImGui.TableNextColumn();
                    var left = r.TimeUntilReady();
                    if (Classify(r) == QuestKind.Once) ImGui.TextDisabled("done");
                    else if (left <= TimeSpan.Zero) ImGui.TextColored(LegacyDashboardRenderer.ColGreen, "ready");
                    else ImGui.TextUnformatted(MiniRemoteText.Duration(left));
                }
                ImGui.EndTable();
            }
        }

        if (ImGui.IsWindowHovered() && ImGui.IsMouseReleased(ImGuiMouseButton.Right)) ImGui.OpenPopup(FavHudPopup);
        if (ImGui.BeginPopup(FavHudPopup))
        {
            bool locked = cs.QuestFavoritesHudLocked;
            if (ImGui.Checkbox("Lock position", ref locked)) cs.QuestFavoritesHudLocked = locked;
            if (ImGui.MenuItem("Open quest tracker")) _ctx.ShowTab?.Invoke(MiniRemoteTabs.Quests);
            if (ImGui.MenuItem("Refresh /myquests")) _ctx.Post(() => _ctx.Chat("[Mini Remote] " + RefreshQuests()));
            if (ImGui.MenuItem("Hide favorites HUD")) cs.ShowQuestFavoritesHud = false;
            ImGui.EndPopup();
        }
        ImGui.End();
    }
}
