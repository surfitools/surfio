using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Surfio.Services;

static class Native
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint flags);

    const uint ES_CONTINUOUS = 0x80000000;
    const uint ES_SYSTEM_REQUIRED = 0x00000001;
    const uint ES_DISPLAY_REQUIRED = 0x00000002;

    /// <summary>Dark title bar to match the player (Windows 10 20H1+ / 11).</summary>
    public static void UseDarkTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        int on = 1;
        if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int)); // older Windows 10 builds
    }

    /// <summary>Stop the screen from sleeping while a video plays.</summary>
    public static void KeepAwake(bool on) =>
        SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED : ES_CONTINUOUS);
}
