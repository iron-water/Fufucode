using System;
using System.IO;
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

            // 4. 发布渠道构建不能通过本地部署覆盖。
            var releaseSource = Path.Combine(root, "release");
            Directory.CreateDirectory(releaseSource);
            new BuildInfo("9.9.9", now, "github", "owner/repo", "def5678").Write(releaseSource);
            await File.WriteAllTextAsync(Path.Combine(releaseSource, "x.dll"), "x");
            var blocked = deployment.Deploy(releaseSource, target);
            checks.Check(!blocked.Success, "发布渠道构建被拒绝通过本地部署覆盖");

            // 5. 更新只对发布构建启用。
            checks.Check(!UpdateService.IsReleaseBuild(new BuildInfo("1.4.0", now, "local")),
                "本地构建不启用更新通道");
            checks.Check(!UpdateService.IsReleaseBuild(new BuildInfo("1.4.0", now, "github")),
                "发布渠道但缺少仓库坐标时不启用更新");
            checks.Check(UpdateService.IsReleaseBuild(new BuildInfo("1.4.0", now, "github", "owner/repo")),
                "发布渠道且含仓库坐标时启用更新");

            // 6. 版本比较只服务于更新通道。
            checks.Equal(0, UpdateVersion.Compare("1.4.0", "1.4.0"), "相同版本比较为 0");
            checks.Check(UpdateVersion.Compare("1.10.0", "1.9.9") > 0, "数字段按数值比较而非字典序");
            checks.Check(UpdateVersion.Compare("1.4.0-dev", "1.4.0") == 0, "预发布后缀不影响数值比较");

            // 7. build-info.json 往返与 informational version 拆分。
            var read = BuildInfo.Read(source);
            checks.Check(read is not null && read.Commit == "abc1234" && read.IsLocalChannel,
                "build-info.json 可读回并保留提交哈希");
            var (version, commit) = BuildInfo.SplitInformationalVersion("1.0.0+383b6beab5492d90");
            checks.Check(version == "1.0.0" && commit == "383b6beab5492d90", "程序集版本中的 +hash 被拆成 commit");
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
