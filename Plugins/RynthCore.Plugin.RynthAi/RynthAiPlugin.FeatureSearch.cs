// RynthAiPlugin.FeatureSearch.cs — the Find a Feature window and /ra find (2026-11-13, approved by the
// owner: players couldn't find RynthSuite's features).
//
// The catalog is Docs/features/*.json (every RynthSuite plugin's features and RynthCore's), embedded in
// RynthAi and read once (Features/FeatureCatalog.cs); the website's search reads the same files. The
// search is Features/FeatureSearch.cs (pure, tested), the window Features/FeatureSearchWindow.cs.
//
// Ways in, buttons first: the dashboard's options menu (⋯ > Find a feature) and /ra find [words], which
// opens the window with the words in the box and says how many match. The window is a plugin window
// (API v89) declared in the sections manifest without a bar button (the bar is full). Without plugin
// windows /ra find prints the best matches in chat instead.
//
// Threads: /ra find runs on the pump thread and leaves its words for the window (a volatile string);
// the window is drawn on the render thread and sends Open's command to the tick (QueueDashCommand).
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.Features;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    /// <summary>The window's title (its panel key) and surface (Huds/SectionsManifest.cs).</summary>
    internal const string FeatureSearchTitle = "Find a Feature", FeatureSearchSurface = "window:featuresearch";

    internal const string FeatureSearchUsage = "Usage: /ra find [words] (no words: browse every feature)";

    /// <summary>The most a /ra find may type (the window's box holds as much).</summary>
    internal const int FeatureQueryMax = 96;

    /// <summary>How many matches /ra find prints in chat when the window can't open.</summary>
    internal const int FeatureChatMatches = 5;

    private volatile string? _featureQueryPending;                       // /ra find's words for the window (pump writes, render takes)
    private FeatureSearchWindow? _featureWindow;                           // render thread
    private long _featureDrawnMs = long.MinValue / 2;                      // render thread
    private int _featureFailures;                                          // render thread

    /// <summary>The window goes in the manifest on an engine with plugin windows (v89).</summary>
    private bool FeatureSearchWindowWanted => Host.HasUiTheme && Host.HasSetPanelOpen;

    /// <summary>The catalog, read on first use (any thread); a bad file logs one line.</summary>
    internal FeatureCatalog FeatureList => FeatureCatalog.Shared(line => Host.Log(line));

    /// <summary>The words /ra find left for the window (tests).</summary>
    internal string? FeatureQueryPendingForTests => _featureQueryPending;

    // ── /ra find ─────────────────────────────────────────────────────────

    /// <summary>
    /// "/ra find [words]": opens the window with the words in the box and says how many features match
    /// (no words: how many there are to browse). "?" says the usage. Every form replies. Pump thread.
    /// </summary>
    private void HandleFindCommand(string[] parts)
    {
        string words = parts.Length > 2 ? string.Join(" ", parts, 2, parts.Length - 2).Trim() : string.Empty;
        if (words is "?" or "-?" or "/?")
        {
            ChatLine("[RynthAi] /ra find <words> — opens Find a Feature with every feature those words match (\"/ra find death\", \"/ra find vend\")");
            ChatLine("[RynthAi] /ra find — opens it to browse every feature by group (also: the RynthAi window's ⋯ menu > Find a feature)");
            return;
        }
        if (words.Length > FeatureQueryMax)
        {
            ReplyFail($"[RynthAi] Find a Feature: that's {words.Length} characters to look for, {FeatureQueryMax} at most. {FeatureSearchUsage}");
            return;
        }
        if (words.Length > 0 && FeatureSearch.Words(words).Count == 0)
        {
            ReplyFail($"[RynthAi] Find a Feature: '{words}' has no letters or numbers to look for. {FeatureSearchUsage}");
            return;
        }
        FeatureCatalog catalog = FeatureList;
        if (catalog.Entries.Count == 0)
        {
            ReplyFail("[RynthAi] Find a Feature: the feature list didn't load (the RynthCore log says why).");
            return;
        }

        List<FeatureHit> hits = FeatureSearch.Search(catalog.Entries, words);
        _featureQueryPending = words;
        int open = OpenFeatureWindow();
        string where = open == 1 ? " (Find a Feature open)"
            : open == -2 ? string.Empty
            : " (the window isn't there yet: RynthCore adds it a few seconds after login)";
        string count = FeatureSearch.CountLine(hits.Count, words);
        if (words.Length > 0 && hits.Count == 0)
            ReplyFail($"[RynthAi] Find a Feature: {count}{where}. Try fewer words, or the start of one. {FeatureSearchUsage}");
        else if (words.Length == 0)
            ReplyOk($"[RynthAi] Find a Feature: {count} to browse by group{where}.");
        else
            ReplyOk($"[RynthAi] Find a Feature: {count}{where}.");
        // No window to show them in: the best few in chat.
        if (open != 1 && words.Length > 0)
            for (int i = 0; i < hits.Count && i < FeatureChatMatches; i++) ChatLine("[RynthAi]   " + ChatText(hits[i].Entry));
    }

    /// <summary>"Name (Plugin): where; /command" for chat.</summary>
    internal static string ChatText(FeatureEntry e)
    {
        string text = e.Name + (e.Plugin.Length > 0 ? " (" + e.Plugin + ")" : string.Empty);
        if (e.Where.Count > 0) text += ": " + e.Where[0];
        if (e.Command.Length > 0) text += (e.Where.Count > 0 ? "; " : ": ") + e.Command;
        return text;
    }

    /// <summary>
    /// The window open: 1 open, 0 closed, -1 not there yet (RynthCore adds plugin windows a few seconds
    /// after login), -2 no plugin windows on this RynthCore. Any thread.
    /// </summary>
    private int OpenFeatureWindow()
    {
        if (!Host.HasSetPanelOpen) return -2;
        int r = Host.SetPanelOpen(FeatureSearchTitle, RynthPanelOpen.Open);
        return r < 0 ? -1 : r;
    }

    /// <summary>The dashboard's ⋯ > Find a feature: the window open, the box ready to type in. Any thread.</summary>
    internal void OpenFeatureSearchWindow()
    {
        if (OpenFeatureWindow() == -1)
            ChatLine("[RynthAi] Find a Feature isn't there yet (RynthCore adds it a few seconds after login). Try again in a moment, or type /ra find <words>.");
    }

    /// <summary>The window's Open: a feature's open command, run as typed (on the tick).</summary>
    private void RunFeatureOpen(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        if (!Host.HasInvokeChatParser)
        {
            ChatLine("[RynthAi] Find a Feature: this RynthCore can't run commands for RynthAi; type " + command);
            return;
        }
        QueueDashCommand(command.Trim());
    }

    // ── Window (render thread) ───────────────────────────────────────────

    /// <summary>
    /// RynthPluginRenderEmbed "window:featuresearch": the Find a Feature window body, docked or popped out.
    /// 1 drawn, 0 not ready, -2 failed (three draws in a row threw).
    /// </summary>
    internal int RenderFeatureSearch(bool popped, out Vector2 want)
    {
        want = default;
        if (!_initialized || Host.ImGuiContext == IntPtr.Zero) return 0;
        long now = Environment.TickCount64;
        bool reopened = now - _featureDrawnMs > 1000;
        _featureDrawnMs = now;
        IntPtr previous = ImGui.GetCurrentContext();
        ImGui.SetCurrentContext(EmbedContext());
        try
        {
            _featureWindow ??= new FeatureSearchWindow(() => FeatureList.Entries, RunFeatureOpen);
            string? pending = Interlocked.Exchange(ref _featureQueryPending, null);
            if (pending != null) _featureWindow.SetQuery(pending);
            else if (reopened) _featureWindow.Reopened();
            want = _featureWindow.Draw();
            _featureFailures = 0;
            return 1;
        }
        catch (Exception ex)
        {
            RynthLog.Exception(LogCat.UI, ex, "RenderFeatureSearch");
            return ++_featureFailures >= 3 ? -2 : 0;
        }
        finally
        {
            ImGui.SetCurrentContext(previous);
        }
    }
}
