using System;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Fufucode.App.Infrastructure;

public sealed class ReplySpeechService : IDisposable
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
