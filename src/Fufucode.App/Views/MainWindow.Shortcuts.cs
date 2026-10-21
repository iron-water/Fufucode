using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.VisualTree;
using Fufucode.App.Features.Shortcuts;

namespace Fufucode.App;

public partial class MainWindow
{
    private ShortcutManagerViewModel _shortcuts = null!;
    private ShortcutsPage _shortcutsPage = null!;
    private ShortcutAction[] _shortcutActions = [];
    private string _shortcutActionSignature = "";
    internal ShortcutManagerViewModel ShortcutsForTesting => _shortcuts;
    internal bool RouteShortcutForTesting(Key key, KeyModifiers modifiers) =>
        HandleGlobalShortcut(new KeyEventArgs { Key = key, KeyModifiers = modifiers });

    private void InitializeShortcuts()
    {
        var store = new ShortcutBindingsStore(Program.SelfTestDataRoot ?? _options.CreateAppDataLayout().Root);
        _shortcuts = new(() => store);
        SyncShortcutCatalog();
        _shortcuts.Changed += UpdateShortcutLabels;
        _shortcutsPage = new(_shortcuts) { CompositionProbe = IsShortcutCompositionActive };
        _actions.Changed += OnShortcutCapabilitiesChanged;
        UpdateShortcutLabels();
    }

    /// <summary>
    /// 动作目录可能因插件页启停而增长/收缩；快捷键目录必须同步重建，
    /// 不能只在启动时投影一次。
    /// </summary>
    private void SyncShortcutCatalog()
    {
        var signature = string.Join("|", _actions.Actions.Select(action => action.Id).OrderBy(id => id, StringComparer.Ordinal));
        if (signature == _shortcutActionSignature) return;
        _shortcutActionSignature = signature;
        _shortcutActions = _actions.Actions.Select(action => new ShortcutAction(action.Id, action.Title, action.Group,
            ShortcutDefaults.ScopeOf(action.Id), ShortcutDefaults.DefaultGestures(action.Id), action.Description)).ToArray();
        _shortcuts.SetActions(_shortcutActions, _actions.GetDisabledReason);
        UpdateShortcutLabels();
    }

    private void UpdateShortcutLabels()
    {
        _actions.UpdateShortcutLabels(Label);
        _terminalPage?.UpdateShortcutLabels(Label);
        string Label(string id) => _shortcutActions.FirstOrDefault(action => action.Id == id) is { } action ? _shortcuts.BindingText(action) : string.Empty;
    }

    private void OnShortcutCapabilitiesChanged()
    {
        SyncShortcutCatalog();
        foreach (var row in _shortcuts.Rows)
            row.RefreshUnavailable(() => _actions.GetDisabledReason(row.Id));
    }

    private bool IsShortcutCompositionActive() => IsShortcutCompositionActive(FocusManager?.GetFocusedElement() as Control);
    private static bool IsShortcutCompositionActive(Control? focused) =>
        focused?.GetVisualDescendants()
            .OfType<TextPresenter>().Any(presenter => !string.IsNullOrEmpty(presenter.PreeditText)) == true;

    private bool RouteConfiguredShortcut(KeyEventArgs e, Control? focused)
    {
        if (_shortcuts is null) return false;
        if (_terminalPage is { } terminalPage &&
            (ReferenceEquals(focused, terminalPage.View) || focused?.GetVisualAncestors().Any(x => ReferenceEquals(x, terminalPage.View)) == true))
            return TryScope(ShortcutScope.Terminal);
        if (focused is TextBox && TryScope(ShortcutScope.Editor)) return true;
        if (focused?.GetVisualAncestors().Any(x => x is Views.BrowserView) == true && TryScope(ShortcutScope.Browser)) return true;
        if (_vm?.IsChatPage == true && TryScope(ShortcutScope.Chat)) return true;
        return TryScope(ShortcutScope.Global);

        bool TryScope(ShortcutScope scope)
        {
            var candidates = _shortcutActions.Where(action => action.Scope == scope &&
                _shortcuts.EffectiveGestures(action).Any(gesture => gesture.Key == e.Key && gesture.Modifiers == e.KeyModifiers)).ToArray();
            if (candidates.Length == 0) return false;
            if (candidates.Length == 1 && candidates[0].Id == "terminal.copy-or-interrupt" && focused is TextBox text &&
                (!ReferenceEquals(text, _terminalPage?.CommandInput) || !string.IsNullOrEmpty(text.SelectedText))) return false;
            if (candidates.Length > 1)
            {
                _vm?.Timeline.AddNotification("快捷键冲突，请在设置中修改：" + string.Join("、", candidates.Select(action => action.Title)));
                return true;
            }
            _ = _actions.ExecuteAsync(candidates[0].Id);
            return true;
        }
    }
}
