using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.App.Features.Storage;

/// <summary>
/// 清理预览与实际执行。安全边界（发现、预览、执行三处同一套规则）：
/// <list type="bullet">
/// <item>不跟随重解析点（junction / 目录或文件符号链接），也不删除链接目标；目标自身、祖先链与子层
/// 出现链接一律跳过并如实报告。字符串前缀不当作实际落点证明，属性**读不出来**也按不安全处理；</item>
/// <item>目录目标只清空预览时存在的条目，且每个条目在执行前都复核「从区域根到该条目」的完整祖先链，
/// 并在当前重新扫描中确认仍可安全到达；预览后被替换为链接或不可读的旧路径不再删除；</item>
/// <item>文件目标在执行时重新核对长度/最后写入时间，日志还要重新核对保留期限；旧预览不会删除已变化
/// 或不再过期的文件；</item>
/// <item>被占用/无权限的条目逐个记录失败原因并继续其余目标；</item>
/// <item>不做任何数据根搬迁（移动/复制数据），只删除明确目标。</item>
/// </list>
/// </summary>
public sealed class StorageCleaner
{
    private readonly StorageCatalog _catalog;
    private readonly StorageUsageMeter _meter;

    public StorageCleaner(StorageCatalog catalog, StorageRetentionPolicy policy, StorageUsageMeter? meter = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(policy);
        _catalog = catalog;
        Policy = policy;
        _meter = meter ?? new StorageUsageMeter();
    }

    public StorageRetentionPolicy Policy { get; }

    /// <summary>默认清理范围：可重建缓存 + 过期日志。浏览器登录/站点数据（含离线存储）必须显式加入。</summary>
    public static IReadOnlyList<StorageCleanupScope> DefaultScopes { get; } = new[]
    {
        StorageCleanupScope.RebuildableCache,
        StorageCleanupScope.ExpiredLogs,
    };

    public Task<StorageCleanupPreview> BuildPreviewAsync(
        IReadOnlyCollection<StorageCleanupScope> scopes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        return Task.Run(() => BuildPreview(scopes, cancellationToken), cancellationToken);
    }

    public Task<StorageCleanupResult> CleanAsync(
        StorageCleanupPreview preview,
        IProgress<StorageCleanupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return Task.Run(() => Clean(preview, progress, cancellationToken), cancellationToken);
    }

    /// <summary>构建预览（同步实现，供后台线程与定向测试直接调用）。</summary>
    internal StorageCleanupPreview BuildPreview(IReadOnlyCollection<StorageCleanupScope> scopes, CancellationToken cancellationToken)
    {
        var normalized = Normalize(scopes);
        var warnings = new List<string>();
        var skipped = new List<StorageCleanupSkip>();
        var candidates = new List<Candidate>();
        var now = DateTime.UtcNow;

        if (normalized.Contains(StorageCleanupScope.RebuildableCache))
            candidates.AddRange(Discover(_catalog.BrowserArea.Id, StorageCatalog.BrowserCacheDirectoryNames,
                StorageCleanupScope.RebuildableCache, skipped, cancellationToken));
        if (normalized.Contains(StorageCleanupScope.BrowserSiteData))
            candidates.AddRange(Discover(_catalog.BrowserArea.Id, StorageCatalog.BrowserSiteDataNames,
                StorageCleanupScope.BrowserSiteData, skipped, cancellationToken));
        if (normalized.Contains(StorageCleanupScope.ExpiredLogs))
            candidates.AddRange(DiscoverExpiredLogs(skipped, now, cancellationToken));

        var accepted = Deduplicate(candidates);
        var targets = new List<StorageCleanupTarget>(accepted.Count);
        foreach (var candidate in accepted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = AllowedRoot(candidate.AreaId);
            if (root is null || !StoragePathGuard.IsUnder(candidate.Path, root) || StoragePathGuard.SamePath(candidate.Path, root))
            {
                skipped.Add(new StorageCleanupSkip(candidate.Path, StorageCleanupSkipKind.OutOfArea, "目标不在允许清理的区域内，未纳入清理"));
                continue;
            }

            var chain = StoragePathGuard.CheckChain(candidate.Path, root);
            if (!chain.IsSafe)
            {
                skipped.Add(new StorageCleanupSkip(chain.Path, SkipKind(chain), Reason(chain, "预览时路径不可安全到达，未纳入清理")));
                continue;
            }

            if (candidate.IsDirectory)
            {
                // 一次扫描同时得到快照与目录内链接/不可读条目的跳过项（扫描自身记录，不重复报）。
                var scan = ScanDirectory(candidate.Path, skipped, cancellationToken);
                if (!scan.Exists)
                {
                    skipped.Add(new StorageCleanupSkip(candidate.Path, StorageCleanupSkipKind.Missing, "预览时目录已不存在"));
                    continue;
                }
                targets.Add(new StorageCleanupTarget(candidate.AreaId, candidate.Path, candidate.Scope, true,
                    StorageTargetSnapshot.ForDirectory(scan.Bytes, scan.Files.Count, scan.Files, scan.Directories)));
            }
            else
            {
                try
                {
                    var info = new FileInfo(candidate.Path);
                    targets.Add(new StorageCleanupTarget(candidate.AreaId, candidate.Path, candidate.Scope, false,
                        StorageTargetSnapshot.ForFile(info.Length, info.LastWriteTimeUtc)));
                }
                catch (Exception error)
                {
                    skipped.Add(new StorageCleanupSkip(candidate.Path, StorageCleanupSkipKind.Unreadable,
                        "预览时无法读取该文件属性，未纳入清理：" + error.Message));
                }
            }
        }

        if (normalized.Count == 0) warnings.Add("未选择任何清理范围。");
        if (targets.Count == 0 && normalized.Count > 0) warnings.Add("所选范围内没有可清理的目标。");
        if (skipped.Count > 0) warnings.Add($"有 {skipped.Count} 个条目被跳过（链接、不可读、越界或已缺失），详情见跳过清单。");
        if (skipped.Any(item => item.Kind == StorageCleanupSkipKind.Unreadable))
            warnings.Add("存在无法读取属性的条目：按不可安全删除处理，本次不会删除它们。");
        if (targets.Any(target => target.Scope != StorageCleanupScope.ExpiredLogs))
            warnings.Add("浏览器可能正在运行：被占用的文件会跳过并如实记录，不会强行删除。");
        if (targets.Any(target => target.Scope == StorageCleanupScope.BrowserSiteData) ||
            skipped.Any(item => item.Kind == StorageCleanupSkipKind.ReparsePoint && item.Path.Contains("Service Worker", StringComparison.OrdinalIgnoreCase)))
            warnings.Add("清理浏览器登录/站点数据：相关网站的登录状态会失效，Service Worker 离线内容需要重新下载；模型认证在 Pi 配置目录，不受影响。");
        warnings.Add("预览后发生变化的目标（重新写入、新增内容、变为链接或不再过期）会保留并如实报告，不会按旧预览删除。");

        var affected = targets.Select(target => target.AreaId).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        return new StorageCleanupPreview(
            normalized,
            targets,
            affected,
            targets.Sum(target => target.Snapshot.Bytes),
            targets.Sum(target => target.Snapshot.FileCount),
            warnings,
            skipped,
            DateTime.UtcNow);
    }

    /// <summary>执行预览中的目标（同步实现，供后台线程与定向测试直接调用）。</summary>
    internal StorageCleanupResult Clean(
        StorageCleanupPreview preview,
        IProgress<StorageCleanupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var now = DateTime.UtcNow;
        var bytesBefore = MeasureAreas(preview.AffectedAreaIds, cancellationToken);

        var failures = new List<StorageCleanupFailure>();
        // 预览阶段跳过的条目继续作为「未删除」的事实保留在结果里，避免执行后原因消失。
        var skipped = new List<StorageCleanupSkip>(preview.Skipped);
        var counters = new Counters();
        var canceled = false;
        var completedTargets = 0;

        for (var index = 0; index < preview.Targets.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                break;
            }
            var target = preview.Targets[index];
            progress?.Report(new StorageCleanupProgress(completedTargets, preview.Targets.Count, target.Path));

            var root = AllowedRoot(target.AreaId);
            if (root is null || !StoragePathGuard.IsUnder(target.Path, root) || StoragePathGuard.SamePath(target.Path, root))
            {
                AddSkip(skipped, new StorageCleanupSkip(target.Path, StorageCleanupSkipKind.OutOfArea, "目标不在允许清理的区域内，未清理"));
                completedTargets++;
                continue;
            }

            // 每个目标一份带记忆的祖先链守卫：从区域根到该目标/每个快照条目逐级复核，读不出属性即不安全。
            var guard = new StoragePathGuard.ChainGuard(root);
            var chain = guard.Check(target.Path);
            if (!chain.IsSafe)
            {
                AddSkip(skipped, new StorageCleanupSkip(chain.Path, SkipKind(chain),
                    Reason(chain, "目标或祖先当前不可安全到达，未清理（如需清理请重新预览）")));
                completedTargets++;
                continue;
            }

            try
            {
                if (target.IsDirectory) CleanDirectoryTarget(target, guard, skipped, failures, counters, cancellationToken, ref canceled);
                else CleanFileTarget(target, guard, skipped, failures, counters, now);
            }
            catch (OperationCanceledException)
            {
                // 取消发生在目标处理过程中：已完成的删除保留，结果如实标成未完成，不抛给调用方。
                canceled = true;
            }
            if (canceled) break;
            completedTargets++;
        }

        // 取消后仍要如实给出清理前后的占用；此时不再响应取消，避免结果缺失。
        var bytesAfter = MeasureAreas(preview.AffectedAreaIds, CancellationToken.None);
        return new StorageCleanupResult(
            preview.Scopes,
            bytesBefore,
            bytesAfter,
            counters.Files,
            counters.Directories,
            counters.Bytes,
            skipped,
            failures,
            canceled,
            Completed: !canceled,
            // 被取消时停在中间的目标不算「已处理」，结果里如实反映剩余量。
            preview.Targets.Count - completedTargets,
            started,
            DateTime.UtcNow);
    }

    // ── 发现 ─────────────────────────────────────────────────────────────

    private List<Candidate> Discover(
        string areaId,
        IReadOnlyList<string> names,
        StorageCleanupScope scope,
        List<StorageCleanupSkip> skipped,
        CancellationToken cancellationToken)
    {
        var results = new List<Candidate>();
        var root = AllowedRoot(areaId);
        if (root is null) return results;

        var rootCheck = StoragePathGuard.CheckElement(root);
        if (rootCheck.Safety == StoragePathSafety.Missing) return results;
        if (!rootCheck.IsSafe)
        {
            skipped.Add(new StorageCleanupSkip(root, SkipKind(rootCheck), Reason(rootCheck, "区域根不可安全到达，本次不清理该区域")));
            return results;
        }

        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();

            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(current);
                directories = Directory.GetDirectories(current);
            }
            catch (Exception error) when (StorageUsageMeter.IsRecoverable(error))
            {
                skipped.Add(new StorageCleanupSkip(current, StorageCleanupSkipKind.Unreadable, "目录无法读取，未跟随：" + error.Message));
                continue;
            }

            foreach (var directory in directories)
            {
                var matched = ContainsName(names, Path.GetFileName(directory));
                if (StoragePathGuard.IsReparsePoint(directory))
                {
                    // 不跟随链接：既不进入，也不把链接当作清理目标（含命中缓存名的情况）。
                    skipped.Add(new StorageCleanupSkip(directory, StorageCleanupSkipKind.ReparsePoint,
                        matched ? "发现时为链接，未跟随、未纳入清理" : "发现时为链接，未跟随"));
                    continue;
                }
                if (matched)
                {
                    results.Add(new Candidate(directory, true, scope, areaId));
                    continue; // 已命中：不再向下扫，避免生成被父目标覆盖的嵌套目标。
                }
                stack.Push(directory);
            }
            foreach (var file in files)
            {
                var matched = ContainsName(names, Path.GetFileName(file));
                var check = StoragePathGuard.CheckElement(file);
                if (check.Safety == StoragePathSafety.Link)
                {
                    skipped.Add(new StorageCleanupSkip(file, StorageCleanupSkipKind.ReparsePoint,
                        matched ? "发现时为链接，未跟随、未纳入清理" : "发现时为链接，未跟随"));
                    continue;
                }
                if (!check.IsSafe)
                {
                    skipped.Add(new StorageCleanupSkip(file, SkipKind(check), Reason(check, "发现时无法确认安全")));
                    continue;
                }
                if (matched) results.Add(new Candidate(file, false, scope, areaId));
            }
        }
        return results;
    }

    private List<Candidate> DiscoverExpiredLogs(List<StorageCleanupSkip> skipped, DateTime now, CancellationToken cancellationToken)
    {
        var results = new List<Candidate>();
        var area = _catalog.LogsArea;
        if (!Directory.Exists(area.Path)) return results;
        foreach (var pattern in StorageCatalog.LogFilePatterns)
        {
            foreach (var file in EnumerateFilesSafe(area.Path, pattern, cancellationToken))
            {
                var check = StoragePathGuard.CheckElement(file);
                if (check.Safety == StoragePathSafety.Link)
                {
                    skipped.Add(new StorageCleanupSkip(file, StorageCleanupSkipKind.ReparsePoint, "日志文件是链接，未跟随也未纳入清理"));
                    continue;
                }
                if (!check.IsSafe)
                {
                    skipped.Add(new StorageCleanupSkip(file, SkipKind(check), Reason(check, "日志文件无法确认安全")));
                    continue;
                }
                DateTime lastWrite;
                try
                {
                    lastWrite = File.GetLastWriteTimeUtc(file);
                }
                catch (Exception error) when (StorageUsageMeter.IsRecoverable(error))
                {
                    skipped.Add(new StorageCleanupSkip(file, StorageCleanupSkipKind.Unreadable, "日志文件时间无法读取：" + error.Message));
                    continue;
                }
                if (!Policy.IsExpired(lastWrite, now)) continue;
                results.Add(new Candidate(file, false, StorageCleanupScope.ExpiredLogs, area.Id));
            }
        }
        return results;
    }

    private static List<Candidate> Deduplicate(List<Candidate> candidates)
    {
        var ordered = candidates
            .OrderBy(candidate => candidate.Path.Length)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var accepted = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in ordered)
        {
            if (!seen.Add(candidate.Path)) continue;
            // 已被接受的目标目录覆盖的嵌套目标不重复清理。
            if (accepted.Any(entry => entry.IsDirectory && StoragePathGuard.IsUnder(candidate.Path, entry.Path))) continue;
            accepted.Add(candidate);
        }
        return accepted;
    }

    // ── 目录快照 ─────────────────────────────────────────────────────────

    private readonly record struct DirectoryScan(
        bool Exists,
        long Bytes,
        List<string> Files,
        List<string> Directories,
        List<string> Links);

    /// <summary>
    /// 走查目录并记录相对文件/目录清单；链接与属性不可读的条目都不进入清单。
    /// <paramref name="skipped"/> 为 null 时只取清单，不逐项记录原因（执行阶段的重扫，原因已由预览记录）；
    /// 非 null 时把链接/不可读条目作为跳过项报告（预览阶段调用，再由一次预算扫描确认收敛）。
    /// </summary>
    private static DirectoryScan ScanDirectory(string path, List<StorageCleanupSkip>? skipped, CancellationToken cancellationToken)
    {
        var files = new List<string>();
        var directories = new List<string>();
        var links = new List<string>();
        long bytes = 0;
        if (!Directory.Exists(path)) return new DirectoryScan(false, 0, files, directories, links);

        var stack = new Stack<string>();
        stack.Push(string.Empty);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = stack.Pop();
            var current = relative.Length == 0 ? path : Path.Combine(path, relative);
            string[] entries;
            string[] children;
            try
            {
                entries = Directory.GetFiles(current);
                children = Directory.GetDirectories(current);
            }
            catch (Exception error) when (StorageUsageMeter.IsRecoverable(error))
            {
                skipped?.Add(new StorageCleanupSkip(current, StorageCleanupSkipKind.Unreadable, "目录无法读取，未纳入清理：" + error.Message));
                continue;
            }

            foreach (var child in children)
            {
                var childRelative = Combine(relative, Path.GetFileName(child));
                var check = StoragePathGuard.CheckElement(child);
                if (check.Safety == StoragePathSafety.Link)
                {
                    links.Add(childRelative);
                    skipped?.Add(new StorageCleanupSkip(child, StorageCleanupSkipKind.ReparsePoint,
                        "子目录是链接，未跟随、未纳入清理"));
                    continue;
                }
                if (!check.IsSafe)
                {
                    skipped?.Add(new StorageCleanupSkip(child, SkipKind(check), Reason(check, "子目录无法确认安全，未纳入清理")));
                    continue;
                }
                directories.Add(childRelative);
                stack.Push(childRelative);
            }
            foreach (var file in entries)
            {
                var fileRelative = Combine(relative, Path.GetFileName(file));
                var check = StoragePathGuard.CheckElement(file);
                if (check.Safety == StoragePathSafety.Link)
                {
                    links.Add(fileRelative);
                    skipped?.Add(new StorageCleanupSkip(file, StorageCleanupSkipKind.ReparsePoint,
                        "文件是链接，未跟随、未纳入清理"));
                    continue;
                }
                if (!check.IsSafe)
                {
                    skipped?.Add(new StorageCleanupSkip(file, SkipKind(check), Reason(check, "条目无法确认安全，未纳入清理")));
                    continue;
                }
                long length;
                try
                {
                    length = new FileInfo(file).Length;
                }
                catch (Exception error) when (StorageUsageMeter.IsRecoverable(error))
                {
                    skipped?.Add(new StorageCleanupSkip(file, StorageCleanupSkipKind.Unreadable, "文件大小无法读取，未纳入清理：" + error.Message));
                    continue;
                }
                files.Add(fileRelative);
                bytes += length;
            }
        }
        return new DirectoryScan(true, bytes, files, directories, links);
    }

    private static string Combine(string relative, string name) =>
        relative.Length == 0 ? name : Path.Combine(relative, name);

    // ── 执行 ─────────────────────────────────────────────────────────────

    private void CleanDirectoryTarget(
        StorageCleanupTarget target,
        StoragePathGuard.ChainGuard guard,
        List<StorageCleanupSkip> skipped,
        List<StorageCleanupFailure> failures,
        Counters counters,
        CancellationToken cancellationToken,
        ref bool canceled)
    {
        if (guard.Check(target.Path) is { Safety: StoragePathSafety.Missing })
        {
            AddSkip(skipped, new StorageCleanupSkip(target.Path, StorageCleanupSkipKind.Missing, "执行时目录已不存在"));
            return;
        }

        // 当前重新扫描：列出此刻仍可安全到达的条目（链接与不可读条目已被排除）。
        var current = ScanDirectory(target.Path, skipped, cancellationToken);
        var currentFiles = new HashSet<string>(current.Files, StringComparer.OrdinalIgnoreCase);
        var plannedFiles = new HashSet<string>(target.Snapshot.Files, StringComparer.OrdinalIgnoreCase);
        var plannedDirectories = new HashSet<string>(target.Snapshot.Directories, StringComparer.OrdinalIgnoreCase);

        var missing = 0;
        var unreachable = 0;
        foreach (var relative in target.Snapshot.Files)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                return;
            }
            var full = Path.Combine(target.Path, relative);

            // ① 完整祖先链：从区域根逐级复核，读不出属性按不安全处理。
            var chain = guard.Check(full);
            if (chain.Safety == StoragePathSafety.Link)
            {
                AddSkip(skipped, new StorageCleanupSkip(chain.Path, StorageCleanupSkipKind.ReparsePoint,
                    "路径链上有链接，未删除链接目标"));
                continue;
            }
            if (chain.Safety == StoragePathSafety.Missing)
            {
                missing++;
                continue;
            }
            if (!chain.IsSafe)
            {
                AddSkip(skipped, new StorageCleanupSkip(chain.Path, StorageCleanupSkipKind.Unreadable,
                    Reason(chain, "无法确认路径安全，未删除")));
                continue;
            }

            // ② 当前扫描必须仍能安全到达该路径；否则不按旧快照删除。
            if (!currentFiles.Contains(relative))
            {
                unreachable++;
                continue;
            }

            var attributes = StoragePathGuard.CheckElement(full);
            if (!attributes.IsSafe)
            {
                AddSkip(skipped, new StorageCleanupSkip(full, SkipKind(attributes), Reason(attributes, "条目当前不可安全删除")));
                continue;
            }
            DeleteFile(full, counters, failures);
        }
        if (missing > 0)
            AddSkip(skipped, new StorageCleanupSkip(target.Path, StorageCleanupSkipKind.Missing, $"预览后 {missing} 个条目已不存在（未删除）"));
        if (unreachable > 0)
            AddSkip(skipped, new StorageCleanupSkip(target.Path, StorageCleanupSkipKind.ChangedSincePreview,
                $"旧快照中 {unreachable} 个条目当前扫描未能安全到达（可能经由链接或被替换），未删除"));

        // 预览后新增的文件/目录不纳入本次清理，如实报告而不是静默扩大删除范围。
        var newFiles = current.Files.Count(relative => !plannedFiles.Contains(relative));
        var newDirectories = current.Directories.Count(relative => !plannedDirectories.Contains(relative));
        if (newFiles > 0 || newDirectories > 0)
            AddSkip(skipped, new StorageCleanupSkip(target.Path, StorageCleanupSkipKind.ChangedSincePreview,
                $"预览后新增 {newFiles} 个文件 / {newDirectories} 个目录，未纳入本次清理（如需一并清理请重新预览）"));
        foreach (var relative in current.Links)
            AddSkip(skipped, new StorageCleanupSkip(Path.Combine(target.Path, relative), StorageCleanupSkipKind.ReparsePoint,
                "链接未跟随、未删除链接目标"));

        // 删除预览时存在的空目录（自深向浅）；链上有链接、属性不可读或仍非空时保留并如实报告。
        foreach (var relative in target.Snapshot.Directories.OrderByDescending(item => item.Length))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                return;
            }
            var full = Path.Combine(target.Path, relative);
            var chain = guard.Check(full);
            if (!chain.IsSafe)
            {
                AddSkip(skipped, new StorageCleanupSkip(chain.Path, SkipKind(chain), Reason(chain, "目录当前不可安全删除，未清空")));
                continue;
            }
            try
            {
                Directory.Delete(full, recursive: false);
                counters.Directories++;
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (Exception error) when (StorageUsageMeter.IsRecoverable(error))
            {
                AddSkip(skipped, new StorageCleanupSkip(full, StorageCleanupSkipKind.ChangedSincePreview,
                    "目录未清空（预览后新增内容或被占用），已保留：" + error.Message));
            }
        }
    }

    private void CleanFileTarget(
        StorageCleanupTarget target,
        StoragePathGuard.ChainGuard guard,
        List<StorageCleanupSkip> skipped,
        List<StorageCleanupFailure> failures,
        Counters counters,
        DateTime now)
    {
        var chain = guard.Check(target.Path);
        if (chain.Safety == StoragePathSafety.Missing)
        {
            AddSkip(skipped, new StorageCleanupSkip(target.Path, StorageCleanupSkipKind.Missing, "执行时文件已不存在"));
            return;
        }
        if (!chain.IsSafe)
        {
            AddSkip(skipped, new StorageCleanupSkip(chain.Path, SkipKind(chain), Reason(chain, "文件当前不可安全删除")));
            return;
        }

        FileAttributes attributes;
        FileInfo info;
        try
        {
            attributes = File.GetAttributes(target.Path);
            info = new FileInfo(target.Path);
            _ = info.Length;
        }
        catch (Exception error) when (StorageUsageMeter.IsRecoverable(error))
        {
            AddSkip(skipped, new StorageCleanupSkip(target.Path, StorageCleanupSkipKind.Unreadable, "执行时无法读取该文件：" + error.Message));
            return;
        }
        if ((attributes & FileAttributes.Directory) != 0)
        {
            AddSkip(skipped, new StorageCleanupSkip(target.Path, StorageCleanupSkipKind.ChangedSincePreview, "预览后由文件变为目录，未按旧预览删除"));
            return;
        }

        if (target.Snapshot.Length is { } plannedLength && info.Length != plannedLength)
        {
            AddSkip(skipped, new StorageCleanupSkip(target.Path, StorageCleanupSkipKind.ChangedSincePreview,
                $"预览后长度已变化（{plannedLength} → {info.Length}），未按旧预览删除"));
            return;
        }
        if (target.Snapshot.LastWriteUtc is { } plannedWrite && info.LastWriteTimeUtc != plannedWrite)
        {
            AddSkip(skipped, new StorageCleanupSkip(target.Path, StorageCleanupSkipKind.ChangedSincePreview,
                $"预览后已被重新写入（{plannedWrite:u} → {info.LastWriteTimeUtc:u}），未按旧预览删除"));
            return;
        }
        if (target.Scope == StorageCleanupScope.ExpiredLogs && !Policy.IsExpired(info.LastWriteTimeUtc, now))
        {
            AddSkip(skipped, new StorageCleanupSkip(target.Path, StorageCleanupSkipKind.NotExpired,
                $"执行时已不再超过保留期限（保留 {Policy.LogRetentionDays} 天），已保留；如需清理请重新预览"));
            return;
        }
        DeleteFile(target.Path, counters, failures);
    }

    private static void DeleteFile(string path, Counters counters, List<StorageCleanupFailure> failures)
    {
        try
        {
            var length = new FileInfo(path).Length;
            File.Delete(path);
            counters.Files++;
            counters.Bytes += length;
        }
        catch (FileNotFoundException)
        {
            // 已被外部删除：既不算失败也不计入清理量。
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception error) when (StorageUsageMeter.IsRecoverable(error))
        {
            failures.Add(new StorageCleanupFailure(path, Describe(error)));
        }
    }

    private static void AddSkip(List<StorageCleanupSkip> skipped, StorageCleanupSkip skip)
    {
        if (skipped.Any(item => item.Kind == skip.Kind && string.Equals(item.Path, skip.Path, StringComparison.OrdinalIgnoreCase)))
            return;
        skipped.Add(skip);
    }

    // ── 组合与辅助 ───────────────────────────────────────────────────────

    /// <summary>允许清理的区域根：只有非保护区域才允许，返回 null 表示该区域不参与清理。</summary>
    private string? AllowedRoot(string areaId)
    {
        if (_catalog.FindArea(areaId) is not { } area) return null;
        return area.IsProtected ? null : area.Path;
    }

    private long MeasureAreas(IReadOnlyList<string> areaIds, CancellationToken cancellationToken)
    {
        long total = 0;
        foreach (var id in areaIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_catalog.FindArea(id) is not { } area) continue;
            var excluded = area.Kind == StorageAreaKind.AppRoot ? _catalog.RootExcludedDirectoryNames : null;
            total += _meter.Meter(area.Path, null, cancellationToken, excluded).Bytes;
        }
        return total;
    }

    private static IReadOnlyCollection<StorageCleanupScope> Normalize(IReadOnlyCollection<StorageCleanupScope> scopes)
    {
        var set = new HashSet<StorageCleanupScope>(scopes);
        return new[] { StorageCleanupScope.RebuildableCache, StorageCleanupScope.ExpiredLogs, StorageCleanupScope.BrowserSiteData }
            .Where(set.Contains)
            .ToArray();
    }

    private static string[] EnumerateFilesSafe(string root, string pattern, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Directory.GetFiles(root, pattern);
        }
        catch (Exception error) when (StorageUsageMeter.IsRecoverable(error))
        {
            return [];
        }
    }

    private static bool ContainsName(IReadOnlyList<string> names, string name)
    {
        for (var index = 0; index < names.Count; index++)
            if (string.Equals(names[index], name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static StorageCleanupSkipKind SkipKind(StoragePathCheck check) => check.Safety switch
    {
        StoragePathSafety.Link => StorageCleanupSkipKind.ReparsePoint,
        StoragePathSafety.Missing => StorageCleanupSkipKind.Missing,
        _ => StorageCleanupSkipKind.Unreadable,
    };

    private static string Reason(StoragePathCheck check, string prefix) => check.Safety switch
    {
        StoragePathSafety.Link => $"{prefix}：路径链上有链接（{check.Path}），未跟随、未删除链接目标",
        StoragePathSafety.Missing => $"{prefix}：条目已不存在（{check.Path}）",
        _ => $"{prefix}：{check.Reason ?? "无法读取属性"}（{check.Path}）",
    };

    private static string Describe(Exception error) => error switch
    {
        UnauthorizedAccessException => "无访问权限：" + error.Message,
        IOException when error.HResult is 32 or 33 or unchecked((int)0x80070020) => "文件被占用：" + error.Message,
        _ => error.Message,
    };

    private readonly record struct Candidate(string Path, bool IsDirectory, StorageCleanupScope Scope, string AreaId);

    private sealed class Counters
    {
        public int Files { get; set; }
        public int Directories { get; set; }
        public long Bytes { get; set; }
    }
}
