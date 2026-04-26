# Changelog — RynthAi: ImGui Monsters table assert (2026-04-26)

- **RynthCore.Plugin.RynthAi** **0.6.6**
- **Fix:** `LegacyMonstersUi` table columns used `ImGuiTableColumnFlags.NoResize` with a pixel width. ImGui requires an explicit sizing policy when `init_width_or_weight` is non-zero (`imgui_tables.cpp` assert). Toggle and **Del** columns now use **`WidthFixed | NoResize`**.
- **Symptom:** “ImGui Assertion Failed” when opening RynthAi UI (e.g. bar **RynthAi** button) — unrelated to Decal running or not; the Monsters window table setup ran on first UI use.
