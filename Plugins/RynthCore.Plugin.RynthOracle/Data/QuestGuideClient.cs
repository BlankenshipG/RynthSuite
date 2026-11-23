// The Leaftide wiki quest guide as RynthILT keeps it (2026-11-01): "RynthILT.Server" v2's GetQuestGuideInfoJson
// says where the guide file is and when it changed; the file is read again only then. Infinite Leaftide only
// (RynthOracle polls it while its server is ILT). Pump thread.
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthOracle.Data;

internal sealed unsafe class QuestGuideClient
{
    private const long PollMs = 2000;
    private const long RequeryMs = 10_000;

    private RynthIltServerApiV1* _table;
    private long _nextPollAt, _nextQueryAt;
    private readonly byte[] _buffer = new byte[4096];
    private long _seenRevision = -1;
    private string _seenPath = "";
    private DateTime _fileTime = DateTime.MinValue;

    public QuestGuide Guide { get; private set; } = QuestGuide.Empty;
    /// <summary>Goes up when the guide or its status changes (the view redraws).</summary>
    public int Revision { get; private set; }
    /// <summary>RynthILT serves the guide (v2).</summary>
    public bool Available { get; private set; }
    public bool Running { get; private set; }
    public bool LoginSaved { get; private set; }
    public string Status { get; private set; } = "";
    /// <summary>The server's name from the info ("tag", the server guide's; "" for RynthILT's).</summary>
    public string Tag { get; private set; } = "";
    /// <summary>The Stamps tab follows this guide's Dynamic Stamps (RynthILT's); a server guide's has none.</summary>
    public bool FeedsDynamicStamps = true;

    public void Clear()
    {
        _nextPollAt = 0;
        _seenRevision = -1;
        _seenPath = "";
        if (Guide.Quests.Count > 0 || Available) Revision++;
        Guide = QuestGuide.Empty;
        Available = false;
        Tag = "";
        if (FeedsDynamicStamps) DynamicStampList.UseWiki(null);
        Status = "";
    }

    public void Poll(RynthCoreHost host, long now)
    {
        if (now < _nextPollAt) return;
        _nextPollAt = now + PollMs;
        RynthIltServerApiV1* t = Table(host, now);
        if (t == null)
        {
            if (Available) { Available = false; Revision++; }
            return;
        }
        string json;
        fixed (byte* b = _buffer)
        {
            int len = t->GetQuestGuideInfoJson(b, _buffer.Length);
            json = len > 0 && len < _buffer.Length ? Encoding.UTF8.GetString(_buffer, 0, len) : "";
        }
        Take(json);
    }

    /// <summary>Takes RynthILT's info JSON and reads the guide file when it changed (internal: the tests feed one).</summary>
    internal void Take(string infoJson)
    {
        if (infoJson.Length == 0) return;
        try
        {
            using JsonDocument d = JsonDocument.Parse(infoJson);
            JsonElement r = d.RootElement;
            string path = r.TryGetProperty("path", out JsonElement p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
            long rev = r.TryGetProperty("revision", out JsonElement v) && v.TryGetInt64(out long n) ? n : 0;
            bool running = r.TryGetProperty("running", out JsonElement ru) && ru.ValueKind == JsonValueKind.True;
            bool login = r.TryGetProperty("login", out JsonElement lo) && lo.ValueKind == JsonValueKind.True;
            string status = r.TryGetProperty("status", out JsonElement st) && st.ValueKind == JsonValueKind.String ? st.GetString() ?? "" : "";
            Tag = r.TryGetProperty("tag", out JsonElement tg) && tg.ValueKind == JsonValueKind.String ? tg.GetString() ?? "" : "";
            bool changed = !Available || running != Running || login != LoginSaved || status != Status;
            Available = true;
            Running = running;
            LoginSaved = login;
            Status = status;
            if (rev != _seenRevision || path != _seenPath)
            {
                bool pathChanged = path != _seenPath;
                _seenRevision = rev;
                _seenPath = path;
                DateTime t = path.Length > 0 && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                if (t != _fileTime || pathChanged)
                {
                    _fileTime = t;
                    Guide = t == DateTime.MinValue ? QuestGuide.Empty : QuestGuide.Parse(File.ReadAllText(path));
                    if (FeedsDynamicStamps) DynamicStampList.UseWiki(Guide.DynStamps);   // the Stamps tab follows the wiki (2026-11-21)
                }
                changed = true;
            }
            if (changed) Revision++;
        }
        catch (Exception)
        {
            // A guide being written (or a bad one): the next poll tries again.
            _seenRevision = -1;
        }
    }

    /// <summary>Asks RynthILT to "refresh", "import" or open its "settings". False without RynthILT v2.</summary>
    public bool Command(string line)
    {
        if (_table == null || _table->Version < 2) return false;
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\0");
        fixed (byte* b = bytes) return _table->QuestGuideCommand(b) != 0;
    }

    private RynthIltServerApiV1* Table(RynthCoreHost host, long now)
    {
        if (_table != null && _table->Version >= 2) return _table;
        _table = null;
        if (now < _nextQueryAt || !host.HasGetPluginInterface) return null;
        _nextQueryAt = now + RequeryMs;
        var p = (RynthIltServerApiV1*)host.GetPluginInterface(RynthIltServer.PluginName, RynthIltServerApiV1.InterfaceName, 2);
        if (p != null && p->Version >= 2 && p->StructSize >= (uint)sizeof(RynthIltServerApiV1) && p->GetQuestGuideInfoJson != null && p->QuestGuideCommand != null)
            _table = p;
        return _table;
    }
}
