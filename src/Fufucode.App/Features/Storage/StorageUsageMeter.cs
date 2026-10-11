using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.App.Features.Storage;

/// <summary>
/// 目录/文件占用计量。口径：
/// <list type="bullet">
/// <item>只读取文件系统元数据（长度），不打开文件内容，因此被其它进程占用的文件也能如实计量；</item>
/// <item>不跟随重解析点（符号链接/junction/挂载点），避免循环与跨卷重复计数，按「未统计」计数；</item>
/// <item>不可读条目计入 <see cref="StorageAreaUsage.SkippedEntries"/> 而不是静默当作 0。</item>
/// </list>
/// 计量可取消：取消通过 <see cref="OperationCanceledException"/> 传播，不返回部分结果。
/// </summary>
public sealed class StorageUsageMeter
{
    public const int DefaultProgressIntervalEntries = 256;

    private readonly int _progressInterval;

    public StorageUsageMeter(int progressIntervalEntries = DefaultProgressIntervalEntries)
        => _progressInterval = Math.Max(1, progressIntervalEntries);

    /// <summary>计量单个路径（文件或目录）。同步实现，供后台线程调用与定向测试直接调用。</summary>
    public StorageAreaUsage Meter(
        string path,
        IProgress<long>? bytesProgress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? excludedChildDirectoryNames = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (File.Exists(path))
        {
            try
            {
                var file = new FileInfo(path);
                return new StorageAreaUsage(path, true, file.Length, 1, 0, 0);
            }
            catch (Exception error) when (IsRecoverable(error))
            {
                return new StorageAreaUsage(path, true, 0, 0, 0, 1);
            }
        }
        if (!Directory.Exists(path)) return StorageAreaUsage.Missing(path);

        long bytes = 0, files = 0, directories = 0, skipped = 0;
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((path, 0));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (current, depth) = stack.Pop();

            IEnumerator<FileSystemInfo> entries;
            try
            {
                var info = new DirectoryInfo(current);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    skipped++;
                    continue;
                }
                directories++;
                entries = info.EnumerateFileSystemInfos().GetEnumerator();
            }
            catch (Exception error) when (IsRecoverable(error))
            {
                skipped++;
                continue;
            }

            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileSystemInfo entry;
                    try
                    {
                        if (!entries.MoveNext()) break;
                        entry = entries.Current;
                    }
                    catch (Exception error) when (IsRecoverable(error))
                    {
                        skipped++;
                        break;
                    }

                    FileAttributes attributes;
                    try
                    {
                        attributes = entry.Attributes;
                    }
                    catch (Exception error) when (IsRecoverable(error))
                    {
                        skipped++;
                        continue;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        // 链接目标可能指向卷外或形成循环：不跟随，按未统计计数。
                        skipped++;
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (depth == 0 && excludedChildDirectoryNames is not null &&
                            ContainsName(excludedChildDirectoryNames, entry.Name))
                        {
                            // 该子目录由自己的区域单独计量，这里不重复计入也不计为未统计。
                            continue;
                        }
                        stack.Push((entry.FullName, depth + 1));
                        continue;
                    }

                    long length;
                    try
                    {
                        length = ((FileInfo)entry).Length;
                    }
                    catch (Exception error) when (IsRecoverable(error))
                    {
                        skipped++;
                        continue;
                    }

                    bytes += length;
                    files++;
                    if (files % _progressInterval == 0) bytesProgress?.Report(bytes);
                }
            }
            finally
            {
                entries.Dispose();
            }
        }

        bytesProgress?.Report(bytes);
        return new StorageAreaUsage(path, true, bytes, files, directories, skipped);
    }

    /// <summary>按清单顺序计量全部区域，上报进度；完成前不发布结果。</summary>
    public Task<StorageMeasurement> MeasureAsync(
        StorageCatalog catalog,
        IProgress<StorageMeasurementProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return Task.Run(() =>
        {
            var measurements = new List<StorageAreaMeasurement>(catalog.Areas.Count);
            long total = 0;
            for (var index = 0; index < catalog.Areas.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var area = catalog.Areas[index];
                var excluded = area.Kind == StorageAreaKind.AppRoot ? catalog.RootExcludedDirectoryNames : null;
                var usage = Meter(area.Path, null, cancellationToken, excluded);
                measurements.Add(new StorageAreaMeasurement(area, usage));
                total += usage.Bytes;
                progress?.Report(new StorageMeasurementProgress(index + 1, catalog.Areas.Count, total, area.Title));
            }
            return new StorageMeasurement(measurements, DateTime.UtcNow, Completed: true);
        }, cancellationToken);
    }

    private static bool ContainsName(IReadOnlyCollection<string> names, string name)
    {
        foreach (var candidate in names)
            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>可恢复的文件系统错误（权限、占用、路径失效）：跳过并计数，不中断整体计量。</summary>
    internal static bool IsRecoverable(Exception error) =>
        error is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException;
}
