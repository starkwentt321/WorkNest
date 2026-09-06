using System.Collections.Concurrent;
using System.Drawing; // ExtractAssociatedIcon 位于 GDI+ 的 System.Drawing.Icon（非 WinForms 命名空间）
using System.Windows;
using System.Windows.Interop; // Imaging 工具类在 System.Windows.Interop，而非 System.Windows.Media.Imaging
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WorkNest.Domain;

namespace WorkNest.Platform.Windows.Icons;

/// <summary>资源图标提取契约（本层定义，界面层经 DI 获取）。</summary>
public interface IResourceIconProvider
{
    /// <summary>提取目标图标；失败返回 null，界面回退到资源类型默认图标（决策 106）。</summary>
    ImageSource? GetIcon(ResourceType type, string target);
}

/// <summary>按“类型+目标”缓存提取结果；仅处理 File/Program，Directory/Website 交给类型默认图标。</summary>
public sealed class ResourceIconProvider : IResourceIconProvider
{
    // null 同样入缓存：对提取失败（如路径已失效）的目标不反复尝试，避免列表滚动时重复 IO
    private readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ImageSource? GetIcon(ResourceType type, string target)
    {
        if (type is not (ResourceType.File or ResourceType.Program) || string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        // 缓存键含类型前缀：同一目标路径以不同类型展示时（历史数据）互不污染
        var key = $"{(int)type}|{target}";
        return _cache.GetOrAdd(key, static (_, path) => ExtractIcon(path), target);
    }

    private static ImageSource? ExtractIcon(string target)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(target);
            if (icon is null)
            {
                return null;
            }

            // HIcon → WPF 位图是数据拷贝；using 结束即释放 GDI 图标句柄，避免句柄泄漏
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze(); // 冻结后才允许跨线程访问（后台提取 → UI 绑定）
            return source;
        }
        catch
        {
            // 决策 106：提取失败返回 null 用类型默认图标，不阻止资源保存与展示
            return null;
        }
    }
}
