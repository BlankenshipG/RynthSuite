using System;
using System.IO;
using System.Runtime.InteropServices;

namespace RynthCore.Install;

/// <summary>
/// Resolves where RynthCore and RynthSuite are installed. The installer lets the user pick
/// both folders and records them under <c>Software\Rynth</c> (HKCU for a per-user install,
/// HKLM for an all-users install), so nothing may assume <c>C:\Games\...</c> any more.
/// <para>
/// Resolution order per folder: environment override (<c>RYNTHCORE_DIR</c> /
/// <c>RYNTHSUITE_DIR</c>, for dev setups) → HKCU → HKLM (32-bit view, where the 32-bit
/// installer writes) → the historical default under <c>C:\Games</c>. Results are cached
/// for the life of the process.
/// </para>
/// Linked into the RynthAi plugin and the Loot / Monster editors. RynthCore keeps an identical copy in
/// <c>src/RynthCore.App/RynthInstallPaths.cs</c> (engine, launcher, injector) — change both together.
/// Registry access is a direct RegGetValueW P/Invoke so it works under NativeAOT and trimming
/// without pulling in Microsoft.Win32.Registry.
/// </summary>
internal static class RynthInstallPaths
{
    /// <summary>Default RynthCore folder (pre-2026.10.4.2 installs always used this).</summary>
    public const string DefaultCoreDir = @"C:\Games\RynthCore";

    /// <summary>Default RynthSuite folder (pre-2026.10.4.2 installs always used this).</summary>
    public const string DefaultSuiteDir = @"C:\Games\RynthSuite";

    /// <summary>Registry key (under HKCU or HKLM) written by the installer.</summary>
    public const string RegistryKey = @"Software\Rynth";

    private static string? _coreDir;
    private static string? _suiteDir;

    /// <summary>RynthCore install folder (launcher, <c>Runtime\</c>, <c>Logs\</c>, <c>Tools\</c>).</summary>
    public static string CoreDir => _coreDir ??= Resolve("RYNTHCORE_DIR", "CoreDir", DefaultCoreDir);

    /// <summary>RynthSuite folder (plugins and their data, e.g. <c>RynthAi\</c>).</summary>
    public static string SuiteDir => _suiteDir ??= Resolve("RYNTHSUITE_DIR", "SuiteDir", DefaultSuiteDir);

    /// <summary>Unified RynthCore log folder (<c>&lt;CoreDir&gt;\Logs</c>).</summary>
    public static string CoreLogsDir => Path.Combine(CoreDir, "Logs");

    /// <summary>RynthAi plugin home and data root (<c>&lt;SuiteDir&gt;\RynthAi</c>).</summary>
    public static string RynthAiDir => Path.Combine(SuiteDir, "RynthAi");

    /// <summary>Reads a string value from <c>Software\Rynth</c> (HKCU first, then HKLM 32-bit view); null when absent.</summary>
    public static string? ReadSetting(string valueName)
        => ReadRegistryString(HKEY_CURRENT_USER, valueName, 0)
           ?? ReadRegistryString(HKEY_LOCAL_MACHINE, valueName, RRF_SUBKEY_WOW6432KEY);

    /// <summary>Deletes a value from HKCU <c>Software\Rynth</c> (used for one-shot installer hand-offs). Best effort.</summary>
    public static void DeleteUserSetting(string valueName)
    {
        try { RegDeleteKeyValueW(HKEY_CURRENT_USER, RegistryKey, valueName); } catch { }
    }

    private static string Resolve(string envVar, string valueName, string fallback)
    {
        string? fromEnv = null;
        try { fromEnv = Environment.GetEnvironmentVariable(envVar); } catch { }

        foreach (string? candidate in new[] { fromEnv, ReadSetting(valueName) })
        {
            string? normalized = Normalize(candidate);
            if (normalized != null) return normalized;
        }
        return fallback;
    }

    /// <summary>Accepts only absolute paths; trims whitespace, quotes and trailing separators.</summary>
    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string p = path.Trim().Trim('"').TrimEnd('\\', '/');
        if (p.Length == 2 && p[1] == ':') p += "\\"; // keep "D:" as the drive root "D:\"
        try
        {
            if (!Path.IsPathFullyQualified(p)) return null;
            string full = Path.GetFullPath(p);
            return full.Length > 3 ? full.TrimEnd('\\') : full; // "D:\" stays a root
        }
        catch
        {
            return null;
        }
    }

    // ── Win32 registry (advapi32) ───────────────────────────────────────────

    private static readonly IntPtr HKEY_CURRENT_USER = new(unchecked((int)0x80000001));
    private static readonly IntPtr HKEY_LOCAL_MACHINE = new(unchecked((int)0x80000002));
    private const uint RRF_RT_REG_SZ = 0x00000002;
    private const uint RRF_RT_REG_EXPAND_SZ = 0x00000004;
    private const uint RRF_SUBKEY_WOW6432KEY = 0x00020000;
    private const int ERROR_SUCCESS = 0;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegGetValueW(IntPtr hkey, string lpSubKey, string lpValue, uint dwFlags,
                                           IntPtr pdwType, char[]? pvData, ref uint pcbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegDeleteKeyValueW(IntPtr hkey, string lpSubKey, string lpValueName);

    private static string? ReadRegistryString(IntPtr root, string valueName, uint viewFlags)
    {
        try
        {
            uint flags = RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ | viewFlags;
            uint bytes = 0;
            if (RegGetValueW(root, RegistryKey, valueName, flags, IntPtr.Zero, null, ref bytes) != ERROR_SUCCESS || bytes < 2)
                return null;

            var buffer = new char[(bytes + 1) / 2];
            if (RegGetValueW(root, RegistryKey, valueName, flags, IntPtr.Zero, buffer, ref bytes) != ERROR_SUCCESS)
                return null;

            int len = Array.IndexOf(buffer, '\0');
            return new string(buffer, 0, len >= 0 ? len : buffer.Length);
        }
        catch
        {
            // Missing advapi32 entry point or access denied — fall back to the defaults.
            return null;
        }
    }
}
