// IltGear.cs — ILT Hub "Gear" tab: the gear tools.
//
// The sections are separate modules (buff keepers, chunk/clap, split arrows, equipment suits,
// VTank import) that this tab just lays out. The charm strip (registry charms and the Guardian
// Hand, with their toggles) is the Charms tab now (IltCharms).
using ImGuiNET;

namespace RynthCore.Plugin.RynthAi.IltHub;

internal sealed class IltGear : IIltFeature
{
    public IltBuffKeepers Keepers { get; }
    public IltChunkClap ChunkClap { get; }
    public IltSplitArrows Split { get; }
    public IltEquipSuits Suits { get; }
    public IltUsdImporter Usd { get; }

    public IltGear(IltHubContext ctx)
    {
        Keepers = new IltBuffKeepers(ctx);
        ChunkClap = new IltChunkClap(ctx);
        Split = new IltSplitArrows(ctx);
        Suits = new IltEquipSuits(ctx);
        Usd = new IltUsdImporter(ctx);
    }

    // ── IIltFeature ─────────────────────────────────────────────────────────

    public void Tick(long nowMs)
    {
        Keepers.Tick(nowMs);
        ChunkClap.Tick(nowMs);
        Split.Tick(nowMs);
        Suits.Tick(nowMs);
    }

    public bool OnChat(string text)
    {
        bool eat = Keepers.OnChat(text);
        eat |= ChunkClap.OnChat(text);
        eat |= Split.OnChat(text);
        eat |= Suits.OnChat(text);
        return eat;
    }

    public void OnLogout()
    {
        Keepers.OnLogout();
        ChunkClap.OnLogout();
        Split.OnLogout();
        Suits.OnLogout();
    }

    // ── UI (render thread) ──────────────────────────────────────────────────

    public void Render()
    {
        if (ImGui.CollapsingHeader("Dispel / Surging Strength", ImGuiTreeNodeFlags.DefaultOpen)) Keepers.Render();
        if (ImGui.CollapsingHeader("Chunking / Clap")) ChunkClap.Render();
        if (ImGui.CollapsingHeader("Split arrows")) Split.Render();
        if (ImGui.CollapsingHeader("Equipment suits")) Suits.Render();
        if (ImGui.CollapsingHeader("VirindiTank import")) Usd.Render();
        ImGui.TextDisabled("Charms and the Guardian Hand are on the Charms tab.");
    }
}
