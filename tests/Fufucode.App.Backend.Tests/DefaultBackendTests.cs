using System;
using System.IO;
using Fufucode.App.Features.Backend;
using Fufucode.Core;
using Fufucode.Core.Backend;

namespace Fufucode.App.Backend.Tests;

/// <summary>全局默认后端（NativeCSharp 为原生核心，四后端为插件）的存取与持久化。</summary>
internal static class DefaultBackendTests
{
    public static void Run(Checks checks)
    {
        var root = Path.Combine(Path.GetTempPath(), "fufucode-backend-pref-" + Guid.NewGuid().ToString("N"));
        try
        {
            var layout = new AppDataLayout(root);
            var store = new BackendPreferenceStore(layout);
            checks.Equal(EngineId.NativeCSharp, store.DefaultBackend, "无配置文件时全局默认后端为 NativeCSharp 原生内核");

            var changed = 0;
            store.Changed += _ => changed++;
            store.SetDefault(EngineId.Codex);
            checks.Equal(EngineId.Codex, store.DefaultBackend, "设置后全局默认后端为 Codex");
            checks.Equal(1, changed, "默认后端变更触发一次 Changed 事件");

            var reopened = new BackendPreferenceStore(new AppDataLayout(root));
            checks.Equal(EngineId.Codex, reopened.DefaultBackend, "默认后端跨实例持久化");

            var switcher = new SessionBackendSwitcher(BackendRegistry.CreateDefault(new FakeRuntimeProbe()), reopened);
            checks.Equal(EngineId.Codex, switcher.DefaultBackend, "切换服务读取全局默认后端");
            switcher.SetDefaultBackend(EngineId.Omp);
            checks.Equal(EngineId.Omp, reopened.DefaultBackend, "切换服务写入全局默认后端");

            var binding = switcher.CreateSession("session-1");
            checks.Equal(EngineId.Omp, binding.Engine, "按全局默认创建会话绑定");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        CorruptFileFallsBack(checks);
    }

    private static void CorruptFileFallsBack(Checks checks)
    {
        var root = Path.Combine(Path.GetTempPath(), "fufucode-backend-corrupt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var layout = new AppDataLayout(root);
            layout.EnsureCreated();
            File.WriteAllText(Path.Combine(root, "backend.json"), "{ not valid json");
            var store = new BackendPreferenceStore(layout);
            checks.Equal(EngineId.NativeCSharp, store.DefaultBackend, "损坏配置回退到默认 NativeCSharp 原生内核");
            checks.Check(store.LastLoadError is not null, "损坏配置记录加载错误供诊断");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
