using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkNest.Application.Abstractions;

namespace WorkNest.App.Services;

/// <summary>背景编辑与应用状态；先解码和保存成功，再替换主窗口背景。</summary>
public partial class BackgroundImageSettings(ISettingsService settings) : ObservableObject
{
    public sealed record Preference(string ImagePath = "", double Transparency = 70);

    [ObservableProperty] private string _imagePath = "";
    [ObservableProperty] private double _transparency = 70;
    [ObservableProperty] private ImageSource? _source;
    [ObservableProperty] private double _opacity = 0.3;
    [ObservableProperty] private string? _statusText;

    public async Task LoadAsync()
    {
        var preference = await settings.GetAsync(SettingKeys.BackgroundImage, new Preference());
        ImagePath = preference.ImagePath ?? "";
        Transparency = double.IsFinite(preference.Transparency) ? Math.Clamp(preference.Transparency, 0, 100) : 70;
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
            var source = await ReadImageAsync(path);
            await settings.SetAsync(SettingKeys.BackgroundImage, new Preference(path, transparency));
            Source = source;
            Opacity = 1 - transparency / 100;
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
