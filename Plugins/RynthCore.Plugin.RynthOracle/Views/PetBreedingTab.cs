using System;
using System.Collections.Generic;
using System.Globalization;
using RynthCore.Plugin.RynthOracle.Ui;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>
/// "Pet Breeding" (Infinite Leaftide): RynthILT's breeding tracker, the same one as RynthAi's Mini
/// Remote Pets > Breeding tab. RynthILT reads the essences, learns the server's rules and runs the
/// actions; this tab draws its JSON ("RynthILT.Data" GetPetBreedingJson) and sends its verbs back.
/// Dance and supply uses ask here first (RynthILT doesn't ask again).
/// </summary>
internal sealed partial class OracleView
{
    /// <summary>Essence rows drawn at most (each is about eight ops; the window's op budget is shared).</summary>
    private const int BreedingMaxRows = 40;

    /// <summary>The breeding verb waiting for Yes / No, with its question ("" = none).</summary>
    private string _breedConfirmVerb = "", _breedConfirmText = "";
    /// <summary>The essence the supplies are used on (index into the essence list).</summary>
    private int _breedTarget;

    private void DrawBreeding(UiWindow w)
    {
        IltBreedingView v = _c.Breeding;
        w.SeparatorText("Pet Breeding");
        if (!v.Loaded)
        {
            w.TextDisabled(_c.IltData.Connected && !_c.IltData.PetBreedingSupported
                ? "This RynthILT is older than 0.2.7 and has no breeding tracker; update RynthILT."
                : "Load RynthILT (0.2.7 or later) to track pet breeding here. Reading...");
            return;
        }

        uint col = v.Enabled == "off" ? UiColors.Red : v.Enabled == "on" ? UiColors.Green : UiColors.Yellow;
        w.TextColored(col, v.Enabled switch
        {
            "off" => "Pet breeding is off on this server.",
            "on" => $"Pet breeding: on. Breeding room: {v.Room}.",
            _ => "Pet breeding: not confirmed yet (Diagnostics in the breeding room checks).",
        });
        if (v.Bond == "off") w.TextColored(UiColors.Yellow, "Pet bond is off here, so bond can't reach the breeding minimum.");

        w.SmallButton("Refresh", "br.refresh", () => _c.BreedingCommand(IltBreedingView.RefreshVerb()));
        w.Tooltip("Re-appraise every essence (charges, rest timers, bond, growth).");
        w.SameLine();
        w.SmallButton("Diagnostics", "br.debug", () => _c.BreedingCommand(IltBreedingView.DebugVerb()));
        w.Tooltip("@breed-debug: whether you're in the breeding room, your pet, partners nearby.");
        w.SameLine();
        w.SmallButton("Dance", "br.dance", () => AskBreeding(IltBreedingView.DanceVerb(),
            "Dance now? If your partner dances in time with both pets summoned in the same room, the breed happens."));
        w.Tooltip("@dance: both owners must dance within a few seconds.");
        w.SameLine();
        w.SmallButton("Mini Remote", "br.remote", () => _c.RunCommand("/ra hub open breeding"));
        w.Tooltip("RynthAi's Mini Remote, Pets > Breeding (the same tracker, with the pair planner).");

        if (_breedConfirmVerb.Length > 0)
        {
            w.TextColored(UiColors.Gold, _breedConfirmText);
            string verb = _breedConfirmVerb;
            w.SmallButton("Yes", "br.yes", () => { _breedConfirmVerb = ""; _c.BreedingCommand(verb); });
            w.SameLine();
            w.SmallButton("No", "br.no", () => _breedConfirmVerb = "");
        }
        if (v.Message.Length > 0) w.TextDisabled(v.Message);
        else if (_c.BreedingMessage.Length > 0) w.TextDisabled(_c.BreedingMessage);
        if (v.FreeSlots == 0) w.TextColored(UiColors.Yellow, "Your main pack is full: as the dam's owner you couldn't receive the baby.");

        DrawBreedEssences(w, v);
        DrawBreedPairs(w, v);
        DrawBreedSupplies(w, v);
        DrawBreedRules(w, v);
        DrawBreedEvents(w, v);
    }

    /// <summary>Holds a verb until the player clicks Yes.</summary>
    private void AskBreeding(string verb, string question)
    {
        _breedConfirmVerb = verb;
        _breedConfirmText = question;
    }

    private void DrawBreedEssences(UiWindow w, IltBreedingView v)
    {
        int ready = 0;
        foreach (IltBreedEssenceRow e in v.Essences) if (e.Ready) ready++;
        if (!w.CollapsingHeader($"Essences ({ready} ready of {v.Essences.Count})", "br.ess", defaultOpen: true)) return;
        if (v.Essences.Count == 0) { w.TextDisabled("No combat pet essences in your pack."); return; }
        for (int i = 0; i < v.Essences.Count && i < BreedingMaxRows; i++)
        {
            IltBreedEssenceRow e = v.Essences[i];
            w.TextColored(e.Ready ? UiColors.Green : e.Problems.Count > 0 && e.ReadyIn > 0 ? UiColors.Yellow : UiColors.Grey, Cut(e.Status, 22));
            w.SameLine(170f);
            w.Text(Cut(e.Name, 32));
            w.Tooltip(BreedTooltip(e));
            w.SameLine(410f);
            w.TextDisabled($"{(e.Male ? "M" : "F")}  T{(e.Tier > 0 ? e.Tier.ToString(CultureInfo.InvariantCulture) : "?")}  B{e.Bond}  "
                           + $"Mut {e.Mutations}{(e.PotencyMutations > 0 ? "+" + e.PotencyMutations + "p" : "")}");
        }
        if (v.Essences.Count > BreedingMaxRows) w.TextDisabled($"... and {v.Essences.Count - BreedingMaxRows} more (see the Mini Remote).");
    }

    private static string BreedTooltip(IltBreedEssenceRow e)
    {
        var lines = new List<string> { $"{e.Name}: {(e.Male ? "stud" : "dam")}, tier {e.Tier}, bond {e.Bond}" };
        if (e.Male) lines.Add($"Charges {e.Charges}/{e.ChargesMax}");
        if (e.Juvenile) lines.Add($"Growing: {e.Stage}, {Math.Max(0, e.KillsRequired - e.Kills)} kills to Adult");
        if (e.Boosters.Count > 0) lines.Add("Boosters: " + string.Join(", ", e.Boosters));
        foreach (string p in e.Problems) lines.Add("- " + p);
        return string.Join("\n", lines);
    }

    private static void DrawBreedPairs(UiWindow w, IltBreedingView v)
    {
        if (v.Pairs.Count == 0) return;
        if (!w.CollapsingHeader($"Ready pairs ({v.Pairs.Count})", "br.pairs", defaultOpen: true)) return;
        w.TextDisabled("Stat mutation odds; fewer inherited mutations keep the odds up. Each owner dances with their own pet.");
        for (int i = 0; i < v.Pairs.Count; i++)
        {
            IltBreedPairRow p = v.Pairs[i];
            w.Text($"{Cut(p.Stud, 26)} x {Cut(p.Dam, 26)}");
            w.SameLine(400f);
            w.TextDisabled($"{Pct(p.ChanceWorst)}-{Pct(p.ChanceBest)}, inherits {p.InheritMin}-{p.InheritMax}");
        }
    }

    private void DrawBreedSupplies(UiWindow w, IltBreedingView v)
    {
        long carried = 0;
        foreach (IltBreedSupplyRow s in v.Supplies) carried += s.Count;
        if (!w.CollapsingHeader($"Supplies ({carried} carried)", "br.sup", defaultOpen: false)) return;
        w.TextDisabled("Ivo sells these at the Seedy Motel. Boosters are spent by the essence's next breed.");

        IltBreedEssenceRow? target = null;
        if (v.Essences.Count > 0)
        {
            var names = new List<string>(v.Essences.Count);
            foreach (IltBreedEssenceRow e in v.Essences) names.Add(Cut(e.Name, 34));
            int index = Math.Clamp(_breedTarget, 0, v.Essences.Count - 1);
            w.Combo("Use on", "br.target", index, names, i => _breedTarget = i);
            target = v.Essences[index];
        }

        for (int i = 0; i < v.Supplies.Count; i++)
        {
            IltBreedSupplyRow s = v.Supplies[i];
            w.TextColored(s.Count > 0 ? UiColors.Green : UiColors.Grey, $"{s.Count,3}  {s.Name}");
            w.Tooltip(s.Price > 0 ? $"{s.Effect}\nIvo's price: {Price(s.Price)} pyreals" : s.Effect);
            if (!s.Apply || s.Count <= 0 || target == null) continue;
            w.SameLine(330f);
            if (System.Linq.Enumerable.Contains(s.Targets, target.Id))
            {
                string verb = IltBreedingView.ApplyVerb(s.Key, target.Id);
                string q = $"Use {s.Name} on {target.Name}? {s.Effect}";
                w.SmallButton("Use", "br.use." + i, () => AskBreeding(verb, q));
            }
            else w.TextDisabled("-");
        }
    }

    private static void DrawBreedRules(UiWindow w, IltBreedingView v)
    {
        if (!w.CollapsingHeader("Server rules", "br.rules", defaultOpen: false)) return;
        w.TextDisabled("Values this server showed win over the ILT profile, which wins over ACE's defaults.");
        foreach (IltBreedRuleRow r in v.Rules)
        {
            w.Text(r.Label);
            if (r.Help.Length > 0) w.Tooltip(r.Help);
            w.SameLine(230f);
            w.Text(r.Value);
            w.SameLine(310f);
            w.TextColored(r.Source == "seen on server" ? UiColors.Green : UiColors.Grey, r.Source);
        }
        if (v.Debug is { } d)
        {
            w.Separator();
            w.TextDisabled($"Last diagnostics ({d.When} UTC):");
            if (d.Location.Length > 0) w.BulletText("You: " + d.Location);
            if (d.Match.Length > 0) w.BulletText(d.Match == "valid" ? "In the breeding room" : "Not in the breeding room");
            if (d.Pet.Length > 0) w.BulletText("Pet: " + d.Pet + (d.Sex.Length > 0 ? " (" + d.Sex + ")" : ""));
            if (d.Partners >= 0) w.BulletText($"Partners nearby: {d.Partners}");
        }
    }

    private static void DrawBreedEvents(UiWindow w, IltBreedingView v)
    {
        if (!w.CollapsingHeader($"Recent events ({v.Events.Count})", "br.events", defaultOpen: false)) return;
        if (v.Events.Count == 0) w.TextDisabled("Nothing yet: rituals, births, growth and charges land here.");
        foreach (IltBreedEventRow e in v.Events)
        {
            w.TextDisabled(e.When);
            w.SameLine(130f);
            w.TextWrapped(e.Text);
        }
    }

    private static string Pct(double v) => (v * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Price(long p) => p >= 1_000_000
        ? (p / 1_000_000.0).ToString("0.##", CultureInfo.InvariantCulture) + "M"
        : p.ToString("N0", CultureInfo.InvariantCulture);
}
