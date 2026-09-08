using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WorkNest.App.Converters;

/// <summary>true → Collapsed；用于反向显隐（如编辑时隐藏列表）。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>null → Visible；用于图标提取失败时回退显示类型字形（决策 106）。</summary>
public sealed class NullToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>置顶菜单文案：true = "取消置顶"，否则 "置顶"（F19）。</summary>
public sealed class PinMenuHeaderConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "取消置顶" : "置顶";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>bool 反转；RadioButton 两个分支共用一个布尔（关闭行为）。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

/// <summary>背景平铺单元：图片解码尺寸（DIP）矩形；空源返回空矩形让画刷不绘制。</summary>
public sealed class ImageTileViewportConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is BitmapSource bitmap && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0
            ? new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight)
            : Rect.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>工作区颜色字符串 → Brush；解析失败回退强调色，同色复用冻结实例。</summary>
public sealed class ColorToBrushConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, SolidColorBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string colorText || colorText.Length == 0)
        {
            return null;
        }
        return Cache.GetOrAdd(colorText, static key =>
        {
            Color color;
            try
            {
                color = (Color)ColorConverter.ConvertFromString(key);
            }
            catch
            {
                // 颜色串异常时不中断列表渲染，回退主题强调色
                color = Color.FromRgb(0x0F, 0x6C, 0xBD);
            }
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        });
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
