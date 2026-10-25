// DataInterface.cs - RynthILT's side of the "RynthILT.Data" interface (Plugins/Shared/RynthIltDataApi.cs):
// the charms list and the bank balances (every character RynthILT has seen on this server) the
// Mini Remote's Charms and Banking tabs show, for other plugins to draw, with the charm Toggle and
// the bank verbs they send back; since version 2 also the pet breeding tracker's JSON and verbs;
// since version 3 the Temple guardians' JSON and verbs (RynthOracle's Quests tab).
//
// The table is allocated once per loaded copy of RynthILT and never freed (a plugin DLL is never
// unloaded); after Release() it reports Version 0 and every function answers 0. Before login there
// is no controller: the JSON getters answer 0 (nothing to report) and the actions are refused.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.Plugin.RynthILT.Hub;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthILT;

internal static unsafe class DataInterface
{
    /// <summary>Longest bank / breeding verb line taken (a transfer with a long character name fits easily).</summary>
    private const int MaxVerbBytes = 256;

    private static RynthIltDataApiV1* _table;
    private static volatile bool _released;

    /// <summary>The table for (<paramref name="iface"/>, <paramref name="version"/>), or 0 when it isn't ours.</summary>
    public static IntPtr Query(string iface, uint version)
    {
        if (iface != RynthIltDataApiV1.InterfaceName || version < 1 || version > RynthIltDataApiV1.InterfaceVersion || _released)
            return IntPtr.Zero;
        if (_table == null)
        {
            var t = (RynthIltDataApiV1*)NativeMemory.AllocZeroed((nuint)sizeof(RynthIltDataApiV1));
            t->StructSize = (uint)sizeof(RynthIltDataApiV1);
            t->GetCharmsJson = &GetCharmsJson;
            t->GetBankJson = &GetBankJson;
            t->ToggleCharm = &ToggleCharm;
            t->BankCommand = &BankCommand;
            t->GetPetBreedingJson = &GetPetBreedingJson;
            t->PetBreedingCommand = &PetBreedingCommand;
            t->GetGuardianJson = &GetGuardianJson;
            t->GuardianCommand = &GuardianCommand;
            t->Version = RynthIltDataApiV1.InterfaceVersion;
            _table = t;
        }
        return (IntPtr)_table;
    }

    /// <summary>Plugin shutdown: readers see Version 0 and re-query; every function answers 0.</summary>
    public static void Release()
    {
        _released = true;
        if (_table != null) _table->Version = 0;
    }

    private static IltHubController? Hub => _released ? null : PluginExports.Runtime.Plugin?.Hub;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetCharmsJson(byte* buffer, int capacity)
    {
        try
        {
            string? json = Hub?.CharmsJson;
            return string.IsNullOrEmpty(json) ? 0 : ProfileInterface.Write(json, buffer, capacity);
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetBankJson(byte* buffer, int capacity)
    {
        try
        {
            string? json = Hub?.BankJson;
            return string.IsNullOrEmpty(json) ? 0 : ProfileInterface.Write(json, buffer, capacity);
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ToggleCharm(uint objectId)
    {
        try
        {
            IltHubController? hub = Hub;
            if (hub == null || objectId == 0) return 0;
            hub.RequestCharmToggle(objectId);
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int BankCommand(byte* utf8)
    {
        try
        {
            IltHubController? hub = Hub;
            if (hub == null) return 0;
            string? line = ReadVerb(utf8);
            if (line == null) return 0;
            hub.RequestBankVerb(line);
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetPetBreedingJson(byte* buffer, int capacity)
    {
        try
        {
            string? json = Hub?.PetBreedingJson;
            return string.IsNullOrEmpty(json) ? 0 : ProfileInterface.Write(json, buffer, capacity);
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int PetBreedingCommand(byte* utf8)
    {
        try
        {
            IltHubController? hub = Hub;
            if (hub == null) return 0;
            string? line = ReadVerb(utf8);
            if (line == null) return 0;
            hub.RequestBreedingVerb(line);
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetGuardianJson(byte* buffer, int capacity)
    {
        try
        {
            string? json = Hub?.GuardianJson;
            return string.IsNullOrEmpty(json) ? 0 : ProfileInterface.Write(json, buffer, capacity);
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GuardianCommand(byte* utf8)
    {
        try
        {
            IltHubController? hub = Hub;
            if (hub == null) return 0;
            string? line = ReadVerb(utf8);
            if (line == null) return 0;
            hub.RequestGuardianVerb(line);
            return 1;
        }
        catch { return 0; }
    }

    /// <summary>A reader's verb line, or null when it is empty, too long or not plain ASCII (no control characters reach a chat command).</summary>
    private static string? ReadVerb(byte* utf8)
    {
        if (utf8 == null) return null;
        int len = 0;
        while (len < MaxVerbBytes && utf8[len] != 0) len++;
        if (len == 0 || len >= MaxVerbBytes) return null;
        string line = System.Text.Encoding.UTF8.GetString(utf8, len);
        foreach (char c in line)
            if (c < 0x20 || c > 0x7E) return null;
        return line;
    }
}
