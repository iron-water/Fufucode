using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;

namespace Fufucode.App.SelfTest;

/// <summary>
/// 变更面板的真实渲染证据：临时 git 仓库里放一个多 hunk 的 C# 文件，
/// 分别按窄栏（右停靠默认宽度）与宽栏各拍一张，用来核对行号、底色、折叠与语法着色。
/// 只写 tmp/ 下的隔离目录，不碰产品数据。
/// </summary>
public static class ChangesPanelShots
{
    public static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool ok, string label) { if (ok) pass("变更面板截图：" + label); else fail("变更面板截图：" + label); }

        var root = Path.Combine(Path.GetTempPath(), "fufucode-changes-shots-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        // 证据留在仓库 tmp/ 下，跑完不被清掉，便于人工核对效果。
        var evidence = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "tmp", "changes-shots"));
        if (Directory.Exists(evidence)) Directory.Delete(evidence, recursive: true);
        Directory.CreateDirectory(evidence);
        try
        {
            if (!await PrepareRepositoryAsync(project))
            {
                Console.WriteLine("SKIP 变更面板截图：git 不可用");
                return;
            }
            var previousRoot = Program.SelfTestDataRoot;
            Program.SelfTestDataRoot = Path.Combine(root, "appdata");
            var window = new MainWindow { Width = 1280, Height = 860 };
            window.Show();
            try
            {
                await Ready();
                var vm = (MainViewModel)window.DataContext!;
                var workspace = vm.WorkspacePanel;
                workspace.AttachContext(project, "shots",
                    ct => Core.ChangeRangeQuery.WorkspaceAsync(project, ct),
                    ct => Core.ChangeRangeQuery.WorkspaceAsync(project, ct));
                await workspace.RefreshAsync();
                Check(workspace.Changes.Count >= 2, "真实仓库里读到多个变更文件");

                window.OpenDockPage("changes", Fufucode.App.Infrastructure.DockZone.Right);
                await Ready();
                Save(window, evidence, "01-changes-list-narrow.png");

                var changes = window.PageForTesting("changes");
                var list = changes.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ChangesList");
                var target = workspace.Changes.FirstOrDefault(f => f.Name == "Widget.cs") ?? workspace.Changes[0];
                list.SelectedItem = target;
                workspace.SelectedChange = target;
                await Until(() => workspace.DiffModel is not null);
                window.UpdateLayout();
                await Task.Delay(120);
                Check(workspace.DiffModel!.Rows.Any(r => r.IsAdded) && workspace.DiffModel.Rows.Any(r => r.IsRemoved),
                    "选中文件后 diff 同时包含新增与删除行");
                Check(workspace.DiffModel.Rows.Any(r => r.IsHeader), "diff 含 hunk 头");
                Check(workspace.DiffModel.Rows.Where(r => r.IsBody).Any(r => r.Runs.Any(run => run.Foreground is not null)),
                    "diff 文本带语法着色片段");
                Save(window, evidence, "02-diff-narrow.png");

                // 宽栏：把右栏拉到 700，检查行号列、底色与代码不被挤爆。
                window.CustomizationForTesting.EditLayout(l => l.RightWidth = 720);
                await Ready();
                await Task.Delay(160);
                window.UpdateLayout();
                Check(window.FindControl<Border>("RightPanelBorder")!.Bounds.Width >= 600, "宽栏下变更页跟随栏宽");
                Save(window, evidence, "03-diff-wide.png");
                vm.Settings.ThemeIndex = 2;   // 2 = 深色，走产品自己的主题通道
                await Ready();
                await Task.Delay(200);
                window.UpdateLayout();
                Save(window, evidence, "04-diff-wide-dark.png");
                vm.Settings.ThemeIndex = 1;   // 1 = 浅色
                await Ready();
                await Task.Delay(200);
                // 行底色要铺满视口，而不是只铺到文字末尾。
                var scroll = window.GetVisualDescendants().OfType<ScrollViewer>()
                    .FirstOrDefault(sv => sv.Name == "DiffScroll");
                var addedBorder = window.GetVisualDescendants().OfType<Border>()
                    .FirstOrDefault(b => b.Classes.Contains("diffRow") && b.Classes.Contains("added"));
                Check(scroll is not null && addedBorder is not null &&
                      Math.Abs(addedBorder.Bounds.Width - scroll.Viewport.Width) < 2,
                    $"新增行底色铺满视口（行 {addedBorder?.Bounds.Width:F0} / 视口 {scroll?.Viewport.Width:F0}）");
                Console.WriteLine("变更面板截图目录：" + evidence);
            }
            finally
            {
                window.Close();
                await Task.Delay(80);
                Program.SelfTestDataRoot = previousRoot;
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }

        static async Task Ready()
        {
            await Task.Delay(120);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        static async Task Until(Func<bool> condition)
        {
            for (var i = 0; i < 80 && !condition(); i++) { await Task.Delay(50); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
            await Task.Delay(80);
        }

        static void Save(Window window, string directory, string name)
        {
            using var frame = window.CaptureRenderedFrame();
            frame?.Save(Path.Combine(directory, name), PngBitmapEncoderOptions.Default);
        }
    }

    /// <summary>一个带多处改动的 C# 文件 + 一个新增文件；够覆盖增删、上下文折叠与着色。</summary>
    private static async Task<bool> PrepareRepositoryAsync(string project)
    {
        if (await Git(project, "init", "-q") != 0) return false;
        await Git(project, "config", "user.email", "fixture@example.test");
        await Git(project, "config", "user.name", "fixture");
        Directory.CreateDirectory(Path.Combine(project, "src"));
        var body = new System.Text.StringBuilder();
        body.AppendLine("namespace Demo;");
        body.AppendLine();
        body.AppendLine("public sealed class Widget");
        body.AppendLine("{");
        for (var i = 0; i < 30; i++) body.AppendLine($"    private int _field{i};");
        body.AppendLine();
        body.AppendLine("    public int Total()");
        body.AppendLine("    {");
        body.AppendLine("        var total = 0;");
        for (var i = 0; i < 20; i++) body.AppendLine($"        total += _field{i};");
        body.AppendLine("        return total;");
        body.AppendLine("    }");
        body.AppendLine("}");
        var path = Path.Combine(project, "src", "Widget.cs");
        await File.WriteAllTextAsync(path, body.ToString());
        await Git(project, "add", ".");
        await Git(project, "commit", "-q", "-m", "base");

        var changed = body.ToString()
            .Replace("        return total;", "        return total + 1; // 修正聚合")
            .Replace("    private int _field7;", "    private int _field7;\n    private string _label = \"widget\";");
        await File.WriteAllTextAsync(path, changed);
        await File.WriteAllTextAsync(Path.Combine(project, "src", "Helper.cs"),
            "namespace Demo;\n\ninternal static class Helper\n{\n    public static int Double(int value) => value * 2;\n}\n");
        return true;
    }

    private static async Task<int> Git(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start);
        if (process is null) return -1;
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}
