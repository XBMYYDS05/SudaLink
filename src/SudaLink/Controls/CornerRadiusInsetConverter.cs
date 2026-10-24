using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SudaLink.Controls;

/// <summary>
/// 把一个 <see cref="CornerRadius"/> 往里收 <see cref="Inset"/> 像素。
/// 玻璃的内侧高光要贴着外缘走平行线，半径必须同比减小，否则圆角处会“飘”出去。
/// </summary>
public sealed class CornerRadiusInsetConverter : IValueConverter
{
    public double Inset { get; set; } = 2.5;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not CornerRadius radius)
            return new CornerRadius(0);

        var inset = parameter is string text && double.TryParse(text, NumberStyles.Float,
            CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : Inset;

        static double Shrink(double side, double by) => Math.Max(0, side - by);

        return new CornerRadius(
            Shrink(radius.TopLeft, inset), Shrink(radius.TopRight, inset),
            Shrink(radius.BottomRight, inset), Shrink(radius.BottomLeft, inset));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
