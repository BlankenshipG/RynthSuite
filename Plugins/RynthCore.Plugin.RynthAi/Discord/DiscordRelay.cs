// DiscordRelay.cs — Copies chosen AC chat channels to Discord channels (webhook or bot).
//
// Threads   OnChatWindowText and the UI / command methods only enqueue (any thread). Tick runs on the
//           plugin pump thread: it parses lines, routes them, batches per route, starts at most one
//           HTTP post per route, and applies results. Status and the log are published for the UI.
//
// Per route  lines arriving within BatchMs are packed into one message (<= 2000 chars). Posts are spaced
//           by MinIntervalMs or whatever Discord's rate limit asks for. Network / 5xx failures retry with
//           backoff; 400 / 401 / 403 / 404 pause the route until its settings change or Resume is pressed.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RynthCore.Plugin.RynthAi.Translate;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.Discord;

internal enum DiscordLogKind { Sent, Error, Info }

/// <summary>One line in the relay window log (immutable once added).</summary>
internal sealed record DiscordLogEntry(DateTime Time, DiscordLogKind Kind, string Route, string Text);

/// <summary>Route state the UI shows; replaced as a whole by the pump thread.</summary>
internal sealed record DiscordRouteStatus(int Queued, int MessagesSent, int LinesSent, int Dropped, bool Paused,
    string LastError, DateTime LastSent);

internal sealed class DiscordRelay : IDisposable
{
    /// <summary>Prefix of the relay's own chat lines, so they are never relayed.</summary>
    public const string ChatPrefix = "[Discord]";

    private const int SaveEveryMs = 2000;
    private const int DedupWindowMs = 3000;
    private const int MaxRawQueue = 2000;
    private const int MaxTransientRetries = 5;
    private const int MaxBackoffMs = 60000;
    private const int ChatTypeSpellcasting = 0x11;

    private sealed record RawLine(DateTime Time, string Text, int ChatType);

    /// <summary>A packed message waiting for, or in, an HTTP post.</summary>
    private sealed class Batch
    {
        public required List<string> Lines;
        public required string Content;
        public bool IsTest;
        public int Attempts;
    }

    /// <summary>Pump-thread state for one route.</summary>
    private sealed class RouteState
    {
        public readonly List<string> Pending = new();
        public long FirstPendingMs;
        public long NextAllowedMs;
        public Batch? InFlight;
        public Batch? Retry;
        public int BackoffMs;
        public bool Paused;
        public string LastError = string.Empty;
        public int MessagesSent, LinesSent, Dropped;
        public DateTime LastSent;
        public readonly Dictionary<string, long> Recent = new(StringComparer.Ordinal);
    }

    private readonly Func<string> _myName;
    private readonly DiscordRelayStore _store;
    private readonly CancellationTokenSource _cts = new();

    // Any thread → pump.
    private readonly ConcurrentQueue<RawLine> _raw = new();
    private readonly ConcurrentQueue<(DiscordRoute Route, Batch Batch, DiscordSendResult Result)> _done = new();
    private readonly ConcurrentQueue<DiscordRoute> _resume = new();
    private readonly ConcurrentQueue<DiscordRoute> _tests = new();
    private int _rawCount;

    // Pump thread only.
    private readonly Dictionary<DiscordRoute, RouteState> _states = new(ReferenceEqualityComparer.Instance);
    private long _lastSaveMs;

    // Pump writes, render reads.
    private readonly ConcurrentDictionary<DiscordRoute, DiscordRouteStatus> _status = new(ReferenceEqualityComparer.Instance);
    private readonly object _logLock = new();
    private readonly List<DiscordLogEntry> _log = new();
    private DiscordLogEntry[] _logSnapshot = Array.Empty<DiscordLogEntry>();

    public DiscordRelaySettings Settings { get; }

    public DiscordRelay(Func<string> myName, DiscordRelayStore? store = null)
    {
        _myName = myName;
        _store = store ?? new DiscordRelayStore();
        Settings = _store.Load();
    }

    // ── Entry points (any thread) ──────────────────────────────────────────

    /// <summary>Inbound chat line from the AC chat window.</summary>
    public void OnChatWindowText(string text, int chatType)
    {
        if (!Settings.Enabled || string.IsNullOrEmpty(text)) return;
        if (Interlocked.Increment(ref _rawCount) > MaxRawQueue)
        {
            Interlocked.Decrement(ref _rawCount);
            return;
        }
        _raw.Enqueue(new RawLine(DateTime.Now, text, chatType));
    }

    /// <summary>Clears a pause on <paramref name="route"/> (after a credential fix, or the Resume button).</summary>
    public void Resume(DiscordRoute route) => _resume.Enqueue(route);

    /// <summary>Posts a test line to <paramref name="route"/>, ignoring its channel filters, and resumes it.</summary>
    public void SendTest(DiscordRoute route)
    {
        _resume.Enqueue(route);
        _tests.Enqueue(route);
    }

    /// <summary>
    /// Saves a pasted webhook URL (encrypted) on <paramref name="route"/>. Returns an error for the UI,
    /// or empty when saved. Any thread; the plain text is not kept.
    /// </summary>
    public string SetWebhook(DiscordRoute route, string url)
    {
        if (!DiscordFormat.TryNormalizeWebhookUrl(url, out string normalized))
            return "That isn't a Discord webhook URL (https://discord.com/api/webhooks/<id>/<token>).";
        route.WebhookProtected = DiscordSecrets.Protect(normalized);
        Resume(route);
        return string.Empty;
    }

    /// <summary>Saves a pasted bot token (encrypted). Returns an error for the UI, or empty when saved.</summary>
    public string SetBotToken(DiscordRoute route, string token)
    {
        if (!DiscordFormat.LooksLikeBotToken(token))
            return "That doesn't look like a bot token (Developer Portal > Bot > Reset Token).";
        route.BotTokenProtected = DiscordSecrets.Protect(token.Trim());
        Resume(route);
        return string.Empty;
    }

    public DiscordRouteStatus? StatusOf(DiscordRoute route) => _status.TryGetValue(route, out var s) ? s : null;

    // ── Pump thread ────────────────────────────────────────────────────────

    public void Tick()
    {
        long now = Environment.TickCount64;
        var s = Settings;
        var routes = s.Routes;

        PruneStates(routes);
        while (_resume.TryDequeue(out var r))
            if (_states.TryGetValue(r, out var st)) { st.Paused = false; st.LastError = string.Empty; st.BackoffMs = 0; st.NextAllowedMs = 0; }
        while (_done.TryDequeue(out var d)) ApplyResult(d.Route, d.Batch, d.Result, now);
        while (_tests.TryDequeue(out var t)) QueueTest(t);

        DrainRaw(s, routes, now);
        foreach (var route in routes) StartNextPost(s, route, now);
        PublishStatus(routes);

        if (now - _lastSaveMs >= SaveEveryMs)
        {
            _lastSaveMs = now;
            _store.SaveIfDirty(s);
        }
    }

    private RouteState State(DiscordRoute route)
    {
        if (!_states.TryGetValue(route, out var st)) _states[route] = st = new RouteState();
        return st;
    }

    private void PruneStates(DiscordRoute[] routes)
    {
        if (_states.Count <= routes.Length) return;
        var live = new HashSet<DiscordRoute>(routes, ReferenceEqualityComparer.Instance);
        var gone = new List<DiscordRoute>();
        foreach (var r in _states.Keys) if (!live.Contains(r)) gone.Add(r);
        foreach (var r in gone) { _states.Remove(r); _status.TryRemove(r, out _); }
    }

    private void DrainRaw(DiscordRelaySettings s, DiscordRoute[] routes, long now)
    {
        if (_raw.IsEmpty) return;
        string me = SafeName();
        bool allowed = s.Enabled && DiscordFormat.CharacterAllowed(s.OnlyCharacters, me);
        // Keyword lists parsed once per drain (the UI may edit them between ticks).
        var keywords = new List<string>[routes.Length];
        for (int i = 0; i < routes.Length; i++) keywords[i] = DiscordFormat.SplitList(routes[i].Keywords);

        while (_raw.TryDequeue(out var raw))
        {
            Interlocked.Decrement(ref _rawCount);
            if (!allowed || routes.Length == 0) continue;
            if (!TryClassify(raw.Text, raw.ChatType, me, out var ch, out string speaker, out string message, out bool own)) continue;
            if (own && s.SkipOwn) continue;

            string tag = s.TagCharacter ? me : string.Empty;
            string haystack = speaker.Length > 0 ? speaker + " " + message : message;
            for (int i = 0; i < routes.Length; i++)
            {
                var route = routes[i];
                if (!route.Enabled || !route.HasSecret) continue;
                if (ch is { } c ? !route.RelaysChannel(c) : !route.IncludeOther) continue;
                if (!DiscordFormat.MatchesKeywords(haystack, keywords[i])) continue;

                var st = State(route);
                if (st.Paused) continue;
                string key = $"{ch}|{speaker}|{message}";
                if (st.Recent.TryGetValue(key, out long seen) && now - seen < DedupWindowMs) continue;
                st.Recent[key] = now;
                if (st.Recent.Count > 256) PruneRecent(st, now);

                string line = DiscordFormat.FormatLine(raw.Time, ch is { } cc ? TranslateChannels.Name(cc) : null,
                    speaker, message, s.IncludeTimestamp, tag);
                Enqueue(s, st, line, now);
            }
        }
    }

    /// <summary>
    /// Splits a chat line into channel (null = Other), speaker and message. Player speech goes through the
    /// translator's parser; anything else is an Other line with no speaker. False for lines never relayed:
    /// the relay's and translator's own echoes, spell words, and blank text.
    /// </summary>
    internal static bool TryClassify(string text, int chatType, string me, out TranslateChannel? channel,
        out string speaker, out string message, out bool own)
    {
        channel = null;
        speaker = message = string.Empty;
        own = false;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.TrimStart();
        if (t.StartsWith(ChatPrefix, StringComparison.Ordinal) || t.StartsWith(ChatTranslator.EchoPrefix, StringComparison.Ordinal))
            return false;
        if (chatType == ChatTypeSpellcasting) return false;

        if (ChatTranslator.TryParseInbound(text, chatType, out var ch, out string who, out string msg, out own))
        {
            channel = ch;
            speaker = own && me.Length > 0 ? me : who;
            message = AcChatEscapes.Decode(msg).Trim();
            return message.Length > 0;
        }

        message = AcChatEscapes.Decode(AcChatEscapes.StripLinks(text)).Trim();
        return message.Length > 0;
    }

    private void Enqueue(DiscordRelaySettings s, RouteState st, string line, long now)
    {
        if (st.Pending.Count == 0) st.FirstPendingMs = now;
        st.Pending.Add(line);
        int over = st.Pending.Count - s.MaxQueue;
        if (over > 0)
        {
            st.Pending.RemoveRange(0, over);
            st.Dropped += over;
        }
    }

    private void QueueTest(DiscordRoute route)
    {
        string me = SafeName();
        string line = DiscordFormat.FormatLine(DateTime.Now, null, null,
            $"RynthAi Discord relay test from {(me.Length > 0 ? me : "RynthAi")} (route \"{route.Name}\").",
            Settings.IncludeTimestamp, null);
        var st = State(route);
        st.Retry = new Batch { Lines = new List<string> { line }, Content = line, IsTest = true };
        st.NextAllowedMs = 0;
    }

    private void StartNextPost(DiscordRelaySettings s, DiscordRoute route, long now)
    {
        if (!_states.TryGetValue(route, out var st) || st.InFlight != null || st.Paused) return;
        if (now < st.NextAllowedMs) return;

        Batch? batch = st.Retry;
        if (batch == null)
        {
            if (!route.Enabled) { st.Pending.Clear(); return; }
            if (st.Pending.Count == 0 || !s.Enabled) return;
            // Wait out the batch window unless the pending text already fills a message.
            int chars = 0;
            foreach (string l in st.Pending) chars += l.Length + 1;
            if (now - st.FirstPendingMs < s.BatchMs && chars < DiscordFormat.MaxContent) return;

            string content = DiscordFormat.Pack(st.Pending, out int taken);
            batch = new Batch { Lines = st.Pending.GetRange(0, taken), Content = content };
            st.Pending.RemoveRange(0, taken);
            st.FirstPendingMs = now;
        }
        st.Retry = null;

        // Decrypt only now, on the way out; the plain text lives in the request task alone.
        string mode = route.Mode, channelId = route.ChannelId, blob = mode == DiscordRoute.ModeBot ? route.BotTokenProtected : route.WebhookProtected;
        if (blob.Length == 0)
        {
            ApplyResult(route, batch, DiscordSendResult.Fail(
                mode == DiscordRoute.ModeBot ? "no bot token saved" : "no webhook URL saved", permanent: true), now);
            return;
        }
        if (!DiscordSecrets.TryUnprotect(blob, out string secret))
        {
            ApplyResult(route, batch, DiscordSendResult.Fail(
                "the saved credential can't be read on this Windows user; paste it again", permanent: true), now);
            return;
        }
        string displayName = route.DisplayName.Length > 0 ? route.DisplayName : SafeName();

        st.InFlight = batch;
        st.NextAllowedMs = now + s.MinIntervalMs;
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            DiscordSendResult r;
            try
            {
                r = mode == DiscordRoute.ModeBot
                    ? await DiscordClient.PostBotAsync(secret, channelId, batch.Content, ct).ConfigureAwait(false)
                    : await DiscordClient.PostWebhookAsync(secret, batch.Content, displayName, ct).ConfigureAwait(false);
            }
            catch (Exception ex) { r = DiscordSendResult.Fail(DiscordFormat.Redact(ex.Message, secret)); }
            _done.Enqueue((route, batch, r));
        });
    }

    private void ApplyResult(DiscordRoute route, Batch batch, DiscordSendResult r, long now)
    {
        var st = State(route);
        st.InFlight = null;
        var s = Settings;

        if (r.Ok)
        {
            st.LastError = string.Empty;
            st.BackoffMs = 0;
            st.MessagesSent++;
            st.LinesSent += batch.Lines.Count;
            st.LastSent = DateTime.Now;
            st.NextAllowedMs = Math.Max(st.NextAllowedMs, now + Math.Max(s.MinIntervalMs, r.RetryAfterMs));
            AddLog(DiscordLogKind.Sent, route.Name, batch.IsTest ? "Test message sent." : $"{batch.Lines.Count} line(s): {batch.Content}");
            return;
        }

        st.LastError = r.Error;
        if (r.Permanent)
        {
            st.Paused = true;
            st.Dropped += batch.Lines.Count + st.Pending.Count;
            st.Pending.Clear();
            AddLog(DiscordLogKind.Error, route.Name, $"Paused: {r.Error}");
            RynthLog.Write(LogCat.Chat, $"[Discord] route '{route.Name}' paused: {r.Error}");
            return;
        }

        if (r.Status == 429)
        {
            // Rate limits don't count as a failed attempt; Discord says exactly when to come back.
            st.Retry = batch;
            st.NextAllowedMs = now + Math.Max(r.RetryAfterMs, s.MinIntervalMs);
            AddLog(DiscordLogKind.Info, route.Name, r.Error);
            return;
        }

        batch.Attempts++;
        if (batch.Attempts >= MaxTransientRetries)
        {
            st.Dropped += batch.Lines.Count;
            AddLog(DiscordLogKind.Error, route.Name, $"Gave up after {batch.Attempts} tries ({r.Error}); {batch.Lines.Count} line(s) dropped.");
            RynthLog.Write(LogCat.Chat, $"[Discord] route '{route.Name}' dropped a batch: {r.Error}");
            st.BackoffMs = 0;
            return;
        }
        st.BackoffMs = st.BackoffMs == 0 ? 2000 : Math.Min(st.BackoffMs * 2, MaxBackoffMs);
        st.Retry = batch;
        st.NextAllowedMs = now + Math.Max(st.BackoffMs, r.RetryAfterMs);
        AddLog(DiscordLogKind.Error, route.Name, $"{r.Error}; retrying in {st.BackoffMs / 1000}s.");
    }

    private static void PruneRecent(RouteState st, long now)
    {
        var stale = new List<string>();
        foreach (var kv in st.Recent) if (now - kv.Value > DedupWindowMs) stale.Add(kv.Key);
        foreach (string k in stale) st.Recent.Remove(k);
    }

    private void PublishStatus(DiscordRoute[] routes)
    {
        foreach (var route in routes)
        {
            if (!_states.TryGetValue(route, out var st)) continue;
            int queued = st.Pending.Count + (st.InFlight?.Lines.Count ?? 0) + (st.Retry?.Lines.Count ?? 0);
            _status[route] = new DiscordRouteStatus(queued, st.MessagesSent, st.LinesSent, st.Dropped, st.Paused, st.LastError, st.LastSent);
        }
    }

    private string SafeName()
    {
        try { return (_myName() ?? string.Empty).Trim().TrimStart('+'); }
        catch { return string.Empty; }
    }

    // ── Log (pump writes, render reads) ────────────────────────────────────

    private void AddLog(DiscordLogKind kind, string route, string text)
    {
        lock (_logLock)
        {
            _log.Add(new DiscordLogEntry(DateTime.Now, kind, route, text));
            int max = Settings.MaxLog;
            if (_log.Count > max) _log.RemoveRange(0, _log.Count - max);
            _logSnapshot = _log.ToArray();
        }
    }

    public DiscordLogEntry[] LogSnapshot()
    {
        lock (_logLock) return _logSnapshot;
    }

    public void ClearLog()
    {
        lock (_logLock)
        {
            _log.Clear();
            _logSnapshot = Array.Empty<DiscordLogEntry>();
        }
    }

    // ── /ra discord ────────────────────────────────────────────────────────

    /// <summary>/ra discord [on|off|window|test [route]|resume [route]|status]</summary>
    public void HandleCommand(string[] args, Action<string> chat)
    {
        var s = Settings;
        string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
        string rest = args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1).Trim() : string.Empty;
        switch (verb)
        {
            case "on": s.Enabled = true; chat($"{ChatPrefix} Relay on. {StatusLine()}"); break;
            case "off": s.Enabled = false; chat($"{ChatPrefix} Relay off."); break;
            case "window":
            case "show":
                s.ShowWindow = !s.ShowWindow;
                break;
            case "test":
            case "resume":
                int n = 0;
                foreach (var r in s.Routes)
                {
                    if (rest.Length > 0 && !r.Name.Equals(rest, StringComparison.OrdinalIgnoreCase)) continue;
                    if (verb == "test") SendTest(r); else Resume(r);
                    n++;
                }
                chat(n == 0
                    ? $"{ChatPrefix} No route{(rest.Length > 0 ? $" named '{rest}'" : "s")}. Add one in the Discord Relay window (/ra discord window)."
                    : $"{ChatPrefix} {(verb == "test" ? "Test sent to" : "Resumed")} {n} route(s).");
                break;
            default:
                chat($"{ChatPrefix} {(s.Enabled ? "On" : "Off")}. {StatusLine()}");
                foreach (var r in s.Routes)
                {
                    var st = StatusOf(r);
                    chat($"{ChatPrefix}   {r.Name}: {(r.Enabled ? "on" : "off")}, {(r.HasSecret ? r.Mode : "no destination set")}"
                         + (st == null ? string.Empty
                             : $", {st.MessagesSent} sent, {st.Queued} queued, {st.Dropped} dropped{(st.Paused ? ", PAUSED" : string.Empty)}"
                               + (st.LastError.Length > 0 ? $" - {st.LastError}" : string.Empty)));
                }
                chat($"{ChatPrefix} /ra discord on|off | window | test [route] | resume [route]");
                break;
        }
    }

    public string StatusLine()
    {
        var s = Settings;
        int ready = 0;
        foreach (var r in s.Routes) if (r.Enabled && r.HasSecret) ready++;
        string chars = s.OnlyCharacters.Trim().Length > 0 ? $", only {s.OnlyCharacters.Trim()}" : string.Empty;
        return $"{ready} of {s.Routes.Length} route(s) ready{chars}.";
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        _store.SaveIfDirty(Settings);
        _cts.Dispose();
    }
}
