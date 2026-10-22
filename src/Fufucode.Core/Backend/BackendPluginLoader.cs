using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Fufucode.Core.Backend;


namespace Fufucode.Core;

/// <summary>
/// 后端插件加载器：默认包不引用任何外部后端程序集。
/// 只有当 Pi 后端插件（Fufucode.Pi.dll）实际存在于插件目录或应用目录时，
/// 才动态加载它并把 <see cref="BackendProcessSpec"/> 翻译成 RPC 客户端工厂。
/// 缺失时返回 null，调用方据此判定该后端不可用——不伪造可用性。
/// </summary>
public static class BackendPluginLoader
{
    /// <summary>Pi 后端插件程序集文件名。</summary>
    public const string PiAssemblyName = "Fufucode.Pi.dll";

    private static readonly object Gate = new();
    private static readonly Dictionary<BackendId, IBackendSessionFactory> SessionFactories = new();
    // 显式停用的后端：即使插件程序集仍在磁盘/内存，也不再回退加载。
    private static readonly HashSet<BackendId> DisabledBackends = new();
    private static Assembly? _piAssembly;
    private static bool _piProbed;
    // Pi 与 OMP 是两个独立插件，但共用 RPC 客户端程序集；不能因为后安装的插件覆盖前一个插件的查找路径。
    // 上限：每次激活插件都会登记目录，多次安装/启用循环会让列表无限增长。只保留最近登记的若干个
    // （按不区分大小写去重，超出时丢弃最旧的）。被丢弃的旧目录若已不存在或装错版本也无害：
    // 查找时逐个检查文件是否存在，找不到就继续下一个候选路径。
    private const int MaxRegisteredDirectories = 16;
    private static readonly List<string> RegisteredDirectories = new();

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

    /// <summary>插件停用后禁止回退加载；重新启用时解除。</summary>
    public static void SetBackendEnabled(BackendId backend, bool enabled)
    {
        lock (Gate)
        {
            if (enabled) DisabledBackends.Remove(backend);
            else
            {
                SessionFactories.Remove(backend);
                DisabledBackends.Add(backend);
                // Pi/OMP 共用 Pi 程序集；停用后清掉探测缓存，避免重启用时沿用
                // 旧路径或旧程序集判断。已在默认 ALC 中的程序集仍会由 CreateRpcFactory 拒绝。
                if (backend is BackendId.Pi or BackendId.Omp)
                {
                    _piAssembly = null;
                    _piProbed = false;
                }
            }
        }
    }

    /// <summary>取得已注册的会话工厂；未安装/未启用时返回 null。</summary>
    public static IBackendSessionFactory? GetSessionFactory(BackendId backend)
    {
        lock (Gate)
        {
            if (SessionFactories.TryGetValue(backend, out var factory)) return factory;
            if (DisabledBackends.Contains(backend)) return null;
            var loaded = TryLoadSessionFactory(backend);
            if (loaded is not null) SessionFactories[backend] = loaded;
            return loaded;
        }
    }

    private static IBackendSessionFactory? TryLoadSessionFactory(BackendId backend)
    {
        var (typeName, assemblyName) = backend switch
        {
            BackendId.Codex => ("Fufucode.Plugins.Codex.CodexSessionFactory", "Fufucode.Plugins.Codex"),
            BackendId.Dsh => ("Fufucode.Plugins.Dsh.DshSessionFactory", "Fufucode.Plugins.Dsh"),
            BackendId.Claude => ("Fufucode.Plugins.Claude.ClaudeSessionFactory", "Fufucode.Plugins.Claude"),
            _ => (null, null),
        };
        if (typeName is null || assemblyName is null) return null;

        // 1. 优先查当前已加载程序集
        foreach (var asm in AssemblyLoadContext.Default.Assemblies)
        {
            if (string.Equals(asm.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
            {
                var type = asm.GetType(typeName, throwOnError: false);
                if (type is not null && Activator.CreateInstance(type) is IBackendSessionFactory instance)
                    return instance;
            }
        }

        // 2. 查磁盘候选路径
        var baseDir = AppContext.BaseDirectory;
        var dllName = assemblyName + ".dll";
        var candidates = new List<string>();
        lock (Gate)
        {
            foreach (var dir in RegisteredDirectories)
                candidates.Add(Path.Combine(dir, dllName));
        }
        candidates.Add(Path.Combine(baseDir, dllName));
        candidates.Add(Path.Combine(baseDir, "plugins", backend.ToString().ToLowerInvariant(), dllName));
        candidates.Add(Path.Combine(baseDir, "backend." + backend.ToString().ToLowerInvariant(), dllName));

        // 向上寻找源码产物（开发调试环境）
        var dirInfo = new DirectoryInfo(baseDir);
        while (dirInfo is not null)
        {
            var devCandidate = Path.Combine(dirInfo.FullName, "src", assemblyName, "bin", "Debug", "net10.0", dllName);
            if (File.Exists(devCandidate))
            {
                candidates.Add(devCandidate);
                break;
            }
            dirInfo = dirInfo.Parent;
        }

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
                var type = asm.GetType(typeName, throwOnError: false);
                if (type is not null && Activator.CreateInstance(type) is IBackendSessionFactory instance)
                    return instance;
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// 为指定后端创建 RPC 客户端工厂。FUMO 永远返回 null（它不使用 RPC 进程）；
    /// 未安装插件返回 null；宿主显式给出进程规格（自定义可执行文件）时允许回退到内置运行时。
    /// </summary>
    public static IRpcClientFactory? CreateRpcFactory(BackendProcessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        lock (Gate)
        {
            if (DisabledBackends.Contains(spec.Backend)) return null;
        }
        return spec.Backend switch
        {
            BackendId.Pi or BackendId.Omp => CreatePiFactory(spec),
            _ => null,
        };
    }

    private static IRpcClientFactory? CreatePiFactory(BackendProcessSpec spec)
    {
        var assembly = LoadPiAssembly();
        if (assembly is null) return null;

        var factoryType = assembly.GetType("Fufucode.Pi.PiRpcClientFactory", throwOnError: false);
        if (factoryType is null) return null;
        try
        {
            return Activator.CreateInstance(factoryType, spec, null) as IRpcClientFactory;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Assembly? LoadPiAssembly()
    {
        lock (Gate)
        {
            if (_piProbed) return _piAssembly;
            _piProbed = true;
            try
            {
                // 已在默认加载上下文时直接返回现有程序集（不能重复 LoadFromAssemblyPath）。
                foreach (var loaded in AssemblyLoadContext.Default.Assemblies)
                {
                    if (string.Equals(loaded.GetName().Name, "Fufucode.Pi", StringComparison.OrdinalIgnoreCase))
                        return _piAssembly = loaded;
                }

                var path = FindPiAssembly();
                if (path is null) return null;
                _piAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            }
            catch (Exception)
            {
                _piAssembly = null;
            }

            return _piAssembly;
        }
    }

    private static string? FindPiAssembly()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>();
        lock (Gate)
        {
            for (var i = RegisteredDirectories.Count - 1; i >= 0; i--)
                candidates.Add(Path.Combine(RegisteredDirectories[i], PiAssemblyName));
        }
        candidates.Add(Path.Combine(baseDir, PiAssemblyName));
        candidates.Add(Path.Combine(baseDir, "backend.pi", PiAssemblyName));
        candidates.Add(Path.Combine(baseDir, "plugins", "backend.pi", PiAssemblyName));
        candidates.Add(Path.Combine(baseDir, "backend.omp", PiAssemblyName));
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        return null;
    }

    /// <summary>登记插件安装目录（由插件包初始化时调用），后续查找优先命中该目录。</summary>
    public static void RegisterPluginDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        var full = Path.GetFullPath(directory);
        lock (Gate)
        {
            RegisteredDirectories.RemoveAll(item => string.Equals(item, full, StringComparison.OrdinalIgnoreCase));
            RegisteredDirectories.Add(full);
            if (RegisteredDirectories.Count > MaxRegisteredDirectories)
                RegisteredDirectories.RemoveRange(0, RegisteredDirectories.Count - MaxRegisteredDirectories);
        }
        ResetCache();
    }

    /// <summary>注销插件安装目录（插件关闭时调用）；目录不存在时为空操作。</summary>
    public static void RemovePluginDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        var full = Path.GetFullPath(directory);
        lock (Gate)
        {
            if (RegisteredDirectories.RemoveAll(item => string.Equals(item, full, StringComparison.OrdinalIgnoreCase)) == 0)
                return;
        }
        ResetCache();
    }

    /// <summary>重新探测（插件安装/卸载后调用）；下次请求会重新查找程序集。</summary>
    public static void ResetCache()
    {
        lock (Gate)
        {
            _piAssembly = null;
            _piProbed = false;
        }
    }
}
