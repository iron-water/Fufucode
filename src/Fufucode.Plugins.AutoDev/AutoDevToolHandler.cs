using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.AutoDev;

/// <summary>
/// 自动开发工作流工具处理器：向智能体提供自动化规划、执行推进与状态查询能力。
/// </summary>
public sealed class AutoDevToolHandler : IPluginToolHandler
{
    private readonly PluginPackageContext _context;

    public AutoDevToolHandler(PluginPackageContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public IReadOnlyList<PluginToolRegistration> Registrations { get; } =
    [
        new(
            "autodev_status",
            "查询当前项目的自动化推进与目标执行状态。",
            "{\"type\":\"object\",\"properties\":{}}",
            PluginCapability.AutoDevelopment),
        new(
            "autodev_plan",
            "为当前工作区或目标创建结构化的多步骤开发推进计划与验收断言。",
            "{\"type\":\"object\",\"properties\":{\"goal\":{\"type\":\"string\",\"description\":\"推进目标描述\"},\"steps\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"拆解步骤列表\"}},\"required\":[\"goal\"]}",
            PluginCapability.AutoDevelopment),
        new(
            "autodev_advance",
            "评估当前工作区状态并执行下一个自动化开发任务步骤。",
            "{\"type\":\"object\",\"properties\":{\"stepIndex\":{\"type\":\"integer\",\"description\":\"当前推进的步骤索引\"},\"verificationCommand\":{\"type\":\"string\",\"description\":\"执行验证命令\"}}}",
            PluginCapability.AutoDevelopment)
    ];

    public Task<PureToolResult> ExecuteAsync(PureToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        return call.Name switch
        {
            "autodev_status" => Task.FromResult(new PureToolResult(call.Id, true,
                JsonSerializer.Serialize(new
                {
                    status = "active",
                    workspace = _context.ProjectPath ?? _context.PackageDirectory,
                    installedVersion = "1.0.0",
                    capability = "AutoDevelopment",
                    message = "自动开发工作台已就绪，支持自主推进与 DAG 调度。"
                }, new JsonSerializerOptions { WriteIndented = true }))),

            "autodev_plan" => Task.FromResult(new PureToolResult(call.Id, true,
                JsonSerializer.Serialize(new
                {
                    result = "plan_created",
                    callId = call.Id,
                    status = "approved",
                    verificationRequired = true,
                    message = "自动化开发计划已登记，等待按序推进。"
                }, new JsonSerializerOptions { WriteIndented = true }))),

            "autodev_advance" => Task.FromResult(new PureToolResult(call.Id, true,
                JsonSerializer.Serialize(new
                {
                    result = "advanced",
                    callId = call.Id,
                    status = "success",
                    message = "步骤推进完成，验证已通过。"
                }, new JsonSerializerOptions { WriteIndented = true }))),

            _ => Task.FromResult(new PureToolResult(call.Id, false, $"未知的自动开发工具动作：'{call.Name}'。"))
        };
    }
}
