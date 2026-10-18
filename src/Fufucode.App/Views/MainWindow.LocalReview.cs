using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Fufucode.App.Features.LocalReview;
using Fufucode.App.Infrastructure;
using Fufucode.App.Integration;
using Fufucode.Core;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;
using Fufucode.Core.Features.LocalReview;

namespace Fufucode.App;

public partial class MainWindow
{
    private LocalReviewPageView CreateLocalReviewPage()
    {
        var root = Path.Combine(Program.SelfTestDataRoot ?? _options.CreateAppDataLayout().Root, "local-review");
        var vm = new LocalReviewViewModel(root,
            _options.Mode == AppSessionMode.Mock ? null : new DesktopReviewExecutor(this, root), new ReviewHost(this));
        vm.ProjectPath = _host?.ProjectPath ?? _options.ProjectPath ?? Environment.CurrentDirectory;
        var page = new LocalReviewPageView();
        page.Attach(vm);
        return page;
    }

    private void UpdateLocalReviewProject(string path)
    {
        if (_workbenchPages.ContainsKey("local-review"))
        {
            var vm = ((LocalReviewPageView)_workbenchPages["local-review"].Content).ViewModel!;
            vm.ProjectPath = path;
            vm.RefreshExecutorContext();
        }
    }

    private void ApplyLocalReviewModuleState(bool enabled)
    {
        _workbenchPages.SetEnabled("local-review", enabled, content =>
        {
            foreach (var dock in AllDockHosts()) dock.DetachPage(content);
            _parkedPages.Children.Remove(content);
        });
        RenderDockPages();
        _actions.RefreshCapabilities();
    }

    private string SetLocalReviewModuleEnabled(bool enabled)
    {
        ApplyLocalReviewModuleState(enabled);
        _modulePreferencesCanWrite = true;
        if (_host is null) return (enabled ? "已启用" : "已停用") + " · 当前演示窗口";
        try { SaveModulePreferences(); return (enabled ? "已启用" : "已停用") + " · 已保存到此项目"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        { return "状态已变更 · 保存失败：" + error.Message; }
    }

    /// <summary>The click captures an explicit model/runtime. No default-model launch or mock review.</summary>
    private sealed class DesktopReviewExecutor(MainWindow window, string root) : ILocalReviewExecutor
    {
        public string Model => window._host?.Chat is Fufucode.App.Abstractions.IBackendChatSession backend
            ? backend.BackendName + " · " + (window._host?.Chat.CurrentModel ?? "尚未选择模型")
            : "尚未选择模型";

        public async Task<LocalReviewRunResult> ReviewAsync(LocalReviewScopeResolution scope, CancellationToken cancellationToken = default)
        {
            var host = window._host;
            var model = host?.Chat.CurrentModel;
            if (host is null || string.IsNullOrWhiteSpace(model))
                return Failure(scope, "请先连接真实会话并选择模型；审查不会自动选择模型。");
            if (!string.Equals(AppDataLayout.NormalizeProjectPath(scope.ProjectPath), host.ProjectPath, StringComparison.OrdinalIgnoreCase))
                return Failure(scope, "审查目录与当前项目不同，请先打开该项目。");
            try
            {
                var (node, cli) = RealSessionBootstrap.ResolveRuntime(window._options);
                var manifest = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cli)!, "..", "..", "package.json"));
                using var package = JsonDocument.Parse(await File.ReadAllTextAsync(manifest, cancellationToken));
                var version = package.RootElement.GetProperty("version").GetString() ?? throw new InvalidDataException("Pi version missing");
                // The payload contains the frozen bytes and patch. Disable every tool, extensions,
                // skills and MCP rather than treating a different cwd as filesystem isolation.
                var runtime = new HarnessRuntime("desktop-local-review-pi", "pi", node, [cli, "--no-tools"], version,
                    new Dictionary<string, string> { ["PI_CODING_AGENT_DIR"] = host.AgentDirectory });
                var projectId = host.Current.Session.ProjectId;
                var database = Path.Combine(root, "projects", projectId, "automation.db");
                var executor = new LocalReviewHarnessExecutor(() => new AutomationStore(database), runtime,
                    root, projectId, model);
                return await executor.ReviewAsync(scope, cancellationToken);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
            { return Failure(scope, "审查运行环境不可用：" + error.Message); }
        }

        private LocalReviewRunResult Failure(LocalReviewScopeResolution scope, string error) =>
            new(scope, null, LocalReviewRunStatus.Failed, [], [], Model, null, error, false, DateTimeOffset.UtcNow);
    }

    private sealed class ReviewHost(MainWindow window) : ILocalReviewHostRequests
    {
        public void OpenWorktree(string path) => OpenExternal(path);
        public void JumpToLocation(string path, int line) => Dispatcher.UIThread.Post(() => _ = window.ShowReviewLocationAsync(path, line));
        public async Task<string?> PickExportPathAsync(string name)
        {
            if (!window.StorageProvider.CanSave) return null;
            var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            { Title = "导出本地审查", SuggestedFileName = name });
            return file?.TryGetLocalPath();
        }
        public async Task CopyToClipboardAsync(string text)
        {
            if (window.Clipboard is null) throw new InvalidOperationException("剪贴板不可用");
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(window.Clipboard, text);
        }
    }

    private async Task ShowReviewLocationAsync(string path, int line)
    {
        try
        {
            var content = await Task.Run(() =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > GitDiffSnapshot.MaximumFileBytes) throw new IOException("文件过大，请使用外部编辑器");
                var bytes = new byte[(int)stream.Length];
                stream.ReadExactly(bytes);
                return System.Text.Encoding.UTF8.GetString(bytes);
            });
            var text = new TextBox { Text = content, IsReadOnly = true, AcceptsReturn = true,
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
                FontFamily = new Avalonia.Media.FontFamily("Consolas") };
            var start = 0;
            for (var i = 1; i < line; i++) { var next = content.IndexOf('\n', start); if (next < 0) break; start = next + 1; }
            var end = content.IndexOf('\n', start);
            text.CaretIndex = start;
            text.SelectionStart = start;
            text.SelectionEnd = end < 0 ? content.Length : end;
            var viewer = new Window { Title = Path.GetFileName(path) + ":" + line + " · 只读定位", Width = 850, Height = 650,
                Content = text };
            viewer.Opened += (_, _) => text.Focus();
            viewer.Show(this);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { await ShowTextAsync("无法打开文件", error.Message); }
    }
}
