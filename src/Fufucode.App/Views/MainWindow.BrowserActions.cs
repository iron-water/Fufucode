using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Features.BrowserExperience;
using Fufucode.App.Features.Shortcuts;

namespace Fufucode.App;

public partial class MainWindow
{
    private Views.BrowserView? _browserActionTarget;
    private Views.BrowserView? BrowserActionTarget => _browserActionTarget ??
        FindBrowser(_overlayReturnFocus ?? FocusManager?.GetFocusedElement() as Control) ??
        (_workbenchPages.ContainsKey("browser") ? _workbenchPages["browser"].Content as Views.BrowserView : null);
    private static Views.BrowserView? FindBrowser(Control? control) => control as Views.BrowserView ??
        control?.GetVisualAncestors().OfType<Views.BrowserView>().FirstOrDefault();
    private Views.BrowserView CreateBrowserPage()
    {
        EnsureBrowserStores();
        var view = new Views.BrowserView(_browserBookmarks, _browserHistory, _browserDownloads);
        view.NativeShortcutHandler = (key, modifiers) => RouteNativeBrowserShortcut(view, key, modifiers);
        view.ChatReferenceRequested += (_, reference) => OnChatReferenceRequested(view, reference);
        view.ViewModel.FullViewChanged += (_, full) => SetBrowserFullView(full);
        // BRW-1：新页面立即读取当前浏览器设置（显示完整网址 + Agent 控制门禁），不等下一次设置变化。
        if (_browserSettings?.ViewModel is { } settings)
        {
            view.ViewModel.ApplyShowFullUrl(settings.CurrentSettings.ShowFullUrl);
            view.ApplyAgentControl(settings.CurrentSettings, _browserSettings.Store, settings);
            view.SetAgentApprovalHandler(ApproveBrowserAgentActionAsync);
            AttachBridge(view);
        }
        return view;
    }
    private void RegisterBrowserActions()
    {
        foreach (var entry in BrowserSurfaceActions.Catalog)
        {
            var id = entry.Id;
            Register(id, entry.Title, "浏览器", "", entry.Description,
                () => { BrowserSurfaceActions.TryExecute(BrowserActionTarget, id); return Task.CompletedTask; },
                () => BrowserSurfaceActions.GetDisabledReason(BrowserActionTarget, id));
        }
    }
    private bool RouteNativeBrowserShortcut(Views.BrowserView view, uint virtualKey, KeyModifiers modifiers)
    {
        if (_shortcuts is null || _shortcutsPage?.IsRecording == true) return false;
        var matches = _shortcutActions.Where(a => a.Scope == ShortcutScope.Browser &&
            _shortcuts.EffectiveGestures(a).Any(g => BrowserVirtualKeys.Of(g.Key) == virtualKey && g.Modifiers == modifiers)).ToArray();
        if (matches.Length == 0)
        {
            // Suppress WebView2's old defaults when that gesture has been rebound/disabled.
            return BrowserSurfaceActions.Catalog.SelectMany(a => ShortcutDefaults.DefaultGestures(a.Id))
                .Any(g => BrowserVirtualKeys.Of(g.Key) == virtualKey && g.Modifiers == modifiers);
        }
        Dispatcher.UIThread.Post(async () =>
        {
            if (matches.Length > 1) { _vm?.Timeline.AddNotification("浏览器快捷键冲突，请在设置中修改。"); return; }
            var prior = _browserActionTarget; _browserActionTarget = view;
            try { await _actions.ExecuteAsync(matches[0].Id); }
            finally { _browserActionTarget = prior; }
        });
        return true;
    }
}

internal static class BrowserVirtualKeys
{
    internal static uint Of(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return (uint)(0x41 + key - Key.A);
        if (key >= Key.D0 && key <= Key.D9) return (uint)(0x30 + key - Key.D0);
        if (key >= Key.F1 && key <= Key.F24) return (uint)(0x70 + key - Key.F1);
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return (uint)(0x60 + key - Key.NumPad0);
        return key switch { Key.Back => 8, Key.Tab => 9, Key.Return => 13, Key.Escape => 27, Key.Space => 32,
            Key.PageUp => 33, Key.PageDown => 34, Key.End => 35, Key.Home => 36, Key.Left => 37, Key.Up => 38,
            Key.Right => 39, Key.Down => 40, Key.Insert => 45, Key.Delete => 46, Key.OemPlus => 187,
            Key.OemMinus => 189, Key.OemComma => 188, Key.OemPeriod => 190, _ => key.ToString() switch {
                "OemSemicolon" or "Oem1" => 186, "OemQuestion" or "Oem2" => 191, "OemTilde" or "Oem3" => 192,
                "OemOpenBrackets" or "Oem4" => 219, "OemPipe" or "Oem5" => 220, "OemCloseBrackets" or "Oem6" => 221,
                "OemQuotes" or "Oem7" => 222, "OemBackslash" or "Oem102" => 226, "Multiply" => 106, "Add" => 107,
                "Subtract" => 109, "Decimal" => 110, "Divide" => 111, "NumLock" => 144, "Scroll" => 145,
                "CapsLock" or "Capital" => 20, "Pause" => 19, "PrintScreen" => 44, _ => 0 } };
    }
}
