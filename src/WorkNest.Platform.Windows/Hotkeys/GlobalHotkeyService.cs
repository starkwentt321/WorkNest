using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms; // NativeWindow/Keys/Message 来自 WinForms，别名规避与 WPF 的类型歧义
using WorkNest.Application.Abstractions;

namespace WorkNest.Platform.Windows.Hotkeys;

/// <summary>
/// 全局呼出快捷键：隐藏消息窗口 + user32 RegisterHotKey/UnregisterHotKey。
/// 线程约束：WM_HOTKEY 投递到窗口创建线程的消息队列，TryRegister 必须在有消息泵的线程（WPF UI 线程）调用。
/// </summary>
public sealed class GlobalHotkeyService : IGlobalHotkeyService, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 1;

    /// <summary>RegisterHotKey 的修饰键位（winuser.h 的 MOD_ 常量）。</summary>
    [Flags]
    private enum ModifierKeys : uint
    {
        Alt = 0x1,
        Control = 0x2,
        Shift = 0x4,
        Win = 0x8,
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private HotkeyWindow? _window;
    private bool _registered;
    private string? _registeredGesture;

    public event EventHandler? HotkeyPressed;

    /// <summary>当前实际生效的手势；未注册任何热键时为 null。界面提示必须以此为准。</summary>
    public string? RegisteredGesture => _registered ? _registeredGesture : null;

    public bool TryRegister(string gesture)
    {
        // 手势无效时不动现有注册：换键失败不应连带毁掉正在生效的旧热键
        if (string.IsNullOrWhiteSpace(gesture) || !TryParseGesture(gesture, out var modifiers, out var virtualKey))
        {
            return false;
        }

        var previousGesture = _registeredGesture;
        Unregister(); // 换键必须先释放旧注册（同一窗口的同一热键 ID），且保持幂等

        try
        {
            _window ??= new HotkeyWindow();
            _window.HotkeyRaised = () => HotkeyPressed?.Invoke(this, EventArgs.Empty);
            if (!RegisterHotKey(_window.Handle, HotkeyId, modifiers, virtualKey))
            {
                // 新组合被占用（典型：已被系统或其他程序注册）：立即恢复原组合，
                // 保证旧热键不失效；恢复结果经 RegisteredGesture 供界面如实提示
                RestorePrevious(previousGesture);
                return false;
            }

            _registered = true;
            _registeredGesture = gesture;
            return true;
        }
        catch
        {
            // 隐藏窗口创建失败等极端情况：热键属增强功能，绝不抛异常影响主流程
            RestorePrevious(previousGesture);
            return false;
        }
    }

    /// <summary>重新注册刚释放的原组合；仍失败则保持未注册状态，由调用方按实际状态提示。</summary>
    private void RestorePrevious(string? previousGesture)
    {
        if (previousGesture is null
            || _window is null
            || !TryParseGesture(previousGesture, out var modifiers, out var virtualKey)
            || !RegisterHotKey(_window.Handle, HotkeyId, modifiers, virtualKey))
        {
            _registered = false;
            _registeredGesture = null;
            return;
        }

        _registered = true;
        _registeredGesture = previousGesture;
    }

    public void Unregister()
    {
        if (!_registered || _window is null)
        {
            return;
        }

        // 窗口已销毁等情况下注销失败可忽略，保持幂等
        _ = UnregisterHotKey(_window.Handle, HotkeyId);
        _registered = false;
    }

    public void Dispose()
    {
        Unregister();
        _window?.ReleaseHandle();
        _window = null;
    }

    /// <summary>
    /// 解析 "Ctrl+Alt+W" 风格手势。修饰键支持 Ctrl/Control、Alt、Shift、Win/Windows；
    /// 键位使用 WinForms.Keys 枚举名（其值与 Win32 虚拟键码一致，可直接作为 VK 传递）。
    /// 要求至少一个修饰键 + 恰好一个非修饰键位，否则视为不支持。
    /// </summary>
    private static bool TryParseGesture(string gesture, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        var hasKey = false;

        foreach (var part in gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= (uint)ModifierKeys.Control;
                    break;
                case "alt":
                    modifiers |= (uint)ModifierKeys.Alt;
                    break;
                case "shift":
                    modifiers |= (uint)ModifierKeys.Shift;
                    break;
                case "win" or "windows":
                    modifiers |= (uint)ModifierKeys.Win;
                    break;
                default:
                    // 只允许一个键位；键位名必须能解析为 Keys 枚举（如 W、F1、D1、Oemtilde）
                    if (hasKey
                        || !Enum.TryParse(part, ignoreCase: true, out WinForms.Keys key)
                        || key == WinForms.Keys.None)
                    {
                        return false;
                    }

                    virtualKey = (uint)key;
                    hasKey = true;
                    break;
            }
        }

        // 不含修饰键的全局热键会劫持用户正常输入，不支持（手势约定为组合键）
        return hasKey && modifiers != 0;
    }

    /// <summary>隐藏消息窗口：仅承载热键消息，从不显示；默认窗口类由 NativeWindow 注册。</summary>
    private sealed class HotkeyWindow : WinForms.NativeWindow
    {
        public Action? HotkeyRaised { get; set; }

        public HotkeyWindow()
        {
            // 空 CreateParams：创建不可见消息窗口；所在线程必须有消息泵才能收到 WM_HOTKEY
            CreateHandle(new WinForms.CreateParams());
        }

        protected override void WndProc(ref WinForms.Message m)
        {
            if (m.Msg == WmHotkey)
            {
                HotkeyRaised?.Invoke();
                return; // 已处理，不再交给默认窗口过程
            }

            base.WndProc(ref m);
        }
    }
}
