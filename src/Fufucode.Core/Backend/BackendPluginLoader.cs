using System;
using System.Collections.Generic;
using Fufucode.Core.Backend;


namespace Fufucode.Core;

/// <summary>
/// 后端插件注册表：默认包不引用任何外部后端程序集。
/// 五类后端（Pi/OMP/Claude/Codex/DSH）一律在自己的插件包初始化时显式登记，
/// 停用/卸载时对称注销；Core 不按程序集名、类型名或安装目录做任何隐式探测。
/// 未登记即未安装：返回 null，调用方据此判定该后端不可用——不伪造可用性。
/// </summary>
public static class BackendPluginLoader
{
    private static readonly object Gate = new();
    // 协议适配型后端（Claude/Codex/DSH）：一次会话打开一个自建对象。
    private static readonly Dictionary<BackendId, IBackendSessionFactory> SessionFactories = new();
    // RPC 型后端（Pi/OMP）：插件登记"按进程规格造工厂"的委托，真正启动发生在会话打开时。
    private static readonly Dictionary<BackendId, Func<BackendProcessSpec, IRpcClientFactory?>> RpcFactories = new();
    // 显式停用的后端：即使插件仍在内存中，也不再创建工厂。
    private static readonly HashSet<BackendId> DisabledBackends = new();

    /// <summary>
    /// 协议适配型后端插件注册自己的会话工厂。插件卸载/停用时必须注销。
    /// </summary>
    public static void RegisterSessionFactory(IBackendSessionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (Gate) SessionFactories[factory.Backend] = factory;
    }

    /// <summary>注销指定后端的会话工厂；与注册成对调用，避免卸载后仍可打开会话。</summary>
    public static void UnregisterSessionFactory(BackendId backend)
    {
        lock (Gate) SessionFactories.Remove(backend);
    }

    /// <summary>
    /// RPC 型后端插件注册工厂委托。传 null 等同于注销，便于插件按自身状态决定是否提供。
    /// </summary>
    public static void RegisterRpcClientFactory(
        BackendId backend, Func<BackendProcessSpec, IRpcClientFactory?>? factory)
    {
        lock (Gate)
        {
            if (factory is null) RpcFactories.Remove(backend);
            else RpcFactories[backend] = factory;
        }
    }

    /// <summary>注销指定后端的 RPC 工厂；与注册成对调用。</summary>
    public static void UnregisterRpcClientFactory(BackendId backend)
    {
        lock (Gate) RpcFactories.Remove(backend);
    }

    /// <summary>插件停用后禁止创建工厂；重新启用时解除（重新启用由插件初始化重新登记）。</summary>
    public static void SetBackendEnabled(BackendId backend, bool enabled)
    {
        lock (Gate)
        {
            if (enabled) DisabledBackends.Remove(backend);
            else
            {
                SessionFactories.Remove(backend);
                RpcFactories.Remove(backend);
                DisabledBackends.Add(backend);
            }
        }
    }

    /// <summary>取得已注册的会话工厂；未安装/未启用时返回 null。</summary>
    public static IBackendSessionFactory? GetSessionFactory(BackendId backend)
    {
        lock (Gate)
        {
            if (DisabledBackends.Contains(backend)) return null;
            return SessionFactories.TryGetValue(backend, out var factory) ? factory : null;
        }
    }

    /// <summary>
    /// 为指定后端创建 RPC 客户端工厂。FUMO 永远返回 null（它不使用 RPC 进程）；
    /// 插件未安装/未启用时同样返回 null。
    /// </summary>
    public static IRpcClientFactory? CreateRpcFactory(BackendProcessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        Func<BackendProcessSpec, IRpcClientFactory?>? factory;
        lock (Gate)
        {
            if (DisabledBackends.Contains(spec.Backend)) return null;
            if (!RpcFactories.TryGetValue(spec.Backend, out factory)) return null;
        }
        return factory(spec);
    }
}
