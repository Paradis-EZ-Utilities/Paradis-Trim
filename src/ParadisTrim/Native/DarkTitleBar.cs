using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ParadisTrim.Native;

/// <summary>
/// Asks Windows to draw a window's title bar dark, so it matches the dark window below it.
/// Supported on Windows 10 (20H1 and later) and Windows 11; elsewhere this quietly does nothing.
/// </summary>
internal static class DarkTitleBar
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeOld = 19; // Windows 10 builds before 20H1

    public static void Apply(Window window)
    {
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Set(window);
        else window.SourceInitialized += (_, _) => Set(window);
    }

    private static void Set(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            var on = 1;
            if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref on, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(handle, DwmUseImmersiveDarkModeOld, ref on, sizeof(int));
            }
        }
        catch (Exception)
        {
            // Purely cosmetic: an older Windows (or a missing dwmapi) just keeps its normal title bar.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
