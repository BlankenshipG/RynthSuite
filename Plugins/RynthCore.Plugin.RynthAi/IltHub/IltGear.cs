// IltGear.cs — ILT Hub "Gear" tab: the gear tools.
//
// The sections are separate modules (buff keepers, equipment suits, VTank import) that this tab
// just lays out. The charm strip (registry charms and the Guardian Hand, with their toggles) is
// the Charms tab (IltCharms); chunking / clap and split arrows moved to the universal Crafting
// tab (IltCrafting) with the tinkering benches.
using ImGuiNET;

namespace RynthCore.Plugin.RynthAi.IltHub;

internal sealed class IltGear : IIltFeature
{
    public IltBuffKeepers Keepers { get; }
    public IltEquipSuits Suits { get; }
    public IltUsdImporter Usd { get; }

    public IltGear(IltHubContext ctx)
    {
        Keepers = new IltBuffKeepers(ctx);
        Suits = new IltEquipSuits(ctx);
        Usd = new IltUsdImporter(ctx);
    }

    // ── IIltFeature ─────────────────────────────────────────────────────────

    public void Tick(long nowMs)
    {
        Keepers.Tick(nowMs);
        Suits.Tick(nowMs);
    }

    public bool OnChat(string text)
    {
        bool eat = Keepers.OnChat(text);
        eat |= Suits.OnChat(text);
        return eat;
    }

    public void OnLogout()
    {
        Keepers.OnLogout();
        Suits.OnLogout();
    }

    // ── UI (render thread) ──────────────────────────────────────────────────

    public void Render()
    {
        if (ImGui.CollapsingHeader("Dispel / Surging Strength", ImGuiTreeNodeFlags.DefaultOpen)) Keepers.Render();
        if (ImGui.CollapsingHeader("Equipment suits")) Suits.Render();
        if (ImGui.CollapsingHeader("VirindiTank import")) Usd.Render();
        ImGui.TextDisabled("Charms are on the Charms tab; chunking, clap and split arrows on Crafting.");
    }
}
