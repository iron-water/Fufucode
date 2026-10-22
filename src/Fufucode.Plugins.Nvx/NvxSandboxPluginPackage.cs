using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.Nvx;

/// <summary>
/// NVX 沙箱的可选插件入口。NVX/OpenVMM 载荷由统一 runtime/nvx 工具链提供，
/// 不随默认 Fufucode Core 程序集分发。
/// </summary>
public sealed class NvxSandboxPluginPackage : IPluginPackage
{
    public string Id => "sandbox.nvx";
    public string Version => "0.1.0";
    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) =>
        new NvxSandboxToolHandler(context.GetService<ISandboxExecutionService>());
}

/// <summary>NVX 工具适配器骨架；真实宿主接线前拒绝执行并给出明确原因。</summary>
public sealed class NvxSandboxToolHandler : IPluginToolHandler
{
    private const string ExecuteTool = "sandbox_nvx_execute";
    private static readonly PluginToolRegistration Registration = new(
        ExecuteTool,
        "在 NVX 硬件隔离微虚拟机中执行一次工作负载。",
        "{\"type\":\"object\",\"properties\":{\"argv\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"cwd\":{\"type\":\"string\"},\"timeoutMs\":{\"type\":\"integer\"}},\"required\":[\"argv\"]}",
        PluginCapability.SandboxExecution);
    private readonly ISandboxExecutionService? _service;

    public NvxSandboxToolHandler(ISandboxExecutionService? service) => _service = service;
    public IReadOnlyList<PluginToolRegistration> Registrations => [Registration];
    public PluginCapability? Capability => PluginCapability.SandboxExecution;

    public Task<PureToolResult> ExecuteAsync(PureToolCall call, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PureToolResult(call.Id, false,
            _service is null ? "NVX 沙箱宿主尚未接线。" : "NVX 沙箱执行适配器尚未完成请求解析接线。"));
}
