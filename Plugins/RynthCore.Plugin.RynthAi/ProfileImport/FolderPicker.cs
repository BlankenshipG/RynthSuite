// FolderPicker.cs — native Windows folder browse (SHBrowseForFolderW).
// Used by the VirindiTank import window so the player can pick Monsters / Loot / Nav / Meta folders.
// Blocks the calling thread until the dialog closes (pump or render).
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace RynthCore.Plugin.RynthAi.ProfileImport;

[SupportedOSPlatform("windows")]
internal static class FolderPicker
{
    /// <summary>
    /// Opens the system folder picker. Returns the chosen path, or null when cancelled / unavailable.
    /// </summary>
    public static string? Pick(string title, string? startingFolder = null)
    {
        IntPtr pidlRoot = IntPtr.Zero;
        try
        {
            if (!string.IsNullOrWhiteSpace(startingFolder) && System.IO.Directory.Exists(startingFolder))
                pidlRoot = ILCreateFromPathW(startingFolder);

            var bi = new BROWSEINFO
            {
                hwndOwner = GetActiveWindow(),
                pidlRoot = pidlRoot,
                pszDisplayName = Marshal.AllocHGlobal(260 * 2),
                lpszTitle = title ?? "Choose folder",
                ulFlags = BifReturnOnlyFsDirs | BifNewDialogStyle | BifEditBox,
            };

            IntPtr pidl = SHBrowseForFolderW(ref bi);
            Marshal.FreeHGlobal(bi.pszDisplayName);
            if (pidl == IntPtr.Zero) return null;

            var sb = new StringBuilder(260);
            bool ok = SHGetPathFromIDListW(pidl, sb);
            Marshal.FreeCoTaskMem(pidl);
            return ok ? sb.ToString() : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (pidlRoot != IntPtr.Zero) Marshal.FreeCoTaskMem(pidlRoot);
        }
    }

    private const uint BifReturnOnlyFsDirs = 0x0001;
    private const uint BifNewDialogStyle = 0x0040;
    private const uint BifEditBox = 0x0010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BROWSEINFO
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolderW(ref BROWSEINFO lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder pszPath);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ILCreateFromPathW([MarshalAs(UnmanagedType.LPWStr)] string pszPath);
}
