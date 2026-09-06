using System.Windows;
using WorkNest.App.Services;
using WorkNest.App.Themes;

namespace WorkNest.App.Views;

/// <summary>重命名输入对话框：Enter 确认、Esc 取消；名称非法时在窗口内提示。</summary>
public partial class RenameWindow : Window
{
    private readonly string _originalPath;

    public RenameWindow(string originalPath)
    {
        InitializeComponent();
        // 弹窗独立加载主题资源，并让系统标题栏跟随当前主窗口主题。
        ThemeManager.Apply(this, ThemeManager.CurrentTheme);
        _originalPath = originalPath;
        var name = System.IO.Path.GetFileName(originalPath);
        NameBox.Text = name;
        PromptText.Text = $"重命名“{name}”为：";
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            // 资源管理器语义：文件默认只选中主名（不含扩展名），目录全选
            var extensionLength = System.IO.Path.GetExtension(name).Length;
            NameBox.Select(0, name.Length - (extensionLength > 0 ? extensionLength : 0));
        };
    }

    /// <summary>用户确认后的新名称（仅当 IsConfirmed 为 true 时有效）。</summary>
    public string? NewName { get; private set; }

    public bool IsConfirmed { get; private set; }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var newName = NameBox.Text.Trim();
        var error = FolderItemOps.Rename(_originalPath, newName);
        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        NewName = newName;
        IsConfirmed = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
