using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Fufucode.App.Features.Automation.Persistence;

/// <summary>
/// 持久化 scope：projectKey + 数据库规范路径 + M 提供的数据库稳定身份，三字段共同决定一个文件。
/// 本类型不打开数据库、不解析 Core 库、不判断「新路径是否等于旧权威」；那属于 M 的接线职责。
/// 文件名只由规范化 scope 的哈希产生，任何字段都不参与路径拼接，因此没有路径逃逸入口。
/// </summary>
public readonly record struct AutomationPersistenceScope : IEquatable<AutomationPersistenceScope>
{
    /// <summary>本模块固定子目录名；只由本模块读写，与用户数据根下其它内容隔离。</summary>
    public const string StoreDirectoryName = "automation-control-state-v1";


    /// <summary>
    /// 用户明确提供的非空项目 key（<b>不透明原身份</b>）：按 Ordinal 精确区分，逐字保真——
    /// 大小写、首尾空格、内部空格都是身份的一部分，本模块不做 Trim 或大小写折叠（C5-PERSIST-OPAQUE-ID-1）。
    /// </summary>
    public string ProjectKey { get; }

    /// <summary>数据库文件的完整绝对路径（本模块不打开它）。保留原始大小写供展示与诊断。</summary>
    public string DatabasePath { get; }

    /// <summary>
    /// M 提供的数据库稳定身份（非空）：同样是不透明原值，Ordinal 精确区分、逐字保真。
    /// 未提供时 scope 未绑定，不可读写。M 不得依赖本模块从显示文字反推原身份。
    /// </summary>
    public string AuthorityIdentity { get; }

    private AutomationPersistenceScope(string projectKey, string databasePath, string authorityIdentity)
    {
        ProjectKey = projectKey;
        DatabasePath = databasePath;
        AuthorityIdentity = authorityIdentity;
    }

    /// <summary>
    /// 创建并校验 scope。projectKey / authorityIdentity 必须非空且不含控制字符，
    /// databasePath 必须是绝对路径。非法输入返回失败，不产生任何磁盘访问。
    /// </summary>
    public static AutomationPersistenceResult<AutomationPersistenceScope> TryCreate(
        string projectKey, string databasePath, string authorityIdentity)
        => TryCreate(projectKey, databasePath, authorityIdentity, AutomationPersistenceLimits.Default);

    /// <summary>同上，但由调用方给出上限（与 store 实例使用同一套 limits）。</summary>
    public static AutomationPersistenceResult<AutomationPersistenceScope> TryCreate(
        string projectKey, string databasePath, string authorityIdentity, AutomationPersistenceLimits limits)
    {
        limits ??= AutomationPersistenceLimits.Default;
        // projectKey / authorityIdentity 是不透明原身份：逐字保真，绝不 Trim、不改大小写、不重排。
        // 曾按 Trim() 存储，使 "project" 与 " project "、"authority" 与 " authority " 成为同一 scope，
        // 一份草稿能被另一身份读回（C5-PERSIST-OPAQUE-ID-1）。长度上限按原始长度判定，超限明示拒绝；
        // 纯空白仍拒绝：那不是「首尾带空格的身份」，而是没有身份。
        if (string.IsNullOrWhiteSpace(projectKey))
            return Fail("projectKey 不能为空或纯空白");
        if (projectKey.Length > limits.MaxIdentifierLength)
            return Fail($"projectKey 原始长度 {projectKey.Length} 超过上限 {limits.MaxIdentifierLength} 字符");
        if (HasControlChar(projectKey))
            return Fail("projectKey 含控制字符");
        if (string.IsNullOrWhiteSpace(authorityIdentity))
            return Fail("authorityIdentity 不能为空或纯空白（由主控 M 提供数据库稳定身份）");
        if (authorityIdentity.Length > limits.MaxIdentifierLength)
            return Fail($"authorityIdentity 原始长度 {authorityIdentity.Length} 超过上限 {limits.MaxIdentifierLength} 字符");
        if (HasControlChar(authorityIdentity))
            return Fail("authorityIdentity 含控制字符");
        if (string.IsNullOrWhiteSpace(databasePath))
            return Fail("数据库路径不能为空");
        if (!TryNormalizeDatabasePath(databasePath, out var normalized, out var pathError))
            return Fail(pathError);
        if (normalized.Length > limits.MaxIdentifierLength)
            return Fail($"规范化数据库路径超过上限 {limits.MaxIdentifierLength} 字符");

        return AutomationPersistenceResult<AutomationPersistenceScope>.Ok(
            new AutomationPersistenceScope(projectKey, normalized, authorityIdentity));

        AutomationPersistenceResult<AutomationPersistenceScope> Fail(string message) =>
            AutomationPersistenceResult<AutomationPersistenceScope>.Fail(AutomationPersistenceErrorCode.InvalidArgument, message);
    }

    /// <summary>
    /// 数据库路径规范化：展开为绝对路径、统一分隔符、去掉结尾分隔符。
    /// Windows 上大小写与 \ / 差异归同一路径；其它平台保持大小写敏感（不套用 Windows 比较语义）。
    /// 相对路径一律拒绝，避免受当前工作目录影响。
    /// </summary>
    public static bool TryNormalizeDatabasePath(string databasePath, out string normalized, out string error)
    {
        normalized = "";
        error = "";
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            error = "数据库路径不能为空";
            return false;
        }

        if (HasControlChar(databasePath))
        {
            error = "数据库路径含控制字符";
            return false;
        }

        // 必须在展开前判断「完整绝对路径」。Path.GetFullPath 会按当前工作目录/当前盘补全，
        // 展开后再查 IsPathRooted 永远通过；而 IsPathRooted 本身也放过 C:relative.db（盘相对）
        // 与 \relative.db（根相对）这两种会随 cwd/当前盘漂移的写法（C5-PERSIST-PATH-1）。
        // IsPathFullyQualified 要求同时含根与盘（或 UNC），因此正是这里需要的谓词。
        if (!Path.IsPathFullyQualified(databasePath))
        {
            error = "数据库路径必须是完整绝对路径（不接受相对、盘相对如 C:relative.db、根相对如 \\relative.db，"
                + "它们会随当前目录或当前盘漂移）：" + databasePath;
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(databasePath);
        }
        catch (Exception pathError)
        {
            error = "数据库路径无法规范化：" + pathError.Message;
            return false;
        }

        if (!Path.IsPathFullyQualified(full))
        {
            error = "数据库路径展开后仍不是完整绝对路径：" + databasePath;
            return false;
        }

        full = full.Replace('/', Path.DirectorySeparatorChar);
        normalized = Path.TrimEndingDirectorySeparator(full);
        if (normalized.Length == 0)
        {
            error = "数据库路径规范化后为空";
            return false;
        }
        return true;
    }

    /// <summary>参与哈希的路径形式：Windows 折叠大小写，其它平台保持精确。</summary>
    public string CanonicalDatabasePath => OperatingSystem.IsWindows()
        ? DatabasePath.ToUpperInvariant()
        : DatabasePath;

    /// <summary>
    /// 规范化 scope 的 SHA-256 十六进制，作为文件名主体。
    /// 元组用**长度前缀**编码（C5-PERSIST-SCOPE-1）：任何字段内容里的分隔符类字符都无法伪造字段边界，
    /// 因此 (a∤C:\X, C:\Y, z) 与 (a, C:\X, C:\Y∤z) 这类「不同三元组」必然得到不同哈希，可各自独立存储。
    /// </summary>
    public string ScopeHash => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        AutomationTupleEncoding.Encode(ProjectKey, CanonicalDatabasePath, AuthorityIdentity))));

    /// <summary>
    /// 三字段是否指向同一存储目标：路径按 OS 语义比较，projectKey / authorityIdentity 按 Ordinal 精确比较。
    /// 项目 key 或稳定身份不同必须隔离，即使数据库路径完全相同。
    /// </summary>
    public bool SameTargetAs(AutomationPersistenceScope other) =>
        string.Equals(ProjectKey, other.ProjectKey, StringComparison.Ordinal) &&
        string.Equals(AuthorityIdentity, other.AuthorityIdentity, StringComparison.Ordinal) &&
        string.Equals(CanonicalDatabasePath, other.CanonicalDatabasePath, StringComparison.Ordinal);

    /// <summary>三字段齐备才可用于读写（TryCreate 成功即满足）。</summary>
    public bool IsBound =>
        !string.IsNullOrWhiteSpace(ProjectKey) && !string.IsNullOrWhiteSpace(DatabasePath) && !string.IsNullOrWhiteSpace(AuthorityIdentity);

    /// <summary>两个 scope 是否指向同一存储目标（等价于 <see cref="SameTargetAs"/>）。</summary>
    public bool Equals(AutomationPersistenceScope other) => SameTargetAs(other);

    public override int GetHashCode() =>
        HashCode.Combine(ProjectKey, AuthorityIdentity, CanonicalDatabasePath);

    public override string ToString() => ProjectKey + " · " + DatabasePath + " · " + AuthorityIdentity;

    /// <summary>
    /// 核验落盘文件内部记录的 scope 三字段是否与请求的 scope 一致：改名或换库导致的错配会被拒绝，
    /// 不会把另一个库或另一个项目的草稿误恢复进来。
    /// </summary>
    public bool MatchesRecordedScope(string recordedProjectKey, string recordedDatabasePath, string recordedAuthorityIdentity)
    {
        if (!string.Equals(recordedProjectKey, ProjectKey, StringComparison.Ordinal)) return false;
        if (!string.Equals(recordedAuthorityIdentity, AuthorityIdentity, StringComparison.Ordinal)) return false;
        if (!TryNormalizeDatabasePath(recordedDatabasePath, out var recordedNormalized, out _)) return false;
        var recordedCanonical = OperatingSystem.IsWindows() ? recordedNormalized.ToUpperInvariant() : recordedNormalized;
        return string.Equals(recordedCanonical, CanonicalDatabasePath, StringComparison.Ordinal);
    }

    private static bool HasControlChar(string value) => value.Any(char.IsControl);
}
