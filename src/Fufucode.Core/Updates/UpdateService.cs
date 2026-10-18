using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core.Updates;

/// <summary>
/// 发布通道更新：只服务 GitHub Release，不涉及本地构建。
///
/// 与 <see cref="BuildDeploymentService"/> 的分工：
/// - 更新判断"有没有值得升级的新版本"，比较版本号，可联网；
/// - 部署判断"运行的是不是这个构建"，比较构建身份，纯本地。
///
/// 仅当构建标记为发布渠道（channel=github）且写入了仓库坐标时启用；
/// 本地开发构建不会走这里。
/// </summary>
public sealed class UpdateService : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public UpdateService(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _ownsHttp = httpClient is null;
    }

    /// <summary>当前构建是否属于发布渠道（只有发布构建才检查更新）。</summary>
    public static bool IsReleaseBuild(BuildInfo? build) =>
        build is { IsReleaseChannel: true } &&
        !string.IsNullOrWhiteSpace(build.GitHubRepository);

    /// <summary>
    /// 读取 GitHub 最新 Release。资产匹配：feed.json 优先，否则匹配 *setup*.exe 并使用
    /// GitHub API 的 digest 作为 SHA-256；没有 digest 时如实留空，由下载步骤报告校验不可用。
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, string repository,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{repository}/releases/latest");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Fufucode", "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, null,
                    $"GitHub Release 查询失败：HTTP {(int)response.StatusCode}（仓库 {repository}）。");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;
            if (!root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String)
                return new UpdateCheckResult(UpdateCheckStatus.Failed, null, "GitHub Release 缺少 tag_name。");

            var version = NormalizeTag(tag.GetString());
            if (version is null)
                return new UpdateCheckResult(UpdateCheckStatus.Failed, null,
                    $"无法从 Release tag '{tag.GetString()}' 解析版本号。");

            if (UpdateVersion.Compare(version, currentVersion) <= 0)
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, null,
                    $"GitHub 最新 Release 为 v{version}，当前已是最新版本。");

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return new UpdateCheckResult(UpdateCheckStatus.Failed, null, "GitHub Release 不含资产列表。");

            var feedAsset = assets.EnumerateArray().FirstOrDefault(a =>
                string.Equals(GetString(a, "name"), "feed.json", StringComparison.OrdinalIgnoreCase));
            if (feedAsset.ValueKind == JsonValueKind.Object &&
                GetString(feedAsset, "browser_download_url") is { Length: > 0 } feedUrl)
            {
                try
                {
                    var json = await _http.GetStringAsync(feedUrl, cancellationToken).ConfigureAwait(false);
                    var manifest = JsonSerializer.Deserialize<AppUpdateManifest>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (manifest is not null && UpdateVersion.Compare(manifest.Version, currentVersion) > 0)
                    {
                        return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, manifest,
                            $"GitHub 发现新版本 v{manifest.Version}（{repository}）。");
                    }
                }
                catch (Exception)
                {
                    // feed.json 不可用时退回资产 digest 路径。
                }
            }

            var setupAsset = assets.EnumerateArray().FirstOrDefault(a =>
            {
                var name = GetString(a, "name") ?? string.Empty;
                return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                       name.Contains("setup", StringComparison.OrdinalIgnoreCase);
            });
            if (setupAsset.ValueKind != JsonValueKind.Object)
                return new UpdateCheckResult(UpdateCheckStatus.Failed, null,
                    $"GitHub Release v{version} 未包含 setup.exe 资产。");

            var packageUrl = GetString(setupAsset, "browser_download_url");
            var digest = GetString(setupAsset, "digest");
            var size = setupAsset.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
            if (packageUrl is null)
                return new UpdateCheckResult(UpdateCheckStatus.Failed, null, "GitHub Release 资产缺少下载地址。");

            var sha256 = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? digest["sha256:".Length..]
                : string.Empty;

            var notes = GetString(root, "body") ?? $"GitHub Release {tag.GetString()}";
            var published = root.TryGetProperty("published_at", out var p) &&
                            DateTimeOffset.TryParse(p.GetString(), out var publishedAt)
                ? publishedAt
                : DateTimeOffset.UtcNow;

            return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable,
                new AppUpdateManifest(version, published, notes, packageUrl, sha256, size,
                    MinCompatibleVersion: "1.0.0", SignaturePolicy: "Unsigned"),
                $"GitHub 发现新版本 v{version}（{repository}）。");
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null,
                $"GitHub 更新检查失败：{ex.GetBaseException().Message}");
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var value = tag.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        return value.Length > 0 && char.IsAsciiDigit(value[0]) ? value : null;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
