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
            try { AutoStartService.Apply(true); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                warning = "无法设置开机启动，请检查系统权限。";
            }
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

        var window = new MainWindow(config, store, warning)
        {
            StartInTray = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase)
        };
        MainWindow = window;
        window.Show();

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
        MessageBox.Show($"操作未完成：{e.Exception.Message}", "SudaLink",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        _registeredWait?.Unregister(null);
        _activationEvent?.Dispose();
        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
            _mutex.Dispose();
        }
    }
}
