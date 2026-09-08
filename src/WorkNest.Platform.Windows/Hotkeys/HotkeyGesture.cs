using WinForms = System.Windows.Forms;

namespace WorkNest.Platform.Windows.Hotkeys;

/// <summary>RegisterHotKey 的修饰键位（winuser.h 的 MOD_ 常量）。</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>
/// 解析后的热键手势：修饰键位与虚拟键码（可直接传 RegisterHotKey），
/// 并保留手势串中的原始键位名，供设置页组装显示名（如 "D5" 回显为 "5"）。
/// 注册（GlobalHotkeyService）与设置页回显（SettingsViewModel）共用同一解析，避免双实现漂移。
/// </summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, uint VirtualKey, string KeyName)
{
    /// <summary>
    /// 解析 "Ctrl+Alt+W" 风格手势。修饰键支持 Ctrl/Control、Alt、Shift、Win/Windows；
    /// 键位使用 WinForms.Keys 枚举名（其值与 Win32 虚拟键码一致，可直接作为 VK 传递）。
    /// 要求至少一个修饰键 + 恰好一个非修饰键位，否则视为不支持。
    /// </summary>
    public static bool TryParse(string gesture, out HotkeyGesture parsed)
    {
        var modifiers = HotkeyModifiers.None;
        uint virtualKey = 0;
        string? keyName = null;

        foreach (var part in gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "win" or "windows":
                    modifiers |= HotkeyModifiers.Win;
                    break;
                default:
                    // 只允许一个键位；键位名必须能解析为 Keys 枚举（如 W、F1、D1、Oemtilde）
                    if (keyName is not null
                        || !Enum.TryParse(part, ignoreCase: true, out WinForms.Keys key)
                        || key == WinForms.Keys.None)
                    {
                        parsed = default;
                        return false;
                    }

                    virtualKey = (uint)key;
                    keyName = part;
                    break;
            }
        }

        // 不含修饰键的全局热键会劫持用户正常输入，不支持（手势约定为组合键）
        if (keyName is null || modifiers == HotkeyModifiers.None)
        {
            parsed = default;
            return false;
        }

        parsed = new HotkeyGesture(modifiers, virtualKey, keyName);
        return true;
    }
}
