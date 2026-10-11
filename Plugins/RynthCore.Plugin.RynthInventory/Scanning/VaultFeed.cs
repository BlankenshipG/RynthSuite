using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using RynthCore.Plugin.RynthInventory.Data;

namespace RynthCore.Plugin.RynthInventory.Scanning;

/// <summary>
/// The Aelrynth account vault (the Aeshnidae.Mule mod): item storage the retail client is never
/// shown, so the only way to see inside is to ask. The plugin types /mule-data, exactly as a
/// player could, and the server answers one chat line a page:
///
///     ~ael1 {"t":"mule","ready":true,"page":1,"pages":3,"count":57,"max":1000,"items":[{...}]}
///
/// (the same "~ael1" lines the Aelrynth Companion reads; only lines whose "t" is "mule" are ours).
/// The pages are collected into one StorageRecord and kept with the character's storage, so the
/// vault is searched, shown with icons and merged across characters like any other storage; the
/// newest copy wins, and a vault belongs to the account, so every character sees the same one.
///
/// A server without the command is asked once after login; its "Unknown command" reply is eaten
/// and the plugin stays quiet. A vault the server says is not ready yet is asked again a few
/// times, and is never recorded as empty.
///
/// Threads: <see cref="ShouldEat"/> may run on AC's thread (it only queues); everything else runs
/// on the plugin tick.
/// </summary>
internal sealed class VaultFeed
{
    public const string Command = "/mule-data";
    public const string Prefix = "~ael1 ";
    public const string Label = "Account vault";
    public const string VaultName = "Aelrynth vault";

    public enum State { Idle, Asking, Unsupported, Waiting }

    private const long AnswerWindowMs = 15_000, RetryReadyMs = 3_000;
    private const int MaxReadyRetries = 6, MaxPages = 80;
    private const string UnknownMarker = "\u0001unknown";

    private readonly Func<string, bool> _send;
    private readonly ConcurrentQueue<string> _lines = new();

    private long _sentAtMs;
    private volatile bool _expecting;
    private int _readyTries;
    private long _retryAtMs;
    private readonly List<ItemRecord> _pages = new();
    private bool _changed;

    public VaultFeed(Func<string, bool> send) => _send = send;

    public State Status { get; private set; } = State.Idle;
    /// <summary>The vault once a full read finished (null until then).</summary>
    public StorageRecord? Record { get; private set; }
    public int Max { get; private set; }

    /// <summary>For /ginv status.</summary>
    public string Describe() => Status switch
    {
        State.Unsupported => "no vault on this server",
        State.Asking => "reading the vault...",
        State.Waiting => "the vault is still loading on the server",
        _ => Record == null ? "vault not read yet" : $"vault: {Record.Items.Count} item(s)" + (Max > 0 ? $" of {Max}" : ""),
    };

    /// <summary>True once after <see cref="Record"/> was replaced.</summary>
    public bool TakeChanged() { bool c = _changed; _changed = false; return c; }

    public void Reset()
    {
        Status = State.Idle;
        _expecting = false;
        _pages.Clear();
        _readyTries = 0;
        while (_lines.TryDequeue(out _)) { }
    }

    /// <summary>Starts a read from page 1, unless one is under way or the server has no vault (<paramref name="force"/> asks anyway).</summary>
    public bool Ask(long nowMs, bool force = false)
    {
        if (Status == State.Asking || Status == State.Waiting) return false;
        if (Status == State.Unsupported && !force) return false;
        _readyTries = 0;
        _pages.Clear();
        return SendPage(nowMs, 1);
    }

    private bool SendPage(long nowMs, int page)
    {
        _sentAtMs = nowMs;
        Status = State.Asking;
        _expecting = true;
        if (_send(page <= 1 ? Command : Command + " " + page.ToString(CultureInfo.InvariantCulture))) return true;
        _expecting = false;
        Status = State.Idle;
        return false;
    }

    /// <summary>True for a line this feed answers, so the caller keeps it out of the chat window. Cheap; any thread.</summary>
    public bool ShouldEat(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        int at = text.IndexOf(Prefix, StringComparison.Ordinal);
        if (at >= 0)
        {
            // Only our own kind: another plugin's ~ael1 line (the Companion's) stays visible to it.
            if (text.IndexOf("\"t\":\"mule\"", at, StringComparison.Ordinal) < 0) return false;
            _lines.Enqueue(text.Substring(at + Prefix.Length));
            return true;
        }
        // The reply of a server that has no such command: swallowed only while we wait for one.
        if (_expecting && text.IndexOf("Unknown command", StringComparison.OrdinalIgnoreCase) >= 0
            && text.IndexOf("mule-data", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _lines.Enqueue(UnknownMarker);
            return true;
        }
        return false;
    }

    /// <summary>Applies what arrived and decides what to send next. Plugin tick.</summary>
    public void Tick(long nowMs, string account)
    {
        while (_lines.TryDequeue(out string? line)) Apply(line, nowMs, account);

        if (Status == State.Waiting && nowMs >= _retryAtMs) SendPage(nowMs, 1);
        else if (Status == State.Asking && nowMs - _sentAtMs > AnswerWindowMs)
        {
            // Nothing came back: a server without the mod, or a lost line. The next /ginv vault retries.
            _expecting = false;
            Status = State.Idle;
            _pages.Clear();
        }
    }

    private void Apply(string line, long nowMs, string account)
    {
        if (line == UnknownMarker)
        {
            if (Status == State.Asking) { _expecting = false; Status = State.Unsupported; _pages.Clear(); }
            return;
        }
        if (Status != State.Asking) return;   // a line we did not ask for (typed by hand, say)
        _expecting = false;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { Status = State.Idle; return; }

            if (!(root.TryGetProperty("ready", out JsonElement ready) && ready.ValueKind == JsonValueKind.True))
            {
                string why = Str(root, "why");
                if (why == "loading" && ++_readyTries <= MaxReadyRetries) { Status = State.Waiting; _retryAtMs = nowMs + RetryReadyMs; }
                else Status = State.Idle;   // closed / fault / error: try again at the next refresh
                _pages.Clear();
                return;
            }

            int page = Int(root, "page", 1), pages = Math.Min(Math.Max(Int(root, "pages", 1), 1), MaxPages);
            Max = Int(root, "max", Max);
            int count = Int(root, "count", 0);
            if (page == 1) _pages.Clear();
            if (root.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
                foreach (JsonElement e in items.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.Object) _pages.Add(Item(e));

            if (page < pages) { SendPage(nowMs, page + 1); return; }

            // Last page. A total that disagrees with what was read means the vault changed under
            // the read (a deposit between pages): keep the old copy and read again shortly.
            if (count != _pages.Count && _readyTries++ < MaxReadyRetries)
            {
                Status = State.Waiting; _retryAtMs = nowMs + RetryReadyMs; _pages.Clear(); return;
            }
            Record = new StorageRecord
            {
                Id = VaultId(account),
                Name = VaultName,
                Label = Label,
                Cell = 0,
                SeenUtc = DateTime.UtcNow,
                Items = new List<ItemRecord>(_pages),
            };
            _pages.Clear();
            _changed = true;
            Status = State.Idle;
        }
        catch (JsonException)
        {
            Status = State.Idle;
            _pages.Clear();
        }
    }

    private static ItemRecord Item(JsonElement e)
    {
        var it = new ItemRecord
        {
            Id = UInt(e, "o"),
            Wcid = UInt(e, "w"),
            Name = Str(e, "n"),
            Icon = UInt(e, "i"),
            Underlay = UInt(e, "u"),
            Overlay = UInt(e, "v"),
            Stack = Math.Max(1, Int(e, "s", 1)),
            MaxStack = Int(e, "m", 0),
            ItemType = UInt(e, "y"),
            Place = ItemPlace.Storage,
        };
        it.Category = ItemCategory.FromItemType(it.ItemType);
        return it;
    }

    /// <summary>
    /// A stand-in container id for the account's vault. Storage is merged by (server, id) with the
    /// newest copy winning, so the same id on every character of the account shows ONE vault. It is
    /// in the top of the id space, where no object the client has seen lives.
    /// </summary>
    public static uint VaultId(string account)
    {
        uint h = 2166136261u;
        foreach (char c in (account ?? "").Trim().ToLowerInvariant()) { h ^= c; h *= 16777619u; }
        return 0xFF000000u | (h & 0x00FFFFFFu);
    }

    private static int Int(JsonElement e, string name, int fallback) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : fallback;

    private static uint UInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out uint u) ? u : 0u;

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
}
