using CommunityToolkit.Mvvm.Input;
using WorkNest.App.Services;
using WorkNest.Application.Abstractions;

namespace WorkNest.App.ViewModels;

public partial class MainViewModel
{
    public bool IsResourceLoading { get; private set; }
    private bool _resourceLoadFailed;
    private bool _searchPending;
    public bool HasSearchText => !string.IsNullOrEmpty(SearchText);
    public bool ShowSearchEmpty => !IsResourceLoading && !_resourceLoadFailed && !_searchPending
        && !IsEditing && !IsBrowsingFolder && !string.IsNullOrWhiteSpace(SearchText) && Resources.Count == 0;
    public bool ShowResourceLoading => IsResourceLoading && !IsEditing && !IsBrowsingFolder;
    public bool CanExpandSearch => !SearchAll && !IsRecentView && CurrentWorkspace is not null;
    public string SearchEmptyMessage => $"{(IsRecentView ? "当前工作区的最近使用中" : SearchAll ? "全部工作区" : "当前工作区")}未找到“{SearchText}”";

    private void NotifySearchState()
    {
        OnPropertyChanged(nameof(HasSearchText));
        OnPropertyChanged(nameof(IsResourceLoading));
        OnPropertyChanged(nameof(ShowResourceLoading));
        OnPropertyChanged(nameof(ShowSearchEmpty));
        OnPropertyChanged(nameof(CanExpandSearch));
        OnPropertyChanged(nameof(SearchEmptyMessage));
    }

    /// <summary>键盘动作先提交防抖中的文字，禁止在范围加载期间使用旧结果。</summary>
    public bool ApplyPendingSearch()
    {
        _searchDebounceTimer?.Stop();
        if (IsResourceLoading || _resourceLoadFailed || IsEditing || IsBrowsingFolder) return false;
        RebuildView();
        return true;
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
        ApplyPendingSearch();
    }

    [RelayCommand]
    private void ExpandSearch()
    {
        if (CanExpandSearch) SearchAll = true;
    }

    public ResourceListPreferences ListPreferences { get; private set; } = new();
    private Task<bool> _lastPreferenceWrite = Task.FromResult(true);

    public async Task LoadListPreferencesAsync()
    {
        try
        {
            ListPreferences = (await Task.Run(() => _settingsService.GetAsync(SettingKeys.ResourceListPreferences,
                new ResourceListPreferences()))).Normalize();
        }
        catch { ListPreferences = new(); }
        ApplyListPreferences();
    }

    private void ApplyListPreferences()
    {
        SortKey = ListPreferences.SortKey;
        SortDescending = ListPreferences.SortDescending;
        OnPropertyChanged(nameof(ListPreferences));
        NotifySortGlyphs();
        RebuildView();
    }

    public async Task UpdateListPreferencesAsync(ResourceListPreferences preferences)
    {
        ListPreferences = preferences.Normalize();
        ApplyListPreferences();
        await SaveListPreferencesAsync();
    }

    private async Task SaveListPreferencesAsync()
    {
        var snapshot = ListPreferences;
        var previous = _lastPreferenceWrite;
        // SQLite 异步接口仍可能同步忙等待；在线程池串行落盘，退出可等待纯后台任务。
        var write = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            try
            {
                await _settingsService.SetAsync(SettingKeys.ResourceListPreferences, snapshot).ConfigureAwait(false);
                return true;
            }
            catch { return false; }
        });
        _lastPreferenceWrite = write;
        if (!await write) ShowError("列表偏好保存失败，重启后可能恢复旧布局。");
    }

    public Task<bool> FlushListPreferencesAsync() => _lastPreferenceWrite;

    [RelayCommand]
    private Task ResetListLayoutAsync() => UpdateListPreferencesAsync(new());

    [RelayCommand]
    private Task ResetListSortAsync() => UpdateListPreferencesAsync(ListPreferences with
        { SortKey = null, SortDescending = false });
}
