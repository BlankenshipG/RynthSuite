// CastWarningHud.cs — the "Out of components" pop-up: a small window at the top of the screen
// when the server refuses a spell for missing components, saying which spell and what RynthAi
// does instead (a lower level, or another spell it can pay for). OK closes it; it closes by
// itself after ShownForMs, and comes back for the next spell that runs out.
// Post: any thread (the pump's chat handlers). Render: render thread (OnRender / OnRenderOverlay).
using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.Huds;

internal static class CastWarningHud
{
    /// <summary>How long a warning stays up without OK.</summary>
    private const long ShownForMs = 30_000;
    /// <summary>The same spell warns again at most this often (its refusals repeat until it expires).</summary>
    private const long RepeatSameMs = 120_000;

    private static readonly Vector4 ColTitle = new(1.00f, 0.45f, 0.35f, 1f);
    private static readonly Vector4 ColBody = new(0.92f, 0.92f, 0.92f, 1f);
    private static readonly Vector4 ColHint = new(0.70f, 0.75f, 0.80f, 1f);

    private static readonly object Gate = new();
    private static string _spell = "";
    private static string _body = "";
    private static long _shownAt;
    private static bool _dismissed = true;
    private static string _lastSpell = "";
    private static long _lastSpellAt;

    /// <summary>
    /// Shows the warning for <paramref name="spellName"/>: <paramref name="whatNext"/> says what RynthAi
    /// does instead. A repeat for the same spell within RepeatSameMs is ignored. Any thread.
    /// </summary>
    public static void Post(string spellName, string whatNext)
    {
        long now = Environment.TickCount64;
        lock (Gate)
        {
            if (spellName == _lastSpell && now - _lastSpellAt < RepeatSameMs) return;
            _lastSpell = spellName;
            _lastSpellAt = now;
            _spell = spellName;
            _body = whatNext;
            _shownAt = now;
            _dismissed = false;
        }
    }

    /// <summary>The warning window, while one is up. Render thread.</summary>
    public static void Render()
    {
        string spell, body;
        lock (Gate)
        {
            if (_dismissed || _spell.Length == 0) return;
            if (Environment.TickCount64 - _shownAt > ShownForMs) { _dismissed = true; return; }
            spell = _spell;
            body = _body;
        }

        // Top centre, clear of the character and the chat.
        ImGuiViewportPtr vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(new Vector2(vp.WorkPos.X + vp.WorkSize.X * 0.5f, vp.WorkPos.Y + 60f), ImGuiCond.Always, new Vector2(0.5f, 0f));
        ImGui.SetNextWindowBgAlpha(0.85f);
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
                    | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove;
        if (!ImGui.Begin("Out of components##rynthcastwarn", flags)) { ImGui.End(); return; }
        SafeImGui.TextColored(ColTitle, "Out of components: " + spell);
        SafeImGui.TextColored(ColBody, body);
        SafeImGui.TextColored(ColHint, "Restock scarabs and tapers (or Prismatic Tapers) to cast it again.");
        if (ImGui.Button("OK##rynthcastwarn"))
            lock (Gate) _dismissed = true;
        ImGui.End();
    }
}
