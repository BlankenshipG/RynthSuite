// IltQuestBonus.cs — The ACECustom quest bonus list ("/qb list"), shown in RynthAi's universal
// Quests tab (RynthILT.Hub section "quests-qb") and read by the Temple attribute tracker.
// Originally the /qb half of SilentKelpie's IltQuests (ILT Hub, RynthAi); split out and moved to
// RynthILT 2026-10-13. The /myquests tracker half stayed in RynthAi (MiniRemote/QuestsTab.cs):
// /myquests is every server's, /qb is ACECustom's.
//
// "/qb list" prints a completed and an incomplete section; both are tracked separately (UB
// merged them). Server throttles /qb to once per minute.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using ImGuiNET;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthILT.Hub;

internal sealed class IltQuestBonus : IIltFeature
{
    private static readonly Regex KillTaskRegex = new(@"(killtask|killcount|slayerquest|totalgolem.*dead|(kills$))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex QbFlagLine = new(@"^[a-zA-Z0-9_\-!@\(\)?]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const long QbCooldownMs = 60_000;

    private readonly IltHubContext _ctx;
    private long _lastQbAt = -QbCooldownMs;

    // QB results (replaced wholesale; read by the render thread).
    private volatile string[] _qbCompleted = Array.Empty<string>();
    private volatile string[] _qbIncomplete = Array.Empty<string>();
    private volatile string _qbStatus = "not loaded";
    private long _qbCount = -1;
    private volatile int _qbGeneration; // bumped per successful "/qb list"; 0 = never loaded this session

    public IltQuestBonus(IltHubContext ctx) => _ctx = ctx;

    /// <summary>The /myquests flag kinds (RynthAi's QuestsTab.Classify, which the Temple tracker reads by).</summary>
    public enum QuestKind { Timed, KillTask, Once }

    public static QuestKind Classify(QuestRecord r)
    {
        if (KillTaskRegex.IsMatch(r.Key) && r.MaxSolves >= 0) return QuestKind.KillTask;
        if (r.MaxSolves == 1 && r.Solves <= 1) return QuestKind.Once;
        return QuestKind.Timed;
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
        _qbGeneration++;
    }

    /// <summary>Increments each time a "/qb list" reply is parsed (0 = not loaded this session).</summary>
    public int QbGeneration => _qbGeneration;

    /// <summary>
    /// Snapshot for the "RynthILT.Server" profile JSON (Oracle Quests / Fellowship). Pump thread.
    /// </summary>
    public IltQuestBonusInfo Snapshot() =>
        new(_ctx.Options.IsOff(IltFeature.Qb), _qbGeneration, _qbCount, _qbStatus, _qbCompleted, _qbIncomplete);

    /// <summary>True when the last "/qb list" (completed or incomplete) contains the flag.</summary>
    public bool QbHasFlag(string flag)
    {
        if (string.IsNullOrWhiteSpace(flag)) return false;
        foreach (string f in _qbCompleted)
            if (f.Equals(flag, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (string f in _qbIncomplete)
            if (f.Equals(flag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
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
        _qbGeneration = 0;
    }

    // ── UI (render thread) ──────────────────────────────────────────────────

    /// <summary>The Quests tab's "Quest bonus (/qb)" section (collapsed header; ILT worlds only).</summary>
    public void RenderSection()
    {
        if (ImGui.CollapsingHeader("Quest bonus (/qb)##qbwin")) RenderQb();
    }

    public void RenderQb()
    {
        if (_ctx.Options.IsOff(IltFeature.Qb))
        {
            ImGui.TextColored(IltStyle.ColTextMute, "/qb is disabled on this server.");
            return;
        }
        if (ImGui.SmallButton("Load /qb list")) _ctx.Post(() => _ctx.Chat("[Mini Remote] " + RefreshQb()));
        ImGui.SameLine();
        long count = _qbCount;
        SafeImGui.TextDisabled((count >= 0 ? $"QB count {IltParse.N0(count)} - " : "") + _qbStatus);

        string filter = _ctx.State.Character.QbFilter;
        ImGui.SetNextItemWidth(220);
        if (ImGui.InputTextWithHint("##qbfilter", "filter flags", ref filter, 64u))
            _ctx.State.Character.QbFilter = filter;

        RenderQbList("Incomplete", _qbIncomplete, filter, IltStyle.ColAmber);
        RenderQbList("Completed", _qbCompleted, filter, IltStyle.ColGreen);
    }

    private static void RenderQbList(string label, string[] items, string filter, Vector4 color)
    {
        var shown = filter.Length == 0 ? items : items.Where(i => i.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (ImGui.TreeNode($"{label} ({shown.Length})##qb{label}"))
        {
            if (ImGui.SmallButton($"Copy##qbc{label}")) ImGui.SetClipboardText(string.Join(Environment.NewLine, shown));
            foreach (string s in shown) SafeImGui.TextColored(color, s);
            ImGui.TreePop();
        }
    }
}
