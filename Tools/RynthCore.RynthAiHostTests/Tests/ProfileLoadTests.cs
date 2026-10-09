using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.ItemInfo;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-10-08: "Describe items when selected" and the Item Info options did not stay with the
// character's profile. They were written to the profile file, but LegacyDashboardRenderer.
// CopySettings (used by every login and profile switch) copies the loaded profile field by
// field and had no line for ItemInfoSettings, so the in-memory defaults stayed and the next
// autosave wrote them over the file. NavOverlay, PetAutoRefill, EnableGroundLoot,
// MonsterNameBlacklist, AmmoRules and the radar placement were dropped the same way.
//  - every serialized LegacyUiSettings member set to a non-default value comes through
//    CopySettings (catches any field added later without a copy line);
//  - the item info options survive a save / load through a profile file on disk.
internal static class ProfileLoadTests
{
    private const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("profile: every saved setting survives loading (CopySettings lists each serialized field)", EverySavedFieldIsCopied);
        r.Add("profile: Describe Items and the Item Info options survive a save and a reload", ItemInfoSurvivesReload);
        r.Add("profile: a profile with null item info / nav overlay loads defaults, not null", NullNestedOptionsLoadDefaults);
    }

    /// <summary>LegacyDashboardRenderer.CopySettings(src, dst): what LoadSettings / SwitchProfile run.</summary>
    private static void CopySettings(LegacyUiSettings src, LegacyUiSettings dst) =>
        typeof(LegacyDashboardRenderer).GetMethod("CopySettings", Any)!.Invoke(null, new object[] { src, dst });

    /// <summary>A different value of the member's type that CopySettings' normalizing keeps distinct from the default.</summary>
    private static bool TryMutate(string name, Type type, object? value, out object? mutated)
    {
        mutated = null;
        if (name == nameof(LegacyUiSettings.OffhandDefault)) { mutated = "Shield"; return true; }   // must parse as an OffhandMode
        if (type == typeof(bool))   { mutated = !(bool)value!; return true; }
        if (type == typeof(int))    { mutated = (int)value! + 3; return true; }   // +3: MovementMode 1/2 map back to 0
        if (type == typeof(long))   { mutated = (long)value! + 3; return true; }
        if (type == typeof(uint))   { mutated = (uint)value! + 3; return true; }
        if (type == typeof(float))  { mutated = (float)value! + 3f; return true; }
        if (type == typeof(double)) { mutated = (double)value! + 3.0; return true; }
        if (type == typeof(string)) { mutated = (string?)value + "x"; return true; }
        if (type.IsEnum)
        {
            foreach (object v in Enum.GetValues(type))
                if (!v.Equals(value)) { mutated = v; return true; }
        }
        return false;
    }

    private static void EverySavedFieldIsCopied()
    {
        var info = RynthAiJsonContext.Default.LegacyUiSettings;
        var defaults = new LegacyUiSettings();
        var src = new LegacyUiSettings();
        var dst = new LegacyUiSettings();

        // Reference members (lists, option objects): dst must end up with a new instance.
        var dstOriginal = new Dictionary<string, object?>();
        var skipped = new List<string>();
        foreach (var p in info.Properties)
        {
            if (p.Get == null || p.Set == null) continue;
            Type t = p.PropertyType;
            if (t.IsValueType || t == typeof(string))
            {
                if (TryMutate(p.Name, t, p.Get(src), out object? m)) p.Set(src, m);
                else skipped.Add(p.Name);
            }
            else
            {
                dstOriginal[p.Name] = p.Get(dst);
            }
        }

        CopySettings(src, dst);

        int members = 0;
        var missing = new List<string>();
        foreach (var p in info.Properties)
        {
            if (p.Get == null || p.Set == null) continue;
            members++;
            if (dstOriginal.TryGetValue(p.Name, out object? before))
            {
                if (before != null && ReferenceEquals(p.Get(dst), before)) missing.Add(p.Name);
            }
            else if (!skipped.Contains(p.Name)
                     && Equals(p.Get(dst), p.Get(defaults)) && !Equals(p.Get(src), p.Get(defaults)))
            {
                missing.Add(p.Name);
            }
        }

        Check.True(members > 100, $"the JSON metadata lists the settings ({members} members)");
        Check.Eq(skipped.Count, 0, "every value member has a test value (no test value for: " + string.Join(", ", skipped) + ")");
        Check.Eq(missing.Count, 0, "members CopySettings leaves at their default: " + string.Join(", ", missing));
    }

    private static void ItemInfoSurvivesReload()
    {
        // What the Item Info window / the engine checkbox change, then autosave writes.
        var saved = new LegacyUiSettings();
        MagItemInfoSettings s = saved.ItemInfoSettings;
        s.OnSelect = true;
        s.ClickTrigger = MagItemInfoSettings.ClickRight;
        s.OnSelectArmor = false;
        s.SetHidden(MagItemInfoField.Spells, true);
        s.SetRatingHidden(2, true);
        s.Layout = MagItemInfoSettings.LayoutOneLine;
        s.ChatType = 7;
        s.Prefix = "[ii] ";
        saved.EnableGroundLoot = true;
        saved.PetAutoRefill = false;
        saved.MonsterNameBlacklist.Add("Drudge Skulker");

        string path = Path.Combine(Program.TempRoot, "profile-load", "Default.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(saved, RynthAiJsonContext.Default.LegacyUiSettings));

        // LoadSettings: read the file, copy it into the live settings object (a fresh session).
        var loaded = JsonSerializer.Deserialize(File.ReadAllText(path), RynthAiJsonContext.Default.LegacyUiSettings)!;
        var live = new LegacyUiSettings();
        CopySettings(loaded, live);

        MagItemInfoSettings l = live.ItemInfoSettings;
        Check.True(l.OnSelect, "Describe items when selected stays on");
        Check.Eq(l.ClickTrigger, MagItemInfoSettings.ClickRight, "click trigger");
        Check.False(l.OnSelectArmor, "armor filter off");
        Check.True(l.IsHidden(MagItemInfoField.Spells), "hidden field");
        Check.True(l.IsRatingHidden(2), "hidden rating");
        Check.Eq(l.Layout, MagItemInfoSettings.LayoutOneLine, "layout");
        Check.Eq(l.ChatType, 7, "chat type");
        Check.Eq(l.Prefix, "[ii] ", "prefix");
        Check.True(live.EnableGroundLoot, "ground loot");
        Check.False(live.PetAutoRefill, "pet auto refill");
        Check.True(live.MonsterNameBlacklist.Contains("Drudge Skulker"), "monster blacklist");

        // Unchanged JSON means the autosave right after login has nothing to write over the file.
        Check.Eq(JsonSerializer.Serialize(live, RynthAiJsonContext.Default.LegacyUiSettings),
                 JsonSerializer.Serialize(saved, RynthAiJsonContext.Default.LegacyUiSettings),
                 "the reloaded profile saves back unchanged (autosave writes nothing different)");
    }

    private static void NullNestedOptionsLoadDefaults()
    {
        string json = JsonSerializer.Serialize(new LegacyUiSettings(), RynthAiJsonContext.Default.LegacyUiSettings);
        var doc = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.LegacyUiSettings)!;
        doc.ItemInfoSettings = null!;
        doc.NavOverlay = null!;
        var live = new LegacyUiSettings();
        live.ItemInfoSettings.Prefix = new string('p', 40);   // replaced by the loaded (default) object
        CopySettings(doc, live);
        Check.NotNull(live.ItemInfoSettings, "item info is never null after a load");
        Check.NotNull(live.NavOverlay, "nav overlay is never null after a load");
        Check.Eq(live.ItemInfoSettings.Prefix, new MagItemInfoSettings().Prefix, "item info falls back to defaults");

        // An over-long hand-edited prefix is trimmed on load (Sanitize).
        var edited = new LegacyUiSettings();
        edited.ItemInfoSettings.Prefix = new string('p', 40);
        var live2 = new LegacyUiSettings();
        CopySettings(edited, live2);
        Check.Eq(live2.ItemInfoSettings.Prefix.Length, 32, "prefix trimmed to 32");
    }
}
