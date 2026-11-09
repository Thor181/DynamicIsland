using System.Runtime.InteropServices;

namespace DynamicIsland.Interop;

internal static class NativeMethods
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_APPWINDOW = 0x00040000;

    private static readonly nint HWND_TOPMOST = -1;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    // QUERY_USER_NOTIFICATION_STATE
    private const int QUNS_BUSY = 2;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int QUNS_PRESENTATION_MODE = 4;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    /// <summary>Hides the window from Alt+Tab/taskbar and prevents it from stealing focus.</summary>
    public static void MakeToolWindow(nint hwnd)
    {
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        style |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        style &= ~WS_EX_APPWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)style);
    }

    public static void EnsureTopmost(nint hwnd)
    {
        if (hwnd != 0)
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Moves and resizes in a single call (physical pixels), avoiding a frame where only one changed.</summary>
    public static void SetBounds(nint hwnd, int x, int y, int width, int height)
    {
        const uint SWP_NOZORDER = 0x0004;
        if (hwnd != 0)
            SetWindowPos(hwnd, 0, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>True when a fullscreen app (game, video, presentation) is in the foreground.</summary>
    public static bool IsFullscreenAppRunning()
    {
        try
        {
            if (SHQueryUserNotificationState(out int state) != 0) return false;
            return state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;
        }
        catch
        {
            return false;
        }
    }
}
