using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SudaLink;

/// <summary>窗口外观：DWM 圆角 + 亚克力背景（液态玻璃的“底”）。</summary>
internal static class WindowFx
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int CornerDoNotRound = 1;
    private const int CornerRound = 2;

    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            ApplyRounding(window);
        };
        window.StateChanged += (_, _) => ApplyRounding(window);
    }

    private static void ApplyRounding(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        var preference = window.WindowState == WindowState.Maximized ? CornerDoNotRound : CornerRound;
        try
        {
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
