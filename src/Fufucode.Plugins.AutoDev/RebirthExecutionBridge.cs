using System;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions.Automation;
using Fufucode.Core.Automation.Rebirth;

namespace Fufucode.Plugins.AutoDev;

/// <summary>
/// Bridges a rebirth Run to the registered execution owner. The owner may be a real harness
/// adapter or a test double; the kernel only records the terminal state the owner reports.
/// </summary>
internal static class RebirthExecutionBridge
{
    public static void Start(string databasePath, string projectId, string ticketId, string runId,
        string prompt, string harness, string model, string root, string evidenceDirectory)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var owner = ExecutionBaseLibrary.Current
                    ?? throw new InvalidOperationException("没有已注册的执行所有者。");
                if (!owner.SupportsHarness(harness))
                    throw new InvalidOperationException("执行器未登记 harness：" + harness);
                var request = new ExecutionRequest(root, projectId, harness, model, prompt,
                    EvidenceDirectory: evidenceDirectory);
                var outcome = await owner.ExecuteAsync(request, CancellationToken.None).ConfigureAwait(false);
                var status = outcome.Status switch
                {
                    ExecutionStatus.Completed => RebirthRunOutcome.Completed,
                    ExecutionStatus.Failed => RebirthRunOutcome.Failed,
                    ExecutionStatus.Interrupted => RebirthRunOutcome.Interrupted,
                    _ => RebirthRunOutcome.Unknown
                };
                using var kernel = new RebirthKernel(databasePath);
                kernel.CompleteRun("bridge:" + runId, runId, status, outcome.Status.ToString(),
                    Trim(outcome.Error ?? outcome.Output), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
            catch (Exception error)
            {
                try
                {
                    using var kernel = new RebirthKernel(databasePath);
                    kernel.CompleteRun("bridge:" + runId + ":error", runId, RebirthRunOutcome.Unknown,
                        error.GetType().Name, Trim(error.Message), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                }
                catch
                {
                    // The run may have been completed or reconciled by a person while the owner was failing.
                }
            }
        });
    }

    private static string Trim(string? text) => string.IsNullOrEmpty(text)
        ? "" : text.Length <= 4000 ? text : text[..4000] + "…";
}
