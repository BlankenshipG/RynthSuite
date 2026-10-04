// IltHubContext.cs — Shared services handed to every ILT Hub feature.
//
// Threading contract:
//   * Feature Tick / OnChat run on the plugin pump thread and may call the host freely.
//   * Feature Render runs on the ImGui render thread and must NOT call AC actions directly.
//     It calls Post(...) to run work on the next pump tick, or Confirm(...) to ask the
//     player first (the confirmed action is then posted). This mirrors the existing
//     DrainMetaCommands pattern in RynthAiPlugin.
using System;
using System.Collections.Concurrent;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.IltHub;

/// <summary>A yes/no question waiting for the player in the Hub window.</summary>
internal sealed class IltConfirmRequest
{
    public string Title = "Confirm";
    public string Message = string.Empty;
    public string YesLabel = "Yes";
    /// <summary>Runs on the pump thread after the player clicks Yes.</summary>
    public Action? OnYes;
}

internal sealed class IltHubContext
{
    private readonly ConcurrentQueue<Action> _posted = new();
    private readonly ConcurrentQueue<IltConfirmRequest> _confirms = new();
    private readonly Func<LegacyUiSettings?> _settings;

    public IltHubContext(RynthCoreHost host, IltHubState state, IltHubStore store, IltChatCapture capture,
                         IltServerOptions options, IltInventory inventory, Func<LegacyUiSettings?> settings)
    {
        Host = host;
        State = state;
        Store = store;
        Capture = capture;
        Options = options;
        Inventory = inventory;
        _settings = settings;
    }

    public RynthCoreHost Host { get; }
    public IltHubState State { get; }
    public IltHubStore Store { get; }
    public IltChatCapture Capture { get; }
    public IltServerOptions Options { get; }
    public IltInventory Inventory { get; }

    /// <summary>The live combat settings (rule lists the Pet picker / USD importer edit). Null before login.</summary>
    public LegacyUiSettings? Settings => _settings();

    /// <summary>Set by the controller when combat settings were edited and must be saved.</summary>
    public Action? SaveCombatSettings;

    /// <summary>Logged-in character name (resolved at login).</summary>
    public string CharName = string.Empty;

    /// <summary>True when the Hub window is open (features use it to gate auto-refreshes).</summary>
    public bool WindowOpen => State.WindowVisible;

    /// <summary>Writes a local chat line (never sent to the server).</summary>
    public void Chat(string text)
    {
        if (Host.HasWriteToChat) Host.WriteToChat(text, 1);
    }

    /// <summary>Queues work for the next pump tick (safe from the render thread).</summary>
    public void Post(Action action) => _posted.Enqueue(action);

    /// <summary>Asks the player to confirm before <paramref name="onYes"/> runs on the pump thread.</summary>
    public void Confirm(string title, string message, Action onYes, string yesLabel = "Yes")
        => _confirms.Enqueue(new IltConfirmRequest { Title = title, Message = message, OnYes = onYes, YesLabel = yesLabel });

    /// <summary>Render thread: takes the next pending confirmation, if any.</summary>
    public bool TryTakeConfirm(out IltConfirmRequest request) => _confirms.TryDequeue(out request!);

    /// <summary>Pump thread: runs everything posted since the last tick.</summary>
    public void DrainPosted()
    {
        int guard = 0;
        while (guard++ < 64 && _posted.TryDequeue(out var a))
        {
            try { a(); }
            catch (Exception ex) { Host.Log($"[IltHub] posted action threw: {ex.Message}"); }
        }
    }

    /// <summary>Elapsed-ms clock used for every Hub timer.</summary>
    public static long NowMs => Environment.TickCount64;
}

/// <summary>Contract every Hub feature module implements.</summary>
internal interface IIltFeature
{
    /// <summary>Pump-thread tick (~every plugin tick).</summary>
    void Tick(long nowMs);

    /// <summary>Pump-thread chat line. Return true to hide the line from the chat window.</summary>
    bool OnChat(string text);

    /// <summary>Logout / teardown: stop automation, drop transient state.</summary>
    void OnLogout();
}
