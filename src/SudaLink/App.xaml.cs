using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace SudaLink;

public partial class App : Application
{
    private const string MutexName = @"Local\SudaLink.SingleInstance";
    private const string EventName = @"Local\SudaLink.Activate";
    private Mutex? _mutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _registeredWait;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // 管理员子进程：只做热点开关，不创建窗口/托盘。必须在单实例检查之前分流，
        // 否则会被主实例的互斥体挡掉。
        if (TryRunHotspotHelper(e.Args))
            return;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        _mutex = new Mutex(true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            try { EventWaitHandle.OpenExisting(EventName).Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        _registeredWait = ThreadPool.RegisterWaitForSingleObject(_activationEvent, (_, _) =>
        {
            Dispatcher.Invoke(() =>
            {
                if (MainWindow is MainWindow window)
                    window.ShowFromTray();
            });
        }, null, Timeout.Infinite, false);

        var store = new ConfigStore();
        string? warning = null;
        AppConfig config;
        try
        {
            var result = store.Load();
            config = result.Config;
            warning = result.Warning;
            if (result.Migrated || !File.Exists(store.ConfigPath))
                store.Save(config);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            config = new AppConfig();
            warning = $"配置读取失败：{ex.Message}";
        }

        if (config.Settings.AutoStart)
        {
            var applied = AutoStartService.Apply(true);
            if (!applied.Success)
                warning = $"无法设置开机启动：{applied.Error}";
            else
                AppLog.Write($"开机启动已登记（{applied.Method}）");
        }

        var forcedTheme = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase))?[8..];
        if (!string.IsNullOrWhiteSpace(forcedTheme))
        {
            config.Settings.Theme = string.Equals(forcedTheme, "light", StringComparison.OrdinalIgnoreCase)
                ? ThemeService.Light
                : ThemeService.Dark;
        }

        ThemeService.Apply(config.Settings.Theme);

        var background = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        var window = new MainWindow(config, store, warning) { StartInTray = background };
        MainWindow = window;

        if (background && window.HasTray)
        {
            // 后台启动：只建窗口句柄，从不 Show —— 开机时那一下黑框就是"显示完立刻隐藏"
            // 留下的残影，干脆别显示。
            window.StartHidden();
        }
        else
        {
            // 托盘图标建不起来时不能静默隐藏，否则用户既没有窗口也没有入口
            if (background)
            {
                AppLog.Write("托盘不可用，后台启动改为缩到任务栏");
                window.StartMinimized();
            }
            else
            {
                window.Show();
            }
        }

        Program.StartupCompleted = true;
        AppLog.Write($"界面就绪（{(background ? "后台启动" : "前台启动")}）");

        // 开发用：--render=<目录> 把三个页面渲染成 PNG 后退出（截图 / 文档用）
        // 可选 --render-wait=<秒>：先跑一段时间再渲染，用来观察实际运行后的日志与状态
        var renderDir = e.Args.FirstOrDefault(a =>
            a.StartsWith("--render=", StringComparison.OrdinalIgnoreCase))?[9..];
        if (!string.IsNullOrWhiteSpace(renderDir))
        {
            var wait = e.Args.FirstOrDefault(a =>
                a.StartsWith("--render-wait=", StringComparison.OrdinalIgnoreCase))?[14..];
            _ = RenderAndExitAsync(window, renderDir,
                double.TryParse(wait, out var seconds) ? seconds : 0.9);
        }
    }

    /// <summary>管理员子进程入口：执行一次热点开关，把结果写回临时文件后退出。</summary>
    private bool TryRunHotspotHelper(string[] args)
    {
        var on = args.Contains("--hotspot-on", StringComparer.OrdinalIgnoreCase);
        var off = args.Contains("--hotspot-off", StringComparer.OrdinalIgnoreCase);
        if (!on && !off)
            return false;

        var resultPath = args
            .FirstOrDefault(a => a.StartsWith("--result=", StringComparison.OrdinalIgnoreCase))?[9..];

        HotspotResult result;
        try
        {
            result = Task.Run(async () => on
                ? await HotspotService.EnsureOnAsync()
                : await HotspotService.TurnOffAsync()).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            result = HotspotResult.Fail($"热点操作失败：{ex.Message}");
        }

        if (!string.IsNullOrWhiteSpace(resultPath))
        {
            try
            {
                File.WriteAllText(resultPath,
                    JsonSerializer.Serialize(new { result.Success, result.Message }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 结果文件写不了就把退出码当作结果，主进程会给出兜底提示
            }
        }

        // 子进程不进入 WPF 消息循环，写完结果直接退出（Shutdown 在 Startup 阶段不够可靠）
        Environment.Exit(result.Success ? 0 : 2);
        return true;
    }

    private async Task RenderAndExitAsync(MainWindow window, string directory, double waitSeconds)
    {
        try
        {
            await window.RenderSnapshotsAsync(directory, waitSeconds);
        }
        finally
        {
            Shutdown();
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        AppLog.Write($"界面异常：{e.Exception}");
        MessageBox.Show($"操作未完成：{e.Exception.Message}", "SudaLink",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        AppLog.Write($"进程退出（ExitCode={e.ApplicationExitCode}）");
        _registeredWait?.Unregister(null);
        _activationEvent?.Dispose();
        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
            _mutex.Dispose();
        }
    }
}
