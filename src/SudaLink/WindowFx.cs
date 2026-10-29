using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SudaLink;

/// <summary>
/// 窗口外观：圆角、光边、投影全部由应用自己在分层窗口上画。
/// 这里只负责拦住系统想叠上来的那一笔：Win11 会给每个窗口描一圈 1px 边框，
/// 落在透明留白上就像“应用外面还套了个方框”。
/// </summary>
internal static class WindowFx
{
    private const int DwmwaBorderColor = 34;

    /// <summary>DWMWA_COLOR_NONE：彻底不画系统边框。</summary>
    private const int NoBorder = unchecked((int)0xFFFFFFFE);

    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) => SuppressSystemBorder(window);
        window.StateChanged += (_, _) => SuppressSystemBorder(window);
    }

    private static void SuppressSystemBorder(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        Set(handle, DwmwaBorderColor, NoBorder);
    }

    private static void Set(IntPtr handle, int attribute, int value)
    {
        try
        {
            DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Windows 10 没有这个属性，保持系统默认外观
        }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
