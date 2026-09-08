using System.IO;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkNest.Application.Abstractions;

namespace WorkNest.App.Services;

/// <summary>
/// 背景显示方式，语义对齐 Windows 壁纸“契合度”。JSON 存枚举名字符串，
/// 调整成员顺序不影响历史数据；Watermark 是历史默认（右下角水印）。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BackgroundDisplayMode
{
    /// <summary>右下角水印：等比、最大 300×300 DIP 贴右下角（历史默认）。</summary>
    Watermark,
    /// <summary>居中：原尺寸居中，超出窗口部分被裁剪。</summary>
    Center,
    /// <summary>平铺：按图片解码尺寸重复铺满窗口。</summary>
    Tile,
    /// <summary>适应：等比缩放到完整可见，不足处留空。</summary>
    Fit,
    /// <summary>填充：等比缩放到铺满窗口，溢出部分裁剪、不变形。</summary>
    Fill,
    /// <summary>拉伸：忽略宽高比铺满窗口，可能变形。</summary>
    Stretch,
}

/// <summary>背景编辑与应用状态；先解码和保存成功，再替换主窗口背景。</summary>
public partial class BackgroundImageSettings(ISettingsService settings) : ObservableObject
{
    public sealed record Preference(string ImagePath = "", double Transparency = 70, BackgroundDisplayMode DisplayMode = BackgroundDisplayMode.Watermark);

    [ObservableProperty] private string _imagePath = "";
    [ObservableProperty] private double _transparency = 70;
    /// <summary>设置页下拉的选择值；点“应用背景”校验保存成功后才提交到 <see cref="DisplayMode"/>。</summary>
    [ObservableProperty] private BackgroundDisplayMode _selectedMode = BackgroundDisplayMode.Watermark;
    /// <summary>当前生效的显示方式，主窗口背景层据此绑定渲染属性。</summary>
    [ObservableProperty] private BackgroundDisplayMode _displayMode = BackgroundDisplayMode.Watermark;
    [ObservableProperty] private ImageSource? _source;
    [ObservableProperty] private double _opacity = 0.3;
    [ObservableProperty] private string? _statusText;

    /// <summary>设置页“显示方式”下拉选项；实例属性以便 XAML 直接绑定。</summary>
    public IReadOnlyList<KeyValuePair<BackgroundDisplayMode, string>> ModeOptions { get; } =
    [
        new(BackgroundDisplayMode.Watermark, "右下角"),
        new(BackgroundDisplayMode.Center, "居中"),
        new(BackgroundDisplayMode.Tile, "平铺"),
        new(BackgroundDisplayMode.Fit, "适应"),
        new(BackgroundDisplayMode.Fill, "填充"),
        new(BackgroundDisplayMode.Stretch, "拉伸"),
    ];

    /// <summary>非平铺模式的缩放方式；平铺由 ImageBrush 专用图层渲染，不走此属性。</summary>
    public Stretch ImageStretch => DisplayMode switch
    {
        BackgroundDisplayMode.Stretch => Stretch.Fill,
        BackgroundDisplayMode.Fill => Stretch.UniformToFill,
        BackgroundDisplayMode.Center => Stretch.None,
        _ => Stretch.Uniform,
    };

    public HorizontalAlignment ImageHorizontalAlignment => DisplayMode switch
    {
        BackgroundDisplayMode.Watermark => HorizontalAlignment.Right,
        BackgroundDisplayMode.Center => HorizontalAlignment.Center,
        _ => HorizontalAlignment.Stretch,
    };

    public VerticalAlignment ImageVerticalAlignment => DisplayMode switch
    {
        BackgroundDisplayMode.Watermark => VerticalAlignment.Bottom,
        BackgroundDisplayMode.Center => VerticalAlignment.Center,
        _ => VerticalAlignment.Stretch,
    };

    /// <summary>水印模式限制 300×300，其余模式解除限制以铺满整个背景层。</summary>
    public double ImageMaxSize => DisplayMode == BackgroundDisplayMode.Watermark ? 300 : double.PositiveInfinity;

    /// <summary>平铺模式切换到 ImageBrush 图层，隐藏 Image 元素。</summary>
    public bool IsTiled => DisplayMode == BackgroundDisplayMode.Tile;

    partial void OnDisplayModeChanged(BackgroundDisplayMode value)
    {
        // 主窗口只绑派生属性，模式切换时逐一通知
        OnPropertyChanged(nameof(ImageStretch));
        OnPropertyChanged(nameof(ImageHorizontalAlignment));
        OnPropertyChanged(nameof(ImageVerticalAlignment));
        OnPropertyChanged(nameof(ImageMaxSize));
        OnPropertyChanged(nameof(IsTiled));
    }

    public async Task LoadAsync()
    {
        var preference = await settings.GetAsync(SettingKeys.BackgroundImage, new Preference());
        ImagePath = preference.ImagePath ?? "";
        Transparency = double.IsFinite(preference.Transparency) ? Math.Clamp(preference.Transparency, 0, 100) : 70;
        // 旧数据缺字段得到默认值；手改出非法枚举值时回落水印模式，路径与透明度保留
        var mode = Enum.IsDefined(preference.DisplayMode) ? preference.DisplayMode : BackgroundDisplayMode.Watermark;
        DisplayMode = mode;
        SelectedMode = mode;
        try
        {
            Source = await ReadImageAsync(ImagePath);
        }
        catch (Exception)
        {
            // 用户图片被移动或损坏时回退内置图片，保留原路径供设置页检查。
            Source = await ReadImageAsync("");
            StatusText = "原背景图片无法读取，已显示默认图片，请重新选择。";
        }
        Opacity = 1 - Transparency / 100;
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        try
        {
            var path = ImagePath.Trim();
            var transparency = double.IsFinite(Transparency) ? Math.Clamp(Transparency, 0, 100) : 70;
            var displayMode = Enum.IsDefined(SelectedMode) ? SelectedMode : BackgroundDisplayMode.Watermark;
            var source = await ReadImageAsync(path);
            await settings.SetAsync(SettingKeys.BackgroundImage, new Preference(path, transparency, displayMode));
            Source = source;
            Opacity = 1 - transparency / 100;
            DisplayMode = displayMode;
            SelectedMode = displayMode;
            StatusText = "背景已应用并保存。";
        }
        catch (Exception ex)
        {
            StatusText = $"应用背景失败，当前背景保持不变：{ex.Message}";
        }
    }

    [RelayCommand]
    private void Reset()
    {
        ImagePath = "";
        Transparency = 70;
        SelectedMode = BackgroundDisplayMode.Watermark;
        StatusText = "已选择默认图片，点击“应用背景”保存。";
    }

    internal static Task<BitmapSource> ReadImageAsync(string path)
    {
        // 内置资源由 WPF 加载；用户文件在后台解码并关闭流，避免锁住图片或阻塞界面。
        if (string.IsNullOrWhiteSpace(path))
        {
            var image = new BitmapImage(new Uri("pack://application:,,,/WorkNest;component/Assets/DefaultBackground.png"));
            image.Freeze();
            return Task.FromResult<BitmapSource>(image);
        }
        return Task.Run<BitmapSource>(() =>
        {
            using var stream = File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 1024;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        });
    }
}
