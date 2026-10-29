using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace SudaLink;

public enum AutoStartMethod
{
    Registry,
    StartupFolder
}public sealed record AutoStartResult(bool Success, AutoStartMethod? Method, string? Error);

public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SudaLink";
    private const string ShortcutName = "SudaLink.lnk";

    /// <summary>
    /// 写入开机启动。优先注册表 Run 键；被安全策略拒绝时退回启动文件夹快捷方式
    /// （启动文件夹对普通用户始终可写，不依赖注册表权限）。
    /// </summary>
    public static AutoStartResult Apply(bool enabled)
    {
        Exception? registryError = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                ?? throw new InvalidOperationException("无法打开注册表启动项");
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

            TryDeleteShortcut();
            return new AutoStartResult(true, AutoStartMethod.Registry, null);
        }
        catch (Exception ex) when (IsAccessError(ex))
        {
            registryError = ex;
        }

        try
        {
            ApplyShortcut(enabled);
            return new AutoStartResult(true, AutoStartMethod.StartupFolder, null);
        }
        catch (Exception ex)
        {
            return new AutoStartResult(false, null,
                $"注册表与启动文件夹都不可写（{registryError?.Message ?? "未知原因"}；{ex.Message}）");
        }
    }

    private static void ApplyShortcut(bool enabled)
    {
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (string.IsNullOrWhiteSpace(folder))
            throw new InvalidOperationException("找不到 Windows 启动文件夹");

        var link = Path.Combine(folder, ShortcutName);
        if (!enabled)
        {
            if (File.Exists(link))
                File.Delete(link);
            return;
        }

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定程序路径");
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("系统缺少 WScript.Shell");

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(link);
        shortcut.TargetPath = executable;
        shortcut.Arguments = "--background";
        shortcut.WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty;
        shortcut.Description = "SudaLink 校园网守护";
        shortcut.Save();
    }

    private static void TryDeleteShortcut()
    {
        try
        {
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            if (string.IsNullOrWhiteSpace(folder))
                return;
            var link = Path.Combine(folder, ShortcutName);
            if (File.Exists(link))
                File.Delete(link);
        }
        catch (Exception ex) when (IsAccessError(ex))
        {
            // 启动文件夹里的残留不影响注册表启动，忽略即可
        }
    }

    private static bool IsAccessError(Exception ex) =>
        ex is UnauthorizedAccessException or System.Security.SecurityException or IOException;
}

/// <summary>
/// 把托盘图标固定到任务栏通知区。Windows 11 默认把新图标塞进「^ 隐藏区」，
/// 用户点最小化/关闭后就会以为程序消失了，所以启动时主动把 IsPromoted 置 1。
/// </summary>
internal static class TrayIconPromoter
{
    private const string SettingsKey = @"Control Panel\NotifyIconSettings";

    public static async Task PromoteAsync(string? executable, Action refresh)
    {
        if (string.IsNullOrWhiteSpace(executable))
            return;

        // 图标注册由 explorer 异步完成，前几次可能还查不到对应键
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (TryPromote(executable))
                return;

            await Task.Delay(600);
        }
    }

    private static bool TryPromote(string executable)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(SettingsKey, writable: true);
            if (root is null)
                return false;

            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name, writable: true);
                if (key?.GetValue("ExecutablePath") is not string path)
                    continue;
                if (!string.Equals(path, executable, StringComparison.OrdinalIgnoreCase))
                    continue;

                var promoted = key.GetValue("IsPromoted") as int? ?? 0;
                if (promoted == 1)
                    return true;

                key.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                return true;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 被策略限制时保持默认行为，不影响主功能
        }

        return false;
    }
}

public sealed record HotspotResult(bool Success, string Message)
{
    public static HotspotResult Ok(string message) => new(true, message);
    public static HotspotResult Fail(string message) => new(false, message);
}

/// <summary>通过 WinRT 控制 Windows 移动热点。</summary>
/// <remarks>
/// 移动热点 API 要求调用进程具备管理员权限，普通权限会直接抛「拒绝访问」。
/// 因此用户主动点击时按需弹出 UAC（子进程 --hotspot-on 执行）；
/// 后台自动开启不弹 UAC，只记录一次可操作提示，避免反复打扰。
/// </remarks>
public static class HotspotService
{
    private static readonly TimeSpan AutoQuietPeriod = TimeSpan.FromMinutes(10);

    private static DateTime _autoQuietUntil = DateTime.MinValue;
    private static string _autoQuietMessage = string.Empty;

    /// <summary>热点开关状态变化（true = 已开启），供界面同步按钮配色。</summary>
    public static event Action<bool>? StateChanged;

    public static bool IsElevated
    {
        get
        {
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
                    return false;
                try
                {
                    return IntegrityLevel(token) >= 0x3000; // High 及以上
                }
                finally
                {
                    CloseHandle(token);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }
    }

    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevelClass = 25;

    private static int IntegrityLevel(IntPtr token)
    {
        GetTokenInformation(token, TokenIntegrityLevelClass, IntPtr.Zero, 0, out var size);
        if (size <= 0)
            return 0;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevelClass, buffer, size, out _))
                return 0;

            var sid = Marshal.ReadIntPtr(buffer);          // TOKEN_MANDATORY_LABEL.Label.Sid
            var count = Marshal.ReadByte(sid, 1);           // SID sub-authority 数量
            return Marshal.ReadInt32(sid, 8 + (count - 1) * 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation
    {
        public int Elevated;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr token, int infoClass, IntPtr info, int length, out int returned);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    public static Task<bool> IsOnAsync()
    {
        if (!TryCreate(out var manager, out _, out _))
            return Task.FromResult(false);
        return Task.FromResult(manager!.TetheringOperationalState == TetheringOperationalState.On);
    }

    public static async Task<HotspotResult> EnsureOnAsync(
        CancellationToken cancellationToken = default, bool allowElevation = false)
    {
        if (!allowElevation && DateTime.UtcNow < _autoQuietUntil)
            return HotspotResult.Fail(_autoQuietMessage);

        if (!TryCreate(out var manager, out var error, out var denied))
            return denied
                ? await DeniedAsync(on: true, allowElevation, cancellationToken)
                : HotspotResult.Fail(error!);

        if (manager!.TetheringOperationalState == TetheringOperationalState.Unknown)
            return HotspotResult.Fail("系统未返回热点状态，请尝试在系统设置中手动开启");

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
            _autoQuietUntil = DateTime.MinValue;
            return HotspotResult.Ok($"热点已开启，{manager.ClientCount} 台设备已连接");
        }
        catch (OperationCanceledException)
        {
            return HotspotResult.Fail("热点开启请求已取消");
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            return await DeniedAsync(on: true, allowElevation, cancellationToken);
        }
        catch (Exception ex) when (IsKnown(ex))
        {
            return HotspotResult.Fail($"热点开启失败：{ex.Message}");
        }
    }

    public static async Task<HotspotResult> TurnOffAsync(
        CancellationToken cancellationToken = default, bool allowElevation = false)
    {
        if (!TryCreate(out var manager, out var error, out var denied))
            return denied
                ? await DeniedAsync(on: false, allowElevation, cancellationToken)
                : HotspotResult.Fail(error!);

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
            _autoQuietUntil = DateTime.MinValue;
            return HotspotResult.Ok("热点已关闭");
        }
        catch (OperationCanceledException)
        {
            return HotspotResult.Fail("热点关闭请求已取消");
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            return await DeniedAsync(on: false, allowElevation, cancellationToken);
        }
        catch (Exception ex) when (IsKnown(ex))
        {
            return HotspotResult.Fail($"热点关闭失败：{ex.Message}");
        }
    }

    private static async Task<HotspotResult> DeniedAsync(
        bool on, bool allowElevation, CancellationToken cancellationToken)
    {
        if (allowElevation && Environment.ProcessPath is { Length: > 0 } executable)
        {
            if (!IsElevated)
                return await RunElevatedAsync(executable, on, cancellationToken);
            return HotspotResult.Fail("热点操作被系统拒绝，请在系统设置里手动切换一次");
        }

        _autoQuietUntil = DateTime.UtcNow.Add(AutoQuietPeriod);
        _autoQuietMessage =
            "移动热点需要管理员权限：点击热点按钮可完成一次授权，或在设置里「以管理员身份重启」，之后即可自动开启";
        return HotspotResult.Fail(_autoQuietMessage);
    }

    /// <summary>以管理员身份启动自身子进程执行热点操作，结果写回临时文件。</summary>
    private static async Task<HotspotResult> RunElevatedAsync(
        string executable, bool on, CancellationToken cancellationToken)
    {
        var resultFile = Path.Combine(Path.GetTempPath(), $"sudalink-hotspot-{Guid.NewGuid():N}.json");
        try
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"{(on ? "--hotspot-on" : "--hotspot-off")} --result=\"{resultFile}\"",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(startInfo);
            if (process is null)
                return HotspotResult.Fail("无法启动管理员进程");

            await process.WaitForExitAsync(cancellationToken);
            if (File.Exists(resultFile))
            {
                var json = await File.ReadAllTextAsync(resultFile, cancellationToken);
                var parsed = JsonSerializer.Deserialize<HelperResult>(json);
                if (parsed is not null)
                    return parsed.Success
                        ? HotspotResult.Ok(parsed.Message)
                        : HotspotResult.Fail(parsed.Message);
            }

            return HotspotResult.Fail(on ? "热点开启失败：管理员进程未返回结果" : "热点关闭失败：管理员进程未返回结果");
        }
        catch (OperationCanceledException)
        {
            return HotspotResult.Fail("热点请求已取消");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return HotspotResult.Fail("已取消管理员授权，热点没有变化");
        }
        catch (Win32Exception ex)
        {
            return HotspotResult.Fail($"无法以管理员身份启动：{ex.Message}");
        }
        finally
        {
            try { File.Delete(resultFile); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed record HelperResult(bool Success, string Message);

    private static bool TryCreate(
        out NetworkOperatorTetheringManager? manager, out string? error, out bool accessDenied)
    {
        manager = null;
        error = null;
        accessDenied = false;
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
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            accessDenied = true;
            error = "无法访问移动热点：拒绝访问";
            return false;
        }
        catch (Exception ex) when (IsKnown(ex))
        {
            error = $"无法访问移动热点：{ex.Message}";
            return false;
        }
    }

    private static bool IsAccessDenied(Exception ex) =>
        ex is UnauthorizedAccessException ||
        (ex is System.Runtime.InteropServices.COMException com && (uint)com.HResult == 0x80070005);

    private static bool IsKnown(Exception ex) =>
        ex is InvalidOperationException or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException;

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
