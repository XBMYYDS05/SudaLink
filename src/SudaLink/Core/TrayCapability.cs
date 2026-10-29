using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SudaLink;

/// <summary>
/// 判断本进程能不能真的把图标放进通知区。
/// <para>
/// 依据是一路查到的落点：托盘图标的注册走 Shell_NotifyIcon，而 UIPI 会直接拒绝
/// 完整性级别低于 Explorer 的进程（返回 ERROR_ACCESS_DENIED = 5），且 WinForms 的
/// NotifyIcon 完全不上报这个失败 —— 于是图标"注册成功"却永远不出现，
/// 用户把窗口收起来之后既看不到窗口也看不到图标。
/// 本机实测：Explorer 跑在 High（0x3000），从桌面/资源管理器/命令行拉起的
/// SudaLink 一律是 Medium（0x2000），图标从来没出现过。
/// </para>
/// 所以在建图标之前先比一次完整性：低于 Explorer 就直接认定托盘不可用，
/// 让"最小化"退回到任务栏，而不是把窗口藏进黑洞。
/// </summary>
internal static class TrayCapability
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    public static bool CanRegister(out string reason)
    {
        var shell = HighestShellIntegrity();
        var self = CurrentIntegrity();
        AppLog.Write($"托盘能力检测：本进程 {Describe(self)}，Explorer {Describe(shell)}");

        if (shell <= 0 || self <= 0)
        {
            reason = "读不到进程完整性，按可用处理";
            return true;
        }

        if (self >= shell)
        {
            reason = $"完整性 {Describe(self)} ≥ Explorer {Describe(shell)}";
            return true;
        }

        reason = $"本进程完整性 {Describe(self)} 低于 Explorer {Describe(shell)}，" +
                 "系统会拒绝托盘图标注册（UIPI 拦截 Shell_NotifyIcon）";
        return false;
    }

    private static int HighestShellIntegrity()
    {
        var best = 0;
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            var handle = IntPtr.Zero;
            try
            {
                // 查高完整性进程用 LIMITED_INFORMATION 才拿得到句柄，PROCESS_QUERY_INFORMATION
                // 会被 UIPI 直接拒掉（.NET 的 Process.Handle 走的就是后者）
                handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
                if (handle != IntPtr.Zero)
                    best = Math.Max(best, Of(handle));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                if (handle != IntPtr.Zero)
                    CloseHandle(handle);
                process.Dispose();
            }
        }

        return best;
    }

    private static int CurrentIntegrity()
    {
        using var process = Process.GetCurrentProcess();
        return Of(process.Handle);
    }

    private static int Of(IntPtr process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token))
            return 0;

        try
        {
            // 第一次调用只问长度，必然返回 false，别把它当失败
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var size);
            if (size <= 0)
                return 0;

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out _))
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
        finally
        {
            CloseHandle(token);
        }
    }

    private static string Describe(int level) => level switch
    {
        0x0000 => "Untrusted",
        0x1000 => "Low",
        0x2000 => "Medium",
        0x2100 => "MediumPlus",
        0x3000 => "High",
        0x4000 => "System",
        _ => $"0x{level:X}"
    };

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr token, int infoClass, IntPtr info, int length, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
