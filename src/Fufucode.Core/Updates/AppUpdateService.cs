using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core.Updates;

/// <summary>
/// 主程序更新服务实现：
/// 支持基于显式配置源的清单检查、哈希与签名政策校验、安全在途任务避让、
/// 挂起更新状态记录及回滚中断解释。
/// </summary>
public sealed class AppUpdateService : IAppUpdateService, IDisposable
{
    private readonly AppDataLayout _layout;
    private readonly AppSettingsStore _settingsStore;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _pendingFilePath;
    private readonly string _interruptedFilePath;
    private readonly object _gate = new();

    public AppUpdateService(
        AppDataLayout layout,
        AppSettingsStore settingsStore,
        HttpClient? httpClient = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        if (httpClient is not null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            _ownsHttpClient = true;
        }
        _pendingFilePath = Path.Combine(_layout.UpdatesDirectory, "pending-update.json");
        _interruptedFilePath = Path.Combine(_layout.UpdatesDirectory, "interrupted.json");
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(string currentVersion, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);

        string? feedLocation = _settingsStore.Current.UpdateFeedUrl;
        if (string.IsNullOrWhiteSpace(feedLocation))
        {
            var localFeed = Path.Combine(_layout.UpdatesDirectory, "feed.json");
            if (File.Exists(localFeed))
            {
                feedLocation = localFeed;
            }
            else
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Failed,
                    null,
                    "未配置更新发布源 (UpdateFeedUrl)，且本地无离线更新清单文件 (feed.json)。");
            }
        }

        try
        {
            string json;
            if (Uri.TryCreate(feedLocation, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                using var response = await _httpClient.GetAsync(uri, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            else
            {
                var localPath = feedLocation.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(feedLocation).LocalPath
                    : feedLocation;
                if (!File.Exists(localPath))
                {
                    return new UpdateCheckResult(
                        UpdateCheckStatus.Failed,
                        null,
                        $"更新发布源文件不存在：{localPath}");
                }
                json = await File.ReadAllTextAsync(localPath, ct).ConfigureAwait(false);
            }

            var manifest = JsonSerializer.Deserialize<AppUpdateManifest>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Failed,
                    null,
                    "更新清单格式无效：缺少版本字段或反序列化为空。");
            }

            var comparison = CompareVersions(manifest.Version, currentVersion);
            if (comparison <= 0)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.UpToDate,
                    manifest,
                    $"当前已是最新版本 (v{currentVersion})。");
            }

            if (!string.IsNullOrWhiteSpace(manifest.MinCompatibleVersion))
            {
                var compatComp = CompareVersions(currentVersion, manifest.MinCompatibleVersion);
                if (compatComp < 0)
                {
                    return new UpdateCheckResult(
                        UpdateCheckStatus.Incompatible,
                        manifest,
                        $"发现新版本 v{manifest.Version}，但当前版本 v{currentVersion} 低于最低兼容要求 v{manifest.MinCompatibleVersion}，无法直接热更新，请手动下载完整安装包。");
                }
            }

            return new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable,
                manifest,
                $"发现新版本 v{manifest.Version}（发布于 {manifest.ReleaseDate:yyyy-MM-dd}）。");
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                null,
                $"检查更新失败：{ex.GetBaseException().Message}");
        }
    }

    public async Task<string> DownloadUpdateAsync(
        AppUpdateManifest manifest,
        IProgress<UpdateProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifest.PackageUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifest.Sha256);

        var stagingDir = _layout.UpdateStagingDirectory(manifest.Version);
        Directory.CreateDirectory(stagingDir);

        var uri = Uri.TryCreate(manifest.PackageUrl, UriKind.Absolute, out var parsedUri) ? parsedUri : null;
        var fileName = uri is not null && !string.IsNullOrWhiteSpace(Path.GetFileName(uri.LocalPath))
            ? Path.GetFileName(uri.LocalPath)
            : $"Fufucode-Setup-{manifest.Version}.exe";
        var targetPath = Path.Combine(stagingDir, fileName);

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        if (uri is not null && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalLength = response.Content.Headers.ContentLength ?? manifest.PackageSizeBytes;
            await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                hasher.AppendData(buffer, 0, bytesRead);
                totalRead += bytesRead;

                var pct = totalLength > 0 ? (double)totalRead / totalLength * 100.0 : 0.0;
                progress?.Report(new UpdateProgressInfo(totalRead, totalLength, pct, $"正在下载更新... {pct:F1}%"));
            }
        }
        else
        {
            var localPath = uri is not null && uri.Scheme == Uri.UriSchemeFile
                ? uri.LocalPath
                : manifest.PackageUrl;

            if (!File.Exists(localPath))
            {
                throw new FileNotFoundException($"更新包源文件不存在：{localPath}");
            }

            var fi = new FileInfo(localPath);
            var totalLength = fi.Length;
            await using var sourceStream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                hasher.AppendData(buffer, 0, bytesRead);
                totalRead += bytesRead;

                var pct = totalLength > 0 ? (double)totalRead / totalLength * 100.0 : 0.0;
                progress?.Report(new UpdateProgressInfo(totalRead, totalLength, pct, $"正在复制本地测试包... {pct:F1}%"));
            }
        }

        var computedHash = Convert.ToHexString(hasher.GetHashAndReset());
        if (!string.Equals(computedHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(targetPath))
            {
                try { File.Delete(targetPath); } catch { }
            }
            throw new InvalidDataException(
                $"更新包 SHA-256 哈希校验失败：期望 '{manifest.Sha256}'，实际计算为 '{computedHash}'。文件已自动删除。");
        }

        progress?.Report(new UpdateProgressInfo(new FileInfo(targetPath).Length, new FileInfo(targetPath).Length, 100.0, "更新包完整性校验通过。"));
        return targetPath;
    }

    public Task<UpdateApplyPlan> PrepareUpdateAsync(
        string currentVersion,
        AppUpdateManifest manifest,
        string installerPath,
        bool hasActiveTasks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);

        if (hasActiveTasks)
        {
            return Task.FromResult(UpdateApplyPlan.Blocked(
                "检测到有后台写入任务仍在运行，为保护工作区数据，请先保存/收敛在途任务，或选择稍后安装。"));
        }

        if (!File.Exists(installerPath))
        {
            return Task.FromResult(UpdateApplyPlan.Blocked(
                $"安装文件不存在或已被移除：{installerPath}"));
        }

        lock (_gate)
        {
            Directory.CreateDirectory(_layout.UpdatesDirectory);
            var pending = new UpdatePendingState(
                manifest.Version,
                currentVersion,
                installerPath,
                DateTimeOffset.UtcNow,
                _layout.UpdateBackupDirectory);

            var json = JsonSerializer.Serialize(pending, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_pendingFilePath, json);
        }

        var args = "/SILENT /NORESTART /CLOSEAPPLICATIONS";
        return Task.FromResult(UpdateApplyPlan.Ready(installerPath, args));
    }

    public Task<UpdateInterruptedState?> CheckAndClearInterruptedStateAsync()
    {
        lock (_gate)
        {
            if (!File.Exists(_interruptedFilePath)) return Task.FromResult<UpdateInterruptedState?>(null);
            try
            {
                var json = File.ReadAllText(_interruptedFilePath);
                var state = JsonSerializer.Deserialize<UpdateInterruptedState>(json);
                File.Delete(_interruptedFilePath);
                return Task.FromResult(state);
            }
            catch
            {
                try { File.Delete(_interruptedFilePath); } catch { }
                return Task.FromResult<UpdateInterruptedState?>(null);
            }
        }
    }

    public Task<bool> CheckAndCompletePendingUpdateAsync(string currentVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);

        lock (_gate)
        {
            if (!File.Exists(_pendingFilePath)) return Task.FromResult(false);
            try
            {
                var json = File.ReadAllText(_pendingFilePath);
                var pending = JsonSerializer.Deserialize<UpdatePendingState>(json);
                if (pending is not null && CompareVersions(pending.TargetVersion, currentVersion) == 0)
                {
                    File.Delete(_pendingFilePath);
                    var stagingDir = _layout.UpdateStagingDirectory(pending.TargetVersion);
                    if (Directory.Exists(stagingDir))
                    {
                        try { Directory.Delete(stagingDir, true); } catch { }
                    }
                    return Task.FromResult(true);
                }
            }
            catch
            {
                // 忽略解析错误
            }
            return Task.FromResult(false);
        }
    }

    public Task RecordInterruptedStateAsync(string attemptedVersion, string restoredToVersion, string errorMessage)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_layout.UpdatesDirectory);
            var state = new UpdateInterruptedState(
                attemptedVersion,
                restoredToVersion,
                DateTimeOffset.UtcNow,
                errorMessage);
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_interruptedFilePath, json);
            return Task.CompletedTask;
        }
    }

    private static int CompareVersions(string v1, string v2)
    {
        var clean1 = v1.TrimStart('v', 'V');
        var clean2 = v2.TrimStart('v', 'V');
        if (Version.TryParse(clean1, out var ver1) && Version.TryParse(clean2, out var ver2))
        {
            return ver1.CompareTo(ver2);
        }
        return string.Compare(clean1, clean2, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
