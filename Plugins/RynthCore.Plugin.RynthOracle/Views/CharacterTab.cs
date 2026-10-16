// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>
/// Character: level and experience, luminance, the status summary (upstream's Status HUD: buff,
/// house, beer, page and rare timers, key skills, aetheria surges), RynthAi's session, RynthILT's
/// charms (the Mini Remote's Character and Charms tabs moved here 2026-10-13) and the full buff list.
/// </summary>
internal sealed partial class OracleView
{
    private void DrawCharacter(UiWindow w)
    {
        CharacterSnapshot c = _c.Character;
        if (!c.Valid)
        {
            w.TextDisabled("Character data isn't available yet.");
            return;
        }

        w.SeparatorText(c.Name.Length > 0 ? c.Name : "Character");
        w.Text($"Level {c.Level}");
        w.SameLine(130f);
        w.Text($"Total experience {TimeText.Number(c.TotalXp)}");
        (long toNext, float frac) = c.LevelProgress();
        w.ProgressBar(frac, -1f, 0f, c.Level >= CharacterSnapshot.MaxLevel || c.Level <= 0
            ? "Maximum level"
            : $"{TimeText.Number(toNext)} to level {c.Level + 1}");
        Field(w, "Unassigned XP", TimeText.Number(c.UnassignedXp), 0f, 130f);
        Field(w, "Skill credits", c.SkillCredits.ToString(), 300f, 420f);
        Field(w, "Luminance", c.MaxLuminance > 0 ? $"{TimeText.Number(c.Luminance)} / {TimeText.Number(c.MaxLuminance)}" : "not unlocked", 0f, 130f);
        Field(w, "Deaths", c.Deaths.ToString(), 300f, 420f);
        if (c.Enlightenment > 0 || c.VitaePercent > 0)
        {
            Field(w, "Enlightenment", c.Enlightenment.ToString(), 0f, 130f);
            Field(w, "Vitae", c.VitaePercent > 0 ? $"{c.VitaePercent}%" : "none", 300f, 420f);
        }

        w.SeparatorText("Status");
        Field(w, "Buffs", BuffsText(c), 0f, 100f);
        Field(w, "House", GroupText(c, SpellIds.House, withHours: true), 300f, 400f);
        Field(w, "Beers", GroupText(c, SpellIds.Beer, withHours: false), 0f, 100f);
        Field(w, "Pages", GroupText(c, SpellIds.Pages, withHours: false), 300f, 400f);
        Field(w, "Rares", RareText(c), 0f, 100f);
        Field(w, "Cloaked", OneText(c, SpellIds.CloakedInSkill), 300f, 400f);
        Field(w, "Destruction", OneText(c, SpellIds.SurgeOfDestruction), 0f, 100f);
        Field(w, "Protection", OneText(c, SpellIds.SurgeOfProtection), 300f, 400f);
        Field(w, "Regen", OneText(c, SpellIds.SurgeOfRegeneration), 0f, 100f);
        Field(w, "Lockpick", LockpickText(c.Lockpick), 0f, 100f);
        Field(w, "Life", SkillText(c.Life), 300f, 400f);
        Field(w, "Melee D", SkillText(c.MeleeDefense), 0f, 100f);
        Field(w, "Summoning", SkillText(c.Summoning), 300f, 400f);

        DrawSession(w);
        DrawCharms(w);

        var list = new List<ActiveEnchantment>(c.Enchantments);
        list.Sort((a, b) => a.Remaining.CompareTo(b.Remaining));
        if (w.CollapsingHeader($"Buffs and debuffs ({list.Count})", "c.buffs", defaultOpen: true))
        {
            if (list.Count == 0) w.TextDisabled("None.");
            foreach (ActiveEnchantment e in list)
            {
                if (e.Debuff) w.TextColored(UiColors.Red, Cut(e.Name, 44));
                else w.Text(Cut(e.Name, 44));
                w.SameLine(330f);
                w.Text(double.IsPositiveInfinity(e.Remaining) ? "-" : TimeText.Clock(e.Remaining));
            }
        }
    }

    /// <summary>
    /// "Session": RynthAi's session (the Mini Remote's Session Stats), so every tracker shows the same
    /// XP, luminance and kills. Read through "RynthAi.Hub" only while this tab is on screen.
    /// </summary>
    private void DrawSession(UiWindow w)
    {
        w.SeparatorText("Session");
        if (!_c.SessionKnown)
        {
            w.TextDisabled("Load RynthAi for the session rates (the Mini Remote's Session Stats).");
            return;
        }
        RynthCore.Plugin.Shared.SessionStatsV1 s = _c.Session;
        w.TextDisabled($"From RynthAi's Mini Remote, {TimeText.Clock(Math.Max(0, s.SessionSeconds))} so far.");
        Field(w, "XP", $"{TimeText.Number(s.Xp)} ({RateText(s.XpPerHour)}/h)", 0f, 100f);
        Field(w, "Luminance", $"{TimeText.Number(s.Lum)} ({RateText(s.LumPerHour)}/h)", 300f, 400f);
        Field(w, "Kills", $"{s.Kills} ({s.KillsPerHour:0}/h)", 0f, 100f);
        Field(w, "Lum, 5 min", TimeText.Number(s.LumLast5Min), 300f, 400f);
        if (s.PassUpTracked != 0)
            Field(w, "Pass-up", $"{TimeText.Number(s.LumPassUp)} ({RateText(s.PassUpPerHour)}/h)", 0f, 100f);
        w.SmallButton("Session Stats", "c.session.open", () => _c.RunCommand(SessionStatsCommand));
        w.Tooltip("Opens RynthCore's Mini Remote on Session Stats (reset the session there).");
    }

    /// <summary>
    /// The Mini Remote's Session Stats section (RynthCore 2026-10-13). "section", not "tab": the
    /// engine sends "tab char" back here (/ro character), since that tab moved to this one.
    /// </summary>
    internal const string SessionStatsCommand = "/rc miniremote section char";

    private static string RateText(double perHour) => perHour > 0 ? TimeText.Number((long)Math.Round(perHour)) : "0";

    /// <summary>
    /// "Charms": RynthILT's Infinite Leaftide charm list (the Mini Remote's old Charms tab), with a
    /// Toggle for each carried charm and Guardian Hand. Nothing is listed here: RynthILT sends the
    /// list, so a charm it learns about shows up without an Oracle update.
    /// </summary>
    private void DrawCharms(UiWindow w)
    {
        Shared.IltCharmsView v = _c.Charms;
        if (!v.Loaded)
        {
            if (_c.Server.Is(ServerTags.InfiniteLeaftide))
            {
                w.SeparatorText("Charms");
                w.TextDisabled("Load RynthILT to list your Infinite Leaftide charms here.");
            }
            return;
        }
        if (!v.Available) return;   // not an ILT world: no charms

        w.SeparatorText("Charms");
        w.TextDisabled($"{v.Acquired} acquired, {v.Active} on (from RynthILT{(v.World.Length > 0 ? ", " + v.World : "")}).");
        if (_c.CharmMessage.Length > 0) w.TextDisabled(_c.CharmMessage);

        if (w.CollapsingHeader($"Charms ({v.Charms.Count})", "c.charms", defaultOpen: true))
        {
            if (v.Charms.Count == 0) w.TextDisabled("RynthILT hasn't listed any charms yet.");
            for (int i = 0; i < v.Charms.Count; i++)
            {
                Shared.IltCharmRow r = v.Charms[i];
                CharmState(w, r.Active);
                w.SameLine(50f);
                if (r.Carried) w.Text(Cut(r.Name, 30));
                else w.TextDisabled(Cut(r.Name, 30));
                if (r.Effect.Length > 0) w.Tooltip(r.Effect);
                w.SameLine(270f);
                w.TextDisabled(r.MaxTier > 0 ? $"T{r.Tier}/{r.MaxTier}" : r.Tier > 0 ? $"T{r.Tier}" : "-");
                w.SameLine(330f);
                w.TextDisabled(AcquiredText(r));
                if (r.Server == "off")
                {
                    w.SameLine(430f);
                    w.TextColored(UiColors.Red, "server off");
                }
                if (r.Carried && r.ToggleId != 0)
                {
                    w.SameLine(510f);
                    uint id = r.ToggleId;
                    w.SmallButton("Toggle", "c.charm." + i, () => _c.ToggleCharm(id));
                }
            }
        }

        if (v.Hands.Count > 0 && w.CollapsingHeader($"Guardian Hands ({v.Hands.Count})", "c.hands", defaultOpen: true))
        {
            for (int i = 0; i < v.Hands.Count; i++)
            {
                Shared.IltHandRow h = v.Hands[i];
                CharmState(w, h.Active);
                w.SameLine(50f);
                w.Text(Cut(h.Name, 30));
                if (h.Server == "off")
                {
                    w.SameLine(430f);
                    w.TextColored(UiColors.Red, "server off");
                }
                w.SameLine(510f);
                uint id = h.Id;
                w.SmallButton("Toggle", "c.hand." + i, () => _c.ToggleCharm(id));
            }
        }
    }

    /// <summary>ON in green, OFF in red, "?" in grey (status not read yet).</summary>
    private static void CharmState(UiWindow w, string active)
    {
        switch (active)
        {
            case "on": w.TextColored(UiColors.Green, "ON"); break;
            case "off": w.TextColored(UiColors.Red, "OFF"); break;
            default: w.TextColored(UiColors.Grey, "?"); break;
        }
    }

    /// <summary>Carried (with the count), stored (seen on this character before) or not acquired.</summary>
    internal static string AcquiredText(Shared.IltCharmRow r) => r.Acquired switch
    {
        "carried" => r.Count > 1 ? $"carried x{r.Count}" : "carried",
        "seen" => r.LastSeen.Length > 0 ? "seen " + r.LastSeen : "seen",
        _ => "not acquired",
    };

    /// <summary>
    /// The buff timer: the soonest-ending long (over 15 minutes) buff that isn't a beer, a debuff
    /// or one of the not-really-buffs, with how many there are (upstream's rule).
    /// </summary>
    private static string BuffsText(CharacterSnapshot c)
    {
        double min = double.MaxValue;
        int count = 0;
        foreach (ActiveEnchantment e in c.Enchantments)
        {
            if (e.Duration <= 900 || double.IsPositiveInfinity(e.Remaining) || e.Debuff) continue;
            if (SpellIds.Beer.Contains(e.SpellId) || SpellIds.NotBuff.Contains(e.SpellId)) continue;
            count++;
            if (e.Remaining < min) min = e.Remaining;
        }
        return count == 0 ? "-" : $"{TimeText.Clock(min)} ({count})";
    }

    private static string GroupText(CharacterSnapshot c, HashSet<uint> ids, bool withHours)
    {
        double min = double.MaxValue;
        int count = 0;
        foreach (ActiveEnchantment e in c.Enchantments)
        {
            if (!ids.Contains(e.SpellId) || double.IsPositiveInfinity(e.Remaining)) continue;
            count++;
            if (e.Remaining < min) min = e.Remaining;
        }
        if (count == 0) return "-";
        return withHours ? $"{TimeText.Clock(min)} ({count})" : TimeText.Clock(min);
    }

    private static string OneText(CharacterSnapshot c, uint spellId)
    {
        double min = double.MaxValue;
        foreach (ActiveEnchantment e in c.Enchantments)
            if (e.SpellId == spellId && e.Remaining < min) min = e.Remaining;
        return min == double.MaxValue ? "-" : double.IsPositiveInfinity(min) ? "on" : TimeText.Clock(min);
    }

    /// <summary>Rares: the soonest to end, how many, and the 3-minute cooldown left after the newest (upstream's rule).</summary>
    private static string RareText(CharacterSnapshot c)
    {
        double min = double.MaxValue, max = 0;
        int count = 0;
        foreach (ActiveEnchantment e in c.Enchantments)
        {
            if (!SpellIds.Rare.Contains(e.SpellId) || double.IsPositiveInfinity(e.Remaining)) continue;
            count++;
            min = Math.Min(min, e.Remaining);
            max = Math.Max(max, e.Remaining);
        }
        if (count == 0) return "-";
        double cooldown = 180 - (900 - max);
        return cooldown > 0
            ? $"{TimeText.Clock(min)} ({count}) {(int)cooldown}s"
            : $"{TimeText.Clock(min)} ({count})";
    }

    private static string SkillText(SkillValue s) => s.Known ? s.Current.ToString() : "-";

    /// <summary>Lockpick, with the Viridian Rise essences one try costs at this skill (upstream's table).</summary>
    private static string LockpickText(SkillValue s)
    {
        if (!s.Known) return "-";
        int v = s.Current;
        if (v < 500) return v.ToString();
        (int a, int b) = v >= 575 ? (3, 10) : v >= 570 ? (4, 11) : v >= 565 ? (5, 12) : v >= 550 ? (6, 13) : v >= 525 ? (6, 14) : (7, 15);
        return $"{v} (VR {a}/{b})";
    }
}
