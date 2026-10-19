using System;
using System.IO;
using System.Linq;

namespace Fufucode.App.Features.Automation.Persistence;

/// <summary>
/// 唯一的读写通道：本模块固定目录内、按 scope 哈希命名的一个文件。
/// 写路径 = 独占锁 → 读当前状态并严格校验 → 应用变更 → generation+1 → 同目录临时文件落盘 → 原子发布。
/// 任一环节失败都保留目标文件原字节；本类绝不调用 Core、绝不启动 harness、绝不删除用户来源。
/// </summary>
internal sealed class AutomationPersistenceWriter
{

    private readonly IAutomationPersistenceFileIo _io;
    private readonly AutomationPersistenceLimits _limits;

    public AutomationPersistenceWriter(string dataRoot, AutomationPersistenceLimits limits, IAutomationPersistenceFileIo io)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
            throw new ArgumentException("数据根不能为空：本模块只写入用户明确提供的数据根下的固定目录。", nameof(dataRoot));
        var fullRoot = Path.GetFullPath(dataRoot);
        if (!Path.IsPathRooted(fullRoot))
            throw new ArgumentException("数据根必须是绝对路径：" + dataRoot, nameof(dataRoot));
        Limits = limits;
        _limits = limits;
        _io = io;
        DataRoot = fullRoot;
        StoreDirectory = Path.Combine(fullRoot, AutomationPersistenceScope.StoreDirectoryName);
    }

    public AutomationPersistenceLimits Limits { get; }
    public string StoreDirectory { get; }

    /// <summary>用户明确提供的数据根（绝对路径）。本模块只写它下面的固定目录。</summary>
    public string DataRoot { get; }

    public string PathOf(AutomationPersistenceScope scope) =>
        Path.Combine(StoreDirectory, scope.ScopeHash + ".json");

    private string LockPathOf(AutomationPersistenceScope scope) =>
        Path.Combine(StoreDirectory, scope.ScopeHash + ".lock");

    /// <summary>
    /// 读取并投影。文件不存在 → <paramref name="absent"/> 的值；非法/损坏/scope 错配/未来格式 → 失败且原字节不动。
    /// 读不加独占锁（多读者可并行），按 <see cref="AutomationPersistenceReadPolicy.ReaderShareMode"/> 打开，
    /// 因此读者持句柄期间写方仍能原子发布，读者保持它打开时刻的旧字节视图。
    /// </summary>
    public AutomationPersistenceResult<T> Read<T>(AutomationPersistenceScope scope, Func<AutomationPersistenceDocument, T> project, T absent)
    {
        var prepare = Prepare(scope, forWrite: false, out var directoryError);
        if (!prepare) return AutomationPersistenceResult<T>.Fail(directoryError);

        var path = PathOf(scope);
        if (!_io.FileExists(path)) return AutomationPersistenceResult<T>.Ok(absent);

        var loaded = LoadDocument(scope, path);
        if (!loaded.IsSuccess) return AutomationPersistenceResult<T>.Fail(loaded.Failure!);
        return AutomationPersistenceResult<T>.Ok(project(loaded.Value!));
    }

    /// <summary>当前 generation；文件不存在视为 0。供 UI 展示与预期值准备。</summary>
    public AutomationPersistenceResult<long> ReadGeneration(AutomationPersistenceScope scope)
        => Read(scope, document => document.Generation, 0L);

    /// <summary>
    /// 在独占锁内应用一次变更并原子发布。**所有写入口共用这一道 generation 门**：
    /// <paramref name="expectedGeneration"/> 缺失（null）→ <see cref="AutomationPersistenceErrorCode.MissingExpectedGeneration"/>
    /// 直接拒绝，绝不盲写（C5-PERSIST-GENERATION-1）；与磁盘当前值不符 → GenerationConflict，明示竞争，不覆盖。
    /// mutator 返回 false 表示无净变化：不写盘、不推进 generation、直接成功。
    /// </summary>
    public AutomationPersistenceResult<AutomationPersistenceWriteResult> Mutate(
        AutomationPersistenceScope scope, long? expectedGeneration, Func<AutomationPersistenceDocument, bool> mutator)
    {
        if (expectedGeneration is null)
            return AutomationPersistenceResult<AutomationPersistenceWriteResult>.Fail(
                AutomationPersistenceErrorCode.MissingExpectedGeneration,
                "写入必须携带预期 generation（新 scope 用 0）。缺失即拒绝盲写：先 ReadGeneration 再提交。");

        var prepare = Prepare(scope, forWrite: true, out var prepareError);
        if (!prepare) return AutomationPersistenceResult<AutomationPersistenceWriteResult>.Fail(prepareError);

        var path = PathOf(scope);
        var lockPath = LockPathOf(scope);
        if (!_io.TryAcquireWriteLock(lockPath, LockTimeoutMs, out var handle))
            return AutomationPersistenceResult<AutomationPersistenceWriteResult>.Fail(
                AutomationPersistenceErrorCode.LockContended,
                "同一 scope 的写入锁被其它实例或进程占用（" + LockTimeoutMs + "ms 内未获得）。本次未写入，原文件保持不动。");

        string? tempPath = null;
        try
        {
            AutomationPersistenceDocument document;
            if (_io.FileExists(path))
            {
                var loaded = LoadDocument(scope, path);
                if (!loaded.IsSuccess) return AutomationPersistenceResult<AutomationPersistenceWriteResult>.Fail(loaded.Failure!);
                document = loaded.Value!;
            }
            else
            {
                document = new AutomationPersistenceDocument
                {
                    ProjectKey = scope.ProjectKey,
                    DatabasePath = scope.DatabasePath,
                    AuthorityIdentity = scope.AuthorityIdentity,
                    Generation = 0,
                    UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                };
            }

            // 方法开头已拒绝 null，因此这里必然有值：磁盘 generation 变了就是竞争，不做任何"那就覆盖吧"的兜底。
            if (expectedGeneration!.Value != document.Generation)
                return AutomationPersistenceResult<AutomationPersistenceWriteResult>.Fail(
                    AutomationPersistenceErrorCode.GenerationConflict,
                    $"预期 generation {expectedGeneration.Value}，磁盘实际 {document.Generation}。写入被拒绝（不 last-writer-wins），请重新读取后再提交。");

            if (!mutator(document))
                return AutomationPersistenceResult<AutomationPersistenceWriteResult>.Ok(
                    new AutomationPersistenceWriteResult(document.Generation, BytesOf(path)));

            document.Generation = checked(document.Generation + 1);
            document.UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (document.ValidateForWrite(_limits) is { } contentError)
                return AutomationPersistenceResult<AutomationPersistenceWriteResult>.Fail(contentError);

            var bytes = document.ToUtf8();
            if (bytes.LongLength > _limits.MaxFileUtf8Bytes)
                return AutomationPersistenceResult<AutomationPersistenceWriteResult>.Fail(
                    AutomationPersistenceErrorCode.Oversize, $"序列化后 {bytes.LongLength} 字节超过上限 {_limits.MaxFileUtf8Bytes}，未写入");

            try
            {
                tempPath = _io.WriteTempFile(StoreDirectory, scope.ScopeHash, bytes);
            }
            catch (Exception tempError)
            {
                return AutomationPersistenceResult<AutomationPersistenceWriteResult>.Fail(
                    AutomationPersistenceErrorCode.TempWriteFailed, "临时文件未能落盘，目标文件原字节不动：" + tempError.Message);
            }

            _io.Publish(tempPath, path);
            tempPath = null;
            return AutomationPersistenceResult<AutomationPersistenceWriteResult>.Ok(
                new AutomationPersistenceWriteResult(document.Generation, bytes.LongLength));
        }
        catch (IOException ioError)
        {
            return AutomationPersistenceResult<AutomationPersistenceWriteResult>.Fail(
                AutomationPersistenceErrorCode.PublishFailed, "原子发布失败，原文件字节保持不动：" + ioError.Message);
        }
        catch (UnauthorizedAccessException accessError)
        {
            return AutomationPersistenceResult<AutomationPersistenceWriteResult>.Fail(
                AutomationPersistenceErrorCode.PublishFailed, "目录或文件权限不足，原文件字节保持不动：" + accessError.Message);
        }
        catch (AutomationPersistenceRejectException rejected)
        {
            // mutator 明确拒绝（重复 ID / 未知 ID / 非法状态推进）：不写盘，原字节不动。
            return AutomationPersistenceResult<AutomationPersistenceWriteResult>.Fail(rejected.Code, rejected.Message);
        }
        catch (Exception error)
        {
            return AutomationPersistenceResult<AutomationPersistenceWriteResult>.Fail(
                AutomationPersistenceErrorCode.PublishFailed, "写入中止，原文件字节保持不动：" + error.Message);
        }
        finally
        {
            if (tempPath is not null) _io.DeleteQuietly(tempPath);
            handle?.Dispose();
        }
    }

    /// <summary>独占写入锁等待上限；超时即明示竞争，不排队不覆盖。</summary>
    public int LockTimeoutMs { get; init; } = 2_000;

    private AutomationPersistenceResult<AutomationPersistenceDocument> LoadDocument(AutomationPersistenceScope scope, string path)
    {
        byte[] bytes;
        try
        {
            bytes = _io.ReadAllBytes(path);
        }
        catch (Exception error)
        {
            return AutomationPersistenceResult<AutomationPersistenceDocument>.Fail(
                AutomationPersistenceErrorCode.ReadFailed, "无法读取 scope 文件（原文件不动）：" + error.Message);
        }
        return AutomationPersistenceDocument.TryParse(bytes, scope, _limits);
    }

    private bool Prepare(AutomationPersistenceScope scope, bool forWrite, out AutomationPersistenceFailure error)
    {
        error = new AutomationPersistenceFailure(AutomationPersistenceErrorCode.RootUnavailable, "未就绪");
        if (!scope.IsBound)
        {
            error = new(AutomationPersistenceErrorCode.InvalidArgument, "scope 未绑定");
            return false;
        }

        // 数据根自身是重解析点（junction/符号链接）时拒绝：否则「固定目录」会随链接落到任意位置。
        // 数据根尚不存在不算不安全：写路径下一步会创建它，读路径等价于「该 scope 还没有文件」。
        if (AutomationPersistencePathGuard.ReparseIssue(DataRoot) is { } rootIssue)
        {
            error = rootIssue;
            return false;
        }

        // 只有写入才创建固定目录；读取保持「不产生磁盘副作用」。
        if (forWrite)
        {
            try
            {
                _io.EnsureDirectory(StoreDirectory);
            }
            catch (Exception directoryError)
            {
                error = new(AutomationPersistenceErrorCode.RootUnavailable, "无法创建本模块固定目录：" + directoryError.Message);
                return false;
            }
        }

        // 目标文件与写入锁文件都必须落在固定目录内，且从目录到目标的每一级都不是重解析点；
        // 路径逃逸与链接跟随在这里被拒绝，不靠"后面会检查"兜底。
        if (AutomationPersistencePathGuard.CheckUse(PathOf(scope), StoreDirectory) is { } pathIssue)
        {
            error = pathIssue;
            return false;
        }
        if (AutomationPersistencePathGuard.CheckUse(LockPathOf(scope), StoreDirectory) is { } lockIssue)
        {
            error = lockIssue;
            return false;
        }
        error = default!;
        return true;
    }

    private long BytesOf(string path)
    {
        try { return new FileInfo(path).Length; } catch (Exception) { return 0; }
    }
}
