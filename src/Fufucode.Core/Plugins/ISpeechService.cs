using System;
using System.Threading.Tasks;

namespace Fufucode.Core.Plugins;

/// <summary>
/// 语音朗读服务统一抽象接口（service.speech-synthesis 插件提供者）。
/// 核心外壳与设置仅依赖此接口，不硬编码 PowerShell 或 Windows SAPI COM 组件。
/// </summary>
public interface ISpeechService : IDisposable
{
    bool IsSpeaking { get; }
    bool IsPaused { get; }
    int Rate { get; set; }
    int Volume { get; set; }
    string Status { get; }
    event Action? Changed;
    Task SpeakAsync(string text);
    Task PauseAsync();
    Task ResumeAsync();
    void Stop();
}
