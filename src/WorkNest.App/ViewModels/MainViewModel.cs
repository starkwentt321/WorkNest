using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkNest.Application.Abstractions;
using WorkNest.Application.Dtos;
using WorkNest.Application.Services;
using WorkNest.Application.Validation;
using WorkNest.App.Services;
using WorkNest.App.Themes;
using WorkNest.Domain;
using WorkNest.Platform.Windows.Icons;

namespace WorkNest.App.ViewModels;

/// <summary>
/// 主窗口视图模型：工作区切换、搜索过滤、资源列表、启动/置顶/编辑/移除与列头排序。
/// 普通错误一律写入 ErrorMessage 横幅（决策 89）；弹窗仅用于文档要求的确认场景
/// （共享同步决策 54/114、移除提示决策 113、未保存修改决策 72）。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IWorkspaceService _workspaceService;
    private readonly IResourceService _resourceService;
    private readonly ILauncherService _launcherService;
    private readonly ISettingsService _settingsService;
    private readonly IAutostartService _autostartService;
    private readonly IBackupService _backupService;
    private readonly IResourceIconProvider _iconProvider;
    private readonly IAppDialogs _dialogs;
    private readonly IFilePicker _filePicker;
    private readonly IFolderBrowserService _folderBrowserService;

    /// <summary>master 列表（当前范围的全量数据）；Resources 是过滤+排序后的视图。</summary>
    private List<ResourceItemViewModel> _master = [];

    /// <summary>master 的 Id 索引：重建视图时免线性查找，避免完整匹配的查找总量随资源数平方增长（R09）。</summary>
    private Dictionary<int, ResourceItemViewModel> _masterById = [];

    /// <summary>编辑中的资源除当前工作区外还关联的工作区（决策 54/114 的确认依据）。</summary>
    private IReadOnlyList<string>? _pendingSharedNames;

    /// <summary>切换工作区时抑制 SearchAll 触发的双重加载。</summary>
    private bool _suppressRangeReload;

    /// <summary>
    /// 资源加载版本号：每次触发加载递增并捕获快照，后台任务返回时比对；
    /// 不一致说明期间已有更新的加载被触发，旧任务的结果整体丢弃，防止晚归覆盖新数据。
    /// </summary>
    private int _loadGeneration;

    /// <summary>批量清空搜索时（切工作区/进最近视图/回退选择）抑制即时重建，由随后的资源加载统一重建一次。</summary>
    private bool _suppressSearchRebuild;

    /// <summary>搜索输入防抖计时器（DispatcherTimer 仅 UI 线程有效）；延迟为 0 时不创建，直接同步重建。</summary>
    private System.Windows.Threading.DispatcherTimer? _searchDebounceTimer;

    /// <summary>搜索防抖延迟（毫秒）；测试环境传 0 走同步直调路径。</summary>
    private readonly int _searchDebounceMilliseconds;

    /// <summary>最近使用视图状态（F09/决策 34）：保留当前工作区上下文的独立视图。</summary>
    private bool _isRecentView;

    /// <summary>最近使用视图锚定的上下文工作区（决策 59）：RecentItem 自身无 Workspace，
    /// 必须记住进入视图前的工作区，否则启动记账/命令可用性全部失效。</summary>
    private WorkspaceDto? _recentContextWorkspace;

    /// <summary>请求打开“管理工作区”对话框（View 层订阅并创建窗口）。</summary>
    public event EventHandler? RequestManageWorkspaces;

    public MainViewModel(
        IWorkspaceService workspaceService,
        IResourceService resourceService,
        ILauncherService launcherService,
        ISettingsService settingsService,
        IAutostartService autostartService,
        IBackupService backupService,
        IResourceIconProvider iconProvider,
        IAppDialogs dialogs,
        IFilePicker filePicker,
        IFolderBrowserService folderBrowserService,
        int searchDebounceMilliseconds = 250)
    {
        _workspaceService = workspaceService;
        _resourceService = resourceService;
        _launcherService = launcherService;
        _settingsService = settingsService;
        _autostartService = autostartService;
        _backupService = backupService;
        _iconProvider = iconProvider;
        _dialogs = dialogs;
        _filePicker = filePicker;
        _folderBrowserService = folderBrowserService;
        _searchDebounceMilliseconds = searchDebounceMilliseconds;
    }

    /// <summary>组合根在启动时赋值：上次选择的工作区 Id（决策 93）。</summary>
    public int? InitialWorkspaceId { get; set; }

    // ============ 工作区 ============

    public ObservableCollection<WorkspaceOptionViewModel> WorkspaceOptions { get; } = [];

    private WorkspaceOptionViewModel? _currentOption;

    /// <summary>
    /// 当前选中的下拉项。选中“管理工作区…”时触发事件并回滚选择（决策 26/44）；
    /// 切换工作区时清空搜索（决策 56）、重置范围为当前工作区（决策 27）并持久化（决策 93）。
    /// </summary>
    public WorkspaceOptionViewModel? CurrentWorkspaceOption
    {
        get => _currentOption;
        set
        {
            if (value is null || ReferenceEquals(value, _currentOption))
            {
                return;
            }
            if (value.IsRecentItem)
            {
                // 决策 44/59：最近使用是独立视图，保留当前工作区上下文；无工作区时回滚选择
                if (CurrentWorkspace is null)
                {
                    OnPropertyChanged();
                    return;
                }
                _recentContextWorkspace = CurrentWorkspace; // 锚定上下文，供启动记账与命令使用
                _currentOption = value;
                OnPropertyChanged();
                InvalidateCommands();
                FolderBrowser = null; // 最近使用视图与浏览面板互斥
                _isRecentView = true;
                OnPropertyChanged(nameof(IsRecentView));
                _suppressRangeReload = true;
                _suppressSearchRebuild = true; // 批量清空搜索不单独重建视图，切换后由资源加载统一重建一次
                SearchAll = false; // 决策 27：最近使用固定当前工作区范围
                SearchText = string.Empty;
                _suppressSearchRebuild = false;
                _suppressRangeReload = false;
                OnPropertyChanged(nameof(StatusScopeText));
                _ = LoadResourcesAsync();
                return;
            }
            if (value.IsManageItem)
            {
                // 保持原选中不变，仅请求打开管理窗口；显式通知让绑定把 UI 选择回滚
                RequestManageWorkspaces?.Invoke(this, EventArgs.Empty);
                OnPropertyChanged();
                return;
            }
            var workspace = value.Workspace;
            if (workspace is null)
            {
                return;
            }
            _recentContextWorkspace = workspace; // 普通选择路径同步上下文，最近视图随时可用
            if (_currentOption?.Workspace?.Id == workspace.Id)
            {
                _currentOption = value;
                OnPropertyChanged();
                ExitRecentView();
                FolderBrowser = null; // 重选同一工作区：从浏览/最近视图回到普通列表
                _ = LoadResourcesAsync(); // 重选同一工作区：从最近使用视图回到普通列表
                return;
            }
            _currentOption = value;
            OnPropertyChanged();
            ExitRecentView();
            FolderBrowser = null; // 切换工作区退出浏览面板
            InvalidateCommands(); // 当前工作区变化影响新增三入口与启动类命令的可用性
            _ = OnCurrentWorkspaceChangedAsync(workspace);
        }
    }

    public WorkspaceDto? CurrentWorkspace =>
        _currentOption?.Workspace ?? (_isRecentView ? _recentContextWorkspace : null);

    private async Task OnCurrentWorkspaceChangedAsync(WorkspaceDto workspace)
    {
        _suppressRangeReload = true;
        _suppressSearchRebuild = true; // 批量清空搜索不单独重建视图，切换后由资源加载统一重建一次
        SearchAll = false; // 决策 27：切回当前工作区范围
        SearchText = string.Empty; // 决策 56：自动清空搜索
        _suppressSearchRebuild = false;
        _suppressRangeReload = false;

        try
        {
            await _settingsService.SetAsync(SettingKeys.LastWorkspaceId, workspace.Id);
        }
        catch
        {
            // 设置写入失败不影响切换
        }
        OnPropertyChanged(nameof(StatusScopeText));
        await LoadResourcesAsync();
    }

    // ============ 资源列表与搜索 ============

    /// <summary>
    /// 过滤+排序后的视图集合。重建时整体替换实例并单次通知，
    /// 避免逐条 Add 触发 N 次 CollectionChanged（替换由 RebuildView 负责，订阅随实例重建）。
    /// </summary>
    public ObservableCollection<ResourceItemViewModel> Resources { get; private set; } = [];

    [ObservableProperty]
    private ResourceItemViewModel? _selectedResource;

    partial void OnSelectedResourceChanged(ResourceItemViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(StatusSelectedText));
        InvalidateCommands();
    }

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>输入即过滤（决策 55），不改数据源；带防抖，连续击键只让最后一次生效，避免每个字符都全量重算。</summary>
    partial void OnSearchTextChanged(string value)
    {
        _searchPending = true;
        NotifySearchState();
        // 批量清空搜索（切工作区等）不触发重建，防止与随后的资源加载双重重建；同时停掉残留的待触发计时
        if (_suppressSearchRebuild)
        {
            _searchDebounceTimer?.Stop();
            return;
        }
        if (_searchDebounceMilliseconds <= 0)
        {
            // 同步直调路径：测试环境没有 Dispatcher 消息泵，计时器永远不会触发，必须退化为同步保证断言时机确定
            RebuildView();
            return;
        }
        if (_searchDebounceTimer is null)
        {
            _searchDebounceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(_searchDebounceMilliseconds),
            };
            _searchDebounceTimer.Tick += (_, _) =>
            {
                _searchDebounceTimer!.Stop();
                RebuildView(); // 读取当前 SearchText，防抖期间连续输入只应用最后一次
            };
        }
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    [ObservableProperty]
    private bool _searchAll;

    /// <summary>搜索范围切换：默认当前工作区，可选全部（决策 27/38）。</summary>
    partial void OnSearchAllChanged(bool value)
    {
        if (_isRecentView && value)
        {
            // 决策 59：最近使用固定当前工作区范围；UI 已禁用复选框，这里兜底回滚
            OnPropertyChanged(nameof(SearchAll));
            return;
        }
        OnPropertyChanged(nameof(StatusScopeText));
        if (!_suppressRangeReload)
        {
            _ = LoadResourcesAsync();
        }
    }

    /// <summary>是否处于“最近使用”视图（F09/决策 34/59）。</summary>
    public bool IsRecentView => _isRecentView;

    /// <summary>退出最近使用视图并刷新范围文案；普通工作区选择路径共用。</summary>
    private void ExitRecentView()
    {
        if (!_isRecentView)
        {
            return;
        }
        _isRecentView = false;
        OnPropertyChanged(nameof(IsRecentView));
        OnPropertyChanged(nameof(StatusScopeText));
    }

    // ============ 错误横幅（决策 89） ============

    [ObservableProperty]
    private string? _errorMessage;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    /// <summary>错误横幅写入（决策 89）；组合根启动链路也经此提示（如热键注册失败）。</summary>
    internal void ShowError(string message) => ErrorMessage = message;

    [RelayCommand]
    private void ClearError() => ErrorMessage = null;

    // ============ F24 异常退出提示（决策 99/102） ============

    /// <summary>组合根启动时按上次会话收尾标志置位；横幅只在用户关闭前持续显示。</summary>
    [ObservableProperty]
    private bool _showAbnormalExitNotice;

    [RelayCommand]
    private void CloseAbnormalNotice() => ShowAbnormalExitNotice = false;

    /// <summary>决策 102：提示附带“打开日志目录”动作，便于用户自查原因，不强制打开恢复页。</summary>
    [RelayCommand]
    private void OpenAbnormalLogFolder()
    {
        // 目录不存在时补建再打开；失败静默，不打断正常使用
        _ = AppPaths.OpenOrCreateInExplorer(AppPaths.LogsDir);
    }

    // ============ 编辑面板（决策 62/71/72） ============

    [ObservableProperty]
    private ResourceEditViewModel? _editPanel;

    /// <summary>编辑面板替换资源列表区域（决策 71）。</summary>
    public bool IsEditing => EditPanel is not null;

    partial void OnEditPanelChanged(ResourceEditViewModel? value)
    {
        OnPropertyChanged(nameof(IsEditing));
        NotifySearchState();
        // 浏览面板与编辑面板同区域互斥；编辑入口虽在列表上，仍防御性关闭避免两面板叠加
        if (value is not null)
        {
            FolderBrowser = null;
        }
        InvalidateCommands();
    }

    // ============ 文件夹应用内浏览（设置 behavior.folderInlineBrowse，默认关闭） ============

    /// <summary>开关内存态：启动时与设置窗口关闭后从设置重读；关闭时双击保持系统打开旧行为。</summary>
    public bool FolderInlineBrowseEnabled { get; private set; }

    [ObservableProperty]
    private FolderBrowserViewModel? _folderBrowser;

    /// <summary>浏览面板替换资源列表区域（与编辑面板同一互斥模式）。</summary>
    public bool IsBrowsingFolder => FolderBrowser is not null;

    partial void OnFolderBrowserChanged(FolderBrowserViewModel? value)
    {
        OnPropertyChanged(nameof(IsBrowsingFolder));
        NotifySearchState();
        InvalidateCommands();
    }

    /// <summary>启动与设置窗口关闭后重读内嵌浏览开关；读取失败按关闭处理（SettingsService 自身吞读取异常）。</summary>
    public async Task RefreshInlineBrowseSettingAsync()
    {
        FolderInlineBrowseEnabled = await _settingsService.GetAsync(SettingKeys.FolderInlineBrowse, false);
    }

    [RelayCommand]
    private void CloseFolderBrowser() => FolderBrowser = null;

    /// <summary>
    /// 双击入口：开关开启且为普通视图下的目录资源 → 计入使用并进入应用内浏览；
    /// 其余（文件/程序/网站、开关关闭、全部工作区/最近使用视图）回退系统启动。
    /// 右键“打开”与回车始终走 LaunchCommand（用户确认：资源管理器旧入口保留）。
    /// </summary>
    [RelayCommand]
    private async Task OpenAsync(object? parameter)
    {
        var item = ResolveItem(parameter) ?? SelectedResource;
        if (item is null)
        {
            return;
        }
        if (await TryOpenInlineAsync(item))
        {
            return;
        }
        LaunchCommand.Execute(item);
    }

    /// <summary>尝试进入应用内浏览；返回是否已处理（无论成败都不再走系统启动，避免双重反馈）。</summary>
    private async Task<bool> TryOpenInlineAsync(ResourceItemViewModel item)
    {
        if (!FolderInlineBrowseEnabled || item.Type != ResourceType.Directory)
        {
            return false;
        }
        if (SearchAll || IsRecentView || CurrentWorkspace is null)
        {
            return false;
        }

        LaunchResultDto result;
        try
        {
            result = await _launcherService.RecordInlineOpenAsync(CurrentWorkspace.Id, item.Id);
        }
        catch (Exception ex)
        {
            ShowError($"打开失败：{ex.Message}");
            return true;
        }

        if (!result.Success)
        {
            if (result.FailureKind == LaunchFailureKind.TargetMissing)
            {
                item.IsInvalid = true; // 与系统启动失败同款：目标缺失标灰（F08）
            }
            ShowError(result.ErrorMessage ?? "打开失败");
            return true;
        }

        // 记账后默认排序重排（与启动成功路径一致，决策 47）；普通视图局部更新即可保持排序语义
        RefreshAfterSuccessfulLaunch(item);
        EnterFolderBrowser(item.Id, item.Dto.Target, item.Name);
        return true;
    }

    /// <summary>
    /// 启动/内联打开成功后的局部更新：更新该项统计并按当前排序重排，替代全量重载。
    /// 语义与全量重载一致：列头排序只用名称/类型/目标，不随统计变化；
    /// 默认排序的排序键（RunCount/LastUsedAt）已同步更新，重算结果与重载一致；
    /// 最近使用视图的服务端顺序是最近时间倒序，刚使用的项必然最新，移到首位即可。
    /// </summary>
    private void RefreshAfterSuccessfulLaunch(ResourceItemViewModel item)
    {
        item.ApplySuccessfulLaunch(DateTime.UtcNow); // 与 LauncherService 记账同用 UtcNow 口径
        if (_isRecentView)
        {
            _master.Remove(item);
            _master.Insert(0, item);
        }
        RebuildView();
    }

    private void EnterFolderBrowser(int resourceId, string target, string rootName)
    {
        var browser = new FolderBrowserViewModel(_folderBrowserService, target, rootName, resourceId);
        browser.ErrorOccurred += message => ShowError(message);
        browser.FileOpenRequested += path =>
        {
            try
            {
                // 子文件交给系统默认程序（UseShellExecute 由 Shell 负责协议/关联解析）
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ShowError($"打开失败：{ex.Message}");
            }
        };
        browser.ExplorerOpenRequested += path =>
        {
            try
            {
                FolderItemOps.OpenInExplorer(path); // 浏览面板根目录直接打开
            }
            catch (Exception ex)
            {
                ShowError($"打开资源管理器失败：{ex.Message}");
            }
        };
        FolderBrowser = browser;
        _ = browser.InitializeAsync();
    }

    // ============ 列头排序（决策 51/43） ============

    /// <summary>当前排序列键（Name/Type/Target），null = 默认排序。</summary>
    public string? SortKey { get; private set; }

    public bool SortDescending { get; private set; }

    public string NameSortGlyph => GlyphFor("Name");
    public string TypeSortGlyph => GlyphFor("Type");
    public string TargetSortGlyph => GlyphFor("Target");

    private string GlyphFor(string key) => SortKey == key ? (SortDescending ? "▼" : "▲") : string.Empty;

    /// <summary>列头点击：同列翻转方向，异列设为升序；方向取消后回到默认排序。</summary>
    [RelayCommand]
    private void ApplyColumnSort(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }
        if (SortKey == key)
        {
            if (SortDescending)
            {
                SortKey = null;
            }
            else
            {
                SortDescending = true;
            }
        }
        else
        {
            SortKey = key;
            SortDescending = false;
        }
        NotifySortGlyphs();
        RebuildView();
        ListPreferences = ListPreferences with { SortKey = SortKey, SortDescending = SortKey is not null && SortDescending };
        _ = SaveListPreferencesAsync();
    }

    private void NotifySortGlyphs()
    {
        OnPropertyChanged(nameof(NameSortGlyph));
        OnPropertyChanged(nameof(TypeSortGlyph));
        OnPropertyChanged(nameof(TargetSortGlyph));
    }

    // ============ 状态栏（决策 57/118） ============

    public string StatusTotalText => $"共 {Resources.Count} 项";

    public string StatusScopeText => IsRecentView
        ? $"最近使用 — {CurrentWorkspace?.Name ?? "无工作区"}" // 决策 69
        : SearchAll ? "范围：全部工作区" : $"范围：{CurrentWorkspace?.Name ?? "无工作区"}";

    public int SelectedCount => SelectedResource is null ? 0 : 1;

    public string StatusSelectedText => $"已选 {SelectedCount}";

    // ============ 初始化 ============

    /// <summary>加载工作区 → 选择 InitialWorkspaceId 或第一项 → 加载资源（决策 93/93+105）。</summary>
    public async Task InitializeAsync()
    {
        // 主题按设置应用（决策 30）；窗口 XAML 默认浅色，深色在此覆盖
        try
        {
            var theme = await _settingsService.GetAsync(SettingKeys.Theme, ThemeManager.Light);
            if (!string.Equals(theme, ThemeManager.CurrentTheme, StringComparison.OrdinalIgnoreCase))
            {
                ThemeManager.ApplyToAll(theme);
            }
        }
        catch
        {
            // 主题读取失败保持浅色
        }

        await RefreshInlineBrowseSettingAsync(); // 内嵌浏览开关随会话恢复
        await LoadListPreferencesAsync();

        await ReloadWorkspacesAsync();

        var target = InitialWorkspaceId is { } initialId
            ? WorkspaceOptions.FirstOrDefault(o => o.Workspace?.Id == initialId)
            : null;
        target ??= WorkspaceOptions.FirstOrDefault(o => !o.IsManageItem && !o.IsRecentItem);
        if (target is not null)
        {
            _currentOption = target;
            _recentContextWorkspace = target.Workspace; // 启动恢复后最近视图即可用
            ExitRecentView(); // 启动恢复固定落在普通工作区视图
            OnPropertyChanged(nameof(CurrentWorkspaceOption));
            OnPropertyChanged(nameof(StatusScopeText));
        }

        await LoadResourcesAsync();
    }

    /// <summary>
    /// 刷新工作区下拉（GetOrderedAsync 已反映最近使用/手动排序）；
    /// 当前选择已不存在（被删除）时回退到第一个有效工作区，避免残留失去归属的列表（R07）。
    /// </summary>
    public async Task ReloadWorkspacesAsync()
    {
        IReadOnlyList<WorkspaceDto> dtos = [];
        try
        {
            dtos = await _workspaceService.GetOrderedAsync();
        }
        catch (Exception ex)
        {
            ShowError($"加载工作区失败：{ex.Message}");
        }
        var keepId = CurrentWorkspace?.Id;
        WorkspaceOptions.Clear();
        foreach (var dto in dtos)
        {
            WorkspaceOptions.Add(new WorkspaceOptionViewModel(dto));
        }
        WorkspaceOptions.Add(WorkspaceOptionViewModel.RecentItem); // 决策 44：下拉内特殊项
        WorkspaceOptions.Add(WorkspaceOptionViewModel.ManageItem);
        var stillExists = keepId is { } id && dtos.Any(d => d.Id == id);
        if (_isRecentView)
        {
            if (stillExists)
            {
                // 最近使用视图不随下拉重建而退出（决策 34：独立视图保留上下文）
                _currentOption = WorkspaceOptions.FirstOrDefault(o => o.IsRecentItem);
            }
            else
            {
                // 最近视图锚定的上下文工作区已被删除：退出最近视图并回退有效选择（R07）
                ExitRecentView();
                _currentOption = null;
            }
        }
        else if (stillExists)
        {
            _currentOption = WorkspaceOptions.FirstOrDefault(o => o.Workspace?.Id == keepId);
        }
        else
        {
            _currentOption = null;
        }

        if (_currentOption is null)
        {
            // 选择失效的统一回退：明确落到第一个有效工作区并同步重载资源（调用方负责串联 LoadResourcesAsync）
            _currentOption = WorkspaceOptions.FirstOrDefault(o => !o.IsManageItem && !o.IsRecentItem);
            _recentContextWorkspace = _currentOption?.Workspace;
            _suppressRangeReload = true;
            _suppressSearchRebuild = true; // 批量清空搜索不单独重建视图，回退后由资源加载统一重建一次
            SearchAll = false; // 决策 27：回退后回到当前工作区范围
            SearchText = string.Empty; // 决策 56：工作区已变化，清空搜索
            _suppressSearchRebuild = false;
            _suppressRangeReload = false;
            ExitRecentView();
            InvalidateCommands(); // 当前工作区变化影响新增三入口与启动类命令的可用性
        }
        OnPropertyChanged(nameof(CurrentWorkspaceOption));
        OnPropertyChanged(nameof(StatusScopeText));
    }

    /// <summary>重载当前范围的资源并重建视图（启动成功后的静默刷新也走这里）。</summary>
    public async Task LoadResourcesAsync()
    {
        // 重入保护：触发时递增版本号并快照当前范围；快速切换工作区/连续触发时，
        // 晚归的旧任务返回后比对版本不一致即整体丢弃，绝不用过期数据覆盖最新请求
        var generation = ++_loadGeneration;
        IsResourceLoading = true;
        _resourceLoadFailed = false;
        NotifySearchState();
        var isRecentView = _isRecentView;
        var searchAll = SearchAll;
        var workspace = CurrentWorkspace;
        List<ResourceItemViewModel> master;
        try
        {
            // Microsoft.Data.Sqlite 的异步 API 实为同步执行，再叠加 DTO 映射（逐项 PathExists 磁盘 IO）
            // 与图标提取（GDI+ 磁盘 IO），整段放后台线程执行；图标位图已 Freeze，允许跨线程访问
            master = await Task.Run(async () =>
            {
                IReadOnlyList<ResourceDto> dtos = [];
                if (isRecentView && workspace is not null)
                {
                    // F09/决策 59/70：当前工作区最近成功启动过的不同资源，最多 20 条
                    dtos = await _resourceService.GetRecentlyUsedAsync(workspace.Id, 20);
                }
                else if (searchAll)
                {
                    dtos = await _resourceService.GetAllWorkspacesAsync();
                }
                else if (workspace is not null)
                {
                    dtos = await _resourceService.GetForWorkspaceAsync(workspace.Id);
                }
                return dtos.Select(d => new ResourceItemViewModel(d, _iconProvider)).ToList();
            });
        }
        catch (Exception ex)
        {
            if (generation == _loadGeneration)
            {
                ShowError($"加载资源失败：{ex.Message}");
                _resourceLoadFailed = true;
            }
            master = [];
        }
        if (generation != _loadGeneration)
        {
            return; // 过期任务：期间已触发更新的加载，丢弃本次结果
        }
        _master = master;
        _masterById = master.ToDictionary(m => m.Id);
        IsResourceLoading = false;
        RebuildView();
    }

    /// <summary>重建视图集合：搜索过滤（SearchFilter）→ 默认排序或列头排序（决策 48/51）。</summary>
    private void RebuildView()
    {
        _searchPending = false;
        var keepId = SelectedResource?.Id;
        IEnumerable<ResourceDto> seq = _master.Select(m => m.Dto).Where(d => SearchFilter.Matches(d, SearchText));

        // 最近使用视图未点列头时保持服务端 LastUsedAt 倒序：
        // 普通默认排序按频次/置顶重排，会把刚用过的项挤到后面，违背“最近”语义（R06/决策 70）
        List<ResourceDto> list = _isRecentView && SortKey is null
            ? seq.ToList()
            : SortKey switch
            {
                "Name" => ResourceOrdering.ApplyColumnSort(seq, d => d.Name, SortDescending).ToList(),
                "Type" => ResourceOrdering.ApplyColumnSort(seq, d => (int)d.Type, SortDescending).ToList(),
                "Target" => ResourceOrdering.ApplyColumnSort(seq, d => d.Target, SortDescending).ToList(),
                _ => ResourceOrdering.ApplyDefault(seq).ToList(),
            };

        // 整体替换集合实例并单次通知：避免 Clear + 逐条 Add 触发 N 次 CollectionChanged，
        // 以及状态文本随每次 Add 重复通知；状态文本统计只随最终结果刷新一次
        var next = new ObservableCollection<ResourceItemViewModel>(list.Select(d => _masterById[d.Id]));
        next.CollectionChanged += (_, _) => OnPropertyChanged(nameof(StatusTotalText));
        Resources = next;
        OnPropertyChanged(nameof(Resources));
        SelectedResource = keepId is { } id ? Resources.FirstOrDefault(r => r.Id == id) : null;
        OnPropertyChanged(nameof(StatusTotalText));
        NotifySearchState();
    }

    private static ResourceItemViewModel? ResolveItem(object? parameter) =>
        parameter as ResourceItemViewModel;

    /// <summary>命令参数优先，未传时用当前选中项（工具栏置顶按钮等）。</summary>
    private ResourceItemViewModel? ResolveSelected(object? parameter) => parameter as ResourceItemViewModel ?? SelectedResource;

    /// <summary>命令可用性刷新：Toolkit 生成的 RelayCommand 需显式通知才会重新求值 CanExecute。</summary>
    private void InvalidateCommands()
    {
        LaunchCommand.NotifyCanExecuteChanged();
        TogglePinCommand.NotifyCanExecuteChanged();
        MovePinnedUpCommand.NotifyCanExecuteChanged();
        MovePinnedDownCommand.NotifyCanExecuteChanged();
        AddFileCommand.NotifyCanExecuteChanged();
        AddFolderCommand.NotifyCanExecuteChanged();
        AddWebsiteCommand.NotifyCanExecuteChanged();
        RemoveFromWorkspaceCommand.NotifyCanExecuteChanged();
        OpenLocationCommand.NotifyCanExecuteChanged();
    }

    // ============ 启动（F03/决策 11/31/47） ============

    private bool CanLaunch(object? parameter)
    {
        var item = ResolveItem(parameter) ?? SelectedResource;
        if (item is null)
        {
            return false;
        }
        return SearchAll ? item.Dto.WorkspaceIds.Count > 0 : CurrentWorkspace is not null;
    }

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task LaunchAsync(object? parameter)
    {
        var item = ResolveItem(parameter) ?? SelectedResource;
        if (item is null)
        {
            return;
        }
        // “全部工作区”范围下按资源第一个归属工作区记账
        var workspaceId = SearchAll ? item.Dto.WorkspaceIds[0] : CurrentWorkspace!.Id;
        try
        {
            var result = await _launcherService.LaunchAsync(workspaceId, item.Id);
            if (result.Success)
            {
                if (SearchAll)
                {
                    // “全部工作区”视图的统计取首个关联行，与本次记账工作区不一定是同一行，
                    // 局部 +1 无法保证与全量重载口径一致，保留全量刷新（决策 47）
                    await LoadResourcesAsync();
                }
                else
                {
                    // 成功后静默局部更新：按最新使用情况重排（决策 47），避免整表重载
                    RefreshAfterSuccessfulLaunch(item);
                }
            }
            else
            {
                if (result.FailureKind == LaunchFailureKind.TargetMissing)
                {
                    item.IsInvalid = true; // 目标缺失同时标灰该项（F08）
                }
                ShowError(result.ErrorMessage ?? "启动失败");
            }
        }
        catch (Exception ex)
        {
            ShowError($"启动失败：{ex.Message}");
        }
    }

    // ============ 置顶（F04/决策 32） ============

    private bool CanTogglePin(object? parameter) =>
        !SearchAll && CurrentWorkspace is not null && ResolveSelected(parameter) is not null;

    [RelayCommand(CanExecute = nameof(CanTogglePin))]
    private async Task TogglePinAsync(object? parameter)
    {
        var item = ResolveSelected(parameter);
        if (item is null || CurrentWorkspace is null)
        {
            return;
        }
        try
        {
            await _resourceService.SetPinnedAsync(CurrentWorkspace.Id, item.Id, !item.IsPinned);
            await LoadResourcesAsync();
        }
        catch (Exception ex)
        {
            ShowError($"置顶操作失败：{ex.Message}");
        }
    }

    // ============ 置顶组排序：拖动 + 上移/下移（决策 32） ============

    private bool CanReorderPinned(object? parameter) =>
        !SearchAll && CurrentWorkspace is not null && ResolveItem(parameter) is { IsPinned: true };

    /// <summary>拖动落点：把 sourceId 移动到 targetId 所在位置（insertAfter 控制前后）。</summary>
    public async Task ReorderPinnedAsync(int sourceId, int targetId, bool insertAfter)
    {
        if (SearchAll || CurrentWorkspace is null || sourceId == targetId)
        {
            return;
        }
        var pinnedIds = _master.Where(m => m.IsPinned).OrderBy(m => m.Dto.LinkSortOrder).Select(m => m.Id).ToList();
        if (!pinnedIds.Contains(sourceId) || !pinnedIds.Contains(targetId))
        {
            return;
        }
        pinnedIds.Remove(sourceId);
        var index = Math.Min(pinnedIds.IndexOf(targetId) + (insertAfter ? 1 : 0), pinnedIds.Count);
        pinnedIds.Insert(index, sourceId);
        await SavePinnedOrderAsync(pinnedIds);
    }

    [RelayCommand(CanExecute = nameof(CanReorderPinned))]
    private async Task MovePinnedUpAsync(object? parameter) => await ShiftPinnedAsync(ResolveItem(parameter), -1);

    [RelayCommand(CanExecute = nameof(CanReorderPinned))]
    private async Task MovePinnedDownAsync(object? parameter) => await ShiftPinnedAsync(ResolveItem(parameter), +1);

    private async Task ShiftPinnedAsync(ResourceItemViewModel? item, int delta)
    {
        if (item is null || SearchAll || CurrentWorkspace is null)
        {
            return;
        }
        var pinnedIds = _master.Where(m => m.IsPinned).OrderBy(m => m.Dto.LinkSortOrder).Select(m => m.Id).ToList();
        var index = pinnedIds.IndexOf(item.Id);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= pinnedIds.Count)
        {
            return;
        }
        (pinnedIds[index], pinnedIds[target]) = (pinnedIds[target], pinnedIds[index]);
        await SavePinnedOrderAsync(pinnedIds);
    }

    /// <summary>
    /// 置顶排序共享骨架（决策 32）：拖动与上移/下移只负责算出新顺序，
    /// 保存、重载列表与失败横幅文案统一在此收口（两个调用方入口均已确保 CurrentWorkspace 非空）。
    /// </summary>
    private async Task SavePinnedOrderAsync(List<int> orderedIds)
    {
        try
        {
            await _resourceService.SetPinnedOrderAsync(CurrentWorkspace!.Id, orderedIds);
            await LoadResourcesAsync();
        }
        catch (Exception ex)
        {
            ShowError($"置顶排序保存失败：{ex.Message}");
        }
    }

    // ============ 新增入口（决策 115） ============

    private bool CanAdd() => CurrentWorkspace is not null;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddFile()
    {
        // 再次新增会替换编辑面板：与取消/关闭共用同一未保存确认，不静默丢弃输入（R04/决策 72）
        if (!ConfirmDiscardChanges())
        {
            return;
        }
        var fileName = _filePicker.PickFile("选择文件");
        if (fileName is null)
        {
            return;
        }
        // .exe 视为程序，.lnk 及其他文件按普通文件（7.2：.lnk 保留快捷方式本身）
        var type = Path.GetExtension(fileName).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            ? ResourceType.Program
            : ResourceType.File;
        EditPanel = ResourceEditViewModel.New(type, fileName, CurrentWorkspace!.Id);
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddFolder()
    {
        if (!ConfirmDiscardChanges())
        {
            return;
        }
        var folderName = _filePicker.PickFolder("选择文件夹");
        if (folderName is null)
        {
            return;
        }
        EditPanel = ResourceEditViewModel.New(ResourceType.Directory, folderName, CurrentWorkspace!.Id);
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddWebsite()
    {
        if (!ConfirmDiscardChanges())
        {
            return;
        }
        // 网址由用户在编辑面板输入目标后保存
        EditPanel = ResourceEditViewModel.New(ResourceType.Website, string.Empty, CurrentWorkspace!.Id);
    }

    // ============ 外部文件/文件夹拖入（批次 C） ============

    /// <summary>
    /// 是否接受外部拖放：仅普通“当前工作区”视图（目标工作区明确，且新增后列表立即可见）；
    /// “全部工作区”与最近使用视图归属不明确，显示禁止光标。
    /// </summary>
    public bool CanAcceptExternalDrop => !SearchAll && !IsRecentView && CurrentWorkspace is not null;

    /// <summary>
    /// 拖入的文件/文件夹批量加入当前工作区，类型判定与“新增文件/文件夹”入口一致
    /// （目录→目录、.exe→程序、其余文件→普通文件，7.2）；名称留空由服务按目标提取（决策 61）。
    /// 直接落库不经过编辑面板（不触碰未保存输入）；逐项容错：单个失败（如已存在于当前工作区）不阻断其余项，最后汇总上横幅。
    /// </summary>
    public async Task AddDroppedPathsAsync(IReadOnlyList<string> paths)
    {
        if (!CanAcceptExternalDrop || paths.Count == 0)
        {
            return;
        }
        var workspaceId = CurrentWorkspace!.Id;
        var added = 0;
        List<string> failures = [];
        foreach (var path in paths)
        {
            var type = DecideTypeForPath(path);
            if (type is null)
            {
                continue; // 虚拟拖放项或路径已消失
            }
            try
            {
                await _resourceService.AddAsync(new ResourceEditInput
                {
                    WorkspaceId = workspaceId,
                    Type = type.Value,
                    Target = path,
                });
                added++;
            }
            catch (Exception ex)
            {
                failures.Add($"{DisplayNameOfPath(path)}：{ex.Message}");
            }
        }
        if (added > 0)
        {
            ErrorMessage = null; // 成功加入即清除旧横幅，与保存流程一致（决策 89）
            await LoadResourcesAsync();
        }
        if (failures.Count > 0)
        {
            ShowError($"已加入 {added} 项，{failures.Count} 项失败：\n{string.Join("\n", failures)}");
        }
        else if (added == 0)
        {
            ShowError("未添加任何资源：拖放项均不是有效的本地文件/文件夹。");
        }
    }

    /// <summary>拖放路径类型判定，与“新增文件/文件夹”入口保持同一规则。</summary>
    private static ResourceType? DecideTypeForPath(string path)
    {
        if (Directory.Exists(path))
        {
            return ResourceType.Directory;
        }
        if (File.Exists(path))
        {
            return Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                ? ResourceType.Program
                : ResourceType.File;
        }
        return null;
    }

    /// <summary>失败汇总里的条目名：取文件名，根路径等取不到时退回完整路径。</summary>
    private static string DisplayNameOfPath(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return name.Length > 0 ? name : path;
    }

    // ============ 编辑（决策 54/62/71/114） ============

    [RelayCommand]
    private async Task EditAsync(object? parameter)
    {
        var item = ResolveItem(parameter) ?? SelectedResource;
        if (item is null)
        {
            return;
        }
        int workspaceId;
        if (SearchAll)
        {
            if (item.Dto.WorkspaceIds.Count == 0)
            {
                return;
            }
            workspaceId = item.Dto.WorkspaceIds[0];
        }
        else
        {
            if (CurrentWorkspace is null)
            {
                return;
            }
            workspaceId = CurrentWorkspace.Id;
        }
        try
        {
            // 进入编辑前记录共享范围，保存前据此提示（决策 54/114）；
            // 读取失败保留 null（未知状态），保存前会重查并可能阻止保存
            _pendingSharedNames = await _resourceService.GetSharedWorkspaceNamesAsync(item.Id, workspaceId);
        }
        catch (Exception ex)
        {
            _pendingSharedNames = null;
            ShowError($"读取共享范围失败，保存前将再次确认：{ex.Message}");
        }
        EditPanel = ResourceEditViewModel.FromItem(item, workspaceId);
    }

    [RelayCommand]
    private async Task SaveEditAsync()
    {
        var panel = EditPanel;
        if (panel is null)
        {
            return;
        }
        // 共享资源保存前确认同步影响范围（决策 54/114）；
        // 范围未知时重查一次，仍失败则阻止保存，绝不静默跳过影响提示
        if (!panel.IsNew && _pendingSharedNames is null)
        {
            try
            {
                _pendingSharedNames = await _resourceService.GetSharedWorkspaceNamesAsync(
                    panel.Id!.Value, panel.EditingWorkspaceId);
            }
            catch (Exception ex)
            {
                ShowError($"无法确认共享影响范围，已取消保存，请稍后重试：{ex.Message}");
                return;
            }
        }
        if (!panel.IsNew && _pendingSharedNames is { Count: > 0 })
        {
            var message = $"该资源还被以下 {_pendingSharedNames.Count} 个工作区使用：\n{string.Join("、", _pendingSharedNames)}\n" +
                          "修改会同步影响这些工作区，是否继续保存？";
            if (!_dialogs.Confirm("共享资源", message))
            {
                return;
            }
        }
        try
        {
            if (panel.IsNew)
            {
                await _resourceService.AddAsync(panel.BuildInput());
            }
            else
            {
                await _resourceService.UpdateAsync(panel.BuildInput());
            }
            EditPanel = null;
            _pendingSharedNames = null;
            ErrorMessage = null; // 下次成功操作关闭横幅
            await LoadResourcesAsync();
        }
        catch (ValidationException vex)
        {
            ShowError(vex.Message); // 校验错误上横幅，不弹窗（决策 89）
        }
        catch (Exception ex)
        {
            ShowError($"保存失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private void CancelEdit()
    {
        var panel = EditPanel;
        if (panel is null)
        {
            return;
        }
        if (!ConfirmDiscardChanges())
        {
            return;
        }
        EditPanel = null;
        _pendingSharedNames = null;
    }

    /// <summary>存在未保存修改时返回 true 表示可放弃；供取消与窗口关闭共用（决策 72）。</summary>
    public bool ConfirmDiscardChanges()
    {
        var panel = EditPanel;
        if (panel is null || !panel.IsDirty)
        {
            return true;
        }
        return _dialogs.Confirm("未保存修改", "存在未保存的修改，确定放弃并返回列表吗？");
    }

    // ============ 移出当前工作区（F19/决策 37/113） ============

    private bool CanRemoveFromWorkspace(object? parameter) =>
        !SearchAll && CurrentWorkspace is not null && (ResolveItem(parameter) ?? SelectedResource) is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveFromWorkspace))]
    private async Task RemoveFromWorkspaceAsync(object? parameter)
    {
        var item = ResolveItem(parameter) ?? SelectedResource;
        if (item is null || CurrentWorkspace is null)
        {
            return;
        }
        try
        {
            var shared = await _resourceService.GetSharedWorkspaceNamesAsync(item.Id, CurrentWorkspace.Id);
            string message;
            if (shared.Count > 0)
            {
                message = $"“{item.Name}”还被 {shared.Count} 个工作区使用（{string.Join("、", shared)}）。\n" +
                          "本次仅从当前工作区移除，其他工作区不受影响。确定移除吗？";
            }
            else
            {
                message = $"“{item.Name}”是最后一个工作区中的关联，移除后该资源记录（含标签与使用记录）将一并删除。\n确定移除吗？";
            }
            if (!_dialogs.Confirm("移出当前工作区", message))
            {
                return;
            }
            await _resourceService.RemoveFromWorkspaceAsync(CurrentWorkspace.Id, item.Id);
            // 浏览中的目录被移除：关闭面板避免继续浏览一个已脱离工作区的目标
            if (FolderBrowser?.ResourceId == item.Id)
            {
                FolderBrowser = null;
            }
            await LoadResourcesAsync();
        }
        catch (Exception ex)
        {
            ShowError($"移除失败：{ex.Message}");
        }
    }

    // ============ 复制路径 / 打开所在位置（F19/决策 52/60/112） ============

    [RelayCommand]
    private void CopyPath(object? parameter)
    {
        var item = ResolveItem(parameter) ?? SelectedResource;
        if (item is null)
        {
            return;
        }
        try
        {
            System.Windows.Clipboard.SetText(item.Target); // 网站使用原始 Target
        }
        catch (Exception ex)
        {
            ShowError($"复制路径失败：{ex.Message}");
        }
    }

    private bool CanOpenLocation(object? parameter) =>
        ResolveSelected(parameter) is { Type: not ResourceType.Website };

    [RelayCommand(CanExecute = nameof(CanOpenLocation))]
    private void OpenLocation(object? parameter)
    {
        var item = ResolveItem(parameter) ?? SelectedResource;
        if (item is null || item.Type == ResourceType.Website)
        {
            return;
        }
        try
        {
            if (item.Type == ResourceType.Directory)
            {
                FolderItemOps.OpenInExplorer(item.Target); // 目录直接打开自身（决策 60）
            }
            else
            {
                FolderItemOps.RevealInExplorer(item.Target); // 文件/程序定位并选中（决策 112）
            }
        }
        catch (Exception ex)
        {
            ShowError($"无法打开所在位置：{ex.Message}");
        }
    }

    // ============ 子视图模型工厂（供 View 层打开对话框） ============

    public WorkspaceManagerViewModel CreateWorkspaceManagerViewModel() => new(_workspaceService, _dialogs);
}
