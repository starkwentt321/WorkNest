using Microsoft.Win32;
using WorkNest.Application.Abstractions;

namespace WorkNest.Platform.Windows.Autostart;

/// <summary>
/// 开机启动：HKCU Run 键（Software\Microsoft\Windows\CurrentVersion\Run）。
/// 只操作当前 Windows 用户的注册表视图，符合设计 3.4“开机启动始终按当前用户注册”。
/// </summary>
public sealed class AutostartService : IAutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WorkNest";

    public bool IsEnabled()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return runKey?.GetValue(ValueName) is string command && !string.IsNullOrWhiteSpace(command);
        }
        catch
        {
            // 注册表读取失败按未启用处理，界面仍允许重新设置
            return false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                // 键不存在时 OpenSubKey 返回 null，直接视为已关闭
                using var existingKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                existingKey?.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            // Environment.ProcessPath 为当前 exe 完整路径；个别发布形态可能取不到，此时放弃写入避免登记坏路径
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return;
            }

            using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            // 路径整体加引号：安装到 Program Files 等带空格目录时不被错误拆成多个参数
            runKey?.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
        }
        catch
        {
            // HKCU 写入失败（组策略限制等）不向上抛：契约返回 void，调用方以 IsEnabled 感知实际状态
        }
    }
}
