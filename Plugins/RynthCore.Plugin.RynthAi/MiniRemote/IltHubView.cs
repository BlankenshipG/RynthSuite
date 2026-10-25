// IltHubView.cs - the Mini Remote's Infinite Leaftide questions (is this RynthILT's world, which
// features the server reported), answered through ServerHubClient. RynthILT ships from RynthSuite
// on this branch (test merge onto rynth main, 2026-10-25), so RynthAi's ILT pieces that rynth moved
// into its own RynthILT (pet capture, the Breeding sub-tab, Rings, Trinkets, the Remote's Bank and
// Guardian checkboxes) still ask here. The old IltClient answered the same questions.

using System;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.MiniRemote;

internal sealed class IltHubView
{
    public const string NotLoaded = "RynthILT (the Infinite Leaftide plugin) isn't loaded.";

    private readonly ServerHubClient _hub;

    public IltHubView(ServerHubClient hub) => _hub = hub;

    /// <summary>A server plugin answers RynthAi's hub (RynthILT on Infinite Leaftide).</summary>
    public bool Loaded => _hub.Loaded;

    /// <summary>RynthCore's active server is Infinite Leaftide ("ilt") and its plugin says this world is its own.</summary>
    public bool IsIltWorld => _hub.Loaded && _hub.ServerActive
        && string.Equals(_hub.ActiveServerId, EngineServerState.IltId, StringComparison.OrdinalIgnoreCase);

    /// <summary>-1 unknown, 0 off, 1 on (RynthIltHub.Feature*).</summary>
    public int Feature(string key) => IsIltWorld ? _hub.Feature(key) : -1;

    public bool IsOff(string key) => Feature(key) == 0;

    public bool RenderSection(string id) => IsIltWorld && _hub.RenderSection(id);
}
