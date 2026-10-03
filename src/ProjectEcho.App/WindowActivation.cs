using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ProjectEcho.App;

internal static class WindowActivation
{
    internal static bool Restore(Window window)
    {
        if (!window.IsVisible) window.Show();
        nint handle = new WindowInteropHelper(window).Handle;
        if (window.WindowState == WindowState.Minimized)
        {
            // SW_RESTORE also preserves a window that was maximized before minimization.
            if (handle != 0) ShowWindow(handle, 9);
            else window.WindowState = WindowState.Normal;
        }
        bool activated = window.Activate();
        if (handle != 0) activated = SetForegroundWindow(handle) || activated;
        return activated;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
}
