using System.Runtime.InteropServices;

namespace LSP.App;

/// <summary>
/// Borderless fullscreen přes Win32 — kanonický postup (Raymond Chen):
/// vstup = ulož WINDOWPLACEMENT + styl, sundej WS_OVERLAPPEDWINDOW a natáhni okno
/// na rcMonitor aktuálního monitoru; výstup = vrať styl a SetWindowPlacement
/// (obnoví jak normální rozměry, tak maximalizaci).
/// Photino SetFullScreen/SetMaximized nepoužíváme: SetFullScreen sizuje podle
/// primárního monitoru bez SWP_FRAMECHANGED (černé bary při DPI škálování)
/// a SetMaximized rozbíjí restore z maximalizovaného okna.
/// </summary>
internal static class NativeFullscreen
{
    private const int GWL_STYLE = -16;
    private const long WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004,
        SWP_FRAMECHANGED = 0x0020, SWP_NOOWNERZORDER = 0x0200;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO info);

    // ponytail: statický stav — app má jediné okno, párování Enter/Exit hlídá volající (isFullScreen).
    private static IntPtr _savedStyle;
    private static WINDOWPLACEMENT _savedPlacement;

    public static void Enter(IntPtr hWnd)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST), ref mi))
            return; // bez rozměrů monitoru nemá cenu sundávat rám

        _savedStyle = GetWindowLongPtrW(hWnd, GWL_STYLE);
        _savedPlacement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        GetWindowPlacement(hWnd, ref _savedPlacement);

        SetWindowLongPtrW(hWnd, GWL_STYLE, (IntPtr)((long)_savedStyle & ~WS_OVERLAPPEDWINDOW));
        SetWindowPos(hWnd, IntPtr.Zero, // HWND_TOP
            mi.rcMonitor.Left, mi.rcMonitor.Top,
            mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top,
            SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
    }

    public static void Exit(IntPtr hWnd)
    {
        SetWindowLongPtrW(hWnd, GWL_STYLE, _savedStyle);
        SetWindowPlacement(hWnd, ref _savedPlacement);
        SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
    }
}
