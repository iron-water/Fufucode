using System;
using System.Threading.Tasks;
using Fufucode.Core.Plugins;

namespace Fufucode.App.Infrastructure;

/// <summary>
/// 语音插件未安装时的安全替身：零外部进程、零 SAPI 依赖。
/// </summary>
public sealed class NullSpeechService : ISpeechService
{
    public static readonly NullSpeechService Instance = new();
    public bool IsSpeaking => false;
    public bool IsPaused => false;
    public int Rate { get; set; }
    public int Volume { get; set; } = 100;
    public string Status => "未安装语音服务";
    public event Action? Changed { add { } remove { } }
    public Task SpeakAsync(string text) => Task.CompletedTask;
    public Task PauseAsync() => Task.CompletedTask;
    public Task ResumeAsync() => Task.CompletedTask;
    public void Stop() { }
    public void Dispose() { }
}
