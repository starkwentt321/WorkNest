using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WorkNest.App.Services;
using WorkNest.App.Tests.Fakes;
using WorkNest.Application.Abstractions;
using Xunit;

namespace WorkNest.App.Tests;

public class BackgroundImageTests
{
    [Fact]
    public async Task Background_LoadApplyRestoreAndMissingFile_PreserveUsableState()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "WorkNest-background-" + Guid.NewGuid());
                Directory.CreateDirectory(directory);
                try
                {
                    // 初始化 WPF 的 pack URI 支持，但不启动真实应用或读取用户数据库。
                    _ = new FrameworkElement();
                    var store = new FakeSettingsService();
                    var background = new BackgroundImageSettings(store);
                    await background.LoadAsync();
                    var defaultImage = Assert.IsAssignableFrom<BitmapSource>(background.Source);
                    Assert.Equal(610, defaultImage.PixelWidth);
                    Assert.Equal(0.3, background.Opacity, 5);
                    Assert.Equal(BackgroundDisplayMode.Watermark, background.DisplayMode);

                    var path = Path.Combine(directory, "自定义图片.png");
                    using (var stream = File.Create(path))
                    {
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(defaultImage));
                        encoder.Save(stream);
                    }
                    background.ImagePath = path;
                    background.Transparency = 100;
                    background.SelectedMode = BackgroundDisplayMode.Fill;
                    await background.ApplyCommand.ExecuteAsync(null);
                    Assert.Equal(0, background.Opacity);
                    var restored = new BackgroundImageSettings(store);
                    await restored.LoadAsync();
                    Assert.Equal(path, restored.ImagePath);
                    Assert.Equal(0, restored.Opacity);
                    Assert.Equal(BackgroundDisplayMode.Fill, restored.DisplayMode);
                    Assert.Equal(Stretch.UniformToFill, restored.ImageStretch);

                    // 解码完成后不锁文件；下次启动文件缺失时回退，失败应用不得覆盖已保存配置。
                    File.Delete(path);
                    var currentSource = background.Source;
                    await background.ApplyCommand.ExecuteAsync(null);
                    Assert.Same(currentSource, background.Source);
                    Assert.Contains("失败", background.StatusText);
                    await restored.LoadAsync();
                    Assert.Contains("默认图片", restored.StatusText);
                    Assert.Equal(610, ((BitmapSource)restored.Source!).PixelWidth);

                    background.ResetCommand.Execute(null);
                    Assert.Equal(BackgroundDisplayMode.Watermark, background.SelectedMode);
                    background.Transparency = 0;
                    await background.ApplyCommand.ExecuteAsync(null);
                    Assert.Equal(1, background.Opacity);
                    var preference = Assert.IsType<BackgroundImageSettings.Preference>(store.Values[SettingKeys.BackgroundImage]);
                    Assert.Equal("", preference.ImagePath);
                    Assert.Equal(0, preference.Transparency);
                    Assert.Equal(BackgroundDisplayMode.Watermark, preference.DisplayMode);

                    // 手改出非法枚举值（如编号错位）时回落水印模式，透明度与路径保留
                    store.Values[SettingKeys.BackgroundImage] = new BackgroundImageSettings.Preference("", 40, (BackgroundDisplayMode)999);
                    await restored.LoadAsync();
                    Assert.Equal(BackgroundDisplayMode.Watermark, restored.DisplayMode);
                    Assert.Equal(40, restored.Transparency);
                    completion.SetResult();
                }
                catch (Exception ex) { completion.SetException(ex); }
                finally
                {
                    Directory.Delete(directory, true);
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void DisplayMode_DerivedPropertiesAndJson_MatchWallpaperSemantics()
    {
        // 派生属性只依赖枚举值，无需 WPF 线程；逐模式核对壁纸契合度语义
        var background = new BackgroundImageSettings(new FakeSettingsService());
        Assert.Equal(Stretch.Uniform, background.ImageStretch);
        Assert.Equal(HorizontalAlignment.Right, background.ImageHorizontalAlignment);
        Assert.Equal(VerticalAlignment.Bottom, background.ImageVerticalAlignment);
        Assert.Equal(300, background.ImageMaxSize);
        Assert.False(background.IsTiled);

        background.DisplayMode = BackgroundDisplayMode.Center;
        Assert.Equal(Stretch.None, background.ImageStretch);
        Assert.Equal(HorizontalAlignment.Center, background.ImageHorizontalAlignment);
        Assert.Equal(VerticalAlignment.Center, background.ImageVerticalAlignment);
        Assert.Equal(double.PositiveInfinity, background.ImageMaxSize);
        Assert.False(background.IsTiled);

        background.DisplayMode = BackgroundDisplayMode.Tile;
        Assert.True(background.IsTiled);

        background.DisplayMode = BackgroundDisplayMode.Fit;
        Assert.Equal(Stretch.Uniform, background.ImageStretch);
        Assert.Equal(HorizontalAlignment.Stretch, background.ImageHorizontalAlignment);
        Assert.Equal(VerticalAlignment.Stretch, background.ImageVerticalAlignment);

        background.DisplayMode = BackgroundDisplayMode.Fill;
        Assert.Equal(Stretch.UniformToFill, background.ImageStretch);

        background.DisplayMode = BackgroundDisplayMode.Stretch;
        Assert.Equal(Stretch.Fill, background.ImageStretch);

        // 枚举以名字符串入 JSON，后续调整成员顺序不影响历史数据
        var json = JsonSerializer.Serialize(new BackgroundImageSettings.Preference("", 70, BackgroundDisplayMode.Tile));
        Assert.Contains("\"DisplayMode\":\"Tile\"", json);
        var roundTrip = JsonSerializer.Deserialize<BackgroundImageSettings.Preference>(json);
        Assert.Equal(BackgroundDisplayMode.Tile, roundTrip!.DisplayMode);
    }
}
