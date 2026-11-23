using System;
using System.Text;

namespace RynthCore.Plugin.RynthChat;

/// <summary>
/// The newest chat lines, exported to the engine as JSON
/// (RynthChatGetScrollbackJson). Add runs on the game thread (chat dispatch),
/// BuildJson on the engine's pump thread; the lock covers both.
///
/// How many: DefaultCapacity until the engine says otherwise
/// (RynthChatSetCapacity, from the chat setting "Chat history"; 0.3.0). It was
/// a fixed 500 before. Changing it keeps the newest lines and never touches the
/// numbering: the engine (and its Discord relay) tell new lines from old by seq.
///
/// JSON per line: {"seq":N,"ts":"HH:mm:ss","chan":"Chat","type":3,"sender":"Bob"|null,"text":"..."}.
/// "type" (AC's ChatMessageType) was added in 0.2.0; engines before it ignore it.
/// ,"auto":true (0.3.4) marks automated system text (ChatLine.Auto); absent otherwise,
/// and engines before 2026.10.18.30 ignore it.
/// Every non-ASCII character is written as a \uXXXX escape, so the export is
/// pure ASCII and survives the ANSI marshalling on both sides (before 0.2.0 an
/// accented name or the "→" of an outgoing tell arrived as "?").
/// </summary>
internal sealed class ChatBuffer
{
    /// <summary>The engine's default for "Chat history" (ChatHistory.DefaultLines there).</summary>
    internal const int DefaultCapacity = 2000;
    internal const int MinCapacity = 100;
    internal const int MaxCapacity = 10000;

    private ChatLine?[] _ring;
    private readonly object _lock = new();
    private int  _head;   // next write index (mod _ring.Length)
    private int  _count;  // lines stored (<= _ring.Length)
    private ulong _nextSeq = 1;

    // Quest lists (2026-10-25): the /myquests and /qb list replies plugins ask for by themselves
    // are hidden; one the player asked for in chat (RynthChat's input, NoteTypedQuestCommand)
    // shows, under All. The window opens on the command and stays open while its lines keep coming.
    private const long TypedQuestFirstLineMs = 15_000;   // the command -> its first line
    private const long QuestListIdleMs = 4_000;          // between two lines of one list
    private long _questShowUntilMs;
    // The same for the /bank balance dump: hidden unless the player typed /bank (RynthILT's and
    // ub-Rythai's own reads stay out of every tab, whether or not a plugin ate them).
    private long _bankShowUntilMs;
    // When the last quest-list line arrived: the eaten lines of a /qb list (its quest names
    // match no fixed format) that follow it belong to the same hidden list.
    private long _lastQuestListLineMs = long.MinValue / 2;

    internal ChatBuffer(int capacity = DefaultCapacity)
    {
        _ring = new ChatLine?[Math.Clamp(capacity, MinCapacity, MaxCapacity)];
    }

    internal int Capacity
    {
        get { lock (_lock) return _ring.Length; }
    }

    internal int Count
    {
        get { lock (_lock) return _count; }
    }

    /// <summary>
    /// Keeps up to <paramref name="capacity"/> lines from now on (clamped to
    /// MinCapacity..MaxCapacity); when it shrinks, the oldest go. Returns the capacity
    /// applied. Any thread.
    /// </summary>
    internal int SetCapacity(int capacity)
    {
        capacity = Math.Clamp(capacity, MinCapacity, MaxCapacity);
        lock (_lock)
        {
            if (capacity == _ring.Length) return capacity;
            int keep = Math.Min(_count, capacity);
            var ring = new ChatLine?[capacity];
            int oldCap = _ring.Length;
            int start = (_head - keep + oldCap) % oldCap;   // the oldest line kept
            for (int i = 0; i < keep; i++) ring[i] = _ring[(start + i) % oldCap];
            _ring = ring;
            _count = keep;
            _head = keep % capacity;
            return capacity;
        }
    }

    /// <param name="loginBurst">The character is entering the world: its system text is automated.</param>
    internal void Add(string? text, int chatType, bool loginBurst = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        string channel = ChatClassifier.IsItemInfo(text) ? ChatClassifier.Items
            : ChatClassifier.IsRynthOutput(text) ? ChatClassifier.Rynth
            : ChatClassifier.ChannelFor(text, chatType);
        string? sender = ChatClassifier.SenderFor(text, chatType);
        string ts = DateTime.Now.ToString("HH:mm:ss");
        bool auto = ChatClassifier.IsAutomatedText(text, chatType)
                    || ChatClassifier.IsLoginBurst(channel, chatType, loginBurst);
        bool questList = ChatClassifier.IsQuestListText(text, chatType);
        bool bankDump = ChatClassifier.IsBankBalanceDump(text);

        lock (_lock)
        {
            bool hidden = false;
            if (questList)
            {
                long now = Environment.TickCount64;
                _lastQuestListLineMs = now;
                if (now < _questShowUntilMs)
                {
                    auto = false;   // the player asked for it: under All like any reply
                    _questShowUntilMs = now + QuestListIdleMs;
                }
                else
                    hidden = true;
            }
            else if (bankDump)
            {
                long now = Environment.TickCount64;
                if (now < _bankShowUntilMs)
                {
                    auto = false;
                    _bankShowUntilMs = now + QuestListIdleMs;
                }
                else
                    hidden = true;
            }
            // Numbered under the lock, so the ring is always in seq order (the
            // engine skips anything at or below the last seq it has seen).
            _ring[_head] = new ChatLine
            {
                Seq       = _nextSeq++,
                Timestamp = ts,
                Channel   = channel,
                ChatType  = chatType,
                Sender    = sender,
                Text      = text,
                Auto      = auto,
                Hidden    = hidden,
            };
            _head = (_head + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
        }
    }

    /// <summary>
    /// A plugin ate the line just dispatched (the engine calls this after every plugin has seen
    /// it): marks it automated when it is the newest line here, with the same text. Returns
    /// whether it was marked. The engine polls the scrollback on the same thread as the chat
    /// dispatch, so the mark lands before the line is exported.
    /// </summary>
    internal bool NoteEaten(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        lock (_lock)
        {
            if (_count == 0) return false;
            ChatLine? newest = _ring[(_head - 1 + _ring.Length) % _ring.Length];
            if (newest == null || !string.Equals(newest.Text, text, StringComparison.Ordinal)) return false;
            long now = Environment.TickCount64;
            bool typedQuestList = now < _questShowUntilMs;
            bool bankDump = ChatClassifier.IsBankBalanceDump(text);
            bool typed = bankDump ? now < _bankShowUntilMs : typedQuestList;
            if (!typed) newest.Auto = true;
            // A quiet /bank refresh's balance dump: not even the System tab.
            if (bankDump) { if (!typed) newest.Hidden = true; }
            // A line of a quest list nobody asked for in chat (a /qb list's quest names).
            else if (!typedQuestList && now - _lastQuestListLineMs < QuestListIdleMs) newest.Hidden = true;
            return true;
        }
    }

    /// <summary>The player sent /myquests or /qb from the chat input: its list shows (game thread).</summary>
    internal void NoteTypedQuestCommand()
    {
        lock (_lock) _questShowUntilMs = Environment.TickCount64 + TypedQuestFirstLineMs;
    }

    /// <summary>The player sent /bank (or /b) from the chat input: its balances show (game thread).</summary>
    internal void NoteTypedBankCommand()
    {
        lock (_lock) _bankShowUntilMs = Environment.TickCount64 + TypedQuestFirstLineMs;
    }

    /// <summary>
    /// The lines with Seq > sinceSeq as a JSON array, oldest first. Only those are
    /// copied out under the lock: the ring holds consecutive seqs, so they are the
    /// newest (newest seq - sinceSeq) lines. Until 0.3.0 the whole ring was cloned
    /// and walked on every poll (10 a second), which grows with the history length.
    /// </summary>
    internal string BuildJson(ulong sinceSeq)
    {
        ChatLine?[] lines;
        lock (_lock)
        {
            ulong newest = _nextSeq - 1;
            int n = sinceSeq >= newest ? 0 : (int)Math.Min((ulong)_count, newest - sinceSeq);
            if (n == 0) return "[]";
            lines = new ChatLine?[n];
            int cap = _ring.Length;
            int start = (_head - n + cap) % cap;
            for (int i = 0; i < n; i++) lines[i] = _ring[(start + i) % cap];
        }

        var sb = new StringBuilder(64 + lines.Length * 192);
        sb.Append('[');
        bool first = true;
        foreach (ChatLine? line in lines)
        {
            if (line == null || line.Seq <= sinceSeq || line.Hidden) continue;

            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"seq\":");
            sb.Append(line.Seq);
            sb.Append(",\"ts\":\"");
            AppendEscaped(sb, line.Timestamp);
            sb.Append("\",\"chan\":\"");
            AppendEscaped(sb, line.Channel);
            sb.Append("\",\"type\":");
            sb.Append(line.ChatType);
            sb.Append(",\"sender\":");
            if (line.Sender != null)
            {
                sb.Append('"');
                AppendEscaped(sb, line.Sender);
                sb.Append('"');
            }
            else
            {
                sb.Append("null");
            }
            sb.Append(",\"text\":\"");
            AppendEscaped(sb, line.Text);
            sb.Append('"');
            if (line.Auto) sb.Append(",\"auto\":true");
            sb.Append('}');
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static void AppendEscaped(StringBuilder sb, string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            // A surrogate pair is escaped as two \uXXXX; a lone half becomes U+FFFD.
            // System.Text.Json refuses a lone surrogate, and before 0.2.1 one such line
            // failed the engine's whole batch (and every poll after it).
            if (char.IsSurrogate(c))
            {
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    AppendUnicodeEscape(sb, c);
                    AppendUnicodeEscape(sb, s[++i]);
                }
                else
                    sb.Append("\\uFFFD");
                continue;
            }
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    // Control characters and everything outside ASCII: the string is
                    // marshalled as ANSI, which would turn those into '?'.
                    if (c < 0x20 || c > 0x7E)
                        AppendUnicodeEscape(sb, c);
                    else
                        sb.Append(c);
                    break;
            }
        }
    }

    private static void AppendUnicodeEscape(StringBuilder sb, char c)
    {
        sb.Append("\\u");
        sb.Append(((int)c).ToString("X4"));
    }
}
