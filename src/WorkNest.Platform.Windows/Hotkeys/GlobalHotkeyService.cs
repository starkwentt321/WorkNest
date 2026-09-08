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
        // （解析规则在平台层 HotkeyGesture，与设置页回显共用）
        if (string.IsNullOrWhiteSpace(gesture) || !HotkeyGesture.TryParse(gesture, out var parsed))
        {
            return false;
        }

        var previousGesture = _registeredGesture;
        Unregister(); // 换键必须先释放旧注册（同一窗口的同一热键 ID），且保持幂等

        try
        {
            _window ??= new HotkeyWindow();
            _window.HotkeyRaised = () => HotkeyPressed?.Invoke(this, EventArgs.Empty);
            if (!RegisterHotKey(_window.Handle, HotkeyId, (uint)parsed.Modifiers, parsed.VirtualKey))
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
            || !HotkeyGesture.TryParse(previousGesture, out var parsed)
            || !RegisterHotKey(_window.Handle, HotkeyId, (uint)parsed.Modifiers, parsed.VirtualKey))
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
