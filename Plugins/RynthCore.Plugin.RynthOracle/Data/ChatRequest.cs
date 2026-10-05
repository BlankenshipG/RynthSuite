// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>
/// RynthOracle sent a server command and expects its reply for a few seconds, so the chat
/// handler can tell its own reply from the same command typed by hand (only ours may be hidden).
/// A time window, as upstream: these replies carry nothing tying them to the request. Each line
/// of the reply extends it, so a long reply stays ours to its end.
/// </summary>
internal sealed class ChatRequest
{
    private const long WindowMs = 5000;
    private long _untilMs;

    public void Sent(long nowMs) => _untilMs = nowMs + WindowMs;
    public void Clear() => _untilMs = 0;
    public bool Awaiting(long nowMs) => nowMs < _untilMs;

    /// <summary>A reply line arrived: keep the window open a little longer.</summary>
    public void Extend(long nowMs)
    {
        if (Awaiting(nowMs)) _untilMs = nowMs + 2000 > _untilMs ? nowMs + 2000 : _untilMs;
    }
}
