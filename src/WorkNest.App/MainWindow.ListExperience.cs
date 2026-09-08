using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using WorkNest.App.ViewModels;

namespace WorkNest.App;

public partial class MainWindow
{
    private bool _searchCompositionActive;

    private void InitializeListExperience()
    {
        PreviewKeyDown += ListExperience_PreviewKeyDown;
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find,
            (_, e) => { FocusResourceSearch(true); e.Handled = true; },
            (_, e) => { e.CanExecute = !ViewModel.IsEditing; e.Handled = true; }));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Find, Key.F, ModifierKeys.Control));
        TextCompositionManager.AddPreviewTextInputStartHandler(SearchBox, (_, _) => _searchCompositionActive = true);
        TextCompositionManager.AddPreviewTextInputHandler(SearchBox, (_, _) =>
            Dispatcher.BeginInvoke(() => _searchCompositionActive = false, DispatcherPriority.Input));
        SearchBox.LostKeyboardFocus += (_, _) => _searchCompositionActive = false;
        ResourceList.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(ColumnResizeCompleted));
        foreach (var column in new[] { NameColumn, TypeColumn, TargetColumn })
        {
            if (column.Header is not GridViewColumnHeader header) continue;
            header.ContextMenu = new ContextMenu();
            header.ContextMenu.Opened += ColumnMenu_Opened;
        }
        ViewModel.PropertyChanged += ListExperience_PropertyChanged;
        Closed += (_, _) => ViewModel.PropertyChanged -= ListExperience_PropertyChanged;
        ApplyColumnPreferences();
    }

    private void ListExperience_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ListPreferences)) ApplyColumnPreferences();
    }

    public void FocusSearchOnRecall() => Dispatcher.BeginInvoke(() =>
    {
        if (!ViewModel.IsBrowsingFolder) FocusResourceSearch(true);
    }, DispatcherPriority.Input);

    private void FocusResourceSearch(bool selectAll)
    {
        if (ViewModel.IsEditing) return;
        SearchBox.Focus();
        if (selectAll) SearchBox.SelectAll();
    }

    private void ListExperience_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel.IsEditing) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ApplicationCommands.Find.Execute(null, this);
            e.Handled = true;
            return;
        }
        // 输入法确认键留给 IME；完成文字组合的同一击不能继续触发资源打开。
        if (e.Key == Key.ImeProcessed || _searchCompositionActive) return;
        if (ViewModel.IsBrowsingFolder)
        {
            if (SearchBox.IsKeyboardFocusWithin && e.Key == Key.Escape
                && Keyboard.Modifiers == ModifierKeys.None && SearchBox.Text.Length > 0)
            {
                ClearSearch_Click(sender, e);
                e.Handled = true;
            }
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Escape && (SearchBox.IsKeyboardFocusWithin || ResourceList.IsKeyboardFocusWithin)
            && ViewModel.HasSearchText)
        {
            ClearSearch_Click(sender, e);
            e.Handled = true;
        }
        else if (SearchBox.IsKeyboardFocusWithin && e.Key is Key.Down or Key.Enter)
        {
            e.Handled = true;
            if (!ViewModel.ApplyPendingSearch()) return;
            var first = ViewModel.Resources.FirstOrDefault();
            if (first is null) return;
            if (e.Key == Key.Enter)
            {
                if (ViewModel.LaunchCommand.CanExecute(first)) ViewModel.LaunchCommand.Execute(first);
            }
            else
            {
                ViewModel.SelectedResource = first;
                ResourceList.ScrollIntoView(first);
                ResourceList.UpdateLayout();
                (ResourceList.ItemContainerGenerator.ContainerFromItem(first) as ListViewItem)?.Focus();
            }
        }
        else if (ResourceList.IsKeyboardFocusWithin && e.Key == Key.Enter)
        {
            // 列表原有 Enter 绑定也要经过新条件校验，阻止加载中旧行被启动。
            e.Handled = true;
            if (ViewModel.ApplyPendingSearch() && ViewModel.SelectedResource is { } selected
                && ViewModel.LaunchCommand.CanExecute(selected)) ViewModel.LaunchCommand.Execute(selected);
        }
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.FolderBrowser is { } browser) browser.SearchText = string.Empty;
        else ViewModel.ClearSearchCommand.Execute(null);
        FocusResourceSearch(false);
    }

    private void ExpandSearch_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ExpandSearchCommand.Execute(null);
        FocusResourceSearch(false);
    }

    private void ApplyColumnPreferences()
    {
        var preferences = ViewModel.ListPreferences;
        var grid = (GridView)ResourceList.View;
        NameColumn.Width = preferences.NameWidth;
        TypeColumn.Width = preferences.TypeWidth;
        TargetColumn.Width = preferences.TargetWidth;
        // 真正移除隐藏列，避免零宽列仍有表头、分隔线和键盘命中区域。
        grid.Columns.Clear();
        grid.Columns.Add(NameColumn);
        if (preferences.ShowType) grid.Columns.Add(TypeColumn);
        if (preferences.ShowTarget) grid.Columns.Add(TargetColumn);
    }

    private async void ColumnResizeCompleted(object sender, DragCompletedEventArgs e)
    {
        // 一次拖动结束才写入，连续 DragDelta 不触发数据库写入。
        var p = ViewModel.ListPreferences;
        var next = p with { NameWidth = NameColumn.Width, TypeWidth = TypeColumn.Width, TargetWidth = TargetColumn.Width };
        if (next != p) await ViewModel.UpdateListPreferencesAsync(next);
    }

    private void ColumnMenu_Opened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        menu.Items.Clear();
        menu.Items.Add(new MenuItem { Header = "名称列", IsCheckable = true, IsChecked = true, IsEnabled = false });
        var type = new MenuItem { Header = "类型列", IsCheckable = true, IsChecked = ViewModel.ListPreferences.ShowType };
        type.Click += async (_, _) => await ViewModel.UpdateListPreferencesAsync(ViewModel.ListPreferences with { ShowType = type.IsChecked });
        var target = new MenuItem { Header = "路径列", IsCheckable = true, IsChecked = ViewModel.ListPreferences.ShowTarget };
        target.Click += async (_, _) => await ViewModel.UpdateListPreferencesAsync(ViewModel.ListPreferences with { ShowTarget = target.IsChecked });
        menu.Items.Add(type);
        menu.Items.Add(target);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "恢复默认排序", Command = ViewModel.ResetListSortCommand });
        menu.Items.Add(new MenuItem { Header = "恢复默认布局", Command = ViewModel.ResetListLayoutCommand });
    }
}
