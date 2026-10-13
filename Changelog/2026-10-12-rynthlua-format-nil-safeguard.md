# RynthLua: string.format takes nil and other values through %s (2026-10-12)

A safeguard for scripts the player can't edit. A DiscordWebhook script (`index`) stopped with:

    error: index:(32,8-34,0): bad argument #3 to 'format' (string expected, got nil)

Standard Lua 5.2+ passes any value given to `%s` through `tostring`: `nil` becomes `"nil"`,
booleans and tables become text, and `__tostring` is used. MoonSharp, RynthLua's Lua engine,
accepts only strings and numbers there and throws. A script written for standard Lua (a missing
chat-event field, an unset setting) then dies on its first message.

## Change

- `LuaScriptHost` start-up code (`rynth-core`, run before every script) wraps `string.format`.
  For each `%s` whose argument isn't a string or number, it substitutes `tostring(value)`,
  then calls the original. The method form (`("%s"):format(x)`) goes through the same
  function.
- Every other conversion (`%d`, `%x`, `%5.1f`, `%q`, `%%`) and every other argument is passed
  through untouched, so they behave and error exactly as before. Calls with no arguments skip
  the wrapper.

## Tests

`Tools/RynthLua.CompatTests/Scripts/format.lua` (new, 10 checks): nil, booleans, tables and
`__tostring` through `%s`; the method form; and `%d`, `%%`, widths, hex, left-aligned `%s`,
`%q` and no-argument calls unchanged. All 11 compatibility scripts pass, 544 checks.

## Files

- `Plugins/RynthCore.Plugin.RynthLua/LuaScriptHost.cs`
- `Tools/RynthLua.CompatTests/Scripts/format.lua`
## Deployment (local)

- Live RynthLua is `C:\Games\RynthLua\RynthCore.Plugin.RynthLua.dll`, not
  `C:\Games\RynthSuite\RynthLua`, which holds an older copy from 10/04 that nothing loads.
  The new DLL was copied there at about 14:24.
- The replaced DLL is saved as `RynthCore.Plugin.RynthLua.dll.previous` beside it, and in
  `C:\Games\RynthCore\HotBackups\20261012-before-rynthlua-format-fix\RynthLua\`.
- Clients 2256 and 64256 hot-reloaded (gen 4, reload 3 of 3) at 14:22, on a reload signal sent
  before the copy. They still run the old RynthLua and take no more hot reloads; the fix loads
  when each client restarts. Client 15208 isn't injected.