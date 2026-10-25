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
    /// 读取 GitHub Release。资产匹配：feed.json 优先，否则匹配 *setup*.exe 并使用
    /// GitHub API 的 digest 作为 SHA-256；没有 digest 时如实留空，由下载步骤报告校验不可用。
    /// </summary>
    /// <param name="includePreReleases">
    /// true 时在全部 Release 中按版本号取最高者（含预发布），服务于实验性测试渠道；
    /// false（默认）等价于旧的 <c>/releases/latest</c>，只看正式版本。
    /// 两个渠道同仓发布时，测试构建必须能读到测试版本，而不能被"最新正式版"盖住。
    /// </param>
    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, string repository,
        bool includePreReleases = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);

        try
        {
            var release = await ResolveReleaseAsync(repository, includePreReleases, cancellationToken)
                .ConfigureAwait(false);
            if (release.Error is { Length: > 0 } failure)
                return new UpdateCheckResult(UpdateCheckStatus.Failed, null, failure);
            var version = release.Version!;
            var assets = release.Assets!;

            if (UpdateVersion.Compare(version, currentVersion) <= 0)
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, null,
                    $"GitHub 最新 Release 为 v{version}，当前已是最新版本。");

            var feedAsset = assets.FirstOrDefault(a =>
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

            var setupAsset = assets.FirstOrDefault(a =>
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

            return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable,
                new AppUpdateManifest(version, release.PublishedAt, release.Notes!, packageUrl, sha256, size,
                    MinCompatibleVersion: "1.0.0", SignaturePolicy: "Unsigned"),
                $"GitHub 发现新版本 v{version}（{repository}）。");
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null,
                $"GitHub 更新检查失败：{ex.GetBaseException().Message}");
        }
    }

    /// <summary>解析出的 Release 候选；仅当 <see cref="Error"/> 为空时其余字段可用。</summary>
    private sealed record ReleaseCandidate(
        string? Version,
        JsonElement[]? Assets,
        string? Notes,
        DateTimeOffset PublishedAt,
        string? Error);

    /// <summary>
    /// 正式渠道读 <c>/releases/latest</c>（GitHub 按语义只返回非预发布版本）；
    /// 测试渠道读 <c>/releases</c> 列表，只在标了 prerelease 的条目里取版本号最高者：
    /// <c>-test.N</c> 预发布由 <c>/latest</c> 永远看不到，但也不能因此把正式版当更新装进测试安装。
    /// </summary>
    private async Task<ReleaseCandidate> ResolveReleaseAsync(string repository, bool includePreReleases,
        CancellationToken cancellationToken)
    {
        var url = includePreReleases
            ? $"https://api.github.com/repos/{repository}/releases?per_page=30"
            : $"https://api.github.com/repos/{repository}/releases/latest";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Fufucode", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return new ReleaseCandidate(null, null, null, default,
                $"GitHub Release 查询失败：HTTP {(int)response.StatusCode}（仓库 {repository}）。");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var root = document.RootElement;

        if (!includePreReleases) return ReadRelease(root, repository);

        if (root.ValueKind != JsonValueKind.Array)
            return new ReleaseCandidate(null, null, null, default, "GitHub Release 列表格式无效。");

        ReleaseCandidate? best = null;
        foreach (var item in root.EnumerateArray())
        {
            // 测试渠道只看测试版本的 Release：正式版的安装身份、协议名与数据根都不同，
            // 把正式版当成"更新"装进来会破坏当前安装。
            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("prerelease", out var preRelease) &&
                preRelease.ValueKind == JsonValueKind.False) continue;
            var candidate = ReadRelease(item, repository);
            if (candidate.Error is not null) continue;
            if (best is null || UpdateVersion.Compare(candidate.Version!, best.Version!) > 0) best = candidate;
        }
        return best ?? new ReleaseCandidate(null, null, null, default,
            $"GitHub 仓库 {repository} 没有可用的 Release。");
    }

    private static ReleaseCandidate ReadRelease(JsonElement root, string repository)
    {
        if (!root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String)
            return new ReleaseCandidate(null, null, null, default, "GitHub Release 缺少 tag_name。");

        var version = NormalizeTag(tag.GetString());
        if (version is null)
            return new ReleaseCandidate(null, null, null, default,
                $"无法从 Release tag '{tag.GetString()}' 解析版本号。");
        if (!UpdateVersion.IsValid(version))
            return new ReleaseCandidate(null, null, null, default,
                $"GitHub Release tag '{tag.GetString()}' 不是合法版本号。");

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return new ReleaseCandidate(null, null, null, default, "GitHub Release 不含资产列表。");

        var notes = GetString(root, "body") ?? $"GitHub Release {tag.GetString()}（{repository}）";
        var published = root.TryGetProperty("published_at", out var p) &&
                        DateTimeOffset.TryParse(p.GetString(), out var publishedAt)
            ? publishedAt
            : DateTimeOffset.UtcNow;
        return new ReleaseCandidate(version, assets.EnumerateArray().ToArray(), notes, published, null);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Release tag → 版本号：剥掉可选的 <c>v</c> 前缀；非数字开头的一律拒绝。</summary>
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
