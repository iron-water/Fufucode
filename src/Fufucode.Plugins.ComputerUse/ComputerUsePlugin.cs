using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.ComputerUse;

/// <summary>
/// 电脑操控插件：优先使用宿主注入的 Sky 后端，其次使用 CUA Driver，
/// 最后才使用显式前台 Win32 兼容路径。后台模式不会静默降级到 SendInput。
/// </summary>
public sealed class ComputerUsePlugin : IPluginToolHandler
{
    public const string ScreenshotTool = "computer_screenshot";
    public const string MouseTool = "computer_mouse";
    public const string KeyboardTool = "computer_keyboard";
    public const string CapabilitiesTool = "computer_capabilities";

    private const string ScreenshotSchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"left\":{\"type\":\"integer\"},\"top\":{\"type\":\"integer\"}," +
        "\"width\":{\"type\":\"integer\"},\"height\":{\"type\":\"integer\"}," +
        "\"returnBase64\":{\"type\":\"boolean\",\"description\":\"true 返回 PNG 数据；默认只返回文件信息。\"}," +
        "\"deliveryMode\":{\"type\":\"string\",\"enum\":[\"background\",\"foreground\"],\"default\":\"background\"}," +
        "\"route\":{\"type\":\"string\",\"enum\":[\"auto\",\"sky\",\"cua\",\"win32\",\"visible\"],\"default\":\"auto\"}," +
        "\"processId\":{\"type\":\"integer\"},\"windowId\":{\"type\":\"integer\"}," +
        "\"agentId\":{\"type\":\"string\"},\"session\":{\"type\":\"string\"}},\"required\":[]}";

    private const string MouseSchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"action\":{\"type\":\"string\",\"enum\":[\"move\",\"click\",\"double_click\",\"right_click\",\"scroll\"]}," +
        "\"x\":{\"type\":\"integer\"},\"y\":{\"type\":\"integer\"}," +
        "\"screenX\":{\"type\":\"integer\",\"description\":\"虚拟光标使用的全局屏幕物理像素 X；缺省时回退到 x。\"}," +
        "\"screenY\":{\"type\":\"integer\",\"description\":\"虚拟光标使用的全局屏幕物理像素 Y；缺省时回退到 y。\"}," +
        "\"delta\":{\"type\":\"integer\",\"description\":\"滚动增量，正数向上。\"}," +
        "\"deliveryMode\":{\"type\":\"string\",\"enum\":[\"background\",\"foreground\"],\"default\":\"background\"}," +
        "\"route\":{\"type\":\"string\",\"enum\":[\"auto\",\"sky\",\"cua\",\"win32\",\"visible\"],\"default\":\"auto\"}," +
        "\"processId\":{\"type\":\"integer\",\"description\":\"后台投递目标进程。\"}," +
        "\"windowId\":{\"type\":\"integer\",\"description\":\"后台投递目标窗口。\"}," +
        "\"agentId\":{\"type\":\"string\"},\"elementToken\":{\"type\":\"string\"},\"session\":{\"type\":\"string\"}}," +
        "\"required\":[\"action\"]}";

    private const string KeyboardSchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"action\":{\"type\":\"string\",\"enum\":[\"type\",\"key\",\"hotkey\"]}," +
        "\"text\":{\"type\":\"string\"},\"key\":{\"type\":\"string\"}," +
        "\"keys\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}," +
        "\"deliveryMode\":{\"type\":\"string\",\"enum\":[\"background\",\"foreground\"],\"default\":\"background\"}," +
        "\"route\":{\"type\":\"string\",\"enum\":[\"auto\",\"sky\",\"cua\",\"win32\",\"visible\"],\"default\":\"auto\"}," +
        "\"processId\":{\"type\":\"integer\",\"description\":\"后台投递目标进程。\"}," +
        "\"windowId\":{\"type\":\"integer\",\"description\":\"后台投递目标窗口。\"}," +
        "\"agentId\":{\"type\":\"string\"},\"elementToken\":{\"type\":\"string\"},\"session\":{\"type\":\"string\"}}," +
        "\"required\":[\"action\"]}";

    private readonly string? _outputDirectory;
    private readonly IReadOnlyList<IComputerUseBackend> _backends;
    private readonly IComputerUseOperationGate _operationGate;
    private readonly IComputerUseCursorRegistry _cursorRegistry;

    public ComputerUsePlugin(
        IComputerScreenCapture? capture = null,
        IComputerInput? input = null,
        string? outputDirectory = null,
        IEnumerable<IComputerUseBackend>? backends = null,
        IComputerUseOperationGate? operationGate = null,
        IComputerUseCursorRegistry? cursorRegistry = null)
    {
        _outputDirectory = string.IsNullOrWhiteSpace(outputDirectory) ? null : outputDirectory;
        _operationGate = operationGate ?? ComputerUseOperationGate.Shared;
        _cursorRegistry = cursorRegistry ?? ComputerUseVirtualCursorRegistry.Shared;

        if (backends is not null)
        {
            _backends = backends.Where(static backend => backend is not null).ToArray();
        }
        else
        {
            // CUA Driver is a process-level fallback when the Sky host is absent.
            // Win32 is retained only as a foreground compatibility backend.
            _backends =
            [
                new CuaDriverComputerUseBackend(),
                new Win32ComputerUseBackend(capture, input),
            ];
        }
    }

    public bool ScreenCaptureEnabled { get; init; } = true;
    public bool InputSimulationEnabled { get; init; } = true;
    public static bool IsScreenCaptureSupported => OperatingSystem.IsWindows();

    public PluginCapability? Capability => PluginCapability.ComputerControl;

    public IReadOnlyList<PluginToolRegistration> Registrations { get; } =
    [
        new PluginToolRegistration(ScreenshotTool,
            "截取当前桌面或目标窗口；后台模式不改变用户前台窗口，必要时返回 PNG 数据。",
            ScreenshotSchema, PluginCapability.ComputerControl),
        new PluginToolRegistration(MouseTool,
            "按目标窗口投递鼠标动作。后台模式不移动用户真实鼠标；目标不可后台投递时返回 background_unavailable。",
            MouseSchema, PluginCapability.ComputerControl),
        new PluginToolRegistration(KeyboardTool,
            "按目标窗口投递键盘动作。后台模式不抢用户键盘焦点；需要目标进程与窗口身份。",
            KeyboardSchema, PluginCapability.ComputerControl),
        new PluginToolRegistration(CapabilitiesTool,
            "报告 Sky、CUA Driver 与 Win32 后端的实际可用性和后台/前台能力，不执行输入。",
            "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            PluginCapability.ComputerControl),
    ];

    public async Task<PureToolResult> ExecuteAsync(
        PureToolCall call,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return call.Name switch
            {
                ScreenshotTool => await CaptureAsync(call, cancellationToken).ConfigureAwait(false),
                MouseTool => await ExecuteMouseAsync(call, cancellationToken).ConfigureAwait(false),
                KeyboardTool => await ExecuteKeyboardAsync(call, cancellationToken).ConfigureAwait(false),
                CapabilitiesTool => DescribeCapabilities(call.Id),
                _ => new PureToolResult(call.Id, false, $"未知的电脑操控工具：'{call.Name}'。"),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            return new PureToolResult(call.Id, false, $"工具参数不是合法 JSON：{ex.Message}");
        }
        catch (Exception ex)
        {
            return new PureToolResult(call.Id, false, $"电脑操控执行失败：{ex.Message}");
        }
    }

    private async Task<PureToolResult> CaptureAsync(
        PureToolCall call,
        CancellationToken cancellationToken)
    {
        if (!ScreenCaptureEnabled)
            return new PureToolResult(call.Id, false, "屏幕截图能力当前已关闭（权限或会话策略不允许）。");

        using var doc = JsonDocument.Parse(ArgumentsOrEmpty(call));
        var root = doc.RootElement;
        var bounds = new ScreenRect(
            ReadInt(root, "left"), ReadInt(root, "top"),
            ReadInt(root, "width"), ReadInt(root, "height"));
        var context = ReadContext(root);
        await using var lease = await _operationGate.EnterAsync(
            context.Target?.ResourceKey ?? "desktop", cancellationToken).ConfigureAwait(false);

        var request = new ComputerUseCaptureRequest(
            bounds,
            ReadBool(root, "returnBase64"),
            context);
        var result = await ExecuteWithFallbackAsync(
            (backend, token) => backend.CaptureAsync(request, token),
            context,
            cancellationToken).ConfigureAwait(false);
        if (!result.Success || result.PngBytes is null)
            return Failure(call.Id, result);

        string? savedPath = null;
        if (_outputDirectory is not null)
        {
            try
            {
                Directory.CreateDirectory(_outputDirectory);
                savedPath = Path.Combine(
                    _outputDirectory,
                    $"screenshot-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.png");
                await File.WriteAllBytesAsync(savedPath, result.PngBytes, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new PureToolResult(call.Id, false, $"截图成功但保存文件失败：{ex.Message}");
            }
        }

        var summary = new StringBuilder();
        summary.Append($"{result.Message} 尺寸：{result.Width ?? bounds.Width ?? 0}×{result.Height ?? bounds.Height ?? 0}，{result.PngBytes.Length} 字节。");
        if (savedPath is not null) summary.Append(" 路径：").Append(savedPath);
        if (request.ReturnBase64) summary.Append(" 数据（base64）：").Append(Convert.ToBase64String(result.PngBytes));
        return new PureToolResult(call.Id, true, summary.ToString());
    }

    private async Task<PureToolResult> ExecuteMouseAsync(
        PureToolCall call,
        CancellationToken cancellationToken)
    {
        if (!InputSimulationEnabled)
            return new PureToolResult(call.Id, false, "鼠标模拟执行已被禁用或未获用户授权。");

        using var doc = JsonDocument.Parse(ArgumentsOrEmpty(call));
        var root = doc.RootElement;
        var action = ReadString(root, "action") ?? string.Empty;
        var context = ReadContext(root);
        var x = ReadInt(root, "x");
        var y = ReadInt(root, "y");
        var delta = ReadInt(root, "delta") ?? 0;
        if (action is not ("move" or "click" or "double_click" or "right_click" or "scroll"))
            return Missing(call.Id, $"不支持的鼠标动作 '{action}'。");
        if (action is not "scroll" && (x is null || y is null))
            return Missing(call.Id, $"'{action}' 需要 x 与 y。");
        if (context.DeliveryMode == ComputerUseDeliveryMode.Background && context.Target?.HasWindow != true)
            return Missing(call.Id, "后台鼠标动作需要 processId 与 windowId；没有窗口目标时不会触碰用户真实鼠标。");

        // Background input is isolated per target window; foreground SendInput/UIA
        // activation touches the shared desktop focus, so it stays globally serialized.
        var inputLockKey = context.DeliveryMode == ComputerUseDeliveryMode.Background
            ? $"input:{context.Target?.ResourceKey ?? "desktop"}"
            : "input:foreground";
        await using var lease = await _operationGate.EnterAsync(inputLockKey, cancellationToken).ConfigureAwait(false);

        var screenX = ReadInt(root, "screenX") ?? x;
        var screenY = ReadInt(root, "screenY") ?? y;
        var isClick = action is "click" or "double_click" or "right_click";
        // Show the press before the backend call so the overlay reflects an
        // in-flight click; clear it afterwards whether the backend succeeded or not.
        if (isClick) UpdateVirtualCursor(context, action, screenX, screenY, "按下", pressed: true);

        var request = new ComputerUseMouseRequest(action, x, y, delta, context);
        var result = await ExecuteWithFallbackAsync(
            (backend, token) => backend.MouseAsync(request, token),
            context,
            cancellationToken).ConfigureAwait(false);
        if (isClick) UpdateVirtualCursor(context, action, screenX, screenY, result.Message, pressed: false);
        return result.Success
            ? new PureToolResult(call.Id, true, result.Message)
            : Failure(call.Id, result);
    }

    private async Task<PureToolResult> ExecuteKeyboardAsync(
        PureToolCall call,
        CancellationToken cancellationToken)
    {
        if (!InputSimulationEnabled)
            return new PureToolResult(call.Id, false, "键盘模拟执行已被禁用或未获用户授权。");

        using var doc = JsonDocument.Parse(ArgumentsOrEmpty(call));
        var root = doc.RootElement;
        var action = ReadString(root, "action") ?? string.Empty;
        var context = ReadContext(root);
        var text = ReadString(root, "text");
        var key = ReadString(root, "key");
        var keys = ReadStringList(root, "keys");
        if (action is not ("type" or "key" or "hotkey"))
            return Missing(call.Id, $"不支持的键盘动作 '{action}'。");
        if (action == "type" && string.IsNullOrEmpty(text))
            return Missing(call.Id, "'type' 需要 text。");
        if (action == "key" && string.IsNullOrWhiteSpace(key))
            return Missing(call.Id, "'key' 需要 key。");
        if (action == "hotkey" && keys.Count == 0)
            return Missing(call.Id, "'hotkey' 需要 keys 数组。");
        if (context.DeliveryMode == ComputerUseDeliveryMode.Background && context.Target?.HasWindow != true)
            return Missing(call.Id, "后台键盘动作需要 processId 与 windowId；不会把按键发送给用户当前焦点窗口。");

        var inputLockKey = context.DeliveryMode == ComputerUseDeliveryMode.Background
            ? $"input:{context.Target?.ResourceKey ?? "desktop"}"
            : "input:foreground";
        await using var lease = await _operationGate.EnterAsync(inputLockKey, cancellationToken).ConfigureAwait(false);
        var request = new ComputerUseKeyboardRequest(action, text, key, keys, context);
        var result = await ExecuteWithFallbackAsync(
            (backend, token) => backend.KeyboardAsync(request, token),
            context,
            cancellationToken).ConfigureAwait(false);
        return result.Success
            ? new PureToolResult(call.Id, true, result.Message)
            : Failure(call.Id, result);
    }

    private async Task<ComputerUseBackendResult> ExecuteWithFallbackAsync(
        Func<IComputerUseBackend, CancellationToken, Task<ComputerUseBackendResult>> execute,
        ComputerUseActionContext context,
        CancellationToken cancellationToken)
    {
        ComputerUseBackendResult? last = null;
        foreach (var backend in OrderedBackends(context))
        {
            if (!backend.IsAvailable)
            {
                last = new ComputerUseBackendResult(false,
                    backend.UnavailableReason ?? $"{backend.Kind} 后端不可用。",
                    ErrorCode: "backend_unavailable");
                continue;
            }
            if (context.DeliveryMode == ComputerUseDeliveryMode.Background && !backend.SupportsBackground)
            {
                last = new ComputerUseBackendResult(false,
                    $"{backend.Kind} 后端没有经过验证的后台投递通道。",
                    ErrorCode: "background_unavailable");
                continue;
            }
            if (context.DeliveryMode == ComputerUseDeliveryMode.Foreground && !backend.SupportsForeground)
            {
                last = new ComputerUseBackendResult(false,
                    $"{backend.Kind} 后端不支持前台投递。",
                    ErrorCode: "foreground_unavailable");
                continue;
            }

            last = await execute(backend, cancellationToken).ConfigureAwait(false);
            if (last.Success) return last;
            // An explicit route is a pin, not a preference: do not silently try another stack.
            if (context.Route is not (ComputerUseRouteMode.Auto or ComputerUseRouteMode.Visible))
                return last;
            if (last.ErrorCode is not ("backend_unavailable" or "background_unavailable" or "process_failed"))
                return last;
        }

        return last ?? new ComputerUseBackendResult(false, "没有已配置的电脑操控后端。", ErrorCode: "backend_unavailable");
    }

    /// <summary>
    /// Hybrid routing policy:
    /// - background / auto: strict CUA window delivery first (Sky is foreground-only by contract).
    /// - foreground / visible: Sky first for higher success, then CUA, then Win32 compatibility.
    /// - explicit route pins exactly one backend and never silently substitutes another.
    /// </summary>
    private IEnumerable<IComputerUseBackend> OrderedBackends(ComputerUseActionContext context)
    {
        var pinned = context.Route switch
        {
            ComputerUseRouteMode.Sky => ComputerUseBackendKind.Sky,
            ComputerUseRouteMode.CuaDriver => ComputerUseBackendKind.CuaDriver,
            ComputerUseRouteMode.Win32 => ComputerUseBackendKind.Win32,
            _ => (ComputerUseBackendKind?)null,
        };
        if (pinned is { } kind)
            return _backends.Where(backend => backend.Kind == kind).ToArray();

        var visibleFirst = context.Route == ComputerUseRouteMode.Visible ||
                           context.DeliveryMode == ComputerUseDeliveryMode.Foreground;
        var priorities = visibleFirst
            ? new[]
            {
                ComputerUseBackendKind.Sky,
                ComputerUseBackendKind.CuaDriver,
                ComputerUseBackendKind.Win32,
            }
            : new[]
            {
                ComputerUseBackendKind.CuaDriver,
                ComputerUseBackendKind.Sky,
                ComputerUseBackendKind.Win32,
            };
        return _backends
            .OrderBy(backend => Array.IndexOf(priorities, backend.Kind))
            .ThenBy(backend => backend.Kind)
            .ToArray();
    }

    /// <summary>把鼠标动作同步到虚拟光标层；只更新绘制状态，不产生系统输入。</summary>
    private void UpdateVirtualCursor(
        ComputerUseActionContext context,
        string action,
        int? x,
        int? y,
        string backendMessage,
        bool pressed)
    {
        if (string.IsNullOrWhiteSpace(context.AgentId) || x is null || y is null) return;
        _cursorRegistry.Update(new ComputerUseCursorState(
            context.AgentId,
            x.Value,
            y.Value,
            ColorFor(context.AgentId),
            context.AgentId,
            IsPressed: pressed,
            Backend: backendMessage,
            TargetWindow: context.Target?.HasWindow == true
                ? $"{context.Target.ProcessId}:{context.Target.WindowId}"
                : null));
    }

    private static string ColorFor(string agentId)
    {
        var palette = new[] { "#4F8EF7", "#35C28D", "#F2994A", "#EB5757", "#9B51E0", "#2D9CDB" };
        var hash = 0;
        foreach (var ch in agentId) hash = unchecked(hash * 31 + ch);
        return palette[Math.Abs(hash) % palette.Length];
    }

    private PureToolResult DescribeCapabilities(string id)
    {
        var descriptions = _backends.Select(backend =>
            $"{backend.Kind}: available={backend.IsAvailable}; background={backend.SupportsBackground}; foreground={backend.SupportsForeground}; reason={backend.UnavailableReason ?? "ready"}")
            .ToArray();
        return new PureToolResult(id, true,
            "电脑操控后端能力：" + string.Join(" | ", descriptions) +
            "。路由：background/auto 优先 CUA 窗口投递；foreground/visible 优先 Sky；显式 route=sky|cua|win32 只走指定后端。" +
            " 并发策略：后台输入按目标窗口分桶、前台输入全局串行；不同窗口截图按目标资源隔离。" +
            " agentId 存在时会同步虚拟光标 Overlay，不产生系统输入。后台失败不会自动升级前台。");
    }

    private static ComputerUseActionContext ReadContext(JsonElement root)
    {
        var mode = ReadString(root, "deliveryMode")?.ToLowerInvariant() switch
        {
            "foreground" => ComputerUseDeliveryMode.Foreground,
            _ => ComputerUseDeliveryMode.Background,
        };
        var route = ReadString(root, "route")?.ToLowerInvariant() switch
        {
            "sky" => ComputerUseRouteMode.Sky,
            "cua" or "cua-driver" => ComputerUseRouteMode.CuaDriver,
            "win32" => ComputerUseRouteMode.Win32,
            "visible" => ComputerUseRouteMode.Visible,
            _ => ComputerUseRouteMode.Auto,
        };
        var processId = ReadInt(root, "processId") ?? ReadInt(root, "pid");
        var windowId = ReadLong(root, "windowId") ?? ReadLong(root, "window_id");
        var token = ReadString(root, "elementToken") ?? ReadString(root, "element_token");
        var session = ReadString(root, "session");
        var agentId = ReadString(root, "agentId") ?? ReadString(root, "agent_id");
        var target = processId is not null || windowId is not null || token is not null
            ? new ComputerUseTarget(processId, windowId, token, session)
            : null;
        return new ComputerUseActionContext(mode, target, agentId, route);
    }

    private static PureToolResult Failure(string id, ComputerUseBackendResult result)
    {
        var suffix = result.ErrorCode is { Length: > 0 } code ? $" 错误码：{code}。" : string.Empty;
        if (result.RecommendedForeground)
            suffix += " 该后台路径不可用；如确实允许前台操作，请显式将 deliveryMode 设为 foreground 后重试。";
        return new PureToolResult(id, false, result.Message + suffix);
    }

    private static PureToolResult Missing(string id, string detail) => new(id, false, "参数错误：" + detail);

    private static string ArgumentsOrEmpty(PureToolCall call) =>
        string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson;

    private static int? ReadInt(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) ? parsed : null;

    private static long? ReadLong(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed) ? parsed : null;

    private static bool ReadBool(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static string? ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IReadOnlyList<string> ReadStringList(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                list.Add(text);
        }
        return list;
    }
}

/// <summary>电脑操控插件包入口，被 <see cref="PluginPackageManager"/> 动态实例化。</summary>
public sealed class ComputerUsePluginPackage : IPluginPackage
{
    public string Id => "agent.computer-use";
    public string Version => "1.1.0";

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context)
    {
        var capture = context.GetService<IComputerScreenCapture>();
        var input = context.GetService<IComputerInput>();
        var injected = context.GetService<IComputerUseBackend>();
        var sky = context.GetService<ISkyComputerUseBackend>();
        var backends = new List<IComputerUseBackend>();
        if (sky is not null) backends.Add(sky);
        if (injected is not null) backends.Add(injected);
        backends.Add(new CuaDriverComputerUseBackend());
        backends.Add(new Win32ComputerUseBackend(capture, input));

        var outputDir = context.ProjectPath is not null
            ? Path.Combine(context.ProjectPath, "output", "screenshots")
            : null;
        var cursors = context.GetService<IComputerUseCursorRegistry>();
        return new ComputerUsePlugin(null, null, outputDir, backends, cursorRegistry: cursors);
    }
}
