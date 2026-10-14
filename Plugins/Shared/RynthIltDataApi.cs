// ============================================================================
//  RynthSuite - Plugins/Shared/RynthIltDataApi.cs
//  The "RynthILT.Data" interface: the Infinite Leaftide data other plugins draw now that
//  the Mini Remote's Charms and Banking tabs are gone (2026-10-13):
//    - RynthOracle's Character tab lists the registry charms (GetCharmsJson, ToggleCharm);
//    - RynthInventory's Global Inventory "Banking" tab lists the bank balances of every
//      character RynthILT has seen on this server, and sends deposits, withdrawals,
//      transfers and balance reads (GetBankJson, BankCommand).
//  Separate from "RynthILT.Server" (the feature profile) and "RynthILT.Hub" (RynthAi's
//  exact-version table) so neither has to change for this.
//  Compiled into RynthILT and its readers (linked source), so the layout can't drift.
//
//  Rules (as the other shared interfaces):
//  - Append-only: new functions go at the end, and Version goes up.
//  - Strings are UTF-8, NUL-terminated. The JSON itself is plain ASCII.
//  - Every function returns safely (no exception crosses the boundary).
//  - The table is allocated once per loaded copy of RynthILT and never freed; shutdown
//    sets Version to 0, and a reader re-queries (host GetPluginInterface) when it sees that.
//  - Reading a JSON says "someone is looking": RynthILT rebuilds that snapshot on its pump
//    (every couple of seconds) only while it is being read, so an idle reader costs nothing.
//
//  GetCharmsJson payload (IltCharms.BuildReaderJson; read leniently):
//  { "available": true, "world": "InfiniteLeaftide", "total": 28, "acquiredCount": 5, "activeCount": 3,
//    "charms": [ { "name": "Mana Barrier", "effect": "...", "acquired": "carried"|"seen"|"no",
//                  "count": 1, "tier": 3, "maxTier": 5, "active": "on"|"off"|"unknown",
//                  "server": "on"|"off"|"unknown", "lastSeen": "2026-10-13", "toggleId": 1234567 } ],
//    "hands": [ { "id": 1234568, "name": "Guardian Hand", "active": "on"|"off"|"unknown",
//                 "server": "on"|"off"|"unknown" } ] }
//  "hands" are the Guardian Hands carried now (not a registry charm); "id" is their Toggle id.
//
//  GetBankJson payload (IltBankExport.Build; read leniently):
//  { "available": true, "enabled": 1, "world": "InfiniteLeaftide", "character": "Name",
//    "characters": [ { "name": "Name", "current": true, "pyreals": 0, "luminance": 0,
//                      "legendaryKeys": 0, "mythicalKeys": 0, "enlightenedCoins": 0,
//                      "weaklyEnlightenedCoins": 0, "prestigeCoins": 0, "updated": "2026-10-13T21:25:37Z" } ],
//    "deposits": ["Everything", ...], "withdrawals": [ { "label": "Pyreals", "notes": false } ],
//    "transfers": [ { "label": "Pyreals", "notes": false } ], "notes": [ { "label": "MMD", "value": 250000 } ],
//    "result": "Deposited ...", "transactions": [ { "when": "...", "type": "Sent", "amount": 1,
//                                                 "currency": "Pyreals", "player": "Who" } ] }
//  "enabled": 1 the server has /bank, 0 it doesn't, -1 not known yet.
//
//  BankCommand verbs (ASCII, space separated; the reply lands in "result"):
//    refresh                                         quiet /bank balance read
//    deposit <depositIndex>
//    withdraw <withdrawIndex> <amount> <noteIndex>   (amount = note count for trade notes)
//    transfer <currencyIndex> <amount> <noteIndex> <recipient name...>
//  The reader confirms a transfer with the player before sending it (RynthILT doesn't ask again).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.Shared;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct RynthIltDataApiV1
{
    public const string InterfaceName = "RynthILT.Data";
    public const uint InterfaceVersion = 1;

    public uint Version;      // InterfaceVersion of the table RynthILT filled; 0 once it has shut down
    public uint StructSize;   // sizeof(RynthIltDataApiV1) as RynthILT built it

    /// <summary>
    /// GetCharmsJson(buffer, capacity): the charms JSON (UTF-8, NUL-terminated) when it fits; returns
    /// its length without the NUL (capacity or more = too small, nothing written; 0 = nothing to report).
    /// </summary>
    public delegate* unmanaged[Cdecl]<byte*, int, int> GetCharmsJson;

    /// <summary>GetBankJson(buffer, capacity): the bank JSON, same contract as GetCharmsJson.</summary>
    public delegate* unmanaged[Cdecl]<byte*, int, int> GetBankJson;

    /// <summary>
    /// ToggleCharm(objectId): uses the carried charm with that id (a "toggleId" from the charms JSON).
    /// RynthILT checks it is one of the player's charms before using it. 1 = queued, 0 = refused.
    /// </summary>
    public delegate* unmanaged[Cdecl]<uint, int> ToggleCharm;

    /// <summary>BankCommand(utf8 verb line): queues a bank verb (header above). 1 = queued, 0 = refused.</summary>
    public delegate* unmanaged[Cdecl]<byte*, int> BankCommand;
}

/// <summary>
/// A reader of "RynthILT.Data" (RynthOracle, RynthInventory). Looks the table up through the host,
/// re-queried while RynthILT is missing or after it unloads (Version 0), and copies a JSON out on
/// request. Pump thread only.
/// </summary>
public sealed unsafe class RynthIltDataClient
{
    private const long RequeryMs = 10_000;
    private const int MaxBuffer = 256 * 1024;
    private const string PluginName = "RynthILT";

    private RynthIltDataApiV1* _table;
    private long _nextQueryAt;
    private byte[] _buffer = new byte[8192];

    /// <summary>RynthILT answered with the table (last lookup).</summary>
    public bool Connected => _table != null && _table->Version >= 1;

    /// <summary>The charms JSON now ("" without RynthILT).</summary>
    public string ReadCharms(RynthCoreHost host, long now)
    {
        RynthIltDataApiV1* t = Table(host, now);
        return t == null || t->GetCharmsJson == null ? "" : Read(t->GetCharmsJson);
    }

    /// <summary>The bank JSON now ("" without RynthILT).</summary>
    public string ReadBank(RynthCoreHost host, long now)
    {
        RynthIltDataApiV1* t = Table(host, now);
        return t == null || t->GetBankJson == null ? "" : Read(t->GetBankJson);
    }

    /// <summary>Asks RynthILT to use a carried charm. False without RynthILT or when it refused.</summary>
    public bool ToggleCharm(RynthCoreHost host, long now, uint objectId)
    {
        RynthIltDataApiV1* t = Table(host, now);
        if (t == null || t->ToggleCharm == null || objectId == 0) return false;
        try { return t->ToggleCharm(objectId) == 1; }
        catch { return false; }
    }

    /// <summary>Queues a bank verb line (see the header). False without RynthILT or when it refused.</summary>
    public bool BankCommand(RynthCoreHost host, long now, string verbLine)
    {
        RynthIltDataApiV1* t = Table(host, now);
        if (t == null || t->BankCommand == null || string.IsNullOrWhiteSpace(verbLine)) return false;
        byte[] bytes = Encoding.UTF8.GetBytes(verbLine + "\0");
        try
        {
            fixed (byte* p = bytes) return t->BankCommand(p) == 1;
        }
        catch { return false; }
    }

    /// <summary>The table, re-queried when it is missing or RynthILT has shut down (Version 0).</summary>
    private RynthIltDataApiV1* Table(RynthCoreHost host, long now)
    {
        if (_table != null && _table->Version >= 1) return _table;
        _table = null;
        if (now < _nextQueryAt || !host.HasGetPluginInterface) return null;
        _nextQueryAt = now + RequeryMs;
        var p = (RynthIltDataApiV1*)host.GetPluginInterface(PluginName, RynthIltDataApiV1.InterfaceName, RynthIltDataApiV1.InterfaceVersion);
        if (p != null && p->Version >= 1 && p->StructSize >= (uint)sizeof(RynthIltDataApiV1))
            _table = p;
        return _table;
    }

    /// <summary>Copies a JSON out, growing the buffer once when RynthILT says it didn't fit.</summary>
    private string Read(delegate* unmanaged[Cdecl]<byte*, int, int> get)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            int len;
            try
            {
                fixed (byte* b = _buffer) len = get(b, _buffer.Length);
            }
            catch { return ""; }
            if (len <= 0) return "";
            if (len < _buffer.Length) return Encoding.UTF8.GetString(_buffer, 0, len);
            if (len + 1 > MaxBuffer) return "";
            _buffer = new byte[len + 1024];
        }
        return "";
    }
}

/// <summary>One registry charm from the charms JSON.</summary>
public sealed record IltCharmRow(string Name, string Effect, string Acquired, int Count, int Tier, int MaxTier,
                                 string Active, string Server, string LastSeen, uint ToggleId)
{
    /// <summary>Carried now (in the pack or equipped).</summary>
    public bool Carried => Acquired == "carried";
}

/// <summary>A Guardian Hand carried now: its object id (the Toggle's id), name, status and the server's switch.</summary>
public sealed record IltHandRow(uint Id, string Name, string Active, string Server);

/// <summary>The charms JSON parsed. <see cref="None"/> without RynthILT; every field read leniently.</summary>
public sealed class IltCharmsView
{
    public static readonly IltCharmsView None = new();

    /// <summary>RynthILT answered.</summary>
    public bool Loaded { get; private init; }
    /// <summary>An Infinite Leaftide world (charms exist).</summary>
    public bool Available { get; private init; }
    public string World { get; private init; } = "";
    public int Acquired { get; private init; }
    public int Active { get; private init; }
    public IReadOnlyList<IltCharmRow> Charms { get; private init; } = Array.Empty<IltCharmRow>();
    /// <summary>The Guardian Hands carried now (none from an older RynthILT).</summary>
    public IReadOnlyList<IltHandRow> Hands { get; private init; } = Array.Empty<IltHandRow>();

    public static IltCharmsView Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return None;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return None;
            var rows = new List<IltCharmRow>();
            if (root.TryGetProperty("charms", out JsonElement cs) && cs.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement c in cs.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object) continue;
                    string name = IltJson.Str(c, "name");
                    if (name.Length == 0) continue;
                    rows.Add(new IltCharmRow(name, IltJson.Str(c, "effect"), IltJson.Str(c, "acquired", "no"),
                        (int)IltJson.Long(c, "count"), (int)IltJson.Long(c, "tier"), (int)IltJson.Long(c, "maxTier"),
                        IltJson.Str(c, "active", "unknown"), IltJson.Str(c, "server", "unknown"), IltJson.Str(c, "lastSeen"),
                        (uint)Math.Clamp(IltJson.Long(c, "toggleId"), 0, uint.MaxValue)));
                }
            }
            var hands = new List<IltHandRow>();
            foreach (JsonElement h in IltJson.Array(root, "hands"))
            {
                uint id = (uint)Math.Clamp(IltJson.Long(h, "id"), 0, uint.MaxValue);
                if (id == 0) continue;   // no id = nothing a Toggle could use
                hands.Add(new IltHandRow(id, IltJson.Str(h, "name", "Guardian Hand"), IltJson.Str(h, "active", "unknown"),
                                         IltJson.Str(h, "server", "unknown")));
            }
            return new IltCharmsView
            {
                Loaded = true,
                Available = IltJson.Bool(root, "available"),
                World = IltJson.Str(root, "world"),
                Acquired = (int)IltJson.Long(root, "acquiredCount"),
                Active = (int)IltJson.Long(root, "activeCount"),
                Charms = rows,
                Hands = hands,
            };
        }
        catch (JsonException) { return None; }
    }
}

/// <summary>One character's saved bank balances from the bank JSON.</summary>
public sealed record IltBankRow(string Name, bool Current, long Pyreals, long Luminance, long LegendaryKeys, long MythicalKeys,
                                long EnlightenedCoins, long WeaklyEnlightenedCoins, long PrestigeCoins, DateTime UpdatedUtc);

/// <summary>A bank form choice: its label and whether the amount is a trade-note count.</summary>
public sealed record IltBankChoice(string Label, bool Notes);

/// <summary>A trade-note denomination: label and pyreal value.</summary>
public sealed record IltBankNote(string Label, long Value);

/// <summary>One logged transfer.</summary>
public sealed record IltBankTx(string When, string Type, long Amount, string Currency, string Player);

/// <summary>The bank JSON parsed. <see cref="None"/> without RynthILT; every field read leniently.</summary>
public sealed class IltBankView
{
    public static readonly IltBankView None = new();

    /// <summary>RynthILT answered.</summary>
    public bool Loaded { get; private init; }
    /// <summary>An Infinite Leaftide world (bank commands exist).</summary>
    public bool Available { get; private init; }
    /// <summary>1 the server has /bank, 0 not, -1 not known yet.</summary>
    public int Enabled { get; private init; } = -1;
    public string World { get; private init; } = "";
    public string Character { get; private init; } = "";
    public string Result { get; private init; } = "";
    public IReadOnlyList<IltBankRow> Characters { get; private init; } = Array.Empty<IltBankRow>();
    public IReadOnlyList<string> Deposits { get; private init; } = Array.Empty<string>();
    public IReadOnlyList<IltBankChoice> Withdrawals { get; private init; } = Array.Empty<IltBankChoice>();
    public IReadOnlyList<IltBankChoice> Transfers { get; private init; } = Array.Empty<IltBankChoice>();
    public IReadOnlyList<IltBankNote> Notes { get; private init; } = Array.Empty<IltBankNote>();
    public IReadOnlyList<IltBankTx> Transactions { get; private init; } = Array.Empty<IltBankTx>();

    public static IltBankView Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return None;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return None;

            var chars = new List<IltBankRow>();
            foreach (JsonElement c in IltJson.Array(root, "characters"))
            {
                string name = IltJson.Str(c, "name");
                if (name.Length == 0) continue;
                DateTime updated = DateTime.MinValue;
                string when = IltJson.Str(c, "updated");
                if (when.Length > 0
                    && DateTime.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime t))
                    updated = t;
                chars.Add(new IltBankRow(name, IltJson.Bool(c, "current"), IltJson.Long(c, "pyreals"), IltJson.Long(c, "luminance"),
                    IltJson.Long(c, "legendaryKeys"), IltJson.Long(c, "mythicalKeys"), IltJson.Long(c, "enlightenedCoins"),
                    IltJson.Long(c, "weaklyEnlightenedCoins"), IltJson.Long(c, "prestigeCoins"), updated));
            }

            var deposits = new List<string>();
            if (root.TryGetProperty("deposits", out JsonElement ds) && ds.ValueKind == JsonValueKind.Array)
                foreach (JsonElement d in ds.EnumerateArray())
                    if (d.ValueKind == JsonValueKind.String) deposits.Add(d.GetString() ?? "");

            var notes = new List<IltBankNote>();
            foreach (JsonElement n in IltJson.Array(root, "notes"))
                notes.Add(new IltBankNote(IltJson.Str(n, "label"), IltJson.Long(n, "value")));

            var tx = new List<IltBankTx>();
            foreach (JsonElement x in IltJson.Array(root, "transactions"))
                tx.Add(new IltBankTx(IltJson.Str(x, "when"), IltJson.Str(x, "type"), IltJson.Long(x, "amount"),
                                     IltJson.Str(x, "currency"), IltJson.Str(x, "player")));

            return new IltBankView
            {
                Loaded = true,
                Available = IltJson.Bool(root, "available"),
                Enabled = (int)Math.Clamp(IltJson.Long(root, "enabled", -1), -1, 1),
                World = IltJson.Str(root, "world"),
                Character = IltJson.Str(root, "character"),
                Result = IltJson.Str(root, "result"),
                Characters = chars,
                Deposits = deposits,
                Withdrawals = Choices(root, "withdrawals"),
                Transfers = Choices(root, "transfers"),
                Notes = notes,
                Transactions = tx,
            };
        }
        catch (JsonException) { return None; }
    }

    private static List<IltBankChoice> Choices(JsonElement root, string name)
    {
        var list = new List<IltBankChoice>();
        foreach (JsonElement c in IltJson.Array(root, name))
            list.Add(new IltBankChoice(IltJson.Str(c, "label"), IltJson.Bool(c, "notes")));
        return list;
    }

    /// <summary>The bank verb lines (header of this file); pure so the tests check them.</summary>
    public static string RefreshVerb() => "refresh";
    public static string DepositVerb(int index) => "deposit " + index.ToString(CultureInfo.InvariantCulture);
    public static string WithdrawVerb(int index, long amount, int note)
        => string.Join(' ', "withdraw", index.ToString(CultureInfo.InvariantCulture), amount.ToString(CultureInfo.InvariantCulture),
                       note.ToString(CultureInfo.InvariantCulture));
    public static string TransferVerb(int currency, long amount, int note, string recipient)
        => string.Join(' ', "transfer", currency.ToString(CultureInfo.InvariantCulture), amount.ToString(CultureInfo.InvariantCulture),
                       note.ToString(CultureInfo.InvariantCulture), (recipient ?? "").Trim());
}

/// <summary>Lenient JSON field reads shared by the views above.</summary>
internal static class IltJson
{
    public static string Str(JsonElement o, string name, string fallback = "") =>
        o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    public static bool Bool(JsonElement o, string name) =>
        o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

    public static long Long(JsonElement o, string name, long fallback = 0) =>
        o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l : fallback;

    public static IEnumerable<JsonElement> Array(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out JsonElement a) || a.ValueKind != JsonValueKind.Array) yield break;
        foreach (JsonElement e in a.EnumerateArray())
            if (e.ValueKind == JsonValueKind.Object) yield return e;
    }
}
