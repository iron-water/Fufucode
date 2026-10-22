using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Fufucode.Core.Plugins;

/// <summary>
/// 插件分发包安装：把 plugins-dist 目录里的 zip（或 GitHub Release 资产 URL）
/// 解压到 &lt;dataRoot&gt;/distribution/&lt;id&gt;，供 PluginPackageManager 的
/// InstallPackage 找到真实载荷。只做分发，不激活；激活仍走插件市场状态机。
/// </summary>
public sealed class PluginDistributionService
{
    private readonly AppDataLayout _layout;
    private readonly HttpClient _http;

    public PluginDistributionService(AppDataLayout layout, HttpClient? http = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public string Directory(string pluginId) => PluginPathGuard.Within(
        _layout.Root, "distribution", PluginPathGuard.Segment(pluginId));

    public bool IsInstalled(string pluginId) =>
        File.Exists(Path.Combine(Directory(pluginId), "manifest.json"));

    public async Task<PluginDistributionStatus> InstallAsync(
        string pluginId,
        string source,
        string? expectedSha256 = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (PluginMarketplaceCatalog.Find(pluginId) is null)
            throw new ArgumentException($"未知插件 '{pluginId}'。", nameof(pluginId));

        var temporary = Path.Combine(_layout.Root, "distribution",
            $".{pluginId}-{Guid.NewGuid():N}.download");
        var staging = Path.Combine(_layout.Root, "distribution",
            $".{pluginId}-{Guid.NewGuid():N}.staging");
        System.IO.Directory.CreateDirectory(Path.Combine(_layout.Root, "distribution"));
        try
        {
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                await using var remote = await _http.GetStreamAsync(uri, cancellationToken).ConfigureAwait(false);
                await using var file = File.Create(temporary);
                await remote.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var local = Path.GetFullPath(source);
                if (!File.Exists(local)) throw new FileNotFoundException("插件分发包不存在。", local);
                File.Copy(local, temporary, overwrite: true);
            }

            if (expectedSha256 is not null)
            {
                var actual = Convert.ToHexString(SHA256.HashData(
                    await File.ReadAllBytesAsync(temporary, cancellationToken).ConfigureAwait(false)))
                    .ToLowerInvariant();
                if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"插件包 SHA-256 不匹配：期望 {expectedSha256}，实际 {actual}。");
            }

            ExtractSafely(temporary, staging);
            var manifestPath = Path.Combine(staging, "manifest.json");
            if (!File.Exists(manifestPath))
                throw new InvalidDataException("插件包缺少 manifest.json。");
            using (var document = JsonDocument.Parse(File.ReadAllText(manifestPath)))
            {
                if (!document.RootElement.TryGetProperty("id", out var id) ||
                    !string.Equals(id.GetString(), pluginId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("插件包 manifest.json 的 id 与请求不一致。");
            }

            var target = Directory(pluginId);
            var backup = target + ".old-" + Guid.NewGuid().ToString("N");
            var movedOld = false;
            try
            {
                if (System.IO.Directory.Exists(target))
                {
                    System.IO.Directory.Move(target, backup);
                    movedOld = true;
                }
                System.IO.Directory.Move(staging, target);
            }
            catch
            {
                // 新载荷移动失败时恢复旧安装，避免留下半删除的插件目录。
                if (System.IO.Directory.Exists(target)) System.IO.Directory.Delete(target, true);
                if (movedOld && System.IO.Directory.Exists(backup)) System.IO.Directory.Move(backup, target);
                throw;
            }
            if (movedOld) System.IO.Directory.Delete(backup, true);
            return new PluginDistributionStatus(pluginId, true, target,
                System.IO.Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, true);
        }
    }

    public void Remove(string pluginId)
    {
        var target = Directory(pluginId);
        if (System.IO.Directory.Exists(target)) System.IO.Directory.Delete(target, true);
    }

    private static void ExtractSafely(string archivePath, string destination)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        System.IO.Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException($"插件包包含不安全路径：{entry.FullName}");
            if (entry.Name.Length == 0)
            {
                System.IO.Directory.CreateDirectory(target);
                continue;
            }
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }
}

public sealed record PluginDistributionStatus(string PluginId, bool Installed, string Directory, int FileCount);
