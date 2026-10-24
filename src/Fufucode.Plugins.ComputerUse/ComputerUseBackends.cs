using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.ComputerUse;

/// <summary>
/// 直接复用旧 Win32 实现的前台后端。它只接受显式 Foreground，
/// 防止调用方误把 SendInput 当成后台窗口投递。
/// </summary>
public sealed class Win32ComputerUseBackend : IComputerUseBackend
{
    private readonly IComputerScreenCapture _capture;
    private readonly IComputerInput _input;

    public Win32ComputerUseBackend(
        IComputerScreenCapture? capture = null,
        IComputerInput? input = null)
    {
        _capture = capture ?? (OperatingSystem.IsWindows()
            ? new Win32ScreenCapture()
            : UnsupportedScreenCapture.Instance);
        _input = input ?? (OperatingSystem.IsWindows()
            ? new Win32InputSimulator()
            : UnsupportedInput.Instance);
    }

    public ComputerUseBackendKind Kind => ComputerUseBackendKind.Win32;
    public bool IsAvailable => OperatingSystem.IsWindows();
    public string? UnavailableReason => IsAvailable ? null : "Win32 电脑操控仅支持 Windows。";
    public bool SupportsBackground => false;
    public bool SupportsForeground => true;

    public async Task<ComputerUseBackendResult> CaptureAsync(
        ComputerUseCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            return Unavailable(UnavailableReason!);
        // 截图是只读操作，不移动鼠标、不抢焦点，因此后台模式也可以安全执行。
        // 只有鼠标/键盘等会改变用户现场的动作才要求前台。

        var result = await _capture.CaptureAsync(request.Bounds, cancellationToken).ConfigureAwait(false);
        return result.Success && result.PngBytes is not null
            ? new ComputerUseBackendResult(true, "Win32 屏幕截图成功。", result.PngBytes, result.Width, result.Height)
            : new ComputerUseBackendResult(false, result.Error ?? "Win32 截图失败。", ErrorCode: "capture_failed");
    }

    public Task<ComputerUseBackendResult> MouseAsync(
        ComputerUseMouseRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable)
            return Task.FromResult(Unavailable(UnavailableReason!));
        if (request.Context.DeliveryMode != ComputerUseDeliveryMode.Foreground)
            return Task.FromResult(BackgroundUnavailable());

        var result = request.Action switch
        {
            "move" when request.X is not null && request.Y is not null =>
                _input.Move(request.X.Value, request.Y.Value),
            "click" when request.X is not null && request.Y is not null =>
                _input.Click(request.X.Value, request.Y.Value, MouseButton.Left),
            "right_click" when request.X is not null && request.Y is not null =>
                _input.Click(request.X.Value, request.Y.Value, MouseButton.Right),
            "double_click" when request.X is not null && request.Y is not null =>
                _input.DoubleClick(request.X.Value, request.Y.Value),
            "scroll" => _input.Scroll(request.Delta),
            _ => false,
        };
        return Task.FromResult(result
            ? new ComputerUseBackendResult(true, $"Win32 前台鼠标动作 '{request.Action}' 已执行。")
            : new ComputerUseBackendResult(false, $"Win32 鼠标动作 '{request.Action}' 执行失败。", ErrorCode: "input_failed"));
    }

    public Task<ComputerUseBackendResult> KeyboardAsync(
        ComputerUseKeyboardRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable)
            return Task.FromResult(Unavailable(UnavailableReason!));
        if (request.Context.DeliveryMode != ComputerUseDeliveryMode.Foreground)
            return Task.FromResult(BackgroundUnavailable());

        var result = request.Action switch
        {
            "type" when !string.IsNullOrEmpty(request.Text) => _input.TypeText(request.Text),
            "key" when !string.IsNullOrWhiteSpace(request.Key) => _input.PressKey(request.Key),
            "hotkey" when request.Keys.Count > 0 => _input.Hotkey(request.Keys),
            _ => false,
        };
        return Task.FromResult(result
            ? new ComputerUseBackendResult(true, $"Win32 前台键盘动作 '{request.Action}' 已执行。")
            : new ComputerUseBackendResult(false, $"Win32 键盘动作 '{request.Action}' 执行失败。", ErrorCode: "input_failed"));
    }

    private static ComputerUseBackendResult BackgroundUnavailable() =>
        new(false,
            "当前 Win32 后端只支持前台输入；要后台操控，请安装并启用 CUA Driver 或注入 Sky 后端。",
            ErrorCode: "background_unavailable",
            RecommendedForeground: false);

    private static ComputerUseBackendResult Unavailable(string message) =>
        new(false, message, ErrorCode: "backend_unavailable");
}

/// <summary>
/// CUA Driver CLI 后端。后台动作按目标窗口投递，失败时保留
/// background_unavailable，不自行升级到 foreground。
/// </summary>
public sealed class CuaDriverComputerUseBackend : IComputerUseBackend
{
    private readonly string? _executablePath;
    private readonly TimeSpan _timeout;

    public CuaDriverComputerUseBackend(string? executablePath = null, TimeSpan? timeout = null)
    {
        _executablePath = ResolveExecutable(executablePath);
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public ComputerUseBackendKind Kind => ComputerUseBackendKind.CuaDriver;
    public bool IsAvailable => _executablePath is not null;
    public string? UnavailableReason => IsAvailable
        ? null
        : "未找到 cua-driver；请安装 CUA Driver，或设置 FUFUCODE_CUA_DRIVER_PATH。";
    public bool SupportsBackground => true;
    public bool SupportsForeground => true;

    public Task<ComputerUseBackendResult> CaptureAsync(
        ComputerUseCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Context.DeliveryMode == ComputerUseDeliveryMode.Background &&
            request.Context.Target?.HasWindow != true)
        {
            return Task.FromResult(Failure(
                "CUA Driver 的桌面级截图不能保证后台不干扰；后台模式必须指定目标窗口。",
                "background_unavailable"));
        }

        var tool = request.Context.Target?.HasWindow == true
            ? "get_window_state"
            : "get_desktop_state";
        var payload = request.Context.Target?.HasWindow == true
            ? WindowPayload(request.Context.Target, new Dictionary<string, object?>
            {
                ["include_screenshot"] = true,
                ["include_accessibility_tree"] = false,
            })
            : new Dictionary<string, object?>
            {
                ["include_screenshot"] = true,
            };
        return CallCaptureAsync(tool, payload, cancellationToken);
    }

    public Task<ComputerUseBackendResult> MouseAsync(
        ComputerUseMouseRequest request,
        CancellationToken cancellationToken = default)
    {
        var target = request.Context.Target;
        if (target?.HasWindow != true)
            return Task.FromResult(Failure("后台鼠标动作必须提供 processId 与 windowId。", "target_required"));

        var payload = WindowPayload(target, new Dictionary<string, object?>
        {
            ["delivery_mode"] = DeliveryName(request.Context.DeliveryMode),
        });
        if (request.X is not null) payload["x"] = request.X;
        if (request.Y is not null) payload["y"] = request.Y;
        if (request.Context.Target?.ElementToken is { Length: > 0 } token)
            payload["element_token"] = token;

        string tool;
        switch (request.Action)
        {
            case "move":
                tool = "move_cursor";
                break;
            case "click":
                tool = "click";
                payload["button"] = "left";
                break;
            case "right_click":
                tool = "right_click";
                break;
            case "double_click":
                tool = "double_click";
                break;
            case "scroll":
                tool = "scroll";
                payload["scrollX"] = 0;
                payload["scrollY"] = request.Delta;
                break;
            default:
                return Task.FromResult(Failure($"不支持的鼠标动作 '{request.Action}'。", "invalid_action"));
        }

        return CallInputAsync(tool, payload, cancellationToken);
    }

    public Task<ComputerUseBackendResult> KeyboardAsync(
        ComputerUseKeyboardRequest request,
        CancellationToken cancellationToken = default)
    {
        var target = request.Context.Target;
        if (target?.HasWindow != true)
            return Task.FromResult(Failure("后台键盘动作必须提供 processId 与 windowId。", "target_required"));

        var payload = WindowPayload(target, new Dictionary<string, object?>
        {
            ["delivery_mode"] = DeliveryName(request.Context.DeliveryMode),
        });
        if (target.ElementToken is { Length: > 0 } token)
            payload["element_token"] = token;

        string tool;
        switch (request.Action)
        {
            case "type":
                tool = "type_text";
                payload["text"] = request.Text ?? string.Empty;
                break;
            case "key":
                tool = "press_key";
                payload["key"] = request.Key ?? string.Empty;
                break;
            case "hotkey":
                tool = "hotkey";
                payload["keys"] = request.Keys;
                break;
            default:
                return Task.FromResult(Failure($"不支持的键盘动作 '{request.Action}'。", "invalid_action"));
        }

        return CallInputAsync(tool, payload, cancellationToken);
    }

    private async Task<ComputerUseBackendResult> CallCaptureAsync(
        string tool,
        Dictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        var result = await CallAsync(tool, payload, cancellationToken).ConfigureAwait(false);
        if (!result.Success) return result;

        var bytes = TryReadPng(result.RawJson ?? string.Empty, out var width, out var height);
        if (bytes is null)
            return Failure("CUA Driver 返回成功，但没有可读取的 PNG 截图。", "capture_missing");
        return result with
        {
            Message = $"CUA Driver {tool} 成功。",
            PngBytes = bytes,
            Width = width,
            Height = height,
        };
    }

    private async Task<ComputerUseBackendResult> CallInputAsync(
        string tool,
        Dictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        var result = await CallAsync(tool, payload, cancellationToken).ConfigureAwait(false);
        return result with { Message = result.Success ? $"CUA Driver {tool} 已执行。" : result.Message };
    }

    private async Task<ComputerUseBackendResult> CallAsync(
        string tool,
        Dictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            return Failure(UnavailableReason!, "backend_unavailable");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _executablePath!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };
        process.StartInfo.ArgumentList.Add("call");
        process.StartInfo.ArgumentList.Add(tool);
        process.StartInfo.ArgumentList.Add("-");

        try
        {
            if (!process.Start())
                return Failure("无法启动 cua-driver。", "process_start_failed");

            var requestJson = JsonSerializer.Serialize(payload);
            await process.StandardInput.WriteAsync(requestJson).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var raw = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            return ParseResult(raw, process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception ex)
        {
            TryKill(process);
            return Failure($"调用 cua-driver 失败：{ex.Message}", "process_failed");
        }
    }

    private static ComputerUseBackendResult ParseResult(string raw, int exitCode)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Failure($"cua-driver 没有返回结果（退出码 {exitCode}）。", "empty_response");

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var isError = root.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True;
            var code = FindString(root, "code") ?? FindString(root, "error_code");
            var message = FindString(root, "message") ?? FindString(root, "error") ?? raw.Trim();
            var recommendedForeground = string.Equals(code, "background_unavailable", StringComparison.OrdinalIgnoreCase) ||
                raw.Contains("background_unavailable", StringComparison.OrdinalIgnoreCase);
            if (isError || exitCode != 0)
                return new ComputerUseBackendResult(false, message, ErrorCode: code ?? "driver_error",
                    RecommendedForeground: recommendedForeground, RawJson: raw);
            return new ComputerUseBackendResult(true, message, RawJson: raw);
        }
        catch (JsonException)
        {
            return new ComputerUseBackendResult(exitCode == 0, raw.Trim(),
                ErrorCode: exitCode == 0 ? null : "invalid_response", RawJson: raw);
        }
    }

    private static byte[]? TryReadPng(string raw, out int? width, out int? height)
    {
        width = null;
        height = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return FindPng(doc.RootElement, ref width, ref height, 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static byte[]? FindPng(JsonElement value, ref int? width, ref int? height, int depth)
    {
        if (depth > 8) return null;
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("width", out var w) && w.TryGetInt32(out var wi)) width ??= wi;
            if (value.TryGetProperty("height", out var h) && h.TryGetInt32(out var hi)) height ??= hi;
            foreach (var name in new[] { "screenshot_png_b64", "screenshot_b64", "png_base64" })
            {
                if (value.TryGetProperty(name, out var encoded) && encoded.ValueKind == JsonValueKind.String &&
                    TryDecode(encoded.GetString(), out var bytes)) return bytes;
            }
            foreach (var property in value.EnumerateObject())
            {
                var found = FindPng(property.Value, ref width, ref height, depth + 1);
                if (found is not null) return found;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var found = FindPng(item, ref width, ref height, depth + 1);
                if (found is not null) return found;
            }
        }
        else if (value.ValueKind == JsonValueKind.String && TryDecodeDataUrl(value.GetString(), out var data))
        {
            return data;
        }
        return null;
    }

    private static bool TryDecode(string? value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            bytes = Convert.FromBase64String(value);
            return bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G';
        }
        catch (FormatException) { return false; }
    }

    private static bool TryDecodeDataUrl(string? value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (value is null || !value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return false;
        var separator = value.IndexOf(",", StringComparison.Ordinal);
        return separator >= 0 && TryDecode(value[(separator + 1)..], out bytes);
    }

    private static string? FindString(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty(name, out var direct) && direct.ValueKind == JsonValueKind.String)
                return direct.GetString();
            foreach (var property in value.EnumerateObject())
                if (FindString(property.Value, name) is { } nested) return nested;
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                if (FindString(item, name) is { } nested) return nested;
        }
        return null;
    }

    private static Dictionary<string, object?> WindowPayload(
        ComputerUseTarget target,
        Dictionary<string, object?> payload)
    {
        // CUA Driver's Windows contract documents pid/window_id on the action
        // itself. Avoid adding the optional cross-platform target object to
        // get_window_state, whose strict schema may reject unknown fields.
        payload["pid"] = target.ProcessId;
        payload["window_id"] = target.WindowId;
        if (!string.IsNullOrWhiteSpace(target.Session))
            payload["session"] = target.Session;
        return payload;
    }

    private static string DeliveryName(ComputerUseDeliveryMode mode) =>
        mode == ComputerUseDeliveryMode.Foreground ? "foreground" : "background";

    private static ComputerUseBackendResult Failure(string message, string code) =>
        new(false, message, ErrorCode: code,
            RecommendedForeground: string.Equals(code, "background_unavailable", StringComparison.Ordinal));

    private static string? ResolveExecutable(string? explicitPath)
    {
        var candidate = explicitPath;
        if (string.IsNullOrWhiteSpace(candidate))
            candidate = Environment.GetEnvironmentVariable("FUFUCODE_CUA_DRIVER_PATH");
        if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            return Path.GetFullPath(candidate);
        if (!string.IsNullOrWhiteSpace(explicitPath)) return null;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in OperatingSystem.IsWindows()
                         ? new[] { "cua-driver.exe", "cua-driver" }
                         : new[] { "cua-driver" })
            {
                var full = Path.Combine(directory.Trim(), name);
                if (File.Exists(full)) return Path.GetFullPath(full);
            }
        }
        return null;
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { }
    }
}

/// <summary>
/// Sky 后端的宿主注入适配器。Sky 的 @oai/sky API 运行在 node_repl/Codex
/// 宿主内，插件不猜测其私有 RPC；宿主提供委托后即可复用统一调度契约。
/// </summary>
public sealed class SkyComputerUseBackend : ISkyComputerUseBackend
{
    private readonly Func<ComputerUseCaptureRequest, CancellationToken, Task<ComputerUseBackendResult>>? _capture;
    private readonly Func<ComputerUseMouseRequest, CancellationToken, Task<ComputerUseBackendResult>>? _mouse;
    private readonly Func<ComputerUseKeyboardRequest, CancellationToken, Task<ComputerUseBackendResult>>? _keyboard;

    public SkyComputerUseBackend(
        Func<ComputerUseCaptureRequest, CancellationToken, Task<ComputerUseBackendResult>>? capture,
        Func<ComputerUseMouseRequest, CancellationToken, Task<ComputerUseBackendResult>>? mouse,
        Func<ComputerUseKeyboardRequest, CancellationToken, Task<ComputerUseBackendResult>>? keyboard)
    {
        _capture = capture;
        _mouse = mouse;
        _keyboard = keyboard;
    }

    public ComputerUseBackendKind Kind => ComputerUseBackendKind.Sky;
    public bool IsAvailable => _capture is not null && _mouse is not null && _keyboard is not null;
    // The public @oai/sky window2 API exposes target windows but no delivery_mode.
    // Treat it as foreground-only until a host bridge explicitly proves a background route.
    public bool SupportsBackground { get; init; }
    public bool SupportsForeground => true;
    public string? UnavailableReason => IsAvailable ? null : "Sky 后端未由宿主注入；@oai/sky 只能在受控 node_repl 宿主内调用。";

    public Task<ComputerUseBackendResult> CaptureAsync(ComputerUseCaptureRequest request, CancellationToken cancellationToken = default) =>
        _capture?.Invoke(request, cancellationToken) ?? Task.FromResult(Failure(UnavailableReason!, "backend_unavailable"));

    public Task<ComputerUseBackendResult> MouseAsync(ComputerUseMouseRequest request, CancellationToken cancellationToken = default) =>
        _mouse?.Invoke(request, cancellationToken) ?? Task.FromResult(Failure(UnavailableReason!, "backend_unavailable"));

    public Task<ComputerUseBackendResult> KeyboardAsync(ComputerUseKeyboardRequest request, CancellationToken cancellationToken = default) =>
        _keyboard?.Invoke(request, cancellationToken) ?? Task.FromResult(Failure(UnavailableReason!, "backend_unavailable"));

    private static ComputerUseBackendResult Failure(string message, string code) =>
        new(false, message, ErrorCode: code);
}
