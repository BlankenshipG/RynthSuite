using System;
using System.Linq;
using System.Text.Json;
using RynthCore.Plugin.RynthILT;
using RynthCore.Plugin.RynthILT.Hub;
using RynthCore.Plugin.Shared;
using RynthCore.RynthAiTests.Harness;

namespace RynthILT.Tests;

// RynthILT's "RynthServer.Profile" (ProfileInterface.cs): the same interface and JSON the server
// plugins serve, for "ilt", carrying Infinite Leaftide's tinkering salvage (Hub/IltSalvage.cs).
// The JSON is read back with the shared reader RynthAi's Crafting tab uses (EngineServerState.cs:
// ServerSalvageView.ParseList), so the rows it plans with are the ones checked here. 2026-10-15.
internal static unsafe class ProfileTests
{
    public static void Register(Runner r)
    {
        r.Add("profile: RynthILT serves RynthServer.Profile for ilt", Interface);
        r.Add("profile: ILT's salvage reaches the Crafting tab's reader (Leather after the cap, jewelry stack)", Salvage);
    }

    private static void Interface()
    {
        Check.Eq(ProfileInterface.Query("RynthILT.Hub", 1), IntPtr.Zero, "another interface's name: not this table");
        Check.Eq(ProfileInterface.Query(RynthServerProfileApiV1.InterfaceName, 2), IntPtr.Zero, "a newer version than we have: none");
        var t = (RynthServerProfileApiV1*)ProfileInterface.Query(RynthServerProfileApiV1.InterfaceName, 1);
        Check.True(t != null, "the table");
        Check.Eq(t->Version, RynthServerProfileApiV1.InterfaceVersion, "version");
        Check.Eq(t->StructSize, (uint)sizeof(RynthServerProfileApiV1), "struct size");
        Check.True(t->GetProfileJson != null, "GetProfileJson");

        string json = ProfileInterface.CurrentJson(true, "InfiniteLeaftide");
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Check.Eq(root.GetProperty("schema").GetInt32(), RynthServerProfile.Schema, "schema");
        Check.Eq(root.GetProperty("id").GetString(), "ilt", "the engine's ILT id");
        Check.Eq(root.GetProperty("name").GetString(), "Infinite Leaftide", "name");
        Check.Eq(root.GetProperty("plugin").GetString(), "RynthILT", "plugin");
        Check.True(root.GetProperty("active").GetBoolean(), "active");
        Check.Eq(root.GetProperty("world").GetString(), "InfiniteLeaftide", "world");
        Check.Eq(root.GetProperty("hosts")[0].GetString(), "infiniteleaftide.online", "hosts from the csproj");
        Check.Eq(ProfileInterface.CurrentJson(true, "InfiniteLeaftide"), json, "unchanged state: the same JSON");
        Check.False(JsonDocument.Parse(ProfileInterface.CurrentJson(false, null)).RootElement.GetProperty("active").GetBoolean(),
            "before the Hub is up: not active");
    }

    private static void Salvage()
    {
        var rows = ServerSalvageView.ParseList(ProfileInterface.CurrentJson(true, "InfiniteLeaftide"));
        Check.Eq(rows.Count, IltSalvage.Rows.Length, "every row arrives");
        for (int i = 0; i < rows.Count; i++)
        {
            var a = IltSalvage.Rows[i];
            var b = rows[i];
            bool same = a.Bench == b.Bench && a.Material == b.Material && a.Kind == b.Kind && a.Effect == b.Effect
                        && a.UsesTinkSlot == b.UsesTinkSlot && a.NameMatch == b.NameMatch && a.AppliedInt == b.AppliedInt
                        && a.Prepared == b.Prepared && a.Order == b.Order && a.AfterCap == b.AfterCap
                        && a.StackGroup == b.StackGroup && a.StackCap == b.StackCap;
            Check.True(same, $"row {i} ({a.Bench} {a.Material}) round-trips");
        }

        const int Leather = 52, Steel = 64, Serpentine = 71, Silver = 63, Diamond = 20, BlackGarnet = 15;
        foreach (string bench in new[] { "melee", "missile", "caster" })
        {
            var leather = rows.Single(r => r.Bench == bench && r.Material == Leather);
            Check.True(leather.AfterCap, bench + " Leather: after the cap");
            Check.False(leather.UsesTinkSlot, bench + " Leather: no tink slot");
            Check.Eq(leather.Kind, "once", bench + " Leather: once");
            Check.Eq(leather.Order, 995, bench + " Leather: last");
        }
        Check.Eq(rows.Count(r => r.AfterCap), 3, "only the weapon Leathers wait for the cap");

        var stack = rows.Where(r => r.StackGroup.Length > 0).ToList();
        Check.Eq(string.Join(",", stack.Select(r => r.Bench + ":" + r.Material)), $"jewelry:{Serpentine},jewelry:{Steel}", "Serpentine + Steel stack");
        Check.True(stack.All(r => r.StackGroup == "jewelry-stack" && r.StackCap == 10 && r.Kind == "tink" && !r.UsesTinkSlot),
            "one group, cap 10, repeatable, no tink slot");

        Check.Eq(rows.Single(r => r.Bench == "jewelry" && r.Material == Silver).AppliedInt, 374, "jewelry Silver flag int");
        var diamond = rows.Single(r => r.Bench == "trinkets" && r.Material == Diamond);
        Check.True(diamond.Prepared && diamond.AppliedInt == 384 && diamond.Order == 10, "trinket Diamond: prepared, flag 384, first");
        Check.Eq(rows.Count(r => r.Prepared), 4, "the four trinket tiers are prepared");
        Check.True(rows.Where(r => r.Material == BlackGarnet).All(r => r.NameMatch == "Abyssal" && r.UsesTinkSlot),
            "Abyssal-Touched Gems by bag name, using a tink slot");
        Check.False(rows.Any(r => r.Bench is "armor" or "items"), "no armor or item rows");
    }
}
