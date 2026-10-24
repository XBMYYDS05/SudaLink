using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace CampusNetWpf;

public partial class App : Application
{
    private const string MutexName = @"Local\CampusNetWpf.SingleInstance";
    private const string EventName = @"Local\CampusNetWpf.Activate";
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
                if (MainWindow is not null)
                    ((MainWindow)MainWindow).ShowFromTray();
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
            if (result.MigratedFromLegacy || e.Args.Contains("--background") == false && !File.Exists(store.ConfigPath))
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

        MainWindow = new MainWindow(config, store, warning)
        {
            StartInTray = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase)
        };
        MainWindow.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        MessageBox.Show($"操作未完成：{e.Exception.Message}", "CampusNet",
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
