using System.Diagnostics;
using System.Text;

namespace SudaLink;

/// <summary>
/// 落盘日志。开机瞬间失败时界面上什么都看不到，只能靠这份文件还原现场。
/// 路径：%LOCALAPPDATA%\SudaLink\app.log，超过 512 KB 轮转一次。
/// </summary>
internal static class AppLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();

    public static string FolderPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SudaLink");

    public static string FilePath => Path.Combine(FolderPath, "app.log");

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(FolderPath);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes)
                    File.Move(FilePath, FilePath + ".1", true);

                File.AppendAllText(FilePath, line, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 日志写不进去也不能影响主流程
            }
        }
    }

    public static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            Process.Start(new ProcessStartInfo(FolderPath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // 打不开资源管理器就算了，用户仍可从地址栏自己找
        }
    }
}
