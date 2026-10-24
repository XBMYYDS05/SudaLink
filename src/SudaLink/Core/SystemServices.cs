using System.Diagnostics;
using Microsoft.Win32;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace SudaLink;

public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SudaLink";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey);
        // 清理旧版本注册项，避免重复启动或指向已删除的程序
        key.DeleteValue("CampusNetWpf", false);
        if (enabled)
        {
            var path = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径");
            key.SetValue(ValueName, $"\"{path}\" --background");
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }
}

public sealed record HotspotResult(bool Success, string Message)
{
    public static HotspotResult Ok(string message) => new(true, message);
    public static HotspotResult Fail(string message) => new(false, message);
}

/// <summary>通过 WinRT 直接控制 Windows 移动热点（不依赖外部脚本，异步调用会被真正等待）。</summary>
public static class HotspotService
{
    /// <summary>热点开关状态变化（true = 已开启），供界面同步按钮配色。</summary>
    public static event Action<bool>? StateChanged;

    public static Task<bool> IsOnAsync()
    {
        if (!TryCreate(out var manager, out _))
            return Task.FromResult(false);
        return Task.FromResult(manager!.TetheringOperationalState == TetheringOperationalState.On);
    }

    public static async Task<HotspotResult> EnsureOnAsync(CancellationToken cancellationToken = default)
    {
        if (!TryCreate(out var manager, out var error))
            return HotspotResult.Fail(error!);

        if (manager!.TetheringOperationalState == TetheringOperationalState.Unknown)
            return HotspotResult.Fail("系统未返回热点状态，请尝试在设置中手动开启");

        if (manager.TetheringOperationalState == TetheringOperationalState.On)
        {
            Raise(true);
            return HotspotResult.Ok($"热点已开启，{manager.ClientCount} 台设备已连接");
        }

        try
        {
            var operation = manager.StartTetheringAsync().AsTask(cancellationToken);
            var result = await operation;
            if (result.Status != TetheringOperationStatus.Success)
                return HotspotResult.Fail($"热点开启失败：{Describe(result.Status)}");

            Raise(true);
            return HotspotResult.Ok($"热点已开启，{manager.ClientCount} 台设备已连接");
        }
        catch (OperationCanceledException)
        {
            return HotspotResult.Fail("热点开启请求已取消");
        }
        catch (Exception ex) when (IsKnown(ex))
        {
            return HotspotResult.Fail($"热点开启失败：{ex.Message}");
        }
    }

    public static async Task<HotspotResult> TurnOffAsync(CancellationToken cancellationToken = default)
    {
        if (!TryCreate(out var manager, out var error))
            return HotspotResult.Fail(error!);

        if (manager!.TetheringOperationalState == TetheringOperationalState.Off)
        {
            Raise(false);
            return HotspotResult.Ok("热点已关闭");
        }

        try
        {
            var operation = manager.StopTetheringAsync().AsTask(cancellationToken);
            var result = await operation;
            if (result.Status != TetheringOperationStatus.Success)
                return HotspotResult.Fail($"热点关闭失败：{Describe(result.Status)}");

            Raise(false);
            return HotspotResult.Ok("热点已关闭");
        }
        catch (OperationCanceledException)
        {
            return HotspotResult.Fail("热点关闭请求已取消");
        }
        catch (Exception ex) when (IsKnown(ex))
        {
            return HotspotResult.Fail($"热点关闭失败：{ex.Message}");
        }
    }

    private static bool TryCreate(out NetworkOperatorTetheringManager? manager, out string? error)
    {
        manager = null;
        error = null;
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null)
            {
                error = "当前没有可用的网络连接，无法共享热点";
                return false;
            }
            manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
            return true;
        }
        catch (Exception ex) when (IsKnown(ex))
        {
            error = $"无法访问移动热点：{ex.Message}";
            return false;
        }
    }

    private static bool IsKnown(Exception ex) =>
        ex is InvalidOperationException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException;

    private static void Raise(bool on)
    {
        var handler = StateChanged;
        if (handler is null)
            return;
        try { handler(on); }
        catch (Exception ex) when (ex is InvalidOperationException) { }
    }

    public static void OpenSystemSettings()
    {
        Process.Start(new ProcessStartInfo("ms-settings:network-mobilehotspot") { UseShellExecute = true });
    }

    private static string Describe(TetheringOperationStatus status) => status switch
    {
        TetheringOperationStatus.Success => "成功",
        TetheringOperationStatus.OperationInProgress => "系统正在处理上一次请求，请稍后重试",
        TetheringOperationStatus.WiFiDeviceOff => "Wi-Fi 已关闭，请先打开 Wi-Fi",
        TetheringOperationStatus.MobileBroadbandDeviceOff => "移动宽带已关闭",
        TetheringOperationStatus.EntitlementCheckTimeout => "运营商权限校验超时",
        TetheringOperationStatus.EntitlementCheckFailure => "系统未通过热点权限校验",
        _ => status.ToString()
    };
}
