// IltChatCapture.cs — Shared "send a server command and collect its reply" helper.
//
// Every ILT Hub feature that talks to ACECustom (/bank, /aug, /qb, /pets, /xp, /pb,
// /ilt features, the login probe, ...) goes through this one queue so that:
//   * only one capture is in flight at a time (replies can't be attributed to the wrong
//     request),
//   * reply lines can be eaten so a Hub refresh doesn't spam the chat window,
//   * an "Unknown command: X" reply is recognised uniformly (feature not on this server).
// All methods must be called on the plugin pump thread (OnTick / OnChatWindowText).
using System;
using System.Collections.Generic;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.IltHub;

/// <summary>Describes one command whose reply should be captured.</summary>
internal sealed class IltChatRequest
{
    /// <summary>Text handed to InvokeChatParser, e.g. "/bank".</summary>
    public string Command = string.Empty;
    /// <summary>True for lines that belong to this reply (captured, refresh the idle timer).</summary>
    public Func<string, bool> IsResponseLine = _ => false;
    /// <summary>Optional: a line that ends the reply immediately (it is captured too).</summary>
    public Func<string, bool>? IsTerminator;
    /// <summary>The reply is complete after this long with no new response line.</summary>
    public int IdleEndMs = 2000;
    /// <summary>Give up if no response line arrives within this long.</summary>
    public int FirstLineTimeoutMs = 15000;
    /// <summary>Hide captured lines from the chat window.</summary>
    public bool Eat = true;
    /// <summary>Invoked once on the pump thread when the capture finishes for any reason.</summary>
    public Action<IltChatResult>? OnComplete;
}

/// <summary>What a capture produced.</summary>
internal sealed class IltChatResult
{
    public readonly List<string> Lines = new();
    /// <summary>No response line ever arrived.</summary>
    public bool TimedOut;
    /// <summary>The server answered "Unknown command: …" for this command.</summary>
    public bool UnknownCommand;
    /// <summary>The engine had no InvokeChatParser, so nothing was sent.</summary>
    public bool NotSent;
    public string? TerminatorLine;
}

internal sealed class IltChatCapture
{
    private readonly RynthCoreHost _host;
    private readonly Queue<IltChatRequest> _queue = new();

    private IltChatRequest? _active;
    private IltChatResult? _activeResult;
    private long _sentAt;
    private long _lastLineAt;

    /// <summary>Minimum spacing between two sends so the server's own command throttles aren't tripped.</summary>
    private const long SendSpacingMs = 400;
    private long _lastSendAt;

    private static long NowMs => Environment.TickCount64;

    public IltChatCapture(RynthCoreHost host) => _host = host;

    /// <summary>True while a capture is in flight or queued.</summary>
    public bool IsBusy => _active != null || _queue.Count > 0;

    /// <summary>True if a request with this exact command is queued or in flight (avoids duplicate refreshes).</summary>
    public bool IsPending(string command)
    {
        if (_active != null && string.Equals(_active.Command, command, StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var r in _queue)
            if (string.Equals(r.Command, command, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Queues a capture. It is sent from <see cref="Tick"/> once earlier captures finish.</summary>
    public void Enqueue(IltChatRequest request) => _queue.Enqueue(request);

    /// <summary>Drops everything (logout / teardown). Pending callbacks are not invoked.</summary>
    public void Clear()
    {
        _queue.Clear();
        _active = null;
        _activeResult = null;
    }

    /// <summary>Starts queued captures and finishes the active one on idle/timeout.</summary>
    public void Tick()
    {
        long now = NowMs;
        if (_active != null && _activeResult != null)
        {
            bool gotAny = _activeResult.Lines.Count > 0;
            if (gotAny && now - _lastLineAt >= _active.IdleEndMs)
                Finish();
            else if (!gotAny && now - _sentAt >= _active.FirstLineTimeoutMs)
            {
                _activeResult.TimedOut = true;
                Finish();
            }
        }

        if (_active == null && _queue.Count > 0 && now - _lastSendAt >= SendSpacingMs)
            StartNext(now);
    }

    /// <summary>
    /// Feed every incoming chat line. Returns true when the line belonged to the active
    /// capture and that capture asked for its lines to be eaten.
    /// </summary>
    public bool OnChatLine(string rawText)
    {
        if (_active == null || _activeResult == null) return false;
        string text = rawText.Trim();
        if (text.Length == 0) return false;

        // "Unknown command: blackjack" — ACE GameActionTalk reply for unregistered commands.
        if (text.StartsWith("Unknown command:", StringComparison.OrdinalIgnoreCase)
            && CommandWordMatches(text.Substring("Unknown command:".Length).Trim()))
        {
            _activeResult.UnknownCommand = true;
            _activeResult.Lines.Add(text);
            bool eat = _active.Eat;
            Finish();
            return eat;
        }

        if (_active.IsTerminator != null && _active.IsTerminator(text))
        {
            _activeResult.TerminatorLine = text;
            _activeResult.Lines.Add(text);
            bool eat = _active.Eat;
            Finish();
            return eat;
        }

        if (!_active.IsResponseLine(text)) return false;
        _activeResult.Lines.Add(text);
        _lastLineAt = NowMs;
        return _active.Eat;
    }

    private bool CommandWordMatches(string word)
    {
        if (_active == null) return false;
        // Request "/bank t p 5 Bob" → command word "bank".
        string cmd = _active.Command.TrimStart('/', '@');
        int sp = cmd.IndexOf(' ');
        if (sp >= 0) cmd = cmd.Substring(0, sp);
        return string.Equals(cmd, word, StringComparison.OrdinalIgnoreCase);
    }

    private void StartNext(long now)
    {
        var req = _queue.Dequeue();
        var result = new IltChatResult();
        if (!_host.HasInvokeChatParser)
        {
            result.NotSent = true;
            try { req.OnComplete?.Invoke(result); }
            catch (Exception ex) { _host.Log($"[IltHub] capture callback '{req.Command}' threw: {ex.Message}"); }
            return;
        }

        _active = req;
        _activeResult = result;
        _sentAt = now;
        _lastLineAt = now;
        _lastSendAt = now;
        _host.InvokeChatParser(req.Command);
    }

    private void Finish()
    {
        var req = _active;
        var res = _activeResult;
        _active = null;
        _activeResult = null;
        if (req == null || res == null) return;
        try { req.OnComplete?.Invoke(res); }
        catch (Exception ex) { _host.Log($"[IltHub] capture callback '{req.Command}' threw: {ex.Message}"); }
    }
}
