using WorkNest.Platform.Windows.Hotkeys;
using Xunit;

namespace WorkNest.IntegrationTests;

/// <summary>
/// 全局热键服务：真实调用 user32 RegisterHotKey/UnregisterHotKey 注册系统级热键，
/// 属 IntegrationTests 边界（非 mock，依赖本机系统状态）。
/// 每个用例结束必须 Dispose 释放系统热键，避免污染同机后续测试；
/// xunit 同一测试类内用例串行执行，本类用例间不会出现键位争用。
/// 若 F9/F10 等键位被本机其他软件的全局热键占用导致偶发失败，可换更冷门的
/// WinForms.Keys 枚举键位（如 F11/F12/Oemtilde）。
/// </summary>
public sealed class GlobalHotkeyServiceTests
{
    [Fact]
    public void TryRegister_ValidGesture_Succeeds()
    {
        var service = new GlobalHotkeyService();
        try
        {
            Assert.True(service.TryRegister("Ctrl+Alt+F9"));
            Assert.Equal("Ctrl+Alt+F9", service.RegisteredGesture);
        }
        finally
        {
            service.Dispose();
        }

        // 释放后必须回到未注册状态，界面提示以 RegisteredGesture 为准
        Assert.Null(service.RegisteredGesture);
    }

    [Fact]
    public void TryRegister_ConflictingNewGesture_KeepsPrevious()
    {
        // R05 回归：新组合被占用时必须立即恢复旧组合，旧热键不失效。
        // 同进程两个隐藏窗口 RegisterHotKey 同一组合同样互斥（svcA 占用 F9）。
        var serviceA = new GlobalHotkeyService();
        var serviceB = new GlobalHotkeyService();
        try
        {
            Assert.True(serviceA.TryRegister("Ctrl+Alt+F9"));
            Assert.True(serviceB.TryRegister("Ctrl+Alt+F10"));

            // F9 已被 serviceA 占用：注册失败，且 RegisteredGesture 仍为旧组合（已恢复生效）
            Assert.False(serviceB.TryRegister("Ctrl+Alt+F9"));
            Assert.Equal("Ctrl+Alt+F10", serviceB.RegisteredGesture);
        }
        finally
        {
            serviceB.Dispose();
            serviceA.Dispose();
        }
    }

    [Fact]
    public void TryRegister_InvalidGesture_DoesNotTouchExisting()
    {
        var service = new GlobalHotkeyService();
        try
        {
            Assert.True(service.TryRegister("Ctrl+Alt+F9"));

            // "W" 无修饰键，按手势约定被解析拒绝；不得连带毁掉正在生效的旧热键
            Assert.False(service.TryRegister("W"));
            Assert.Equal("Ctrl+Alt+F9", service.RegisteredGesture);
        }
        finally
        {
            service.Dispose();
        }
    }
}
