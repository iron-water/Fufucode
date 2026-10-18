using System;

namespace Fufucode.App.Features.Automation.Persistence;

/// <summary>
/// 持久化模块对外的唯一失败分类。每个失败都必须可诊断（含具体原因文本），
/// 并且失败路径绝不改写目标文件原字节。
/// </summary>
public enum AutomationPersistenceErrorCode
{
    /// <summary>调用方输入不合法（空 key、相对路径、超长、重复候选等），未产生任何磁盘访问。</summary>
    InvalidArgument,
    /// <summary>规范化后的 scope 文件落在本模块固定目录之外，拒绝。</summary>
    ScopeOutsideRoot,
    /// <summary>scope 文件或其祖先目录是重解析点（junction/符号链接），拒绝读写。</summary>
    ReparsePoint,
    /// <summary>无法建立本模块固定目录（权限/磁盘）。</summary>
    RootUnavailable,
    /// <summary>同一 scope 的写入锁被其他实例/进程占用，明示竞争，不排队不覆盖。</summary>
    LockContended,
    /// <summary>写入所需的临时文件无法创建或落盘。</summary>
    TempWriteFailed,
    /// <summary>临时文件已落盘但原子发布失败（目标被读者句柄占用等），原文件保持原字节。</summary>
    PublishFailed,
    /// <summary>目标文件读取失败（不是「不存在」）。</summary>
    ReadFailed,
    /// <summary>JSON 非法、重复键、未知字段、scope 字段错配、格式未知或未来版本。</summary>
    InvalidFileContent,
    /// <summary>文件内的 generation 与调用方预期不符，拒绝 last-writer-wins。</summary>
    GenerationConflict,
    /// <summary>
    /// 写入没有给出预期 generation（C5-PERSIST-GENERATION-1）。所有写入口共用同一道门：
    /// 缺预期值一律拒绝盲写；调用方先用 ReadGeneration 取得当前值（不存在即 0）再写入。
    /// </summary>
    MissingExpectedGeneration,
    /// <summary>同 ID 条目已存在但内容不同，或引用的 ID 不存在。</summary>
    DuplicateIdentity,
    /// <summary>引用的 commandId / todoId 在本 scope 内不存在。</summary>
    UnknownIdentifier,
    /// <summary>草稿绑定的待办版本/证据摘要已前进，不自动把新版本套到旧草稿上。</summary>
    DraftSuperseded,
    /// <summary>超过保守容量上限（条目数或 UTF-8 字节数），写入前拒绝。</summary>
    Oversize,
    /// <summary>状态值不属于声明的状态机（pending_unknown / receipt_observed）。</summary>
    InvalidState,
    /// <summary>已记录内容自身校验失败（摘要不符、时间戳非法等），原字节保留。</summary>
    CorruptRecord,
}

/// <summary>失败详情。<see cref="Message"/> 面向日志与用户提示，不含密钥或数据库内容。</summary>
public sealed record AutomationPersistenceFailure(AutomationPersistenceErrorCode Code, string Message)
{
    public override string ToString() => Code + " · " + Message;
}

/// <summary>带值结果。调用方必须检查 <see cref="IsSuccess"/> 后才能取 <see cref="Value"/>。</summary>
public sealed class AutomationPersistenceResult<T>
{
    public bool IsSuccess { get; private init; }
    public T? Value { get; private init; }
    public AutomationPersistenceFailure? Failure { get; private init; }

    public static AutomationPersistenceResult<T> Ok(T value) => new() { IsSuccess = true, Value = value };
    public static AutomationPersistenceResult<T> Fail(AutomationPersistenceFailure failure) => new() { IsSuccess = false, Failure = failure };
    public static AutomationPersistenceResult<T> Fail(AutomationPersistenceErrorCode code, string message) => Fail(new AutomationPersistenceFailure(code, message));

    /// <summary>参数校验失败（调用方给出的 ID/文本非法）的简写。</summary>
    public static AutomationPersistenceResult<T> InvalidArgument(string message) => Fail(AutomationPersistenceErrorCode.InvalidArgument, message);

    /// <summary>
    /// 失败即抛的便捷取值：只在 <see cref="IsSuccess"/> 为 false 时抛出，异常信息保留错误码。
    /// 成功但值本身为 null 是合法结果（例如「这条草稿不存在」），此时返回 null 而不是抛异常。
    /// </summary>
    public T ValueOrThrow() => IsSuccess
        ? Value!
        : throw new InvalidOperationException("自动化持久化操作失败：" + (Failure?.ToString() ?? "无结果"));
}
