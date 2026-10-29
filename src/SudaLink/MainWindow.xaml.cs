using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using Forms = System.Windows.Forms;

namespace SudaLink;

public partial class MainWindow : Window
{
    // 深色底上的语义色：比浅色主题提亮一档，才能在玻璃上"发光"
    private static readonly Color OnlineColor = Color.FromRgb(0x2F, 0xE3, 0x9A);
    private static readonly Color WarnColor = Color.FromRgb(0xFF, 0xA5, 0x3D);
    private static readonly Color BusyColor = Color.FromRgb(0x6E, 0x9B, 0xFF);
    private static readonly Color ErrorColor = Color.FromRgb(0xFF, 0x5F, 0x73);
    private static readonly Color IdleColor = Color.FromRgb(0x6C, 0x7A, 0x93);

    private static readonly Brush OnlineBrush = Freeze(OnlineColor);
    private static readonly Brush OfflineBrush = Freeze(WarnColor);
    private static readonly Brush BusyBrush = Freeze(BusyColor);
    private static readonly Brush ErrorBrush = Freeze(ErrorColor);
    private static readonly Brush IdleBrush = Freeze(IdleColor);

    private readonly AppConfig _config;
    private readonly ConfigStore _store;
    private readonly NetworkMonitor _monitor;
    private readonly ObservableCollection<LogEntry> _logs = [];
    private TrayIconHost? _tray;
    private readonly string? _startupWarning;
    private Account? _editingAccount;
    private bool _exitRequested;
    private bool _cleanedUp;
    private bool _runtimeStarted;
    private bool _hotspotBusy;
    private bool _loadingSettings;
    private bool _hotspotOn;
    private bool _pulsing;
    private bool _trayHintShown;
    private Storyboard? _pulse;

    private const int HotKeyId = 0x53;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public bool StartInTray { get; init; }

    /// <summary>托盘图标是否真的建起来了。没建起来时不允许"启动了却没有入口"。</summary>
    public bool HasTray => _tray is not null;

    public MainWindow(AppConfig config, ConfigStore store, string? startupWarning)
    {
        InitializeComponent();
        WindowFx.Attach(this);
        Shell.SizeChanged += (_, _) => UpdateFrameClip();
        UpdateFrame();
        // 在 Win32 层拦截关闭，避免 WPF Closing/Closed 链路里任何一处抢先 Shutdown
        SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(this) is HwndSource source)
            {
                source.AddHook(WndProc);
                // 全局唤回热键：Ctrl+Alt+S（托盘不可见时的兜底入口）
                RegisterHotKey(source.Handle, HotKeyId, ModControl | ModAlt, 0x53);
            }
        };
        // 系统关机/注销时允许窗口正常关闭，否则会被"缩到后台"逻辑挡住
        Application.Current.SessionEnding += (_, _) => _exitRequested = true;
        _config = config;
        _store = store;
        _startupWarning = startupWarning;
        _monitor = new NetworkMonitor(_config, new PortalAuthenticator());
        _monitor.StatusChanged += OnNetworkStatus;
        _monitor.LogReceived += AddLog;
        StartTray();
        // Windows 11 默认把新托盘图标藏在「^」里，这里主动固定到通知区
        _ = TrayIconPromoter.PromoteAsync(Environment.ProcessPath, () => { });
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
        InitializeRuntime();

        // 兜底：后台启动且托盘可用时窗口本不该显示，万一被外部 Show 出来，立刻收回去
        if (StartInTray && HasTray && !_exitRequested)
        {
            Hide();
            ShowInTaskbar = false;
        }
    }

    /// <summary>后台启动：只创建窗口句柄（热键、托盘、消息钩子都用得上），但不显示窗口。</summary>
    public void StartHidden()
    {
        ShowInTaskbar = false;
        _ = new WindowInteropHelper(this).EnsureHandle();
        InitializeRuntime();
    }

    /// <summary>
    /// 托盘用不了时的后台启动：直接以最小化姿态出现在任务栏上。
    /// 既不会在开机时弹一个窗口出来，也不会变成一个找不到入口的幽灵进程。
    /// </summary>
    public void StartMinimized()
    {
        ShowInTaskbar = true;
        WindowState = WindowState.Minimized;
        Show();
        InitializeRuntime();
    }

    /// <summary>
    /// 后台服务与界面初值只跑一次。前台启动走 Loaded，后台启动走 StartHidden，
    /// 两条路径都要能独立完成初始化 —— 以前这些活全塞在 Loaded 里，隐藏启动就一条都没执行。
    /// </summary>
    private void InitializeRuntime()
    {
        if (_runtimeStarted)
            return;
        _runtimeStarted = true;

        AddLog(new LogEntry(DateTime.Now, "信息", "SudaLink 已在后台守护校园网"));
        if (!string.IsNullOrWhiteSpace(_startupWarning))
            AddLog(new LogEntry(DateTime.Now, "警告", _startupWarning));
        ApplyBackdrop();
        UpdatePauseButton();
        UpdateLogFooter();
        _ = RefreshHotspotAsync();
        _monitor.Start();
    }

    private void StartTray()
    {
        if (!TrayCapability.CanRegister(out var reason))
        {
            AppLog.Write($"托盘不可用：{reason}");
            AddLog(new LogEntry(DateTime.Now, "警告",
                "系统不允许本程序注册托盘图标，最小化将缩到任务栏"));
            return;
        }

        _tray = TrayIconHost.TryCreate(
            show: () => Dispatcher.Invoke(ShowFromTray),
            connectNow: () => Dispatcher.Invoke(() =>
            {
                _monitor.ConnectNow();
                AddLog(new LogEntry(DateTime.Now, "信息", "已请求立即检测并连接"));
            }),
            toggleHotspot: () => Dispatcher.InvokeAsync(StartHotspotAsync),
            togglePause: () => Dispatcher.Invoke(() =>
            {
                _monitor.SetPaused(!_monitor.IsPaused);
                UpdatePauseButton();
            }),
            openLog: AppLog.OpenFolder,
            exit: () => Dispatcher.Invoke(ExitApplication));

        if (_tray is null)
        {
            AddLog(new LogEntry(DateTime.Now, "警告", "托盘图标没有注册成功，最小化时将退回到任务栏"));
            AppLog.Write("托盘不可用，收起到后台会退化为缩到任务栏");
        }
    }

    public void ShowFromTray()
    {
        ShowInTaskbar = true;
        if (!IsVisible)
            Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
        // 窗口可能是在隐藏状态下完成布局的，唤回时补一次绘制，避免停在黑底
        InvalidateVisual();
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

            var (title, shortText, brush, glow) = snapshot.Status switch
            {
                ConnectionStatus.Online => ("网络已连接", "已连接", OnlineBrush, OnlineColor),
                ConnectionStatus.Offline => ("网络未连接", "未联网", OfflineBrush, WarnColor),
                ConnectionStatus.NoCampusNetwork => ("未在校园网环境", "网关不可达", OfflineBrush, WarnColor),
                ConnectionStatus.Authenticating => ("正在认证", "认证中", BusyBrush, BusyColor),
                ConnectionStatus.Failed => ("连接需要处理", "认证失败", ErrorBrush, ErrorColor),
                ConnectionStatus.Paused => ("自动重连已暂停", "已暂停", IdleBrush, IdleColor),
                _ => ("正在检测网络", "检测中", BusyBrush, BusyColor)
            };

            SoftChange(StatusMessage, snapshot.Message);
            SoftChange(StatusTitle, title);
            StatusDot.Fill = brush;
            AnimateGlow(glow);
            _tray?.SetText($"SudaLink · {shortText}");

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
        PauseButton.Style = (Style)FindResource(paused ? "TileOff" : "TileOn");
        PauseButton.ToolTip = paused ? "点击恢复自动重连" : "点击暂停自动重连";
    }

    private void OnHotspotStateChanged(bool on) =>
        Dispatcher.InvokeAsync(() => ApplyHotspotState(on));

    private void ApplyHotspotState(bool on)
    {
        _hotspotOn = on;
        HotspotText.Text = on ? "热点已开启" : "热点未开启";
        HotspotButton.Style = (Style)FindResource(on ? "TileOn" : "TileOff");
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
            UpdateLogFooter();
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
        TabOverview.IsChecked = page == "Overview";
        TabAccounts.IsChecked = page == "Accounts";
        TabSettings.IsChecked = page == "Settings";
        if (changed)
            AnimateIn(target);
    }

    /// <summary>开发期截图：把三个页面各渲染一张 PNG（供 <c>--render=</c> 使用）。</summary>
    public async Task RenderSnapshotsAsync(string directory, double waitSeconds = 0.9)
    {
        Directory.CreateDirectory(directory);
        var suffix = ThemeService.Current == ThemeService.Light ? "light" : "dark";
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(waitSeconds, 0.2)));
        foreach (var (page, name) in new[]
                 {
                     ("Overview", "overview"), ("Accounts", "accounts"), ("Settings", "settings")
                 })
        {
            SetPage(page);
            await Task.Delay(520);
            SaveSnapshot(Path.Combine(directory, $"{suffix}-{name}.png"));
        }
    }

    private void SaveSnapshot(string path)
    {
        if (Content is not FrameworkElement root || root.ActualWidth < 1)
            return;

        const double scale = 2.0;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth * scale),
            (int)Math.Ceiling(root.ActualHeight * scale),
            RTB_DPI * scale, RTB_DPI * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);

        // 真机窗口由 DWM 圆角（8 逻辑像素），截图也裁出同样的圆角，别让文档里的窗口是方的
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, bitmap.Width, bitmap.Height);
            dc.PushClip(new RectangleGeometry(rect, WindowCornerRadius * scale, WindowCornerRadius * scale));
            dc.DrawImage(bitmap, rect);
            dc.Pop();
        }

        var rounded = new RenderTargetBitmap(
            bitmap.PixelWidth, bitmap.PixelHeight,
            RTB_DPI * scale, RTB_DPI * scale, PixelFormats.Pbgra32);
        rounded.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rounded));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private const double RTB_DPI = 96;

    /// <summary>窗口四角的圆角半径（与 Windows 11 DWM 圆角一致）。</summary>
    private const double WindowCornerRadius = 8.0;

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
            new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
    }

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
        GatewayInput.Text = _config.Settings.Gateway;
        SelectInterval(_config.Settings.IntervalSeconds);
        _loadingSettings = true;
        ThemeLight.IsChecked = ThemeService.Current == ThemeService.Light;
        ThemeDark.IsChecked = ThemeService.Current == ThemeService.Dark;
        _loadingSettings = false;
    }

    private void ThemeChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
            return;

        var theme = ThemeLight.IsChecked == true ? ThemeService.Light : ThemeService.Dark;
        if (_config.Settings.Theme == theme)
            return;

        _config.Settings.Theme = theme;
        ThemeService.Apply(theme);
        ApplyBackdrop();
        SaveConfig();
        AddLog(new LogEntry(DateTime.Now, "信息",
            $"外观已切换为{(theme == ThemeService.Light ? "浅色" : "深色")}"));
    }

    /// <summary>按当前主题装载壁纸。用 OnLoad 同步解码，玻璃才不会取到上一张图。</summary>
    private void ApplyBackdrop()
    {
        var name = ThemeService.Current == ThemeService.Light ? "backdrop-light.jpg" : "backdrop-dark.jpg";
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri($"pack://application:,,,/Assets/{name}", UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            BackdropImage.Source = bitmap;
        }
        catch (IOException)
        {
            // 壁纸丢了就退回纯色，界面仍然可用
        }

        Controls.GlassPanel.InvalidateBackdrop();
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
        var result = AutoStartService.Apply(enabled);
        if (!result.Success)
        {
            AutoStartSwitch.IsChecked = !enabled;
            MessageBox.Show($"开机启动设置失败：{result.Error}", "设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _config.Settings.AutoStart = enabled;
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
        HotspotStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x6C, 0x7A, 0x93));
        HotspotStatus.Text = _hotspotOn ? "正在关闭热点…" : "正在开启热点…";
        try
        {
            var result = _hotspotOn
                ? await HotspotService.TurnOffAsync(allowElevation: true)
                : await HotspotService.EnsureOnAsync(allowElevation: true);
            HotspotStatus.Foreground = new SolidColorBrush(result.Success ? OnlineColor : ErrorColor);
            HotspotStatus.Text = result.Message;
            AddLog(new LogEntry(DateTime.Now, result.Success ? "信息" : "警告", result.Message));
        }
        finally
        {
            _hotspotBusy = false;
        }
    }

    private void RelaunchElevated(object sender, RoutedEventArgs e)
    {
        if (HotspotService.IsElevated)
        {
            AddLog(new LogEntry(DateTime.Now, "信息", "当前已是管理员权限，无需重启"));
            return;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            AddLog(new LogEntry(DateTime.Now, "警告", "无法确定程序路径，请手动以管理员身份运行"));
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas"
            });
            AddLog(new LogEntry(DateTime.Now, "信息", "正在以管理员身份重启…"));
            ExitApplication();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            AddLog(new LogEntry(DateTime.Now, "信息", "已取消管理员授权"));
        }
        catch (Exception ex)
        {
            AddLog(new LogEntry(DateTime.Now, "警告", $"无法以管理员身份重启：{ex.Message}"));
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

    /// <summary>窗口圆角。比内部卡片的 22~26 略小，视觉上才像"外壳包住内容"。</summary>
    private const double WindowRadius = 24;

    private void OnWindowStateChanged(object sender, EventArgs e) => UpdateFrame();

    private void UpdateFrame()
    {
        UpdateFrameClip();
    }

    /// <summary>把内容裁成圆角。WPF 的 Border 不会替子元素裁剪，只能自己上几何 Clip。</summary>
    private void UpdateFrameClip()
    {
        if (Shell.ActualWidth < 1 || Shell.ActualHeight < 1)
        {
            Shell.Clip = null;
            return;
        }

        var radius = WindowState == WindowState.Maximized ? 0 : WindowRadius;
        Shell.Clip = radius > 0
            ? new RectangleGeometry(new Rect(0, 0, Shell.ActualWidth, Shell.ActualHeight), radius, radius)
            : null;
    }

    private void MinimizeWindow(object sender, RoutedEventArgs e) => CollapseToBackground();

    private void MaximizeWindow(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>点叉就是退出。想留在后台请用最小化（或托盘菜单）。</summary>
    private void CloseWindow(object sender, RoutedEventArgs e) => ExitApplication();

    /// <summary>
    /// 收起到托盘。托盘图标是唯一入口，图标没建起来时必须退化为缩到任务栏，
    /// 否则用户既看不到窗口也看不到图标。
    /// </summary>
    private void CollapseToBackground()
    {
        if (_tray is not null)
        {
            Hide();
            ShowInTaskbar = false;
            ShowTrayHintOnce();
        }
        else
        {
            WindowState = WindowState.Minimized;
            ShowInTaskbar = true;
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested)
            return;

        // 正常关闭：交给 WPF 走完 Closed，Cleanup 在 OnWindowClosed 里做
        _exitRequested = true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        const int WM_RESPAWN_SHELL = 0x8000 + 7;

        if (msg == WM_HOTKEY && wParam.ToInt32() == HotKeyId)
        {
            ShowFromTray();
            handled = true;
            return IntPtr.Zero;
        }

        // 维护用：由本进程（普通权限）拉起 explorer，修复高权限 Explorer 导致托盘注册失败
        if (msg == WM_RESPAWN_SHELL)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
            }

            handled = true;
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private void ShowTrayHintOnce()
    {
        if (_trayHintShown)
            return;
        _trayHintShown = true;
        _tray?.ShowHint("SudaLink 仍在后台运行",
            "网络守护没有中断。点托盘图标可唤回窗口，右键可以退出。");
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
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
                UnregisterHotKey(handle, HotKeyId);
        }
        catch (InvalidOperationException)
        {
        }
        if (_cleanedUp)
            return;
        _cleanedUp = true;
        HotspotService.StateChanged -= OnHotspotStateChanged;
        _monitor.Dispose();
        _tray?.Dispose();
        _tray = null;
    }

    private void UpdateLogFooter() =>
        LogsFooter.Text = $"网关 {_config.Settings.Gateway} · 每 {_config.Settings.IntervalSeconds} 秒检测 · 共 {_logs.Count} 条记录";

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>状态卡的辉光随状态平滑变色，避免硬切。</summary>
    private void AnimateGlow(Color target)
    {
        if (StatusCard.Glow.Equals(target))
            return;

        StatusCard.BeginAnimation(Controls.GlassPanel.GlowProperty,
            new ColorAnimation(target, TimeSpan.FromMilliseconds(520))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }
}
