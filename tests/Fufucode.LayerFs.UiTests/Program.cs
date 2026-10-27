using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Features.Plugins;
using Fufucode.Core;
using Fufucode.Core.Plugins;
using Fufucode.Plugins.LayerFs;

internal static class Program
{
    internal static string[] Arguments = [];
    public static int Main(string[] args)
    {
        Arguments = args;
        if (args.Contains("--native"))
        {
            AppBuilder.Configure<TestApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
            return Environment.ExitCode;
        }
        AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var task = RunAsync(null);
        while (!task.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        task.GetAwaiter().GetResult();
        return 0;
    }

    internal static async Task RunAsync(Window? window)
    {
        var rootIndex = Array.IndexOf(Arguments, "--data-root");
        var root = rootIndex >= 0 ? Arguments[rootIndex + 1] : Path.Combine(Path.GetTempPath(), "fufucode-layerfs-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourceIndex = Array.IndexOf(Arguments, "--payload");
        var payload = sourceIndex >= 0 ? Arguments[sourceIndex + 1] : Path.GetDirectoryName(typeof(LayerFsPluginPackage).Assembly.Location)!;
        var layout = new AppDataLayout(root);
        var store = new PluginStateStore(layout);
        await using var manager = new PluginPackageManager(layout, new PluginToolRegistry(new PluginCapabilityGateway(store)), store);
        if (store.GetState("workspace.layerfs").IsInstalled) manager.UninstallPackage("workspace.layerfs");
        Check(!store.GetState("workspace.layerfs").IsInstalled, "Optional plugin starts uninstalled");
        manager.InstallPackage("workspace.layerfs", payload);
        Check(store.GetState("workspace.layerfs").IsInstalled, "Physical package installed");
        if (Arguments.Contains("--install-only")) return;
        var contribution = manager.PageContributions["workspace.layerfs"];
        Check(contribution.PageId == "layerfs" && contribution.IconKey == "Branch", "Page contribution registered in the workbench catalog shape");
        var page = (IPluginWorkspacePage)contribution.CreateView();
        if (window is not null) { window.Content = page.View; window.Show(); window.Width = 420; window.Height = 1000; }
        var view = (Control)page.View;
        page.SetProject(@"C:\Projects\测试 project", false);
        Control Named(string name) => view.FindControl<Control>(name) ?? throw new Exception("Missing named control: " + name);
        Check(((TextBox)Named("Source")).Text == @"C:\Projects\测试 project", "Host project forwarded to import input");
        Check(Named("Branches") is ListBox && Named("Commits") is ListBox && Named("Sandboxes") is ComboBox,
            "Sidebar page exposes branch, commit and sandbox lists");
        if (window is not null)
        {
            async Task Click(string name)
            {
                var button = (Button)Named(name);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (!button.IsEnabled && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(50); }
                Dispatcher.UIThread.RunJobs();
                Check(button.IsEnabled, name + " completed");
            }
            ((Expander)Named("ConnectionSection")).IsExpanded = true;
            await Click("CheckButton");
            var env = ((TextBlock)Named("EnvironmentText")).Text;
            var status = ((TextBlock)Named("Status")).Text;
            Console.WriteLine($"EnvironmentText=[{env}] Status=[{status}]");
            Check(env?.Contains("环境就绪") == true, "Native page reports a healthy real runtime");
            await Click("RefreshButton");
            var projects = (ComboBox)Named("Projects");
            Check(projects.ItemCount > 0, "Native page project list populated from the Store");
            // 验证数据里最早的项目只有空分支；选时间戳最新的项目，保证有真实检查点可渲染。
            if (projects.Items.OfType<string>().OrderBy(name => name, StringComparer.Ordinal).LastOrDefault() is { } newest)
            {
                projects.SelectedItem = newest;
                var branchesReady = DateTime.UtcNow.AddSeconds(30);
                while (((ListBox)Named("Branches")).ItemCount == 0 && DateTime.UtcNow < branchesReady)
                { Dispatcher.UIThread.RunJobs(); await Task.Delay(50); }
            }
            Check(((ListBox)Named("Branches")).ItemCount > 0, "Project state fills the branch list");
            ((Expander)Named("HistorySection")).IsExpanded = true;
            // main 通常没有提交；选一个带 head 的分支再刷新，验证真实提交列表。
            var branches = (ListBox)Named("Branches");
            var items = branches.Items.OfType<ListBoxItem>().ToList();
            Console.WriteLine($"Project=[{projects.SelectedItem}] Branches=[{string.Join(",", items.Select(item => item.Tag))}]");
            branches.SelectedItem = items.FirstOrDefault(item => (string?)item.Tag == "work")
                ?? items.FirstOrDefault(item => (string?)item.Tag != "main") ?? items.FirstOrDefault();
            Dispatcher.UIThread.RunJobs();
            Console.WriteLine($"Selected=[{(branches.SelectedItem as ListBoxItem)?.Tag}]");
            // 项目/分支选择会各自触发异步刷新，可能互相覆盖；反复重设分支并重新拉取，直到结果稳定。
            var deadline = DateTime.UtcNow.AddSeconds(60);
            var commits = (ListBox)Named("Commits");
            var commitsButton = (Button)Named("CommitsButton");
            while (commits.ItemCount == 0 && DateTime.UtcNow < deadline)
            {
                if (commitsButton.IsEnabled)
                {
                    var items2 = branches.Items.OfType<ListBoxItem>().ToList();
                    var target = items2.FirstOrDefault(item => (string?)item.Tag == "work") ?? items2.LastOrDefault();
                    if (target is not null && !ReferenceEquals(branches.SelectedItem, target)) branches.SelectedItem = target;
                    else commitsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(200);
            }
            Console.WriteLine($"Commits={commits.ItemCount} Hint=[{((TextBlock)Named("HistoryHint")).Text}] Status=[{((TextBlock)Named("Status")).Text}]");
            Check(commits.ItemCount > 0, "Selected branch history shown");
            using var image = new RenderTargetBitmap(new PixelSize((int)window.Width, (int)window.Height), new Vector(96, 96));
            image.Render(window);
            using (var stream = File.Create(Path.Combine(root, "layerfs-native.png"))) image.Save(stream, new PngBitmapEncoderOptions());
            Console.WriteLine("Screenshot: " + Path.Combine(root, "layerfs-native.png"));
        }
        page.Dispose();
        manager.SetPackageEnabled("workspace.layerfs", false);
        Check(!manager.PageContributions.ContainsKey("workspace.layerfs"), "Disable unregisters page");
        manager.SetPackageEnabled("workspace.layerfs", true);
        Check(manager.PageContributions.ContainsKey("workspace.layerfs"), "Enable recreates contribution");
        var retained = Path.Combine(root, "layerfs", "retained.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(retained)!);
        File.WriteAllText(retained, "data retention");
        manager.UninstallPackage("workspace.layerfs");
        Check(!File.Exists(Path.Combine(root, "plugins", "workspace.layerfs", "manifest.json")), "Uninstall removes payload");
        Check(File.Exists(retained), "Uninstall retains plugin data");
        Console.WriteLine("PASS plugin lifecycle and page verification");
    }
    private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); }
}

internal sealed class TestApp : Application
{
    public override void Initialize() => Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new Window { Title = "LayerFS 侧边栏验证", Width = 420, Height = 1000 };
            desktop.MainWindow = window;
            window.Opened += async (_, _) =>
            {
                try { await Program.RunAsync(window); Environment.ExitCode = 0; }
                catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
                desktop.Shutdown(Environment.ExitCode);
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
