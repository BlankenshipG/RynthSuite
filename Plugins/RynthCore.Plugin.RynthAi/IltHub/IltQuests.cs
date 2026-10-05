// IltQuests.cs — Quest Tracker UI and Quest Bonus (/qb) list for the ILT Hub Character tab.
//
// Quest Tracker reuses the meta QuestTracker (the same /myquests cache the meta
// expression engine reads) — it never runs a second parser. Friendly names come from:
//   1. an optional user-supplied quests.xml (<SuiteDir>\RynthAi\quests.xml),
//   2. the description the server prints in /myquests,
//   3. the raw flag key.
// UB's bundled quests.xml is NOT embedded (no license on UB content).
//
// QB: "/qb list" prints a completed and an incomplete section; both are tracked
// separately (UB merged them). Server throttles /qb to once per minute.
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

namespace RynthCore.Plugin.RynthAi.IltHub;

internal sealed class IltQuests : IIltFeature
{
    /// <summary>Optional user quest-name file (UB-style or simple key/name XML).</summary>
    public static readonly string QuestsXmlPath = System.IO.Path.Combine(RynthInstallPaths.RynthAiDir, @"quests.xml");

    private static readonly Regex KillTaskRegex = new(@"(killtask|killcount|slayerquest|totalgolem.*dead|(kills$))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex QbFlagLine = new(@"^[a-zA-Z0-9_\-!@\(\)?]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const long QuestRefreshCooldownMs = 60_000;
    private const long QbCooldownMs = 60_000;

    private readonly IltHubContext _ctx;
    private readonly Func<QuestTracker?> _tracker;
    private Dictionary<string, string> _friendlyNames = new(StringComparer.OrdinalIgnoreCase);
    private long _lastQuestRefreshAt = -QuestRefreshCooldownMs;
    private long _lastQbAt = -QbCooldownMs;

    // QB results (replaced wholesale; read by the render thread).
    private volatile string[] _qbCompleted = Array.Empty<string>();
    private volatile string[] _qbIncomplete = Array.Empty<string>();
    private volatile string _qbStatus = "not loaded";
    private long _qbCount = -1;

    // Render-thread UI state.
    private int _questView; // 0 timed, 1 kill tasks, 2 once, 3 all

    public IltQuests(IltHubContext ctx, Func<QuestTracker?> tracker)
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
        catch (Exception ex) { RynthLog.Exception(LogCat.IltQuests, ex, "quests.xml load"); }
        _friendlyNames = map;
    }

    // ── Commands ────────────────────────────────────────────────────────────

    /// <summary>Quiet /myquests refresh with a 60 s client-side cooldown (server throttle).</summary>
    public string RefreshQuests()
    {
        RynthLog.Trace(LogCat.IltQuests, $"RefreshQuests()");
        var t = _tracker();
        if (t == null) return "Quest tracker not ready.";
        if (_ctx.Options.IsOff(IltFeature.Quests) || t.Disabled) return "/myquests is disabled on this server.";
        long now = IltHubContext.NowMs;
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
        if (t == null) { _ctx.Chat("[ILT Hub] Quest tracker not ready."); return; }
        Regex rx;
        try { rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); }
        catch (ArgumentException ex) { _ctx.Chat("[ILT Hub] Bad pattern: " + ex.Message); return; }

        var hits = t.Snapshot().Where(r => rx.IsMatch(r.Key) || rx.IsMatch(FriendlyName(r))).OrderBy(r => r.Key).ToList();
        if (hits.Count == 0) { _ctx.Chat($"[ILT Hub] No quest flags match '{pattern}' ({t.Count} cached)."); return; }
        foreach (var r in hits.Take(5))
            _ctx.Chat($"[ILT Hub] {FriendlyName(r)} [{r.Key}] solves {r.Solves}{(r.MaxSolves > 0 ? "/" + r.MaxSolves : "")} - {IltParse.Duration(r.TimeUntilReady())}");
        if (hits.Count > 5) _ctx.Chat($"[ILT Hub] ...and {hits.Count - 5} more.");
    }

    /// <summary>Captures "/qb list" (quiet).</summary>
    public string RefreshQb()
    {
        RynthLog.Trace(LogCat.IltQuests, $"RefreshQb()");
        if (_ctx.Options.IsOff(IltFeature.Qb)) return "/qb is disabled on this server.";
        long now = IltHubContext.NowMs;
        if (now - _lastQbAt < QbCooldownMs)
            return $"Wait {(QbCooldownMs - (now - _lastQbAt)) / 1000}s (server allows one /qb per minute).";
        if (_ctx.Capture.IsPending("/qb list")) return "Already loading...";
        _lastQbAt = now;
        _qbStatus = "loading...";

        _ctx.Capture.Enqueue(new IltChatRequest
        {
            Command = "/qb list",
            IsResponseLine = IsQbLine,
            IsTerminator = t => t.Contains("Quest bonus list is empty", StringComparison.OrdinalIgnoreCase)
                             || t.Contains("\"qb\" is not currently enabled", StringComparison.OrdinalIgnoreCase)
                             || t.StartsWith("[QB] This command may only be run once every", StringComparison.OrdinalIgnoreCase),
            IdleEndMs = 5000,
            FirstLineTimeoutMs = 30000,
            Eat = true,
            OnComplete = OnQbResult,
        });
        return "Loading quest bonus list...";
    }

    private static bool IsQbLine(string t)
    {
        if (t.StartsWith("Your completed quest bonus list", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("Your incomplete quest bonus list", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("Your current quest bonus count", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!QbFlagLine.IsMatch(t)) return false;
        if (t.StartsWith("You ", StringComparison.Ordinal) || t.StartsWith("The ", StringComparison.Ordinal)) return false;
        return !(t.Contains("You have", StringComparison.OrdinalIgnoreCase) || t.Contains("command", StringComparison.OrdinalIgnoreCase)
                 || t.Contains("enabled", StringComparison.OrdinalIgnoreCase) || t.Contains("list", StringComparison.OrdinalIgnoreCase));
    }

    private void OnQbResult(IltChatResult r)
    {
        if (r.UnknownCommand) { _ctx.Options.Set(IltFeature.Qb, IltTri.Off); _qbStatus = "/qb not available"; return; }
        if (r.TerminatorLine != null)
        {
            if (r.TerminatorLine.Contains("not currently enabled", StringComparison.OrdinalIgnoreCase))
            { _ctx.Options.Set(IltFeature.Qb, IltTri.Off); _qbStatus = "/qb is disabled on this server"; return; }
            if (r.TerminatorLine.Contains("only be run once", StringComparison.OrdinalIgnoreCase))
            { _qbStatus = "rate limited by the server"; return; }
        }
        if (r.TimedOut) { _qbStatus = "no reply from server"; return; }
        _ctx.Options.Set(IltFeature.Qb, IltTri.On);

        var done = new List<string>();
        var todo = new List<string>();
        List<string>? current = null;
        foreach (string line in r.Lines)
        {
            if (line.StartsWith("Your completed quest bonus list", StringComparison.OrdinalIgnoreCase)) { current = done; continue; }
            if (line.StartsWith("Your incomplete quest bonus list", StringComparison.OrdinalIgnoreCase)) { current = todo; continue; }
            if (line.StartsWith("Your current quest bonus count", StringComparison.OrdinalIgnoreCase))
            {
                int colon = line.IndexOf(':');
                if (colon > 0) _qbCount = IltParse.ParseLeadingLong(line.Substring(colon + 1));
                continue;
            }
            if (line.Contains("is empty", StringComparison.OrdinalIgnoreCase)) continue;
            (current ?? done).Add(line.Trim());
        }
        done.Sort(StringComparer.OrdinalIgnoreCase);
        todo.Sort(StringComparer.OrdinalIgnoreCase);
        _qbCompleted = done.ToArray();
        _qbIncomplete = todo.ToArray();
        _qbStatus = $"{done.Count} completed, {todo.Count} incomplete";
    }

    /// <summary>Login probe tap: "/qb" (no args) prints the count line.</summary>
    public void ApplyQbProbe(List<string> lines)
    {
        RynthLog.Trace(LogCat.IltQuests, $"ApplyQbProbe(lines={lines.Count})");
        foreach (string line in lines)
            if (line.StartsWith("Your current quest bonus count", StringComparison.OrdinalIgnoreCase))
            {
                int colon = line.IndexOf(':');
                if (colon > 0) _qbCount = IltParse.ParseLeadingLong(line.Substring(colon + 1));
            }
    }

    // ── IIltFeature ─────────────────────────────────────────────────────────

    public void Tick(long nowMs) { }
    public bool OnChat(string text) => false;
    public void OnLogout()
    {
        _qbCompleted = Array.Empty<string>();
        _qbIncomplete = Array.Empty<string>();
        _qbCount = -1;
        _qbStatus = "not loaded";
    }

    // ── UI (render thread) ──────────────────────────────────────────────────

    /// <summary>
    /// Quest tracker body. Drawn inside the Hub's Quests tab, or filling the standalone
    /// "Quests" window when <paramref name="poppedOut"/> (the table then uses the full height).
    /// </summary>
    public void RenderQuestTracker(bool poppedOut = false)
    {
        var t = _tracker();
        if (t == null) { ImGui.TextDisabled("Quest tracker not ready."); return; }
        if (_ctx.Options.IsOff(IltFeature.Quests) || t.Disabled)
        {
            ImGui.TextColored(LegacyDashboardRenderer.ColTextMute, "/myquests is disabled on this server.");
            return;
        }
        var cs = _ctx.State.Character;

        if (ImGui.SmallButton("Refresh##quests")) _ctx.Post(() => _ctx.Chat("[ILT Hub] " + RefreshQuests()));
        ImGui.SameLine();
        if (ImGui.SmallButton("Reload quests.xml")) _ctx.Post(LoadFriendlyNames);
        ImGui.SameLine();
        if (!poppedOut && ImGui.SmallButton("Pop out##quests")) cs.QuestTrackerPoppedOut = true;
        if (!poppedOut && ImGui.IsItemHovered()) ImGui.SetTooltip("Undock the quest tracker into its own window.");
        if (!poppedOut) ImGui.SameLine();
        ImGui.TextDisabled(t.IsRefreshing ? "refreshing..." : t.LastStatus
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
                new Vector2(0, poppedOut ? -1 : 260)))
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
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(starred ? "Remove from favorites" : "Add to favorites");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(FriendlyName(r));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(r.MaxSolves > 0 ? $"{r.Solves}/{r.MaxSolves}" : r.Solves.ToString());
                ImGui.TableNextColumn();
                var left = r.TimeUntilReady();
                if (Classify(r) == QuestKind.Once) ImGui.TextDisabled("done");
                else if (left <= TimeSpan.Zero) ImGui.TextColored(LegacyDashboardRenderer.ColGreen, "ready");
                else ImGui.TextUnformatted(IltParse.Duration(left));
                ImGui.TableNextColumn(); ImGui.TextDisabled(r.Key);
            }
            ImGui.EndTable();
        }
    }

    private static int ViewToKind(int view) => view switch { 0 => (int)QuestKind.Timed, 1 => (int)QuestKind.KillTask, _ => (int)QuestKind.Once };

    // ── Favorites / floating windows ────────────────────────────────────────

    private static readonly Vector4 FavStarOn = new(1.00f, 0.82f, 0.25f, 1f);
    private static readonly Vector4 FavStarOff = new(0.45f, 0.48f, 0.52f, 1f);
    private const string FavHudPopup = "##questfavhudopts";

    /// <summary>Starred keys as a set (render thread; the list itself is only edited on the pump thread).</summary>
    private HashSet<string> FavoriteSet()
        => new(_ctx.State.Character.QuestFavorites.ToArray(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Stars / unstars a quest flag (pump thread).</summary>
    private void ToggleFavorite(string key)
    {
        var list = _ctx.State.Character.QuestFavorites;
        if (list.RemoveAll(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) == 0)
            list.Add(key.ToLowerInvariant());
    }

    /// <summary>
    /// Windows that live outside the Hub window: the undocked "Quests" tracker and the
    /// floating favorites HUD. Called every frame whether or not the Hub window is open.
    /// </summary>
    public void RenderFloatingWindows()
    {
        var cs = _ctx.State.Character;
        if (cs.QuestTrackerPoppedOut) RenderPoppedOutTracker(cs);
        if (cs.ShowQuestFavoritesHud) RenderFavoritesHud(cs);
    }

    private void RenderPoppedOutTracker(IltCharacterState cs)
    {
        ImGui.SetNextWindowSize(new Vector2(700, 520), ImGuiCond.FirstUseEver);
        bool open = true;
        if (ImGui.Begin("Quests##iltquestswin", ref open))
        {
            // Quest bonus first (collapsed by default) so the tracker table can fill the rest.
            if (ImGui.CollapsingHeader("Quest bonus (/qb)##qbwin")) RenderQb();
            RenderQuestTracker(poppedOut: true);
        }
        ImGui.End();
        // Closing the window docks quests back into the Hub's Quests tab.
        if (!open) cs.QuestTrackerPoppedOut = false;
    }

    /// <summary>Compact always-on list of starred quests and when each is ready.</summary>
    private void RenderFavoritesHud(IltCharacterState cs)
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
            ImGui.TextDisabled(t == null ? "Quest tracker not ready." : "No favorites yet - star quests in the tracker.");
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
                    else ImGui.TextUnformatted(IltParse.Duration(left));
                }
                ImGui.EndTable();
            }
        }

        if (ImGui.IsWindowHovered() && ImGui.IsMouseReleased(ImGuiMouseButton.Right)) ImGui.OpenPopup(FavHudPopup);
        if (ImGui.BeginPopup(FavHudPopup))
        {
            bool locked = cs.QuestFavoritesHudLocked;
            if (ImGui.Checkbox("Lock position", ref locked)) cs.QuestFavoritesHudLocked = locked;
            if (ImGui.MenuItem("Open quest tracker")) cs.QuestTrackerPoppedOut = true;
            if (ImGui.MenuItem("Refresh /myquests")) _ctx.Post(() => _ctx.Chat("[ILT Hub] " + RefreshQuests()));
            if (ImGui.MenuItem("Hide favorites HUD")) cs.ShowQuestFavoritesHud = false;
            ImGui.EndPopup();
        }
        ImGui.End();
    }

    public void RenderQb()
    {
        if (_ctx.Options.IsOff(IltFeature.Qb))
        {
            ImGui.TextColored(LegacyDashboardRenderer.ColTextMute, "/qb is disabled on this server.");
            return;
        }
        if (ImGui.SmallButton("Load /qb list")) _ctx.Post(() => _ctx.Chat("[ILT Hub] " + RefreshQb()));
        ImGui.SameLine();
        long count = _qbCount;
        ImGui.TextDisabled((count >= 0 ? $"QB count {IltParse.N0(count)} - " : "") + _qbStatus);

        string filter = _ctx.State.Character.QbFilter;
        ImGui.SetNextItemWidth(220);
        if (ImGui.InputTextWithHint("##qbfilter", "filter flags", ref filter, 64u))
            _ctx.State.Character.QbFilter = filter;

        RenderQbList("Incomplete", _qbIncomplete, filter, LegacyDashboardRenderer.ColAmber);
        RenderQbList("Completed", _qbCompleted, filter, LegacyDashboardRenderer.ColGreen);
    }

    private static void RenderQbList(string label, string[] items, string filter, Vector4 color)
    {
        var shown = filter.Length == 0 ? items : items.Where(i => i.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (ImGui.TreeNode($"{label} ({shown.Length})##qb{label}"))
        {
            if (ImGui.SmallButton($"Copy##qbc{label}")) ImGui.SetClipboardText(string.Join(Environment.NewLine, shown));
            foreach (string s in shown) ImGui.TextColored(color, s);
            ImGui.TreePop();
        }
    }
}
