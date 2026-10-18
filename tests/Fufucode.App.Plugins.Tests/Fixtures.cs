using System.Text;
using System.Text.Json;
using Fufucode.App.Features.Plugins;

namespace Fufucode.App.Plugins.Tests;

/// <summary>
/// 测试用宿主：注入真实宿主身份与可控的加载快照/应用结果。
/// 它不是产品宿主；只用于验证模块的接线、状态来源与失败语义，不冒充真实 Pi 加载。
/// </summary>
internal sealed class FakeHost : ILocalPluginHost
{
    public HostIdentity Identity { get; set; } = HostIdentity.Pi("1.1.0");

    public bool SnapshotSuccess { get; set; } = true;
    public string? SnapshotError { get; set; }
    public List<string> LoadedPaths { get; } = [];
    public Func<string, HostApplyResult>? OnApply { get; set; }
    /// <summary>按版本定制的异步应用回调（用于跨 await 竞争反例）。</summary>
    public Func<string, string, Task<HostApplyResult>>? OnApplyAsync { get; set; }
    public List<(string Entry, string PluginId, string Version)> Applies { get; } = [];
    public int SnapshotCalls { get; private set; }

    public Task<HostPluginSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        SnapshotCalls++;
        return Task.FromResult(SnapshotSuccess
            ? new HostPluginSnapshot(true, Identity, LoadedPaths.ToArray())
            : HostPluginSnapshot.Unavailable(SnapshotError ?? "宿主快照不可用（测试夹具）"));
    }

    public Task<HostApplyResult> ApplyAsync(string candidateEntryPath, string pluginId, string version,
        CancellationToken cancellationToken = default)
    {
        Applies.Add((candidateEntryPath, pluginId, version));
        if (OnApplyAsync is not null) return OnApplyAsync(candidateEntryPath, version);
        var result = OnApply?.Invoke(candidateEntryPath) ?? new HostApplyResult(true);
        return Task.FromResult(result);
    }
}

internal sealed class FakePicker : ILocalPluginPicker
{
    public Func<string?>? OnDirectory { get; set; }
    public List<string> Asked { get; } = [];

    public Task<string?> PickPluginDirectoryAsync(CancellationToken cancellationToken = default)
    {
        Asked.Add("dir");
        return Task.FromResult(OnDirectory?.Invoke());
    }

    public Task<string?> PickEntryFileAsync(string rootDirectory, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}

/// <summary>本地插件源目录构造：真实临时文件，不模拟文件系统。</summary>
internal static class PluginFixture
{
    public static string Create(string root, string name, string id, string displayName, string version,
        string backend = "pi", string entry = "index.js", string? entryBody = null,
        string[]? supportedHostVersions = null, bool writeDescriptor = true, object? descriptorOverride = null)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        if (entry.Length > 0)
        {
            File.WriteAllText(Path.Combine(dir, entry), entryBody ?? $"// {id} {version}\n",
                new UTF8Encoding(false));
        }

        if (writeDescriptor)
        {
            File.WriteAllText(Path.Combine(dir, PluginDescriptorFile.FileName),
                descriptorOverride is null
                    ? JsonSerializer.Serialize(new
                    {
                        id,
                        displayName,
                        version,
                        backend,
                        entryRelativePath = entry,
                        supportedHostVersions = supportedHostVersions ?? ["1.1.0"],
                    })
                    : JsonSerializer.Serialize(descriptorOverride),
                new UTF8Encoding(false));
        }

        return dir;
    }

    /// <summary>只改正文不改版本号：用于"同名版本内容不一致必须拒绝"的反例。</summary>
    public static void ChangeBody(string dir, string entry, string body) =>
        File.WriteAllText(Path.Combine(dir, entry), body, new UTF8Encoding(false));
}
