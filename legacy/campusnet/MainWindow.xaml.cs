using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using Forms = System.Windows.Forms;

namespace CampusNetWpf;

public partial class MainWindow : Window
{
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(22, 163, 74));
    private static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(217, 119, 6));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(220, 38, 38));
    private static readonly Brush Blue = new SolidColorBrush(Color.FromRgb(37, 99, 235));
    private static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(100, 116, 139));
    private static readonly Brush NavActive = new SolidColorBrush(Color.FromRgb(30, 65, 124));
    private static readonly Brush NavInactive = Brushes.Transparent;

    private readonly AppConfig _config;
    private readonly ConfigStore _store;
    private readonly CampusAuthenticator _authenticator = new();
    private readonly NetworkMonitor _monitor;
    private readonly ObservableCollection<LogEntry> _logs = [];
    private readonly Forms.NotifyIcon _tray;
    private readonly string? _startupWarning;
    private Account? _editingAccount;
    private bool _exitRequested;
    private bool _cleanedUp;

    public bool StartInTray { get; init; }
    public ObservableCollection<LogEntry> DashboardLogItems { get; } = [];

    public MainWindow(AppConfig config, ConfigStore store, string? startupWarning)
    {
        InitializeComponent();
        _config = config;
        _store = store;
        _startupWarning = startupWarning;
        DataContext = this;
        LogsList.ItemsSource = _logs;
        _monitor = new NetworkMonitor(_config, _authenticator);
        _monitor.StatusChanged += OnNetworkStatus;
        _monitor.LogReceived += AddLog;
        _tray = CreateTrayIcon();
        Loaded += OnLoaded;
        RefreshAccounts();
        LoadSettings();
        SetPage("Dashboard");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AddLog(new LogEntry(DateTime.Now, "信息", "CampusNet 2.0 已启动"));
        if (!string.IsNullOrWhiteSpace(_startupWarning))
            AddLog(new LogEntry(DateTime.Now, "警告", _startupWarning));
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
        menu.Items.Add("显示 CampusNet", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("立即连接", null, (_, _) => Dispatcher.Invoke(() => _monitor.ConnectNow()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        var icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty)
            ?? System.Drawing.SystemIcons.Application;
        var tray = new Forms.NotifyIcon
        {
            Text = "CampusNet · 正在检测",
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
            LastCheckText.Text = snapshot.Timestamp.ToString("HH:mm:ss");
            if (snapshot.NextCheckSeconds.HasValue)
            {
                NextCheckText.Text = $"约 {snapshot.NextCheckSeconds.Value} 秒后进行下次检测";
                return;
            }

            var (title, shortText, color) = snapshot.Status switch
            {
                ConnectionStatus.Online => ("网络已连接", "已连接", Green),
                ConnectionStatus.Offline => ("网络未连接", "未联网", Amber),
                ConnectionStatus.NoCampusNetwork => ("未在校园网环境", "网关不可达", Amber),
                ConnectionStatus.Authenticating => ("正在认证", "认证中", Blue),
                ConnectionStatus.Failed => ("连接需要处理", "认证失败", Red),
                ConnectionStatus.Paused => ("自动重连已暂停", "已暂停", Gray),
                _ => ("正在检测网络", "检测中", Blue)
            };
            StatusText.Text = title;
            StatusShort.Text = shortText;
            StatusMessage.Text = snapshot.Message;
            StatusDot.Fill = color;
            SideStatusDot.Fill = color;
            SideStatus.Text = shortText;
            _tray.Text = TrimTrayText($"CampusNet · {shortText}");
            PauseButton.Content = _monitor.IsPaused ? "恢复重连" : "暂停重连";
        });
    }

    private void AddLog(LogEntry entry)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _logs.Insert(0, entry);
            DashboardLogItems.Insert(0, entry);
            while (_logs.Count > 500) _logs.RemoveAt(_logs.Count - 1);
            while (DashboardLogItems.Count > 30) DashboardLogItems.RemoveAt(DashboardLogItems.Count - 1);
        });
    }

    private void SetPage(string page)
    {
        DashboardPage.Visibility = page == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
        AccountsPage.Visibility = page == "Accounts" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        LogsPage.Visibility = page == "Logs" ? Visibility.Visible : Visibility.Collapsed;
        (PageTitle.Text, PageSubtitle.Text) = page switch
        {
            "Accounts" => ("账号管理", "管理校园网与运营商账号"),
            "Settings" => ("偏好设置", "自动守护、系统启动与网关参数"),
            "Logs" => ("运行日志", "查看认证与网络状态变化"),
            _ => ("网络概览", "校园网连接状态与自动守护")
        };
        foreach (var (button, active) in new[]
                 {
                     (NavDashboard, page == "Dashboard"), (NavAccounts, page == "Accounts"),
                     (NavSettings, page == "Settings"), (NavLogs, page == "Logs")
                 })
        {
            button.Background = active ? NavActive : NavInactive;
            button.Foreground = active ? Brushes.White : new SolidColorBrush(Color.FromRgb(148, 163, 184));
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void ShowDashboard(object sender, RoutedEventArgs e) => SetPage("Dashboard");
    private void ShowAccounts(object sender, RoutedEventArgs e) => SetPage("Accounts");
    private void ShowSettings(object sender, RoutedEventArgs e) => SetPage("Settings");
    private void ShowLogs(object sender, RoutedEventArgs e) => SetPage("Logs");

    private void RefreshAccounts(string? selectId = null)
    {
        foreach (var account in _config.Accounts)
            account.IsCurrent = account.Id == _config.CurrentAccountId;
        AccountsList.ItemsSource = null;
        AccountsList.ItemsSource = _config.Accounts;
        AccountsList.SelectedItem = _config.Accounts.FirstOrDefault(a => a.Id == (selectId ?? _config.CurrentAccountId));
        var current = _config.CurrentAccount;
        CurrentAccountText.Text = current?.Username ?? "未选择";
        CurrentOperatorText.Text = current?.OperatorName ?? "请在账号管理中添加账号";
        SideAccount.Text = current is null ? "未选择账号" : $"{current.OperatorName} · {current.Username}";
    }

    private void NewAccount(object sender, RoutedEventArgs e)
    {
        AccountsList.SelectedItem = null;
        _editingAccount = null;
        UsernameInput.Text = string.Empty;
        PasswordInput.Password = string.Empty;
        PasswordVisibleInput.Text = string.Empty;
        EnabledInput.IsChecked = true;
        ShowPasswordInput.IsChecked = false;
        EditorHint.Text = "正在创建新账号";
        UsernameInput.Focus();
    }

    private void AccountSelected(object sender, SelectionChangedEventArgs e)
    {
        if (AccountsList.SelectedItem is not Account account)
            return;
        _editingAccount = account;
        UsernameInput.Text = account.Username;
        PasswordInput.Password = account.Password;
        PasswordVisibleInput.Text = account.Password;
        EnabledInput.IsChecked = account.Enabled;
        EditorHint.Text = account.IsCurrent ? "当前优先使用的账号" : "编辑后点击保存账号";
    }

    private void SaveAccount(object sender, RoutedEventArgs e)
    {
        var username = UsernameInput.Text.Trim();
        var password = ShowPasswordInput.IsChecked == true ? PasswordVisibleInput.Text : PasswordInput.Password;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            MessageBox.Show("请输入账号和密码。", "账号管理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_config.Accounts.Any(a => a.Id != _editingAccount?.Id
                && string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("该账号已存在。", "账号管理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _editingAccount ??= new Account();
        _editingAccount.Username = username;
        _editingAccount.Password = password;
        _editingAccount.Enabled = EnabledInput.IsChecked == true;
        if (!_config.Accounts.Contains(_editingAccount))
            _config.Accounts.Add(_editingAccount);
        if (string.IsNullOrEmpty(_config.CurrentAccountId))
            _config.CurrentAccountId = _editingAccount.Id;
        if (SaveConfig())
        {
            RefreshAccounts(_editingAccount.Id);
            AddLog(new LogEntry(DateTime.Now, "信息", $"账号 {username} 已保存"));
        }
    }

    private void SetCurrentAccount(object sender, RoutedEventArgs e)
    {
        if (_editingAccount is null || !_config.Accounts.Contains(_editingAccount))
        {
            MessageBox.Show("请先保存账号，再设为当前账号。", "账号管理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _config.CurrentAccountId = _editingAccount.Id;
        _editingAccount.Enabled = true;
        EnabledInput.IsChecked = true;
        if (SaveConfig())
        {
            RefreshAccounts(_editingAccount.Id);
            AddLog(new LogEntry(DateTime.Now, "信息", "当前账号已更新"));
        }
    }

    private void DeleteAccount(object sender, RoutedEventArgs e)
    {
        if (_editingAccount is null || !_config.Accounts.Contains(_editingAccount))
            return;
        var result = MessageBox.Show($"确定删除账号 {_editingAccount.Username} 吗？", "删除账号",
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
        var visible = ShowPasswordInput.IsChecked == true;
        if (visible)
        {
            PasswordVisibleInput.Text = PasswordInput.Password;
            PasswordVisibleInput.Visibility = Visibility.Visible;
            PasswordInput.Visibility = Visibility.Collapsed;
        }
        else
        {
            PasswordInput.Password = PasswordVisibleInput.Text;
            PasswordInput.Visibility = Visibility.Visible;
            PasswordVisibleInput.Visibility = Visibility.Collapsed;
        }
    }

    private void LoadSettings()
    {
        AutoReconnectInput.IsChecked = _config.Settings.AutoReconnect;
        AutoHotspotInput.IsChecked = _config.Settings.AutoHotspot;
        AutoStartInput.IsChecked = _config.Settings.AutoStart;
        CloseToTrayInput.IsChecked = _config.Settings.CloseToTray;
        GatewayInput.Text = _config.Settings.Gateway;
        IntervalInput.Text = _config.Settings.IntervalSeconds.ToString();
        UpdateGuardSummary();
    }

    private void SaveSettings(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(IntervalInput.Text, out var interval) || interval < 3 || interval > 300)
        {
            MessageBox.Show("检测间隔必须是 3–300 之间的整数。", "设置", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { CampusAuthenticator.ValidateGateway(GatewayInput.Text.Trim()); }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "设置", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _config.Settings.AutoReconnect = AutoReconnectInput.IsChecked == true;
        _config.Settings.AutoHotspot = AutoHotspotInput.IsChecked == true;
        _config.Settings.AutoStart = AutoStartInput.IsChecked == true;
        _config.Settings.CloseToTray = CloseToTrayInput.IsChecked == true;
        _config.Settings.IntervalSeconds = interval;
        _config.Settings.Gateway = GatewayInput.Text.Trim();
        try { AutoStartService.Apply(_config.Settings.AutoStart); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageBox.Show($"开机启动设置失败：{ex.Message}", "设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (SaveConfig())
        {
            UpdateGuardSummary();
            AddLog(new LogEntry(DateTime.Now, "信息", "设置已保存并应用"));
        }
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
            MessageBox.Show($"保存失败：{ex.Message}", "CampusNet", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void UpdateGuardSummary()
    {
        GuardModeText.Text = _config.Settings.AutoReconnect ? "自动重连" : "仅检测状态";
        IntervalText.Text = $"每 {_config.Settings.IntervalSeconds} 秒检测";
    }

    private void ConnectNow(object sender, RoutedEventArgs e)
    {
        _monitor.ConnectNow();
        PauseButton.Content = "暂停重连";
        AddLog(new LogEntry(DateTime.Now, "信息", "已请求立即检测并连接"));
    }

    private void TogglePause(object sender, RoutedEventArgs e)
    {
        _monitor.SetPaused(!_monitor.IsPaused);
        PauseButton.Content = _monitor.IsPaused ? "恢复重连" : "暂停重连";
        AddLog(new LogEntry(DateTime.Now, "信息", _monitor.IsPaused ? "自动重连已暂停" : "自动重连已恢复"));
    }

    private void OpenHotspotSettings(object sender, RoutedEventArgs e) => HotspotService.OpenHotspotSettings();

    private void CopyLogs(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(string.Join(Environment.NewLine, _logs.Reverse().Select(x => x.Display)));
    }

    private void ClearLogs(object sender, RoutedEventArgs e)
    {
        _logs.Clear();
        DashboardLogItems.Clear();
    }

    private void WindowDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

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
        _monitor.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
    }

    private static string TrimTrayText(string text) => text.Length <= 63 ? text : text[..63];
}
