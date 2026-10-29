using System.Text.Json;
using Fufucode.Core.Backend;

namespace Fufucode.Core.Security;

/// <summary>Enforces native policy at the capability boundary, including child agent dispatch.</summary>
public sealed class SecurityToolDispatcher : IPureToolDispatcher
{
    private static readonly AsyncLocal<Authorization?> AuthorizationSlot = new();
    private static readonly AsyncLocal<bool> RequiredSlot = new();
    private sealed record Authorization(string Name, string Arguments, string Policy);
    public static bool SecurityApprovalRequired => RequiredSlot.Value;
    public IPureToolDispatcher Inner { get; }
    public SecurityPolicyEvaluator Evaluator { get; }
    private readonly SecurityAuditStore _audit;
    public SecurityToolDispatcher(IPureToolDispatcher inner, SecurityPolicyEvaluator evaluator, SecurityAuditStore audit)
    { Inner = inner; Evaluator = evaluator; _audit = audit; }

    public async Task<PureToolResult> ExecuteCheckedAsync(PureToolCall call,
        Func<PureToolCall, CancellationToken, Task<bool>>? approvalGate, bool ordinaryApproval,
        Func<PureToolResult?> recheckMode, CancellationToken ct)
    {
        var decision = Evaluator.Evaluate(call);
        if (decision.Action == SecurityAction.Deny) return Reject(call, decision);
        string snapshot;
        try { snapshot = Snapshot(); }
        catch (SecurityPolicyException) { return Reject(call, new(SecurityAction.Deny, "policy", "安全策略无效。")); }
        if (decision.Action == SecurityAction.Ask || ordinaryApproval)
        {
            if (decision.Action == SecurityAction.Ask && approvalGate is null)
                return Reject(call, new(SecurityAction.Deny, "approval", "安全规则要求人工确认，但没有审批监听者。"));
            if (approvalGate is not null)
            {
                var priorRequired = RequiredSlot.Value;
                RequiredSlot.Value = decision.Action == SecurityAction.Ask;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromMinutes(5));
                    bool approved;
                    try { approved = await approvalGate(call, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { approved = false; }
                    if (!approved)
                        return Reject(call, new(SecurityAction.Deny, "approval", "人工确认被拒绝、超时或取消。"));
                }
                finally { RequiredSlot.Value = priorRequired; }
            }
        }
        ct.ThrowIfCancellationRequested();
        if (recheckMode() is { } denied) return denied;
        // Approval is tied to the complete policy and exact arguments, not just a tool name.
        try
        {
            if (snapshot != Snapshot()) return Reject(call, new(SecurityAction.Deny, "policy", "审批期间安全策略已变更，请重试。"));
        }
        catch (SecurityPolicyException) { return Reject(call, new(SecurityAction.Deny, "policy", "安全策略不可读。")); }
        var previous = AuthorizationSlot.Value;
        AuthorizationSlot.Value = new(call.Name, call.ArgumentsJson, snapshot);
        try { return await DispatchAsync(call, ct).ConfigureAwait(false); }
        finally { AuthorizationSlot.Value = previous; }
    }

    public async Task<PureToolResult> DispatchAsync(PureToolCall call, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var decision = Evaluator.Evaluate(call);
        if (decision.Action == SecurityAction.Deny) return Reject(call, decision);
        if (decision.Action == SecurityAction.Ask)
        {
            var authorization = AuthorizationSlot.Value;
            if (authorization is null || authorization.Name != call.Name || authorization.Arguments != call.ArgumentsJson || authorization.Policy != Snapshot())
                return Reject(call, new(SecurityAction.Deny, "approval", "安全规则要求通过原生审批后执行。"));
        }
        // Do not allow an outer agent's approval lease to authorize arbitrary nested calls.
        var prior = AuthorizationSlot.Value;
        try
        {
            var result = await Inner.DispatchAsync(call, cancellationToken).ConfigureAwait(false);
            var settings = Evaluator.Store.Current;
            if (!settings.Enabled) return result;
            var content = result.Content;
            if (settings.SensitiveDataProtectionEnabled) content = SecurityContentScanner.Redact(content, settings);
            if (settings.InjectionProtectionEnabled && SecurityContentScanner.ContainsInjection(content))
            {
                _audit.Record("injection", SecurityAction.Deny, "工具结果命中本地提示注入规则，已阻止回填。", settings);
                return new(call.Id, false, "[安全中心：工具结果命中本地提示注入规则，内容已隐藏。规则检测有局限，不是 AI 判定。]");
            }
            _audit.Record(decision.Category, decision.Action, decision.Summary, settings);
            return result with { Content = content };
        }
        catch (SecurityPolicyException) { return new(call.Id, false, "安全策略读取失败，工具结果已隐藏。"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        { return new(call.Id, false, "安全执行失败（" + ex.GetType().Name + "），工具结果已隐藏。"); }
        finally { AuthorizationSlot.Value = prior; }
    }
    private string Snapshot() => JsonSerializer.Serialize(Evaluator.Store.Current, SecurityPolicyStore.Json);
    private PureToolResult Reject(PureToolCall call, SecurityDecision decision)
    {
        try { _audit.Record(decision.Category, SecurityAction.Deny, decision.Summary, Evaluator.Store.Current); }
        catch (SecurityPolicyException) { }
        return new(call.Id, false, "<tool_use_error>安全中心：" + decision.Summary + "</tool_use_error>");
    }
}
