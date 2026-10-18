using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.ComputerUse;

/// <summary>
/// 电脑操控插件（agent.computer-use）的基础工具集：
/// Win32 屏幕截图与鼠标/键盘输入模拟。
/// 作为独立物理插件包编译，不静态包含在核心默认程序集中。
/// </summary>
public sealed class ComputerUsePlugin : IPluginToolHandler
{
    public const string ScreenshotTool = "computer_screenshot";
    public const string MouseTool = "computer_mouse";
    public const string KeyboardTool = "computer_keyboard";

    private const string ScreenshotSchema =
        "{\"type\":\"object\",\"properties\":{\"left\":{\"type\":\"integer\"},\"top\":{\"type\":\"integer\"}," +
        "\"width\":{\"type\":\"integer\"},\"height\":{\"type\":\"integer\"}," +
        "\"returnBase64\":{\"type\":\"boolean\",\"description\":\"true 返回 PNG 数据；默认只返回文件信息。\"}}," +
        "\"required\":[]}";

    private const string MouseSchema =
        "{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\",\"enum\":[\"move\",\"click\",\"double_click\",\"right_click\",\"scroll\"]}," +
        "\"x\":{\"type\":\"integer\"},\"y\":{\"type\":\"integer\"}," +
        "\"delta\":{\"type\":\"integer\",\"description\":\"滚动增量，正数向上。\"}},\"required\":[\"action\"]}";

    private const string KeyboardSchema =
        "{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\",\"enum\":[\"type\",\"key\",\"hotkey\"]}," +
        "\"text\":{\"type\":\"string\"},\"key\":{\"type\":\"string\"},\"keys\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}}," +
        "\"required\":[\"action\"]}";

    private readonly IComputerScreenCapture _capture;
    private readonly IComputerInput _input;
    private readonly string? _outputDirectory;

    public ComputerUsePlugin(
        IComputerScreenCapture? capture = null,
        IComputerInput? input = null,
        string? outputDirectory = null)
    {
        _capture = capture ?? (OperatingSystem.IsWindows() ? new Win32ScreenCapture() : UnsupportedScreenCapture.Instance);
        _input = input ?? (OperatingSystem.IsWindows() ? new Win32InputSimulator() : UnsupportedInput.Instance);
        _outputDirectory = string.IsNullOrWhiteSpace(outputDirectory) ? null : outputDirectory;
    }

    public bool ScreenCaptureEnabled { get; init; } = true;
    public bool InputSimulationEnabled { get; init; } = true;
    public static bool IsScreenCaptureSupported => OperatingSystem.IsWindows();

    public PluginCapability? Capability => PluginCapability.ComputerControl;

    public IReadOnlyList<PluginToolRegistration> Registrations { get; } = new[]
    {
        new PluginToolRegistration(ScreenshotTool,
            "截取当前桌面或指定矩形区域的屏幕图像；返回保存路径与尺寸，必要时返回 PNG 数据。",
            ScreenshotSchema, PluginCapability.ComputerControl),
        new PluginToolRegistration(MouseTool,
            "模拟鼠标移动、左键/右键单击、双击与滚轮滚动。",
            MouseSchema, PluginCapability.ComputerControl),
        new PluginToolRegistration(KeyboardTool,
            "模拟键盘输入：键入文本、按下单个按键或组合快捷键。",
            KeyboardSchema, PluginCapability.ComputerControl),
    };

    public async Task<PureToolResult> ExecuteAsync(PureToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return call.Name switch
            {
                ScreenshotTool => await CaptureAsync(call, cancellationToken).ConfigureAwait(false),
                MouseTool => ExecuteMouse(call),
                KeyboardTool => await ExecuteKeyboardAsync(call, cancellationToken).ConfigureAwait(false),
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

    private async Task<PureToolResult> CaptureAsync(PureToolCall call, CancellationToken cancellationToken)
    {
        if (!ScreenCaptureEnabled)
            return new PureToolResult(call.Id, false, "屏幕截图能力当前已关闭（权限或会话策略不允许）。");

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var root = doc.RootElement;
        var bounds = new ScreenRect(
            root.TryGetProperty("left", out var l) && l.TryGetInt32(out var left) ? left : null,
            root.TryGetProperty("top", out var t) && t.TryGetInt32(out var top) ? top : null,
            root.TryGetProperty("width", out var w) && w.TryGetInt32(out var width) ? width : null,
            root.TryGetProperty("height", out var h) && h.TryGetInt32(out var height) ? height : null);
        var returnBase64 = root.TryGetProperty("returnBase64", out var rb) && rb.ValueKind == JsonValueKind.True;

        var result = await _capture.CaptureAsync(bounds, cancellationToken).ConfigureAwait(false);
        if (!result.Success || result.PngBytes is null)
            return new PureToolResult(call.Id, false, result.Error ?? "屏幕截图失败。");

        string? savedPath = null;
        if (_outputDirectory is not null)
        {
            try
            {
                Directory.CreateDirectory(_outputDirectory);
                savedPath = Path.Combine(_outputDirectory, $"screenshot-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.png");
                await File.WriteAllBytesAsync(savedPath, result.PngBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new PureToolResult(call.Id, false, $"截图成功但保存文件失败：{ex.Message}");
            }
        }

        var summary = new StringBuilder();
        summary.Append($"屏幕截图成功：{result.Width ?? bounds.Width ?? 0}×{result.Height ?? bounds.Height ?? 0}，{result.PngBytes.Length} 字节。");
        if (savedPath is not null) summary.Append(" 路径：").Append(savedPath);
        if (returnBase64) summary.Append(" 数据（base64）：").Append(Convert.ToBase64String(result.PngBytes));
        return new PureToolResult(call.Id, true, summary.ToString());
    }

    private PureToolResult ExecuteMouse(PureToolCall call)
    {
        if (!InputSimulationEnabled)
            return new PureToolResult(call.Id, false, "鼠标模拟执行已被禁用或未获用户授权。");

        using var doc = JsonDocument.Parse(call.ArgumentsJson);
        var root = doc.RootElement;
        var action = ReadString(root, "action") ?? string.Empty;
        var x = ReadInt(root, "x");
        var y = ReadInt(root, "y");
        var delta = ReadInt(root, "delta") ?? 0;

        switch (action)
        {
            case "move":
                if (x is null || y is null) return Missing(call.Id, "'move' 需要 x 与 y。");
                return Result(call.Id, _input.Move(x.Value, y.Value), "移动鼠标");
            case "click":
                if (x is null || y is null) return Missing(call.Id, "'click' 需要 x 与 y。");
                return Result(call.Id, _input.Click(x.Value, y.Value, MouseButton.Left), "左键单击");
            case "right_click":
                if (x is null || y is null) return Missing(call.Id, "'right_click' 需要 x 与 y。");
                return Result(call.Id, _input.Click(x.Value, y.Value, MouseButton.Right), "右键单击");
            case "double_click":
                if (x is null || y is null) return Missing(call.Id, "'double_click' 需要 x 与 y。");
                return Result(call.Id, _input.DoubleClick(x.Value, y.Value), "双击");
            case "scroll":
                return Result(call.Id, _input.Scroll(delta), "滚轮滚动");
            default:
                return Missing(call.Id, $"不支持的鼠标动作 '{action}'。");
        }
    }

    private async Task<PureToolResult> ExecuteKeyboardAsync(PureToolCall call, CancellationToken cancellationToken)
    {
        if (!InputSimulationEnabled)
            return new PureToolResult(call.Id, false, "键盘模拟执行已被禁用或未获用户授权。");

        using var doc = JsonDocument.Parse(call.ArgumentsJson);
        var root = doc.RootElement;
        var action = ReadString(root, "action") ?? string.Empty;
        switch (action)
        {
            case "type":
                var text = ReadString(root, "text");
                if (string.IsNullOrEmpty(text)) return Missing(call.Id, "'type' 需要 text。");
                if (!_input.TypeText(text)) return new PureToolResult(call.Id, false, "键盘输入模拟失败。");
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                return new PureToolResult(call.Id, true, $"已键入 {text.Length} 个字符。");
            case "key":
                var key = ReadString(root, "key");
                if (string.IsNullOrWhiteSpace(key)) return Missing(call.Id, "'key' 需要 key。");
                return Result(call.Id, _input.PressKey(key), $"按下按键 {key}");
            case "hotkey":
                var keys = ReadStringList(root, "keys");
                if (keys.Count == 0) return Missing(call.Id, "'hotkey' 需要 keys 数组。");
                return Result(call.Id, _input.Hotkey(keys), $"按下组合键 {string.Join("+", keys)}");
            default:
                return Missing(call.Id, $"不支持的键盘动作 '{action}'。");
        }
    }

    private static PureToolResult Result(string id, bool ok, string what) =>
        new(id, ok, ok ? $"{what} 已执行。" : $"{what} 执行失败。");

    private static PureToolResult Missing(string id, string detail) => new(id, false, "参数错误：" + detail);

    private static int? ReadInt(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) ? parsed : null;

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
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text) list.Add(text);
        }
        return list;
    }
}

/// <summary>
/// 电脑操控插件包入口，被 <see cref="PluginPackageManager"/> 动态实例化。
/// </summary>
public sealed class ComputerUsePluginPackage : IPluginPackage
{
    public string Id => "agent.computer-use";
    public string Version => "1.0.0";

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context)
    {
        var capture = context.GetService<IComputerScreenCapture>();
        var input = context.GetService<IComputerInput>();
        var outputDir = context.ProjectPath is not null
            ? Path.Combine(context.ProjectPath, "output", "screenshots")
            : null;
        return new ComputerUsePlugin(capture, input, outputDir);
    }
}
