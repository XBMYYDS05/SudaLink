using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SudaLink;

/// <summary>窗口外观微调：交给 DWM 处理圆角，保证与系统原生视觉一致、文字不被降质。</summary>
internal static class WindowFx
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int CornerDoNotRound = 1;
    private const int CornerRound = 2;

    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) => Apply(window);
        window.StateChanged += (_, _) => Apply(window);
    }

    private static void Apply(Window window)
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
            // 老系统没有该接口，保持方形窗口即可
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
