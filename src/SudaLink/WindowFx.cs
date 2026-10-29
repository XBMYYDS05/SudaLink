using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SudaLink;

/// <summary>窗口外观：DWM 圆角、边框、阴影由我们自己控制。</summary>
internal static class WindowFx
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;

    private const int CornerDoNotRound = 1;
    private const int CornerRound = 2;

    /// <summary>DWMWA_COLOR_DEFAULT：使用系统默认边框色。</summary>
    private static readonly int DefaultBorder = unchecked((int)0xFFFFFFFF);

    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            ApplyRounding(window);
            SuppressSystemBorder(window);
        };
        window.StateChanged += (_, _) =>
        {
            ApplyRounding(window);
            SuppressSystemBorder(window);
        };
    }

    private static void ApplyRounding(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        var preference = window.WindowState == WindowState.Maximized ? CornerDoNotRound : CornerRound;
        Set(handle, DwmwaWindowCornerPreference, preference);
    }

    /// <summary>
    /// Windows 11 会给每个窗口画一圈 1px 的边框（默认浅灰）。本窗口是分层透明窗口、
    /// 内容又内缩了投影留白，这圈边框就会被孤立在最外层，看起来像"应用外面还套了个方框"。
    /// 把边框色设成 DWMWA_COLOR_NONE 关掉它，圆角与光边全部由我们自己画。
    /// </summary>
    private static void SuppressSystemBorder(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        // 不透明窗口：边框、圆角、投影都交回系统，避免自绘边缘出现白点
        Set(handle, DwmwaBorderColor, DefaultBorder);
    }

    private static void Set(IntPtr handle, int attribute, int value)
    {
        try
        {
            DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Windows 10 没有这些属性，保持系统默认外观
        }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
