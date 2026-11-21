// ServerGuideClient.cs - The active server's own wiki quest guide (2026-11-21): "RynthServer.QuestGuide"
// (Plugins/Shared/RynthServerQuestGuideApi.cs) from the plugin RynthCore names as the server's owner, e.g.
// RynthConquest's Conquest wiki quests. Its info JSON has RynthILT's guide info's shape, so a QuestGuideClient
// takes it (Take) and reads the guide file when it changed. RynthOracle names no server: whatever guide the
// active server's plugin offers is used (OracleContext.Wiki merges it over the AC Community Wiki's).
// Pump thread.
using System.Text;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthOracle.Data;

internal sealed unsafe class ServerGuideClient
{
    private const long PollMs = 2000;
    private const long RequeryMs = 10_000;

    private RynthServerQuestGuideApiV1* _table;
    private string _owner = "";
    private long _nextPollAt, _nextQueryAt;
    private readonly byte[] _buffer = new byte[4096];

    /// <summary>The guide as read (its Guide, Status, Tag, Revision).</summary>
    public readonly QuestGuideClient Client = new() { FeedsDynamicStamps = false };

    public void Clear()
    {
        _table = null;
        _owner = "";
        _nextPollAt = _nextQueryAt = 0;
        Client.Clear();
    }

    public void Poll(RynthCoreHost host, string owner, long now)
    {
        if (now < _nextPollAt) return;
        _nextPollAt = now + PollMs;
        if (owner != _owner)
        {
            _owner = owner;
            _table = null;
            _nextQueryAt = 0;
            Client.Clear();
        }
        if (owner.Length == 0) return;
        if (_table != null && _table->Version == 0) { _table = null; _nextQueryAt = 0; }
        if (_table == null)
        {
            if (now < _nextQueryAt || !host.HasGetPluginInterface) return;
            _nextQueryAt = now + RequeryMs;
            var p = (RynthServerQuestGuideApiV1*)host.GetPluginInterface(owner, RynthServerQuestGuideApiV1.InterfaceName, RynthServerQuestGuideApiV1.InterfaceVersion);
            if (p == null || p->Version < 1 || p->StructSize < (uint)sizeof(RynthServerQuestGuideApiV1) || p->GetGuideInfoJson == null) return;
            _table = p;
        }
        string json;
        fixed (byte* b = _buffer)
        {
            int len = _table->GetGuideInfoJson(b, _buffer.Length);
            json = len > 0 && len < _buffer.Length ? Encoding.UTF8.GetString(_buffer, 0, len) : "";
        }
        if (json.Length == 0)
        {
            if (Client.Guide.Quests.Count > 0 || Client.Available) Client.Clear();
            return;
        }
        Client.Take(json);
    }
}
