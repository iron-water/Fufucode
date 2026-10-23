using System;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.Speech;

/// <summary>
/// 独立物理插件实现的 Windows SAPI 语音朗读服务。
/// </summary>
public sealed class ReplySpeechService : ISpeechService
{
    private Process? _process;
    public bool IsSpeaking => _process is not null;
    public bool IsPaused { get; private set; }
    public int Rate { get; set; }
    public int Volume { get; set; } = 100;
    public string Status { get; private set; } = "未朗读";
    public event Action? Changed;
    private void Update(string status, bool paused = false)
    { Status = status; IsPaused = paused; Changed?.Invoke(); }
    public async Task SpeakAsync(string text)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("朗读需要 Windows SAPI");
        var script = """
            try {
                $ErrorActionPreference='Stop'
                $inputReader=[IO.StreamReader]::new([Console]::OpenStandardInput(),[Text.UTF8Encoding]::new($false))
                $request=$inputReader.ReadLine() | ConvertFrom-Json
                $voice=New-Object -ComObject SAPI.SpVoice
                $voice.Rate=$request.rate
                $voice.Volume=$request.volume
                [void]$voice.Speak($request.text,1)
                [Console]::Out.WriteLine('speaking')
                $paused=$false
                $command=$inputReader.ReadLineAsync()
                while ($paused -or -not $voice.WaitUntilDone(0)) {
                    if ($command.Wait(50)) {
                        switch ($command.Result) {
                            'pause' { if (-not $paused) { $voice.Pause(); $paused=$true; [Console]::Out.WriteLine('paused') } }
                            'resume' { if ($paused) { $voice.Resume(); $paused=$false; [Console]::Out.WriteLine('speaking') } }
                            'stop' { [void]$voice.Speak('',2); [Console]::Out.WriteLine('stopped'); exit 0 }
                            $null { exit 0 }
                        }
                        $command=$inputReader.ReadLineAsync()
                    }
                }
                [Console]::Out.WriteLine('done')
            } catch { [Console]::Error.Write($_.Exception.Message); exit 1 }
            """;
        var start = new ProcessStartInfo("powershell.exe") { CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true,
            StandardInputEncoding = new UTF8Encoding(false) };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动朗读");
        _process = process; Update("正在启动朗读");
        try
        {
            var error = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { text, rate = Math.Clamp(Rate, -10, 10), volume = Math.Clamp(Volume, 0, 100) }));
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (!ReferenceEquals(_process, process)) break;
                Update(line switch { "speaking" => "正在朗读", "paused" => "已暂停", "done" => "朗读完成", "stopped" => "已停止", _ => Status }, line == "paused");
                if (line is "done" or "stopped") process.StandardInput.Close();
            }
            await process.WaitForExitAsync();
            if (ReferenceEquals(_process, process) && process.ExitCode != 0)
            { Update("朗读失败"); throw new InvalidOperationException(await error); }
        }
        finally { if (ReferenceEquals(_process, process)) { _process = null; IsPaused = false; Changed?.Invoke(); } process.Dispose(); }
    }
    public Task PauseAsync() => SendAsync("pause");
    public Task ResumeAsync() => SendAsync("resume");
    private async Task SendAsync(string command)
    {
        var process = _process;
        if (process is null || process.HasExited) return;
        await process.StandardInput.WriteLineAsync(command);
    }
    public void Stop() { var process = _process; _process = null; if (process is not null && !process.HasExited) process.Kill(entireProcessTree: true); Update("已停止"); }
    public void Dispose() => Stop();
}

/// <summary>
/// 语音朗读插件包入口：激活时把自己登记为进程内唯一的朗读服务，
/// 停用/卸载时撤回登记并释放残留的朗读进程。
/// </summary>
public sealed class SpeechPluginPackage : IPluginPackage
{
    private ReplySpeechService? _owned;

    public string Id => "service.speech-synthesis";
    public string Version => "1.0.0";

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => null;

    /// <summary>
    /// 宿主可能有多个插件管理器（工作台页宿主、设置页市场）先后激活同一插件。
    /// 朗读服务是进程内共享的：后来者复用已登记的服务，只由创建者负责释放，
    /// 避免第二个管理器因“已有宿主”而把一次正常激活记成加载失败。
    /// </summary>
    public Task InitializeAsync(PluginPackageContext context, CancellationToken cancellationToken = default)
    {
        if (_owned is not null || PluginPackageManager.ActiveSpeechService is not null) return Task.CompletedTask;
        _owned = new ReplySpeechService();
        PluginPackageManager.ActiveSpeechService = _owned;
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        var owned = _owned;
        _owned = null;
        if (owned is null) return Task.CompletedTask;
        if (ReferenceEquals(PluginPackageManager.ActiveSpeechService, owned))
            PluginPackageManager.ActiveSpeechService = null;
        owned.Dispose();
        return Task.CompletedTask;
    }
}
