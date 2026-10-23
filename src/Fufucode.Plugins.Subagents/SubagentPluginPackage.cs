using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.Subagents;

/// <summary>可选 Subagent 插件：install 后提供单一、默认开启的子代理能力，无档位选择。</summary>
public sealed class SubagentPluginPackage : IPluginPackage
{
    public const string PluginId = "workspace.subagents";
    private SubagentFactory? _factory;

    public string Id => PluginId;
    public string Version => "1.0.0";

    public Task InitializeAsync(PluginPackageContext context, CancellationToken cancellationToken = default)
    {
        _factory = new SubagentFactory();
        NativeSubagentRegistry.Register(_factory);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (_factory is not null)
        {
            NativeSubagentRegistry.Unregister(_factory);
            _factory.Clear();
        }
        _factory = null;
        return Task.CompletedTask;
    }

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => null;

    private sealed class SubagentFactory : INativeSubagentFactory
    {
        public INativeSubagentRuntime Create(IPureModelClient modelClient, IPureToolDispatcher capabilities,
            string? defaultModelId, string? workspaceRoot,
            IReadOnlyList<PureToolDefinition> capabilityDefinitions)
        {
            var runtime = new SubagentRuntime(modelClient, capabilities, defaultModelId, workspaceRoot,
                capabilityDefinitions);
            _runtimes.Add(runtime);
            return runtime;
        }

        public void Clear() => _runtimes.Clear();

        private readonly List<SubagentRuntime> _runtimes = new();
    }

    private sealed class SubagentRuntime : INativeSubagentRuntime, INativeSubagentDispatcher
    {
        private readonly IPureModelClient _modelClient;
        private readonly IPureToolDispatcher _capabilities;
        private readonly string? _defaultModelId;
        private readonly string? _workspaceRoot;
        private readonly IReadOnlyList<PureToolDefinition> _capabilityDefinitions;
        private SubagentRunner _runner;

        public SubagentRuntime(IPureModelClient modelClient, IPureToolDispatcher capabilities,
            string? defaultModelId, string? workspaceRoot,
            IReadOnlyList<PureToolDefinition> capabilityDefinitions)
        {
            _modelClient = modelClient;
            _capabilities = capabilities;
            _defaultModelId = defaultModelId;
            _workspaceRoot = workspaceRoot;
            _capabilityDefinitions = capabilityDefinitions;
            _runner = CreateRunner();
        }

        public IPureToolDispatcher Inner => _capabilities;
        public bool IsActive => true;
        public Func<PureToolCall, CancellationToken, Task<bool>>? ApprovalGate
        {
            get => _runner.ApprovalGate;
            set => _runner.ApprovalGate = value;
        }
        public Func<IReadOnlyList<PureAgentMessage>>? HistoryProvider
        {
            get => _runner.HistoryProvider;
            set => _runner.HistoryProvider = value;
        }
        public Func<PermissionMode>? ParentMode
        {
            get => _runner.ParentMode;
            set => _runner.ParentMode = value;
        }
        public Action<NativeSubagentProgress>? Progress
        {
            get => _progress;
            set => _progress = value;
        }
        private Action<NativeSubagentProgress>? _progress;

        public IPureToolDispatcher Wrap(IPureToolDispatcher inner) =>
            new SubagentDispatcher(inner, _runner);

        public IReadOnlyList<NativeSubagentDescriptor> Agents =>
            _runner.Agents.Select(agent => new NativeSubagentDescriptor(
                agent.Name, agent.Description, agent.Mode.ToString().ToLowerInvariant())).ToArray();

        public string BuildPromptSection() => _runner.BuildPromptSection();

        /// <summary>只读 utility 任务：使用内置 explore 代理，失败时返回 null 由调用方回退。</summary>
        public async Task<string?> RunUtilityAsync(string prompt, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return null;
            try
            {
                var result = await _runner.RunAsync("explore", prompt, null, cancellationToken).ConfigureAwait(false);
                return result.Success ? result.Content?.Trim() : null;
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        private SubagentRunner CreateRunner()
        {
            var runner = new SubagentRunner(AgentRegistry.Discover(_workspaceRoot), _modelClient,
                _capabilities, _defaultModelId, _workspaceRoot, _capabilityDefinitions);
            runner.Progress = progress => _progress?.Invoke(new NativeSubagentProgress(
                progress.JobId, progress.Agent, progress.Kind, progress.Detail));
            return runner;
        }
    }
}
