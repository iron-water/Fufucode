using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions.Automation;
using Fufucode.Core.Automation.Rebirth;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.AutoDev;

/// <summary>
/// AutoDev tool surface backed by the rebirth project kernel. Every mutation is a real command;
/// no tool claims that work ran just because a message was sent.
/// </summary>
public sealed class AutoDevToolHandler : IPluginToolHandler
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly PluginPackageContext _context;

    public AutoDevToolHandler(PluginPackageContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public IReadOnlyList<PluginToolRegistration> Registrations { get; } =
    [
        new("autodev_status", "读取当前工作区的项目内核状态、工单、运行、证据与待处理事项。",
            "{\"type\":\"object\",\"properties\":{}}", PluginCapability.AutoDevelopment),
        new("autodev_plan", "在项目委托内登记目标和工单；不启动执行，不伪造完成。",
            "{\"type\":\"object\",\"properties\":{\"goal\":{\"type\":\"string\"},\"steps\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}},\"required\":[\"goal\"]}",
            PluginCapability.AutoDevelopment),
        new("autodev_advance", "返回当前需要推进的工单；本工具本身不执行、不标记成功。",
            "{\"type\":\"object\",\"properties\":{}}", PluginCapability.AutoDevelopment),
        new("autodev_authorize", "在项目委托内显式授权一个 Proposed 工单。",
            "{\"type\":\"object\",\"properties\":{\"ticketId\":{\"type\":\"string\"}},\"required\":[\"ticketId\"]}",
            PluginCapability.AutoDevelopment),
        new("autodev_run_start", "为一个已授权工单登记一次由外部执行器负责的 Run；本工具不启动进程。",
            "{\"type\":\"object\",\"properties\":{\"ticketId\":{\"type\":\"string\"},\"executor\":{\"type\":\"string\"},\"model\":{\"type\":\"string\"},\"workspace\":{\"type\":\"string\"}},\"required\":[\"ticketId\"]}",
            PluginCapability.AutoDevelopment),
        new("autodev_run_finish", "外部执行器回报 Run 终态；unknown 不会被自动重试。",
            "{\"type\":\"object\",\"properties\":{\"runId\":{\"type\":\"string\"},\"outcome\":{\"type\":\"string\",\"enum\":[\"completed\",\"failed\",\"interrupted\",\"unknown\"]},\"terminalCode\":{\"type\":\"string\"},\"detail\":{\"type\":\"string\"}},\"required\":[\"runId\",\"outcome\"]}",
            PluginCapability.AutoDevelopment),
        new("autodev_evidence", "把产物、测试、评审或人工验收绑定到工单和可选 Run。",
            "{\"type\":\"object\",\"properties\":{\"ticketId\":{\"type\":\"string\"},\"runId\":{\"type\":\"string\"},\"kind\":{\"type\":\"string\",\"enum\":[\"artifact\",\"test\",\"review\",\"humanAcceptance\",\"note\"]},\"criterion\":{\"type\":\"string\"},\"reference\":{\"type\":\"string\"},\"passed\":{\"type\":\"boolean\"},\"verifier\":{\"type\":\"string\"}},\"required\":[\"ticketId\",\"kind\",\"criterion\",\"reference\",\"passed\",\"verifier\"]}",
            PluginCapability.AutoDevelopment),
        new("autodev_complete_ticket", "仅当所有验收条件都有有效通过证据时完成工单。",
            "{\"type\":\"object\",\"properties\":{\"ticketId\":{\"type\":\"string\"}},\"required\":[\"ticketId\"]}",
            PluginCapability.AutoDevelopment),
        new("autodev_complete_project", "仅当所有工单完成/取消且没有未决 Attention 时完成项目。",
            "{\"type\":\"object\",\"properties\":{}}", PluginCapability.AutoDevelopment),
        new("autodev_veto", "记录人的路线/工单否决；这是约束，不是模型可批准的建议。",
            "{\"type\":\"object\",\"properties\":{\"ticketId\":{\"type\":\"string\"},\"target\":{\"type\":\"string\"},\"scope\":{\"type\":\"string\"},\"text\":{\"type\":\"string\"}},\"required\":[\"target\",\"scope\",\"text\"]}",
            PluginCapability.AutoDevelopment),
        new("autodev_input", "记录人的查询、建议、证据、范围变更、停止或接管输入。",
            "{\"type\":\"object\",\"properties\":{\"ticketId\":{\"type\":\"string\"},\"kind\":{\"type\":\"string\",\"enum\":[\"query\",\"suggestion\",\"evidence\",\"veto\",\"scopeChange\",\"stop\",\"takeover\",\"answer\",\"newGoal\"]},\"text\":{\"type\":\"string\"},\"target\":{\"type\":\"string\"},\"scope\":{\"type\":\"string\"}},\"required\":[\"kind\",\"text\"]}",
            PluginCapability.AutoDevelopment),
        new("autodev_attention", "提出一个结构化、一次只问一个的问题。",
            "{\"type\":\"object\",\"properties\":{\"ticketId\":{\"type\":\"string\"},\"question\":{\"type\":\"string\"}},\"required\":[\"ticketId\",\"question\"]}",
            PluginCapability.AutoDevelopment),
        new("autodev_answer", "记录人对结构化 Attention 的回答。",
            "{\"type\":\"object\",\"properties\":{\"attentionId\":{\"type\":\"string\"},\"answer\":{\"type\":\"string\"}},\"required\":[\"attentionId\",\"answer\"]}",
            PluginCapability.AutoDevelopment),
        new("autodev_stop", "人明确停止一个工单；运行被标记为 Interrupted，不伪装成完成。",
            "{\"type\":\"object\",\"properties\":{\"ticketId\":{\"type\":\"string\"},\"reason\":{\"type\":\"string\"}},\"required\":[\"ticketId\"]}",
            PluginCapability.AutoDevelopment),
        new("autodev_takeover", "把工单交给人工；Agent 之后不能再启动它。",
            "{\"type\":\"object\",\"properties\":{\"ticketId\":{\"type\":\"string\"}},\"required\":[\"ticketId\"]}",
            PluginCapability.AutoDevelopment),
        new("autodev_resume", "在否决解除或重规划完成后恢复 ReplanRequired 工单。",
            "{\"type\":\"object\",\"properties\":{\"ticketId\":{\"type\":\"string\"}},\"required\":[\"ticketId\"]}",
            PluginCapability.AutoDevelopment),
        new("autodev_reconcile", "对 needsReconciliation 工单做显式对账并恢复。",
            "{\"type\":\"object\",\"properties\":{\"ticketId\":{\"type\":\"string\"},\"resolution\":{\"type\":\"string\"}},\"required\":[\"ticketId\",\"resolution\"]}",
            PluginCapability.AutoDevelopment),
        new("autodev_lift_veto", "由人解除一条 active 否决；历史保留。",
            "{\"type\":\"object\",\"properties\":{\"decisionId\":{\"type\":\"string\"}},\"required\":[\"decisionId\"]}",
            PluginCapability.AutoDevelopment)
    ];

    public Task<PureToolResult> ExecuteAsync(PureToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        try
        {
            using var kernel = new RebirthKernel(KernelPath);
            cancellationToken.ThrowIfCancellationRequested();
            var result = call.Name switch
            {
                "autodev_status" => Status(kernel),
                "autodev_plan" => Plan(kernel, call),
                "autodev_advance" => Advance(kernel),
                "autodev_authorize" => kernel.AuthorizeTicket(Command(call), Required(call, "ticketId"), "agent", Now()),
                "autodev_run_start" => StartRun(kernel, call),
                "autodev_run_finish" => kernel.CompleteRun(Command(call), Required(call, "runId"),
                    ParseOutcome(Required(call, "outcome")), Text(call, "terminalCode"), Text(call, "detail"), Now()),
                "autodev_evidence" => kernel.AddEvidence(Command(call), Required(call, "ticketId"),
                    Text(call, "runId"), ParseEvidenceKind(Required(call, "kind")), Required(call, "criterion"),
                    Required(call, "reference"), Bool(call, "passed"), Text(call, "verifier") ?? "agent", Now()),
                "autodev_complete_ticket" => kernel.CompleteTicket(Command(call), Required(call, "ticketId"), Now()),
                "autodev_complete_project" => kernel.CompleteProject(Command(call), ProjectId, Now()),
                "autodev_veto" => kernel.RecordVeto(Command(call), ProjectId, Text(call, "ticketId"),
                    Required(call, "target"), Required(call, "scope"), Required(call, "text"), "human", Now()),
                "autodev_input" => kernel.RecordHumanInput(Command(call), ProjectId, Text(call, "ticketId"),
                    "human", ParseInputKind(Required(call, "kind")), Required(call, "text"), Now(),
                    Text(call, "target"), Text(call, "scope")),
                "autodev_attention" => kernel.CreateAttention(Command(call), Required(call, "ticketId"),
                    Required(call, "question"), Now()),
                "autodev_answer" => kernel.AnswerAttention(Command(call), Required(call, "attentionId"),
                    Required(call, "answer"), Now()),
                "autodev_stop" => kernel.StopTicket(Command(call), Required(call, "ticketId"),
                    Text(call, "reason") ?? "human stop", "human", Now()),
                "autodev_takeover" => kernel.TakeoverTicket(Command(call), Required(call, "ticketId"), "human", Now()),
                "autodev_resume" => kernel.ResumeTicket(Command(call), Required(call, "ticketId"), "agent", Now()),
                "autodev_reconcile" => kernel.ReconcileTicket(Command(call), Required(call, "ticketId"),
                    Required(call, "resolution"), Now()),
                "autodev_lift_veto" => kernel.LiftVeto(Command(call), ProjectId, Required(call, "decisionId"), "human", Now()),
                _ => throw new InvalidOperationException("未知的自动开发工具动作：" + call.Name)
            };
            return Task.FromResult(new PureToolResult(call.Id, true,
                JsonSerializer.Serialize(result, OutputJson)));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException
            or KeyNotFoundException or FileNotFoundException)
        {
            return Task.FromResult(new PureToolResult(call.Id, false, error.Message));
        }
    }

    private object StartRun(RebirthKernel kernel, PureToolCall call)
    {
        var ticketId = Required(call, "ticketId");
        var executor = Text(call, "executor") ?? "manual";
        var model = Text(call, "model") ?? "manual";
        var snapshot = kernel.ReadSnapshot(ProjectId);
        var ticket = snapshot.Tickets.FirstOrDefault(item => item.Id == ticketId)
            ?? throw new KeyNotFoundException("Unknown ticket: " + ticketId);
        var root = string.IsNullOrWhiteSpace(_context.DataRoot) ? _context.PackageDirectory : _context.DataRoot;
        var workspace = Text(call, "workspace") ?? Path.Combine(DataRoot, "auto-dev", "rebirth", RebirthPaths.Stable(ProjectId), "runs", ticketId);
        var launch = !string.Equals(executor, "manual", StringComparison.OrdinalIgnoreCase);
        if (launch)
        {
            var owner = ExecutionBaseLibrary.Current
                ?? throw new InvalidOperationException("没有已注册的执行所有者；先启用 AutoDev 或改用 executor=manual 登记。");
            if (!owner.SupportsHarness(executor))
                throw new InvalidOperationException("执行器未登记 harness：" + executor);
        }
        var run = kernel.StartRun(Command(call), ticketId, executor, model, workspace, Now());
        if (launch)
        {
            Directory.CreateDirectory(workspace);
            var evidence = Path.Combine(workspace, "evidence");
            Directory.CreateDirectory(evidence);
            RebirthExecutionBridge.Start(kernel.DatabasePath, ProjectId, ticketId, run.Id,
                BuildPrompt(snapshot, ticket), executor, model, root, evidence);
        }
        return run;
    }

    private static string BuildPrompt(RebirthSnapshot snapshot, RebirthTicket ticket)
    {
        var brief = snapshot.Brief.Brief;
        return $"""
            Ticket: {ticket.Id}
            Title: {ticket.Title}
            Goal: {ticket.Goal}

            Acceptance:
            {string.Join("\n", ticket.Acceptance.Select(item => "- " + item))}

            Project constraints:
            {string.Join("\n", brief.Constraints.Select(item => "- " + item).DefaultIfEmpty("- none"))}

            Work only within the ticket scope. Perform real work in the provided workspace.
            Report actual results and unverified items; do not claim verification that did not run.
            """;
    }

    private object Status(RebirthKernel kernel)
    {
        try
        {
            var snapshot = kernel.ReadSnapshot(ProjectId);
            return new
            {
                status = snapshot.Project.Status.ToString(),
                projectId = snapshot.Project.Id,
                briefVersion = snapshot.Project.BriefVersion,
                revision = snapshot.Project.Revision,
                tickets = snapshot.Tickets,
                runs = snapshot.Runs,
                openAttention = snapshot.Attention.Where(item => item.Status == RebirthAttentionStatus.Open),
                activeVetoes = snapshot.Decisions.Where(item => item.Kind == RebirthDecisionKind.Veto && item.Status == "Active"),
                database = kernel.DatabasePath
            };
        }
        catch (KeyNotFoundException)
        {
            return new
            {
                status = "uninitialized",
                projectId = ProjectId,
                database = kernel.DatabasePath,
                message = "项目尚未建立 Brief；调用 autodev_plan 创建。"
            };
        }
    }

    private object Plan(RebirthKernel kernel, PureToolCall call)
    {
        var goal = Required(call, "goal");
        var steps = Strings(call, "steps");
        var acceptance = steps.Length > 0 ? steps : new[] { goal };
        var brief = new RebirthBrief(goal, new[] { goal }, Array.Empty<string>(), Array.Empty<string>(),
            acceptance, Array.Empty<string>(), 600, RebirthAutonomy.WithinMandate);
        RebirthSnapshot? existing = null;
        try { existing = kernel.ReadSnapshot(ProjectId); }
        catch (KeyNotFoundException)
        {
            kernel.CreateProject(Command(call) + ":project", goal, brief, "agent", Now(), ProjectId);
        }
        if (existing is not null && existing.Brief.Brief.Statement != goal)
            kernel.UpdateBrief(Command(call) + ":brief", ProjectId, existing.Project.BriefVersion, brief, "agent", Now());

        var known = existing?.Tickets.Select(ticket => ticket.Id).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var created = new List<RebirthTicket>();
        for (var index = 0; index < acceptance.Length; index++)
        {
            var title = acceptance[index].Trim();
            if (title.Length == 0) continue;
            var ticketId = "ticket-" + RebirthPaths.Stable(title);
            if (known.Contains(ticketId)) continue;
            created.Add(kernel.CreateTicket(Command(call) + ":ticket:" + index, ProjectId,
                title, title, new[] { title }, "default", "auto-dev", Now(), ticketId: ticketId));
            known.Add(ticketId);
        }
        var snapshot = kernel.ReadSnapshot(ProjectId);
        return new { status = snapshot.Project.Status.ToString(), projectId = ProjectId, created, tickets = snapshot.Tickets };
    }

    private object Advance(RebirthKernel kernel)
    {
        var snapshot = kernel.ReadSnapshot(ProjectId);
        var next = snapshot.Tickets.FirstOrDefault(ticket =>
            ticket.Status is RebirthTicketStatus.Authorized or RebirthTicketStatus.Active or RebirthTicketStatus.Review);
        return new
        {
            status = next is null ? "idle" : "actionable",
            next,
            tickets = snapshot.Tickets,
            openAttention = snapshot.Attention.Where(item => item.Status == RebirthAttentionStatus.Open)
        };
    }

    private string DataRoot => string.IsNullOrWhiteSpace(_context.DataRoot)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fufucode")
        : _context.DataRoot;

    private string ProjectPath => _context.ProjectPath ?? _context.PackageDirectory;

    private string KernelPath => RebirthPaths.Database(DataRoot, ProjectPath);

    private string ProjectId => RebirthPaths.ProjectId(ProjectPath);

    private static string Command(PureToolCall call) => call.Id;

    private static string Required(PureToolCall call, string name) =>
        Text(call, name) ?? throw new ArgumentException("缺少参数：" + name);

    private static string? Text(PureToolCall call, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            return document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string[] Strings(PureToolCall call, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            if (!document.RootElement.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();
            return value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? "").Where(item => item.Length > 0).ToArray();
        }
        catch (JsonException) { return Array.Empty<string>(); }
    }

    private static bool Bool(PureToolCall call, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            if (!document.RootElement.TryGetProperty(name, out var value)) return false;
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => false
            };
        }
        catch (JsonException) { return false; }
    }

    private static RebirthRunOutcome ParseOutcome(string value) => value.ToLowerInvariant() switch
    {
        "completed" => RebirthRunOutcome.Completed,
        "failed" => RebirthRunOutcome.Failed,
        "interrupted" => RebirthRunOutcome.Interrupted,
        "unknown" => RebirthRunOutcome.Unknown,
        _ => throw new ArgumentException("未知的 Run 终态：" + value)
    };

    private static RebirthEvidenceKind ParseEvidenceKind(string value) => value.ToLowerInvariant() switch
    {
        "artifact" => RebirthEvidenceKind.Artifact,
        "test" => RebirthEvidenceKind.Test,
        "review" => RebirthEvidenceKind.Review,
        "humanacceptance" => RebirthEvidenceKind.HumanAcceptance,
        "note" => RebirthEvidenceKind.Note,
        _ => throw new ArgumentException("未知的证据类型：" + value)
    };

    private static RebirthInputKind ParseInputKind(string value) => value.ToLowerInvariant() switch
    {
        "query" => RebirthInputKind.Query,
        "suggestion" => RebirthInputKind.Suggestion,
        "evidence" => RebirthInputKind.Evidence,
        "veto" => RebirthInputKind.Veto,
        "scopechange" => RebirthInputKind.ScopeChange,
        "stop" => RebirthInputKind.Stop,
        "takeover" => RebirthInputKind.Takeover,
        "answer" => RebirthInputKind.Answer,
        "newgoal" => RebirthInputKind.NewGoal,
        _ => throw new ArgumentException("未知的输入类型：" + value)
    };

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
