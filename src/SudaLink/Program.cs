using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace SudaLink;

/// <summary>
/// 自定义入口点，只为了一件事：开机启动失败时能自愈。
/// <para>
/// 实测开机那次（2026-10-29 00:32）进程死在 <c>Dispatcher..ctor()</c>：
/// <c>COMException 0x800705AF 页面文件太小</c> —— 登录瞬间系统提交内存被一堆
/// 开机程序占满，WPF 连 Dispatcher 都建不出来，进程当场消失。窗口没了、托盘也没了，
/// 用户只看到「开机后是黑的，得再点一次图标」。这类失败是暂时的，所以这里
/// 用一个新进程延迟重试，而不是让用户手动补一次。
/// </para>
/// 只有「还没启动完成」时才会重试；已经跑起来之后的崩溃照旧不留活口，避免崩溃循环。
/// </summary>
internal static class Program
{
    private const string RetryArgument = "--retry=";
    private static readonly int[] RetryDelays = [8, 20, 45];

    /// <summary>App.OnStartup 走完一轮后才置 true，供失败判定使用。</summary>
    internal static bool StartupCompleted { get; set; }

    [STAThread]
    private static int Main(string[] args)
    {
        EnablePerMonitorDpi();

        var attempt = 0;
        var forwarded = new List<string>();
        foreach (var arg in args)
        {
            if (arg.StartsWith(RetryArgument, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(arg[RetryArgument.Length..], out var parsed))
                attempt = parsed;
            else
                forwarded.Add(arg);
        }

        if (attempt > 0)
        {
            // 重试进程先让出这段时间，等开机那波提交内存过去
            var delay = RetryDelays[Math.Min(attempt, RetryDelays.Length) - 1];
            AppLog.Write($"重试进程（第 {attempt} 次）等待 {delay} 秒后启动");
            Thread.Sleep(TimeSpan.FromSeconds(delay));
        }

        AppLog.Write($"进程启动：参数[{string.Join(' ', forwarded)}]");

        try
        {
            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }
        catch (Exception ex)
        {
            AppLog.Write($"启动异常：{ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");

            if (!StartupCompleted && attempt < RetryDelays.Length)
            {
                StartSuccessor(attempt + 1, forwarded);
                return 3;
            }

            AppLog.Write("启动最终失败");
            ShowFailure(ex);
            return 1;
        }
    }

    /// <summary>
    /// 声明 Per-Monitor V2 DPI 感知。清单里塞 dpiAware 会让单文件宿主报
    /// 「并行配置不正确」直接起不来，所以改成运行时设置，效果一样。
    /// </summary>
    private static void EnablePerMonitorDpi()
    {
        try
        {
            // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
            SetProcessDpiAwarenessContext(new IntPtr(-4));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Windows 10 1703 之前没有这个 API，交给系统默认缩放
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    /// <summary>拉起下一个带 <c>--retry</c> 参数的自己；当前进程随后退出，不留僵尸。</summary>
    private static void StartSuccessor(int attempt, IReadOnlyList<string> arguments)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            return;

        try
        {
            var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
            startInfo.ArgumentList.Add($"{RetryArgument}{attempt}");

            Process.Start(startInfo);
            AppLog.Write($"已拉起重试进程（第 {attempt} 次）");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Write($"拉起重试进程失败：{ex.Message}");
        }
    }

    private static void ShowFailure(Exception ex)
    {
        var reason = ex is COMException or OutOfMemoryException
            ? "系统内存（页面文件）不足，程序没能完成初始化"
            : ex.Message;
        try
        {
            System.Windows.Forms.MessageBox.Show(
                $"SudaLink 没能启动：{reason}。\n\n请稍后重新打开；详细日志在：\n{AppLog.FilePath}",
                "SudaLink", System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Warning);
        }
        catch (Exception)
        {
            // 弹窗本身失败就算了
        }
    }
}
