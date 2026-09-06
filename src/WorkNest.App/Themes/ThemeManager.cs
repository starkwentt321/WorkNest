using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WorkNest.App.Themes;

/// <summary>
/// 主题管理：窗口 XAML 默认合并 Light.xaml，运行时按设置替换合并字典实现浅/深切换（决策 13/18/30）。
/// 不修改 App.xaml，资源全部落在各窗口的 Window.Resources 上。
/// 系统标题栏由 DWM 绘制，须经 IMMERSIVE_DARK_MODE 属性才会跟随深色主题。
/// </summary>
public static class ThemeManager
{
    public const string Light = "light";
    public const string Dark = "dark";

    private static readonly Uri LightSource = new("pack://application:,,,/Themes/Light.xaml");
    private static readonly Uri DarkSource = new("pack://application:,,,/Themes/Dark.xaml");

    /// <summary>当前生效主题名；新建窗口构造时用它保持一致。</summary>
    public static string CurrentTheme { get; private set; } = Light;

    public static Uri ResolveSource(string? theme) =>
        string.Equals(theme, Dark, StringComparison.OrdinalIgnoreCase) ? DarkSource : LightSource;

    /// <summary>把窗口合并字典中的主题字典替换为目标主题，保持其余资源（转换器等）不动。</summary>
    public static void Apply(Window window, string? theme)
    {
        var themeName = Normalize(theme);
        CurrentTheme = themeName;
        var dicts = window.Resources.MergedDictionaries;
        var target = new ResourceDictionary { Source = ResolveSource(themeName) };
        // 先移除已存在的主题字典（Light/Dark 二选一），再插到最前保证 token 优先解析
        for (var i = dicts.Count - 1; i >= 0; i--)
        {
            if (IsThemeDictionary(dicts[i]))
            {
                dicts.RemoveAt(i);
            }
        }
        dicts.Insert(0, target);

        ApplyTitleBarTheme(window, themeName);
    }

    /// <summary>系统标题栏跟随主题（构造期句柄未创建时挂 SourceInitialized 兜底）。</summary>
    private static void ApplyTitleBarTheme(Window window, string themeName)
    {
        // 先退订再订阅，保证任意窗口上只挂一个处理器
        window.SourceInitialized -= OnSourceInitializedForTitleBar;
        window.SourceInitialized += OnSourceInitializedForTitleBar;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            SetImmersiveDarkMode(hwnd, themeName == Dark);
        }
    }

    private static void OnSourceInitializedForTitleBar(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            SetImmersiveDarkMode(hwnd, CurrentTheme == Dark);
        }
    }

    /// <summary>DWM 沉浸式深色标题栏：属性 20 在 Win10 2004+/Win11 生效，旧 19 顺带设置无害。</summary>
    private static void SetImmersiveDarkMode(IntPtr hwnd, bool enabled)
    {
        try
        {
            var value = enabled ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, 19, ref value, sizeof(int));
        }
        catch
        {
            // DWM 不可用时保持系统默认标题栏，不影响功能
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>对所有已打开窗口统一换肤（设置窗口切换主题时调用）。</summary>
    public static void ApplyToAll(string? theme)
    {
        CurrentTheme = Normalize(theme);
        // 全限定：WorkNest.Application 命名空间与 System.Windows.Application 类型名冲突，须显式指明
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return;
        }
        foreach (var window in app.Windows.OfType<Window>().ToList())
        {
            Apply(window, CurrentTheme);
        }
    }

    private static string Normalize(string? theme) =>
        string.Equals(theme, Dark, StringComparison.OrdinalIgnoreCase) ? Dark : Light;

    private static bool IsThemeDictionary(ResourceDictionary dict) =>
        dict.Source is { } src &&
        (src.OriginalString.EndsWith("/Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) ||
         src.OriginalString.EndsWith("/Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase));
}
