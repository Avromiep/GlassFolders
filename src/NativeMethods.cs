using System.Runtime.InteropServices;

namespace GlassFolders;

/// <summary>
/// P/Invoke surface. Kept in one place so the shell/COM quirks are contained.
/// </summary>
internal static class NativeMethods
{
    // ---- IShellItemImageFactory (high-quality, alpha-correct icon extraction) ----

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE
    {
        public int cx;
        public int cy;
        public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
    }

    [Flags]
    internal enum SIIGBF
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        MemoryOnly = 0x02,
        IconOnly = 0x04,
        ThumbnailOnly = 0x08,
        InCacheOnly = 0x10,
        ScaleUp = 0x100,
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    // riid for IShellItemImageFactory used with SHCreateItemFromParsingName
    internal static Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    internal static extern void SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    // ---- GDI to read the DIB bits produced by GetImage (preserves alpha) ----

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public uint dsBitfield0;
        public uint dsBitfield1;
        public uint dsBitfield2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [DllImport("gdi32.dll")]
    internal static extern int GetObject(IntPtr hgdiobj, int cbBuffer, ref DIBSECTION lpvObject);

    [DllImport("gdi32.dll")]
    internal static extern int GetObject(IntPtr hgdiobj, int cbBuffer, ref BITMAP lpvObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr hObject);

    // ---- Shell change notification (refresh desktop icon live) ----

    internal const int SHCNE_ASSOCCHANGED = 0x08000000;
    internal const int SHCNE_UPDATEITEM = 0x00002000;
    internal const int SHCNE_UPDATEIMAGE = 0x00008000;
    internal const uint SHCNF_IDLIST = 0x0000;
    internal const uint SHCNF_PATHW = 0x0005;
    internal const uint SHCNF_FLUSH = 0x1000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    // ---- Acrylic / blur-behind for the expanded glass panel ----

    internal enum AccentState
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_GRADIENT = 1,
        ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
        ACCENT_ENABLE_BLURBEHIND = 3,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
        ACCENT_ENABLE_HOSTBACKDROP = 5,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AccentPolicy
    {
        public AccentState AccentState;
        public uint AccentFlags;
        public uint GradientColor; // AABBGGRR
        public uint AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    internal const int WCA_ACCENT_POLICY = 19;

    [DllImport("user32.dll")]
    internal static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    // ---- DWM window backdrop (Win11) for a real acrylic/frosted panel ----

    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    // DWM_WINDOW_CORNER_PREFERENCE
    internal const int DWMWCP_ROUND = 2;

    // DWM_SYSTEMBACKDROP_TYPE
    internal const int DWMSBT_MAINWINDOW = 2;      // Mica
    internal const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic (frosted)
    internal const int DWMSBT_TABBEDWINDOW = 4;    // Tabbed Mica

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    // ---- Window rect (for screenshotting the composited glass over the wallpaper) ----

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    // ---- Rounded window region (clips the acrylic blur to rounded corners, no box) ----

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

    [DllImport("user32.dll")]
    internal static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    // ---- Low-level global mouse hook (single-click-to-open for our folder icons) ----

    internal const int WH_MOUSE_LL = 14;
    internal const int WM_LBUTTONDOWN = 0x0201;
    internal const int WM_LBUTTONUP = 0x0202;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    internal delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr GetModuleHandle(string? lpModuleName);

    // ---- Current desktop wallpaper (for the frostiness preview) ----

    internal const uint SPI_GETDESKWALLPAPER = 0x0073;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SystemParametersInfo(uint uiAction, uint uiParam,
        System.Text.StringBuilder pvParam, uint fWinIni);
}
