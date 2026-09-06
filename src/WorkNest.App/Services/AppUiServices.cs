using System.Windows;

namespace WorkNest.App.Services;

/// <summary>
/// 确认对话框抽象：把 MessageBox 从视图模型中解耦，便于 VM 状态测试（批次 B 完成标准）。
/// 生产实现使用 WPF MessageBox；测试注入假实现记录调用并返回预设结果。
/// </summary>
public interface IAppDialogs
{
    /// <summary>是/否确认；返回 true 表示用户确认。</summary>
    bool Confirm(string title, string message);
}

/// <summary>文件/文件夹选择抽象：文件对话框不进入视图模型，选择取消时返回 null。</summary>
public interface IFilePicker
{
    /// <summary>选择文件；用户取消返回 null。</summary>
    string? PickFile(string title);

    /// <summary>选择文件夹；用户取消返回 null。</summary>
    string? PickFolder(string title);
}

/// <summary>MessageBox 生产实现：模态是/否确认（与原内联调用行为一致）。</summary>
public sealed class AppDialogs : IAppDialogs
{
    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}

/// <summary>Win32 通用对话框生产实现：文件按 .exe 归类等判定仍留在调用方。</summary>
public sealed class Win32FilePicker : IFilePicker
{
    public string? PickFile(string title)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            CheckFileExists = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickFolder(string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = title,
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
