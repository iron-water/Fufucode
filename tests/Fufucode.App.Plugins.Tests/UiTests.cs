using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Fufucode.App.Features.Plugins;
using Fufucode.Core;

namespace Fufucode.App.Plugins.Tests;

/// <summary>
/// E3-LOCAL 本地插件页面的无头定向测试（真实 Avalonia 控件 + 编译绑定）：
/// 验证列表/选中/输入/失败返回绑定、临时移出视觉树与重挂不丢状态、重复 Attach 幂等、
/// 最终 Detach 释放视图模型且之后命令空转。
/// 线程纪律：Avalonia 初始化后主线程即 UI 线程，不在主线程 await 依赖 dispatcher 续体的任务；
/// 异步动作通过命令 + Pump 驱动，离线工作用 Task.Run 脱离 UI 线程。
/// </summary>
internal static class UiTests
{
    public static int Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "fufucode-plugins-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            return RunCore(root, check);
        }
        finally
        {
            Console.WriteLine("UI evidence root: " + root);
            if (Environment.GetEnvironmentVariable("E3_KEEP_TMP") is null)
            {
                try { Directory.Delete(root, recursive: true); }
                catch (Exception error) { Console.WriteLine("临时目录保留（清理失败不影响结果）：" + error.GetType().Name); }
            }
        }
    }

    private static int RunCore(string root, Action<bool, string> check)
    {
        // 真实临时插件源目录（Avalonia 初始化前建立，避免 UI 线程等待）。
        const string uiId = "ui.plugin";
        var uiKey = PluginIdRules.Sanitize(uiId, "plugin");
        var source = PluginFixture.Create(root, "ui 插件 目录", uiId, "UI 插件", "1.0.0");
        var dataRoot = Path.Combine(root, "appdata");
        var host = new FakeHost();
        var service = new LocalPluginService(new AppDataLayout(dataRoot), host);
        var picker = new FakePicker { OnDirectory = () => source };

        var builder = AppBuilder.Configure<SmokeApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
        builder.SetupWithoutStarting();

        using var viewModel = new LocalPluginViewModel(service, picker);
        var view = new LocalPluginSectionView();
        view.Attach(viewModel);
        var window = new Window { Width = 1100, Height = 800, Content = view };
        window.Show();

        var passed = 0;
        void Check(bool condition, string name) { check(condition, name); passed++; }

        Check(Pump(() => view.IsAttached && ReferenceEquals(view.ViewModel, viewModel)),
            "UI 无头视图加载页面（真实编译绑定）");

        // ---------- 导入：目录 → 预览 → 导入固定版本 ----------
        viewModel.PickDirectoryCommand.Execute(null);
        Check(Pump(() => viewModel.SourceDirectory.Length > 0), "UI 目录选择经注入委托（不硬编码路径）");
        Check(Pump(() => viewModel.HasPreview && !viewModel.HasPreviewError),
            "UI 预览显示描述文件与兼容结果");
        Check(Pump(() => viewModel.CanImport), "UI 兼容时可导入");

        viewModel.ImportCommand.Execute(null);
        Check(Pump(() => !viewModel.IsBusy && viewModel.Plugins.Count == 1),
            "UI 导入后列表渲染本地副本（真实临时文件）");
        Check(Pump(() => viewModel.Versions.Count == 1 && viewModel.SelectedVersion is not null),
            "UI 版本列表与选中项绑定生效");
        Check(viewModel.StatusText.Contains("未启动会话"), "UI 导入后如实声明未应用、未启动会话");

        var pluginList = view.GetLogicalDescendants().OfType<ListBox>()
            .First(list => AutomationProperties.GetName(list) == "本地插件列表");
        var versionList = view.GetLogicalDescendants().OfType<ListBox>()
            .First(list => AutomationProperties.GetName(list) == "固定版本列表");
        Check(Pump(() => pluginList.ItemCount == 1), "UI 插件列表控件反映视图模型");
        Check(Pump(() => versionList.ItemCount == 1), "UI 版本列表控件反映视图模型");

        // ---------- 应用 + 宿主快照确认后才显示已加载 ----------
        Check(Pump(() => viewModel.CanApply), "UI 兼容且选中版本时可应用");
        viewModel.ApplyCommand.Execute(null);
        Check(Pump(() => !viewModel.IsBusy && host.Applies.Count == 1),
            "UI 应用经宿主回调，不自行改加载目录");
        Check(viewModel.StatusText.Contains("实际加载以宿主安全重启后的真实快照为准"),
            "UI 应用后不伪造运行状态");
        Check(Pump(() => !viewModel.Plugins[0].StatusIsSuccess), "UI 未确认前不显示宿主已加载");

        host.LoadedPaths.Add(Path.Combine(service.Root, uiKey, viewModel.SelectedVersion!.VersionId, "index.js"));
        viewModel.RefreshCommand.Execute(null);
        Check(Pump(() => !viewModel.IsBusy), "UI 刷新后回到空闲");
        Check(viewModel.Plugins[0].StatusIsSuccess,
            "UI 宿主快照确认后显示已加载（状态来自真实快照）");

        // ---------- 失败返回：应用失败保留旧版本与诊断 ----------
        host.OnApply = _ => new HostApplyResult(false, "宿主应用失败：模拟安全重启未通过");
        // 已加载场景下应用只登记 pending；清空加载路径才能验证宿主回调失败这条路径。
        host.LoadedPaths.Clear();
        var selectedBefore = viewModel.SelectedVersion.VersionId;
        viewModel.ApplyCommand.Execute(null);
        Check(Pump(() => !viewModel.IsBusy && viewModel.Plugins[0].HasLoadError),
            "UI 应用失败后显示加载错误");
        Check(viewModel.StatusText.Contains("保留旧选定版本"), "UI 应用失败如实说明保留旧版本");
        Check(viewModel.Plugins[0].Versions.Any(v => v.VersionId == selectedBefore),
            "UI 失败后旧版本仍在列表中");
        host.OnApply = null;

        // ---------- 移除：非活动托管版本可移除；活动只登记 pending ----------
        viewModel.RefreshCommand.Execute(null);
        Check(Pump(() => !viewModel.IsBusy), "UI 移除前刷新回到空闲");
        host.LoadedPaths.Clear();
        viewModel.RefreshCommand.Execute(null);
        Check(Pump(() => !viewModel.IsBusy), "UI 刷新后回到空闲");
        Check(viewModel.CanRemove, "UI 非活动版本可移除");
        viewModel.RemoveCommand.Execute(null);
        Check(Pump(() => !viewModel.IsBusy && viewModel.Plugins.Count == 0),
            "UI 移除后列表清空（只删自己托管的非活动副本）");
        Check(Directory.Exists(source), "UI 移除后外部源目录保留（不删用户插件）");

        // ---------- 快照未知时移除只登记该版本的 pending，且不删任何文件 ----------
        viewModel.PickDirectoryCommand.Execute(null);
        Check(Pump(() => viewModel.CanImport), "UI pending 场景：可再次导入");
        viewModel.ImportCommand.Execute(null);
        Check(Pump(() => !viewModel.IsBusy && viewModel.Plugins.Count == 1), "UI pending 场景：导入完成");
        var pendingVersionId = viewModel.SelectedVersion!.VersionId;
        var pendingFilePath = Path.Combine(service.Root, uiKey, pendingVersionId, "index.js");
        host.SnapshotSuccess = false;
        viewModel.RefreshCommand.Execute(null);
        Check(Pump(() => !viewModel.IsBusy), "UI pending 场景：刷新完成");
        Check(viewModel.CanRemove, "UI 快照未知时仍可发起移除（登记 pending）");
        viewModel.RemoveCommand.Execute(null);
        Check(Pump(() => !viewModel.IsBusy), "UI 移除请求处理完成");
        Check(viewModel.Plugins.Count == 1 && viewModel.Plugins[0].PendingRemoveText.Contains("待移除"),
            "UI 绑定显示待移除目标（绑定具体版本，不是整插件）");
        Check(File.Exists(pendingFilePath), "UI 快照未知时不删除任何文件");
        host.SnapshotSuccess = true;
        viewModel.RefreshCommand.Execute(null);
        Check(Pump(() => !viewModel.IsBusy && viewModel.Plugins[0].Item.PendingRemoveReady),
            "UI 宿主确认未使用后标记可执行移除");
        viewModel.RemoveCommand.Execute(null);
        Check(Pump(() => !viewModel.IsBusy && viewModel.Plugins.Count == 0),
            "UI 显式移除只作用于原目标版本");
        Check(!File.Exists(pendingFilePath), "UI 显式移除后目标文件删除");

        // ---------- 临时移出视觉树不释放、不取消 ----------
        viewModel.PickDirectoryCommand.Execute(null);
        Check(Pump(() => viewModel.CanImport), "UI 再次选择目录后可导入");
        viewModel.ImportCommand.Execute(null);
        window.Content = null;
        PumpTicks(6);
        Check(!viewModel.IsDisposed, "UI 临时移出视觉树不销毁视图模型");
        Check(view.IsAttached && ReferenceEquals(view.ViewModel, viewModel), "UI 移出后仍持有同一视图模型");
        window.Content = view;
        Check(Pump(() => !viewModel.IsBusy && viewModel.Plugins.Count == 1),
            "UI 移出重挂后导入结果正常渲染（未丢状态）");

        // ---------- 窄容器下动作按钮必须可达 ----------
        window.Width = 320;
        Check(Pump(() => view.Bounds.Width > 0 && view.Bounds.Width <= 340), "UI 窗口收窄到停靠栏宽度");
        Check(Pump(() => ActionButtonsReachable(view)), "UI 窄宽度下导入/应用/回退/移除等按钮仍可达");
        window.Width = 1100;
        Check(Pump(() => view.Bounds.Width > 700), "UI 恢复宽布局");
        Check(Pump(() => ActionButtonsReachable(view)), "UI 恢复宽度后按钮仍可达");

        // ---------- 重复 Attach 幂等 + 最终 Detach 释放 ----------
        view.Attach(viewModel);
        view.Attach(viewModel);
        Check(view.IsAttached, "UI 重复 Attach 保持挂载");
        view.Detach();
        Check(!view.IsAttached && view.ViewModel is null, "UI 最终 Detach 清除挂载状态");
        Check(viewModel.IsDisposed, "UI 最终 Detach 释放视图模型");

        var pluginsAfterDispose = viewModel.Plugins.Count;
        var statusAfterDispose = viewModel.StatusText;
        var appliesAfterDispose = host.Applies.Count;
        viewModel.RefreshCommand.Execute(null);
        viewModel.ImportCommand.Execute(null);
        viewModel.ApplyCommand.Execute(null);
        viewModel.RollbackCommand.Execute(null);
        viewModel.RemoveCommand.Execute(null);
        viewModel.ResolvePendingCommand.Execute(null);
        PumpTicks(6);
        Check(viewModel.Plugins.Count == pluginsAfterDispose && viewModel.StatusText == statusAfterDispose,
            "UI 释放后命令空转：不改状态、不刷新列表");
        Check(host.Applies.Count == appliesAfterDispose, "UI 释放后不再调用宿主回调");

        window.Close();
        return passed;
    }

    private static bool ActionButtonsReachable(LocalPluginSectionView view)
    {
        string[] labels = ["选择插件目录…", "选择入口…", "预览", "导入固定版本", "刷新", "应用所选版本",
            "回退到上一版本", "移除所选版本", "按宿主快照确认"];
        var buttons = view.GetLogicalDescendants().OfType<Button>()
            .Where(button => button.Content is string content && labels.Contains(content, StringComparer.Ordinal))
            .ToList();
        if (buttons.Count != labels.Length)
        {
            Console.WriteLine($"[diag] 按钮数量 {buttons.Count}/{labels.Length}");
            return false;
        }

        return buttons.All(button =>
        {
            if (!button.IsVisible || button.Bounds.Width <= 1 || button.Bounds.Height <= 1)
            {
                Console.WriteLine($"[diag] {button.Content} 不可见或零尺寸 bounds={button.Bounds.Size}");
                return false;
            }

            var origin = button.TranslatePoint(new Point(0, 0), view);
            if (origin is null) return false;
            var reachable = origin.Value.X >= -1 && origin.Value.Y >= -1
                && origin.Value.X + button.Bounds.Width <= view.Bounds.Width + 1
                && origin.Value.Y + button.Bounds.Height <= view.Bounds.Height + 1;
            if (!reachable)
                Console.WriteLine($"[diag] {button.Content} 溢出可视区 bounds={button.Bounds.Size} origin={origin} view={view.Bounds.Size}");
            return reachable;
        });
    }

    private static bool Pump(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            Thread.Sleep(10);
        }

        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    private static void PumpTicks(int rounds = 6)
    {
        for (var i = 0; i < rounds; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
    }

    private sealed class SmokeApp : Application
    {
        public override void Initialize()
        {
            Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
            Styles.Add(new StyleInclude(new Uri("avares://Fufucode.App/"))
            {
                Source = new Uri("avares://Fufucode.App/Styles/FufucodeTheme.axaml"),
            });
            Styles.Add(new StyleInclude(new Uri("avares://Fufucode.App/"))
            {
                Source = new Uri("avares://Fufucode.App/Styles/WorkbenchStyles.axaml"),
            });
        }
    }
}
