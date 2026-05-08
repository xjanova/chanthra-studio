using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ChanthraStudio.Helpers;

/// <summary>
/// Win32 helpers for the frameless main window: drag-to-move, system menu on
/// alt-space, and proper maximize bounds (so the window doesn't overflow the
/// taskbar when WindowState = Maximized).
/// </summary>
public static class WindowChromeHelper
{
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_RESTORE = 0xF120;
    private const int SC_MAXIMIZE = 0xF030;

    public static void HookMaxBounds(Window window)
    {
        var hwndSource = (HwndSource?)PresentationSource.FromVisual(window);
        if (hwndSource is null)
        {
            window.SourceInitialized += (_, _) =>
            {
                var src = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
                src?.AddHook(WndProc);
            };
        }
        else
        {
            hwndSource.AddHook(WndProc);
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            WmGetMinMaxInfo(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorOptions.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return;
        var info = new MONITORINFO();
        if (!GetMonitorInfo(monitor, info)) return;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var rcWork = info.rcWork;
        var rcMonitor = info.rcMonitor;
        mmi.ptMaxPosition.X = Math.Abs(rcWork.left - rcMonitor.left);
        mmi.ptMaxPosition.Y = Math.Abs(rcWork.top - rcMonitor.top);
        mmi.ptMaxSize.X = Math.Abs(rcWork.right - rcWork.left);
        mmi.ptMaxSize.Y = Math.Abs(rcWork.bottom - rcWork.top);
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    private enum MonitorOptions : uint
    {
        MONITOR_DEFAULTTONULL = 0,
        MONITOR_DEFAULTTOPRIMARY = 1,
        MONITOR_DEFAULTTONEAREST = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MONITORINFO
    {
        public int cbSize = Marshal.SizeOf(typeof(MONITORINFO));
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, MonitorOptions flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, [In, Out] MONITORINFO lpmi);
}
