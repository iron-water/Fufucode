using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading.Tasks;

namespace Fufucode.Core.Plugins;

/// <summary>校验载荷后安装并激活；失败恢复旧版本，状态和加载错误不伪装为成功。</summary>
public sealed class PluginPackageManager : IAsyncDisposable
{
    public static IRemoteControlService? ActiveRemoteControlService { get; set; }
    // 进程级工厂登记表：键是插件 id，数量受市场目录限制；提供注销入口供测试/卸载清理，
    // 避免宿主生命周期内重复登记留下无法回收的闭包。
    private static readonly ConcurrentDictionary<string, Func<IPluginPackage>> _registeredFactories = new(StringComparer.OrdinalIgnoreCase);
    private readonly AppDataLayout _layout;
    private readonly PluginToolRegistry _toolRegistry;
    private readonly PluginStateStore _stateStore;
    private readonly PluginDependencyStore _dependencyStore;
    private volatile Func<Type, object?>? _serviceResolver;

    /// <summary>
    /// 宿主服务解析器：组合根可以在构造后注入（例如性能监控插件需要的宿主桥）。
    /// 插件激活时通过 <see cref="PluginPackageContext.GetService{T}"/> 取得。
    /// </summary>
    public Func<Type, object?>? ServiceResolver
    {
        get => _serviceResolver;
        set => _serviceResolver = value;
    }
    private readonly Dictionary<string, (IPluginPackage Package, IPluginToolHandler? Handler, PluginPackageContext Context)> _activePlugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PluginPageContribution> _pageContributions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AssemblyLoadContext> _loadContexts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 页面贡献装饰钩子：宿主可包装插件返回的贡献（例如把页面包装进自己的持久化层、
    /// 为设置页提供额外内容）。Core 不引用任何具体插件。
    /// </summary>
    public static Func<string, IPluginPackage, PluginPageContribution?, PluginPageContribution?>? PageDecorator { get; set; }

    /// <summary>已激活插件的页面贡献发生变化（激活、停用、更新）。宿主据此挂载/卸载页面。</summary>
    public event Action? PagesChanged;

    /// <summary>诊断：当前已激活的插件 id。</summary>
    public IReadOnlyCollection<string> GetActivePluginIds()
    {
        lock (_gate) return _activePlugins.Keys.ToArray();
    }

    /// <summary>页面贡献被宿主就地替换后，让页面宿主重新同步（不触发插件激活/停用事件）。</summary>
    public void RequestPageResync() => PageContributionsChanged?.Invoke();

    /// <summary>指定插件的页面贡献；未激活或未贡献页面时返回 null。</summary>
    public PluginPageContribution? PageContribution(string pluginId)
    {
        lock (_gate) return _pageContributions.TryGetValue(pluginId, out var page) ? page : null;
    }
    private readonly object _gate = new();
    private bool _changing;
    public PluginPackageManager(AppDataLayout layout, PluginToolRegistry toolRegistry, PluginStateStore? stateStore = null,
        Func<Type, object?>? serviceResolver = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _stateStore = stateStore ?? new(layout);
        _dependencyStore = new(layout.PluginsDirectory);
        _serviceResolver = serviceResolver;
        _stateStore.Changed += SyncActivePlugins;
        PluginStateNotifier.Changed += OnNotifiedStateChanged;
    }

    private void OnNotifiedStateChanged()
    {
        // 设置页、浏览器和工作区宿主各自持有一个管理器；跨实例写入后先刷新
        // 本地快照，再让统一 Changed 链路收敛工具和页面贡献。
        _stateStore.Reload();
    }
    public AppDataLayout Layout => _layout;
    public PluginStateStore StateStore => _stateStore;
    public PluginDependencyStore DependencyStore => _dependencyStore;
    public IReadOnlyDictionary<string, string> LoadErrors => _loadErrors;
    private readonly Dictionary<string, string> _loadErrors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前活动插件贡献的工作区页面；宿主据此动态注册/移除页面，不在应用里静态写死。</summary>
    public IReadOnlyDictionary<string, PluginPageContribution> PageContributions
    {
        get { lock (_gate) return new Dictionary<string, PluginPageContribution>(_pageContributions, StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>页面贡献集合发生变化（插件激活/停用/更新）。事件可能在非 UI 线程触发。</summary>
    public event Action? PageContributionsChanged;

    public static void RegisterPackageFactory(string pluginId, Func<IPluginPackage> factory)
    {
        PluginPathGuard.Segment(pluginId);
        _registeredFactories[pluginId] = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>注销进程级包工厂；未登记时为空操作。</summary>
    public static void UnregisterPackageFactory(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) return;
        _registeredFactories.TryRemove(pluginId, out _);
    }
    private string Target(string id) => PluginPathGuard.Within(_layout.PluginsDirectory, PluginPathGuard.Segment(id));
    private static void Known(string id)
    {
        PluginPathGuard.Segment(id);
        if (PluginMarketplaceCatalog.Find(id) is null) throw new ArgumentException($"未知插件 '{id}'。");
    }
    public bool HasPhysicalPayload(string pluginId) => File.Exists(Path.Combine(Target(pluginId), "manifest.json"));
    public void InstallPackage(string pluginId, string? sourcePayloadDirectory = null)
    {
        Known(pluginId);
        lock (_gate)
        {
            var definition = PluginMarketplaceCatalog.Find(pluginId)!;
            if (definition.IsEmbedded)
            {
                if (_stateStore.GetState(pluginId).IsInstalled)
                    throw new InvalidOperationException("插件已安装，请使用启用或停用操作。");
                _stateStore.Install(pluginId);
                return;
            }
            if (_stateStore.GetState(pluginId).IsInstalled && HasPhysicalPayload(pluginId))
                throw new InvalidOperationException("插件已安装，请使用更新操作。");
            Deploy(pluginId, sourcePayloadDirectory, false);
        }
    }
    public void UpdatePackage(string pluginId, string? newSourcePayloadDirectory = null)
    {
        Known(pluginId);
        lock (_gate)
        {
            if (!_stateStore.GetState(pluginId).IsInstalled || !HasPhysicalPayload(pluginId))
                throw new InvalidOperationException($"插件 '{pluginId}' 尚未安装。");
            Deploy(pluginId, newSourcePayloadDirectory, true);
        }
    }

    private void Deploy(string id, string? sourceDirectory, bool update)
    {
        var target = Target(id);
        var staging = PluginPathGuard.Within(_layout.PluginsDirectory, ".staging", id + "-" + Guid.NewGuid().ToString("N"));
        var backup = staging + ".backup";
        // 只有用户显式写过的状态才能在失败时回滚；默认/计算状态回写会伪造“用户已卸载”，
        // 使后续启动不再自动播种默认插件。
        var hadExplicitState = _stateStore.TryGetStoredState(id, out var storedState);
        var previous = hadExplicitState ? storedState : _stateStore.GetState(id);
        var wasActive = _activePlugins.ContainsKey(id);
        var oldDeps = wasActive ? _activePlugins[id].Package.Dependencies.ToArray() : Array.Empty<PluginDependencyDeclaration>();
        var acquired = new List<PluginDependencyDeclaration>();
        IPluginPackage? prepared = null;
        AssemblyLoadContext? preparedContext = null;
        var swapped = false;
        _changing = true;
        try
        {
            var source = ResolveSourceDirectory(id, sourceDirectory);
            Directory.CreateDirectory(staging);
            if (source is not null)
                CopyDirectory(source, staging);
            else if (_registeredFactories.TryGetValue(id, out var factory))
                File.WriteAllText(Path.Combine(staging, "manifest.json"),
                    JsonSerializer.Serialize(new { id, version = factory().Version, entryAssembly = id + ".dll" }));
            else throw new FileNotFoundException($"无法安装插件 '{id}'：没有可用的分发载荷。");

            (prepared, preparedContext) = ResolvePackage(id, staging);
            foreach (var dep in prepared.Dependencies)
            {
                _dependencyStore.ResolveAndAcquire(dep, id, source);
                if (!oldDeps.Any(old => DependencyKey(old) == DependencyKey(dep))) acquired.Add(dep);
            }
            DeactivatePluginLocked(id);
            if (Directory.Exists(target)) Directory.Move(target, backup);
            Directory.Move(staging, target);
            swapped = true;
            if (!update || previous.IsEnabled)
                ActivatePrepared(id, prepared, target, preparedContext);
            else preparedContext?.Unload();
            preparedContext = null;
            _stateStore.RestoreState(previous with { IsInstalled = true, IsEnabled = !update || previous.IsEnabled, LastUpdatedUtc = DateTime.UtcNow });
        }
        catch (Exception error)
        {
            var recoveryErrors = new List<Exception>();
            try { DeactivatePluginLocked(id); } catch (Exception e) { recoveryErrors.Add(e); }
            preparedContext?.Unload();
            try
            {
                if (swapped && Directory.Exists(target)) Directory.Delete(target, true);
                if (Directory.Exists(backup)) Directory.Move(backup, target);
            }
            catch (Exception e) { recoveryErrors.Add(e); }
            try { _dependencyStore.ReleaseDependencies(id, acquired); } catch (Exception e) { recoveryErrors.Add(e); }
            try
            {
                if (hadExplicitState) _stateStore.RestoreState(previous);
                if (wasActive) ActivatePluginLocked(id);
            }
            catch (Exception e) { recoveryErrors.Add(e); }
            if (recoveryErrors.Count > 0)
                throw new AggregateException($"插件 '{id}' 操作失败，恢复未完成；保留目录 '{backup}' 供恢复。", new[] { error }.Concat(recoveryErrors));
            throw new InvalidOperationException($"插件 '{id}' 操作失败：{error.Message}。" +
                (update ? "已自动回滚并恢复上一版本可用状态。" : "未安装新插件，原状态已保留。"), error);
        }
        finally
        {
            _changing = false;
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
        // Cleanup failures are reported as cleanup failures, never roll back a committed installation.
        try
        {
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            _dependencyStore.ReleaseDependencies(id, oldDeps.Where(old => !prepared!.Dependencies.Any(dep => DependencyKey(dep) == DependencyKey(old))));
        }
        catch (Exception e) { throw new IOException($"插件已更新，但旧载荷/依赖清理未完成：{e.Message}", e); }
    }
    private static string DependencyKey(PluginDependencyDeclaration d) => $"{d.PackageId}@{d.Version}/{d.TargetFileName ?? d.PackageId + ".dll"}".ToUpperInvariant();

    public void UninstallPackage(string pluginId)
    {
        Known(pluginId);
        if (PluginMarketplaceCatalog.Find(pluginId)!.IsEmbedded)
        {
            _stateStore.Uninstall(pluginId);
            return;
        }
        SetBackendEnabledFor(pluginId, false);
        if (PluginMarketplaceCatalog.Find(pluginId)!.IsCoreBuiltin) throw new InvalidOperationException("核心内置组件不可卸载。");
        lock (_gate)
        {
            var previous = _stateStore.GetState(pluginId);
            var deps = _activePlugins.TryGetValue(pluginId, out var entry) ? entry.Package.Dependencies : Array.Empty<PluginDependencyDeclaration>();
            _changing = true;
            try
            {
                DeactivatePluginLocked(pluginId);
                // Disable first: a file lock must not leave callable tools labelled uninstalled.
                _stateStore.RestoreState(previous with { IsEnabled = false });
                var target = Target(pluginId);
                if (Directory.Exists(target)) Directory.Delete(target, true);
                _dependencyStore.ReleaseDependencies(pluginId, deps);
                _stateStore.Uninstall(pluginId);
            }
            finally { _changing = false; }
        }
    }
    public void SetPackageEnabled(string pluginId, bool enabled)
    {
        Known(pluginId);
        lock (_gate)
        {
            var previous = _stateStore.GetState(pluginId);
            if (!previous.IsInstalled) throw new InvalidOperationException("插件尚未安装。");
            if (!enabled && PluginMarketplaceCatalog.Find(pluginId)!.IsCoreBuiltin)
                throw new InvalidOperationException("核心内置组件不可禁用。");
            _changing = true;
            try
            {
                var embedded = PluginMarketplaceCatalog.Find(pluginId)!.IsEmbedded;
                if (!embedded && enabled)
                {
                    SetBackendEnabledFor(pluginId, true);
                    ActivatePluginLocked(pluginId);
                }
                else if (!embedded)
                {
                    DeactivatePluginLocked(pluginId);
                    SetBackendEnabledFor(pluginId, false);
                }
                _stateStore.SetEnabled(pluginId, enabled);
            }
            catch
            {
                if (!previous.IsEnabled) DeactivatePluginLocked(pluginId);
                throw;
            }
            finally { _changing = false; }
        }
    }
    public void InitializeInstalledPlugins(string? projectPath = null)
    {
        lock (_gate)
        {
            // 默认安装的插件随包分发：首次启动（或数据目录被清空）时从程序目录的 plugins/<id> 播种，
            // 之后按正常安装状态加载。缺载荷只记录错误，不影响其它插件。
            // 注意不能先判断 IsActive：状态库把"没有载荷"视为未安装，
            // 那样默认插件永远等不到播种。这里只按"默认安装且用户没做过显式选择"处理。
            foreach (var def in PluginMarketplaceCatalog.All.Where(d => d.DefaultInstalled && !d.IsCoreBuiltin && !_stateStore.HasExplicitState(d.Id)))
            {
                if (HasPhysicalPayload(def.Id)) continue;
                try
                {
                    InstallPackage(def.Id);
                    if (def.DefaultEnabled) _stateStore.SetEnabled(def.Id, true);
                }
                catch (Exception error) { _loadErrors[def.Id] = error.Message; }
            }

            foreach (var def in PluginMarketplaceCatalog.All.Where(d => _stateStore.IsActive(d.Id) && HasPhysicalPayload(d.Id)))
                try { ActivatePluginLocked(def.Id, projectPath); _loadErrors.Remove(def.Id); }
                catch (Exception e) { _loadErrors[def.Id] = e.Message; }
        }
    }
    private void SyncActivePlugins()
    {
        lock (_gate)
        {
            if (_changing) return;
            foreach (var def in PluginMarketplaceCatalog.All)
            {
                if (_stateStore.IsActive(def.Id) && !_activePlugins.ContainsKey(def.Id) && HasPhysicalPayload(def.Id))
                {
                    try { ActivatePluginLocked(def.Id); _loadErrors.Remove(def.Id); }
                    catch (Exception e) { _loadErrors[def.Id] = e.Message; }
                }
                else if (!_stateStore.IsActive(def.Id) && _activePlugins.ContainsKey(def.Id)) DeactivatePluginLocked(def.Id);
            }
        }
    }
    /// <summary>把插件 id 映射到后端标识，并同步 BackendPluginLoader 的启用状态。</summary>
    private static void SetBackendEnabledFor(string pluginId, bool enabled)
    {
        if (!pluginId.StartsWith("backend.", StringComparison.OrdinalIgnoreCase)) return;
        if (!Backend.EngineIdMap.TryParse(pluginId["backend.".Length..], out var engine)) return;
        var backend = BackendCapabilities.FromEngineId(engine);
        if (backend is null) return;
        BackendPluginLoader.SetBackendEnabled(backend.Value, enabled);
    }

    private string? ResolveSourceDirectory(string id, string? source)
    {
        if (source is not null)
        {
            PluginPathGuard.RejectLinks(source);
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"分发目录不存在：{source}");
            return Path.GetFullPath(source);
        }
        return new[] { Path.Combine(AppContext.BaseDirectory, "plugins-dist", id), Path.Combine(AppContext.BaseDirectory, "plugins", id),
            Path.Combine(_layout.Root, "distribution", id) }.FirstOrDefault(Directory.Exists);
    }
    private static void CopyDirectory(string source, string target)
    {
        PluginPathGuard.RejectLinks(source);
        PluginPathGuard.RejectLinks(target);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            PluginPathGuard.RejectLinks(file);
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        }
        foreach (var dir in Directory.GetDirectories(source)) CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }
    private void ActivatePluginLocked(string id, string? projectPath = null)
    {
        if (_activePlugins.ContainsKey(id)) return;
        var (package, loadContext) = ResolvePackage(id, Target(id));
        ActivatePrepared(id, package, Target(id), loadContext, projectPath);
    }
    private void ActivatePrepared(string id, IPluginPackage package, string directory, AssemblyLoadContext? loadContext, string? projectPath = null)
    {
        if (loadContext is PackageLoadContext packageContext) packageContext.Directory = directory;
        var context = new PluginPackageContext(id, directory, projectPath, _serviceResolver, _layout.Root);
        IPluginToolHandler? handler = null;
        PluginPageContribution? page = null;
        try
        {
            package.InitializeAsync(context).GetAwaiter().GetResult();
            handler = package.CreateToolHandler(context);
            if (handler is not null) _toolRegistry.Register(handler);
            var rawPage = package.CreatePageContribution(context);
            page = PageDecorator is { } decorator ? decorator(id, package, rawPage) : rawPage;
            var content = package.CreateContentContribution(context);
            if (content is not null) PluginContentRegistry.Register(id, content);
            _activePlugins.Add(id, (package, handler, context));
            if (loadContext is not null) _loadContexts[id] = loadContext;
            if (page is not null) _pageContributions[id] = page;
        }
        catch (Exception error)
        {
            if (handler is not null) _toolRegistry.Unregister(handler);
            try { package.ShutdownAsync().GetAwaiter().GetResult(); }
            catch (Exception cleanup) { throw new AggregateException("插件激活和副作用恢复均失败。", error, cleanup); }
            finally { loadContext?.Unload(); }
            throw;
        }
        if (page is not null) { PagesChanged?.Invoke(); PageContributionsChanged?.Invoke(); }
    }
    private void DeactivatePluginLocked(string id)
    {
        // 先摘掉渲染贡献：即使后续 shutdown 失败，也不能让已停用插件的控件继续被创建。
        PluginContentRegistry.Unregister(id);
        if (!_activePlugins.TryGetValue(id, out var entry)) return;
        if (entry.Handler is not null) _toolRegistry.Unregister(entry.Handler);
        // Preserve the entry when shutdown fails so a later stop can retry.
        entry.Package.ShutdownAsync().GetAwaiter().GetResult();
        _activePlugins.Remove(id);
        var hadPage = _pageContributions.Remove(id);
        if (_loadContexts.Remove(id, out var context)) context.Unload();
        if (hadPage) { PagesChanged?.Invoke(); PageContributionsChanged?.Invoke(); }
    }
    private (IPluginPackage Package, AssemblyLoadContext? Context) ResolvePackage(string id, string directory)
    {
        var manifestPath = PluginPathGuard.Within(directory, "manifest.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = manifest.RootElement;
        if (!root.TryGetProperty("id", out var identity) || !string.Equals(identity.GetString(), id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("插件清单ID与请求不一致。");
        if (_registeredFactories.TryGetValue(id, out var factory))
        {
            var fixture = factory();
            if (fixture.Id != id) throw new InvalidDataException("插件工厂ID与请求不一致。");
            return (fixture, null);
        }
        var name = PluginPathGuard.Segment(root.GetProperty("entryAssembly").GetString() ?? "");
        var path = PluginPathGuard.Within(directory, name);
        if (!File.Exists(path)) throw new FileNotFoundException("插件入口程序集不存在。", path);
        var context = new PackageLoadContext(directory, _dependencyStore.SharedPoolDirectory);
        try
        {
            using var stream = File.OpenRead(path);
            var asm = context.LoadFromStream(stream);
            var type = root.TryGetProperty("packageType", out var typeName)
                ? asm.GetType(typeName.GetString() ?? "", true)
                : asm.GetTypes().SingleOrDefault(t => typeof(IPluginPackage).IsAssignableFrom(t) && !t.IsAbstract);
            if (type is null || !typeof(IPluginPackage).IsAssignableFrom(type) || Activator.CreateInstance(type) is not IPluginPackage package ||
                !string.Equals(package.Id, id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("插件入口不符合宿主契约或身份。");
            if (!root.TryGetProperty("version", out var version) || package.Version != version.GetString())
                throw new InvalidDataException("插件版本与清单不一致。");
            context.Dependencies = package.Dependencies;
            return (package, context);
        }
        catch { context.Unload(); throw; }
    }
    private sealed class PackageLoadContext(string directory, string pool) : AssemblyLoadContext(isCollectible: true)
    {
        public string Directory { get; set; } = directory;
        public IReadOnlyList<PluginDependencyDeclaration> Dependencies { get; set; } = Array.Empty<PluginDependencyDeclaration>();
        protected override Assembly? Load(AssemblyName name)
        {
            var host = Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);
            if (host is not null) return host;
            var local = PluginPathGuard.Within(Directory, PluginPathGuard.Segment(name.Name + ".dll"));
            var shared = Dependencies.FirstOrDefault(d => (d.TargetFileName ?? d.PackageId + ".dll") == name.Name + ".dll");
            if (!File.Exists(local) && shared is not null)
                local = PluginPathGuard.Within(pool, shared.PackageId, shared.Version, shared.TargetFileName ?? shared.PackageId + ".dll");
            if (File.Exists(local))
            {
                using var stream = File.OpenRead(local);
                return LoadFromStream(stream);
            }
            // 宿主程序集回退：在发布单目录或开发目录下查找宿主提供的共享程序集（如 Avalonia）
            var fileName = PluginPathGuard.Segment(name.Name + ".dll");
            var candidateApp = Path.Combine(AppContext.BaseDirectory, fileName);
            if (File.Exists(candidateApp))
            {
                using var stream = File.OpenRead(candidateApp);
                return LoadFromStream(stream);
            }
            var devAppRelease = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Fufucode.App", "bin", "Release", "net10.0", fileName);
            if (File.Exists(devAppRelease))
            {
                using var stream = File.OpenRead(Path.GetFullPath(devAppRelease));
                return LoadFromStream(stream);
            }
            var devAppDebug = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Fufucode.App", "bin", "Debug", "net10.0", fileName);
            if (File.Exists(devAppDebug))
            {
                using var stream = File.OpenRead(Path.GetFullPath(devAppDebug));
                return LoadFromStream(stream);
            }
            return null;
        }
    }
    public ValueTask DisposeAsync()
    {
        PluginStateNotifier.Changed -= OnNotifiedStateChanged;
        _stateStore.Changed -= SyncActivePlugins;
        lock (_gate)
            foreach (var id in _activePlugins.Keys.ToArray()) DeactivatePluginLocked(id);
        return ValueTask.CompletedTask;
    }
}
