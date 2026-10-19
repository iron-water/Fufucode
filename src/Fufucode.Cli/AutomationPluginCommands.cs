using System.Reflection;
using Fufucode.App.Abstractions.Automation;
using Fufucode.Core;
using Fufucode.Core.Plugins;

namespace Fufucode.Cli;

/// <summary>
/// 自动开发命令的宿主侧入口：CLI 不静态引用插件。插件未安装或已停用时给出真实原因，
/// 不内置替代实现、不静默改走另一套数据库/宿主。
/// </summary>
public static class AutomationPluginCommands
{
    private const string PluginId = "automation.auto-dev";
    private static IAutomationCommandHost? _host;

    public static async Task<int> RunControlAsync(CliOptions options, TextWriter output, CancellationToken ct)
    {
        if (options.SessionId is not null)
            throw new ArgumentException("control 命令不接受会话参数。");
        return await Load().RunControlAsync([.. options.Arguments], output, ct);
    }

    public static async Task<int> RunHostAsync(CliOptions options, TextWriter output, CancellationToken ct)
    {
        if (options.SessionId is not null)
            throw new ArgumentException("host 命令不接受会话参数。");
        return await Load().RunHostAsync([.. options.Arguments], output, ct);
    }

    private static IAutomationCommandHost Load()
    {
        if (_host is not null) return _host;
        // 测试/便携部署可用环境变量指向隔离的数据根；产品路径仍由 AppDataLayout 决定。
        var layout = new AppDataLayout(Environment.GetEnvironmentVariable("FUFUCODE_DATA_ROOT"));
        var state = new PluginStateStore(layout).GetState(PluginId);
        if (!state.IsInstalled)
            throw new InvalidOperationException(
                "自动开发工作台未安装。请先安装插件：fufucode plugins install automation.auto-dev --from <zip|url>。");
        if (!state.IsEnabled)
            throw new InvalidOperationException(
                "自动开发工作台已安装但已停用。请在设置或插件市场中启用 automation.auto-dev。");

        var pluginDirectory = layout.PluginDirectory(PluginId);
        var payload = Path.Combine(pluginDirectory, "Fufucode.Plugins.AutoDev.dll");
        if (!File.Exists(payload))
            throw new InvalidOperationException("自动开发插件载荷不完整：" + payload);

        // 只加载清单声明的命令入口，避免在 CLI 进程里枚举/触碰插件中的 Avalonia 页面类型。
        var manifestPath = Path.Combine(pluginDirectory, "manifest.json");
        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
        var typeName = manifest.RootElement.TryGetProperty("commandHostType", out var hostType)
            ? hostType.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(typeName))
            throw new InvalidOperationException("自动开发插件清单未声明 commandHostType。");

        var assembly = Assembly.LoadFrom(payload);
        var type = assembly.GetType(typeName, throwOnError: false)
            ?? throw new InvalidOperationException("自动开发插件命令入口类型不存在：" + typeName);
        if (!typeof(IAutomationCommandHost).IsAssignableFrom(type) || type.IsAbstract)
            throw new InvalidOperationException("自动开发插件命令入口未实现 IAutomationCommandHost。");
        _host = (IAutomationCommandHost)Activator.CreateInstance(type)!;
        return _host;
    }
}
