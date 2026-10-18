using System;
using System.IO;
using System.Threading;

namespace Fufucode.App.Features.Automation.Persistence;

/// <summary>
/// 本模块唯一的磁盘访问边界。默认实现只碰固定目录内自己的文件；测试可注入失败
/// （真实文件锁、权限、发布失败）来验证「原子失败保原字节」，不为凑绿放宽断言。
/// </summary>
public interface IAutomationPersistenceFileIo
{
    /// <summary>目录存在则不创建；不存在则创建。失败抛 <see cref="IOException"/>。</summary>
    void EnsureDirectory(string directoryPath);

    bool FileExists(string filePath);

    /// <summary>按 <see cref="AutomationPersistenceReadPolicy.ReaderShareMode"/> 读取整份文件。</summary>
    byte[] ReadAllBytes(string filePath);

    /// <summary>
    /// 取得 scope 写入锁（同一 scope 串行化的真实跨进程锁）。锁被占用时在 <paramref name="timeoutMs"/>
    /// 内等待后返回 false —— 调用方必须把冲突明示给上层，绝不退化成 last-writer-wins。
    /// </summary>
    bool TryAcquireWriteLock(string lockFilePath, int timeoutMs, out IAutomationPersistenceLock? handle);

    /// <summary>在同一目录创建临时文件并写入全部内容、Flush(true) 落盘后关闭，返回临时文件路径。</summary>
    string WriteTempFile(string directoryPath, string fileNameHint, byte[] content);

    /// <summary>
    /// 把临时文件原子发布为目标文件：目标已存在时用 <see cref="File.Replace(string, string, string)"/>，
    /// 不存在时用 <see cref="File.Move(string, string, bool)"/>。失败抛异常，目标保持原字节。
    /// </summary>
    void Publish(string tempFilePath, string targetFilePath);

    /// <summary>尽力删除临时/锁附属文件；失败静默（不影响目标文件的保真）。</summary>
    void DeleteQuietly(string filePath);
}

/// <summary>写入锁句柄，Dispose 即释放。</summary>
public interface IAutomationPersistenceLock : IDisposable
{
}

/// <summary>
/// 本模块声明的读取共享策略：读文件时以 <see cref="ReaderShareMode"/> 打开，因此并发读者互不阻塞，
/// 写方也能在读者持句柄期间原子发布；读者保留它打开时刻的旧字节视图（Windows 语义）。
/// 测试与 M 的接线都按这一声明断言，而不是按实现细节猜测。
/// </summary>
public static class AutomationPersistenceReadPolicy
{
    public const FileShare ReaderShareMode = FileShare.ReadWrite | FileShare.Delete;
}

/// <summary>默认的本地文件实现。</summary>
public sealed class AutomationPersistenceFileIo : IAutomationPersistenceFileIo
{
    public void EnsureDirectory(string directoryPath) => Directory.CreateDirectory(directoryPath);

    public bool FileExists(string filePath) => File.Exists(filePath);

    public byte[] ReadAllBytes(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            AutomationPersistenceReadPolicy.ReaderShareMode);
        var buffer = new byte[stream.Length];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read <= 0) break;
            total += read;
        }
        return total == buffer.Length ? buffer : buffer.AsSpan(0, total).ToArray();
    }

    public bool TryAcquireWriteLock(string lockFilePath, int timeoutMs, out IAutomationPersistenceLock? handle)
    {
        handle = null;
        var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        while (true)
        {
            try
            {
                // 独占打开锁文件：FileShare.None 让同 scope 的其它实例/进程立刻失败。
                var stream = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
                handle = new FileLockHandle(stream);
                return true;
            }
            catch (IOException)
            {
                if (Environment.TickCount64 >= deadline) return false;
                Thread.Sleep(10);
            }
            catch (UnauthorizedAccessException)
            {
                if (Environment.TickCount64 >= deadline) return false;
                Thread.Sleep(10);
            }
        }
    }

    public string WriteTempFile(string directoryPath, string fileNameHint, byte[] content)
    {
        var tempPath = Path.Combine(directoryPath, fileNameHint + ".tmp-" + Guid.NewGuid().ToString("N") + ".json");
        using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }
        return tempPath;
    }

    public void Publish(string tempFilePath, string targetFilePath)
    {
        if (File.Exists(targetFilePath))
        {
            File.Replace(tempFilePath, targetFilePath, destinationBackupFileName: null);
            return;
        }

        try
        {
            File.Move(tempFilePath, targetFilePath, overwrite: false);
        }
        catch (IOException)
        {
            // 目标可能在检查后被其它发布者创建：此时改用 Replace 保持原子覆盖语义。
            if (File.Exists(targetFilePath)) File.Replace(tempFilePath, targetFilePath, destinationBackupFileName: null);
            else throw;
        }
    }

    public void DeleteQuietly(string filePath)
    {
        try { File.Delete(filePath); } catch (Exception) { /* 附属文件失败不影响目标保真 */ }
    }

    private sealed class FileLockHandle(FileStream stream) : IAutomationPersistenceLock
    {
        public void Dispose()
        {
            try { stream.Dispose(); } catch (Exception) { /* 释放失败不再重试 */ }
        }
    }
}
