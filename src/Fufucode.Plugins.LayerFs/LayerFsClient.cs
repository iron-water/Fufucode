using System.Diagnostics;
using System.Text.Json;

namespace Fufucode.Plugins.LayerFs;

public sealed record LayerFsSettings
{
    public string Distribution { get; init; } = "Ubuntu-26.04";
    public string User { get; init; } = "root";
    public string Node { get; init; } = "/opt/fufucode-layerfs/node/bin/node";
    public string Config { get; init; } = "/var/lib/fufucode-layerfs/config.json";
}

public sealed class LayerFsClient(string bridgePath, LayerFsSettings settings) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _gate = new(1);
    private readonly object _processLock = new();
    private Process? _active;
    public bool IsBusy { get; private set; }

    public static ProcessStartInfo StartInfo(string bridgePath, LayerFsSettings settings)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("此插件需要 Windows 与 WSL2。");
        if (string.IsNullOrWhiteSpace(settings.Distribution) || string.IsNullOrWhiteSpace(settings.User)
            || !settings.Node.StartsWith('/') || !settings.Config.StartsWith('/'))
            throw new ArgumentException("请填写 WSL 发行版、用户和绝对 Linux 路径。");
        var info = new ProcessStartInfo("wsl.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var arg in new[] { "--distribution", settings.Distribution, "--user", settings.User,
            "--cd", "/tmp", "--exec", "timeout", "--kill-after=10s", "360s", settings.Node,
            ToLinuxPath(bridgePath, settings.Distribution), settings.Config }) info.ArgumentList.Add(arg);
        return info;
    }

    public static string ToLinuxPath(string path, string distribution)
    {
        var normalized = path.Replace('\\', '/');
        foreach (var prefix in new[] { "//wsl.localhost/", "//wsl$/" })
        {
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = normalized[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0 || !rest[..slash].Equals(distribution, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("WSL 路径必须属于所选发行版。");
            return rest[slash..];
        }
        if (normalized.Length >= 3 && char.IsAsciiLetter(normalized[0]) && normalized[1..3] == ":/")
            return $"/mnt/{char.ToLowerInvariant(normalized[0])}/{normalized[3..]}";
        if (normalized.StartsWith('/') && !normalized.StartsWith("//")) return normalized;
        throw new ArgumentException("请选择本地驱动器路径或所选 WSL 发行版中的路径。");
    }

    public async Task<JsonElement> CallAsync(object request)
    {
        await _gate.WaitAsync(_lifetime.Token);
        IsBusy = true;
        try
        {
            using var process = new Process { StartInfo = StartInfo(bridgePath, settings) };
            lock (_processLock)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                process.Start();
                _active = process;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(370));
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
                await process.StandardInput.FlushAsync();
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.StandardInput.Close();
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new InvalidOperationException("操作已中断，结果未知。请刷新检查现有状态后再决定下一步；未自动重试。");
            }
            var text = await stdout;
            var error = await stderr;
            var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (process.ExitCode is 124 or 137)
                throw new InvalidOperationException("WSL 操作超时，结果未知。请刷新检查状态后再决定下一步；未自动重试。");
            if (line is null) throw new InvalidOperationException($"WSL 调用失败（{process.ExitCode}）：{error.Trim()}");
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                throw new InvalidOperationException($"{root.GetProperty("code").GetString()}：{root.GetProperty("message").GetString()}");
            if (process.ExitCode != 0) throw new InvalidOperationException($"桥接进程退出 {process.ExitCode}：{error.Trim()}");
            return root.TryGetProperty("result", out var result) ? result.Clone() : JsonSerializer.SerializeToElement<object?>(null);
        }
        finally
        {
            lock (_processLock) _active = null;
            IsBusy = false;
            _gate.Release();
        }
    }

    public void Dispose()
    {
        lock (_processLock)
        {
            _lifetime.Cancel();
            if (_active is not { } process) return;
            try
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(1500))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(1500);
                }
            }
            catch (InvalidOperationException) { }
        }
    }
}
