using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    // 值为该次按下被受理的时间（TickCount64），用于识别丢失松开后残留的记录。
    private readonly Dictionary<(string Device, byte Button), long> _mouseShortcutButtons = [];
    // 同一次物理点击的重复通知会在几毫秒内到达。鼠标不会在没有松开的情况下
    // 再次按下，所以超过该间隔的第二次按下说明中间的松开丢失了。
    private const long MouseShortcutDuplicateWindowMs = 250;
    private readonly SemaphoreSlim _systemShortcutGate = new(1, 1);

    private bool CanSendDockShortcut(string? target)
    {
        // Globe+A is the iPad Dock command. On iPhone the same report can
        // become an ordinary A in the focused app. Use the actual target's
        // model, not a different device currently selected in the main list.
        var device = _viewModel.Devices.FirstOrDefault(candidate =>
            Models.DeviceViewModel.UdidEquals(candidate.Udid, target));
        if (device is null && Models.DeviceViewModel.UdidEquals(
                _viewModel.SelectedDevice?.Udid, target)) device = _viewModel.SelectedDevice;
        return device?.ProductType.StartsWith("iPad", StringComparison.OrdinalIgnoreCase) == true;
    }

    private bool TryHandlePointerShortcut(PreviewPointerEventArgs e, string? udid)
    {
        // Side buttons belong to shortcuts only. Consume unbound clicks too,
        // so USB does not turn them into touches or release an active drag.
        var sideButton = e.Button is 8 or 16;
        var identity = (udid?.ToUpperInvariant() ?? string.Empty, e.Button);
        if (e.Kind == PreviewPointerKind.Reset)
        {
            foreach (var key in _mouseShortcutButtons.Keys.Where(item => item.Device == identity.Item1).ToArray())
                _mouseShortcutButtons.Remove(key);
            return false;
        }
        if (e.Kind == PreviewPointerKind.ButtonUp)
            return _mouseShortcutButtons.Remove(identity) || sideButton;
        if (e.Kind != PreviewPointerKind.ButtonDown ||
            !CanForwardControlKeyboard(udid, GetControlKeyboardWindow(udid))) return sideButton;
        var now = Environment.TickCount64;
        if (_mouseShortcutButtons.TryGetValue(identity, out var pressedAt))
        {
            if (now - pressedAt <= MouseShortcutDuplicateWindowMs) return true;
            // 上一次点击的松开没有送达（焦点切换、窗口路由变化等），残留记录
            // 会把这次真实的新点击当成重复按下吞掉。清除残留后按新点击处理。
            _mouseShortcutButtons.Remove(identity);
            if (MouseShortcutVirtualKey(e.Button) is int virtualKey)
                _keyboardRouter.ReleaseMouseShortcut(virtualKey);
            DiagnosticLogger.ReverseControl("keyboard_input", "mouse_shortcut_stale_press_recovered",
                ("button", e.Button), ("held_ms", now - pressedAt));
        }
        if (!TryHandleMouseShortcut(e.Button)) return sideButton;
        // Swallow the matching release even if the user releases Ctrl/Alt
        // first. Otherwise a navigation click can release an unrelated touch.
        _mouseShortcutButtons[identity] = now;
        return true;
    }

    private static int? MouseShortcutVirtualKey(byte button) => button switch
    {
        2 => (int)KeyboardShortcut.MouseRight,
        4 => (int)KeyboardShortcut.MouseMiddle,
        8 => (int)KeyboardShortcut.MouseXButton1,
        16 => (int)KeyboardShortcut.MouseXButton2,
        _ => null,
    };

    private SemaphoreSlim KeyboardSessionGate(DirectKeyboardRoute route) =>
        _viewModel.DeviceInput.KeyboardGate(route);

    private async Task SendRoutedKeyboardAsync(DirectKeyboardRoute route, byte modifiers,
        byte[] usages, Func<bool> canSend)
    {
        var gate = KeyboardSessionGate(route);
        await gate.WaitAsync();
        try
        {
            if (canSend() && route.IsCurrent()) await route.SendAsync(modifiers, usages, canSend);
        }
        finally { gate.Release(); }
    }

    private async Task SendUsbSystemShortcutAsync(byte usage, DirectKeyboardRoute route,
        Func<bool> canSend)
    {
        // Globe is a Consumer-page control, not a keyboard modifier bit.
        // Keep the complete system chord on one ordered Indigo connection.
        const ushort page = CoreDeviceTouchProtocol.IndigoConsumerUsagePage;
        const ushort globe = CoreDeviceTouchProtocol.GlobeKeyboardLayoutUsage;
        if (route.SendButtonAsync is null) return;
        try
        {
            await route.SendButtonAsync(page, globe, "down", canSend);
            // The bridge also preserves these intervals at the actual send
            // boundary if host frames were queued behind a slow operation.
            await Task.Delay(60);
            if (!canSend()) return;
            await route.SendButtonAsync(CoreDeviceTouchProtocol.IndigoKeyboardUsagePage, usage, "down", canSend);
            await Task.Delay(80);
        }
        finally
        {
            // Both releases must be attempted even if either transport call
            // fails, and neither may inherit the expired focus guard.
            try { await route.SendButtonAsync(CoreDeviceTouchProtocol.IndigoKeyboardUsagePage, usage, "up", null); }
            finally
            {
                await Task.Delay(20);
                await route.SendButtonAsync(page, globe, "up", null);
            }
        }
    }
}
