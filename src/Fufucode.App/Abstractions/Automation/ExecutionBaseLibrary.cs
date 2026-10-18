using System;
using System.Linq;

namespace Fufucode.App.Abstractions.Automation;

// 执行契约：宿主（本地审查、CLI）向“执行所有者”请求一次 harness 运行。
// 自动开发插件安装并启用时登记所有者；未登记时调用方必须显示真实原因，不能回退到宿主内第二份执行器。

/// <summary>一次执行请求。提示词、工作树与运行簿记都由所有者负责，调用方只给出事实。</summary>
public sealed record ExecutionRequest(
    string OwnerDataRoot,
    string ProjectId,
    string Harness,
    string Model,
    string Prompt,
    string? Provider = null,
    string? EvidenceDirectory = null,
    string? BaseSha = null);

/// <summary>一次执行结果。Failed 是真实终态；Unknown 只能人工对账，不能自动重发。</summary>
public sealed record ExecutionOutcome(
    string RunId,
    string? ThreadId,
    string? TaskId,
    ExecutionStatus Status,
    string? Output,
    string? Error,
    string? Worktree,
    string? SettleHead = null,
    bool RetainedWorktree = false);

public enum ExecutionStatus { Completed, Failed, Interrupted, Unknown }

/// <summary>
/// 执行所有者（当前由自动开发插件实现）。停止/卸载时未完成运行必须如实返回 Unknown 并保留工作树。
/// </summary>
public interface IExecutionOwner
{
    /// <summary>稳定标识，例如 "automation.auto-dev"。</summary>
    string OwnerId { get; }

    /// <summary>harness id 是否已登记到本所有者（pi/omp/codex/dsh 等）。</summary>
    bool SupportsHarness(string harness);

    System.Threading.Tasks.Task<ExecutionOutcome> ExecuteAsync(ExecutionRequest request,
        System.Threading.CancellationToken cancellationToken = default);
}

/// <summary>宿主侧执行入口：按所有者登记，不缓存、不回退。</summary>
public static class ExecutionBaseLibrary
{
    private static readonly object Gate = new();
    private static readonly System.Collections.Generic.List<IExecutionOwner> Owners = [];

    /// <summary>取最近登记的所有者；注销后自动回落到仍存活的同插件实例。</summary>
    public static IExecutionOwner? Current
    {
        get { lock (Gate) return Owners.Count > 0 ? Owners[^1] : null; }
    }

    public static void Register(IExecutionOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (Gate)
        {
            // 不同插件不能抢同一个后台；同一插件的多个激活实例（主窗口与设置页各持一个
            // 管理器）可以并存，注销其中一个不会让另一个失去执行入口。
            var conflicting = Owners.FirstOrDefault(existing =>
                !ReferenceEquals(existing, owner) &&
                !string.Equals(existing.OwnerId, owner.OwnerId, StringComparison.OrdinalIgnoreCase));
            if (conflicting is not null)
                throw new InvalidOperationException(
                    $"执行所有者 '{conflicting.OwnerId}' 已登记，拒绝 '{owner.OwnerId}' 覆盖。");
            if (!Owners.Any(existing => ReferenceEquals(existing, owner))) Owners.Add(owner);
        }
    }

    public static bool Unregister(IExecutionOwner owner)
    {
        lock (Gate) return Owners.Remove(owner);
    }
}
