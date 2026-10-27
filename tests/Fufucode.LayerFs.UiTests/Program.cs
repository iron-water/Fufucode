using Avalonia;
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
        Check(!store.GetState("workspace.layerfs").IsInstalled, "Optional plugin starts uninstalled");
        manager.InstallPackage("workspace.layerfs", payload);
        Check(store.GetState("workspace.layerfs").IsInstalled, "Physical package installed");
        if (Arguments.Contains("--install-only")) return;
        var contribution = manager.PageContributions["workspace.layerfs"];
        Check(contribution.PageId == "layerfs", "Page contribution registered");
        var page = (IPluginWorkspacePage)contribution.CreateView();
        page.SetProject(@"C:\Projects\测试 project", false);
        var fields = page.View.GetVisualDescendants().OfType<TextBox>().ToList();
        if (window is not null) { window.Content = page.View; window.Show(); await Task.Delay(300); }
        // Controls may not have a visual parent until attached. Read via logical tree too.
        fields = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(page.View).OfType<TextBox>().ToList();
        Check(fields.Any(box => box.Text == @"C:\Projects\测试 project"), "Host project forwarded to import input");
        if (window is not null)
        {
            async Task Click(string title)
            {
                var button = page.View.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, title));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var deadline = DateTime.UtcNow.AddSeconds(45);
                while (!button.IsEnabled && DateTime.UtcNow < deadline) await Task.Delay(50);
                Check(button.IsEnabled, title + " completed");
            }
            page.View.GetVisualDescendants().OfType<Expander>().Single().IsExpanded = true;
            await Task.Delay(200);
            await Click("检查环境");
            Check(fields.Any(f => f.IsReadOnly && (f.Text?.Contains("\"missing\": []") ?? false)), "Native page displays healthy real runtime");
            await Click("刷新项目");
            var selector = page.View.GetVisualDescendants().OfType<ComboBox>().Single();
            Check(selector.ItemCount > 0, "Native page project list populated");
            await Click("查看状态");
            Check(fields.Any(f => f.IsReadOnly && (f.Text?.Contains("\"branches\"") ?? false)), "Native page displays real project history");
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
            var window = new Window { Title = "LayerFS 原生插件验证", Width = 950, Height = 1080 };
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
