using System.Security.Cryptography;
using System.Text.Json;
using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

var failures = 0;
var tests = new (string, Func<Task>)[]
{
    ("failed state persistence is not reported as success", StateFailure),
    ("registration conflict does not leave partial tools", RegistryConflict),
    ("unregister cannot revoke another handler's tool", RegistryOwner),
    ("dependency path traversal is rejected", DependencyTraversal),
    ("corrupt shared dependency retains other consumers", DependencyCorruption),
    ("two files of one dependency version coexist", DependencyFiles),
    ("failed install does not become installed", InvalidInstall),
    ("manifest entry assembly is loaded for service plugins", ServiceManifest),
    ("failed initialization preserves previous installation", FailedInitialization),
    ("install copies nested resources", NestedResources),
    ("update keeps a disabled plugin disabled", DisabledUpdate),
    ("failed update retains old payload", FailedUpdate),
};
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failures++; Console.WriteLine($"FAIL {name}: {e.Message}"); }
}
Console.WriteLine($"Review regressions: {tests.Length - failures} passed, {failures} failed");
return failures == 0 ? 0 : 1;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static void Reject(Action action)
{
    try { action(); } catch { return; }
    throw new Exception("operation unexpectedly succeeded");
}
static Task StateFailure()
{
    using var f = new Fixture();
    Directory.CreateDirectory(Path.Combine(f.Root, "plugins.json"));
    Reject(() => f.Store.Install("agent.browser-use"));
    Check(!f.Store.IsActive("agent.browser-use"), "failed write changed in-memory state");
    return Task.CompletedTask;
}
static Task RegistryConflict()
{
    using var f = new Fixture();
    f.Store.Install("agent.browser-use");
    var owner = new Handler("owned");
    f.Registry.Register(owner);
    Reject(() => f.Registry.Register(new Handler("ghost", "owned")));
    Check(f.Registry.ActiveToolNames.SequenceEqual(new[] { "owned" }), "rejected registration left a ghost tool");
    f.Registry.Refresh();
    return Task.CompletedTask;
}
static Task RegistryOwner()
{
    using var f = new Fixture();
    f.Store.Install("agent.browser-use");
    f.Registry.Register(new Handler("owned"));
    Check(!f.Registry.Unregister(new Handler("owned")), "unregistered foreign handler claimed success");
    Check(f.Registry.ActiveToolNames.Contains("owned"), "original tool was removed");
    return Task.CompletedTask;
}
static Task DependencyTraversal()
{
    using var f = new Fixture();
    var source = f.Source("dist");
    File.WriteAllText(Path.Combine(source, "lib.dll"), "payload");
    var deps = new PluginDependencyStore(f.Layout.PluginsDirectory);
    Reject(() => deps.ResolveAndAcquire(new("../escaped", "1", null, "lib.dll"), "a", source));
    Check(!Directory.Exists(Path.Combine(f.Layout.PluginsDirectory, "escaped")), "wrote outside dependency pool");
    return Task.CompletedTask;
}
static Task DependencyCorruption()
{
    using var f = new Fixture();
    var source = f.Source("dist");
    File.WriteAllText(Path.Combine(source, "lib.dll"), "valid");
    var deps = new PluginDependencyStore(f.Layout.PluginsDirectory);
    var path = deps.ResolveAndAcquire(new("lib", "1", Sha(Path.Combine(source, "lib.dll")), "lib.dll"), "a", source);
    Reject(() => deps.ResolveAndAcquire(new("lib", "1", new string('0', 64), "lib.dll"), "b", source));
    Check(File.Exists(path) && File.ReadAllText(path) == "valid", "failed second install destroyed first consumer's dependency");
    return Task.CompletedTask;
}
static Task DependencyFiles()
{
    using var f = new Fixture();
    var source = f.Source("dist");
    File.WriteAllText(Path.Combine(source, "a.dll"), "a");
    File.WriteAllText(Path.Combine(source, "b.dll"), "b");
    var deps = new PluginDependencyStore(f.Layout.PluginsDirectory);
    var a = new PluginDependencyDeclaration("lib", "1", null, "a.dll");
    var b = new PluginDependencyDeclaration("lib", "1", null, "b.dll");
    var pa = deps.ResolveAndAcquire(a, "a", source);
    var pb = deps.ResolveAndAcquire(b, "b", source);
    Check(File.Exists(pa) && File.Exists(pb), "second file deleted the first");
    deps.ReleaseDependencies("a", new[] { a });
    Check(File.Exists(pb), "removing one file deleted another consumer's file");
    return Task.CompletedTask;
}
static async Task InvalidInstall()
{
    using var f = new Fixture();
    await using var pm = f.Manager();
    var source = f.Source("bad");
    File.WriteAllText(Path.Combine(source, "manifest.json"), """{"id":"service.speech-synthesis","version":"1.0.0","entryAssembly":"missing.dll"}""");
    Reject(() => pm.InstallPackage("service.speech-synthesis", source));
    Check(!f.Store.IsActive("service.speech-synthesis"), "missing DLL was marked installed");
}
static async Task ServiceManifest()
{
    using var f = new Fixture();
    await using var pm = f.Manager();
    var source = f.Source("speech");
    var assembly = typeof(Fufucode.Plugins.Speech.SpeechPluginPackage).Assembly;
    File.Copy(assembly.Location, Path.Combine(source, Path.GetFileName(assembly.Location)));
    File.WriteAllText(Path.Combine(source, "manifest.json"), JsonSerializer.Serialize(new
    {
        id = "service.speech-synthesis", version = "1.0.0",
        entryAssembly = Path.GetFileName(assembly.Location),
        packageType = typeof(Fufucode.Plugins.Speech.SpeechPluginPackage).FullName
    }));
    pm.InstallPackage("service.speech-synthesis", source);
    var active = (System.Collections.IDictionary)typeof(PluginPackageManager)
        .GetField("_activePlugins", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(pm)!;
    Check(active.Contains("service.speech-synthesis"), "market state succeeded but service was never loaded");
    // 宿主只认插件激活时登记的服务：未登记时朗读入口必须整条不可用，而不是留一个空实现。
    Check(PluginPackageManager.ActiveSpeechService is not null,
        "activated speech plugin registered its service for the host");
    pm.UninstallPackage("service.speech-synthesis");
    Check(PluginPackageManager.ActiveSpeechService is null,
        "uninstalled speech plugin withdrew its service from the host");
    Check(!pm.HasPhysicalPayload("service.speech-synthesis"), "payload remains after uninstall");
}
static async Task FailedInitialization()
{
    using var f = new Fixture();
    await using var pm = f.Manager();
    PluginPackageManager.RegisterPackageFactory("agent.browser-use", () => new Package { Fail = true });
    Reject(() => pm.InstallPackage("agent.browser-use"));
    Check(!f.Store.IsActive("agent.browser-use"), "initialization failure was marked installed");
    Check(!pm.HasPhysicalPayload("agent.browser-use"), "failed first install left payload");
}
static async Task NestedResources()
{
    using var f = new Fixture();
    await using var pm = f.Manager();
    PluginPackageManager.RegisterPackageFactory("agent.browser-use", () => new Package());
    var source = f.PackageSource("v1");
    Directory.CreateDirectory(Path.Combine(source, "assets"));
    File.WriteAllText(Path.Combine(source, "assets", "sample.txt"), "resource");
    pm.InstallPackage("agent.browser-use", source);
    Check(File.Exists(Path.Combine(f.Layout.PluginDirectory("agent.browser-use"), "assets", "sample.txt")), "nested asset missing");
}
static async Task DisabledUpdate()
{
    using var f = new Fixture();
    await using var pm = f.Manager();
    var initializations = 0;
    PluginPackageManager.RegisterPackageFactory("agent.browser-use", () => new Package { Started = () => initializations++ });
    pm.InstallPackage("agent.browser-use", f.PackageSource("v1"));
    pm.SetPackageEnabled("agent.browser-use", false);
    var before = initializations;
    pm.UpdatePackage("agent.browser-use", f.PackageSource("v2"));
    Check(initializations == before && !f.Store.IsActive("agent.browser-use"), "disabled update initialized services");
}
static async Task FailedUpdate()
{
    using var f = new Fixture();
    await using var pm = f.Manager();
    PluginPackageManager.RegisterPackageFactory("agent.browser-use", () => new Package());
    pm.InstallPackage("agent.browser-use", f.PackageSource("v1"));
    var calls = 0;
    PluginPackageManager.RegisterPackageFactory("agent.browser-use", () => new Package { Fail = ++calls == 1 });
    Reject(() => pm.UpdatePackage("agent.browser-use", f.PackageSource("v2")));
    Check(File.ReadAllText(Path.Combine(f.Layout.PluginDirectory("agent.browser-use"), "version.txt")) == "v1",
        "failed update did not restore old files");
}
static string Sha(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "fufucode-review-" + Guid.NewGuid().ToString("N"));
    public AppDataLayout Layout { get; }
    public PluginStateStore Store { get; }
    public PluginToolRegistry Registry { get; }
    public Fixture()
    {
        Layout = new(Root); Layout.EnsureCreated(); Store = new(Layout);
        Registry = new(new PluginCapabilityGateway(Store));
    }
    public PluginPackageManager Manager() => new(Layout, Registry, Store);
    public string Source(string name) { var path = Path.Combine(Root, name); Directory.CreateDirectory(path); return path; }
    public string PackageSource(string name)
    {
        var path = Source(name);
        File.WriteAllText(Path.Combine(path, "manifest.json"), """{"id":"agent.browser-use","version":"1.0.0","entryAssembly":"fixture.dll"}""");
        File.WriteAllText(Path.Combine(path, "version.txt"), name);
        return path;
    }
    public void Dispose() { try { Directory.Delete(Root, true); } catch { /* Preserve diagnostic files if locked. */ } }
}
sealed class Handler(params string[] names) : IPluginToolHandler
{
    public IReadOnlyList<PluginToolRegistration> Registrations => names.Select(n => new PluginToolRegistration(n, n, "{}", PluginCapability.BrowserControl)).ToArray();
    public Task<PureToolResult> ExecuteAsync(PureToolCall call, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
sealed class Package : IPluginPackage
{
    public string Id => "agent.browser-use";
    public string Version => "1.0.0";
    public bool Fail { get; init; }
    public Action? Started { get; init; }
    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => null;
    public Task InitializeAsync(PluginPackageContext context, CancellationToken cancellationToken = default)
    {
        if (Fail) throw new InvalidOperationException("fixture initialization failed");
        Started?.Invoke(); return Task.CompletedTask;
    }
}
