using WorkNest.Platform.Windows.Hotkeys;
using Xunit;

namespace WorkNest.IntegrationTests;

/// <summary>
/// HotkeyGesture.TryParse 纯逻辑解析：不调用 RegisterHotKey、不占用窗口/键位资源
/// （注册链路见 GlobalHotkeyServiceTests）。VirtualKey 断言用 Win32 VK 十六进制字面量
/// （WinForms.Keys 枚举值与 VK 一致，见 HotkeyGesture 文档）。
/// </summary>
public sealed class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+W", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x57, "W")] // W
    // 大小写混合：修饰键匹配不区分大小写，键位名保留手势串原样（供回显）
    [InlineData("ctrl+alt+w", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x57, "w")]
    // 分段两侧空白被修剪（TrimEntries）
    [InlineData("Ctrl + Alt + W", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x57, "W")]
    // 单修饰键组合同样合法
    [InlineData("Ctrl+F1", HotkeyModifiers.Control, 0x70, "F1")]
    public void TryParse_ValidGesture_ParsesModifiersVirtualKeyAndKeyName(
        string gesture, HotkeyModifiers expectedModifiers, uint expectedVirtualKey, string expectedKeyName)
    {
        Assert.True(HotkeyGesture.TryParse(gesture, out var parsed));
        Assert.Equal(new HotkeyGesture(expectedModifiers, expectedVirtualKey, expectedKeyName), parsed);
    }

    [Theory]
    [InlineData("")]            // 空串：无键位
    [InlineData("   ")]         // 全空白：修剪后无键位
    [InlineData("garbage")]     // 非枚举键位名
    [InlineData("None")]        // Keys.None 键位被显式拒绝
    [InlineData("W")]           // 仅键位无修饰键：会劫持正常输入，不支持
    [InlineData("Ctrl")]        // 仅修饰键无键位
    [InlineData("Ctrl+Alt+W+X")] // 多余键位段：约定恰好一个非修饰键位
    public void TryParse_InvalidGesture_ReturnsFalse(string gesture)
    {
        Assert.False(HotkeyGesture.TryParse(gesture, out var parsed));
        Assert.Equal(default, parsed);
    }

    [Theory]
    [InlineData("D0", 0x30)]
    [InlineData("D1", 0x31)]
    [InlineData("D2", 0x32)]
    [InlineData("D3", 0x33)]
    [InlineData("D4", 0x34)]
    [InlineData("D5", 0x35)]
    [InlineData("D6", 0x36)]
    [InlineData("D7", 0x37)]
    [InlineData("D8", 0x38)]
    [InlineData("D9", 0x39)]
    public void TryParse_NumberKeys_PreservesKeyNameForEcho(string key, uint expectedVirtualKey)
    {
        Assert.True(HotkeyGesture.TryParse($"Ctrl+{key}", out var parsed));

        Assert.Equal(HotkeyModifiers.Control, parsed.Modifiers);
        Assert.Equal(expectedVirtualKey, parsed.VirtualKey);
        // 原始键位名 "D5" 等保留在 KeyName，设置页据此组装显示名（如回显为 "5"）
        Assert.Equal(key, parsed.KeyName);
    }
}
