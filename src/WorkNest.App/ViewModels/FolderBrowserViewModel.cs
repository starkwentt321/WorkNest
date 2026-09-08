using System.Collections.ObjectModel;
using System.IO; // WindowsDesktop SDK 的隐式 using 不含 System.IO，必须显式引入
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkNest.Application.Abstractions;
using WorkNest.Application.Dtos;

namespace WorkNest.App.ViewModels;

/// <summary>文件夹浏览面板中的单个子项（子目录或文件），展示列由只读属性提供。</summary>
public sealed class FolderEntryViewModel
{
    public string Name { get; }

    public string FullPath { get; }

    public bool IsDirectory { get; }

    public string TypeName => IsDirectory ? "文件夹" : "文件";

    /// <summary>类型默认字形（与资源列表同源：文件夹 E8B7 / 文件 E7C3）。</summary>
    public string Glyph => IsDirectory ? "\uE8B7" : "\uE7C3";

    /// <summary>文件夹不显示大小；文件按 B/KB/MB/GB 自适应。</summary>
    public string SizeDisplay => IsDirectory ? string.Empty : FormatSize(Length);

    public string ModifiedDisplay => LastWriteTime.ToString("yyyy-MM-dd HH:mm");

    private long Length { get; }

    private DateTime LastWriteTime { get; }

    public FolderEntryViewModel(FolderEntryDto dto)
    {
        Name = dto.Name;
        FullPath = dto.FullPath;
        IsDirectory = dto.IsDirectory;
        Length = dto.Length;
        LastWriteTime = dto.LastWriteTime;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.#} GB",
    };
}

/// <summary>面包屑导航段：显示名 + 对应目录路径（根段显示名取资源名）。</summary>
public sealed record BreadcrumbSegment(string Name, string Path);

/// <summary>
/// 文件夹应用内浏览面板（设置 behavior.folderInlineBrowse 开启时，双击目录资源进入）。
/// 支持逐级深入、上级、前进/后退历史导航（对应鼠标 XButton2/XButton1）与面包屑跳转；
/// 子目录双击深入、子文件双击经 FileOpenRequested 交给主窗口用系统默认程序打开。
/// </summary>
public partial class FolderBrowserViewModel : ObservableObject
{
    private readonly IFolderBrowserService _browserService;

    /// <summary>浏览根：被双击的资源目标路径；上级导航到根为止，不越出资源范围。</summary>
    public string RootPath { get; }

    /// <summary>根段显示名（资源名），用于面包屑首段。</summary>
    public string RootName { get; }

    /// <summary>所属资源 Id，供主窗口在资源被移除时联动关闭面板。</summary>
    public int ResourceId { get; }

    // 前进/后退历史：只记录访问过的目录；Open 截断前进分支，Back/Forward 移动索引
    private readonly List<string> _history = [];

    private int _historyIndex = -1;

    public FolderBrowserViewModel(IFolderBrowserService browserService, string rootPath, string rootName, int resourceId)
    {
        _browserService = browserService;
        RootPath = rootPath;
        RootName = rootName;
        ResourceId = resourceId;
    }

    /// <summary>当前目录子项；导航时整体替换实例并单次通知，避免大目录（上限 5000）逐条 Add 的逐项 CollectionChanged。</summary>
    public ObservableCollection<FolderEntryViewModel> Entries { get; private set; } = [];

    private IReadOnlyList<FolderEntryViewModel> _allEntries = [];
    private bool _listingTruncated;

    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value) => FilterEntries();

    private void FilterEntries()
    {
        // 只筛选当前目录的已加载子项，保留原顺序；清空时无需再次访问磁盘。
        var tokens = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Entries = new ObservableCollection<FolderEntryViewModel>(_allEntries.Where(entry =>
            tokens.All(token => entry.Name.Contains(token, StringComparison.OrdinalIgnoreCase))));
        OnPropertyChanged(nameof(Entries));
        StatusText = tokens.Length == 0 ? $"{Entries.Count} 项" : $"找到 {Entries.Count} 项 / 共 {_allEntries.Count} 项";
        if (_listingTruncated) StatusText += $"（目录过大，仅搜索前 {_allEntries.Count} 项）";
    }

    public ObservableCollection<BreadcrumbSegment> Breadcrumbs { get; } = [];

    /// <summary>加载中标记：期间禁用导航命令，防止并发枚举互相覆盖列表。</summary>
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _currentPath = string.Empty;

    /// <summary>当前目录条目数与截断提示。</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    public bool CanGoUp => _history.Count > 0 && !string.Equals(
        CurrentPath, RootPath, StringComparison.OrdinalIgnoreCase);

    public bool CanGoBack => _historyIndex > 0;

    public bool CanGoForward => _historyIndex < _history.Count - 1;

    /// <summary>面板内无法处理的错误（目录不可读等）上报主窗口横幅。</summary>
    public event Action<string>? ErrorOccurred;

    /// <summary>双击子文件：由主窗口用系统默认程序打开（面板自身不启动进程，便于测试）。</summary>
    public event Action<string>? FileOpenRequested;

    /// <summary>用户请求在系统资源管理器中打开当前目录（旧入口保留，Shell 调用由主窗口执行）。</summary>
    public event Action<string>? ExplorerOpenRequested;

    [RelayCommand]
    private void OpenInExplorer() => ExplorerOpenRequested?.Invoke(CurrentPath);

    /// <summary>进入浏览面板的首屏加载（枚举根目录）。</summary>
    public Task InitializeAsync() => NavigateCoreAsync(RootPath, commitHistory: true);

    [RelayCommand]
    private async Task OpenEntryAsync(FolderEntryViewModel? entry)
    {
        if (entry is null || IsBusy)
        {
            return;
        }
        if (entry.IsDirectory)
        {
            await OpenAsync(entry.FullPath);
        }
        else
        {
            FileOpenRequested?.Invoke(entry.FullPath);
        }
    }

    [RelayCommand]
    private Task GoUpAsync() => CanGoUp && !IsBusy
        ? OpenAsync(Path.GetDirectoryName(CurrentPath.TrimEnd(Path.DirectorySeparatorChar))!)
        : Task.CompletedTask;

    [RelayCommand]
    private async Task GoBackAsync()
    {
        if (!CanGoBack || IsBusy)
        {
            return;
        }
        _historyIndex--;
        await NavigateCoreAsync(_history[_historyIndex], commitHistory: false);
    }

    [RelayCommand]
    private async Task GoForwardAsync()
    {
        if (!CanGoForward || IsBusy)
        {
            return;
        }
        _historyIndex++;
        await NavigateCoreAsync(_history[_historyIndex], commitHistory: false);
    }

    [RelayCommand]
    private Task RefreshAsync() => IsBusy ? Task.CompletedTask : NavigateCoreAsync(CurrentPath, commitHistory: false);

    // 文件系统操作的刷新调度：删除/重命名由 Shell 或文件 API 完成，
    // 立即刷新可能枚举到旧状态；重命名内联编辑等待更久。重复触发时重置计时。
    private const int DefaultRefreshDelayMs = 600;

    private const int RenameRefreshDelayMs = 2500;

    private System.Windows.Threading.DispatcherTimer? _shellCommandRefreshTimer;

    /// <summary>删除/重命名等文件系统操作完成后调用，延迟刷新当前目录。</summary>
    public void ScheduleRefreshAfterShellCommand(bool isRename)
    {
        if (_shellCommandRefreshTimer is null)
        {
            _shellCommandRefreshTimer = new System.Windows.Threading.DispatcherTimer();
            _shellCommandRefreshTimer.Tick += OnShellCommandRefreshTick;
        }
        _shellCommandRefreshTimer.Interval = TimeSpan.FromMilliseconds(isRename ? RenameRefreshDelayMs : DefaultRefreshDelayMs);
        _shellCommandRefreshTimer.Stop();
        _shellCommandRefreshTimer.Start();
    }

    private async void OnShellCommandRefreshTick(object? sender, EventArgs e)
    {
        ((System.Windows.Threading.DispatcherTimer)sender!).Stop();
        await RefreshAsync();
    }

    /// <summary>面包屑跳转：前进式导航（清空其后的历史分支）。</summary>
    [RelayCommand]
    private Task OpenBreadcrumbAsync(BreadcrumbSegment? segment) =>
        segment is null || IsBusy ? Task.CompletedTask : OpenAsync(segment.Path);

    private Task OpenAsync(string path) => NavigateCoreAsync(path, commitHistory: true);

    private async Task NavigateCoreAsync(string path, bool commitHistory)
    {
        IsBusy = true;
        FolderListingDto listing;
        try
        {
            // 枚举成功才提交导航：失败保持当前目录与历史不变，仅上报错误
            listing = await _browserService.ListAsync(path);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(ex.Message);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        if (commitHistory)
        {
            if (_historyIndex < _history.Count - 1)
            {
                _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
            }
            _history.Add(path);
            _historyIndex = _history.Count - 1;
        }

        ApplyListing(listing);
    }

    private void ApplyListing(FolderListingDto listing)
    {
        CurrentPath = listing.Path;
        // 整体替换集合实例 + 单次 OnPropertyChanged：一次 Reset 通知替代逐条 Add（大目录可达 5000 项）
        _allEntries = listing.Entries.Select(e => new FolderEntryViewModel(e)).ToList();
        _listingTruncated = listing.Truncated;
        FilterEntries();
        RebuildBreadcrumbs();
        OnPropertyChanged(nameof(CanGoUp));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    /// <summary>面包屑 = 根段（资源名）+ 当前路径相对根的各级目录名。</summary>
    private void RebuildBreadcrumbs()
    {
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new BreadcrumbSegment(RootName, RootPath));
        var root = RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = CurrentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (current.Length <= root.Length)
        {
            return;
        }

        var relative = current[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var accumulated = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            accumulated = Path.Combine(accumulated, segment);
            Breadcrumbs.Add(new BreadcrumbSegment(segment, accumulated));
        }
    }
}
