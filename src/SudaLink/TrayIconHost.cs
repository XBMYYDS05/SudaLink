using System.Drawing;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace SudaLink;

/// <summary>
/// 托盘图标宿主。
/// <para>
/// 这里有个关键点：NotifyIcon 必须建在 WPF 的 UI 线程上。WinForms 的 NotifyIcon 内部
/// 会监听 Explorer 广播的 TaskbarCreated —— 开机时通知区往往比本进程还晚就绪，
/// 早一步注册的图标会被 Shell 丢掉；只有收到这条广播后重新注册，图标才真的出现。
/// 之前把图标放在独立 STA 线程 + 自己的消息循环里，异常还被吞掉，
/// 于是「收进托盘」之后窗口和图标一起消失，用户既看不到也退不掉。
/// </para>
/// </summary>
internal sealed class TrayIconHost : IDisposable
{
    public const int MaxTextLength = 63;

    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private bool _disposed;

    private TrayIconHost(Forms.NotifyIcon icon, Forms.ContextMenuStrip menu)
    {
        _icon = icon;
        _menu = menu;
    }

    /// <summary>创建失败（权限、Shell 异常等）返回 null，调用方据此退化为「缩到任务栏」。</summary>
    public static TrayIconHost? TryCreate(
        Action show,
        Action connectNow,
        Action toggleHotspot,
        Action togglePause,
        Action openLog,
        Action exit)
    {
        Forms.NotifyIcon? icon = null;
        Forms.ContextMenuStrip? menu = null;
        try
        {
            menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
            menu.Items.Add("打开 SudaLink", null, (_, _) => show());
            menu.Items.Add("立即连接", null, (_, _) => connectNow());
            menu.Items.Add("开启 / 关闭热点", null, (_, _) => toggleHotspot());
            menu.Items.Add("暂停 / 恢复自动重连", null, (_, _) => togglePause());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("查看运行日志", null, (_, _) => openLog());
            menu.Items.Add("退出 SudaLink", null, (_, _) => exit());

            icon = new Forms.NotifyIcon
            {
                Text = "SudaLink",
                Icon = LoadIcon(),
                ContextMenuStrip = menu,
                Visible = true
            };
            icon.MouseClick += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left)
                    show();
            };
            icon.DoubleClick += (_, _) => show();

            AppLog.Write("托盘图标已注册");
            return new TrayIconHost(icon, menu);
        }
        catch (Exception ex)
        {
            AppLog.Write($"托盘图标创建失败：{ex.GetType().Name} {ex.Message}");
            icon?.Dispose();
            menu?.Dispose();
            return null;
        }
    }

    public void SetText(string text)
    {
        if (_disposed)
            return;
        try
        {
            _icon.Text = text.Length <= MaxTextLength ? text : text[..MaxTextLength];
        }
        catch (Exception ex) when (ex is ArgumentException or ObjectDisposedException or InvalidOperationException)
        {
            // 图标已被销毁，状态文字跟不上就算了
        }
    }

    public void ShowHint(string title, string text)
    {
        if (_disposed)
            return;
        try
        {
            _icon.ShowBalloonTip(3000, title, text, Forms.ToolTipIcon.Info);
        }
        catch (Exception ex) when (ex is ArgumentException or ObjectDisposedException or InvalidOperationException)
        {
            // 系统关闭气泡通知时忽略
        }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executable) && Icon.ExtractAssociatedIcon(executable) is { } extracted)
                return extracted;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or COMException)
        {
            // 单文件发布时偶尔取不到关联图标，退回系统图标
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
    }
}
