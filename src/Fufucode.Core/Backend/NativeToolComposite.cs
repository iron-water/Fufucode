using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Plugins;

namespace Fufucode.Core.Backend;

/// <summary>
/// 原生会话的组合工具调度器：插件工具按当前实时目录路由到对应注册表，
/// 其余工具落到会话自身的 <see cref="ProductionToolDispatcher"/>（工作区正确）。
/// 插件注册表由组合根延迟提供，安装/卸载后下一次请求立即反映。
/// </summary>
public sealed class NativeToolComposite : IPureToolDispatcher, IPluginToolCatalog
{
    private readonly IPureToolDispatcher _builtin;
    public IPureToolDispatcher Builtin => _builtin;
    private readonly Func<IReadOnlyList<IPureToolDispatcher>>? _providers;

    public NativeToolComposite(IPureToolDispatcher builtin,
        Func<IReadOnlyList<IPureToolDispatcher>>? providers)
    {
        _builtin = builtin ?? throw new ArgumentNullException(nameof(builtin));
        _providers = providers;
    }

    private IReadOnlyList<IPluginToolCatalog> Catalogs() =>
        _providers?.Invoke().OfType<IPluginToolCatalog>().ToArray() ?? [];

    public IReadOnlyList<string> ActiveToolNames =>
        Catalogs().SelectMany(catalog => catalog.ActiveToolNames)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<PluginToolRegistration> ActiveRegistrations =>
        Catalogs().SelectMany(catalog => catalog.ActiveRegistrations)
            .GroupBy(registration => registration.Name, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(registration => registration.Name, StringComparer.Ordinal)
            .ToArray();

    public async Task<PureToolResult> DispatchAsync(PureToolCall call,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        var providers = _providers?.Invoke() ?? [];

        foreach (var provider in providers)
        {
            if (provider is not IPluginToolCatalog catalog) continue;
            if (!Contains(catalog.ActiveToolNames, call.Name)) continue;
            return await provider.DispatchAsync(call, cancellationToken).ConfigureAwait(false);
        }

        return await _builtin.DispatchAsync(call, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>目录实现可能返回集合或数组；两者都按 O(1) / O(n) 各自最优方式判断。</summary>
    private static bool Contains(IReadOnlyList<string> names, string name)
    {
        if (names is HashSet<string> set) return set.Contains(name);
        for (var i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], name, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
