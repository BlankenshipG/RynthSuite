# Server plugins: the server card draws through the engine's ImGui (2026-10-20)

All 16 server plugins: patch versions bumped (RynthDreamWeave 0.1.5 -> 0.1.6, the others
0.1.4 -> 0.1.5). Built with the engine fix in RynthCore 2026.10.20.4.

## Why

SK's client on DreamWeave died when the server card was opened in a popped-out Mini Remote
(2026-10-19 20:15 and 20:19). The server card drew with a second copy of ImGui (its own
`cimgui.dll`), whose current context is the main window's. A popped-out window draws in a context
of its own, so the card's first ImGui call faulted outside a frame, and the runtime aborted the
client. Details: RynthCore `Changelog/2026-10-20-popout-plugin-imgui-context-crash-2026.10.20.4.md`.

## Changed

- `Shared/ServerPack/ServerPackPlugin.cs`: `Initialize` calls `ImGuiNativeBinding.Ensure`, as
  RynthAi and RynthILT do. The card's ImGui.NET then binds to the engine's `RynthCore.cimgui.dll`
  and draws in the engine's current context, a pop-out's included.
- `Shared/ServerPack/ServerPlugin.props` compiles `Shared/ImGuiNativeBinding.cs` into every server
  plugin.

The installed server plugins (rynth's 2026.10.19.1 managed builds) are covered by the engine fix
alone: the engine now binds every managed plugin's ImGui.NET to its own module.
