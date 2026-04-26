# Changelog — RynthAi: Suite tool paths and IL3000 (2026-04-24)

- **RynthCore.Plugin.RynthAi** **0.6.4**
- **Suite tool resolution:** `SuiteToolPaths.FindPublishedTool` walks from the **plugin assembly’s on-disk path** first (Rynth `Runtime\Plugins\…` or shadow), then from **`AppContext.BaseDirectory`**. The first path is required when the engine is injected into `acclient` (the process base is the game folder, not the Rynth install). **IL3000** for `Assembly.Location` is handled with `[UnconditionalSuppressMessage("SingleFile", "IL3000:…")]` on a small helper that returns the plugin’s directory, because this project is a **NativeAOT `.dll`**, not an assembly embedded in a single-file host.
