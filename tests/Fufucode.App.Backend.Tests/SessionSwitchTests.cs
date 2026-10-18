using System;
using System.Linq;
using Fufucode.App.Features.Backend;
using Fufucode.Core.Backend;

namespace Fufucode.App.Backend.Tests;

/// <summary>会话后端平滑切换：重绑执行引擎并保留时间线与消息历史。</summary>
internal static class SessionSwitchTests
{
    public static void Run(Checks checks)
    {
        var probe = new FakeRuntimeProbe()
            .Set(EngineId.Pi, true, "pi-node", "1.1.0")
            .Set(EngineId.Omp, true, "omp-cli", "18.8.5")
            .Set(EngineId.Codex, false)
            .Set(EngineId.Dsh, false)
            .Set(EngineId.NativeCSharp, true, "dotnet", "10.0.401");
        var registry = BackendRegistry.CreateDefault(probe);
        var switcher = new SessionBackendSwitcher(registry, new BackendPreferenceStore(TempLayout()));

        var binding = switcher.CreateSession("session-switch");
        checks.Equal(EngineId.NativeCSharp, binding.Engine, "新会话默认绑定 NativeCSharp 原生内核");
        binding.Append(new SessionTimelineEntry(SessionTimelineRole.User, "你好"));
        binding.Append(new SessionTimelineEntry(SessionTimelineRole.Assistant, "你好，我是 Pi。"));

        var view = switcher.Describe(binding);
        checks.Equal("#8B5CF6", view.Badge.BadgeColor, "初始会话展示 NativeCSharp 紫色徽标");
        checks.Check(view.IsAvailable, "初始会话展示运行时可用");

        var engine = switcher.Switch(binding, EngineId.Omp);
        checks.Equal(EngineId.Omp, engine.Id, "切换到 OMP 返回 OMP 引擎");
        checks.Equal(EngineId.Omp, binding.Engine, "会话绑定更新为 OMP");
        checks.Equal("omp", binding.BackendId, "会话持久化标识更新为 omp");
        checks.Equal(2, binding.Timeline.Count, "切换后时间线条目数量保持不变");
        checks.Equal("你好", binding.Timeline[0].Text, "切换后首条用户消息保留");
        checks.Equal("你好，我是 Pi。", binding.Timeline[1].Text, "切换后助手消息保留");

        var switched = switcher.Describe(binding);
        checks.Equal("#D97706", switched.Badge.BadgeColor, "切换后会话展示 OMP 橙色徽标");
        checks.Equal("omp", switched.BackendId, "切换后视图持久化标识为 omp");

        var partial = new BackendRegistry(new IBackendEngine[]
        {
            new PiBackendEngine(probe),
            new OmpBackendEngine(probe),
        });
        var partialSwitcher = new SessionBackendSwitcher(partial,
            new BackendPreferenceStore(TempLayout()));
        var beforeTimeline = binding.Timeline.ToArray();
        checks.Throws<InvalidOperationException>(() => partialSwitcher.Switch(binding, EngineId.Codex),
            "切换到未注册内核抛出而不改绑定");
        checks.Equal(EngineId.Omp, binding.Engine, "失败切换后绑定保持 OMP");
        checks.Check(beforeTimeline.SequenceEqual(binding.Timeline), "失败切换后时间线不变");

        switcher.Switch(binding, EngineId.Dsh);
        checks.Equal(EngineId.Dsh, binding.Engine, "成功切换到 DSH 更新绑定");
        checks.Equal("#0891B2", switcher.Describe(binding).Badge.BadgeColor, "切换后展示 DSH 青色徽标");
        checks.Equal(2, binding.Timeline.Count, "多次切换后时间线仍保留");
    }

    private static Core.AppDataLayout TempLayout() =>
        new(System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "fufucode-backend-switch-" + Guid.NewGuid().ToString("N")));
}
