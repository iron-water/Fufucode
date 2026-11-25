namespace Fufucode.Fui;

internal sealed partial class TuiController
{
    private const string SettingsGroupPrefix = "settings-group:";
    private string? _keyCaptureAction;

    private void OpenSettings(string query = "") =>
        OpenPicker("settings", "设置", SettingsChoices(), query,
            new FuiPickerOptions(
                Detail: choice => FuiSettingCatalog.Find(choice.Value) is { } definition
                    ? FuiSettingCatalog.Description(definition)
                    : null,
                GroupHeader: choice => choice.Value.StartsWith(SettingsGroupPrefix, StringComparison.Ordinal),
                PreserveOrder: true));

    private IReadOnlyList<FuiChoice> SettingsChoices()
    {
        var choices = new List<FuiChoice>(FuiSettingCatalog.Definitions.Count + FuiSettingCatalog.Groups.Length);
        foreach (var group in FuiSettingCatalog.Groups)
        {
            var definitions = FuiSettingCatalog.Definitions.Where(item => item.Group == group).ToArray();
            if (definitions.Length == 0) continue;
            choices.Add(new(SettingsGroupPrefix + group, group));
            choices.AddRange(definitions.Select(definition =>
            {
                var value = FuiSettingCatalog.GroupValue(_settings, definition);
                var restart = definition.RestartRequired ? " · 需新进程" : "";
                return new FuiChoice(definition.Key,
                    $"{definition.Label}  {value}{restart}",
                    FuiSettingCatalog.Detail(definition.Key));
            }));
        }
        return choices;
    }

    private void OpenStatusSegments()
    {
        var choices = FuiStatusSegmentCatalog.All.Select(segment =>
        {
            var enabled = _settings.StatusSegments.Contains(segment.Id, StringComparer.Ordinal);
            return new FuiChoice(segment.Id, $"{(enabled ? "✓" : "·")} {segment.Label}",
                enabled ? "已显示" : "已隐藏");
        }).ToArray();
        OpenPicker("status-segments", "设置 · 状态分段", choices,
            options: new FuiPickerOptions(Detail: choice => choice.Detail, PreserveOrder: true));
    }

    private void OpenTheme()
    {
        var current = _settings.Theme;
        var choices = FuiSettingCatalog.OptionsFor(FuiSettingCatalog.Find("theme")!)
            .OrderBy(option => option.Value == current ? 0 : 1)
            .Select(option => new FuiChoice(option.Value,
                option.Value == current ? $"{option.Label} · 当前" : option.Label))
            .ToArray();
        OpenPicker("theme", "主题", choices);
    }

    private FuiTerminalRuntimeState TerminalState()
    {
        if (_terminalState is not null) return _terminalState;
        var codePage = Console.OutputEncoding.CodePage;
        return new(0, 0, codePage, codePage, Console.OutputEncoding.WebName,
            false, false, "未知（未连接 TerminalSurface）", TuiTheme.FromEnvironment(_settings.Theme),
            _dataRoot ?? "未知");
    }

    private void OpenTerminalPanel()
    {
        var state = TerminalState();
        OpenPicker("terminal", "终端", FuiDoctor.TerminalChoices(_settings, _terminal, state,
            FuiTerminalDetector.EnvironmentDictionary()));
    }

    private void Doctor()
    {
        var environment = FuiTerminalDetector.EnvironmentDictionary();
        var report = FuiDoctor.Format(_settings, _terminal, TerminalState(), environment);
        if (_settingsStore.LoadIssue is { Length: > 0 } issue) report = issue + "\n\n" + report;
        Info(report);
    }

    private void OpenKeys()
    {
        var choices = FuiKeyBindingCatalog.All.Select(action =>
        {
            var key = FuiKeyBindingCatalog.TryResolve(_settings.Keybindings, action.Id, out var chord, out _)
                ? chord.Format() : "无效";
            return new FuiChoice(action.Id, $"{action.Label}  {key}", action.Description);
        }).ToArray();
        OpenPicker("keys", "按键", choices, options: new FuiPickerOptions(
            Detail: choice => FuiKeyBindingCatalog.Find(choice.Value)?.Description,
            PreserveOrder: true));
    }

    private void SettingSelected(string key, string query)
    {
        if (FuiSettingCatalog.Find(key) is not { } definition)
        {
            Error($"未知设置：{key}");
            return;
        }
        switch (definition.Kind)
        {
            case FuiSettingKind.MultiSelect:
                OpenStatusSegments();
                return;
            case FuiSettingKind.KeyBinding when definition.KeyAction is { } action:
                BeginKeyCapture(action);
                return;
            default:
                var next = FuiSettingCatalog.Cycle(_settings, definition);
                ApplySettings(next, $"{definition.Label}：{FuiSettingCatalog.GroupValue(next, definition)}");
                OpenSettings(query);
                return;
        }
    }

    private void ToggleSetting(string key)
    {
        if (FuiSettingCatalog.Find(key) is not { } definition)
        {
            Error($"未知设置：{key}");
            return;
        }
        var next = FuiSettingCatalog.Cycle(_settings, definition);
        ApplySettings(next, $"{definition.Label}：{FuiSettingCatalog.GroupValue(next, definition)}");
    }

    private void SetSetting(string key, string value, bool announce)
    {
        if (FuiSettingCatalog.Find(key) is not { } definition)
        {
            Error($"未知设置：{key}");
            return;
        }
        var next = FuiSettingCatalog.Set(_settings, definition, value);
        ApplySettings(next, announce ? $"{definition.Label}：{FuiSettingCatalog.GroupValue(next, definition)}" : null);
    }

    private void ApplySettings(FuiSettings next, string? notice)
    {
        var result = _settingsStore.Update(_ => next);
        if (!result.Ok)
        {
            Error(result.Error ?? "设置未保存。");
            return;
        }
        _settings = _settingsStore.Current;
        _expandTools = _settings.ExpandTools;
        _showThinking = _settings.ShowThinking;
        Dirty = true;
        if (notice is { Length: > 0 }) Info(notice);
    }

    private void BeginKeyCapture(string action)
    {
        if (FuiKeyBindingCatalog.Find(action) is not { } definition)
        {
            Error($"未知按键动作：{action}");
            return;
        }
        ClosePicker();
        _keyCaptureAction = action;
        _status = "等待按键";
        Info($"按下“{definition.Label}”的新按键；Esc 取消。");
        Dirty = true;
    }

    private void CaptureKey(ConsoleKeyInfo key)
    {
        var action = _keyCaptureAction!;
        if (key.Key == ConsoleKey.Escape && key.Modifiers == 0)
        {
            _keyCaptureAction = null;
            _status = "就绪";
            Info("按键绑定未修改。");
            return;
        }
        if (MouseInput.IsRoutedKey(key.Key)) return;
        if (!FuiKeyChord.TryFromKey(key, out var chord, out var shapeError))
        {
            Error($"按键未绑定：{shapeError}");
            return;
        }
        if (!FuiKeyBindingCatalog.TrySet(_settings, action, chord, out var next, out var error))
        {
            Error($"按键未绑定：{error}");
            return;
        }

        var name = FuiKeyBindingCatalog.Find(action)!.Label;
        var result = _settingsStore.Update(_ => next);
        if (!result.Ok)
        {
            Error(result.Error ?? "按键设置未保存。");
            return;
        }
        _settings = _settingsStore.Current;
        _keyCaptureAction = null;
        _status = "就绪";
        Info($"{name}：{chord.Format()}（已保存）");
        Dirty = true;
    }

    private void NotifyTurn(TuiEvent.TurnFinished turn)
    {
        if (turn.Outcome is not (FuiTurnOutcome.Completed or FuiTurnOutcome.Failed)) return;
        var body = turn.Outcome == FuiTurnOutcome.Completed
            ? $"{Path.GetFileName(_active.Project)} · 回合已完成"
            : $"{Path.GetFileName(_active.Project)} · 回合失败" +
              (string.IsNullOrWhiteSpace(turn.Summary) ? "" : " · " + TerminalText.SingleLine(turn.Summary));
        try { _notification?.Invoke(_settings, "FUI", body); }
        catch (Exception)
        {
            // Notification delivery never changes the turn outcome or interrupts the UI loop.
        }
    }

    private IReadOnlyList<string> StatusSegments()
    {
        var provider = _active as IFuiStatusProvider;
        var project = Path.GetFileName(Path.TrimEndingDirectorySeparator(_active.Project));
        if (project.Length == 0) project = _active.Project;
        var snapshot = new FuiStatusLine.Snapshot(project, _active.Model ?? "未配置",
            provider?.RuntimeLabel ?? "FUMO", provider?.QueuedCount ?? 0,
            _agentActivity.RunningCount, _agentActivity.Jobs.Count, provider?.UsageSummary);
        var value = FuiStatusLine.Segments(_settings, snapshot);
        return value.Length == 0 ? [] : [value];
    }

    private string CommandKeyHint =>
        FuiKeyBindingCatalog.TryResolve(_settings.Keybindings, FuiKeyBindingCatalog.Commands, out var chord, out _)
            ? chord.FormatDisplay() : "Ctrl+P";
}
