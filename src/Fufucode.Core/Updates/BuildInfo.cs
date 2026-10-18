using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fufucode.Core.Updates;

/// <summary>
/// 构建标记：随每个构建产物写出的 build-info.json。
///
/// 它是"部署"与"更新"的分界线：
/// - 部署（本地开发）只比较 <see cref="BuildId"/>：运行中的构建是否就是目标构建；
/// - 更新（GitHub 发布）只比较版本号，与本地构建互不相干。
/// </summary>
/// <param name="Version">构建版本号（本地 dev 序列或发布标签）。</param>
/// <param name="BuiltAtUtc">构建时间。</param>
/// <param name="Channel">"local" = 本地开发构建；"github" = 正式发布构建。</param>
/// <param name="GitHubRepository">正式发布构建写入 owner/repo；本地构建为 null。</param>
/// <param name="Commit">构建对应的提交短哈希；未知时为 null。</param>
/// <param name="SourceDirectory">指针构建指向的真实构建目录；普通构建为 null。</param>
public sealed record BuildInfo(
    string Version,
    DateTimeOffset BuiltAtUtc,
    string Channel,
    string? GitHubRepository = null,
    string? Commit = null,
    /// <summary>指针构建：真实构建目录（用于从任意安装位置指向仓库 out/app）。</summary>
    string? SourceDirectory = null)
{
    public const string FileName = "build-info.json";

    public bool IsReleaseChannel => string.Equals(Channel, "github", StringComparison.OrdinalIgnoreCase);

    public bool IsLocalChannel => !IsReleaseChannel;

    /// <summary>
    /// 构建身份：提交短哈希 + 构建时间。同一个提交的重复构建也各有不同身份，
    /// 因此本地"改完重新构建"天然就是一次部署，不需要版本号参与判断。
    /// </summary>
    [JsonIgnore]
    public string BuildId
    {
        get
        {
            var stamp = BuiltAtUtc.UtcDateTime.ToString("yyyyMMddHHmmss");
            var commit = string.IsNullOrWhiteSpace(Commit) ? "nocommit" : Commit.Trim().ToLowerInvariant();
            return $"{commit}-{stamp}";
        }
    }

    /// <summary>两个构建标记是否指向同一次构建。</summary>
    public bool SameBuild(BuildInfo? other) =>
        other is not null && string.Equals(BuildId, other.BuildId, StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>读取指定目录下的构建标记；不存在或损坏时返回 null（不猜测）。</summary>
    public static BuildInfo? Read(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            var info = JsonSerializer.Deserialize<BuildInfo>(File.ReadAllText(path), JsonOptions);
            return string.IsNullOrWhiteSpace(info?.Version) ? null : info;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>写出构建标记（打包脚本也写同格式 JSON）。</summary>
    public void Write(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, FileName), JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>
    /// 解析当前进程所属构建。优先 build-info.json；缺失时（例如 dotnet run）
    /// 回退到程序集版本（其中的 +hash 即 SourceRevisionId）与文件时间，标记为本地构建。
    /// </summary>
    public static BuildInfo ResolveCurrent()
    {
        var info = Read(AppContext.BaseDirectory);
        if (info is not null) return info;

        var assembly = Assembly.GetEntryAssembly() ?? typeof(BuildInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                            ?? assembly.GetName().Version?.ToString()
                            ?? "0.0.0";
        var (version, commit) = SplitInformationalVersion(informational);

        var path = assembly.Location;
        var builtAt = path.Length > 0 && File.Exists(path)
            ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)
            : DateTimeOffset.UtcNow;
        return new BuildInfo(version, builtAt, "local", Commit: commit);
    }

    /// <summary>"1.0.0+383b6beab549..." → ("1.0.0", "383b6beab549...")。</summary>
    public static (string Version, string? Commit) SplitInformationalVersion(string informational)
    {
        var plus = informational.IndexOf('+');
        if (plus < 0) return (informational, null);
        var version = informational[..plus];
        var commit = informational[(plus + 1)..].Trim();
        return (version, commit.Length == 0 ? null : commit);
    }
}
