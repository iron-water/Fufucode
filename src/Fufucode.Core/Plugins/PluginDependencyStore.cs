using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace Fufucode.Core.Plugins;

public sealed class SharedDependencyRecord
{
    public string PackageId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public List<string> ReferencedByPlugins { get; set; } = [];
}

public sealed class SharedDependenciesManifest
{
    public int Version { get; set; } = 1;
    public Dictionary<string, SharedDependencyRecord> Dependencies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>按包、版本和文件分别跟踪共享依赖；同进程的多个市场实例共用写锁。</summary>
public sealed class PluginDependencyStore
{
    /// <summary>
    /// 同进程多实例共用的写锁表。正常情况下条目数等于不同插件目录的数量（很少），
    /// 但测试/频繁切换根目录时会累积。设上限，超出时只淘汰当前无人持有的锁；
    /// 已被淘汰目录上的单实例操作仍由它持有的锁对象串行化（只影响跨实例互斥）。
    /// </summary>
    private const int MaxGates = 256;
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _sharedPoolDir;
    private readonly string _manifestPath;
    private readonly object _gate;

    public PluginDependencyStore(string pluginsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsDirectory);
        _sharedPoolDir = PluginPathGuard.Within(pluginsDirectory, ".shared-packages");
        _manifestPath = Path.Combine(_sharedPoolDir, "dependencies.json");
        _gate = GetGate(_sharedPoolDir);
    }
    public string SharedPoolDirectory => _sharedPoolDir;

    private static object GetGate(string key)
    {
        var gate = Gates.GetOrAdd(key, _ => new object());
        if (Gates.Count > MaxGates) TrimGates(key);
        return gate;
    }

    private static void TrimGates(string keepKey)
    {
        foreach (var item in Gates)
        {
            if (Gates.Count <= MaxGates) break;
            if (string.Equals(item.Key, keepKey, StringComparison.OrdinalIgnoreCase)) continue;
            // 仅淘汰未被持有的锁；TryRemove 以值匹配，避免误删并发替换的新锁。
            if (Monitor.TryEnter(item.Value))
            {
                try { Gates.TryRemove(new KeyValuePair<string, object>(item.Key, item.Value)); }
                finally { Monitor.Exit(item.Value); }
            }
        }
    }

    private static string FileName(PluginDependencyDeclaration dep)
    {
        PluginPathGuard.Segment(dep.PackageId);
        PluginPathGuard.Segment(dep.Version);
        if (dep.Sha256 is not null && (dep.Sha256.Length != 64 || !dep.Sha256.All(Uri.IsHexDigit)))
            throw new InvalidDataException("依赖 SHA256 必须是64位十六进制值。");
        return PluginPathGuard.Segment(dep.TargetFileName ?? $"{dep.PackageId}.dll");
    }
    private static string Key(string id, string version, string file) => $"{id}@{version}/{file}";

    public string ResolveAndAcquire(PluginDependencyDeclaration dep, string consumerPluginId, string? distributionDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(dep);
        PluginPathGuard.Segment(consumerPluginId);
        var file = FileName(dep);
        lock (_gate)
        {
            var target = PluginPathGuard.Within(_sharedPoolDir, dep.PackageId, dep.Version, file);
            var manifest = LoadManifestLocked();
            var key = Key(dep.PackageId, dep.Version, file);
            if (File.Exists(target))
            {
                // An incompatible request must not delete an existing consumer's working file.
                VerifyHash(target, dep.Sha256);
            }
            else
            {
                var source = FindDependencyInDistribution(dep, file, distributionDirectory)
                    ?? throw new FileNotFoundException($"缺失插件依赖：'{dep.PackageId}' {dep.Version}/{file}。");
                VerifyHash(source, dep.Sha256);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var staged = target + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.Copy(source, staged);
                    VerifyHash(staged, dep.Sha256);
                    File.Move(staged, target);
                }
                finally { if (File.Exists(staged)) File.Delete(staged); }
            }
            if (!manifest.Dependencies.TryGetValue(key, out var record))
                record = new SharedDependencyRecord
                {
                    PackageId = dep.PackageId, Version = dep.Version, FileName = file,
                    FileSizeBytes = new FileInfo(target).Length, Sha256 = dep.Sha256
                };
            if (!record.ReferencedByPlugins.Contains(consumerPluginId, StringComparer.OrdinalIgnoreCase))
                record.ReferencedByPlugins.Add(consumerPluginId);
            manifest.Dependencies[key] = record;
            SaveManifestLocked(manifest);
            return target;
        }
    }

    public void ReleaseDependencies(string consumerPluginId, IEnumerable<PluginDependencyDeclaration> deps)
    {
        PluginPathGuard.Segment(consumerPluginId);
        ArgumentNullException.ThrowIfNull(deps);
        var entries = deps.Select(d => (Dependency: d, File: FileName(d))).ToArray();
        lock (_gate)
        {
            var manifest = LoadManifestLocked();
            foreach (var (dep, file) in entries)
            {
                var key = Key(dep.PackageId, dep.Version, file);
                if (!manifest.Dependencies.TryGetValue(key, out var record)) continue;
                record.ReferencedByPlugins.RemoveAll(id => string.Equals(id, consumerPluginId, StringComparison.OrdinalIgnoreCase));
                if (record.ReferencedByPlugins.Count != 0) continue;
                var target = PluginPathGuard.Within(_sharedPoolDir, dep.PackageId, dep.Version, file);
                if (File.Exists(target)) File.Delete(target);
                manifest.Dependencies.Remove(key);
            }
            if (Directory.Exists(_sharedPoolDir)) SaveManifestLocked(manifest);
        }
    }

    public bool IsReferencedByOthers(string packageId, string version, string currentPluginId)
    {
        PluginPathGuard.Segment(packageId); PluginPathGuard.Segment(version);
        lock (_gate)
            return LoadManifestLocked().Dependencies.Values.Any(r =>
                string.Equals(r.PackageId, packageId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase) &&
                r.ReferencedByPlugins.Any(id => !string.Equals(id, currentPluginId, StringComparison.OrdinalIgnoreCase)));
    }

    private string? FindDependencyInDistribution(PluginDependencyDeclaration dep, string file, string? source)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(source)) roots.Add(source);
        roots.Add(Path.Combine(AppContext.BaseDirectory, "plugins-dist"));
        roots.Add(AppContext.BaseDirectory);
        roots.Add(Path.Combine(Directory.GetCurrentDirectory(), "out", "plugins-dist"));
        foreach (var root in roots)
            foreach (var parts in new[] { new[] { ".shared-packages", dep.PackageId, dep.Version, file }, new[] { dep.PackageId, file }, new[] { file } })
            {
                var candidate = PluginPathGuard.Within(root, parts);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    private static void VerifyHash(string path, string? expected)
    {
        if (expected is null) return;
        using var stream = File.OpenRead(path);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"插件依赖 '{Path.GetFileName(path)}' 哈希校验失败，原有共享文件未被修改。");
    }

    private SharedDependenciesManifest LoadManifestLocked()
    {
        PluginPathGuard.RejectLinks(_manifestPath);
        if (!File.Exists(_manifestPath)) return new();
        var stored = JsonSerializer.Deserialize<SharedDependenciesManifest>(File.ReadAllText(_manifestPath))
            ?? throw new InvalidDataException("共享依赖清单为空。");
        var migrated = new SharedDependenciesManifest();
        foreach (var item in stored.Dependencies.Values)
        {
            var file = FileName(new(item.PackageId, item.Version, item.Sha256, item.FileName));
            var key = Key(item.PackageId, item.Version, file);
            if (migrated.Dependencies.TryGetValue(key, out var old))
                item.ReferencedByPlugins = item.ReferencedByPlugins.Concat(old.ReferencedByPlugins).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            migrated.Dependencies[key] = item;
        }
        return migrated;
    }

    private void SaveManifestLocked(SharedDependenciesManifest manifest)
    {
        PluginPathGuard.RejectLinks(_manifestPath);
        Directory.CreateDirectory(_sharedPoolDir);
        var temp = _manifestPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _manifestPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
