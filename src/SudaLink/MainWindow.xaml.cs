using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using Forms = System.Windows.Forms;

namespace SudaLink;

public partial class MainWindow : Window
{
    private static readonly Brush OnlineBrush = new SolidColorBrush(Color.FromRgb(14, 159, 110));
    private static readonly Brush OfflineBrush = new SolidColorBrush(Color.FromRgb(217, 119, 6));
    private static readonly Brush BusyBrush = new SolidColorBrush(Color.FromRgb(37, 99, 235));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(220, 38, 38));
    private static readonly Brush IdleBrush = new SolidColorBrush(Color.FromRgb(154, 161, 172));

    private readonly AppConfig _config;
    private readonly ConfigStore _store;
    private readonly NetworkMonitor _monitor;
    private readonly ObservableCollection<LogEntry> _logs = [];
    private readonly Forms.NotifyIcon _tray;
    private readonly string? _startupWarning;
    private Account? _editingAccount;
    private bool _exitRequested;
    private bool _cleanedUp;
    private bool _hotspotBusy;
    private bool _hotspotOn;
    private bool _pulsing;
    private Storyboard? _pulse;

    public bool StartInTray { get; init; }

    public MainWindow(AppConfig config, ConfigStore store, string? startupWarning)
    {
        InitializeComponent();
        WindowFx.Attach(this);
        _config = config;
        _store = store;
        _startupWarning = startupWarning;
        _monitor = new NetworkMonitor(_config, new PortalAuthenticator());
        _monitor.StatusChanged += OnNetworkStatus;
        _monitor.LogReceived += AddLog;
        _tray = CreateTrayIcon();
        HotspotService.StateChanged += OnHotspotStateChanged;
        LogsList.ItemsSource = _logs;
        Loaded += OnLoaded;
        LoadSettings();
        RefreshAccounts();
        var requested = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))?[7..];
        SetPage(requested switch
        {
            "accounts" => "Accounts",
            "settings" => "Settings",
            _ => "Overview"
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AddLog(new LogEntry(DateTime.Now, "信息", "SudaLink 已在后台守护校园网"));
        if (!string.IsNullOrWhiteSpace(_startupWarning))
            AddLog(new LogEntry(DateTime.Now, "警告", _startupWarning));
        UpdatePauseButton();
        _ = RefreshHotspotAsync();
        _monitor.Start();
        if (StartInTray)
        {
            Hide();
            ShowInTaskbar = false;
        }
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 SudaLink", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("立即连接", null, (_, _) => Dispatcher.Invoke(() =>
        {
            _monitor.ConnectNow();
            AddLog(new LogEntry(DateTime.Now, "信息", "已请求立即检测并连接"));
        }));
        menu.Items.Add("开启 / 关闭热点", null, (_, _) => Dispatcher.InvokeAsync(StartHotspotAsync));
        menu.Items.Add("暂停 / 恢复自动重连", null, (_, _) => Dispatcher.Invoke(() =>
        {
            _monitor.SetPaused(!_monitor.IsPaused);
            UpdatePauseButton();
        }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        var icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty)
            ?? System.Drawing.SystemIcons.Application;
        var tray = new Forms.NotifyIcon
        {
            Text = "SudaLink · 检测中",
            Icon = icon,
            Visible = true,
            ContextMenuStrip = menu
        };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        return tray;
    }

    public void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void OnNetworkStatus(NetworkSnapshot snapshot)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (snapshot.NextCheckSeconds is int seconds and > 0)
            {
                StatusMeta.Text = $"上次检测 {snapshot.Timestamp:HH:mm:ss} · 约 {seconds} 秒后再次检测";
                return;
            }

            var (title, shortText, brush) = snapshot.Status switch
            {
                ConnectionStatus.Online => ("网络已连接", "已连接", OnlineBrush),
                ConnectionStatus.Offline => ("网络未连接", "未联网", OfflineBrush),
                ConnectionStatus.NoCampusNetwork => ("未在校园网环境", "网关不可达", OfflineBrush),
                ConnectionStatus.Authenticating => ("正在认证", "认证中", BusyBrush),
                ConnectionStatus.Failed => ("连接需要处理", "认证失败", ErrorBrush),
                ConnectionStatus.Paused => ("自动重连已暂停", "已暂停", IdleBrush),
                _ => ("正在检测网络", "检测中", BusyBrush)
            };

            SoftChange(StatusMessage, snapshot.Message);
            SoftChange(StatusTitle, title);
            StatusDot.Fill = brush;
            SideStatusDot.Fill = brush;
            SideStatus.Text = shortText;
            _tray.Text = TrimTrayText($"SudaLink · {shortText}");

            SetPulse(snapshot.Status is ConnectionStatus.Checking or ConnectionStatus.Authenticating, brush);

            StatusMeta.Text = $"上次检测 {snapshot.Timestamp:HH:mm:ss}";

            if (snapshot.Status == ConnectionStatus.Online && snapshot.AccountName is { Length: > 0 } name)
                UpdateCurrentAccountLabel(name);

            UpdatePauseButton();
        });
    }

    private void UpdatePauseButton()
    {
        var paused = _monitor.IsPaused;
        PauseText.Text = paused ? "重连已暂停" : "重连守护中";
        PauseButton.Style = (Style)FindResource(paused ? "GhostOff" : "GhostOn");
        PauseButton.ToolTip = paused ? "点击恢复自动重连" : "点击暂停自动重连";
    }

    private void OnHotspotStateChanged(bool on) =>
        Dispatcher.InvokeAsync(() => ApplyHotspotState(on));

    private void ApplyHotspotState(bool on)
    {
        _hotspotOn = on;
        HotspotText.Text = on ? "热点已开启" : "热点未开启";
        HotspotButton.Style = (Style)FindResource(on ? "GhostOn" : "GhostOff");
        HotspotButton.ToolTip = on ? "点击关闭移动热点" : "点击开启移动热点";
    }

    private async Task RefreshHotspotAsync() =>
        ApplyHotspotState(await HotspotService.IsOnAsync());

    private void AddLog(LogEntry entry)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _logs.Insert(0, entry);
            while (_logs.Count > 400)
                _logs.RemoveAt(_logs.Count - 1);
        });
    }

    private void SetPage(string page)
    {
        var target = page switch
        {
            "Accounts" => (FrameworkElement)AccountsPage,
            "Settings" => SettingsPage,
            _ => DashboardPage
        };
        var changed = target.Visibility != Visibility.Visible;
        DashboardPage.Visibility = page == "Overview" ? Visibility.Visible : Visibility.Collapsed;
        AccountsPage.Visibility = page == "Accounts" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        var (title, subtitle) = page switch
        {
            "Accounts" => ("账号", "学号与运营商组合决定认证出口"),
            "Settings" => ("设置", "自动守护、热点与启动行为"),
            _ => ("概览", "校园网连接状态与自动守护")
        };
        PageTitle.Text = title;
        PageSubtitle.Text = subtitle;
        NavOverview.IsChecked = page == "Overview";
        NavAccounts.IsChecked = page == "Accounts";
        NavSettings.IsChecked = page == "Settings";
        if (changed)
            AnimateIn(target);
    }

    /// <summary>页面切换：淡入并轻微上浮，避免生硬跳转。</summary>
    private void AnimateIn(FrameworkElement element)
    {
        if (element.RenderTransform is not TranslateTransform shift)
        {
            shift = new TranslateTransform();
            element.RenderTransform = shift;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        element.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        Flash(PageTitle);
        Flash(PageSubtitle);
    }

    private static void Flash(UIElement element) =>
        element.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

    /// <summary>文字变化时做一次极轻的淡入，提示内容已更新。</summary>
    private static void SoftChange(TextBlock block, string text)
    {
        if (block.Text == text)
            return;
        block.Text = text;
        block.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    /// <summary>检测/认证进行中时，状态点外圈做呼吸光晕。</summary>
    private void SetPulse(bool active, Brush brush)
    {
        StatusHalo.Fill = brush;
        if (active == _pulsing)
            return;
        _pulsing = active;
        _pulse ??= (Storyboard)FindResource("StatusPulse");
        if (active)
        {
            _pulse.Begin(this, true);
        }
        else
        {
            _pulse.Stop(this);
            StatusHalo.Opacity = 0;
        }
    }

    private void ShowDashboard(object sender, RoutedEventArgs e) => SetPage("Overview");
    private void ShowAccounts(object sender, RoutedEventArgs e) => SetPage("Accounts");
    private void ShowSettings(object sender, RoutedEventArgs e) => SetPage("Settings");

    // ---------- 账号 ----------

    private void RefreshAccounts(string? selectId = null)
    {
        foreach (var account in _config.Accounts)
            account.IsCurrent = account.Id == _config.CurrentAccountId;
        AccountsList.ItemsSource = null;
        AccountsList.ItemsSource = _config.Accounts;
        AccountsList.SelectedItem = _config.Accounts.FirstOrDefault(a => a.Id == (selectId ?? _config.CurrentAccountId));
        var current = _config.CurrentAccount;
        UpdateCurrentAccountLabel(current?.Username);
        SideAccount.Text = current is null ? "未选择账号" : $"{current.CarrierName} · {current.Number}";
    }

    private void UpdateCurrentAccountLabel(string? username)
    {
        var account = _config.Accounts.FirstOrDefault(a => string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase))
            ?? _config.CurrentAccount;
        if (account is null)
        {
            CurrentNumber.Text = "未选择账号";
            CurrentCarrierTag.Visibility = Visibility.Collapsed;
            return;
        }
        CurrentNumber.Text = account.Number;
        CurrentCarrier.Text = account.CarrierName;
        CurrentCarrierTag.Visibility = Visibility.Visible;
    }

    private void NewAccount(object sender, RoutedEventArgs e)
    {
        _editingAccount = null;
        AccountsList.SelectedItem = null;
        AccountNumberInput.Text = string.Empty;
        PasswordInput.Password = string.Empty;
        PasswordVisible.Text = string.Empty;
        AccountEnabled.IsChecked = true;
        ShowPassword.IsChecked = false;
        ShowPassword.Content = "显示密码";
        PasswordVisible.Visibility = Visibility.Collapsed;
        PasswordInput.Visibility = Visibility.Visible;
        CarrierCampus.IsChecked = true;
        EditorTitle.Text = "添加账号";
        EditorHint.Text = "填写学号并选择运营商即可";
        AccountNumberInput.Focus();
    }

    private void AccountSelected(object sender, SelectionChangedEventArgs e)
    {
        if (AccountsList.SelectedItem is not Account account)
            return;
        _editingAccount = account;
        AccountNumberInput.Text = account.Number;
        PasswordInput.Password = account.Password;
        PasswordVisible.Text = account.Password;
        AccountEnabled.IsChecked = account.Enabled;
        SetCarrier(account.CarrierName);
        ShowPassword.IsChecked = false;
        ShowPassword.Content = "显示密码";
        PasswordVisible.Visibility = Visibility.Collapsed;
        PasswordInput.Visibility = Visibility.Visible;
        EditorTitle.Text = "编辑账号";
        EditorHint.Text = account.IsCurrent ? "这是当前优先使用的账号" : "修改后记得保存";
    }

    private void SaveAccount(object sender, RoutedEventArgs e)
    {
        var number = AccountNumberInput.Text.Trim();
        var password = ShowPassword.IsChecked == true ? PasswordVisible.Text : PasswordInput.Password;
        if (string.IsNullOrWhiteSpace(number) || string.IsNullOrEmpty(password))
        {
            MessageBox.Show("请填写学号和密码。", "账号", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var carrier = SelectedCarrier();
        var username = number + Carriers.SuffixByName(carrier);
        if (_config.Accounts.Any(a => a.Id != _editingAccount?.Id
                && string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("该学号与运营商的组合已存在。", "账号", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _editingAccount ??= new Account();
        _editingAccount.SetLogin(number, carrier);
        _editingAccount.Password = password;
        _editingAccount.Enabled = AccountEnabled.IsChecked == true;
        if (!_config.Accounts.Contains(_editingAccount))
            _config.Accounts.Add(_editingAccount);
        _config.CurrentAccountId = string.IsNullOrEmpty(_config.CurrentAccountId)
            ? _editingAccount.Id
            : _config.CurrentAccountId;

        if (SaveConfig())
        {
            RefreshAccounts(_editingAccount.Id);
            AddLog(new LogEntry(DateTime.Now, "信息", $"账号 {number}（{carrier}）已保存"));
        }
    }

    private void SetCurrentAccount(object sender, RoutedEventArgs e)
    {
        if (_editingAccount is null || !_config.Accounts.Contains(_editingAccount))
        {
            MessageBox.Show("请先保存账号，再设为当前账号。", "账号", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _config.CurrentAccountId = _editingAccount.Id;
        _editingAccount.Enabled = true;
        AccountEnabled.IsChecked = true;
        if (SaveConfig())
        {
            RefreshAccounts(_editingAccount.Id);
            AddLog(new LogEntry(DateTime.Now, "信息", "当前优先账号已更新"));
        }
    }

    private void DeleteAccount(object sender, RoutedEventArgs e)
    {
        if (_editingAccount is null || !_config.Accounts.Contains(_editingAccount))
            return;
        var result = MessageBox.Show($"确定删除 {_editingAccount.Number}（{_editingAccount.CarrierName}）吗？", "删除账号",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
            return;

        var id = _editingAccount.Id;
        _config.Accounts.Remove(_editingAccount);
        if (_config.CurrentAccountId == id)
            _config.CurrentAccountId = _config.Accounts.FirstOrDefault()?.Id ?? string.Empty;
        if (SaveConfig())
        {
            NewAccount(sender, e);
            RefreshAccounts();
            AddLog(new LogEntry(DateTime.Now, "信息", "账号已删除"));
        }
    }

    private void TogglePasswordVisibility(object sender, RoutedEventArgs e)
    {
        var visible = ShowPassword.IsChecked == true;
        ShowPassword.Content = visible ? "隐藏密码" : "显示密码";
        if (visible)
        {
            PasswordVisible.Text = PasswordInput.Password;
            PasswordVisible.Visibility = Visibility.Visible;
            PasswordInput.Visibility = Visibility.Collapsed;
        }
        else
        {
            PasswordInput.Password = PasswordVisible.Text;
            PasswordInput.Visibility = Visibility.Visible;
            PasswordVisible.Visibility = Visibility.Collapsed;
        }
    }

    private string SelectedCarrier() =>
        CarrierMobile.IsChecked == true ? "移动"
        : CarrierUnicom.IsChecked == true ? "联通"
        : CarrierTelecom.IsChecked == true ? "电信"
        : "校园网";

    private void SetCarrier(string carrier)
    {
        CarrierCampus.IsChecked = carrier == "校园网";
        CarrierMobile.IsChecked = carrier == "移动";
        CarrierUnicom.IsChecked = carrier == "联通";
        CarrierTelecom.IsChecked = carrier == "电信";
    }

    // ---------- 设置 ----------

    private void LoadSettings()
    {
        AutoReconnectSwitch.IsChecked = _config.Settings.AutoReconnect;
        AutoHotspotSwitch.IsChecked = _config.Settings.AutoHotspot;
        AutoStartSwitch.IsChecked = _config.Settings.AutoStart;
        CloseToTraySwitch.IsChecked = _config.Settings.CloseToTray;
        GatewayInput.Text = _config.Settings.Gateway;
        SelectInterval(_config.Settings.IntervalSeconds);
    }

    private void SelectInterval(int seconds)
    {
        Interval5.IsChecked = seconds <= 5;
        Interval10.IsChecked = seconds > 5 && seconds <= 10;
        Interval30.IsChecked = seconds > 10 && seconds <= 30;
        Interval60.IsChecked = seconds > 30;
    }

    private int SelectedInterval() =>
        Interval5.IsChecked == true ? 5
        : Interval10.IsChecked == true ? 10
        : Interval30.IsChecked == true ? 30
        : 60;

    private void ToggleAutoReconnect(object sender, RoutedEventArgs e)
    {
        _config.Settings.AutoReconnect = AutoReconnectSwitch.IsChecked == true;
        SaveConfig();
    }

    private void ToggleAutoHotspot(object sender, RoutedEventArgs e)
    {
        _config.Settings.AutoHotspot = AutoHotspotSwitch.IsChecked == true;
        if (SaveConfig() && _config.Settings.AutoHotspot)
            AddLog(new LogEntry(DateTime.Now, "信息", "已开启自动热点：认证成功后将自动共享网络"));
    }

    private void ToggleAutoStart(object sender, RoutedEventArgs e)
    {
        var enabled = AutoStartSwitch.IsChecked == true;
        try
        {
            AutoStartService.Apply(enabled);
            _config.Settings.AutoStart = enabled;
            SaveConfig();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            AutoStartSwitch.IsChecked = !enabled;
            MessageBox.Show($"开机启动设置失败：{ex.Message}", "设置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ToggleCloseToTray(object sender, RoutedEventArgs e)
    {
        _config.Settings.CloseToTray = CloseToTraySwitch.IsChecked == true;
        SaveConfig();
    }

    private void IntervalChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;
        _config.Settings.IntervalSeconds = SelectedInterval();
        if (SaveConfig())
            AddLog(new LogEntry(DateTime.Now, "信息", $"检测间隔已改为 {SelectedInterval()} 秒"));
    }

    private void ApplyGateway(object sender, RoutedEventArgs e)
    {
        var gateway = GatewayInput.Text.Trim();
        try
        {
            PortalAuthenticator.ValidateGateway(gateway);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "设置", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _config.Settings.Gateway = gateway;
        if (SaveConfig())
            AddLog(new LogEntry(DateTime.Now, "信息", $"认证网关已设为 {gateway}"));
    }

    private bool SaveConfig()
    {
        try
        {
            _store.Save(_config);
            _monitor.UpdateConfig(_config);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            MessageBox.Show($"保存失败：{ex.Message}", "SudaLink", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    // ---------- 动作 ----------

    private void ConnectNow(object sender, RoutedEventArgs e)
    {
        _monitor.ConnectNow();
        UpdatePauseButton();
        AddLog(new LogEntry(DateTime.Now, "信息", "已请求立即检测并连接"));
    }

    private void TogglePause(object sender, RoutedEventArgs e)
    {
        _monitor.SetPaused(!_monitor.IsPaused);
        UpdatePauseButton();
        AddLog(new LogEntry(DateTime.Now, "信息", _monitor.IsPaused ? "自动重连已暂停" : "自动重连已恢复"));
    }

    private void StartHotspot(object sender, RoutedEventArgs e) => _ = StartHotspotAsync();

    private async Task StartHotspotAsync()
    {
        if (_hotspotBusy)
            return;
        _hotspotBusy = true;
        HotspotStatus.Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128));
        HotspotStatus.Text = _hotspotOn ? "正在关闭热点…" : "正在开启热点…";
        try
        {
            var result = _hotspotOn
                ? await HotspotService.TurnOffAsync()
                : await HotspotService.EnsureOnAsync();
            HotspotStatus.Foreground = new SolidColorBrush(result.Success
                ? Color.FromRgb(14, 159, 110)
                : Color.FromRgb(220, 38, 38));
            HotspotStatus.Text = result.Message;
            AddLog(new LogEntry(DateTime.Now, result.Success ? "信息" : "警告", result.Message));
        }
        finally
        {
            _hotspotBusy = false;
        }
    }

    private void OpenHotspotSettings(object sender, RoutedEventArgs e) => HotspotService.OpenSystemSettings();

    private void CopyLogs(object sender, RoutedEventArgs e)
    {
        if (_logs.Count == 0)
            return;
        Clipboard.SetText(string.Join(Environment.NewLine, _logs.Reverse().Select(x => x.Display)));
    }

    private void ClearLogs(object sender, RoutedEventArgs e) => _logs.Clear();

    // ---------- 窗口 ----------

    private void MinimizeWindow(object sender, RoutedEventArgs e)
    {
        Hide();
        ShowInTaskbar = false;
    }

    private void MaximizeWindow(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_exitRequested && _config.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;
        }
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        Cleanup();
        Application.Current.Shutdown();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_exitRequested)
            Cleanup();
        else
            Application.Current.Shutdown();
    }

    private void Cleanup()
    {
        if (_cleanedUp)
            return;
        _cleanedUp = true;
        HotspotService.StateChanged -= OnHotspotStateChanged;
        _monitor.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
    }

    private static string TrimTrayText(string text) => text.Length <= 63 ? text : text[..63];
}
