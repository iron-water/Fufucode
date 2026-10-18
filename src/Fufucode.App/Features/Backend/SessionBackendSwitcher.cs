using System;
using Fufucode.Core.Backend;

namespace Fufucode.App.Features.Backend;

/// <summary>
/// 会话后端一键切换服务：把会话绑定与全局默认存取组合在一起。
/// 切换先按目标内核解析（未注册即拒绝），成功后重绑执行引擎并保留时间线。
/// </summary>
public sealed class SessionBackendSwitcher
{
    private readonly IBackendRegistry _registry;
    private readonly BackendPreferenceStore _preference;

    public SessionBackendSwitcher(IBackendRegistry registry, BackendPreferenceStore preference)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _preference = preference ?? throw new ArgumentNullException(nameof(preference));
    }

    /// <summary>全局默认后端。</summary>
    public EngineId DefaultBackend => _preference.DefaultBackend;

    /// <summary>设置全局默认后端（持久化）。</summary>
    public void SetDefaultBackend(EngineId backend) => _preference.SetDefault(backend);

    /// <summary>按全局默认创建会话绑定。</summary>
    public SessionBackendBinding CreateSession(string sessionId) =>
        new(sessionId, _preference.DefaultBackend);

    /// <summary>
    /// 切换会话后端。返回重绑后的内核；已有时间线与消息历史原样保留。
    /// </summary>
    public IBackendEngine Switch(SessionBackendBinding binding, EngineId target)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return binding.Rebind(target, _registry);
    }

    /// <summary>会话后端展示视图。</summary>
    public SessionBackendView Describe(SessionBackendBinding binding) =>
        SessionBackendView.For(binding, _registry);
}
