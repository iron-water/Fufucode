using System.Diagnostics;
using System.Security.Cryptography;
using Fufucode.App.Features.Storage;
using Fufucode.Core;

// O2 存储管理定向测试（独立入口，未注册到全量自测；注册属 Codex 集成范围）：
//   dotnet run --project tests/Fufucode.App.Storage.Tests
// 覆盖任务书 M2 验收点：真实占用与计量口径、清理预览与实际变化、
// 活跃文件失败不损坏其余数据、取消有效、保护范围（会话/配置/认证/资料/源码）保持不变。

var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var work = Path.Combine(repoRoot, "tmp", "o2-storage-tests", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
var passed = 0;
var skipped = new List<string>();

void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    passed++;
    Console.WriteLine("PASS " + name);
}

void Skip(string name)
{
    skipped.Add(name);
    Console.WriteLine("SKIP " + name);
}

string NewFile(string path, int size)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, new byte[size]);
    return path;
}

void SetAge(string path, int days) => File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-days));

string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

/// <summary>用 mklink /J 建立 junction（Windows 上不需要额外权限；符号链接才需要）。</summary>
bool MakeJunction(string linkPath, string targetPath)
{
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var process = Process.Start(start)!;
        process.WaitForExit(10_000);
        return process.ExitCode == 0 && Directory.Exists(linkPath);
    }
    catch (Exception error)
    {
        Console.WriteLine("junction 创建异常：" + error.Message);
        return false;
    }
}

Dictionary<string, long> Snapshot(params string[] roots)
{
    var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    foreach (var root in roots)
    {
        if (!Directory.Exists(root)) continue;
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            result[Path.GetFullPath(file)] = new FileInfo(file).Length;
    }
    return result;
}

string DescribeDiff(Dictionary<string, long> before, Dictionary<string, long> after)
{
    var changed = before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase)
        .Where(key => !before.TryGetValue(key, out var size) || !after.TryGetValue(key, out var now) || size != now)
        .Take(5).ToArray();
    return string.Join(", ", changed);
}

try
{
    // ── A. 合成数据根 ───────────────────────────────────────────────────
    var appRoot = Path.Combine(work, "app");
    var layout = new AppDataLayout(appRoot);
    var catalog = new StorageCatalog(layout, Path.Combine(appRoot, "webview"), Path.Combine(appRoot, "library"));

    long expectedTotal = 0;
    var expectedByArea = new Dictionary<string, long>(StringComparer.Ordinal);
    void Area(string id, long bytes) { expectedByArea[id] = bytes; expectedTotal += bytes; }

    NewFile(Path.Combine(appRoot, "settings.json"), 100);
    NewFile(Path.Combine(appRoot, "ui.json"), 50);
    NewFile(Path.Combine(appRoot, "keybindings.json"), 20);
    Area("app-root", 170);

    NewFile(Path.Combine(appRoot, "runtime", "pi", "runtime.bin"), 500); Area("runtime", 500);
    NewFile(Path.Combine(appRoot, "pi-agent", "auth.json"), 200); Area("pi-agent", 200);
    NewFile(Path.Combine(appRoot, "sessions", "p1", "s1.jsonl"), 1000);
    NewFile(Path.Combine(appRoot, "sessions", "p1", "s2.jsonl"), 2000);
    NewFile(Path.Combine(appRoot, "sessions", "p2", "s3.jsonl"), 3000); Area("sessions", 6000);
    NewFile(Path.Combine(appRoot, "exports", "diag.zip"), 300); Area("exports", 300);
    NewFile(Path.Combine(appRoot, "library", "library.db"), 400);
    NewFile(Path.Combine(appRoot, "library", "copies", "e1", "v1", "a.txt"), 128); Area("library", 528);

    var oldLog = NewFile(Path.Combine(appRoot, "logs", "app-old.jsonl"), 2048); SetAge(oldLog, 30);
    var oldLogFile = NewFile(Path.Combine(appRoot, "logs", "app-old.log"), 512); SetAge(oldLogFile, 40);
    var newLog = NewFile(Path.Combine(appRoot, "logs", "app-new.jsonl"), 1024);
    var nonLog = NewFile(Path.Combine(appRoot, "logs", "notes.txt"), 700); SetAge(nonLog, 40);
    Area("logs", 4284);

    var cacheShader = NewFile(Path.Combine(appRoot, "webview", "EBWebView", "GrShaderCache", "cache.bin"), 4096);
    var cacheShader2 = NewFile(Path.Combine(appRoot, "webview", "EBWebView", "ShaderCache", "shader.bin"), 2048);
    var cacheDefault = NewFile(Path.Combine(appRoot, "webview", "EBWebView", "Default", "Cache", "data.bin"), 8192);
    var cacheShaderDir = Path.Combine(appRoot, "webview", "EBWebView", "GrShaderCache");
    var cacheShaderDir2 = Path.Combine(appRoot, "webview", "EBWebView", "ShaderCache");
    var cacheDefaultDir = Path.Combine(appRoot, "webview", "EBWebView", "Default", "Cache");
    var cookies = NewFile(Path.Combine(appRoot, "webview", "EBWebView", "Default", "Cookies"), 2048);
    var history = NewFile(Path.Combine(appRoot, "webview", "EBWebView", "Default", "History"), 1024);
    NewFile(Path.Combine(appRoot, "webview", "EBWebView", "Default", "Preferences"), 256);
    var localStorage = NewFile(Path.Combine(appRoot, "webview", "EBWebView", "Default", "Local Storage", "leveldb", "x.ldb"), 512);
    var offlineCache = NewFile(Path.Combine(appRoot, "webview", "EBWebView", "Default", "Service Worker", "CacheStorage", "offline.bin"), 640);
    Area("webview", 18176 + 640);

    var meter = new StorageUsageMeter();
    var measurement = await meter.MeasureAsync(catalog);
    Check(measurement.Completed, "整根计量完成");
    Check(measurement.TotalBytes == expectedTotal, $"数据根合计与实际文件一致（{measurement.TotalBytes}/{expectedTotal}）");
    Check(measurement.TotalSkipped == 0, "正常目录没有未统计条目");
    foreach (var pair in expectedByArea)
        Check(measurement.Areas.Single(item => item.Area.Id == pair.Key).Usage.Bytes == pair.Value,
            $"区域占用与实际一致：{pair.Key}={pair.Value}");

    // 口径：根目录区域不重复计入被单独计量的子目录。
    Check(measurement.Areas.Single(item => item.Area.Id == "app-root").Usage.Bytes == 170,
        "根区域只统计根下文件，子目录由各自区域计量");
    Check(measurement.TotalFiles == Directory.GetFiles(appRoot, "*", SearchOption.AllDirectories).Length,
        "文件总数与实际文件数一致");

    var missingUsage = meter.Meter(Path.Combine(work, "不存在"));
    Check(!missingUsage.Exists && missingUsage.Bytes == 0, "不存在的路径如实报告为未计量");

    // ── B. 清理预览 ─────────────────────────────────────────────────────
    var cleaner = new StorageCleaner(catalog, new StorageRetentionPolicy(14), meter);
    var preview = await cleaner.BuildPreviewAsync(StorageCleaner.DefaultScopes);
    Check(preview.Scopes.Count == 2 && preview.Scopes.Contains(StorageCleanupScope.RebuildableCache) && preview.Scopes.Contains(StorageCleanupScope.ExpiredLogs),
        "默认范围只有可重建缓存与过期日志");
    Check(preview.Targets.Count == 5, $"默认预览目标数正确（{preview.Targets.Count}）");
    Check(preview.Targets.Any(target => target.Path == cacheShaderDir && target.IsDirectory), "缓存目录 GrShaderCache 是目标");
    Check(preview.Targets.Any(target => target.Path == cacheShaderDir2 && target.IsDirectory), "缓存目录 ShaderCache 是目标");
    Check(preview.Targets.Any(target => target.Path == cacheDefaultDir && target.IsDirectory), "缓存目录 Default/Cache 是目标");
    Check(preview.Targets.Any(target => target.Path == oldLog && !target.IsDirectory), "过期 jsonl 日志是目标");
    Check(preview.Targets.Any(target => target.Path == oldLogFile), "过期 .log 日志是目标");
    Check(!preview.Targets.Any(target => target.Path == newLog), "未过期日志不是目标");
    Check(!preview.Targets.Any(target => target.Path == nonLog), "日志目录中的非日志文件不是目标");
    Check(!preview.Targets.Any(target => target.Path == cookies || target.Path == history),
        "未选择站点数据范围时 cookie/历史不是目标");
    Check(!preview.Targets.Any(target => target.Path.StartsWith(Path.Combine(appRoot, "sessions"), StringComparison.OrdinalIgnoreCase)
        || target.Path.StartsWith(Path.Combine(appRoot, "pi-agent"), StringComparison.OrdinalIgnoreCase)
        || target.Path.StartsWith(Path.Combine(appRoot, "library"), StringComparison.OrdinalIgnoreCase)
        || target.Path.StartsWith(Path.Combine(appRoot, "runtime"), StringComparison.OrdinalIgnoreCase)
        || target.Path.StartsWith(Path.Combine(appRoot, "exports"), StringComparison.OrdinalIgnoreCase)),
        "会话/认证/资料/运行时/导出都不是清理目标");
    Check(preview.TotalBytes == 14336 + 2560, $"预览预计大小与目标实际大小一致（{preview.TotalBytes}）");
    Check(preview.TotalFiles == 5, "预览预计文件数与目标一致");
    Check(preview.AffectedAreaIds.SequenceEqual(new[] { "logs", "webview" }), "预览标明受影响区域");
    Check(preview.Warnings.Any(warning => warning.Contains("占用")), "预览提示浏览器占用风险");

    // ── C. 默认清理：实际变化 + 保护范围不变 ─────────────────────────────
    var protectedRoots = new[]
    {
        Path.Combine(appRoot, "sessions"), Path.Combine(appRoot, "pi-agent"), Path.Combine(appRoot, "runtime"),
        Path.Combine(appRoot, "exports"), Path.Combine(appRoot, "library"),
    };
    var protectedBefore = Snapshot(protectedRoots);
    var settingsBefore = Snapshot(appRoot).Where(pair => !pair.Key.Contains("webview", StringComparison.OrdinalIgnoreCase)
        && !pair.Key.Contains(Path.Combine(appRoot, "logs"), StringComparison.OrdinalIgnoreCase)).ToDictionary(pair => pair.Key, pair => pair.Value);

    var result = await cleaner.CleanAsync(preview);
    Check(result.Completed && !result.Canceled, "默认清理正常完成");
    Check(result.Failures.Count == 0, $"默认清理没有失败项（{string.Join(" | ", result.Failures.Select(f => f.Reason))}）");
    Check(result.DeletedFiles == 5 && result.DeletedBytes == 16896, $"删除量与预览一致（{result.DeletedFiles} 个文件 / {result.DeletedBytes} 字节）");
    Check(result.DeletedDirectories == 0, "目录目标只清空内容、不删除目录本身");
    Check(result.BytesBefore == 23100 && result.BytesAfter == 6204, $"清理前后占用有证据（{result.BytesBefore} → {result.BytesAfter}）");
    Check(result.BytesFreed == result.DeletedBytes, "释放量与删除量一致");
    Check(Directory.Exists(cacheShaderDir) && Directory.Exists(cacheShaderDir2) && Directory.Exists(cacheDefaultDir), "缓存目录本身保留");
    Check(Directory.GetFiles(cacheShaderDir, "*", SearchOption.AllDirectories).Length == 0
        && Directory.GetFiles(cacheDefaultDir, "*", SearchOption.AllDirectories).Length == 0, "缓存目录内容已清空");
    Check(!File.Exists(oldLog) && !File.Exists(oldLogFile), "过期日志已删除");
    Check(File.Exists(newLog) && File.Exists(nonLog), "未过期日志与非日志文件保留");
    Check(File.Exists(cookies) && File.Exists(history) && File.Exists(localStorage), "未选择的站点数据保留");
    Check(File.Exists(offlineCache), "默认清理不动 Service Worker 离线存储（CacheStorage）");
    Check(Snapshot(protectedRoots).Count == protectedBefore.Count
        && Snapshot(protectedRoots).All(pair => protectedBefore.TryGetValue(pair.Key, out var size) && size == pair.Value),
        "会话/认证/运行时/导出/资料逐字节不变：" + DescribeDiff(protectedBefore, Snapshot(protectedRoots)));
    var settingsAfter = Snapshot(appRoot).Where(pair => !pair.Key.Contains("webview", StringComparison.OrdinalIgnoreCase)
        && !pair.Key.Contains(Path.Combine(appRoot, "logs"), StringComparison.OrdinalIgnoreCase)).ToDictionary(pair => pair.Key, pair => pair.Value);
    Check(settingsBefore.Count == settingsAfter.Count
        && settingsAfter.All(pair => settingsBefore.TryGetValue(pair.Key, out var size) && size == pair.Value),
        "配置与数据库文件保持不变：" + DescribeDiff(settingsBefore, settingsAfter));

    // ── D. 活跃文件失败不损坏其余数据 ────────────────────────────────────
    var locked = NewFile(Path.Combine(appRoot, "webview", "EBWebView", "GrShaderCache", "locked.bin"), 500);
    NewFile(Path.Combine(appRoot, "webview", "EBWebView", "GrShaderCache", "free.bin"), 1000);
    var secondCacheFile = NewFile(Path.Combine(appRoot, "webview", "EBWebView", "Default", "Cache", "new2.bin"), 2000);
    var lockStream = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    try
    {
        var lockedPreview = await cleaner.BuildPreviewAsync(StorageCleaner.DefaultScopes);
        var lockedResult = await cleaner.CleanAsync(lockedPreview);
        Check(lockedResult.Completed, "有占用文件时清理仍然完成（不抛异常）");
        Check(lockedResult.Failures.Any(failure => failure.Reason.Contains("占用")), "占用文件被记录为失败并给出原因");
        Check(File.Exists(locked), "被占用的文件保留原样");
        Check(File.Exists(locked) && new FileInfo(locked).Length == 500, "被占用文件内容未被截断或改写");
        Check(!File.Exists(Path.Combine(appRoot, "webview", "EBWebView", "GrShaderCache", "free.bin")), "同一目录中未被占用的文件正常清理");
        Check(!File.Exists(secondCacheFile), "另一个缓存目录中的文件正常清理");
        Check(lockedResult.DeletedBytes == 3000, $"只统计真正删除的字节（{lockedResult.DeletedBytes}）");
        Check(lockedResult.BytesAfter == lockedResult.BytesBefore - lockedResult.DeletedBytes, "前后占用差等于实际删除量");
        Check(Directory.Exists(Path.Combine(appRoot, "webview", "EBWebView", "GrShaderCache")), "含占用文件的目录保留");
        Check(Snapshot(protectedRoots).All(pair => protectedBefore.TryGetValue(pair.Key, out var size) && size == pair.Value)
            && Snapshot(protectedRoots).Count == protectedBefore.Count, "占用失败未影响保护范围");
    }
    finally
    {
        lockStream.Dispose();
    }

    // ── E. 浏览器登录/站点数据范围 ──────────────────────────────────────
    var sitePreview = await cleaner.BuildPreviewAsync(new[] { StorageCleanupScope.BrowserSiteData });
    Check(sitePreview.Targets.Any(target => target.Path == cookies), "显式选择后 cookie 成为目标");
    Check(sitePreview.Targets.Any(target => target.Path == history), "显式选择后历史成为目标");
    Check(sitePreview.Targets.Any(target => target.Path == Path.Combine(appRoot, "webview", "EBWebView", "Default", "Local Storage")),
        "站点存储目录成为目标");
    Check(!sitePreview.Targets.Any(target => target.Path.EndsWith("Preferences", StringComparison.OrdinalIgnoreCase)), "浏览器 profile 配置不是目标");
    Check(sitePreview.Targets.All(target => target.AreaId == "webview"), "站点数据范围只涉及浏览器区域");
    Check(sitePreview.Warnings.Any(warning => warning.Contains("登录")) && sitePreview.Warnings.Any(warning => warning.Contains("模型认证")),
        "站点数据清理明确提示登录影响并与模型认证分开");
    Check(sitePreview.Targets.Any(target => target.Path == Path.Combine(appRoot, "webview", "EBWebView", "Default", "Service Worker")),
        "Service Worker 离线存储属于站点数据范围（需显式确认）");
    Check(sitePreview.Warnings.Any(warning => warning.Contains("离线")), "站点数据提示包含离线内容影响");

    var siteResult = await cleaner.CleanAsync(sitePreview);
    Check(siteResult.Completed && siteResult.DeletedFiles == 4, $"站点数据清理删除量与预览一致（{siteResult.DeletedFiles}）");
    Check(!File.Exists(cookies) && !File.Exists(history) && !File.Exists(localStorage) && !File.Exists(offlineCache),
        "站点数据与离线存储已删除（显式范围）");
    Check(File.Exists(Path.Combine(appRoot, "pi-agent", "auth.json")), "模型认证目录不受站点数据清理影响");
    Check(Snapshot(protectedRoots).All(pair => protectedBefore.TryGetValue(pair.Key, out var size) && size == pair.Value)
        && Snapshot(protectedRoots).Count == protectedBefore.Count, "站点数据清理未触碰保护范围");

    // ── F. 保留策略 ─────────────────────────────────────────────────────
    var ret50 = NewFile(Path.Combine(appRoot, "logs", "old-50d.jsonl"), 100); SetAge(ret50, 50);
    var ret40 = NewFile(Path.Combine(appRoot, "logs", "old-40d.jsonl"), 100); SetAge(ret40, 40);
    var ret10 = NewFile(Path.Combine(appRoot, "logs", "new-10d.jsonl"), 100); SetAge(ret10, 10);

    var logOnly = new[] { StorageCleanupScope.ExpiredLogs };
    var retention14 = await cleaner.BuildPreviewAsync(logOnly);
    Check(retention14.Targets.Any(target => target.Path == ret50) && retention14.Targets.Any(target => target.Path == ret40),
        "14 天保留：50/40 天前的日志是目标");
    Check(!retention14.Targets.Any(target => target.Path == ret10), "14 天保留：10 天前的日志保留");

    var cleaner60 = new StorageCleaner(catalog, new StorageRetentionPolicy(60), meter);
    var retention60 = await cleaner60.BuildPreviewAsync(logOnly);
    Check(retention60.Targets.Count == 0, "60 天保留：50 天前的日志不再纳入清理");
    Check(new StorageRetentionPolicy(0).LogRetentionDays == StorageRetentionPolicy.MinLogRetentionDays
        && new StorageRetentionPolicy(9999).LogRetentionDays == StorageRetentionPolicy.MaxLogRetentionDays,
        "保留天数越界被夹紧");

    // ── G. 计量可取消 ───────────────────────────────────────────────────
    var cancelRoot = Path.Combine(work, "cancel");
    var cancelLayout = new AppDataLayout(cancelRoot);
    var cancelCatalog = new StorageCatalog(cancelLayout, Path.Combine(cancelRoot, "webview"), Path.Combine(cancelRoot, "library"));
    for (var index = 0; index < 2500; index++)
        NewFile(Path.Combine(cancelRoot, "webview", "EBWebView", "GrShaderCache", $"f{index:D4}.bin"), 1);

    var fullMeasurement = await meter.MeasureAsync(cancelCatalog);
    Check(fullMeasurement.TotalBytes == 2500, "可取消计量的完整结果正确（2500 字节）");

    using (var cts = new CancellationTokenSource())
    {
        var progress = new InlineProgress<StorageMeasurementProgress>(_ => cts.Cancel());
        var canceled = false;
        try
        {
            await meter.MeasureAsync(cancelCatalog, progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        Check(canceled, "计量可以被取消（抛出取消而不是返回部分结果）");
    }

    using (var preCanceled = new CancellationTokenSource())
    {
        preCanceled.Cancel();
        var threw = false;
        try
        {
            await meter.MeasureAsync(cancelCatalog, null, preCanceled.Token);
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }
        Check(threw, "已取消的令牌不会启动整根计量");
    }

    // 清理也可以取消：取消后如实报告部分结果，且已完成的目标确实生效。
    var cancelCleaner = new StorageCleaner(cancelCatalog, new StorageRetentionPolicy(14), meter);
    var cancelPreview = await cancelCleaner.BuildPreviewAsync(StorageCleaner.DefaultScopes);
    Check(cancelPreview.Targets.Count == 1 && cancelPreview.TotalFiles == 2500, "取消测试：一个缓存目录目标（2500 文件）");
    Check(cancelPreview.Targets.Count > 0, "取消测试：预览目标存在");

    var secondCache = Path.Combine(cancelRoot, "webview", "EBWebView", "ShaderCache");
    for (var index = 0; index < 1500; index++) NewFile(Path.Combine(secondCache, $"g{index:D4}.bin"), 1);
    var twoTargets = await cancelCleaner.BuildPreviewAsync(StorageCleaner.DefaultScopes);
    Check(twoTargets.Targets.Count == 2, $"取消测试：两个缓存目录目标（{twoTargets.Targets.Count}）");

    using (var cts = new CancellationTokenSource())
    {
        var progress = new InlineProgress<StorageCleanupProgress>(update =>
        {
            if (update.TargetsDone >= 1) cts.Cancel();
        });
        var cancelResult = await cancelCleaner.CleanAsync(twoTargets, progress, cts.Token);
        Check(cancelResult.Canceled && !cancelResult.Completed, "清理取消被如实标记为未完成");
        Check(cancelResult.UnprocessedTargets == 1, $"取消后剩余目标如实计数（{cancelResult.UnprocessedTargets}）");
        var doneTarget = twoTargets.Targets[0].Path;
        var pendingTarget = twoTargets.Targets[1].Path;
        Check(Directory.GetFiles(doneTarget, "*", SearchOption.AllDirectories).Length == 0, "取消前已完成的目标确实清空");
        Check(Directory.GetFiles(pendingTarget, "*", SearchOption.AllDirectories).Length > 0, "取消后未处理的目标保持原样");
        Check(cancelResult.Failures.Count == 0, "取消不产生虚假失败项");
    }

    // ── H. 重解析点不跟随（避免循环与重复计数） ──────────────────────────
    var webviewRoot = Path.Combine(appRoot, "webview");
    var beforeJunction = meter.Meter(webviewRoot);
    var junction = Path.Combine(appRoot, "webview", "EBWebView", "Default", "loop");
    var junctionMade = MakeJunction(junction, Path.Combine(appRoot, "webview"));

    if (junctionMade)
    {
        var stopwatch = Stopwatch.StartNew();
        var withJunction = meter.Meter(webviewRoot);
        stopwatch.Stop();
        Check(stopwatch.ElapsedMilliseconds < 10_000, "含重解析点的计量不会陷入循环");
        Check(withJunction.Bytes == beforeJunction.Bytes, $"重解析点不计入字节数（{withJunction.Bytes}/{beforeJunction.Bytes}）");
        Check(withJunction.SkippedEntries == beforeJunction.SkippedEntries + 1, "重解析点按未统计条目如实计数");
    }
    else
    {
        Skip("重解析点不跟随（mklink /J 在本机不可用）");
    }

    // ── K. 链接保护：发现、预览、执行都不跟随重解析点 ────────────────────
    var linkRoot = Path.Combine(work, "links");
    var outsideRoot = Path.Combine(work, "outside");
    var linkLayout = new AppDataLayout(linkRoot);
    var linkCatalog = new StorageCatalog(linkLayout, Path.Combine(linkRoot, "webview"), Path.Combine(linkRoot, "library"));
    var linkWebview = Path.Combine(linkRoot, "webview");
    var linkCacheDir = Path.Combine(linkWebview, "EBWebView", "Default", "Cache");
    var linkShaderDir = Path.Combine(linkWebview, "EBWebView", "ShaderCache");

    // 数据根之外的哨兵与链接目标（只能通过链接到达）
    var sentinel = NewFile(Path.Combine(outsideRoot, "user-source.txt"), 1234);
    var sentinelHash = HashFile(sentinel);
    var externalCacheFile = NewFile(Path.Combine(outsideRoot, "external-cache", "external-cache.bin"), 4096);
    var externalTrap = NewFile(Path.Combine(outsideRoot, "Code Cache", "trap.bin"), 2048);

    var linkMine = NewFile(Path.Combine(linkCacheDir, "mine.bin"), 512);
    var linkNormal = NewFile(Path.Combine(linkShaderDir, "mine2.bin"), 256);
    var linkOffline = NewFile(Path.Combine(linkWebview, "EBWebView", "Default", "Service Worker", "CacheStorage", "offline.bin"), 640);

    var linkedCacheChild = Path.Combine(linkCacheDir, "linked");
    var linkedCacheDir = Path.Combine(linkWebview, "EBWebView", "GrShaderCache");
    var linkedAncestor = Path.Combine(linkWebview, "EBWebView", "external-link");
    var linkedLoop = Path.Combine(linkCacheDir, "loop");

    var linksMade = MakeJunction(linkedCacheChild, outsideRoot)
        && MakeJunction(linkedCacheDir, Path.Combine(outsideRoot, "external-cache"))
        && MakeJunction(linkedAncestor, outsideRoot)
        && MakeJunction(linkedLoop, linkWebview);
    if (!linksMade)
    {
        Skip("链接保护（mklink /J 在本机不可用）");
    }
    else
    {
        var fileLink = Path.Combine(linkCacheDir, "file-link.bin");
        var fileLinkMade = false;
        try
        {
            File.CreateSymbolicLink(fileLink, sentinel);
            fileLinkMade = File.Exists(fileLink);
        }
        catch (Exception error)
        {
            Console.WriteLine("文件符号链接不可用：" + error.Message);
        }
        if (!fileLinkMade) Skip("文件符号链接（本机不可用）");

        var linkCleaner = new StorageCleaner(linkCatalog, new StorageRetentionPolicy(14), meter);
        var linkStopwatch = Stopwatch.StartNew();
        var linkPreview = await linkCleaner.BuildPreviewAsync(StorageCleaner.DefaultScopes);

        Check(linkPreview.Targets.Count == 2, $"链接夹具：只把普通缓存目录当作目标（{linkPreview.Targets.Count}）");
        Check(linkPreview.Targets.Any(target => target.Path == linkCacheDir) && linkPreview.Targets.Any(target => target.Path == linkShaderDir),
            "链接夹具：普通缓存目录仍是目标");
        Check(linkPreview.TotalBytes == 512 + 256, $"链接内容不参与预览计量（{linkPreview.TotalBytes}）");
        Check(!linkPreview.Targets.Any(target => target.Path.StartsWith(outsideRoot, StringComparison.OrdinalIgnoreCase)),
            "数据根外的链接目标不是清理目标");
        Check(!linkPreview.Targets.Any(target => target.Path == Path.Combine(outsideRoot, "Code Cache")),
            "祖先链接下的缓存名目录不会成为目标");
        Check(!linkPreview.Targets.Any(target => target.Path.Contains("Service Worker", StringComparison.OrdinalIgnoreCase)),
            "默认范围不含站点离线存储");
        Check(linkPreview.Skipped.Any(skip => skip.Path == linkedCacheChild && skip.Kind == StorageCleanupSkipKind.ReparsePoint && skip.Reason.Contains("链接")),
            "缓存内链接在预览中如实报告原因");
        Check(linkPreview.Skipped.Any(skip => skip.Path == linkedCacheDir && skip.Reason.Contains("链接")),
            "缓存名目录本身是链接时如实报告");
        Check(linkPreview.Skipped.Any(skip => skip.Path == linkedAncestor), "祖先链接在预览中如实报告");
        Check(linkPreview.Skipped.Any(skip => skip.Path == linkedLoop), "自引用链接在预览中如实报告");
        Check(linkPreview.Skipped.All(skip => skip.Reason.Length > 0), "所有跳过项都带原因");
        Check(linkPreview.Skipped.Select(skip => skip.Path + "|" + skip.Kind).Distinct().Count() == linkPreview.Skipped.Count,
            "预览跳过清单没有重复条目");

        var linkResult = await linkCleaner.CleanAsync(linkPreview);
        linkStopwatch.Stop();
        Check(linkResult.Completed && linkResult.Failures.Count == 0, "链接夹具：清理完成且没有删除失败");
        Check(linkResult.DeletedFiles == 2 && linkResult.DeletedBytes == 768, $"只删除普通缓存文件（{linkResult.DeletedFiles} / {linkResult.DeletedBytes}）");
        Check(!File.Exists(linkMine) && !File.Exists(linkNormal), "普通缓存文件已删除");
        Check(Directory.Exists(linkCacheDir), "缓存目录本身保留");
        Check(File.Exists(sentinel) && new FileInfo(sentinel).Length == 1234 && HashFile(sentinel) == sentinelHash,
            "数据根外哨兵文件内容与哈希不变");
        Check(File.Exists(externalCacheFile) && new FileInfo(externalCacheFile).Length == 4096, "链接目标目录内的外部文件未被删除");
        Check(File.Exists(externalTrap) && new FileInfo(externalTrap).Length == 2048, "祖先链接下的外部内容未被删除");
        Check(Directory.Exists(linkedCacheDir) && Directory.Exists(linkedAncestor) && Directory.Exists(linkedCacheChild),
            "链接本身未被删除");
        Check(linkResult.Skipped.Any(skip => skip.Kind == StorageCleanupSkipKind.ReparsePoint && skip.Reason.Contains("链接")),
            "执行结果如实报告跳过的链接");
        Check(linkStopwatch.ElapsedMilliseconds < 60_000, $"含链接的发现与清理不循环（{linkStopwatch.ElapsedMilliseconds} ms）");
        Check(File.Exists(linkOffline), "默认范围不动 Service Worker 离线存储");

        // 预览后把目标目录替换成链接：执行必须拒绝，且不删除链接目标
        var replacedTarget = Path.Combine(linkWebview, "EBWebView", "DawnCache");
        NewFile(Path.Combine(replacedTarget, "replace.bin"), 300);
        var replacePreview = await linkCleaner.BuildPreviewAsync(StorageCleaner.DefaultScopes);
        Check(replacePreview.Targets.Any(target => target.Path == replacedTarget), "替换场景：目标先出现在预览里");
        Directory.Delete(replacedTarget, recursive: true);
        var replaceOutside = Path.Combine(outsideRoot, "replace-target");
        var replaceTrap = NewFile(Path.Combine(replaceOutside, "trap2.bin"), 777);
        var replaceHash = HashFile(replaceTrap);
        Check(MakeJunction(replacedTarget, replaceOutside), "替换场景：目标目录已替换为链接");

        var replaceResult = await linkCleaner.CleanAsync(replacePreview);
        Check(File.Exists(replaceTrap) && HashFile(replaceTrap) == replaceHash && new FileInfo(replaceTrap).Length == 777,
            "预览后替换为链接的目标：外部文件内容与哈希不变");
        Check(replaceResult.Skipped.Any(skip => skip.Path == replacedTarget && skip.Kind == StorageCleanupSkipKind.ReparsePoint && skip.Reason.Contains("链接")),
            "预览后变为链接的目标被拒绝并如实报告");
        Check(replaceResult.Failures.Count == 0, "替换场景没有删除失败");

        // M 复核反例（R-030 残留）：目标目录本身不是链接，但预览后其子目录被替换成链接。
        // 旧快照里的 nested/victim.txt 仍会被尝试删除，必须从「区域根到该条目」的完整链上拒绝。
        var nestedCache = Path.Combine(linkWebview, "EBWebView", "Code Cache");
        var nestedDir = Path.Combine(nestedCache, "nested");
        var nestedVictim = NewFile(Path.Combine(nestedDir, "victim.txt"), 321);
        var nestedPreview = await linkCleaner.BuildPreviewAsync(StorageCleaner.DefaultScopes);
        Check(nestedPreview.Targets.Any(target => target.Path == nestedCache), "嵌套替换：缓存目录在预览中");
        File.Delete(nestedVictim);
        Directory.Delete(nestedDir, recursive: false);
        var nestedOutside = Path.Combine(outsideRoot, "nested-fixture");
        var nestedExternalVictim = NewFile(Path.Combine(nestedOutside, "victim.txt"), 654);
        var nestedExternalHash = HashFile(nestedExternalVictim);
        Check(MakeJunction(nestedDir, nestedOutside), "嵌套替换：nested 已替换为指向外部夹具的 junction");

        var nestedResult = await linkCleaner.CleanAsync(nestedPreview);
        Check(File.Exists(nestedExternalVictim) && new FileInfo(nestedExternalVictim).Length == 654 && HashFile(nestedExternalVictim) == nestedExternalHash,
            "嵌套替换：外部同名文件内容与哈希不变");
        Check(nestedResult.Skipped.Any(skip => skip.Path == nestedDir && skip.Kind == StorageCleanupSkipKind.ReparsePoint),
            "嵌套替换：链上链接被拒绝并如实报告");
        Check(!nestedResult.Skipped.Any(skip => skip.Path.EndsWith("victim.txt", StringComparison.OrdinalIgnoreCase)
                && !skip.Path.StartsWith(nestedCache, StringComparison.OrdinalIgnoreCase)),
            "嵌套替换：跳过项只涉及缓存内路径，不指向外部夹具");
        Check(!nestedResult.Skipped.Any(skip => string.Equals(skip.Path, nestedExternalVictim, StringComparison.OrdinalIgnoreCase)),
            "嵌套替换：跳过清单不把外部路径当作清理对象");

        // 两级嵌套：缓存目录 → 中间目录 → 叶目录 都被替换，叶下的旧快照文件同样不得删除
        var deepCache = Path.Combine(linkWebview, "EBWebView", "DawnGraphiteCache");
        var deepMid = Path.Combine(deepCache, "mid");
        var deepLeaf = Path.Combine(deepMid, "leaf");
        var deepVictim = NewFile(Path.Combine(deepLeaf, "victim.txt"), 111);
        var deepPreview = await linkCleaner.BuildPreviewAsync(StorageCleaner.DefaultScopes);
        Check(deepPreview.Targets.Any(target => target.Path == deepCache), "两级嵌套：缓存目录在预览中");
        File.Delete(deepVictim);
        Directory.Delete(deepLeaf, recursive: false);
        Directory.Delete(deepMid, recursive: false);
        var deepOutside = Path.Combine(outsideRoot, "deep-fixture");
        var deepExternalVictim = NewFile(Path.Combine(deepOutside, "victim.txt"), 999);
        var deepExternalHash = HashFile(deepExternalVictim);
        var deepLinksMade = MakeJunction(deepMid, deepOutside);
        if (!deepLinksMade)
        {
            Skip("两级嵌套父目录替换（第二个 junction 创建失败）");
        }
        else
        {
            var deepResult = await linkCleaner.CleanAsync(deepPreview);
            Check(File.Exists(deepExternalVictim) && HashFile(deepExternalVictim) == deepExternalHash,
                "两级嵌套父目录替换：外部同名文件内容与哈希不变");
            Check(!deepResult.Skipped.Any(skip => string.Equals(skip.Path, deepExternalVictim, StringComparison.OrdinalIgnoreCase)),
                "两级嵌套父目录替换：跳过清单不把外部路径当作清理对象");
            Check(deepResult.Skipped.Any(skip => skip.Path == deepMid && skip.Kind == StorageCleanupSkipKind.ReparsePoint),
                "两级嵌套父目录替换：中间层链接被拒绝并如实报告");
        }

        using (var linkVm = new StorageManagerViewModel(linkCatalog, 14))
        {
            await linkVm.PreviewCleanupAsync();
            Check(linkVm.HasSkipped && linkVm.Skipped.Any(row => row.KindText == "链接未跟随"),
                "视图模型把跳过的链接显示为独立条目");
            Check(linkVm.Skipped.All(row => row.Path.Length > 0 && row.Reason.Length > 0), "跳过条目带路径与原因");
        }
    }

    // ── L. 执行时重新验证：旧预览不得删除已变化/不再过期的目标 ─────────────
    var staleRoot = Path.Combine(work, "stale");
    var staleLayout = new AppDataLayout(staleRoot);
    var staleCatalog = new StorageCatalog(staleLayout, Path.Combine(staleRoot, "webview"), Path.Combine(staleRoot, "library"));
    var staleCleaner = new StorageCleaner(staleCatalog, new StorageRetentionPolicy(14), meter);
    var staleCacheDir = Path.Combine(staleRoot, "webview", "EBWebView", "GrShaderCache");
    var staleCacheDir2 = Path.Combine(staleRoot, "webview", "EBWebView", "ShaderCache");

    var staleLog = NewFile(Path.Combine(staleRoot, "logs", "stale-30d.jsonl"), 100); SetAge(staleLog, 30);
    NewFile(Path.Combine(staleRoot, "logs", "fresh.jsonl"), 100);
    NewFile(Path.Combine(staleCacheDir, "a.bin"), 100);
    NewFile(Path.Combine(staleCacheDir2, "b.bin"), 100);
    var logOnlyScopes = new[] { StorageCleanupScope.ExpiredLogs };
    var cacheOnlyScopes = new[] { StorageCleanupScope.RebuildableCache };

    // L-1：预览后保留期提高到 60 天 → 执行时该日志已不再过期
    var stalePreview = await staleCleaner.BuildPreviewAsync(StorageCleaner.DefaultScopes);
    Check(stalePreview.Targets.Any(target => target.Path == staleLog), "过时场景：日志先进入预览");
    var retention60Cleaner = new StorageCleaner(staleCatalog, new StorageRetentionPolicy(60), meter);
    var retentionResult = await retention60Cleaner.CleanAsync(stalePreview);
    Check(File.Exists(staleLog) && new FileInfo(staleLog).Length == 100, "执行时不再过期的日志被保留（未按旧预览删除）");
    Check(retentionResult.Skipped.Any(skip => skip.Path == staleLog && skip.Kind == StorageCleanupSkipKind.NotExpired && skip.Reason.Contains("保留")),
        "不再过期时如实报告保留原因");

    // L-2：预览后日志被重新写入（长度与时间都变化）
    SetAge(staleLog, 30);
    var rewritePreview = await staleCleaner.BuildPreviewAsync(logOnlyScopes);
    Check(rewritePreview.Targets.Any(target => target.Path == staleLog), "重写场景：日志在预览中");
    File.WriteAllBytes(staleLog, new byte[250]);
    File.SetLastWriteTimeUtc(staleLog, DateTime.UtcNow);
    var rewrittenHash = HashFile(staleLog);
    var rewriteResult = await staleCleaner.CleanAsync(rewritePreview);
    Check(File.Exists(staleLog) && HashFile(staleLog) == rewrittenHash, "预览后被重新写入的日志内容与哈希不变");
    Check(rewriteResult.Skipped.Any(skip => skip.Path == staleLog && skip.Kind == StorageCleanupSkipKind.ChangedSincePreview),
        "重新写入的日志如实报告为已变化");
    Check(rewriteResult.DeletedFiles == 0, "重写场景没有按旧预览删除任何文件");

    // L-3：预览后新增的过期日志不进入旧预览的删除范围
    File.Delete(staleLog);
    var beforeAddPreview = await staleCleaner.BuildPreviewAsync(logOnlyScopes);
    Check(beforeAddPreview.Targets.Count == 0, "新增场景：预览时没有日志目标");
    var addedLog = NewFile(Path.Combine(staleRoot, "logs", "added-30d.jsonl"), 100); SetAge(addedLog, 30);
    var addedHash = HashFile(addedLog);
    var addResult = await staleCleaner.CleanAsync(beforeAddPreview);
    Check(File.Exists(addedLog) && HashFile(addedLog) == addedHash, "预览后新增的过期日志未被删除");
    Check(addResult.DeletedFiles == 0, "新增场景没有扩大删除范围");

    // L-4：缓存目录预览后新增内容不纳入删除范围（删除范围与预览一致）
    File.Delete(Path.Combine(staleCacheDir2, "b.bin"));
    var previewedCacheFile = NewFile(Path.Combine(staleCacheDir, "previewed.bin"), 100);
    var cachePreview = await staleCleaner.BuildPreviewAsync(cacheOnlyScopes);
    Check(cachePreview.Targets.Any(target => target.Path == staleCacheDir), "缓存新增场景：目标在预览中");
    var addedCacheFile = NewFile(Path.Combine(staleCacheDir, "added.bin"), 300);
    var addedCacheHash = HashFile(addedCacheFile);
    var cacheResult = await staleCleaner.CleanAsync(cachePreview);
    Check(!File.Exists(previewedCacheFile), "预览时存在的缓存文件已删除");
    Check(File.Exists(addedCacheFile) && HashFile(addedCacheFile) == addedCacheHash, "预览后新增的缓存文件未被删除");
    Check(cacheResult.Skipped.Any(skip => skip.Path == staleCacheDir && skip.Kind == StorageCleanupSkipKind.ChangedSincePreview && skip.Reason.Contains("新增")),
        "新增内容被如实报告为未纳入清理");
    Check(cacheResult.DeletedFiles == 1, $"删除量与预览一致（{cacheResult.DeletedFiles}）");

    // L-5：预览后目标文件被外部删除
    File.Delete(addedCacheFile);
    var vanishTarget = Path.Combine(staleRoot, "webview", "EBWebView", "DawnGraphiteCache");
    var vanish = NewFile(Path.Combine(vanishTarget, "vanish.bin"), 100);
    var vanishPreview = await staleCleaner.BuildPreviewAsync(cacheOnlyScopes);
    File.Delete(vanish);
    var vanishResult = await staleCleaner.CleanAsync(vanishPreview);
    Check(vanishResult.Completed && vanishResult.Failures.Count == 0, "预览后消失的文件不算失败");
    Check(!File.Exists(vanish), "消失的文件保持缺失状态");
    Check(vanishResult.Skipped.Any(skip => skip.Path == vanishTarget && skip.Kind == StorageCleanupSkipKind.Missing),
        "消失的条目如实报告为已不存在");

    // ── M. 缓存分类：Service Worker/CacheStorage 属于站点离线存储 ──────────
    Check(!StorageCatalog.BrowserCacheDirectoryNames.Contains("CacheStorage", StringComparer.OrdinalIgnoreCase)
        && !StorageCatalog.BrowserCacheDirectoryNames.Contains("Service Worker", StringComparer.OrdinalIgnoreCase),
        "CacheStorage / Service Worker 不在默认可重建缓存集合内");
    Check(StorageCatalog.BrowserSiteDataNames.Contains("CacheStorage", StringComparer.OrdinalIgnoreCase)
        && StorageCatalog.BrowserSiteDataNames.Contains("Service Worker", StringComparer.OrdinalIgnoreCase),
        "CacheStorage / Service Worker 归入站点数据（需显式确认）");

    // ── I. 视图模型状态机 ───────────────────────────────────────────────
    var vmRoot = Path.Combine(work, "vm");
    var vmLayout = new AppDataLayout(vmRoot);
    var vmCatalog = new StorageCatalog(vmLayout, Path.Combine(vmRoot, "webview"), Path.Combine(vmRoot, "library"));
    NewFile(Path.Combine(vmRoot, "settings.json"), 100);
    NewFile(Path.Combine(vmRoot, "sessions", "s.jsonl"), 500);
    NewFile(Path.Combine(vmRoot, "pi-agent", "auth.json"), 64);
    var vmOldLog = NewFile(Path.Combine(vmRoot, "logs", "vm-old.jsonl"), 200); SetAge(vmOldLog, 30);
    NewFile(Path.Combine(vmRoot, "logs", "vm-new.jsonl"), 300);
    NewFile(Path.Combine(vmRoot, "webview", "EBWebView", "GrShaderCache", "vm-cache.bin"), 1024);
    var vmCookies = NewFile(Path.Combine(vmRoot, "webview", "EBWebView", "Default", "Cookies"), 100);

    var opened = string.Empty;
    using (var vm = new StorageManagerViewModel(vmCatalog, 14, path => opened = path))
    {
        Check(vm.CanOpenFolders, "注入回调后允许打开目录");
        Check(!vm.CanMeasure == false && vm.CanPreview, "未计量时也能预览清理");
        Check(vm.SelectedScopes().SequenceEqual(new[] { StorageCleanupScope.RebuildableCache, StorageCleanupScope.ExpiredLogs }),
            "默认勾选范围只有缓存与过期日志");
        Check(vm.Scopes.Single(option => option.Scope == StorageCleanupScope.BrowserSiteData).IsEnabled == false,
            "站点数据默认不勾选");

        await vm.MeasureAsync();
        Check(vm.HasMeasurement && !vm.IsMeasuring, "计量完成后发布结果");
        Check(vm.Areas.All(row => row.Measured && row.UsageText.Length > 0), "每个区域都有计量结果");
        Check(vm.MeasuredText.Contains(StorageFormat.Bytes(2288)), $"合计占用与文件实际大小一致（{vm.MeasuredText}）");
        Check(vm.TotalText.Contains(StorageFormat.Bytes(2288)), "合计文本与实际占用一致");

        vm.OpenFolder(vm.Areas[0].Path);
        Check(opened == vm.Areas[0].Path, "打开目录回调收到区域实际路径");

        await vm.PreviewCleanupAsync();
        Check(vm.HasPreview && vm.Targets.Count == 2, $"预览目标进入界面（{vm.Targets.Count}）");
        Check(vm.PreviewSummary.Contains("预计释放"), "预览摘要包含预计大小");
        Check(vm.CanClean, "预览后允许执行清理");

        vm.Scopes.Single(option => option.Scope == StorageCleanupScope.ExpiredLogs).IsEnabled = false;
        Check(!vm.HasPreview, "改变清理范围后预览失效");
        Check(vm.Status.Contains("重新预览"), "范围变化提示重新预览");

        await vm.PreviewCleanupAsync();
        Check(vm.Targets.Count == 1 && vm.Targets.All(target => target.Scope == StorageCleanupScope.RebuildableCache),
            "重新预览只包含缓存目标");

        await vm.CleanAsync();
        Check(vm.HasResult && !vm.IsCleaning, "清理结果进入界面");
        Check(vm.ResultText.Contains("→") && vm.ResultText.Contains("释放"), $"结果记录前后占用与实际变化（{vm.ResultText}）");
        Check(!vm.HasPreview && vm.Targets.Count == 0, "清理后预览被消费，不会重复执行同一份目标");
        Check(!File.Exists(Path.Combine(vmRoot, "webview", "EBWebView", "GrShaderCache", "vm-cache.bin")), "缓存文件确实被清理");
        Check(File.Exists(vmCookies) && File.Exists(Path.Combine(vmRoot, "sessions", "s.jsonl")),
            "未选范围与保护范围保持不变");

        vm.LogRetentionDays = 0;
        Check(vm.LogRetentionDays == 1, "保留天数下界夹紧");
        vm.LogRetentionDays = 9999;
        Check(vm.LogRetentionDays == 365, "保留天数上界夹紧");
        vm.LogRetentionDays = 30;
        Check(vm.RetentionText.Contains("30"), "保留说明随天数更新");
    }

    using (var noFolder = new StorageManagerViewModel(vmCatalog))
    {
        Check(!noFolder.CanOpenFolders, "未注入回调时不允许打开目录");
        noFolder.OpenFolder(vmCatalog.DataRoot);
        Check(true, "未注入回调时打开目录是空操作（不抛异常）");
    }

    // 全新安装：数据根尚无内容时也要如实展示，不报错、不产生目标。
    var freshRoot = Path.Combine(work, "fresh");
    var freshCatalog = new StorageCatalog(new AppDataLayout(freshRoot), Path.Combine(freshRoot, "webview"), Path.Combine(freshRoot, "library"));
    using (var fresh = new StorageManagerViewModel(freshCatalog))
    {
        await fresh.MeasureAsync();
        Check(fresh.Areas.All(row => row.UsageText == "目录不存在"), "全新安装：各区域如实显示目录不存在");
        Check(fresh.MeasuredText.Contains(StorageFormat.Bytes(0)), $"全新安装：合计占用为零（{fresh.MeasuredText}）");
        await fresh.PreviewCleanupAsync();
        Check(!fresh.HasPreview && fresh.Status.Contains("没有可清理的目标"), "全新安装：没有目标时不可执行清理");
        Check(!fresh.CanClean, "全新安装：预览为空时执行清理保持禁用");
    }

    // 取消计量：结果不发布，状态如实。
    var cancelVmRoot = Path.Combine(work, "vm-cancel");
    var cancelVmLayout = new AppDataLayout(cancelVmRoot);
    var cancelVmCatalog = new StorageCatalog(cancelVmLayout, Path.Combine(cancelVmRoot, "webview"), Path.Combine(cancelVmRoot, "library"));
    for (var index = 0; index < 2500; index++)
        NewFile(Path.Combine(cancelVmRoot, "webview", "EBWebView", "GrShaderCache", $"h{index:D4}.bin"), 1);

    using (var vm = new StorageManagerViewModel(cancelVmCatalog))
    {
        var measuring = vm.MeasureAsync();
        vm.CancelMeasure();
        await measuring;
        Check(!vm.IsMeasuring, "取消后不再处于计量状态");
        Check(vm.Status.Contains("取消"), "取消后状态如实说明");
        Check(!vm.HasMeasurement && vm.Areas.All(row => !row.Measured), "取消的计量不发布部分结果");
    }

    // ── J. 无头页面冒烟 ─────────────────────────────────────────────────
    var pageRoot = Path.Combine(work, "page");
    var pageLayout = new AppDataLayout(pageRoot);
    var pageCatalog = new StorageCatalog(pageLayout, Path.Combine(pageRoot, "webview"), Path.Combine(pageRoot, "library"));
    NewFile(Path.Combine(pageRoot, "settings.json"), 100);
    NewFile(Path.Combine(pageRoot, "sessions", "s.jsonl"), 500);
    var pageOldLog = NewFile(Path.Combine(pageRoot, "logs", "page-old.jsonl"), 400); SetAge(pageOldLog, 30);
    NewFile(Path.Combine(pageRoot, "webview", "EBWebView", "GrShaderCache", "page-cache.bin"), 2048);
    NewFile(Path.Combine(pageRoot, "webview", "EBWebView", "Default", "Cookies"), 256);
    var pageOutside = Path.Combine(work, "page-outside");
    NewFile(Path.Combine(pageOutside, "outside.bin"), 128);
    var pageLinkedCache = Path.Combine(pageRoot, "webview", "EBWebView", "GPUCache");
    var pageLinkMade = MakeJunction(pageLinkedCache, pageOutside);
    if (!pageLinkMade) Skip("页面跳过清单渲染（mklink /J 在本机不可用）");

    var folderOpened = string.Empty;
    using (var vm = new StorageManagerViewModel(pageCatalog, 14, path => folderOpened = path))
    {
        Fufucode.App.Storage.Tests.UiSmoke.Run(vm, pageRoot, pageLinkMade ? pageLinkedCache : null, pageOutside, Check);
    }
    Check(folderOpened.Length == 0 || folderOpened.StartsWith(pageRoot, StringComparison.OrdinalIgnoreCase),
        "页面打开目录回调指向真实路径");

    Console.WriteLine($"\n全部通过：{passed} 项断言" + (skipped.Count > 0 ? $"，跳过 {skipped.Count} 项：{string.Join("、", skipped)}" : string.Empty));
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    Console.Error.WriteLine(error);
    return 1;
}

/// <summary>同步内联进度回调：测试需要确定性触发点，不能用会切线程的 Progress&lt;T&gt;。</summary>
internal sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}
