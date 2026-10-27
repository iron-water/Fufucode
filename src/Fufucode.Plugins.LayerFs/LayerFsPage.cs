using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Fufucode.Plugins.LayerFs;

public sealed class LayerFsPage : UserControl, IDisposable
{
    private readonly string _bridge;
    private readonly string _settingsFile;
    private LayerFsClient? _client;
    private LayerFsSettings _settings = new();
    private readonly TextBlock _status = new() { Text = "连接 WSL2，检查 LayerFS 运行环境。", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _distribution = Input("WSL 发行版");
    private readonly TextBox _user = Input("WSL 用户");
    private readonly TextBox _node = Input("Linux Node 绝对路径");
    private readonly TextBox _config = Input("Linux config.json 绝对路径");
    private readonly TextBox _source = Input("要导入的项目目录");
    private readonly TextBox _name = Input("新项目名，例如 my-project");
    private readonly ComboBox _projects = new() { MinWidth = 180, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _branch = Input("分支名", "work");
    private readonly TextBox _sandbox = Input("沙箱编号，例如 1");
    private readonly TextBox _workspace = Input("挂载路径，例如 /workspaces/1");
    private readonly TextBox _command = Input("在工作区执行的 Linux 命令", "pwd && ls -la");
    private readonly TextBox _message = Input("检查点说明", "保存工作区改动");
    private readonly TextBox _output = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"), MinHeight = 160 };
    private readonly List<Button> _buttons = [];
    private bool _busy;
    private bool _disposed;

    public LayerFsPage(string packageDirectory, string dataRoot)
    {
        _bridge = Path.Combine(packageDirectory, "Bridge", "layerfs-bridge.mjs");
        _settingsFile = Path.Combine(dataRoot, "layerfs", "settings.json");
        LayerFsSettings settings = new();
        try { if (File.Exists(_settingsFile)) settings = JsonSerializer.Deserialize<LayerFsSettings>(File.ReadAllText(_settingsFile)) ?? settings; }
        catch (Exception e) when (e is IOException or JsonException) { _status.Text = $"设置无法读取：{e.Message}"; }
        _distribution.Text = settings.Distribution;
        _user.Text = settings.User;
        _node.Text = settings.Node;
        _config.Text = settings.Config;
        _settings = settings;
        _client = new LayerFsClient(_bridge, settings);

        var body = new StackPanel { Spacing = 14, Margin = new Thickness(18), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Stretch };
        body.Children.Add(new TextBlock { Text = "LayerFS 工作区", FontSize = 22, FontWeight = FontWeight.SemiBold });
        body.Children.Add(_status);
        body.Children.Add(new TextBlock { Text = "将项目导入为独立副本，在分支和沙箱中工作。当前为 Disposable 存储，不保证崩溃或断电后的数据持久性，请保留源项目。",
            TextWrapping = TextWrapping.Wrap, Opacity = .7 });
        body.Children.Add(new Expander { Header = "WSL2 连接设置", HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = Stack(_distribution, _user, _node, _config, Row(Button("保存连接", SaveConnection), Button("检查环境", CheckAsync))) });
        body.Children.Add(Heading("项目"));
        body.Children.Add(Row(_projects, Button("刷新项目", RefreshAsync), Button("查看状态", StateAsync)));
        body.Children.Add(_source);
        body.Children.Add(Row(_name, Button("导入项目", InitAsync)));
        body.Children.Add(Heading("分支与提交"));
        body.Children.Add(Row(_branch, Button("从 main 创建分支", ForkAsync), Button("查看提交", CommitsAsync)));
        body.Children.Add(Heading("沙箱工作区"));
        body.Children.Add(Row(_sandbox, Button("创建沙箱", CreateSandboxAsync), Button("挂载分支", MountAsync)));
        body.Children.Add(_workspace);
        body.Children.Add(_command);
        body.Children.Add(Row(Button("执行命令", ExecAsync), Button("卸载工作区", UnmountAsync), Button("关闭空沙箱", CloseSandboxAsync)));
        body.Children.Add(Row(_message, Button("保存检查点", CommitAsync)));
        body.Children.Add(Heading("操作结果"));
        body.Children.Add(_output);
        body.Children.Add(new TextBlock { Text = "停用或卸载插件会释放桥接进程，保留 WSL 中的项目、Store 和挂载状态。卸载工作区前请先保存检查点。",
            TextWrapping = TextWrapping.Wrap, Opacity = .65 });
        Content = new ScrollViewer { Content = body };
    }

    private static TextBox Input(string watermark, string? text = null) => new() { PlaceholderText = watermark, Text = text,
        MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Stretch };
    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
    private static StackPanel Stack(params Control[] children)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }
    private static WrapPanel Row(params Control[] children)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children) { child.Margin = new Thickness(0, 0, 8, 6); panel.Children.Add(child); }
        return panel;
    }
    private Button Button(string text, Func<Task> action)
    {
        var button = new Button { Content = text };
        button.Click += async (_, _) => await RunAsync(action);
        _buttons.Add(button);
        return button;
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _disposed) return;
        _busy = true;
        foreach (var button in _buttons) button.IsEnabled = false;
        SetInputsEnabled(false);
        _status.Text = "操作进行中…";
        try { await action(); if (!_disposed) _status.Text = "操作完成。"; }
        catch (Exception e) { if (!_disposed) { _status.Text = e.Message; _output.Text = e.ToString(); } }
        finally
        {
            _busy = false;
            foreach (var button in _buttons) button.IsEnabled = !_disposed;
            SetInputsEnabled(!_disposed);
        }
    }
    private void SetInputsEnabled(bool enabled)
    {
        foreach (var field in new[] { _distribution, _user, _node, _config, _source, _name, _branch, _sandbox, _workspace, _command, _message })
            field.IsEnabled = enabled;
        _projects.IsEnabled = enabled;
    }
    private Task SaveConnection()
    {
        var settings = new LayerFsSettings { Distribution = Required(_distribution), User = Required(_user), Node = Required(_node), Config = Required(_config) };
        _ = LayerFsClient.StartInfo(_bridge, settings);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
        var temp = _settingsFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _settingsFile, overwrite: true);
        _client?.Dispose();
        _settings = settings;
        _client = new LayerFsClient(_bridge, settings);
        _output.Text = "连接已保存。点击“检查环境”查看 Docker、镜像和 LayerFS 状态。";
        return Task.CompletedTask;
    }
    private static string Required(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? throw new ArgumentException($"请填写{box.PlaceholderText}。") : box.Text.Trim();
    private string Project => _projects.SelectedItem as string ?? throw new InvalidOperationException("请先刷新并选择 LayerFS 项目。");
    private async Task<JsonElement> Call(object request)
    {
        var result = await _client!.CallAsync(request);
        if (!_disposed) _output.Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        return result;
    }
    private async Task CheckAsync()
    {
        var status = await Call(new { action = "status" });
        if (status.GetProperty("missing").GetArrayLength() > 0 || status.GetProperty("docker").GetProperty("level").GetString() != "ok")
            throw new InvalidOperationException($"LayerFS 环境尚未就绪：{status.GetRawText()}");
    }
    private async Task RefreshAsync()
    {
        var selected = _projects.SelectedItem as string;
        var result = await Call(new { action = "projects" });
        var names = result.EnumerateArray().Select(item => item.GetProperty("name").GetString()!).ToList();
        _projects.ItemsSource = names;
        _projects.SelectedItem = names.Contains(selected!) ? selected : names.FirstOrDefault();
    }
    private async Task InitAsync()
    {
        var source = LayerFsClient.ToLinuxPath(Required(_source), _settings.Distribution);
        await Call(new { action = "init", project = Required(_name), source });
        await RefreshAsync();
    }
    private async Task StateAsync() => await Call(new { action = "state", project = Project });
    private async Task ForkAsync() => await Call(new { action = "fork", project = Project, branch = Required(_branch) });
    private async Task CommitsAsync() => await Call(new { action = "commits", project = Project, branch = Required(_branch) });
    private async Task CreateSandboxAsync()
    {
        var result = await Call(new { action = "createSandbox", project = Project });
        _sandbox.Text = result.GetProperty("sandbox").GetString();
    }
    private async Task MountAsync()
    {
        var result = await Call(new { action = "mount", project = Project, branch = Required(_branch), sandbox = Required(_sandbox) });
        _workspace.Text = result.GetProperty("path").GetString();
    }
    private async Task ExecAsync()
    {
        var result = await Call(new { action = "exec", project = Project, sandbox = Required(_sandbox), path = Required(_workspace), command = Required(_command) });
        if (result.TryGetProperty("exitCode", out var exit) && exit.GetInt32() != 0)
            throw new InvalidOperationException($"命令退出码 {exit.GetInt32()}。\n{result.GetRawText()}");
    }
    private async Task CommitAsync() => await Call(new { action = "commit", project = Project, sandbox = Required(_sandbox), path = Required(_workspace), message = Required(_message) });
    private async Task UnmountAsync() => await Call(new { action = "unmount", project = Project, sandbox = Required(_sandbox), path = Required(_workspace) });
    private async Task CloseSandboxAsync() => await Call(new { action = "closeSandbox", project = Project, sandbox = Required(_sandbox) });
    public void SetProject(string? path) { if (!_busy && !_disposed) _source.Text = path ?? ""; }
    public void Dispose() { _disposed = true; _client?.Dispose(); }
}
