using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Backend;

namespace Fufucode.Core.Plugins;

/// <summary>
/// 插件工具调度表：以插件市场实时状态为准，动态注册/注销插件工具。
/// 未安装或未启用的插件不暴露工具；已注册工具在卸载/停用后立即失效，
/// 不会留下仍可调用的幽灵工具。对清单外工具仍委托给宿主基础调度器。
/// </summary>
public sealed class PluginToolRegistry : IPureToolDispatcher, IPluginToolCatalog, IDelegatingToolDispatcher
{
    private readonly IPureToolDispatcher? _inner;
    public IPureToolDispatcher? InnerDispatcher => _inner;
    private readonly PluginCapabilityGateway _gateway;
    private readonly object _gate = new();
    private readonly Dictionary<string, (IPluginToolHandler Handler, PluginToolRegistration Registration)> _handlers =
        new(StringComparer.Ordinal);

    /// <summary>已注册的处理器（含当前无工具契约的动态处理器），用于刷新与不可用原因解释。</summary>
    private readonly List<IPluginToolHandler> _registeredHandlers = new();

    public PluginToolRegistry(PluginCapabilityGateway gateway, IPureToolDispatcher? inner = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _inner = inner;
        _gateway.Changed += NotifyChanged;
    }

    /// <summary>插件工具集合发生变化（注册/注销）时触发，宿主可据此刷新工具清单。</summary>
    public event Action? Changed;

    /// <summary>注册一个插件工具处理器；同名工具只能有一个提供者。</summary>
    public void Register(IPluginToolHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            var registrations = handler.Registrations.ToArray();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var registration in registrations)
            {
                if (ProductionToolDispatcher.IsBuiltinToolName(registration.Name))
                    throw new InvalidOperationException(
                        $"插件工具 '{registration.Name}' 与原生内置工具同名，不允许覆盖。");
                if (!names.Add(registration.Name) || _handlers.ContainsKey(registration.Name))
                    throw new InvalidOperationException($"插件工具 '{registration.Name}' 已注册，不允许多个提供者。");
            }
            if (!_registeredHandlers.Contains(handler)) _registeredHandlers.Add(handler);
            foreach (var registration in registrations)
                _handlers[registration.Name] = (handler, registration);
        }
        InvalidateSnapshot();
        Changed?.Invoke();
    }

    /// <summary>
    /// 按各已注册处理器的当前 <see cref="IPluginToolHandler.Registrations"/> 重建工具表。
    /// 动态工具集（MCP 子进程启动后 tools/list 才可用）必须在对齐完成后调用本方法，
    /// 否则新工具不会出现在 <see cref="ActiveToolNames"/> 中。
    /// </summary>
    public void Refresh()
    {
        lock (_gate)
        {
            var refreshed = new Dictionary<string, (IPluginToolHandler Handler, PluginToolRegistration Registration)>(StringComparer.Ordinal);
            foreach (var handler in _registeredHandlers)
            {
                foreach (var registration in handler.Registrations)
                {
                    if (ProductionToolDispatcher.IsBuiltinToolName(registration.Name))
                        throw new InvalidOperationException(
                            $"插件工具 '{registration.Name}' 与原生内置工具同名，不允许覆盖。");
                    if (refreshed.ContainsKey(registration.Name))
                        throw new InvalidOperationException($"插件工具 '{registration.Name}' 已注册，不允许多个提供者。");
                    refreshed[registration.Name] = (handler, registration);
                }
            }
            _handlers.Clear();
            foreach (var entry in refreshed) _handlers.Add(entry.Key, entry.Value);
        }
        InvalidateSnapshot();
        Changed?.Invoke();
    }

    /// <summary>注销该处理器：撤销其工具并停止为其解释不可用原因；不存在时是幂等空操作。</summary>
    public bool Unregister(IPluginToolHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var removed = false;
        lock (_gate)
        {
            foreach (var name in _handlers.Where(entry => ReferenceEquals(entry.Value.Handler, handler))
                         .Select(entry => entry.Key).ToArray())
            {
                _handlers.Remove(name);
                removed = true;
            }
            if (_registeredHandlers.Remove(handler)) removed = true;
        }
        if (removed)
        {
            InvalidateSnapshot();
            Changed?.Invoke();
        }
        return removed;
    }

    /// <summary>当前真正可调用的工具名（已按插件门禁过滤）。</summary>
    public IReadOnlyList<string> ActiveToolNames => Snapshot().Names;

    /// <summary>当前可调用的动作契约（名称、说明、参数 Schema），供模型请求声明工具。</summary>
    public IReadOnlyList<PluginToolRegistration> ActiveRegistrations => Snapshot().Registrations;

    /// <summary>
    /// 工具目录只读快照。每次会话请求都会读取名称与 Schema，原来每次都加锁、
    /// 过滤、排序并复制一遍；插件变更频率远低于请求频率，改为变更时重建、
    /// 读取时直接复用同一份不可变数组。
    /// </summary>
    private sealed record CatalogSnapshot(
        IReadOnlyList<string> Names,
        IReadOnlyList<PluginToolRegistration> Registrations);

    private volatile CatalogSnapshot? _snapshot;

    private CatalogSnapshot Snapshot()
    {
        if (_snapshot is { } cached) return cached;
        lock (_gate)
        {
            if (_snapshot is { } existing) return existing;
            var active = _handlers.Values
                .Where(entry => _gateway.IsAvailable(entry.Registration.Capability))
                .Select(entry => entry.Registration)
                .OrderBy(registration => registration.Name, StringComparer.Ordinal)
                .ToArray();
            var created = new CatalogSnapshot(
                active.Select(registration => registration.Name).ToArray(),
                active);
            _snapshot = created;
            return created;
        }
    }

    private void InvalidateSnapshot()
    {
        _snapshot = null;
    }

    /// <summary>某工具当前是否可用；不可用时给出原因（未注册或插件未启用）。</summary>
    public bool IsActive(string toolName, out string reason)
    {
        lock (_gate)
        {
            if (!_handlers.TryGetValue(toolName, out var entry))
            {
                reason = $"插件工具 '{toolName}' 未注册。";
                return false;
            }

            var status = _gateway.Status(entry.Registration.Capability);
            reason = status.IsAvailable ? string.Empty : status.Reason;
            return status.IsAvailable;
        }
    }

    public async Task<PureToolResult> DispatchAsync(PureToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        IPluginToolHandler? handler = null;
        lock (_gate)
        {
            if (_handlers.TryGetValue(call.Name, out var entry) &&
                _gateway.IsAvailable(entry.Registration.Capability))
            {
                handler = entry.Handler;
            }
        }

        if (handler is not null)
            return await handler.ExecuteAsync(call, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            // 动态处理器认领该名字时给出它的真实原因（未安装 / 已停用 / 清单未就绪），
            // 而不是泛化的“未注册”，也不把调用透传给其它调度器。
            foreach (var candidate in _registeredHandlers)
            {
                if (!candidate.OwnsToolName(call.Name)) continue;
                var status = candidate.Capability is { } capability ? _gateway.Status(capability) : null;
                if (status is { IsAvailable: false } unavailable)
                    return new PureToolResult(call.Id, false, unavailable.Reason);
                if (candidate.ExplainUnavailable(call.Name) is { Length: > 0 } explanation)
                    return new PureToolResult(call.Id, false, explanation);
                // 能力可用且处理器没有额外说明：按“未注册”继续，不阻断后续调度器。
                break;
            }
        }

        if (_inner is not null)
            return await _inner.DispatchAsync(call, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            return new PureToolResult(call.Id, false, IsActive(call.Name, out var reason)
                ? $"插件工具 '{call.Name}' 暂不可执行。"
                : reason);
        }
    }

    private void NotifyChanged()
    {
        // 能力门禁状态变化会改变可见工具集合，快照必须失效。
        InvalidateSnapshot();
        Changed?.Invoke();
    }
}
