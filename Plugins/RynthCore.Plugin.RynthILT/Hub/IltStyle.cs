// IltStyle.cs - The colours the ILT tabs and panels draw with (RynthAi's dashboard palette, so
// the tabs look the same inside RynthAi's Mini Remote as when they lived there), the dashboard
// style for RynthILT's own windows, and a JSON string escaper for the hand-built exports.
using System.Numerics;
using ImGuiNET;

namespace RynthCore.Plugin.RynthILT.Hub;

internal static class IltStyle
{
    internal static readonly Vector4 ColTeal = new(0.15f, 0.85f, 0.90f, 1.00f);
    internal static readonly Vector4 ColAmber = new(0.91f, 0.70f, 0.20f, 1.00f);
    internal static readonly Vector4 ColGreen = new(0.25f, 0.85f, 0.45f, 1.00f);
    internal static readonly Vector4 ColTextMute = new(0.55f, 0.65f, 0.75f, 1.00f);
    internal static readonly Vector4 ColHp = new(0.85f, 0.20f, 0.20f, 1.00f);

    /// <summary>
    /// RynthAi's dashboard colours for a window of RynthILT's own (Games HUD, confirmations, the
    /// Guardian panel). Returns how many colours to pop. Inside RynthAi's Mini Remote the style
    /// is already pushed.
    /// </summary>
    internal static int PushDashboardStyle()
    {
        ImGui.PushStyleColor(ImGuiCol.FrameBg,          new Vector4(0.18f, 0.22f, 0.28f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered,   new Vector4(0.24f, 0.30f, 0.38f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive,    new Vector4(0.30f, 0.38f, 0.48f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.CheckMark,        new Vector4(0.85f, 0.90f, 1.00f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.PopupBg,          new Vector4(0.10f, 0.14f, 0.20f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.Header,           new Vector4(0.20f, 0.28f, 0.36f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered,    new Vector4(0.26f, 0.36f, 0.46f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive,     new Vector4(0.32f, 0.44f, 0.58f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.TitleBg,          new Vector4(0.14f, 0.18f, 0.24f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive,    new Vector4(0.22f, 0.30f, 0.40f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed, new Vector4(0.10f, 0.14f, 0.20f, 0.85f));
        return 11;
    }

    /// <summary>Minimal JSON string escaper for hand-built export JSON.</summary>
    internal static string JsonEscape(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
