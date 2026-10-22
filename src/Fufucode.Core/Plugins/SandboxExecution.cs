using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core.Plugins;

/// <summary>沙箱执行请求。宿主只传递显式参数，不把命令拼接进 shell。</summary>
public sealed record SandboxExecutionRequest(
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    TimeSpan? Timeout = null);

/// <summary>沙箱执行结果，保留 stdout/stderr 分流和可解释的终态。</summary>
public sealed record SandboxExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    SandboxExitCategory Category,
    bool Contained);

public enum SandboxExitCategory
{
    Exit,
    Timeout,
    Cancelled,
    OutputLimit,
    Failed,
}

/// <summary>
/// 可替换的执行沙箱边界。插件实现负责生命周期、隔离和资源回收，Core 不依赖具体虚拟化技术。
/// </summary>
public interface ISandboxExecutionService : IAsyncDisposable
{
    string ProviderId { get; }
    bool IsAvailable { get; }
    string? UnavailableReason { get; }
    Task<SandboxExecutionResult> ExecuteAsync(
        SandboxExecutionRequest request,
        CancellationToken cancellationToken = default);
}
