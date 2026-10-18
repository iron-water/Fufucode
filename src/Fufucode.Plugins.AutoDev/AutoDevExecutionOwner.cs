using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions.Automation;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;

namespace Fufucode.Plugins.AutoDev;

/// <summary>
/// 插件对宿主的执行所有者实现：宿主请求一次 harness 运行，插件在审查专用 scratch 工作树里
/// 建任务、持久派发并返回终态。提示词与隔离策略来自调用方；运行簿记仍只写项目状态库。
/// </summary>
public sealed class AutoDevExecutionOwner : IExecutionOwner
{
    private readonly Func<string, HarnessRuntime>? _runtimeResolver;
    private long _sequence;

    /// <param name="runtimeResolver">测试/嵌入方可以注入已解析的部署；产品路径由环境变量登记。</param>
    public AutoDevExecutionOwner(Func<string, HarnessRuntime>? runtimeResolver = null)
    {
        _runtimeResolver = runtimeResolver;
    }

    public string OwnerId => AutoDevPluginPackage.PluginId;

    public bool SupportsHarness(string harness) =>
        !string.IsNullOrWhiteSpace(harness) &&
        (_runtimeResolver is not null || HarnessAdapters.RegisteredIds.Contains(harness));

    public async Task<ExecutionOutcome> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Runtime is null && !SupportsHarness(request.Harness))
            return Failed("未登记的 harness：" + request.Harness);

        var database = Path.Combine(request.OwnerDataRoot, "projects", request.ProjectId, "automation.db");
        // 与既有审查模块约定一致：scratch/evidence 直接位于模块数据根下。
        var root = request.EvidenceDirectory ?? request.OwnerDataRoot;
        var scratchRoot = Path.Combine(root, "scratch");
        var evidenceRoot = Path.Combine(root, "evidence");
        var runId = $"host-{Interlocked.Increment(ref _sequence):D6}-{Guid.NewGuid():N}";
        var threadId = "host-thread-" + runId;
        var taskId = "host-task-" + runId;
        Directory.CreateDirectory(scratchRoot);
        Directory.CreateDirectory(evidenceRoot);
        var worktree = Directory.CreateDirectory(Path.Combine(scratchRoot, runId)).FullName;

        // 线程冻结的是实际部署的 harness（runtime.Harness），不是调用方请求的别名。
        // 调用方显式解析的部署优先（例如本地审查复用已登记的 Pi 运行时），
        // 其次测试/嵌入方注入的解析器，最后才是部署环境变量。
        var runtime = request.Runtime ?? _runtimeResolver?.Invoke(request.Harness) ?? ResolveRuntime(request.Harness);
        HarnessExecutor executor;
        using (var store = new AutomationStore(database))
        {
            executor = new HarnessExecutor(store, evidenceRoot, hostId: OwnerId);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            store.CreateThread(threadId, request.ProjectId, runtime.Harness, request.Model, worktree, null, now);
            store.CreateTask(taskId, threadId, "host execution via " + OwnerId, request.BaseSha ?? "", now);

            var runRequest = new HarnessRequest(runId, threadId, taskId, request.Model, request.Prompt, request.Provider);
            try
            {
                var run = await executor.ExecuteAsync(runtime, runRequest, cancellationToken);
                return Outcome(run, runId, threadId, taskId, worktree);
            }
            catch (OperationCanceledException)
            {
                // 取消发生在派发前后都可能是未知态；以持久运行簿记为准，缺失即证明未派发。
                var run = TryRead(executor.Runs, runId);
                return Outcome(run, runId, threadId, taskId, worktree);
            }
        }
    }

    private static ExecutionOutcome Outcome(HarnessRun run, string runId, string threadId, string taskId, string worktree)
    {
        var status = run.Status switch
        {
            HarnessRunStatus.Completed => ExecutionStatus.Completed,
            HarnessRunStatus.Failed => ExecutionStatus.Failed,
            HarnessRunStatus.Interrupted => ExecutionStatus.Interrupted,
            _ => ExecutionStatus.Unknown,
        };
        // 未知终态必须保留 scratch，供人工对账；已证明的终态才释放。
        if (status is ExecutionStatus.Completed or ExecutionStatus.Failed) TryDelete(worktree);
        return new ExecutionOutcome(runId, threadId, taskId, status, run.Output, run.Error,
            status is ExecutionStatus.Unknown ? worktree : null, run.SettleHead,
            RetainedWorktree: status is ExecutionStatus.Unknown);
    }

    private static HarnessRun TryRead(HarnessRunStore runs, string runId)
    {
        try { return runs.Get(runId); }
        catch (KeyNotFoundException)
        {
            return new HarnessRun(runId, "", HarnessRunStatus.Failed, null, null, null,
                "cancelled before dispatch: the durable claim was never written");
        }
    }

    private static ExecutionOutcome Failed(string error) =>
        new(Guid.NewGuid().ToString("N"), null, null, ExecutionStatus.Failed, null, error, null);

    /// <summary>
    /// 与自动推进引擎同一约定：部署方通过 FUFUCODE_&lt;HARNESS&gt;_RUNTIME 提供绝对可执行文件。
    /// 未登记时构造一个不可启动的路径，由执行器拒绝并报告配置缺口，不猜测其它后端。
    /// </summary>
    private static HarnessRuntime ResolveRuntime(string harness)
    {
        var registered = Environment.GetEnvironmentVariable("FUFUCODE_" + harness.ToUpperInvariant() + "_RUNTIME");
        var executable = string.IsNullOrWhiteSpace(registered) ? "<unregistered-" + harness + "-runtime>" : registered;
        return new HarnessRuntime("host-" + harness, harness, executable, [], "host-v1");
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception) { /* Scratch cleanup is best effort; evidence stays for inspection. */ }
    }
}
