# RynthAi 0.6.19 — ILT Hub draw diagnostics; built against API v69

Previous release: 0.6.18 (per-category log levels).

## Crash fix (via the SDK)

* Built against plugin API v69. AutoStack only polls `GetMergeStackResult` on v69+
  engines; on 2026.10.4.12/.13 engines (whose v68 thunk corrupted RynthAi's stack
  and crashed the client) it falls back to the 10 s grace check.

## ILT Hub "won't show" diagnostics

* `OnRenderOverlay` logs once when it first draws
  (`[Overlay] OnRenderOverlay drawing (iltHub=ready|null)`) and once per reason
  when it skips (`not initialized`, `login not complete`, `no ImGui context`).
* The Hub window logs its geometry on the first frame of every show:
  `[IltHub] window drawn: expanded=… collapsed=… pos=(x,y) size=(w,h) display=(w,h) onScreen=…`.
* If a saved position leaves the window with less than 40 px on screen, it is
  moved to (40,40) and `[IltHub] window was off-screen - moved to (40,40).` is logged.
