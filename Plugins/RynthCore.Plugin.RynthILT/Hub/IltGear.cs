// IltGear.cs — ILT Hub "Gear" tab: the gear tools.
// Originally SilentKelpie's ILT Hub (RynthAi IltHub/); moved to RynthILT 2026-10-13.
//
// The sections are separate modules (buff keepers, chunk/clap, split arrows, equipment suits)
// that this tab just lays out, plus RynthAi's VTank .usd importer (it edits RynthAi's own rules,
// so it stayed in RynthAi and is drawn here through RynthAi.Hub RenderSection). The charm strip (registry charms and the Guardian
// Hand, with their toggles) is the Charms tab now (IltCharms). The Rings inscription chain
// (IltRingChainRunner) is SilentKelpie's RynthAi Crafting > Rings, moved here 2026-10-15.
using ImGuiNET;

namespace RynthCore.Plugin.RynthILT.Hub;

internal sealed class IltGear : IIltFeature
{
    public IltBuffKeepers Keepers { get; }
    public IltChunkClap ChunkClap { get; }
    public IltSplitArrows Split { get; }
    public IltEquipSuits Suits { get; }
    public IltRingChainRunner Rings { get; }

    private readonly IltHubContext _ctx;

    public IltGear(IltHubContext ctx)
    {
        Keepers = new IltBuffKeepers(ctx);
        ChunkClap = new IltChunkClap(ctx);
        Split = new IltSplitArrows(ctx);
        Suits = new IltEquipSuits(ctx);
        // The two pack jobs that answer craft dialogs never run together.
        Rings = new IltRingChainRunner(ctx, () => Split.Running);
        Split.Blocked = () => Rings.Running;
        _ctx = ctx;
    }

    // ── IIltFeature ─────────────────────────────────────────────────────────

    public void Tick(long nowMs)
    {
        Keepers.Tick(nowMs);
        ChunkClap.Tick(nowMs);
        Split.Tick(nowMs);
        Suits.Tick(nowMs);
        Rings.Tick(nowMs);
    }

    public bool OnChat(string text)
    {
        bool eat = Keepers.OnChat(text);
        eat |= ChunkClap.OnChat(text);
        eat |= Split.OnChat(text);
        eat |= Suits.OnChat(text);
        eat |= Rings.OnChat(text);
        return eat;
    }

    public void OnLogout()
    {
        Keepers.OnLogout();
        ChunkClap.OnLogout();
        Split.OnLogout();
        Suits.OnLogout();
        Rings.OnLogout();
    }

    // ── UI (render thread) ──────────────────────────────────────────────────

    public void Render()
    {
        if (ImGui.CollapsingHeader("Dispel / Surging Strength", ImGuiTreeNodeFlags.DefaultOpen)) Keepers.Render();
        if (ImGui.CollapsingHeader("Chunking / Clap")) ChunkClap.Render();
        if (ImGui.CollapsingHeader("Split arrows")) Split.Render();
        if (ImGui.CollapsingHeader("Equipment suits")) Suits.Render();
        if (ImGui.CollapsingHeader("Rings (inscription chain)")) Rings.Render();
        if (ImGui.CollapsingHeader("VirindiTank import") && !_ctx.Ai.RenderSection(RynthCore.Plugin.Shared.RynthAiHub.SectionVTankImport))
            ImGui.TextDisabled("The VirindiTank .usd import fills RynthAi's rules: it comes with RynthAi, which isn't loaded.");
        ImGui.TextDisabled("Charms and the Guardian Hand are on the Charms tab.");
    }
}
