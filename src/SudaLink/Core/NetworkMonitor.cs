namespace SudaLink;

public sealed class NetworkMonitor : IDisposable
{
    private readonly PortalAuthenticator _authenticator;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _configLock = new(1, 1);
    private AppConfig _config;
    private Task? _worker;
    private bool _forceLogin;
    private bool _paused;
    private int _failureCount;
    private ConnectionStatus? _lastStatus;
    private bool _wasOnline;

    public NetworkMonitor(AppConfig config, PortalAuthenticator authenticator)
    {
        _config = config;
        _authenticator = authenticator;
    }

    public event Action<NetworkSnapshot>? StatusChanged;
    public event Action<LogEntry>? LogReceived;
    public bool IsPaused => _paused;

    public void Start()
    {
        if (_worker is not null)
            return;
        _worker = Task.Run(RunAsync);
        Log("网络守护已启动");
    }

    public void UpdateConfig(AppConfig config)
    {
        _configLock.Wait();
        try { _config = config; }
        finally { _configLock.Release(); }
        Wake();
    }

    public void ConnectNow()
    {
        _forceLogin = true;
        _paused = false;
        Wake();
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        Wake();
    }

    public void Wake()
    {
        if (_wake.CurrentCount == 0)
            _wake.Release();
    }

    private async Task RunAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            var delay = 10;
            try
            {
                var config = await GetConfigAsync();
                var force = _forceLogin;
                _forceLogin = false;

                if (_paused)
                {
                    Publish(new NetworkSnapshot(ConnectionStatus.Paused,
                        "已停止自动检测，点击「立即连接」可手动重试", DateTime.Now));
                    delay = config.Settings.IntervalSeconds;
                }
                else
                {
                    // 已联网时不必反复提示“检测中”，避免状态文案闪烁
                    if (_lastStatus != ConnectionStatus.Online)
                        Publish(new NetworkSnapshot(ConnectionStatus.Checking, "正在检测网络连接", DateTime.Now));
                    var online = await _authenticator.CheckInternetAsync(_cancellation.Token);                    if (online)
                    {
                        _failureCount = 0;
                        Publish(new NetworkSnapshot(ConnectionStatus.Online, "网络连接正常", DateTime.Now,
                            config.CurrentAccount?.Username));
                        if (!_wasOnline && config.Settings.AutoHotspot)
                        {
                            Log("网络已恢复，正在开启移动热点");
                            var hotspot = await HotspotService.EnsureOnAsync(_cancellation.Token);
                            Log(hotspot.Message, hotspot.Success ? "信息" : "警告");
                        }
                        _wasOnline = true;
                        delay = config.Settings.IntervalSeconds;
                    }
                    else
                    {
                        _wasOnline = false;
                        if (!config.Settings.AutoReconnect && !force)
                        {
                            Publish(new NetworkSnapshot(ConnectionStatus.Offline, "当前未联网，自动重连已关闭", DateTime.Now));
                            delay = config.Settings.IntervalSeconds;
                        }
                        else
                        {
                            delay = await TryLoginAsync(config);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                break;
            }
            catch (ArgumentException ex)
            {
                _failureCount++;
                Publish(new NetworkSnapshot(ConnectionStatus.Failed, ex.Message, DateTime.Now));
                Log(ex.Message, "错误");
                delay = Backoff(10);
            }
            catch (Exception ex)
            {
                _failureCount++;
                Publish(new NetworkSnapshot(ConnectionStatus.Failed, "网络检测出现异常，将自动恢复", DateTime.Now));
                Log($"检测异常：{ex.GetType().Name}，将稍后重试", "错误");
                delay = Backoff(10);
            }

            Publish(new NetworkSnapshot(_lastStatus ?? ConnectionStatus.Checking,
                "等待下次检测", DateTime.Now, NextCheckSeconds: delay));
            await WaitAsync(delay);
        }
    }

    private async Task<int> TryLoginAsync(AppConfig config)
    {
        bool reachable;
        try
        {
            reachable = await _authenticator.IsGatewayReachableAsync(config.Settings.Gateway, _cancellation.Token);
        }
        catch (ArgumentException)
        {
            throw;
        }

        if (!reachable)
        {
            Publish(new NetworkSnapshot(ConnectionStatus.NoCampusNetwork,
                "未检测到苏大认证网关，请连接 SUDA_WIFI 或有线校园网", DateTime.Now));
            LogOnce(ConnectionStatus.NoCampusNetwork, "校园网网关不可达");
            return Math.Max(config.Settings.IntervalSeconds, 15);
        }

        var accounts = config.OrderedEnabledAccounts().Select(a => a.Clone()).ToList();
        if (accounts.Count == 0)
        {
            Publish(new NetworkSnapshot(ConnectionStatus.Failed, "没有可用账号，请先添加并启用账号", DateTime.Now));
            LogOnce(ConnectionStatus.Failed, "没有已启用的账号");
            return Math.Max(config.Settings.IntervalSeconds, 30);
        }

        Publish(new NetworkSnapshot(ConnectionStatus.Authenticating, "正在认证校园网", DateTime.Now));
        var reasons = new List<string>();
        foreach (var account in accounts)
        {
            _cancellation.Token.ThrowIfCancellationRequested();
            Publish(new NetworkSnapshot(ConnectionStatus.Authenticating,
                $"正在尝试 {account.Username}", DateTime.Now, account.Username));
            Log($"正在使用 {account.OperatorName} 账号 {MaskAccount(account.Username)} 登录");
            var result = await _authenticator.LoginAsync(config.Settings.Gateway, account, _cancellation.Token);
            if (result.Success)
            {
                await Task.Delay(800, _cancellation.Token);
                if (await _authenticator.CheckInternetAsync(_cancellation.Token))
                {
                    _failureCount = 0;
                    _wasOnline = true;
                    Publish(new NetworkSnapshot(ConnectionStatus.Online, "认证成功，网络已连接", DateTime.Now,
                        account.Username));
                    Log($"认证成功：{MaskAccount(account.Username)}");
                    if (config.Settings.AutoHotspot)
                    {
                        var hotspot = await HotspotService.EnsureOnAsync(_cancellation.Token);
                        Log(hotspot.Message, hotspot.Success ? "信息" : "警告");
                    }
                    return config.Settings.IntervalSeconds;
                }
                reasons.Add("认证后联网检测未通过");
            }
            else
            {
                reasons.Add(result.Message);
                Log($"账号 {MaskAccount(account.Username)} 登录失败：{result.Message}", "警告");
            }
        }

        _failureCount++;
        var reason = reasons.LastOrDefault() ?? "全部账号尝试失败";
        Publish(new NetworkSnapshot(ConnectionStatus.Failed, reason, DateTime.Now));
        return Backoff(config.Settings.IntervalSeconds);
    }

    private async Task<AppConfig> GetConfigAsync()
    {
        await _configLock.WaitAsync(_cancellation.Token);
        try { return _config; }
        finally { _configLock.Release(); }
    }

    private int Backoff(int baseSeconds)
    {
        var exponent = Math.Min(Math.Max(_failureCount - 1, 0), 4);
        return Math.Min(120, Math.Max(baseSeconds, 5) * (int)Math.Pow(2, exponent));
    }

    private async Task WaitAsync(int seconds)
    {
        var delay = Task.Delay(TimeSpan.FromSeconds(seconds), _cancellation.Token);
        var wake = _wake.WaitAsync(_cancellation.Token);
        await Task.WhenAny(delay, wake);
    }

    private void Publish(NetworkSnapshot snapshot)
    {
        _lastStatus = snapshot.Status;
        StatusChanged?.Invoke(snapshot);
    }

    private void LogOnce(ConnectionStatus status, string message)
    {
        if (_lastStatus != status)
            Log(message, "警告");
    }

    private void Log(string message, string level = "信息") =>
        LogReceived?.Invoke(new LogEntry(DateTime.Now, level, message));

    private static string MaskAccount(string username)
    {
        var at = username.IndexOf('@');
        var suffix = at >= 0 ? username[at..] : string.Empty;
        var name = at >= 0 ? username[..at] : username;
        if (name.Length <= 4)
            return new string('*', name.Length) + suffix;
        return name[..2] + new string('*', name.Length - 4) + name[^2..] + suffix;
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        Wake();
        _authenticator.Dispose();
        _wake.Dispose();
        _configLock.Dispose();
        _cancellation.Dispose();
    }
}
