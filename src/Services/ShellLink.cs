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
        string? workingDirectory = null)
    {
        var link = (IShellLinkW)new CShellLink();
        link.SetPath(targetPath);
        if (arguments != null) link.SetArguments(arguments);
        if (description != null) link.SetDescription(description);
        if (workingDirectory != null) link.SetWorkingDirectory(workingDirectory);
        if (iconPath != null) link.SetIconLocation(iconPath, iconIndex);

        var file = (IPersistFile)link;
        file.Save(lnkPath, true);

        Marshal.ReleaseComObject(file);
        Marshal.ReleaseComObject(link);
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
