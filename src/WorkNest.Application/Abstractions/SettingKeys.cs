namespace WorkNest.Application.Abstractions;

/// <summary>AppSetting 键名约定；值以 JSON 序列化保存。</summary>
public static class SettingKeys
{
    /// <summary>关闭按钮行为：true = 隐藏到托盘（首次默认），false = 直接退出。</summary>
    public const string CloseToTray = "behavior.closeToTray";

    /// <summary>主题："light" / "dark"。</summary>
    public const string Theme = "ui.theme";

    /// <summary>背景图片路径和透明度百分比；空路径使用内置图片。</summary>
    public const string BackgroundImage = "ui.backgroundImage";

    /// <summary>全局呼出快捷键，如 "Ctrl+Alt+W"。</summary>
    public const string Hotkey = "behavior.hotkey";

    /// <summary>开机启动（当前用户）。</summary>
    public const string Autostart = "behavior.autostart";

    /// <summary>双击文件夹资源在应用内浏览（true）；false = 系统资源管理器打开（默认，保持旧行为）。</summary>
    public const string FolderInlineBrowse = "behavior.folderInlineBrowse";

    /// <summary>工作区排序模式："LastUsed" / "Manual"。</summary>
    public const string WorkspaceSortMode = "workspace.sortMode";

    /// <summary>上次选择的工作区 Id（JSON number）。</summary>
    public const string LastWorkspaceId = "state.lastWorkspaceId";

    /// <summary>主窗口位置与尺寸（JSON：X/Y/W/H/State）。</summary>
    public const string WindowBounds = "state.windowBounds";

    /// <summary>资源列表视图偏好（JSON：列宽/顺序/显隐/排序列）。</summary>
    public const string ViewPreference = "state.viewPreference";

    /// <summary>会话收尾标志（F24/决策 99）：正常退出写 true，启动时读到 false 即视为上次异常退出，随后立即写 false 布防。</summary>
    public const string SessionCleanExit = "state.sessionCleanExit";

    /// <summary>
    /// 会话状态键判定（state. 前缀）：窗口布局、上次工作区、会话标志等运行状态。
    /// 这些键的写入不代表业务内容变化，不触发延迟合并备份，避免仅浏览也重置防抖计时。
    /// </summary>
    public static bool IsSessionStateKey(string key)
        => key.StartsWith("state.", StringComparison.Ordinal);
}
