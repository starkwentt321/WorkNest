using System.Windows;
using System.Windows.Media;
using WorkNest.App.Services;
using WorkNest.Domain;
using WorkNest.Platform.Windows.Icons;

namespace WorkNest.App.Tests.Fakes;

/// <summary>
/// 确认对话框假实现：返回可设置的预设值并记录每次调用的标题与消息，
/// 用于断言未保存修改确认的触发与结果（R04）。
/// </summary>
public sealed class FakeDialogs : IAppDialogs
{
    /// <summary>下一次 Confirm 的返回值（默认 true：非必要不阻断）。</summary>
    public bool ConfirmResult { get; set; } = true;

    public List<(string Title, string Message)> ConfirmCalls { get; } = [];

    public bool Confirm(string title, string message, MessageBoxImage icon = MessageBoxImage.Question)
    {
        ConfirmCalls.Add((title, message));
        return ConfirmResult;
    }
}

/// <summary>
/// 文件/文件夹选择器假实现：返回可设置的预设值，null 表示用户取消；记录调用标题。
/// </summary>
public sealed class FakeFilePicker : IFilePicker
{
    /// <summary>PickFile 的预设返回值（null = 取消）。</summary>
    public string? FileResult { get; set; }

    /// <summary>PickFolder 的预设返回值（null = 取消）。</summary>
    public string? FolderResult { get; set; }

    /// <summary>PickSaveFile 的预设返回值（null = 取消）。</summary>
    public string? SaveFileResult { get; set; }

    public List<string> PickFileCalls { get; } = [];

    public List<string> PickFolderCalls { get; } = [];

    public List<(string Title, string Filter, string InitialFileName)> PickSaveFileCalls { get; } = [];

    public string? PickFile(string title)
    {
        PickFileCalls.Add(title);
        return FileResult;
    }

    public string? PickFolder(string title)
    {
        PickFolderCalls.Add(title);
        return FolderResult;
    }

    public string? PickSaveFile(string title, string filter, string initialFileName, string? initialDirectory)
    {
        PickSaveFileCalls.Add((title, filter, initialFileName));
        return SaveFileResult;
    }
}

/// <summary>图标提取假实现：恒返回 null，走类型默认字形回退，测试不触碰真实文件。</summary>
public sealed class FakeIconProvider : IResourceIconProvider
{
    public ImageSource? GetIcon(ResourceType type, string target) => null;
}
