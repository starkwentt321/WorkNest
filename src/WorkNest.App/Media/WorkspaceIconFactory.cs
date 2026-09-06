using System.Collections.Concurrent;
using System.Windows.Media;

namespace WorkNest.App.Media;

/// <summary>
/// 工作区图标工厂：按自动分配的颜色生成文件夹风格图标（决策 39/F01）。
/// 主体用工作区色、前板提亮、描边加深；同色复用缓存，实例全部冻结以便跨线程绑定。
/// </summary>
public static class WorkspaceIconFactory
{
    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource Create(string? colorText) =>
        Cache.GetOrAdd(colorText ?? string.Empty, static key => Build(key));

    private static ImageSource Build(string colorText)
    {
        var baseColor = Parse(colorText) ?? Color.FromRgb(0x0F, 0x6C, 0xBD);
        var frontColor = Blend(baseColor, Colors.White, 0.18);
        var outlineColor = Blend(baseColor, Colors.Black, 0.3);

        // 文件夹背板（带标签折角）与前板，坐标系约 32×32
        var back = Geometry.Parse("M2.5,7.5 L12.5,7.5 L15.5,10.5 L29.5,10.5 L29.5,26.5 L2.5,26.5 Z");
        var front = Geometry.Parse("M2.5,13.5 L29.5,13.5 L29.5,26.5 L2.5,26.5 Z");

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing
        {
            Geometry = back,
            Brush = new SolidColorBrush(baseColor),
            Pen = new Pen(new SolidColorBrush(outlineColor), 1),
        });
        group.Children.Add(new GeometryDrawing
        {
            Geometry = front,
            Brush = new SolidColorBrush(frontColor),
            Pen = new Pen(new SolidColorBrush(outlineColor), 1),
        });

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static Color? Parse(string colorText)
    {
        if (colorText.Length == 0)
        {
            return null;
        }
        try
        {
            return (Color)ColorConverter.ConvertFromString(colorText);
        }
        catch
        {
            return null;
        }
    }

    private static Color Blend(Color from, Color to, double ratio)
    {
        static byte Lerp(double a, double b, double t) => (byte)Math.Round(a + (b - a) * t);
        return Color.FromRgb(
            Lerp(from.R, to.R, ratio),
            Lerp(from.G, to.G, ratio),
            Lerp(from.B, to.B, ratio));
    }
}
