using System.Windows;

namespace SudaLink;

/// <summary>外观主题切换：替换调色板字典，界面靠 DynamicResource 自动刷新。</summary>
public static class ThemeService
{
    public const string Dark = "Dark";
    public const string Light = "Light";

    public static string Current { get; private set; } = Dark;

    public static void Apply(string? theme)
    {
        var target = string.Equals(theme, Light, StringComparison.OrdinalIgnoreCase) ? Light : Dark;
        Current = target;

        if (Application.Current is not { } app)
            return;

        var source = new Uri($"Themes/Palette.{target}.xaml", UriKind.Relative);
        var merged = app.Resources.MergedDictionaries;

        var stale = merged
            .Where(d => d.Source?.OriginalString.Contains("Palette.", StringComparison.Ordinal) == true)
            .ToList();
        foreach (var dictionary in stale)
            merged.Remove(dictionary);

        merged.Insert(0, new ResourceDictionary { Source = source });

        // 壁纸与染色都换了，玻璃需要重新取背景位图
        Controls.GlassPanel.InvalidateBackdrop();
    }
}
