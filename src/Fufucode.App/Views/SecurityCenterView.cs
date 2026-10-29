using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Fufucode.App.ViewModels;
using Fufucode.Core.Security;

namespace Fufucode.App.Views;

/// <summary>与设置页共用行卡和主题资源；每项编辑写入执行端读取的策略文件。</summary>
public sealed class SecurityCenterView : UserControl
{
    public static readonly StyledProperty<string> PageProperty =
        AvaloniaProperty.Register<SecurityCenterView, string>(nameof(Page), "security");
    public string Page { get => GetValue(PageProperty); set => SetValue(PageProperty, value); }

    private SecurityCenterViewModel? _model;
    private StackPanel _body = new();
    private TextBlock _status = new();
    private bool _first;
    private static readonly string[] ActionNames = ["允许", "询问", "拒绝"];

    public SecurityCenterView()
    {
        SizeChanged += (_, _) => UpdateRows();
        DataContextChanged += (_, _) =>
        {
            if (_model is not null) _model.PropertyChanged -= OnModelChanged;
            _model = DataContext as SecurityCenterViewModel;
            if (_model is not null) _model.PropertyChanged += OnModelChanged;
            Render();
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PageProperty) Render();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SecurityCenterViewModel.Status) or nameof(SecurityCenterViewModel.HasError))
            _status.Text = _model?.Status;
        if (args.PropertyName == nameof(SecurityCenterViewModel.IsAvailable)) Render();
    }

    private void Render()
    {
        _body = new StackPanel { Spacing = 0 };
        Content = _body;
        _first = true;
        if (_model is null) return;
        _status = SettingsLayout.Note(_model.Status);
        AutomationProperties.SetAutomationId(_status, "security-status");
        _body.Children.Add(_status);
        var resetConfirmation = new StackPanel { Spacing = 8, IsVisible = false };
        resetConfirmation.Children.Add(SettingsLayout.Note("恢复默认会替换全部安全规则与开关，审计和备份文件保留。"));
        resetConfirmation.Children.Add(Actions(Button("确认恢复默认", () => { _model.Reset(); Render(); }, "security-confirm-reset"),
            Button("取消", () => resetConfirmation.IsVisible = false)));
        _body.Children.Add(Button("恢复默认安全策略", () => resetConfirmation.IsVisible = true, "security-reset"));
        _body.Children.Add(resetConfirmation);
        if (!_model.IsAvailable)
        {
            if (_model.DataRoot is { } root)
                _body.Children.Add(Button("重新读取策略", () => _model.Attach(root, _model.OpenFolder)));
            return;
        }
        switch (Page)
        {
            case "security.files": Files(); break;
            case "security.commands": Commands(); break;
            case "security.network": Network(); break;
            case "security.sensitive": Sensitive(); break;
            case "security.review": Review(); break;
            case "security.audit": Audit(); break;
            default: Overview(); break;
        }
    }

    private void UpdateRows()
    {
        if (Bounds.Width <= 0) return;
        var narrow = Bounds.Width < 620;
        foreach (var row in this.GetVisualDescendants().OfType<Border>().Where(x => x.Classes.Contains("settingsRow")))
        {
            if (row.Child is not Grid grid || grid.Children.Count < 2) continue;
            var text = grid.Children.OfType<StackPanel>().FirstOrDefault();
            var control = grid.Children.OfType<Border>().FirstOrDefault();
            if (text is null || control is null) continue;
            grid.ColumnDefinitions = new ColumnDefinitions(narrow ? "Auto,*" : "Auto,*,Auto");
            grid.RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto" : "Auto");
            Grid.SetRow(control, narrow ? 1 : 0);
            Grid.SetColumn(control, narrow ? 0 : 2);
            Grid.SetColumnSpan(control, narrow ? 2 : 1);
            control.Margin = narrow ? new Thickness(0, 8, 0, 0) : default;
            control.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        }
    }

    private void Group(string title, params Control[] controls)
    {
        _body.Children.Add(SettingsLayout.GroupHeader(title, _first));
        _first = false;
        _body.Children.Add(SettingsLayout.Group(controls));
    }

    private static Button Button(string text, Action action, string? id = null)
    {
        var button = new Button { Content = text, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(button, text);
        if (id is not null) AutomationProperties.SetAutomationId(button, id);
        button.Click += (_, _) => action();
        return button;
    }

    private static WrapPanel Actions(params Control[] controls)
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        foreach (var control in controls) wrap.Children.Add(control);
        return wrap;
    }

    private static Border Panel(params Control[] controls)
    {
        var content = new StackPanel { Spacing = 8 };
        foreach (var control in controls) content.Children.Add(control);
        var panel = new Border { Child = content };
        panel.Classes.Add("settingsPanel");
        return panel;
    }

    private static TextBox Input(string placeholder, string id, int maxLength = 2048)
    {
        var input = new TextBox { PlaceholderText = placeholder, MaxLength = maxLength, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(input, placeholder);
        AutomationProperties.SetAutomationId(input, id);
        return input;
    }

    private static ComboBox Choice(SecurityAction value, Action<SecurityAction> changed, string name)
    {
        var box = new ComboBox { ItemsSource = ActionNames, SelectedIndex = (int)value, MinWidth = 90 };
        AutomationProperties.SetName(box, name);
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) changed((SecurityAction)box.SelectedIndex); };
        return box;
    }

    private Border Toggle(string title, string description, bool value, Func<SecuritySettings, bool, SecuritySettings> change, string id)
    {
        var toggle = new ToggleSwitch { IsChecked = value, OnContent = "", OffContent = "" };
        AutomationProperties.SetAutomationId(toggle, id);
        AutomationProperties.SetName(toggle, title);
        var reverting = false;
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (reverting || _model is null) return;
            if (_model.Save(s => change(s, toggle.IsChecked == true))) return;
            reverting = true;
            toggle.IsChecked = !toggle.IsChecked;
            reverting = false;
        };
        return SettingsLayout.ToggleRow(title, description, toggle);
    }

    private Border Link(string title, string description, string page, string icon) =>
        SettingsLayout.ChevronRow(title, description, () => _model?.Navigate?.Invoke(page), icon);

    private void Overview()
    {
        var s = _model!.Settings;
        Group("执行保护",
            Toggle("启用安全策略", "在 FUMO 工具执行前检查规则；明确拒绝不受完全授权模式覆盖。", s.Enabled,
                (x, v) => x with { Enabled = v }, "security-enabled"),
            SettingsLayout.Row("保护范围", "FUMO 原生会话与其工具调用链。外部后端、后台自动开发及子进程内部操作不保证受本中心规则控制。", null, "ShieldCheckmark"),
            SettingsLayout.Row("执行隔离", "当前使用工具策略与工作区写入边界。Shell 和插件不在操作系统沙箱中运行。", null),
            SettingsLayout.Row("策略自保护", "启用时，原生文件工具无法访问应用数据目录，防止修改策略、审计和备份。", null));
        Group("行为与内容防护",
            Toggle("工具行为风险审查", "使用本地规则检查高风险工具操作，必要时请求审批。", s.BehaviorReviewEnabled,
                (x, v) => x with { BehaviorReviewEnabled = v }, "security-behavior"),
            Toggle("工具内容注入防护", "检查工具返回中的常见指令注入特征，再交给模型。", s.InjectionProtectionEnabled,
                (x, v) => x with { InjectionProtectionEnabled = v }, "security-injection"),
            Link("行为审查规则", "管理补充给模型的行为约束提示。", "security.review", "ShieldCheckmark"));
        Group("访问规则",
            Link("文件安全", $"{s.FileRules.Count} 条路径规则 · 分别配置读取、写入和删除。", "security.files", "Folder"),
            Link("命令安全", $"{s.CommandRules.Count} 条命令规则，{s.BlockedPrograms.Count} 个禁用程序。", "security.commands", "Terminal"),
            Link("网络安全", $"{s.NetworkRules.Count} 条域名规则 · HTTP 远程与本地请求策略。", "security.network", "Globe"));
        Group("数据保护",
            Toggle("敏感信息保护", "检查工具内容中的密钥、令牌和私钥，减少意外进入模型上下文。", s.SensitiveDataProtectionEnabled,
                (x, v) => x with { SensitiveDataProtectionEnabled = v }, "security-sensitive"),
            Link("敏感信息检测规则", $"{s.SecretRules.Count(r => r.Enabled)} 条已启用 · 支持自定义正则表达式。", "security.sensitive", "ShieldCheckmark"),
            Toggle("修改前自动备份", "对原生文件写入和编辑保留恢复副本；Shell 与外部工具修改不在此范围。", s.BackupEnabled,
                (x, v) => x with { BackupEnabled = v }, "security-backup"));
        BackupLimits();
        Group("审计",
            Toggle("记录安全事件", "保存规则判定与审批摘要，敏感内容脱敏后写入。", s.AuditEnabled,
                (x, v) => x with { AuditEnabled = v }, "security-audit-enabled"),
            Link("审计中心", "查看最近记录、导出日志或清空记录。", "security.audit", "History"));
    }

    private void BackupLimits()
    {
        var total = new NumericUpDown { Minimum = 1, Maximum = 100000, Value = _model!.Settings.BackupTotalMb, Width = 120 };
        var file = new NumericUpDown { Minimum = 1, Maximum = 10000, Value = _model.Settings.BackupFileMb, Width = 120 };
        AutomationProperties.SetName(total, "备份总上限 MB");
        AutomationProperties.SetName(file, "单文件上限 MB");
        Group("备份容量",
            SettingsLayout.Row("备份总上限（MB）", "超出总配额时按备份策略处理。", total),
            SettingsLayout.Row("单文件上限（MB）", "无法安全备份时会阻止本次直接文件修改。", file),
            SettingsLayout.Row("保存备份容量", null, Button("保存容量", () =>
                _model.Save(s => s with { BackupTotalMb = (int)(total.Value ?? 3000), BackupFileMb = (int)(file.Value ?? 100) }), "security-save-backup")),
            SettingsLayout.Row("恢复备份", "每份备份含原始路径清单与文件副本。恢复前会再次确认覆盖目标。",
                Actions(Button("打开备份目录", () =>
                {
                    if (_model.DataRoot is not { } root) return;
                    var folder = Path.Combine(root, "security-backups");
                    Directory.CreateDirectory(folder);
                    _model.OpenFolder?.Invoke(folder);
                }, "security-open-backups"), Button("选择备份恢复", SelectBackup, "security-restore-backup"))));
    }

    private static string ActionText(SecurityAction action) => ActionNames[(int)action];

    private Control OrderButtons(int index, int count, Action<int> move, Action remove)
    {
        var up = Button("上移", () => move(-1)); up.IsEnabled = index > 0;
        var down = Button("下移", () => move(1)); down.IsEnabled = index + 1 < count;
        return Actions(up, down, Button("删除", remove));
    }

    private void ChangeList<T>(Func<SecuritySettings, IReadOnlyList<T>> select, Func<SecuritySettings, IReadOnlyList<T>, SecuritySettings> replace,
        Action<List<T>> edit, bool render = true)
    {
        if (_model!.Save(s => { var values = select(s).ToList(); edit(values); return replace(s, values); }) && render) Render();
    }

    private static void Move<T>(List<T> values, int index, int offset)
    {
        var other = index + offset;
        if (index < 0 || index >= values.Count || other < 0 || other >= values.Count) return;
        (values[index], values[other]) = (values[other], values[index]);
    }

    private void Files()
    {
        _body.Children.Add(SettingsLayout.Note("规则从上到下匹配，命中首条后停止；新规则添加到顶部。目录规则包含其下内容，支持 * 与 **。未命中时沿用会话权限策略。"));
        var path = Input("路径，例如 %USERPROFILE%\\.ssh\\ 或 **/.env", "security-file-pattern");
        var read = SecurityAction.Ask; var write = SecurityAction.Ask; var delete = SecurityAction.Ask;
        Group("添加路径规则", Panel(path, Actions(
            Label("读取"), Choice(read, v => read = v, "新规则读取权限"),
            Label("写入"), Choice(write, v => write = v, "新规则写入权限"),
            Label("删除"), Choice(delete, v => delete = v, "新规则删除权限"),
            Button("添加", () =>
            {
                if (string.IsNullOrWhiteSpace(path.Text)) { _model!.Fail("请输入文件路径。"); return; }
                ChangeList(s => s.FileRules, (s, x) => s with { FileRules = x }, x => x.Insert(0, new(path.Text.Trim(), read, write, delete)));
            }, "security-add-file"))));
        var rules = _model!.Settings.FileRules;
        var rows = new List<Control>();
        for (var i = 0; i < rules.Count; i++)
        {
            var index = i; var rule = rules[i];
            void Update(Func<FileSecurityRule, FileSecurityRule> change) => ChangeList(s => s.FileRules,
                (s, x) => s with { FileRules = x }, x => x[index] = change(x[index]), false);
            rows.Add(Panel(SettingsLayout.Row(rule.Pattern, "路径匹配规则", OrderButtons(index, rules.Count,
                offset => ChangeList(s => s.FileRules, (s, x) => s with { FileRules = x }, x => Move(x, index, offset)),
                () => ChangeList(s => s.FileRules, (s, x) => s with { FileRules = x }, x => x.RemoveAt(index)))),
                Actions(Label("读取"), Choice(rule.Read, v => Update(x => x with { Read = v }), "读取 " + rule.Pattern),
                    Label("写入"), Choice(rule.Write, v => Update(x => x with { Write = v }), "写入 " + rule.Pattern),
                    Label("删除"), Choice(rule.Delete, v => Update(x => x with { Delete = v }), "删除 " + rule.Pattern))));
        }
        Group("路径规则", rows.Count == 0 ? [SettingsLayout.Note("暂无路径规则。添加路径并选择权限。")]: rows.ToArray());
    }

    private static TextBlock Label(string text)
    {
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        label.Classes.Add("settingsRowDesc");
        return label;
    }

    private void Commands()
    {
        _body.Children.Add(SettingsLayout.Note("命令规则检查工具提交的命令文本；脚本、子进程与动态构造的命令无法等同于系统隔离。复杂命令仍可能需要审批。"));
        var program = Input("程序名，例如 diskpart（可省略 .exe）", "security-blocked-program");
        Group("程序黑名单", Panel(program, Button("添加禁用程序", () =>
        {
            if (string.IsNullOrWhiteSpace(program.Text)) { _model!.Fail("请输入程序名。"); return; }
            ChangeList(s => s.BlockedPrograms, (s, x) => s with { BlockedPrograms = x }, x => x.Insert(0, program.Text.Trim()));
        }, "security-add-program")));
        foreach (var name in _model!.Settings.BlockedPrograms)
            _body.Children.Add(SettingsLayout.Row(name, "执行前拦截直接识别到的程序。", Button("删除", () =>
                ChangeList(s => s.BlockedPrograms, (s, x) => s with { BlockedPrograms = x }, x => x.Remove(name)))));
        RuleEditor("命令规则", "命令前缀，例如 git push", "command", s => s.CommandRules, (s, x) => s with { CommandRules = x });
    }

    private void Network()
    {
        _body.Children.Add(SettingsLayout.Note("网络规则检查工具调用中可识别的 URL 与主机名。Shell 内部连接、已启动的外部进程与模型提供商连接不属于全局网络防火墙。"));
        Group("HTTP 请求策略",
            SettingsLayout.Row("HTTP 远程连接", "明文 HTTP 的远程请求。HTTPS 仍按域名规则检查。", Choice(_model!.Settings.RemoteHttpAction,
                value => _model.Save(s => s with { RemoteHttpAction = value }), "HTTP 远程连接")),
            SettingsLayout.Row("HTTP 本地连接", "仅本机回环地址。局域网地址不视为本机。", Choice(_model.Settings.LocalHttpAction,
                value => _model.Save(s => s with { LocalHttpAction = value }), "HTTP 本地连接")));
        RuleEditor("域名规则", "域名，例如 api.example.com 或 *.example.com", "network", s => s.NetworkRules, (s, x) => s with { NetworkRules = x });
    }

    private void RuleEditor(string title, string placeholder, string id,
        Func<SecuritySettings, IReadOnlyList<SecurityRule>> select, Func<SecuritySettings, IReadOnlyList<SecurityRule>, SecuritySettings> replace)
    {
        var pattern = Input(placeholder, "security-" + id + "-pattern");
        var action = SecurityAction.Ask;
        Group("添加" + title, Panel(pattern, Actions(Choice(action, value => action = value, "新规则动作"),
            Button("添加", () =>
            {
                if (string.IsNullOrWhiteSpace(pattern.Text)) { _model!.Fail("请输入规则内容。"); return; }
                ChangeList(select, replace, values => values.Insert(0, new(pattern.Text.Trim(), action)));
            }, "security-add-" + id)), SettingsLayout.Note("规则从上到下匹配，首条命中后停止；新规则默认添加到顶部。")));
        var rules = select(_model!.Settings);
        var rows = new List<Control>();
        for (var i = 0; i < rules.Count; i++)
        {
            var index = i; var rule = rules[i];
            rows.Add(Panel(SettingsLayout.Row(rule.Pattern, "命中后：" + ActionText(rule.Action),
                OrderButtons(index, rules.Count, offset => ChangeList(select, replace, x => Move(x, index, offset)),
                    () => ChangeList(select, replace, x => x.RemoveAt(index)))),
                Choice(rule.Action, v => ChangeList(select, replace, x => x[index] = x[index] with { Action = v }, false), "规则动作 " + rule.Pattern)));
        }
        Group(title, rows.Count == 0 ? [SettingsLayout.Note("暂无规则，未命中时保持现有会话审批策略。")]: rows.ToArray());
    }

    private void Sensitive()
    {
        var s = _model!.Settings;
        Group("自动检测",
            Toggle("敏感信息保护", "检测工具返回中的密钥、令牌与私钥；审计不记录原始秘密。", s.SensitiveDataProtectionEnabled,
                (x, v) => x with { SensitiveDataProtectionEnabled = v }, "security-sensitive-switch"));
        var name = Input("规则名称", "security-secret-name", 100);
        var pattern = Input("检测用正则表达式", "security-secret-pattern", 2048);
        Group("添加检测规则", Panel(name, pattern, Button("添加检测规则", () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(pattern.Text))
            { _model.Fail("请输入规则名称与正则表达式。"); return; }
            try { _ = new Regex(pattern.Text, RegexOptions.None, TimeSpan.FromMilliseconds(100)); }
            catch (ArgumentException) { _model.Fail("正则表达式无效，请检查括号与转义。"); return; }
            ChangeList(x => x.SecretRules, (x, values) => x with { SecretRules = values },
                values => values.Insert(0, new(name.Text.Trim(), pattern.Text)));
        }, "security-add-secret")));
        var rules = s.SecretRules;
        var rows = new List<Control>();
        for (var i = 0; i < rules.Count; i++)
        {
            var index = i; var rule = rules[i];
            var toggle = new ToggleSwitch { IsChecked = rule.Enabled, OnContent = "", OffContent = "" };
            AutomationProperties.SetName(toggle, "启用检测 " + rule.Name);
            toggle.IsCheckedChanged += (_, _) => ChangeList(x => x.SecretRules, (x, values) => x with { SecretRules = values },
                values => values[index] = values[index] with { Enabled = toggle.IsChecked == true }, false);
            rows.Add(SettingsLayout.Row(rule.Name, rule.Pattern, Actions(toggle, Button("删除", () =>
                ChangeList(x => x.SecretRules, (x, values) => x with { SecretRules = values }, values => values.RemoveAt(index))))));
        }
        Group("检测规则", rows.Count == 0 ? [SettingsLayout.Note("暂无检测规则。添加正则表达式以开始检测。")]: rows.ToArray());
    }

    private void Review()
    {
        Group("行为审查",
            Toggle("工具行为风险审查", "本地确定性规则评估高风险操作。规则拒绝优先于会话自动授权。", _model!.Settings.BehaviorReviewEnabled,
                (s, v) => s with { BehaviorReviewEnabled = v }, "security-review-switch"),
            SettingsLayout.Row("审查方式", "当前使用本地风险规则，无独立 AI 审批模型。自定义提示用于补充模型行为约束，不替代文件、命令和网络规则。", null));
        var input = Input("补充行为约束，每条最多 300 字符", "security-review-instruction", 300);
        input.AcceptsReturn = true;
        var add = Button("添加约束提示", () =>
        {
            if (string.IsNullOrWhiteSpace(input.Text)) { _model.Fail("请输入约束提示。"); return; }
            ChangeList(s => s.ReviewInstructions, (s, x) => s with { ReviewInstructions = x }, x => x.Add(input.Text.Trim()));
        }, "security-add-review");
        add.IsEnabled = _model.Settings.ReviewInstructions.Count < 5;
        Group("自定义约束提示", Panel(SettingsLayout.Note("最多 5 条。提示会补充给原生模型，强制权限请使用访问规则。"), input, add));
        foreach (var instruction in _model.Settings.ReviewInstructions)
            _body.Children.Add(SettingsLayout.Row(instruction, null, Button("删除", () =>
                ChangeList(s => s.ReviewInstructions, (s, x) => s with { ReviewInstructions = x }, x => x.Remove(instruction)))));
    }

    private void Audit()
    {
        var confirmation = new StackPanel { Spacing = 8, IsVisible = false };
        confirmation.Children.Add(SettingsLayout.Note("清空后无法恢复这些审计记录。是否继续？"));
        confirmation.Children.Add(Actions(Button("确认清空", () => { _model!.ClearAudit(); Render(); }, "security-confirm-clear-audit"),
            Button("取消", () => confirmation.IsVisible = false)));
        Group("审计日志", SettingsLayout.Row("最近 100 条记录", "仅显示安全事件摘要；导出包含已保留的审计记录。",
            Actions(Button("刷新", () => { _model!.Refresh(); Render(); }, "security-refresh-audit"),
                Button("导出日志", ExportAudit, "security-export-audit"),
                Button("清空记录", () => confirmation.IsVisible = true, "security-clear-audit"))));
        _body.Children.Add(confirmation);
        var lines = _model!.AuditLines();
        if (lines.Count == 0) _body.Children.Add(SettingsLayout.Note("还没有安全事件。启用审计后，工具的规则判定与审批结果会出现在这里。"));
        foreach (var line in lines) _body.Children.Add(SettingsLayout.Row(line, null, null));
    }

    private async void SelectBackup()
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider.CanOpen != true || _model?.DataRoot is not { } root) return;
            var folder = Path.Combine(root, "security-backups");
            Directory.CreateDirectory(folder);
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择安全备份清单", AllowMultiple = false,
                SuggestedStartLocation = await top.StorageProvider.TryGetFolderFromPathAsync(folder),
                FileTypeFilter = [new FilePickerFileType("备份清单") { Patterns = ["*.json"] }],
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is not { } manifest) return;
            var metadata = System.Text.Json.JsonSerializer.Deserialize<SecurityBackupManifest>(File.ReadAllText(manifest));
            if (metadata is null) { _model.Fail("备份清单为空。"); return; }
            var confirm = Panel(SettingsLayout.Note("将恢复：" + metadata.OriginalPath +
                (metadata.Existed ? "。当前内容会被覆盖，恢复前先备份当前文件。" : "。此文件在备份时不存在，恢复将移除当前文件并先备份。")),
                Actions(Button("确认恢复", () =>
                {
                    try { new SecurityBackupStore(root).Restore(manifest); _model.Complete("备份已恢复。可在审计中心查看恢复记录。"); }
                    catch (Exception error) { _model.Fail("恢复失败：" + error.Message); }
                    Render();
                }, "security-confirm-restore"), Button("取消", Render)));
            _body.Children.Insert(1, confirm);
            confirm.BringIntoView();
        }
        catch (Exception error) { _model?.Fail("无法选择备份：" + error.Message); }
    }

    private async void ExportAudit()
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider.CanSave != true) { _model!.Fail("当前窗口不支持选择导出位置。"); return; }
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出安全审计日志", SuggestedFileName = $"fufucode-security-{DateTime.Now:yyyyMMdd-HHmmss}.json",
                DefaultExtension = "json", ShowOverwritePrompt = true,
            });
            if (file?.TryGetLocalPath() is { } path) _model!.ExportAudit(path);
        }
        catch (Exception error) { _model!.Fail("导出失败：" + error.Message); }
    }
}
