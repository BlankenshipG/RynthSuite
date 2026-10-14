// IltGear.cs — ILT Hub "Gear" tab: the gear tools.
// Originally SilentKelpie's ILT Hub (RynthAi IltHub/); moved to RynthILT 2026-10-13.
//
// The sections are separate modules (buff keepers, chunk/clap, split arrows, equipment suits)
// that this tab just lays out, plus RynthAi's VTank .usd importer (it edits RynthAi's own rules,
// so it stayed in RynthAi and is drawn here through RynthAi.Hub RenderSection). The charm strip (registry charms and the Guardian
// Hand, with their toggles) is RynthOracle's Character tab now (IltCharms feeds it through
// "RynthILT.Data", 2026-10-13).
using ImGuiNET;

namespace RynthCore.Plugin.RynthILT.Hub;

internal sealed class IltGear : IIltFeature
{
    public IltBuffKeepers Keepers { get; }
    public IltChunkClap ChunkClap { get; }
    public IltSplitArrows Split { get; }
    public IltEquipSuits Suits { get; }

    private readonly IltHubContext _ctx;

    public IltGear(IltHubContext ctx)
    {
        Keepers = new IltBuffKeepers(ctx);
        ChunkClap = new IltChunkClap(ctx);
        Split = new IltSplitArrows(ctx);
        Suits = new IltEquipSuits(ctx);
        _ctx = ctx;
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
        if (ImGui.CollapsingHeader("VirindiTank import") && !_ctx.Ai.RenderSection(RynthCore.Plugin.Shared.RynthAiHub.SectionVTankImport))
            ImGui.TextDisabled("The VirindiTank .usd import fills RynthAi's rules: it comes with RynthAi, which isn't loaded.");
        ImGui.TextDisabled("Charms and the Guardian Hand: RynthOracle > Character (" + IltSections.OracleCharacterCommand + ").");
    }
}
