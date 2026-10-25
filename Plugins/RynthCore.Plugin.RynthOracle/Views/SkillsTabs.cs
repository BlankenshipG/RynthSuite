// ============================================================================
//  RynthCore.Plugin.RynthOracle - Views/SkillsTabs.cs
//  Skills / Attributes / Progress tabs: the engine draws SkillsFace into this
//  window after the Oracle tab strip (ScriptWindowFace + OracleSkillsEmbed).
//  These Draw methods only reserve the page and document the hand-off.
// ============================================================================

using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

internal sealed partial class OracleView
{
    /// <summary>Skills: raise / train (was the standalone Skills panel).</summary>
    private void DrawSkillsEmbed(UiWindow w)
    {
        // Placeholder only: ScriptWindowFace draws SkillsFace into the remaining space.
        w.Dummy(0f, 1f);
    }

    /// <summary>Attributes & vitals: same engine body, attributes tab.</summary>
    private void DrawAttributesEmbed(UiWindow w) => w.Dummy(0f, 1f);

    /// <summary>Progress: ILT progression planners (was Skills → Progression).</summary>
    private void DrawProgressEmbed(UiWindow w) => w.Dummy(0f, 1f);
}
