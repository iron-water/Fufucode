using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core;
using Fufucode.Core.Updates;

namespace Fufucode.App.Backend.Tests;

/// <summary>
/// 部署 / 更新两条路径的边界测试：
/// 1. 构建身份由 commit + 构建时间组成，同一提交的两次构建身份不同；
/// 2. 部署只比较构建身份，不比较版本号；
/// 3. 部署会跳过内容一致的文件，只复制变化部分；
/// 4. 发布渠道构建不能通过本地部署覆盖；
/// 5. 更新只对发布构建启用（需要 channel=github + 仓库坐标）；
/// 6. 版本比较只服务于更新通道。
/// </summary>
internal static class UpdateDiscoveryTests
{
    public static async Task RunAsync(Checks checks)
    {
        var root = Path.Combine(Path.GetTempPath(), "fufucode-deploy-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        try
        {
            var layout = new AppDataLayout(root);
            layout.EnsureCreated();

            var now = DateTimeOffset.UtcNow;
            var build = new BuildInfo("1.4.0", now, "local", Commit: "abc1234");
            build.Write(source);

            // 1. 构建身份 = commit + 构建时间；重复构建身份不同。
            checks.Check(build.BuildId.StartsWith("abc1234-", StringComparison.Ordinal), "构建身份以提交短哈希开头");
            var rebuilt = build with { BuiltAtUtc = now.AddSeconds(1) };
            checks.Check(!build.SameBuild(rebuilt), "同一提交的两次构建身份不同");
            checks.Check(build.SameBuild(build with { }), "同一构建标记身份相同");

            // 2. 部署：内容一致的文件跳过，只复制变化部分。
            await File.WriteAllTextAsync(Path.Combine(source, "a.dll"), "same");
            await File.WriteAllTextAsync(Path.Combine(source, "b.dll"), "new");
            await File.WriteAllTextAsync(Path.Combine(target, "a.dll"), "same");
            await File.WriteAllTextAsync(Path.Combine(target, "b.dll"), "old");
            File.SetLastWriteTimeUtc(Path.Combine(target, "a.dll"), now.UtcDateTime.AddMinutes(-5));
            File.SetLastWriteTimeUtc(Path.Combine(target, "b.dll"), now.UtcDateTime.AddMinutes(-5));
            var deployment = new BuildDeploymentService(layout);
            var result = deployment.Deploy(source, target);
            checks.Check(result.Success, "部署成功");
            // 部署按"大小 + 写入时间"判断，信任构建产物：两个目录里的 a.dll 是分别写入的，
            // 时间戳不同，因此会被复制。真正无变化的是"同一构建再次部署"（见第 3 步）。
            checks.Equal(3, result.Copied, "首次部署复制全部源文件（含构建标记）");
            checks.Equal(0, result.Skipped, "不同时间写入的文件不会被误判为未变");
            checks.Equal("new", await File.ReadAllTextAsync(Path.Combine(target, "b.dll")), "目标文件已更新为源内容");
            checks.Check(File.Exists(Path.Combine(layout.UpdateBackupDirectory, build.BuildId, "b.dll")),
                "被覆盖的旧文件已备份");

            // 3. 重复部署同一构建：识别为已部署。
            var again = deployment.Deploy(source, target);
            checks.Check(again.Success && again.Copied == 0 && again.Skipped > 0, "同一构建重复部署不复制任何文件");

            // 3b. 待重启清单绑定构建身份：源目录被重新构建后，旧清单必须作废，
            // 否则重启会用新构建的文件补旧清单，安装目录混装两个构建。
            var pendingSource = Path.Combine(root, "pending-source");
            var pendingTarget = Path.Combine(root, "pending-target");
            Directory.CreateDirectory(pendingSource);
            Directory.CreateDirectory(pendingTarget);
            var oldBuild = new BuildInfo("1.4.0", now.AddMinutes(-10), "local", Commit: "aaa1111");
            oldBuild.Write(pendingSource);
            await File.WriteAllTextAsync(Path.Combine(pendingSource, "a.dll"), "old-a");
            await File.WriteAllTextAsync(Path.Combine(pendingTarget, "a.dll"), "installed-old-a");
            await File.WriteAllTextAsync(Path.Combine(pendingTarget, "restart-pending.json"),
                JsonSerializer.Serialize(new
                {
                    SourceDirectory = pendingSource,
                    BuildId = oldBuild.BuildId,
                    CreatedAtUtc = now,
                    Files = new[] { "a.dll" },
                }));
            // 用户尚未重启就重新构建了同一个输出目录。
            new BuildInfo("1.4.0", now.AddMinutes(-5), "local", Commit: "bbb2222").Write(pendingSource);
            await File.WriteAllTextAsync(Path.Combine(pendingSource, "a.dll"), "new-build-a");
            checks.Check(!deployment.ApplyPendingOnStartup(pendingTarget),
                "源构建身份变化时拒绝应用待重启清单");
            checks.Check(await File.ReadAllTextAsync(Path.Combine(pendingTarget, "a.dll")) == "installed-old-a",
                "过期待重启清单不会把新构建文件混入旧安装");
            checks.Check(!File.Exists(Path.Combine(pendingTarget, "restart-pending.json")),
                "过期待重启清单被清除，避免下次启动继续混装");

            // 3c. 构建身份仍匹配但源文件缺失：保留清单等待下次启动，不静默丢弃，也不部分应用。
            var currentPendingBuild = BuildInfo.Read(pendingSource)!;
            await File.WriteAllTextAsync(Path.Combine(pendingTarget, "restart-pending.json"),
                JsonSerializer.Serialize(new
                {
                    SourceDirectory = pendingSource,
                    BuildId = currentPendingBuild.BuildId,
                    CreatedAtUtc = now,
                    Files = new[] { "a.dll", "missing.dll" },
                }));
            checks.Check(!deployment.ApplyPendingOnStartup(pendingTarget),
                "源文件缺失时拒绝部分应用待重启清单");
            checks.Check(await File.ReadAllTextAsync(Path.Combine(pendingTarget, "a.dll")) == "installed-old-a",
                "源文件缺失时一个文件也不覆盖");
            checks.Check(File.Exists(Path.Combine(pendingTarget, "restart-pending.json")),
                "源文件缺失时保留待重启清单，下一次启动仍可重试");
            File.Delete(Path.Combine(pendingTarget, "restart-pending.json"));

            // 4. 发布渠道构建不能通过本地部署覆盖。
            var releaseSource = Path.Combine(root, "release");
            Directory.CreateDirectory(releaseSource);
            new BuildInfo("9.9.9", now, "github", "owner/repo", "def5678").Write(releaseSource);
            await File.WriteAllTextAsync(Path.Combine(releaseSource, "x.dll"), "x");
            var blocked = deployment.Deploy(releaseSource, target);
            checks.Check(!blocked.Success, "发布渠道构建被拒绝通过本地部署覆盖");

            // 审阅包必须保持独立：既不能作为部署源，也不能被本地部署覆盖。
            var reviewSource = Path.Combine(root, "review-source");
            (build with { Review = true, BuiltAtUtc = now.AddYears(1) }).Write(reviewSource);
            checks.Check(!deployment.Deploy(reviewSource, target).Success,
                "审阅构建不能作为本地部署源");
            var previousSource = Environment.GetEnvironmentVariable("FUFUCODE_UPDATE_SOURCE_DIR");
            try
            {
                Environment.SetEnvironmentVariable("FUFUCODE_UPDATE_SOURCE_DIR", reviewSource);
                checks.Check(deployment.FindLatestBuild(out _)?.Review != true,
                    "发现本地构建时跳过时间更晚的审阅包");
                var pointer = Path.Combine(root, "pointer");
                (build with { SourceDirectory = reviewSource }).Write(pointer);
                Environment.SetEnvironmentVariable("FUFUCODE_UPDATE_SOURCE_DIR", pointer);
                checks.Check(deployment.FindLatestBuild(out _)?.Review != true,
                    "解析本地构建指针后仍排除审阅包");
            }
            finally
            {
                Environment.SetEnvironmentVariable("FUFUCODE_UPDATE_SOURCE_DIR", previousSource);
            }
            foreach (var protectedBuild in new[]
                     {
                         build with { Review = true },
                         build with { Channel = "github" },
                         build with { Channel = "test" },
                     })
            {
                protectedBuild.Write(target);
                var marker = await File.ReadAllTextAsync(Path.Combine(target, BuildInfo.FileName));
                await File.WriteAllTextAsync(Path.Combine(target, "b.dll"), "protected");
                var refused = deployment.Deploy(source, target);
                checks.Check(!refused.Success && !refused.RestartRequired && refused.Copied == 0,
                    $"不覆盖受保护构建（{protectedBuild.Channel}, review={protectedBuild.Review}）");
                checks.Equal(marker, await File.ReadAllTextAsync(Path.Combine(target, BuildInfo.FileName)),
                    "拒绝部署时保留原构建标记");
                checks.Equal("protected", await File.ReadAllTextAsync(Path.Combine(target, "b.dll")),
                    "拒绝部署时保留原程序载荷");
                if (protectedBuild.Review)
                {
                    var pendingPath = Path.Combine(target, "restart-pending.json");
                    await File.WriteAllTextAsync(pendingPath, JsonSerializer.Serialize(new
                    {
                        SourceDirectory = source,
                        BuildId = build.BuildId,
                        Files = new[] { "b.dll" },
                    }));
                    checks.Check(!deployment.ApplyPendingOnStartup(target) &&
                                 await File.ReadAllTextAsync(Path.Combine(target, "b.dll")) == "protected" &&
                                 File.Exists(pendingPath),
                        "审阅启动不执行既有部署清单，也不丢弃清单");
                    File.Delete(pendingPath);
                }
            }

            // 5. 更新只对发布构建启用（正式 github 与实验性 test 两个渠道都算发布构建）。
            checks.Check(!UpdateService.IsReleaseBuild(new BuildInfo("1.4.0", now, "local")),
                "本地构建不启用更新通道");
            checks.Check(!UpdateService.IsReleaseBuild(new BuildInfo("1.4.0", now, "github")),
                "发布渠道但缺少仓库坐标时不启用更新");
            checks.Check(UpdateService.IsReleaseBuild(new BuildInfo("1.4.0", now, "github", "owner/repo")),
                "发布渠道且含仓库坐标时启用更新");
            checks.Check(UpdateService.IsReleaseBuild(new BuildInfo("0.1.0-test.1", now, "test", "owner/repo")),
                "测试渠道且含仓库坐标时启用更新");
            checks.Check(!UpdateService.IsReleaseBuild(new BuildInfo("0.1.0-test.1", now, "test")),
                "测试渠道但缺少仓库坐标时不启用更新");

            // 5b. 测试渠道与正式渠道互斥：测试构建不能被本地部署覆盖，正式构建也不认 test 标记。
            var testChannel = new BuildInfo("0.1.0-test.1", now, "test", "owner/repo", "abc1234");
            checks.Check(testChannel.IsTestChannel && testChannel.IsReleaseChannel && !testChannel.IsLocalChannel,
                "测试渠道是发布构建的一种，不是本地构建");
            checks.Check(new BuildInfo("1.4.0", now, "github", "owner/repo").IsGitHubRelease &&
                         !new BuildInfo("1.4.0", now, "github", "owner/repo").IsTestChannel,
                "正式渠道不被误判为测试渠道");

            // 6. 版本比较只服务于更新通道。
            checks.Equal(0, UpdateVersion.Compare("1.4.0", "1.4.0"), "相同版本比较为 0");
            checks.Check(UpdateVersion.Compare("1.10.0", "1.9.9") > 0, "数字段按数值比较而非字典序");

            // 6b. 预发布后缀按 SemVer 排序：测试渠道只差 -test.N，忽略后缀就永远发现不到更新。
            checks.Check(UpdateVersion.Compare("1.4.0-dev", "1.4.0") < 0, "预发布版本低于同号正式版");
            checks.Check(UpdateVersion.Compare("0.1.0-test.2", "0.1.0-test.1") > 0, "同前缀的测试序列号按数值递增");
            checks.Check(UpdateVersion.Compare("0.1.0-test.10", "0.1.0-test.9") > 0, "测试序列号按数值而非字典序比较");
            checks.Check(UpdateVersion.Compare("0.2.0-test.1", "0.1.0-test.9") > 0, "主版本优先于预发布后缀");
            checks.Check(UpdateVersion.IsValid("0.1.0-test.1") && !UpdateVersion.IsValid("test") &&
                         !UpdateVersion.IsValid("") && !UpdateVersion.IsValid(null),
                "版本校验拒绝非版本号的 tag");
            checks.Equal("0.1.0-test.3",
                UpdateVersion.Max(new[] { "0.1.0-test.1", "0.1.0-test.3", "0.1.0-test.2", "bogus" }),
                "Max 忽略非版本号并取最高者");

            // 7. build-info.json 往返与 informational version 拆分。
            var read = BuildInfo.Read(source);
            checks.Check(read is not null && read.Commit == "abc1234" && read.IsLocalChannel,
                "build-info.json 可读回并保留提交哈希");
            var (version, commit) = BuildInfo.SplitInformationalVersion("1.0.0+383b6beab5492d90");
            checks.Check(version == "1.0.0" && commit == "383b6beab5492d90", "程序集版本中的 +hash 被拆成 commit");

            // 打包脚本写的是全小写 "githubRepository"，而 CamelCase 策略把记录属性
            // "GitHubRepository" 写成 "gitHubRepository"。读回必须大小写不敏感，
            // 否则发布包读不到仓库坐标，更新通道静默失效（2026-11-03 实测发现）。
            var releaseDir = Path.Combine(root, "release-marker");
            Directory.CreateDirectory(releaseDir);
            await File.WriteAllTextAsync(Path.Combine(releaseDir, BuildInfo.FileName),
                "{\"version\":\"1.4.0\",\"builtAtUtc\":\"2026-11-03T04:50:59Z\",\"channel\":\"github\"," +
                "\"commit\":\"c0405f4c6423\",\"githubRepository\":\"iron-water/Fufucode\"}");
            var releaseBuild = BuildInfo.Read(releaseDir);
            checks.Check(releaseBuild?.GitHubRepository == "iron-water/Fufucode",
                "打包脚本写出的 githubRepository 能被读回");
            checks.Check(UpdateService.IsReleaseBuild(releaseBuild),
                "发布渠道标记可识别为发布构建（更新通道启用）");

            // 8. 清单里的相对 packageUrl 必须补成绝对地址：下载端只认绝对路径，
            // 否则会当成不存在的本地文件（GitHub 资产里的 feed.json 就是这么写的）。
            var feedDir = Path.Combine(root, "feed");
            Directory.CreateDirectory(feedDir);
            var packagePath = Path.Combine(feedDir, "Fufucode-Native-Setup.exe");
            await File.WriteAllTextAsync(packagePath, "fake-installer");
            var feedPath = Path.Combine(feedDir, "feed.json");
            await File.WriteAllTextAsync(feedPath, JsonSerializer.Serialize(new
            {
                version = "9.9.9",
                releaseDate = now,
                releaseNotes = "测试用发布说明",
                packageUrl = "Fufucode-Native-Setup.exe",
                sha256 = Convert.ToHexString(SHA256.HashData("fake-installer"u8.ToArray())),
                packageSizeBytes = 14,
                minCompatibleVersion = "1.0.0",
            }));
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"),
                JsonSerializer.Serialize(new { version = 1, updateFeedUrl = feedPath }));
            var feedStore = new AppSettingsStore(layout);
            using var appUpdates = new AppUpdateService(layout, feedStore);
            var feedResult = await appUpdates.CheckForUpdatesAsync("1.0.0");
            checks.Check(feedResult.Status == UpdateCheckStatus.UpdateAvailable, "本地 feed 清单发现新版本");
            checks.Check(feedResult.Manifest is { } feedManifest &&
                         Path.IsPathRooted(feedManifest.PackageUrl) &&
                         string.Equals(feedManifest.PackageUrl, packagePath, StringComparison.OrdinalIgnoreCase),
                "清单里的相对 packageUrl 解析为同目录的绝对路径");

            // 9. GitHub 路径：资产 JsonElement 必须在 JsonDocument 释放后仍然可读。
            // 只在新版本分支读资产，所以「已是最新」一直正常、一有更新就 ObjectDisposedException。
            var releaseJson =
                "{\"tag_name\":\"v9.9.9\",\"body\":\"* 修复若干问题\",\"published_at\":\"2026-11-03T05:50:15Z\"," +
                "\"assets\":[{\"name\":\"feed.json\",\"browser_download_url\":\"https://example.invalid/feed.json\"}," +
                "{\"name\":\"Fufucode-Native-Setup.exe\",\"browser_download_url\":\"https://example.invalid/Fufucode-Native-Setup.exe\"," +
                "\"size\":14,\"digest\":\"sha256:" + new string('b', 64) + "\"}]}";
            var feedJson = JsonSerializer.Serialize(new
            {
                version = "9.9.9",
                releaseDate = now,
                releaseNotes = "feed 里的固定文案",
                packageUrl = "Fufucode-Native-Setup.exe",
                sha256 = new string('b', 64),
                packageSizeBytes = 14,
            });
            using var github = new UpdateService(new HttpClient(new StubHttpHandler(request =>
                request.RequestUri!.AbsolutePath.EndsWith("/feed.json", StringComparison.Ordinal)
                    ? (System.Net.HttpStatusCode.OK, feedJson)
                    : (System.Net.HttpStatusCode.OK, releaseJson))));
            var githubResult = await github.CheckAsync("1.4.0", "iron-water/Fufucode");
            checks.Check(githubResult.Status == UpdateCheckStatus.UpdateAvailable,
                "GitHub Release 有新版本时返回 UpdateAvailable（资产在文档释放后仍可读）");
            checks.Check(githubResult.Manifest is { } githubManifest &&
                         githubManifest.PackageUrl == "https://example.invalid/Fufucode-Native-Setup.exe",
                "feed.json 的相对 packageUrl 按资产地址补全为绝对下载地址");
            checks.Equal("* 修复若干问题", githubResult.Manifest?.ReleaseNotes, "发布说明取 Release 正文");
            _ = Task.CompletedTask;
        }
        finally
        {
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}

/// <summary>用固定 JSON 应答任意请求，避免测试联网。</summary>
internal sealed class StubHttpHandler(Func<HttpRequestMessage, (System.Net.HttpStatusCode Status, string Body)> respond)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var (status, body) = respond(request);
        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });
    }
}
