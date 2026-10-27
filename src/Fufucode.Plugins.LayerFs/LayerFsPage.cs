using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media;

namespace Fufucode.Plugins.LayerFs;

public sealed partial class LayerFsPage : UserControl, IDisposable
{
    private readonly string _bridge;
    private readonly string _settingsFile;
    private LayerFsClient _client;
    private LayerFsSettings _settings = new();
    private readonly List<Button> _buttons = [];
    private bool _busy;
    private bool _updating;
    private bool _pendingCommits;
    private bool _disposed;

    public LayerFsPage(string packageDirectory, string dataRoot)
    {
        InitializeComponent();
        _bridge = Path.Combine(packageDirectory, "Bridge", "layerfs-bridge.mjs");
        _settingsFile = Path.Combine(dataRoot, "layerfs", "settings.json");
        try { if (File.Exists(_settingsFile)) _settings = JsonSerializer.Deserialize<LayerFsSettings>(File.ReadAllText(_settingsFile)) ?? _settings; }
        catch (Exception e) when (e is IOException or JsonException) { Status.Text = $"设置无法读取：{e.Message}"; }
        Distribution.Text = _settings.Distribution;
        LinuxUser.Text = _settings.User;
        Node.Text = _settings.Node;
        Config.Text = _settings.Config;
        Message.Text = "保存工作区改动";
        _client = new LayerFsClient(_bridge, _settings);
        Wire(RefreshButton, RefreshAsync);
        Wire(StateButton, StateAsync);
        Wire(CheckButton, CheckAsync);
        Wire(SaveConnectionButton, SaveConnection);
        Wire(InitButton, InitAsync);
        Wire(ForkButton, ForkAsync);
        Wire(CommitsButton, CommitsAsync);
        Wire(CreateSandboxButton, CreateSandboxAsync);
        Wire(MountButton, MountAsync);
        Wire(ExecButton, ExecAsync);
        Wire(CommitButton, CommitAsync);
        Wire(UnmountButton, UnmountAsync);
        Wire(CloseSandboxButton, CloseSandboxAsync);
        ConnectionButton.Click += (_, _) => ConnectionSection.IsExpanded = !ConnectionSection.IsExpanded;
        Projects.SelectionChanged += async (_, _) => { if (!_updating) await RunAsync(StateAsync); };
        Branches.SelectionChanged += async (_, _) =>
        {
            if (_updating) return;
            // 上一个操作未结束时不能静默丢弃这次选择，等它结束后补跑。
            if (_busy) { _pendingCommits = true; return; }
            await RunAsync(CommitsAsync);
        };
        Sandboxes.SelectionChanged += (_, _) => { if (!_updating) SelectSandbox(); };
        Workspaces.SelectionChanged += (_, _) => { };
    }

    private void Wire(Button button, Func<Task> action)
    {
        button.Click += async (_, _) => await RunAsync(action);
        _buttons.Add(button);
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _disposed) return;
        _busy = true;
        EnableInputs(false);
        Status.Text = "操作进行中…";
        try { await action(); if (!_disposed) Status.Text = "操作完成。"; }
        catch (Exception e) { if (!_disposed) { Status.Text = e.Message; Output.Text = e.ToString(); } }
        finally { _busy = false; if (!_disposed) EnableInputs(true); }
        if (_pendingCommits && !_disposed) { _pendingCommits = false; await RunAsync(CommitsAsync); }
    }
    private void EnableInputs(bool enabled)
    {
        foreach (var button in _buttons) button.IsEnabled = enabled;
        foreach (var field in new[] { Distribution, LinuxUser, Node, Config, Source, ProjectName, BranchName, Command, Message }) field.IsEnabled = enabled;
        Projects.IsEnabled = Branches.IsEnabled = Sandboxes.IsEnabled = Workspaces.IsEnabled = enabled;
    }
    private Task SaveConnection()
    {
        var settings = new LayerFsSettings { Distribution = Required(Distribution), User = Required(LinuxUser), Node = Required(Node), Config = Required(Config) };
        _ = LayerFsClient.StartInfo(_bridge, settings);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
        var temp = _settingsFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _settingsFile, overwrite: true);
        _client.Dispose();
        _settings = settings;
        _client = new LayerFsClient(_bridge, settings);
        ClearProjectState();
        Projects.ItemsSource = null;
        EnvironmentText.Text = "连接已保存，点击“检查环境”。";
        return Task.CompletedTask;
    }
    private static string Required(TextBox box) => string.IsNullOrWhiteSpace(box.Text)
        ? throw new ArgumentException("请填写" + (Avalonia.Automation.AutomationProperties.GetName(box) ?? box.PlaceholderText) + "。") : box.Text.Trim();
    private string Project => Projects.SelectedItem as string ?? throw new InvalidOperationException("请先刷新并选择 LayerFS 项目。");
    private string Branch => (Branches.SelectedItem as ListBoxItem)?.Tag as string ?? throw new InvalidOperationException("请选择分支。");
    private SandboxChoice Sandbox => Sandboxes.SelectedItem as SandboxChoice ?? throw new InvalidOperationException("请选择或创建沙箱。");
    private WorkspaceChoice Workspace => Workspaces.SelectedItem as WorkspaceChoice ?? throw new InvalidOperationException("请挂载分支或选择工作区。");
    private async Task<JsonElement> Call(object request, bool display = true)
    {
        var result = await _client.CallAsync(request);
        if (display && !_disposed) Output.Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        return result;
    }
    private async Task CheckAsync()
    {
        var status = await Call(new { action = "status" });
        if (status.GetProperty("missing").GetArrayLength() > 0 || status.GetProperty("docker").GetProperty("level").GetString() != "ok")
            throw new InvalidOperationException($"LayerFS 环境尚未就绪：{status.GetRawText()}");
        EnvironmentText.Text = $"{_settings.Distribution} · Docker {Text(status.GetProperty("docker"), "version")} · 环境就绪";
    }
    private async Task RefreshAsync()
    {
        var selected = Projects.SelectedItem as string;
        var result = await Call(new { action = "projects" }, false);
        var names = result.EnumerateArray().Select(item => Text(item, "name")).ToList();
        _updating = true;
        try
        {
            Projects.ItemsSource = names;
            Projects.SelectedItem = names.Contains(selected!) ? selected : names.FirstOrDefault();
            EmptyHint.IsVisible = names.Count == 0;
            ClearProjectState();
        }
        finally { _updating = false; }
        if (names.Count > 0) await StateAsync();
    }
    private async Task InitAsync()
    {
        var project = Required(ProjectName);
        await Call(new { action = "init", project, source = LayerFsClient.ToLinuxPath(Required(Source), _settings.Distribution) });
        await RefreshAsync();
        _updating = true;
        try { Projects.SelectedItem = project; }
        finally { _updating = false; }
        await StateAsync();
    }
    private void ClearProjectState()
    {
        Branches.ItemsSource = Commits.ItemsSource = Sandboxes.ItemsSource = Workspaces.ItemsSource = null;
        HistoryHint.Text = "选择分支以查看提交。";
    }
    private async Task StateAsync()
    {
        var branch = (Branches.SelectedItem as ListBoxItem)?.Tag as string;
        var sandbox = (Sandboxes.SelectedItem as SandboxChoice)?.Id;
        var state = await Call(new { action = "state", project = Project }, false);
        _updating = true;
        try
        {
            var rows = state.GetProperty("branches").EnumerateArray().Select(item => Item(Text(item, "name"), Short(Text(item, "head")), Text(item, "name"))).ToList();
            Branches.ItemsSource = rows;
            Branches.SelectedItem = rows.FirstOrDefault(row => Equals(row.Tag, branch)) ?? rows.FirstOrDefault();
            var sandboxes = state.GetProperty("sandboxes").EnumerateArray().Select(item => new SandboxChoice(Text(item, "id"),
                item.GetProperty("workspaces").EnumerateArray().Select(ws => new WorkspaceChoice(Text(item, "id"), Text(ws, "path"), Text(ws, "branch"))).ToList())).ToList();
            Sandboxes.ItemsSource = sandboxes;
            Sandboxes.SelectedItem = sandboxes.FirstOrDefault(item => item.Id == sandbox) ?? sandboxes.FirstOrDefault();
            SelectSandbox();
        }
        finally { _updating = false; }
        await CommitsAsync();
    }
    private void SelectSandbox()
    {
        var path = (Workspaces.SelectedItem as WorkspaceChoice)?.Path;
        var workspaces = (Sandboxes.SelectedItem as SandboxChoice)?.Workspaces ?? [];
        Workspaces.ItemsSource = workspaces;
        Workspaces.SelectedItem = workspaces.FirstOrDefault(item => item.Path == path) ?? workspaces.FirstOrDefault();
    }
    private async Task ForkAsync()
    {
        await Call(new { action = "fork", project = Project, branch = Required(BranchName) });
        await StateAsync();
    }
    private async Task CommitsAsync()
    {
        JsonElement result;
        try { result = await Call(new { action = "commits", project = Project, branch = Branch }, false); }
        catch (InvalidOperationException error) when (error.Message.StartsWith("NoSuchBranch", StringComparison.Ordinal))
        {
            // main 这类尚无提交的分支，LayerFS 以 NoSuchBranch 回答；空历史不是错误。
            Commits.ItemsSource = null;
            HistoryHint.Text = "此分支还没有检查点。";
            return;
        }
        var rows = result.GetProperty("items").EnumerateArray().Select(item => Item(Text(item, "message"), Short(Text(item, "id")), Text(item, "id"))).ToList();
        Commits.ItemsSource = rows;
        HistoryHint.Text = rows.Count == 0 ? "此分支还没有检查点。" : $"显示 {rows.Count} 条提交" + (result.GetProperty("hasMore").GetBoolean() ? "，还有更多。" : "。");
    }
    private async Task CreateSandboxAsync()
    {
        var created = await Call(new { action = "createSandbox", project = Project });
        var id = Text(created, "sandbox");
        await StateAsync();
        Sandboxes.SelectedItem = Sandboxes.ItemsSource!.Cast<SandboxChoice>().FirstOrDefault(item => item.Id == id);
    }
    private async Task MountAsync()
    {
        var mount = await Call(new { action = "mount", project = Project, sandbox = Sandbox.Id, branch = Branch });
        var path = Text(mount, "path");
        await StateAsync();
        Workspaces.SelectedItem = Workspaces.ItemsSource!.Cast<WorkspaceChoice>().FirstOrDefault(item => item.Path == path);
    }
    private async Task ExecAsync()
    {
        var ws = Workspace;
        var result = await Call(new { action = "exec", project = Project, sandbox = ws.Sandbox, path = ws.Path, command = Required(Command) });
        if (result.TryGetProperty("exitCode", out var exit) && exit.GetInt32() != 0)
            throw new InvalidOperationException($"命令退出码 {exit.GetInt32()}。\n{result.GetRawText()}");
    }
    private async Task CommitAsync()
    {
        var ws = Workspace;
        await Call(new { action = "commit", project = Project, sandbox = ws.Sandbox, path = ws.Path, message = Required(Message) });
        await StateAsync();
    }
    private async Task UnmountAsync()
    {
        var ws = Workspace;
        await Call(new { action = "unmount", project = Project, sandbox = ws.Sandbox, path = ws.Path });
        await StateAsync();
    }
    private async Task CloseSandboxAsync()
    {
        await Call(new { action = "closeSandbox", project = Project, sandbox = Sandbox.Id });
        await StateAsync();
    }
    private static string Text(JsonElement item, string property) => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    private static string Short(string text) => text.Length > 12 ? text[..12] : text;
    private static ListBoxItem Item(string title, string detail, string key)
    {
        var primary = new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis };
        primary.Classes.Add("body");
        var secondary = new TextBlock { Text = detail, TextTrimming = TextTrimming.CharacterEllipsis };
        secondary.Classes.Add("caption");
        var content = new StackPanel { Spacing = 2, Children = { primary, secondary } };
        var row = new ListBoxItem { Content = content, Tag = key };
        ToolTip.SetTip(row, key);
        return row;
    }
    public void SetProject(string? path) { if (!_busy && !_disposed) Source.Text = path ?? ""; }
    public void Dispose() { _disposed = true; _client.Dispose(); }
    private sealed record WorkspaceChoice(string Sandbox, string Path, string Branch)
    {
        public override string ToString() => $"{Branch} · {Path}";
    }
    private sealed record SandboxChoice(string Id, List<WorkspaceChoice> Workspaces)
    {
        public override string ToString() => $"沙箱 {Id} · {Workspaces.Count} 个工作区";
    }
}
