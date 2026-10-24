using System.Diagnostics;
using Microsoft.Win32;

namespace CampusNetWpf;

public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CampusNetWpf";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey);
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

public static class HotspotService
{
    public static async Task<string> TryStartAsync(CancellationToken cancellationToken)
    {
        const string script = "$ErrorActionPreference='Stop'; " +
            "Add-Type -AssemblyName System.Runtime.WindowsRuntime; " +
            "$null=[Windows.Networking.Connectivity.NetworkInformation,Windows.Networking.Connectivity,ContentType=WindowsRuntime]; " +
            "$null=[Windows.Networking.Connectivity.NetworkOperatorTetheringManager,Windows.Networking.Connectivity,ContentType=WindowsRuntime]; " +
            "$p=[Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile(); " +
            "if($null -eq $p){throw '没有可用的网络配置文件'}; " +
            "$m=[Windows.Networking.Connectivity.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($p); " +
            "if($m.TetheringOperationalState -eq 0){$null=$m.StartTetheringAsync()}; " +
            "'已提交移动热点开启请求'";
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动热点组件");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();
            if (process.ExitCode != 0)
                return $"热点开启失败：{(error.Length > 0 ? error : "系统拒绝请求")}";
            return output.Length > 0 ? output : "已提交移动热点开启请求";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "热点开启请求超时";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"热点开启失败：{ex.Message}";
        }
    }

    public static void OpenHotspotSettings()
    {
        Process.Start(new ProcessStartInfo("ms-settings:network-mobilehotspot") { UseShellExecute = true });
    }
}
