// ImGuiNativeBinding.cs - Binds ImGui.NET's "cimgui" P/Invokes to the cimgui module the
// engine already loaded.
//
// The engine ships its own build as RynthCore.cimgui.dll. Without this resolver, Windows
// resolves "cimgui" to a separate cimgui.dll: a second ImGui instance whose windows (ILT Hub,
// Pets, Quests, Mini Remote, Item HUD) are built but never reach the engine's draw data.
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using ImGuiNET;

namespace RynthCore.Plugin.RynthAi;

internal static class ImGuiNativeBinding
{
    private const string EngineModule = "RynthCore.cimgui.dll";
    private const string LegacyModule = "cimgui.dll";

    private static int _configured;   // 1 once the resolver is registered (survives DLL-copy reuse)
    private static int _resolvedLogged;

    /// <summary>
    /// Registers the resolver. Call before the first ImGui call (Initialize). The resolver can be
    /// set only once per assembly, so later calls (a reused plugin DLL copy) are no-ops.
    /// </summary>
    public static void Ensure()
    {
        if (Interlocked.Exchange(ref _configured, 1) != 0) return;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(ImGui).Assembly, Resolve);
            RynthLog.Write(LogCat.UI, "[ImGui] native resolver registered (prefers the engine's " + EngineModule + ").");
        }
        catch (InvalidOperationException)
        {
            // Already registered by an earlier instance in this process.
        }
        catch (Exception ex)
        {
            RynthLog.Exception(LogCat.UI, ex, "ImGuiNativeBinding.Ensure");
        }
    }

    /// <summary>Runs on the first cimgui P/Invoke: the engine's module, else whatever cimgui.dll is loaded.</summary>
    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "cimgui", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(libraryName, LegacyModule, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        string bound = EngineModule;
        IntPtr module = GetModuleHandleA(EngineModule);
        if (module == IntPtr.Zero)
        {
            bound = LegacyModule;
            module = GetModuleHandleA(LegacyModule);
        }

        if (Interlocked.Exchange(ref _resolvedLogged, 1) == 0)
            RynthLog.Write(LogCat.UI, module != IntPtr.Zero
                ? $"[ImGui] cimgui bound to loaded {bound} (0x{module.ToInt64():X})."
                : "[ImGui] no loaded cimgui module found; falling back to the default DLL search.");
        return module; // IntPtr.Zero = default resolution
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleA(string lpModuleName);
}
