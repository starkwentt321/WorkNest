using System.Windows;

namespace WorkNest.App.Services;

/// <summary>
/// 确认对话框抽象：把 MessageBox 从视图模型中解耦，便于 VM 状态测试（批次 B 完成标准）。
/// 生产实现使用 WPF MessageBox；测试注入假实现记录调用并返回预设结果。
/// </summary>
public interface IAppDialogs
{
    /// <summary>是/否确认；返回 true 表示用户确认。icon 由调用方指定以保持原有提示风格。</summary>
    bool Confirm(string title, string message, MessageBoxImage icon = MessageBoxImage.Question);
}

/// <summary>文件/文件夹选择抽象：文件对话框不进入视图模型，选择取消时返回 null。</summary>
public interface IFilePicker
{
    /// <summary>选择文件；用户取消返回 null。</summary>
    string? PickFile(string title);

    /// <summary>选择文件夹；用户取消返回 null。</summary>
    string? PickFolder(string title);

    /// <summary>选择保存位置（另存为）；用户取消返回 null。初始目录仅在实际存在时生效。</summary>
    string? PickSaveFile(string title, string filter, string initialFileName, string? initialDirectory);
}

/// <summary>
/// MessageBox 生产实现：模态是/否确认。视图模型不持有窗口引用，
/// owner 在实现内解析为当前活动窗口，保持对话框与发起窗口的模态归属（原内联调用带 WindowOwner 的语义）。
/// </summary>
public sealed class AppDialogs : IAppDialogs
{
    public bool Confirm(string title, string message, MessageBoxImage icon = MessageBoxImage.Question)
    {
        var owner = ActiveWindow();
        var result = owner is not null
            ? MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, icon)
            : MessageBox.Show(message, title, MessageBoxButton.YesNo, icon);
        return result == MessageBoxResult.Yes;
    }

    internal static Window? ActiveWindow() =>
        // WorkNest.Application 命名空间会遮蔽 System.Windows.Application，此处必须全限定
        System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
}

/// <summary>Win32 通用对话框生产实现：文件按 .exe 归类等判定仍留在调用方。owner 解析策略与 AppDialogs 一致。</summary>
public sealed class Win32FilePicker : IFilePicker
{
    public string? PickFile(string title) => PickFile(title, filter: null, owner: null);

    /// <summary>带过滤与归属窗口的文件选择；owner 为 null 时取当前活动窗口。</summary>
    public string? PickFile(string title, string? filter, Window? owner)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            CheckFileExists = true,
        };
        if (!string.IsNullOrEmpty(filter))
        {
            dialog.Filter = filter;
        }
        return Show(dialog, owner) ? dialog.FileName : null;
    }

    public string? PickFolder(string title) => PickFolder(title, owner: null);

    public string? PickFolder(string title, Window? owner)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = title,
        };
        return Show(dialog, owner) ? dialog.FolderName : null;
    }

    /// <summary>另存为对话框（导出配置）：默认文件名与初始目录由调用方按当前导出路径推导。</summary>
    public string? PickSaveFile(string title, string filter, string initialFileName, string? initialDirectory)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = initialFileName,
        };
        if (!string.IsNullOrEmpty(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }
        return Show(dialog, owner: null) ? dialog.FileName : null;
    }

    /// <summary>统一归属窗口：显式传入优先，否则取当前活动窗口，与原内联 ShowDialog 行为等价。</summary>
    private static bool Show(Microsoft.Win32.CommonDialog dialog, Window? owner) =>
        dialog.ShowDialog(owner ?? AppDialogs.ActiveWindow()) == true;
}
