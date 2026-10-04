using System;
using System.IO;
using System.Runtime.InteropServices;
using ImGuiNET;
using RynthCore.Install;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

/// <summary>
/// A standalone tool (Loot Editor, Monster Editor) launched from inside the game: start, track, focus and close
/// it without leaving AC.
///
/// Uses raw Win32 (ShellExecuteExW with SEE_MASK_NOCLOSEPROCESS, WaitForSingleObject, WM_CLOSE) instead of
/// System.Diagnostics.Process: Process's handle-touching accessors silently AV this host under NativeAOT in the
/// injected x86 acclient.exe (deep-audit finding #10, 2026-06-18). All members run on the ImGui/game thread.
/// </summary>
internal sealed class ExternalTool
{
    private readonly string _displayName;
    private IntPtr _processHandle = IntPtr.Zero;
    private int _pid;

    public ExternalTool(string displayName) => _displayName = displayName;

    /// <summary>Full path of the Loot Editor in the installed RynthCore folder (&lt;CoreDir&gt;\Tools\LootEditor).</summary>
    public static string LootEditorExe =>
        Path.Combine(RynthInstallPaths.CoreDir, "Tools", "LootEditor", "RynthCore.LootEditor.exe");

    /// <summary>Full path of the Monster Editor in the installed RynthSuite folder (&lt;SuiteDir&gt;\RynthAi\MonsterEditor).</summary>
    public static string MonsterEditorExe =>
        Path.Combine(RynthInstallPaths.RynthAiDir, "MonsterEditor", "RynthCore.MonsterEditor.exe");

    /// <summary>True while the process this instance started is still alive (releases the handle once it exits).</summary>
    public bool IsRunning
    {
        get
        {
            if (_processHandle == IntPtr.Zero) return false;
            if (WaitForSingleObject(_processHandle, 0) == WaitTimeout) return true;
            Release();
            return false;
        }
    }

    /// <summary>
    /// Brings the tool to the front if it is already running, otherwise starts <paramref name="exePath"/> with
    /// <paramref name="arguments"/>. Returns a short user-facing message on failure, or null on success.
    /// </summary>
    public string? OpenOrFocus(string exePath, string? arguments)
    {
        if (IsRunning)
        {
            IntPtr hwnd = FindMainWindowForPid(_pid);
            if (hwnd != IntPtr.Zero)
            {
                if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
                SetForegroundWindow(hwnd);
            }
            return null;
        }
        return Start(exePath, arguments);
    }

    /// <summary>Starts the tool (without checking whether it already runs). Null on success, else a message.</summary>
    public string? Start(string exePath, string? arguments)
    {
        if (!File.Exists(exePath))
            return $"{_displayName} not found: {exePath} (re-run the RynthCore installer to add it).";

        Release();
        var info = new SHELLEXECUTEINFOW
        {
            cbSize       = Marshal.SizeOf<SHELLEXECUTEINFOW>(),
            fMask        = SeeMaskNoCloseProcess,   // keep hProcess so we can track / focus / close it
            lpVerb       = "open",
            lpFile       = exePath,
            lpParameters = arguments,
            lpDirectory  = Path.GetDirectoryName(exePath),
            nShow        = SwShowNormal,
        };
        if (!ShellExecuteExW(ref info) || info.hProcess == IntPtr.Zero)
            return $"Failed to launch {_displayName} (Win32 error {Marshal.GetLastWin32Error()}).";

        _processHandle = info.hProcess;
        _pid = (int)GetProcessId(info.hProcess);
        return null;
    }

    /// <summary>Asks the tool to close (WM_CLOSE to its main window; hard kill if it has none) and releases the handle.</summary>
    public void Close()
    {
        if (IsRunning)
        {
            IntPtr hwnd = FindMainWindowForPid(_pid);
            if (hwnd != IntPtr.Zero)
                PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
            else
                TerminateProcess(_processHandle, 0);
        }
        Release();
    }

    /// <summary>Closes our process handle without touching the tool (it keeps running). Call on plugin shutdown.</summary>
    public void Release()
    {
        if (_processHandle != IntPtr.Zero)
        {
            CloseHandle(_processHandle);
            _processHandle = IntPtr.Zero;
            _pid = 0;
        }
    }

    /// <summary>
    /// Draws the "Tools" row used on the Items window and the Looting settings page: one button per tool.
    /// <paramref name="idSuffix"/> keeps the ImGui IDs unique per window.
    /// </summary>
    public static void DrawToolButtons(string idSuffix, Action? openLootEditor, Action? openMonsterEditor)
    {
        if (openLootEditor == null && openMonsterEditor == null) return;

        ImGui.TextColored(LegacyDashboardRenderer.ColAmber, "Tools");
        ImGui.SameLine();
        if (openLootEditor != null)
        {
            if (ImGui.Button($"Loot Editor##tools{idSuffix}"))
                openLootEditor();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Open the Loot Editor with the current loot profile (focuses it if already open).");
            ImGui.SameLine();
        }
        if (openMonsterEditor != null)
        {
            if (ImGui.Button($"Monster Editor##tools{idSuffix}"))
                openMonsterEditor();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Open the Monster Editor for this character (focuses it if already open).");
        }
        ImGui.Separator();
        ImGui.Spacing();
    }

    // ── Win32 ────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFOW
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }

    private const uint SeeMaskNoCloseProcess = 0x00000040;
    private const int SwShowNormal = 1;
    private const int SwRestore = 9;
    private const uint WaitTimeout = 0x00000102;
    private const uint WmClose = 0x0010;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteExW(ref SHELLEXECUTEINFOW lpExecInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(IntPtr hProcess);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>First visible top-level window owned by <paramref name="pid"/> (what Process.MainWindowHandle finds).</summary>
    private static IntPtr FindMainWindowForPid(int pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint wndPid);
            if (wndPid == (uint)pid && IsWindowVisible(hWnd))
            {
                found = hWnd;
                return false; // stop enumerating
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
