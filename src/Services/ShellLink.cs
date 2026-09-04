using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace GlassFolders.Services;

/// <summary>
/// Thin IShellLinkW/IPersistFile wrapper to create and read .lnk shortcuts without
/// pulling in the Windows Script Host COM library.
/// </summary>
public static class ShellLink
{
    public static void Create(
        string lnkPath,
        string targetPath,
        string? arguments = null,
        string? iconPath = null,
        int iconIndex = 0,
        string? description = null,
        string? workingDirectory = null,
        string? appUserModelId = null)
    {
        var link = (IShellLinkW)new CShellLink();
        link.SetPath(targetPath);
        if (arguments != null) link.SetArguments(arguments);
        if (description != null) link.SetDescription(description);
        if (workingDirectory != null) link.SetWorkingDirectory(workingDirectory);
        if (iconPath != null) link.SetIconLocation(iconPath, iconIndex);

        // A distinct AppUserModelID makes the taskbar/Start treat this shortcut as its own app,
        // so clicking a pinned copy LAUNCHES it (which opens the folder) instead of trying to
        // focus our windowless tray process — the reason a pinned folder used to do nothing.
        if (appUserModelId != null)
        {
            var store = (IPropertyStore)link;
            var key = PKEY_AppUserModel_ID;
            // Build a VT_LPWSTR PROPVARIANT by hand; PropVariantClear frees the string for us.
            var pv = new PROPVARIANT { vt = VT_LPWSTR, p = Marshal.StringToCoTaskMemUni(appUserModelId) };
            try { store.SetValue(ref key, ref pv); store.Commit(); }
            finally { PropVariantClear(ref pv); }
        }

        var file = (IPersistFile)link;
        file.Save(lnkPath, true);

        Marshal.ReleaseComObject(file);
        Marshal.ReleaseComObject(link);
    }

    /// <summary>Reads back the AppUserModelID set on a .lnk (null if none). Used to verify writes.</summary>
    public static string? ReadAppUserModelId(string lnkPath)
    {
        try
        {
            var link = (IShellLinkW)new CShellLink();
            var file = (IPersistFile)link;
            file.Load(lnkPath, 0);
            var store = (IPropertyStore)link;
            var key = PKEY_AppUserModel_ID;
            store.GetValue(ref key, out var pv);
            string? result = pv.vt == VT_LPWSTR && pv.p != IntPtr.Zero
                ? Marshal.PtrToStringUni(pv.p) : null;
            PropVariantClear(ref pv);
            Marshal.ReleaseComObject(file);
            Marshal.ReleaseComObject(link);
            return result;
        }
        catch { return null; }
    }

    /// <summary>Reads a .lnk's command-line arguments (null/empty if none).</summary>
    public static string? ReadArguments(string lnkPath)
    {
        try
        {
            var link = (IShellLinkW)new CShellLink();
            var file = (IPersistFile)link;
            file.Load(lnkPath, 0);
            var sb = new System.Text.StringBuilder(1024);
            link.GetArguments(sb, sb.Capacity);
            Marshal.ReleaseComObject(file);
            Marshal.ReleaseComObject(link);
            var s = sb.ToString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch { return null; }
    }

    /// <summary>Resolves the target path a .lnk points at (best effort).</summary>
    public static string? ResolveTarget(string lnkPath)
    {
        try
        {
            var link = (IShellLinkW)new CShellLink();
            var file = (IPersistFile)link;
            file.Load(lnkPath, 0);
            var sb = new System.Text.StringBuilder(260);
            var data = new WIN32_FIND_DATAW();
            link.GetPath(sb, sb.Capacity, ref data, 0);
            Marshal.ReleaseComObject(file);
            Marshal.ReleaseComObject(link);
            var result = sb.ToString();
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        catch
        {
            return null;
        }
    }

    // ---- AppUserModelID (taskbar/Start identity) plumbing ----

    // PKEY_AppUserModel_ID = {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, pid 5.
    private static PROPERTYKEY PKEY_AppUserModel_ID => new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5,
    };

    private const ushort VT_LPWSTR = 31;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    // Opaque PROPVARIANT (x64 layout: 8 bytes header + two pointer-sized slots for the union).
    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT { public ushort vt; public ushort w1, w2, w3; public IntPtr p; public IntPtr p2; }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        void Commit();
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile,
            int cch, ref WIN32_FIND_DATAW pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath,
            int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }
}
