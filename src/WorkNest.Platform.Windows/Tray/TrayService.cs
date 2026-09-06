using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms; // NotifyIcon 来自 WinForms，别名规避与 WPF 的类型歧义
using WorkNest.Application.Abstractions;

namespace WorkNest.Platform.Windows.Tray;

/// <summary>
/// 托盘图标服务：图标由 GDI+ 动态生成（不引入资源文件），菜单「打开 WorkNest / 退出」，双击图标等同打开。
/// 线程约束：NotifyIcon 靠窗口消息分发点击与菜单事件，Initialize 必须在拥有消息泵的线程（WPF UI 线程）调用。
/// </summary>
public sealed class TrayService : ITrayService
{
    private const int IconSize = 32;

    private WinForms.NotifyIcon? _notifyIcon;
    private WinForms.ContextMenuStrip? _menu;
    private Icon? _icon;
    private IntPtr _iconHandle;

    public event EventHandler? OpenRequested;

    public event EventHandler? ExitRequested;

    public void Initialize()
    {
        if (_notifyIcon is not null)
        {
            return; // 幂等：重复 Initialize 不重复创建托盘图标
        }

        _icon = CreateAppIcon();

        _menu = new WinForms.ContextMenuStrip();
        var openItem = new WinForms.ToolStripMenuItem("打开 WorkNest");
        openItem.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        var exitItem = new WinForms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        _menu.Items.Add(openItem);
        _menu.Items.Add(exitItem);

        _notifyIcon = new WinForms.NotifyIcon
        {
            Text = "WorkNest",
            Icon = _icon,
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>GDI+ 生成托盘图标：圆角矩形填充 #0F6CBD，居中白色粗体 W。</summary>
    private Icon CreateAppIcon()
    {
        using var bitmap = new Bitmap(IconSize, IconSize);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            using var background = new SolidBrush(Color.FromArgb(0x0F, 0x6C, 0xBD));
            using var cornerPath = CreateRoundedRectPath(1f, 1f, IconSize - 2f, IconSize - 2f, 6f);
            graphics.FillPath(background, cornerPath);

            using var font = new Font("Segoe UI", 18f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var foreground = new SolidBrush(Color.White);
            var textSize = graphics.MeasureString("W", font);
            graphics.DrawString("W", font, foreground, (IconSize - textSize.Width) / 2f, (IconSize - textSize.Height) / 2f);
        }

        // GetHicon 创建的原生 HICON 需在 Dispose 中用 DestroyIcon 释放；FromHandle 包装器不拥有该句柄
        _iconHandle = bitmap.GetHicon();
        return Icon.FromHandle(_iconHandle);
    }

    private static GraphicsPath CreateRoundedRectPath(float x, float y, float width, float height, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2f;
        path.AddArc(x, y, diameter, diameter, 180f, 90f);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270f, 90f);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0f, 90f);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90f, 90f);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        // 先隐藏再释放，避免图标残留在托盘区直到鼠标划过
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _menu?.Dispose();
        _menu = null;

        _icon?.Dispose(); // 仅释放托管包装，原生句柄见下
        _icon = null;

        if (_iconHandle != IntPtr.Zero)
        {
            _ = DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
