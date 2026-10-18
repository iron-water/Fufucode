using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Fufucode.App.Features.Plugins;
using Fufucode.Core;

namespace Fufucode.App.Plugins.Tests;

/// <summary>
/// 服务层定向测试：真实临时文件与真实本地插件目录。
/// 覆盖导入与重开、固定版本/摘要绑定、兼容未知/不支持、更新失败保旧、pending 不动活动目录、
/// 显式回退、并发旧 generation 拒绝、文件锁/损坏保真、入口越界与链接、移除只限托管非活动。
/// </summary>
internal static class ServiceTests
{
    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "fufucode-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var passed = 0;
        var failures = 0;
        void Check(bool condition, string name)
        {
            if (condition) { passed++; Console.WriteLine("PASS " + name); return; }
            failures++;
            Console.WriteLine("FAIL " + name);
        }

        try
        {
            await RunCore(root, Check);
        }
        catch (Exception error)
        {
            failures++;
            Console.WriteLine("FAIL 未处理异常：" + error);
        }
        finally
        {
            Console.WriteLine("evidence root: " + root);
            try { Directory.Delete(root, recursive: true); }
            catch (Exception error) { Console.WriteLine("临时目录保留（清理失败不影响结果）：" + error.GetType().Name); }
        }

        Console.WriteLine(failures == 0 ? $"ALL PASS ({passed})" : $"FAILURES={failures} PASSED={passed}");
        return failures == 0 ? 0 : 1;
    }

    private static async Task RunCore(string root, Action<bool, string> check)
    {
        var dataRoot = Path.Combine(root, "appdata");
        var layout = new AppDataLayout(dataRoot);
        var host = new FakeHost();
        var service = new LocalPluginService(layout, host);

        // ── 1. 导入与重开：真实副本落在 plugins/local，重开后目录与 pending 可恢复 ──
        const string demoId = "demo.plugin";
        var demoKey = PluginIdRules.Sanitize(demoId, "plugin");
        var source = PluginFixture.Create(root, "src 插件 目录", demoId, "演示插件", "1.0.0");
        var imported = await service.ImportAsync(new LocalPluginImportRequest(source));
        check(imported.Version == "1.0.0", "导入返回固定版本 1.0.0");
        check(imported.ContentHash.Length == 64, "导入返回版本摘要（SHA256）");
        check(Directory.Exists(Path.Combine(service.Root, demoKey, imported.VersionId)),
            "副本落在 plugins/local/<安全ID>/<版本>");
        check(File.Exists(Path.Combine(service.Root, demoKey, imported.VersionId, "index.js")),
            "副本包含真实入口文件");
        check(!Directory.Exists(Path.Combine(service.Root, LocalPluginService.StagingDirectoryName)) ||
              Directory.GetFileSystemEntries(Path.Combine(service.Root, LocalPluginService.StagingDirectoryName)).Length == 0,
            "原子发布后本轮暂存已清空");

        var view = await service.ListAsync();
        check(view.Items.Count == 1 && view.Items[0].Versions.Count == 1, "重开（新查询）读回同一插件与版本");
        check(view.Items[0].Versions[0].ContentHash == imported.ContentHash, "重开后摘要与导入时一致");
        check(string.Equals(Path.GetFullPath(view.Items[0].Versions[0].SourceRoot ?? string.Empty),
                  Path.GetFullPath(source), StringComparison.Ordinal),
            "来源路径逐字绑定（不改写源目录）");

        // ── 2. 固定版本/摘要绑定：同版本不同内容必须拒绝，不能覆盖固定版本 ──
        var sameVersionDifferentBody = PluginFixture.Create(root, "src 同版本", "demo.plugin", "演示插件", "1.0.0",
            entryBody: "// different body\n");
        var conflict = await CaptureAsync(() => service.ImportAsync(new LocalPluginImportRequest(sameVersionDifferentBody)));
        check(conflict is not null && conflict.Contains("拒绝覆盖固定版本"),
            "同名版本内容不一致：明确拒绝，不覆盖固定版本");
        var afterConflict = await service.ListAsync();
        check(afterConflict.Items[0].Versions.Count == 1, "冲突导入后仍只有一个固定版本");
        check(File.ReadAllText(Path.Combine(service.Root, demoKey, imported.VersionId, "index.js"))
                .Contains("demo.plugin 1.0.0"),
            "冲突导入后原有固定版本字节未被覆盖");

        // 同版本同摘要：不重复复制，保留原有绑定。
        var identical = await service.ImportAsync(new LocalPluginImportRequest(source));
        check(identical.VersionId == imported.VersionId && identical.ContentHash == imported.ContentHash,
            "同版本同摘要重复导入：返回同一版本，不建副本");

        // ── 3. 兼容：未知宿主身份、版本不匹配、OMP/Codex/DSH 不支持 ──
        // 语义：本地副本照常浏览/安装（任务书"宿主缺失照常浏览/安装本地副本"），
        // 但兼容未确认或后端不支持时一律拒绝应用。
        var unknownHost = new LocalPluginService(new AppDataLayout(Path.Combine(root, "appdata-unknown")),
            new FakeHost { Identity = HostIdentity.Unknown() });
        var unknownPreview = unknownHost.Preview(new LocalPluginImportRequest(source));
        check(unknownPreview.Compatibility == PluginCompatibility.Unknown && unknownPreview.CanImport,
            "宿主身份未知：本地副本仍可导入，兼容状态标 unknown");
        check(!unknownPreview.CanApplyVersion, "宿主身份未知：拒绝应用，不假称兼容");
        var unknownImported = await unknownHost.ImportAsync(new LocalPluginImportRequest(source));
        var unknownApply = await unknownHost.ApplyVersionAsync(demoId, unknownImported.VersionId);
        check(!unknownApply.Success && unknownApply.Rejection == LocalPluginRejectionKind.IncompatibleHost,
            "宿主身份未知：应用被拒绝");
        var unknownList = await unknownHost.ListAsync();
        check(unknownList.Items[0].Status == LocalPluginStatus.Blocked &&
              unknownList.Items[0].StatusText.Contains("未知"),
            "宿主身份未知：列表如实显示 blocked/未知，不显示已加载");

        var mismatchHost = new LocalPluginService(new AppDataLayout(Path.Combine(root, "appdata-mismatch")),
            new FakeHost { Identity = HostIdentity.Pi("9.9.9") });
        var mismatchPreview = mismatchHost.Preview(new LocalPluginImportRequest(source));
        check(mismatchPreview.Compatibility == PluginCompatibility.Incompatible &&
              !mismatchPreview.CanApplyVersion,
            "声明宿主版本与注入身份不匹配：拒绝应用");
        check(mismatchPreview.CompatibilityText.Contains("不匹配"), "不匹配时给出精确的声明与实际身份");

        var ompSource = PluginFixture.Create(root, "src omp", "omp.plugin", "OMP插件", "1.0.0", backend: "omp");
        var ompService = new LocalPluginService(new AppDataLayout(Path.Combine(root, "appdata-omp")), new FakeHost());
        var ompPreview = ompService.Preview(new LocalPluginImportRequest(ompSource));
        check(ompPreview.Compatibility == PluginCompatibility.Unsupported,
            "OMP 后端首版明确 unsupported");
        check(!ompPreview.CanApplyVersion, "OMP 后端明确拒绝应用");
        var ompImported = await ompService.ImportAsync(new LocalPluginImportRequest(ompSource));
        var ompApply = await ompService.ApplyVersionAsync("omp.plugin", ompImported.VersionId);
        check(!ompApply.Success && ompApply.Rejection == LocalPluginRejectionKind.IncompatibleHost,
            "OMP 后端应用被拒绝（不支持不冒充可运行）");
        var ompList = await ompService.ListAsync();
        check(ompList.Items[0].Status == LocalPluginStatus.Blocked &&
              ompList.Items[0].BackendText == "OMP",
            "OMP 副本在列表中明确显示为不支持，而不是已加载");

        // Codex/DSH 同样明确 unsupported。
        foreach (var backend in new[] { "codex", "dsh" })
        {
            var other = PluginFixture.Create(root, "src " + backend, backend + ".plugin", backend, "1.0.0",
                backend: backend);
            var otherPreview = ompService.Preview(new LocalPluginImportRequest(other));
            check(otherPreview.Compatibility == PluginCompatibility.Unsupported &&
                  !otherPreview.CanApplyVersion, $"后端 {backend} 首版明确 unsupported");
        }

        // 缺描述文件：不读 Pi/OMP 原生声明。
        var noDescriptor = PluginFixture.Create(root, "src 无描述", "x.plugin", "X", "1.0.0", writeDescriptor: false);
        var noDescriptorPreview = service.Preview(new LocalPluginImportRequest(noDescriptor));
        check(noDescriptorPreview.Rejection?.Kind == LocalPluginRejectionKind.MissingDescriptor,
            "缺 fufucode-plugin.json：拒绝，不读 Pi/OMP 原生声明");

        // ── 4. 入口越界与链接遍历 ──
        var escapeSource = PluginFixture.Create(root, "src 越界", "escape.plugin", "越界插件", "1.0.0",
            descriptorOverride: new
            {
                id = "escape.plugin",
                displayName = "越界插件",
                version = "1.0.0",
                backend = "pi",
                entryRelativePath = "../outside.js",
                supportedHostVersions = new[] { "1.1.0" },
            });
        File.WriteAllText(Path.Combine(root, "outside.js"), "// outside\n", new UTF8Encoding(false));
        var escapePreview = service.Preview(new LocalPluginImportRequest(escapeSource));
        check(escapePreview.Rejection?.Kind == LocalPluginRejectionKind.EntryOutsideRoot,
            "入口 .. 越界：明确拒绝");

        var absoluteEntry = PluginFixture.Create(root, "src 绝对入口", "abs.plugin", "绝对入口", "1.0.0",
            descriptorOverride: new
            {
                id = "abs.plugin",
                displayName = "绝对入口",
                version = "1.0.0",
                backend = "pi",
                entryRelativePath = Path.Combine(root, "src 绝对入口", "index.js"),
                supportedHostVersions = new[] { "1.1.0" },
            });
        var absolutePreview = service.Preview(new LocalPluginImportRequest(absoluteEntry));
        check(absolutePreview.Rejection?.Kind == LocalPluginRejectionKind.EntryOutsideRoot,
            "入口为绝对路径：拒绝");

        // 显式选择相对入口：调用方指定的入口优先于描述文件声明，且同样过越界校验。
        var multiEntry = PluginFixture.Create(root, "src 多入口", "multi.plugin", "多入口", "1.0.0",
            entry: "main.js", entryBody: "// main\n");
        File.WriteAllText(Path.Combine(multiEntry, "alt.js"), "// alt entry\n", new UTF8Encoding(false));
        var explicitEntry = service.Preview(new LocalPluginImportRequest(multiEntry, "alt.js"));
        check(explicitEntry.Rejection is null && explicitEntry.EntryRelativePath == "alt.js",
            "显式选择相对入口：优先于描述文件声明");
        var escapedEntry = service.Preview(new LocalPluginImportRequest(multiEntry, "../outside.js"));
        check(escapedEntry.Rejection?.Kind == LocalPluginRejectionKind.EntryOutsideRoot,
            "显式入口同样拒绝越界");
        var importedMulti = await service.ImportAsync(new LocalPluginImportRequest(multiEntry, "alt.js"));
        check(importedMulti.EntryRelativePath == "alt.js", "导入记录使用显式选择的相对入口");

        var linkSource = PluginFixture.Create(root, "src 链接", "link.plugin", "链接插件", "1.0.0");
        var linked = Path.Combine(linkSource, "linked");
        if (MakeLink(linked, root))
        {
            var linkPreview = service.Preview(new LocalPluginImportRequest(linkSource));
            check(linkPreview.Rejection?.Kind == LocalPluginRejectionKind.SymlinkTraversal,
                "包内链接/重解析点：拒绝遍历（本环境可创建链接时）");
        }
        else
        {
            Console.WriteLine("SKIP 链接反例（本环境无权限创建 junction/symlink）");
        }

        // ── 5. 应用成功：只登记"已交宿主"，实际加载以快照为准 ──
        var applyResult = await service.ApplyVersionAsync(demoId, imported.VersionId);
        check(applyResult.Success, "应用成功返回");
        check(host.Applies.Count == 1 && host.Applies[0].PluginId == demoId,
            "应用经过宿主回调（不自行改加载目录）");
        var afterApply = await service.ListAsync();
        check(afterApply.Items[0].SelectedVersionId == imported.VersionId, "应用成功后选定版本已记录");
        check(afterApply.Items[0].Status != LocalPluginStatus.Loaded || afterApply.Items[0].IsActive,
            "未确认加载时不显示 Loaded（文件存在/选定不等于 active）");

        // 宿主快照确认加载后状态才变为 Loaded。
        host.LoadedPaths.Add(Path.Combine(service.Root, demoKey, imported.VersionId, "index.js"));
        var loadedView = await service.ListAsync();
        check(loadedView.Items[0].IsActive && loadedView.Items[0].Status == LocalPluginStatus.Loaded,
            "宿主快照确认后状态才为 Loaded（状态来源为真实快照）");

        // ── 6. 更新失败：保旧选定版本与文件、保留诊断、不产生回退目标 ──
        var v2Source = PluginFixture.Create(root, "src v2", "demo.plugin", "演示插件", "2.0.0");
        var v2 = await service.ImportAsync(new LocalPluginImportRequest(v2Source));
        check(v2.Version == "2.0.0" && v2.VersionId != imported.VersionId, "导入第二个固定版本 2.0.0");

        host.OnApply = _ => new HostApplyResult(false, "宿主应用失败：模拟安全重启未通过");
        // 已加载（活动使用）场景下应用只登记 pending；这里先让宿主不再加载任何副本，
        // 才能验证"宿主应用回调失败"这条失败路径本身。
        host.LoadedPaths.Clear();
        var failResult = await service.ApplyVersionAsync(demoId, v2.VersionId);
        check(!failResult.Success && failResult.Status == LocalPluginStatus.LoadError, "更新失败返回失败状态");
        var afterFail = await service.ListAsync();
        check(afterFail.Items[0].SelectedVersionId == imported.VersionId,
            "更新失败后旧选定版本保持不变");
        check(File.Exists(Path.Combine(service.Root, demoKey, imported.VersionId, "index.js")),
            "更新失败后旧版本文件仍在");
        check(afterFail.Items[0].LoadError?.Contains("宿主应用失败") == true, "更新失败保留诊断");
        // 失败的更新没有生效，因此不该凭空产生回退目标（不制造"回退到失败版本"的路径）。
        check(!afterFail.Items[0].CanRollback, "更新失败未生效时没有凭空产生的回退目标");

        // ── 6b. 成功更新后显式回退：走同一宿主门 ──
        host.OnApply = _ => new HostApplyResult(true);
        var successUpdate = await service.ApplyVersionAsync(demoId, v2.VersionId);
        check(successUpdate.Success, "第二次更新成功（宿主回调通过）");
        var afterUpdate = await service.ListAsync();
        check(afterUpdate.Items[0].SelectedVersionId == v2.VersionId, "更新成功后选定版本前进");
        check(afterUpdate.Items[0].PreviousVersionId == imported.VersionId,
            "更新成功后旧选定版本成为可回退目标");
        check(afterUpdate.Items[0].LoadError is null, "更新成功后清除上一次失败诊断");

        var appliesBeforeRollback = host.Applies.Count;
        host.LoadedPaths.Add(Path.Combine(service.Root, demoKey, v2.VersionId, "index.js"));
        var rollback = await service.RollbackAsync(demoId);
        check(rollback.Success || rollback.IsPending, "显式回退经同一宿主门返回");
        // 活动使用中回退只登记 pending：不调用宿主回调、不原地改/删加载目录。
        check(host.Applies.Count == appliesBeforeRollback, "活动使用中回退不调用宿主应用回调");
        var rollbackView = await service.ListAsync();
        check(rollbackView.Items[0].PendingApplyVersionId == imported.VersionId ||
              rollbackView.Items[0].SelectedVersionId == imported.VersionId,
            "回退目标登记为旧稳定版本");
        check(File.Exists(Path.Combine(service.Root, demoKey, v2.VersionId, "index.js")),
            "回退期间活动版本目录未被删除");
        host.LoadedPaths.Clear();
        var rollbackAgain = await service.RollbackAsync(demoId);
        check(rollbackAgain.Success || rollbackAgain.IsPending, "非活动时回退同样经宿主门");
        check(host.Applies.Count > appliesBeforeRollback, "非活动回退调用宿主回调（不直接改文件或杀进程）");
        var afterRollback = await service.ListAsync();
        check(afterRollback.Items[0].SelectedVersionId == imported.VersionId,
            "回退后选定版本回到旧稳定版本");
        var rollbackDiagnostics = await service.ListAsync();
        check(rollbackDiagnostics.Items[0].Versions.Count == 2,
            "回退保留两个固定版本与历史（不删除旧副本）");

        // ── 7. pending 不动活动目录 ──
        var pendingHost = new FakeHost();
        var pendingService = new LocalPluginService(new AppDataLayout(Path.Combine(root, "appdata-pending")), pendingHost);
        var p1 = await pendingService.ImportAsync(new LocalPluginImportRequest(source));
        await pendingService.ApplyVersionAsync(demoId, p1.VersionId);
        pendingHost.LoadedPaths.Add(Path.Combine(pendingService.Root, demoKey, p1.VersionId, "index.js"));
        var p2 = await pendingService.ImportAsync(new LocalPluginImportRequest(v2Source));
        var pendingApply = await pendingService.ApplyVersionAsync(demoId, p2.VersionId);
        check(pendingApply.IsPending, "活动使用中只登记 pending");
        check(pendingHost.Applies.Count == 1, "活动使用中未调用宿主应用回调");
        var pendingView = await pendingService.ListAsync();
        check(pendingView.Items[0].PendingApplyVersionId == p2.VersionId, "pending 版本已登记");
        var activeFile = Path.Combine(pendingService.Root, demoKey, p1.VersionId, "index.js");
        check(File.Exists(activeFile), "活动版本目录未被原地改动或删除");

        // 宿主从活动版本切换后，按真实快照确认 pending。
        pendingHost.LoadedPaths.Clear();
        pendingHost.LoadedPaths.Add(Path.Combine(pendingService.Root, demoKey, p2.VersionId, "index.js"));
        var resolved = await pendingService.ResolvePendingAsync();
        check(resolved >= 1, "宿主确认后 pending 被解析");
        var resolvedView = await pendingService.ListAsync();
        check(resolvedView.Items[0].SelectedVersionId == p2.VersionId, "确认后选定版本前进到 2.0.0");
        check(resolvedView.Items[0].PreviousVersionId == p1.VersionId, "历史版本保留，可供显式回退");

        // 快照不可用时只能登记 pending。
        pendingHost.SnapshotSuccess = false;
        var p3 = await pendingService.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "src v3", demoId, "演示插件", "3.0.0")));
        var unknownSnapshotApply = await pendingService.ApplyVersionAsync(demoId, p3.VersionId);
        check(unknownSnapshotApply.IsPending, "宿主快照不可用时只登记 pending，不假成功");
        pendingHost.SnapshotSuccess = true;

        // ── 8. 移除：只限自己托管且非活动；活动/未知先 pending ──
        var removeHost = new FakeHost();
        var removeService = new LocalPluginService(new AppDataLayout(Path.Combine(root, "appdata-remove")), removeHost);
        var r1 = await removeService.ImportAsync(new LocalPluginImportRequest(source));
        await removeService.ApplyVersionAsync(demoId, r1.VersionId);
        removeHost.LoadedPaths.Add(Path.Combine(removeService.Root, demoKey, r1.VersionId, "index.js"));
        var activeRemove = await removeService.RemoveVersionAsync(demoId, r1.VersionId);
        check(activeRemove.IsPending, "活动使用中的版本只登记待移除");
        check(File.Exists(Path.Combine(removeService.Root, demoKey, r1.VersionId, "index.js")),
            "活动版本文件未被删除");

        removeHost.LoadedPaths.Clear();
        var inactiveRemove = await removeService.RemoveVersionAsync(demoId, r1.VersionId);
        check(inactiveRemove.Success, "非活动托管版本可移除");
        check(!Directory.Exists(Path.Combine(removeService.Root, demoKey, r1.VersionId)),
            "移除后副本目录已删除");

        // 外部源与托管目录外的路径不可移除。
        var externalReject = await removeService.RemoveVersionAsync("no.such.plugin", "v");
        check(!externalReject.Success, "未托管插件不能被移除");
        check(Directory.Exists(source), "外部源目录被保留（不删除用户插件）");

        // ── 9. 并发：旧 generation 拒绝，不覆盖新状态 ──
        var concurrentRoot = Path.Combine(root, "appdata-concurrent");
        var cService = new LocalPluginService(new AppDataLayout(concurrentRoot), new FakeHost());
        var c1 = await cService.ImportAsync(new LocalPluginImportRequest(source));
        var staleGeneration = cService.CurrentGeneration()!.Value;
        var catalogPath = Path.Combine(cService.Root, PluginCatalogStore.CatalogFileName);
        var before = File.ReadAllText(catalogPath);
        // 另一个写入者推进代次：本服务持旧代次的写入必须被拒绝，且不产生任何副作用。
        var cNew = await cService.ImportAsync(new LocalPluginImportRequest(v2Source));
        check(cNew.Version == "2.0.0", "并发场景前置：另一个写入者已导入 2.0.0");
        var staleError = await CaptureAsync(() => cService.ImportAsync(
            new LocalPluginImportRequest(PluginFixture.Create(root, "src stale", demoId, "演示插件", "4.0.0")),
            expectedGeneration: staleGeneration));
        check(staleError is not null && staleError.Contains("代次"), "并发旧 generation 写入被拒绝");
        check(!Directory.Exists(Path.Combine(cService.Root, demoKey, "4.0.0")),
            "旧代次写入被拒绝时不产生副本副作用（先校验后代次提交）");
        check(File.ReadAllText(catalogPath) == File.ReadAllText(catalogPath) &&
              !File.ReadAllText(catalogPath).Contains("4.0.0"),
            "拒绝写入后目录状态未包含被拒绝的版本");
        var bumped = JsonNode.Parse(File.ReadAllText(catalogPath))!;
        bumped["Generation"] = 999;
        File.WriteAllText(catalogPath, bumped.ToJsonString(), new UTF8Encoding(false));
        var futureGenError = await CaptureAsync(() => cService.ImportAsync(
            new LocalPluginImportRequest(PluginFixture.Create(root, "src gen", demoId, "演示插件", "5.0.0")),
            expectedGeneration: 1));
        check(futureGenError is not null, "代次高于调用方预期同样拒绝（双向 CAS）");
        File.WriteAllText(catalogPath, before, new UTF8Encoding(false));

        // ── 10. 损坏/未来版本：不静默重建 ──
        var corruptRoot = Path.Combine(root, "appdata-corrupt");
        var corruptService = new LocalPluginService(new AppDataLayout(corruptRoot), new FakeHost());
        var cr1 = await corruptService.ImportAsync(new LocalPluginImportRequest(source));
        var corruptCatalog = Path.Combine(corruptService.Root, PluginCatalogStore.CatalogFileName);
        var goodCatalog = File.ReadAllText(corruptCatalog);
        File.WriteAllText(corruptCatalog, "{ not json", new UTF8Encoding(false));
        var corruptView = await corruptService.ListAsync();
        check(corruptView.IsCorrupt && corruptView.Items.Count == 0, "损坏目录状态：报告损坏，不返回伪造列表");
        var corruptError = await CaptureAsync(() => corruptService.ImportAsync(new LocalPluginImportRequest(v2Source)));
        check(corruptError is not null, "损坏状态下写入被拒绝");
        check(File.ReadAllText(corruptCatalog) == "{ not json", "损坏文件未被静默重建或覆盖");

        File.WriteAllText(corruptCatalog, goodCatalog, new UTF8Encoding(false));
        var recovered = await corruptService.ListAsync();
        check(!recovered.IsCorrupt && recovered.Items.Count == 1, "恢复有效状态后可正常读取（未丢失副本）");

        // 未来版本 schema 明确拒绝。
        var future = JsonNode.Parse(goodCatalog)!;
        future["SchemaVersion"] = 99;
        File.WriteAllText(corruptCatalog, future.ToJsonString(), new UTF8Encoding(false));
        var futureView = await corruptService.ListAsync();
        check(futureView.IsCorrupt && (futureView.CorruptReason?.Contains("99") ?? false),
            "未来版本目录状态：明确拒绝，不读取不重建");

        // ── 11. 文件锁：另一个进程持锁时本次不改动状态 ──
        var lockRoot = Path.Combine(root, "appdata-lock");
        var lockService = new LocalPluginService(new AppDataLayout(lockRoot), new FakeHost());
        await lockService.ImportAsync(new LocalPluginImportRequest(source));
        var lockDirectory = Path.Combine(lockService.Root, ".locks");
        // SessionLease 对 sessionId 做 SHA256(ToUpperInvariant)，与产品实现同一算法。
        var lockName = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes("plugins-local-catalog".ToUpperInvariant())));
        using (var held = new FileStream(Path.Combine(lockDirectory, lockName + ".lock"),
                   FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var lockedError = await CaptureAsync(() => lockService.ImportAsync(new LocalPluginImportRequest(v2Source)));
            check(lockedError is not null && lockedError.Contains("另一个进程"),
                "文件锁被占用：拒绝写入并说明，不改动状态");
        }

        // ── 12. 目录状态只写自己的根，不碰 desktop.db ──
        check(!File.Exists(Path.Combine(dataRoot, "desktop.db")), "模块数据根没有 desktop.db：未写用户数据库");
        check(File.ReadAllText(Path.Combine(source, "index.js")).Contains("demo.plugin 1.0.0"),
            "源目录内容全程未被改写");

        // ── 13. 返工反例 E3-VERSION-COMPAT-1：兼容声明按固定版本冻结 ──
        var compatHost = new FakeHost();   // 宿主 Pi 1.1.0
        var compatService = new LocalPluginService(new AppDataLayout(Path.Combine(root, "appdata-compat")), compatHost);
        var compatV1 = await compatService.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "compat v1", "alpha", "alpha", "1.0", supportedHostVersions: ["1.1.0"])));
        await compatService.ApplyVersionAsync("alpha", compatV1.VersionId);
        await compatService.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "compat v2", "alpha", "alpha", "2.0", supportedHostVersions: ["2.0.0"])));
        var compatListAfterImport = await compatService.ListAsync();
        check(compatListAfterImport.Items[0].Compatibility == PluginCompatibility.Compatible,
            "导入 v2 不改写 v1 的兼容门禁（按所选版本判定）");
        var v1Again = await compatService.ApplyVersionAsync("alpha", compatV1.VersionId);
        check(v1Again.Success, "导入 v2 后 v1 在正确宿主仍可应用");
        compatHost.Identity = HostIdentity.Pi("2.0.0");
        var appliesBeforeWrongHost = compatHost.Applies.Count;
        var wrongHost = await compatService.ApplyVersionAsync("alpha", compatV1.VersionId);
        check(!wrongHost.Success && compatHost.Applies.Count == appliesBeforeWrongHost,
            "v1 不得继承 v2 的宿主许可（错误宿主上拒绝且无宿主副作用）");
        var compatListWrongHost = await compatService.ListAsync();
        check(compatListWrongHost.Items[0].Compatibility == PluginCompatibility.Incompatible &&
              compatListWrongHost.Items[0].Status == LocalPluginStatus.Blocked,
            "列表按所选版本如实显示不兼容");
        // 切回正确宿主后 v2 仍按自己的声明工作（两版本声明互不污染）。
        compatHost.Identity = HostIdentity.Pi("1.1.0");
        var v2WrongHost = await compatService.ApplyVersionAsync("alpha", "2.0");
        check(!v2WrongHost.Success && compatHost.Applies.Count == appliesBeforeWrongHost,
            "v2 在 1.1.0 宿主上同样被拒（版本级声明双向生效）");

        // ── 14. 返工反例 E3-PENDING-REMOVE-1：pending 绑定具体版本，只读核对不删文件 ──
        var pendRemoveHost = new FakeHost();
        var pendRemove = new LocalPluginService(new AppDataLayout(Path.Combine(root, "appdata-pendremove")),
            pendRemoveHost);
        var pr1 = await pendRemove.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "pr v1", "beta", "beta", "1.0")));
        var pr2 = await pendRemove.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "pr v2", "beta", "beta", "2.0")));
        pendRemoveHost.SnapshotSuccess = false;
        var prPending = await pendRemove.RemoveVersionAsync("beta", pr1.VersionId);
        check(prPending.IsPending, "快照未知时移除只登记 pending");
        var prPendList = await pendRemove.ListAsync();
        check(prPendList.Items[0].PendingRemoveVersionId == pr1.VersionId,
            "pending 绑定被请求的具体版本（不是整个插件）");
        pendRemoveHost.SnapshotSuccess = true;
        await pendRemove.ResolvePendingAsync();
        var prAfterResolve = await pendRemove.ListAsync();
        check(prAfterResolve.Items[0].Versions.Any(v => v.VersionId == pr2.VersionId) &&
              File.Exists(Path.Combine(pendRemove.Root, "beta", pr2.DirectoryName, "index.js")),
            "按快照核对不得删除未被请求的版本 v2");
        check(prAfterResolve.Items[0].Versions.Any(v => v.VersionId == pr1.VersionId) &&
              File.Exists(Path.Combine(pendRemove.Root, "beta", pr1.DirectoryName, "index.js")),
            "只读核对入口不删除任何文件（v1 仍在）");
        check(prAfterResolve.Items[0].PendingRemoveReady,
            "宿主确认未使用后 pending 目标标记为可执行移除");
        var prExplicit = await pendRemove.RemoveVersionAsync("beta", pr1.VersionId);
        check(prExplicit.Success, "显式移除只作用于原目标版本");
        var prFinal = await pendRemove.ListAsync();
        check(prFinal.Items[0].Versions.Single().VersionId == pr2.VersionId,
            "显式移除后只剩未请求的 v2");

        // ── 15. 返工反例 E3-REMOVE-FAILURE-1：删除失败如实报告并保目录状态 ──
        var lockRemoveHost = new FakeHost();
        var lockRemove = new LocalPluginService(new AppDataLayout(Path.Combine(root, "appdata-rmfail")),
            lockRemoveHost);
        var lr1 = await lockRemove.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "lr v1", "gamma", "gamma", "1.0")));
        var lockedEntry = Path.Combine(lockRemove.Root, "gamma", lr1.DirectoryName, "index.js");
        using (var held = new FileStream(lockedEntry, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var lockedRemoval = await lockRemove.RemoveVersionAsync("gamma", lr1.VersionId);
            check(!lockedRemoval.Success, "真实文件锁：移除必须报告失败而不是成功");
            check(lockedRemoval.Message.Contains("保留") && lockedRemoval.Message.Contains("可重试"),
                "移除失败给出可恢复说明，不吞异常");
            var lockedList = await lockRemove.ListAsync();
            check(lockedList.Items.Count == 1 && lockedList.Items[0].Versions.Any(v => v.VersionId == lr1.VersionId),
                "移除失败后目录记录仍保留该版本（未写成已移除）");
            check(File.Exists(lockedEntry), "移除失败后文件仍在");
            check(lockedList.Items[0].LoadError?.Contains("移除失败") == true,
                "移除失败保留诊断线索");
        }
        var retryRemoval = await lockRemove.RemoveVersionAsync("gamma", lr1.VersionId);
        check(retryRemoval.Success, "释放锁后重试移除可收敛");
        var retryList = await lockRemove.ListAsync();
        check(retryList.Items.Count == 0, "收敛后记录与副本都已移除");

        // ── 15b. 目录状态提交失败：如实报告且重试可收敛 ──
        var commitHost = new FakeHost();
        var commitRoot = Path.Combine(root, "appdata-rmcommit");
        var commitService = new LocalPluginService(new AppDataLayout(commitRoot), commitHost);
        var cm1 = await commitService.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "cm v1", "iota", "iota", "1.0")));
        var commitCatalogPath = Path.Combine(commitService.Root, PluginCatalogStore.CatalogFileName);
        var cmFile = Path.Combine(commitService.Root, "iota", cm1.DirectoryName, "index.js");
        using (var blockedCatalog = new FileStream(commitCatalogPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var commitFailure = await commitService.RemoveVersionAsync("iota", cm1.VersionId);
            check(!commitFailure.Success, "目录状态不可写时移除必须报告失败，不宣称成功");
            check(commitFailure.Message.Contains("目录状态") && commitFailure.Message.Contains("可重试"),
                "状态不可用时给出可恢复说明");
            check(File.Exists(cmFile), "目录状态不可用时在删除之前失败：文件未被删除");
            check(commitFailure.Rejection == LocalPluginRejectionKind.LockedOrCorrupt,
                "状态不可用归类为锁定/损坏，便于上层区分");
        }
        var commitRetry = await commitService.RemoveVersionAsync("iota", cm1.VersionId);
        check(commitRetry.Success, "状态恢复后重试移除收敛");
        var commitFinal = await commitService.ListAsync();
        check(commitFinal.Items.Count == 0, "收敛后记录已清理");

        // ── 15c. 待移除目标与当前使用保护跨重开保留 ──
        var persistHost = new FakeHost();
        var persistRoot = Path.Combine(root, "appdata-pendingpersist");
        var persistFirst = new LocalPluginService(new AppDataLayout(persistRoot), persistHost);
        var pp1 = await persistFirst.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "pp v1", "kappa", "kappa", "1.0")));
        var pp2 = await persistFirst.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "pp v2", "kappa", "kappa", "2.0")));
        persistHost.SnapshotSuccess = false;
        await persistFirst.RemoveVersionAsync("kappa", pp1.VersionId);
        var persistSecond = new LocalPluginService(new AppDataLayout(persistRoot),
            new FakeHost { SnapshotSuccess = true });
        var persistList = await persistSecond.ListAsync();
        check(persistList.Items.Single().PendingRemoveVersionId == pp1.VersionId,
            "待移除目标跨重开保留（仍绑定原版本）");
        check(persistList.Items.Single().Versions.Count == 2, "重开后两个版本都还在（未删文件）");
        var persistSecondInstance = await persistSecond.RemoveVersionAsync("kappa", pp2.VersionId);
        check(persistSecondInstance.Success, "重开后可移除未使用的另一个版本");
        var persistAfter = await persistSecond.ListAsync();
        check(persistAfter.Items.Single().PendingRemoveVersionId == pp1.VersionId &&
              persistAfter.Items.Single().Versions.Single().VersionId == pp1.VersionId,
            "移除 v2 不影响仍待移除的 v1 目标");

        // ── 16. 返工反例 E3-STALE-APPLY-1：跨 await 操作身份 ──
        var raceHost = new FakeHost();
        var raceService = new LocalPluginService(new AppDataLayout(Path.Combine(root, "appdata-race")), raceHost);
        var raceV1 = await raceService.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "race v1", "delta", "delta", "1.0")));
        var raceV2 = await raceService.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "race v2", "delta", "delta", "2.0")));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HostApplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        raceHost.OnApplyAsync = (_, version) =>
        {
            if (version == "1.0") { entered.TrySetResult(); return release.Task; }
            return Task.FromResult(new HostApplyResult(true));
        };
        var olderApply = raceService.ApplyVersionAsync("delta", raceV1.VersionId);
        await entered.Task;
        var newerApply = await raceService.ApplyVersionAsync("delta", raceV2.VersionId);
        check(newerApply.Success, "竞争场景：较新的应用请求成功");
        release.SetResult(new HostApplyResult(true));
        var olderResult = await olderApply;
        var raceList = await raceService.ListAsync();
        check(raceList.Items.Single().SelectedVersionId == raceV2.VersionId,
            "旧 v1 迟到完成不得覆盖较新的 v2 选定/回退目标");
        check(!olderResult.Success && olderResult.Rejection == LocalPluginRejectionKind.StaleOperation,
            "迟到的旧结果明确标记为被取代，不宣称成功");
        check(raceList.Items.Single().PendingApplyVersionId is null,
            "旧结果不得清掉或改写较新操作的 pending");

        // 竞争中的失败回调同样不得覆盖较新决定。
        var enteredFail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFail = new TaskCompletionSource<HostApplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        raceHost.OnApplyAsync = (_, version) =>
        {
            if (version == "1.0") { enteredFail.TrySetResult(); return releaseFail.Task; }
            return Task.FromResult(new HostApplyResult(true));
        };
        var staleFail = raceService.ApplyVersionAsync("delta", raceV1.VersionId);
        await enteredFail.Task;
        await raceService.ApplyVersionAsync("delta", raceV2.VersionId);
        releaseFail.SetResult(new HostApplyResult(false, "旧操作的失败"));
        var staleFailResult = await staleFail;
        var raceList2 = await raceService.ListAsync();
        check(staleFailResult.Rejection == LocalPluginRejectionKind.StaleOperation &&
              raceList2.Items.Single().SelectedVersionId == raceV2.VersionId &&
              raceList2.Items.Single().LoadError is null,
            "旧失败的诊断不得污染较新成功后的状态");

        // 竞争中的异常回调同样有界：不覆盖新状态，且不把异常抛出到调用方之外。
        var enteredThrow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseThrow = new TaskCompletionSource<HostApplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        raceHost.OnApplyAsync = (_, version) =>
        {
            if (version == "1.0") { enteredThrow.TrySetResult(); return releaseThrow.Task; }
            return Task.FromResult(new HostApplyResult(true));
        };
        var staleThrow = raceService.ApplyVersionAsync("delta", raceV1.VersionId);
        await enteredThrow.Task;
        await raceService.ApplyVersionAsync("delta", raceV2.VersionId);
        releaseThrow.TrySetException(new InvalidOperationException("旧操作宿主异常"));
        var staleThrowResult = await staleThrow;
        var raceList3 = await raceService.ListAsync();
        check(staleThrowResult.Rejection == LocalPluginRejectionKind.StaleOperation &&
              raceList3.Items.Single().SelectedVersionId == raceV2.VersionId &&
              raceList3.Items.Single().LoadError is null,
            "旧异常在有界保护内被吸收，不覆盖较新状态、不外泄为未处理异常");

        // 应用等待期间移除不得删除候选文件。
        var busyHost = new FakeHost();
        var busyService = new LocalPluginService(new AppDataLayout(Path.Combine(root, "appdata-busyapply")), busyHost);
        var busyV1 = await busyService.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "busy v1", "epsilon", "epsilon", "1.0")));
        var busyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busyRelease = new TaskCompletionSource<HostApplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        busyHost.OnApplyAsync = (_, _) => { busyEntered.TrySetResult(); return busyRelease.Task; };
        var busyApply = busyService.ApplyVersionAsync("epsilon", busyV1.VersionId);
        await busyEntered.Task;
        var busyRemoval = await busyService.RemoveVersionAsync("epsilon", busyV1.VersionId);
        check(!busyRemoval.Success && busyRemoval.Rejection == LocalPluginRejectionKind.OperationBusy,
            "应用等待期间移除候选被明确拒绝");
        check(File.Exists(Path.Combine(busyService.Root, "epsilon", busyV1.DirectoryName, "index.js")),
            "应用等待期间候选文件未被删除");
        busyRelease.SetResult(new HostApplyResult(true));
        await busyApply;
        var busyAfter = await busyService.ListAsync();
        check(busyAfter.Items.Single().SelectedVersionId == busyV1.VersionId,
            "被拒绝的移除未破坏在途应用的结果");

        // 取消：有界终止并回收自己的 pending。
        var cancelHost = new FakeHost();
        var cancelService = new LocalPluginService(new AppDataLayout(Path.Combine(root, "appdata-cancel")), cancelHost);
        var cancelV1 = await cancelService.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "cancel v1", "zeta", "zeta", "1.0")));
        using var cancelSource = new CancellationTokenSource();
        var cancelEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cancelHost.OnApplyAsync = async (_, _) =>
        {
            cancelEntered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancelSource.Token);
            return new HostApplyResult(true);
        };
        var cancelledApply = cancelService.ApplyVersionAsync("zeta", cancelV1.VersionId, cancelSource.Token);
        await cancelEntered.Task;
        cancelSource.Cancel();
        var cancelledResult = await cancelledApply;
        var cancelList = await cancelService.ListAsync();
        check(!cancelledResult.Success && cancelList.Items.Single().PendingApplyVersionId is null,
            "取消后回收自己的 pending（有界终止，不残留进行中）");
        check(cancelList.Items.Single().SelectedVersionId is null,
            "取消不伪造选定版本");

        // 其他 VM 实例竞争：令牌持久化，第二个服务实例的新决定同样使旧结果过期。
        var crossHost = new FakeHost();
        var crossDataRoot = Path.Combine(root, "appdata-crossvm");
        var crossLayout = new AppDataLayout(crossDataRoot);
        var crossA = new LocalPluginService(crossLayout, crossHost);
        var crossB = new LocalPluginService(crossLayout, crossHost);
        var crossV1 = await crossA.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "cross v1", "eta", "eta", "1.0")));
        var crossV2 = await crossA.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "cross v2", "eta", "eta", "2.0")));
        var crossEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var crossRelease = new TaskCompletionSource<HostApplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        crossHost.OnApplyAsync = (_, version) =>
        {
            if (version == "1.0") { crossEntered.TrySetResult(); return crossRelease.Task; }
            return Task.FromResult(new HostApplyResult(true));
        };
        var crossApplyV1 = crossA.ApplyVersionAsync("eta", crossV1.VersionId);
        await crossEntered.Task;
        var crossApplyV2 = await crossB.ApplyVersionAsync("eta", crossV2.VersionId);
        check(crossApplyV2.Success, "另一实例可以推进较新的决定");
        crossRelease.SetResult(new HostApplyResult(true));
        var crossOldResult = await crossApplyV1;
        var crossList = await crossA.ListAsync();
        check(crossList.Items.Single().SelectedVersionId == crossV2.VersionId &&
              crossOldResult.Rejection == LocalPluginRejectionKind.StaleOperation,
            "另一服务实例的新决定不被旧实例的迟到结果覆盖");

        // 版本级声明跨重开保留：新实例读回同一门禁，不受后来导入影响。
        var reopenRoot = Path.Combine(root, "appdata-reopen");
        var reopenFirst = new LocalPluginService(new AppDataLayout(reopenRoot), new FakeHost());
        var reopenV1 = await reopenFirst.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "reopen v1", "theta", "theta", "1.0", supportedHostVersions: ["1.1.0"])));
        await reopenFirst.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "reopen v2", "theta", "theta", "2.0", supportedHostVersions: ["2.0.0"])));
        var reopenSecond = new LocalPluginService(new AppDataLayout(reopenRoot),
            new FakeHost { Identity = HostIdentity.Pi("1.1.0") });
        var reopenList = await reopenSecond.ListAsync();
        var reopenV1Back = reopenList.Items.Single().Versions.Single(v => v.VersionId == reopenV1.VersionId);
        check(reopenV1Back.DeclaredHostVersions.Count == 1 && reopenV1Back.DeclaredHostVersions[0] == "1.1.0",
            "版本级声明跨重开保留（未被 v2 改写）");
        check(reopenList.Items.Single().Compatibility == PluginCompatibility.Compatible,
            "重开后按所选版本判定兼容");

        // ── 17. 返工二 E3-SUPERSEDED-INFLIGHT-1：在途候选的实际资源保护 ──
        var inflightHost = new FakeHost();
        var inflightDataRoot = Path.Combine(root, "appdata-inflight");
        var inflightLayout = new AppDataLayout(inflightDataRoot);
        var inflightService = new LocalPluginService(inflightLayout, inflightHost);
        var inflightOther = new LocalPluginService(inflightLayout, inflightHost);
        var if1 = await inflightService.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "if v1", "alpha2", "alpha2", "1.0")));
        var if2 = await inflightService.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "if v2", "alpha2", "alpha2", "2.0")));
        var if1File = Path.Combine(inflightService.Root, "alpha2", if1.DirectoryName, "index.js");
        var ifEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ifFinish = new TaskCompletionSource<HostApplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        inflightHost.OnApplyAsync = (_, version) =>
        {
            if (version == "1.0") { ifEntered.TrySetResult(); return ifFinish.Task; }
            return Task.FromResult(new HostApplyResult(true));
        };
        var ifFirst = inflightService.ApplyVersionAsync("alpha2", if1.VersionId);
        await ifEntered.Task;
        var ifSecond = await inflightService.ApplyVersionAsync("alpha2", if2.VersionId);
        check(ifSecond.Success, "较新的应用请求照常被接受（不删除竞争场景）");
        var inflightRemove = await inflightService.RemoveVersionAsync("alpha2", if1.VersionId);
        check(!inflightRemove.Success && inflightRemove.Rejection == LocalPluginRejectionKind.OperationBusy,
            "旧宿主回调仍在运行时，其候选被明确拒绝移除");
        check(File.Exists(if1File), "在途候选文件未被删除");
        var otherInstanceRemove = await inflightOther.RemoveVersionAsync("alpha2", if1.VersionId);
        check(!otherInstanceRemove.Success && File.Exists(if1File),
            "另一个服务实例同样不能在途删除该候选（在途登记持久化）");
        ifFinish.SetResult(new HostApplyResult(true));
        var ifFirstResult = await ifFirst;
        check(ifFirstResult.Rejection == LocalPluginRejectionKind.StaleOperation,
            "旧回调迟到完成判为被取代");
        var ifAfter = await inflightService.ListAsync();
        check(ifAfter.Items.Single().SelectedVersionId == if2.VersionId,
            "在途保护不影响较新决定生效");
        check(ifAfter.Items.Single().InFlightVersionIds.Count == 0, "回调返回后在途登记已回收");
        var ifRemoveAfter = await inflightService.RemoveVersionAsync("alpha2", if1.VersionId);
        check(ifRemoveAfter.Success, "回调结束后可显式移除旧候选");
        check(!File.Exists(if1File), "回调结束后移除真正删除文件");

        // 宿主显式对账回收（进程异常结束等场景），避免永久保护。
        var abandonHost = new FakeHost();
        var abandonService = new LocalPluginService(new AppDataLayout(Path.Combine(root, "appdata-abandon")),
            abandonHost);
        var ab1 = await abandonService.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "ab v1", "omega", "omega", "1.0")));
        var abEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abNever = new TaskCompletionSource<HostApplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        abandonHost.OnApplyAsync = (_, _) => { abEntered.TrySetResult(); return abNever.Task; };
        var abApply = abandonService.ApplyVersionAsync("omega", ab1.VersionId);
        await abEntered.Task;
        var abNoReason = await abandonService.AbandonInFlightAsync("omega", ab1.VersionId, "  ");
        check(!abNoReason.Success, "对账回收必须给出宿主证据说明");
        var abBlocked = await abandonService.RemoveVersionAsync("omega", ab1.VersionId);
        check(!abBlocked.Success, "未对账时仍拒绝删除在途候选");
        var abReclaim = await abandonService.AbandonInFlightAsync("omega", ab1.VersionId,
            "宿主已确认该候选未被加载且不会有后续副作用");
        check(abReclaim.Success, "宿主显式对账后可回收在途登记");
        var abRemove = await abandonService.RemoveVersionAsync("omega", ab1.VersionId);
        check(abRemove.Success, "对账后移除可执行（不永久锁死）");
        abNever.TrySetResult(new HostApplyResult(true));
        await abApply;

        // ── 18. 返工二 E3-CANCELLED-COMPLETION-1：自己的取消 vs 被新操作取代 ──
        var cancelHost2 = new FakeHost();
        var cancelService2 = new LocalPluginService(new AppDataLayout(Path.Combine(root, "appdata-cancel2")),
            cancelHost2);
        var cc1 = await cancelService2.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "cc v1", "sigma", "sigma", "1.0")));
        using var cts2 = new CancellationTokenSource();
        cancelHost2.OnApplyAsync = (_, _) => { cts2.Cancel(); return Task.FromResult(new HostApplyResult(true)); };
        var cancelledSuccess = await cancelService2.ApplyVersionAsync("sigma", cc1.VersionId, cts2.Token);
        var ccState = PluginCatalogStore.Load(cancelService2.Root).State!;
        var ccRecord = ccState.Plugins.Single(p => p.Id == "sigma");
        check(ccRecord.OperationToken is null && ccRecord.PendingApplyVersionId is null,
            "宿主返回成功但调用方已取消：不留永久在途令牌/pending");
        check(ccRecord.InFlight.Count == 0, "该分支同样回收在途登记");
        check(cancelledSuccess.Rejection != LocalPluginRejectionKind.StaleOperation,
            "自己的取消不误报为被新操作取代");
        check(!cancelledSuccess.Success && ccRecord.SelectedVersionId is null,
            "宿主可能已成功时不假称成功、不伪造选定");
        check(ccRecord.LastApplyReceipt?.Outcome == "cancelled_unknown" &&
              ccRecord.LastApplyReceipt.VersionId == cc1.VersionId,
            "保留可只读核对的原身份回执（不自动重发）");
        var ccList = await cancelService2.ListAsync();
        check(ccList.Items.Single().LoadError?.Contains("结果未知") == true,
            "取消但宿主已返回：如实登记为结果未知");

        // 取消以异常形式返回时同样有界回收并保留回执。
        var cancelHost3 = new FakeHost();
        var cancelService3 = new LocalPluginService(new AppDataLayout(Path.Combine(root, "appdata-cancel3")),
            cancelHost3);
        var cd1 = await cancelService3.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "cd v1", "tau", "tau", "1.0")));
        using var cts3 = new CancellationTokenSource();
        cancelHost3.OnApplyAsync = async (_, _) =>
        {
            cts3.Cancel();
            await Task.Yield();
            throw new OperationCanceledException(cts3.Token);
        };
        var cancelledThrow = await cancelService3.ApplyVersionAsync("tau", cd1.VersionId, cts3.Token);
        var cdState = PluginCatalogStore.Load(cancelService3.Root).State!;
        var cdRecord = cdState.Plugins.Single(p => p.Id == "tau");
        check(cdRecord.OperationToken is null && cdRecord.InFlight.Count == 0 && cdRecord.PendingApplyVersionId is null,
            "取消异常分支同样清理干净（无永久在途）");
        check(!cancelledThrow.Success && cancelledThrow.Rejection == LocalPluginRejectionKind.OperationCancelled,
            "取消异常分支如实报取消，不报被取代");
        check(cdRecord.LastApplyReceipt?.Outcome == "cancelled_unknown", "取消异常分支保留回执");

        // ── 19. 返工二 E3-FIXED-CONTENT-1：应用/回退前复核固定摘要与入口 ──
        var integrityHost = new FakeHost();
        var integrityService = new LocalPluginService(new AppDataLayout(Path.Combine(root, "appdata-integrity")),
            integrityHost);
        var ig1 = await integrityService.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "ig v1", "gamma2", "gamma2", "1.0", entryBody: "// original\n")));
        var igEntry = Path.Combine(integrityService.Root, "gamma2", ig1.DirectoryName, "index.js");
        File.WriteAllText(igEntry, "// replaced after import\n", new UTF8Encoding(false));
        var tampered = await integrityService.ApplyVersionAsync("gamma2", ig1.VersionId);
        check(!tampered.Success && tampered.Rejection == LocalPluginRejectionKind.IntegrityMismatch,
            "被改写的托管副本拒绝应用");
        check(integrityHost.Applies.Count == 0, "摘要不符时没有任何宿主副作用");
        var igList = await integrityService.ListAsync();
        check(igList.Items.Single().Versions.Single().ContentHash == ig1.ContentHash,
            "拒绝应用时不改写旧摘要凑通过");
        var igState = PluginCatalogStore.Load(integrityService.Root).State!;
        check(igState.Plugins.Single().Versions.Single().IntegrityError is not null,
            "篡改诊断写入版本记录");
        var igBlockedRollback = await integrityService.RollbackAsync("gamma2");
        check(!igBlockedRollback.Success, "回退同样经过完整性门禁");

        // 入口被删除（自定义入口）时拒绝应用。
        var entryGoneSource = PluginFixture.Create(root, "ig-entry", "gamma3", "gamma3", "1.0",
            entry: "main.js", entryBody: "// main\n");
        File.WriteAllText(Path.Combine(entryGoneSource, "alt.js"), "// alt\n", new UTF8Encoding(false));
        var ig3 = await integrityService.ImportAsync(new LocalPluginImportRequest(entryGoneSource, "alt.js"));
        File.Delete(Path.Combine(integrityService.Root, "gamma3", ig3.DirectoryName, "alt.js"));
        var entryMissing = await integrityService.ApplyVersionAsync("gamma3", ig3.VersionId);
        check(!entryMissing.Success && integrityHost.Applies.Count == 0,
            "入口缺失时拒绝应用且无宿主副作用");

        // 恢复原始字节后可以正常应用（不把误报变成永久损坏）。
        File.WriteAllText(igEntry, "// original\n", new UTF8Encoding(false));
        var restored = await integrityService.ApplyVersionAsync("gamma2", ig1.VersionId);
        check(restored.Success, "恢复导入时字节后可正常应用");
        check(integrityHost.Applies.Count == 1, "恢复后宿主回调恰好被调用一次");

        // 在途登记与回执跨重开保留（供宿主只读核对）。
        var receiptRoot = Path.Combine(root, "appdata-receipt");
        var receiptFirst = new LocalPluginService(new AppDataLayout(receiptRoot), new FakeHost());
        var rc1 = await receiptFirst.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "rc v1", "upsilon", "upsilon", "1.0")));
        await receiptFirst.ApplyVersionAsync("upsilon", rc1.VersionId);
        var receiptSecond = new LocalPluginService(new AppDataLayout(receiptRoot),
            new FakeHost { Identity = HostIdentity.Pi("1.1.0") });
        var receiptState = PluginCatalogStore.Load(receiptSecond.Root).State!;
        check(receiptState.Plugins.Single().LastApplyReceipt?.Outcome == "confirmed",
            "应用回执跨重开保留（confirmed）");

        // ── 20. 返工三 E3-RECEIPT-IDENTITY-1：回执身份绑定接受请求时的原版本与自己的令牌 ──
        var idHost = new FakeHost();
        var idService = new LocalPluginService(new AppDataLayout(Path.Combine(root, "appdata-receiptid")), idHost);
        var idV1 = await idService.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "id v1", "rho", "rho", "1.0")));
        var idV2 = await idService.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "id v2", "rho", "rho", "2.0")));
        var idEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var idRelease = new TaskCompletionSource<HostApplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        idHost.OnApplyAsync = (_, version) =>
        {
            if (version == "1.0") { idEntered.TrySetResult(); return idRelease.Task; }
            return Task.FromResult(new HostApplyResult(true));
        };
        var idFirst = idService.ApplyVersionAsync("rho", idV1.VersionId);
        await idEntered.Task;
        await idService.ApplyVersionAsync("rho", idV2.VersionId);
        var idStateBefore = PluginCatalogStore.Load(idService.Root).State!.Plugins.Single();
        var confirmedReceipt = idStateBefore.LastApplyReceipt!;
        check(confirmedReceipt.VersionId == idV2.VersionId && confirmedReceipt.Outcome == "confirmed",
            "较新请求的确认回执已记录且身份为 v2");
        idRelease.TrySetResult(new HostApplyResult(false, "actual failure of version 1"));
        await idFirst;
        var idStateAfter = PluginCatalogStore.Load(idService.Root).State!.Plugins.Single();
        check(idStateAfter.LastApplyReceipt!.Token == confirmedReceipt.Token &&
              idStateAfter.LastApplyReceipt.VersionId == idV2.VersionId &&
              idStateAfter.LastApplyReceipt.Outcome == "confirmed",
            "迟到旧结果不顶掉已确认的新回执（最新回执保持 v2/confirmed）");
        var lateHistory = idStateAfter.RecentReceipts.Single(r => r.Token != confirmedReceipt.Token);
        check(lateHistory.VersionId == idV1.VersionId && lateHistory.Outcome == "failed" &&
              lateHistory.Message == "actual failure of version 1",
            "迟到旧结果按自己的原身份（v1/token1/failed）进历史，不冒充 v2 失败");
        check(idStateAfter.RecentReceipts.All(r =>
                !(r.Token != confirmedReceipt.Token && r.VersionId == idV2.VersionId)),
            "历史中不存在「旧令牌配新版本」的身份污染");

        // 迟到成功同样按原身份记录。
        var idEntered2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var idRelease2 = new TaskCompletionSource<HostApplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        idHost.OnApplyAsync = (_, version) =>
        {
            if (version == "1.0") { idEntered2.TrySetResult(); return idRelease2.Task; }
            return Task.FromResult(new HostApplyResult(true));
        };
        var idLate = idService.ApplyVersionAsync("rho", idV1.VersionId);
        await idEntered2.Task;
        await idService.ApplyVersionAsync("rho", idV2.VersionId);
        idRelease2.TrySetResult(new HostApplyResult(true));
        await idLate;
        var idState3 = PluginCatalogStore.Load(idService.Root).State!.Plugins.Single();
        var lateSuccess = idState3.RecentReceipts.Last(r => r.VersionId == idV1.VersionId);
        check(lateSuccess.Outcome == "confirmed" && lateSuccess.Token != idState3.LastApplyReceipt!.Token &&
              idState3.LastApplyReceipt.VersionId == idV2.VersionId,
            "迟到成功按 v1 原身份入历史，不覆盖 v2 的最新回执");

        // 取消分支的回执身份也必须是原请求版本，而不是当前 pending。
        var idCancelHost = new FakeHost();
        var idCancelService = new LocalPluginService(new AppDataLayout(Path.Combine(root, "appdata-receiptid2")),
            idCancelHost);
        var ic1 = await idCancelService.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "ic v1", "phi", "phi", "1.0")));
        using var idCts = new CancellationTokenSource();
        idCancelHost.OnApplyAsync = (_, _) => { idCts.Cancel(); return Task.FromResult(new HostApplyResult(true)); };
        await idCancelService.ApplyVersionAsync("phi", ic1.VersionId, idCts.Token);
        var icState = PluginCatalogStore.Load(idCancelService.Root).State!.Plugins.Single();
        check(icState.LastApplyReceipt!.VersionId == ic1.VersionId &&
              icState.LastApplyReceipt.Outcome == "cancelled_unknown",
            "取消分支回执身份为原请求版本且结果为 cancelled_unknown");
        check(icState.RecentReceipts.Any(r => r.VersionId == ic1.VersionId && r.Outcome == "cancelled_unknown"),
            "取消分支同时保留按身份的receipt历史");

        // 完整性拒绝分支：回执身份为被拒绝的原版本，且历史保留。
        var idIntegrityHost = new FakeHost();
        var idIntegrityService = new LocalPluginService(
            new AppDataLayout(Path.Combine(root, "appdata-receiptid3")), idIntegrityHost);
        var ii1 = await idIntegrityService.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "ii v1", "chi", "chi", "1.0", entryBody: "// keep\n")));
        File.WriteAllText(Path.Combine(idIntegrityService.Root, "chi", ii1.DirectoryName, "index.js"),
            "// tampered\n", new UTF8Encoding(false));
        await idIntegrityService.ApplyVersionAsync("chi", ii1.VersionId);
        var iiState = PluginCatalogStore.Load(idIntegrityService.Root).State!.Plugins.Single();
        check(iiState.LastApplyReceipt!.VersionId == ii1.VersionId &&
              iiState.LastApplyReceipt.Outcome == "integrity_rejected",
            "完整性拒绝回执绑定被拒绝的原版本身份");

        // 对账回收后的迟到结果：仍按原身份入历史，不从当前 pending 猜归属。
        var idAbandonHost = new FakeHost();
        var idAbandonService = new LocalPluginService(
            new AppDataLayout(Path.Combine(root, "appdata-receiptid4")), idAbandonHost);
        var ia1 = await idAbandonService.ImportAsync(new LocalPluginImportRequest(
            PluginFixture.Create(root, "ia v1", "psi", "psi", "1.0")));
        var iaEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var iaRelease = new TaskCompletionSource<HostApplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        idAbandonHost.OnApplyAsync = (_, _) => { iaEntered.TrySetResult(); return iaRelease.Task; };
        var iaApply = idAbandonService.ApplyVersionAsync("psi", ia1.VersionId);
        await iaEntered.Task;
        await idAbandonService.AbandonInFlightAsync("psi", ia1.VersionId, "宿主确认无后续副作用");
        iaRelease.TrySetResult(new HostApplyResult(false, "late failure after abandon"));
        await iaApply;
        var iaState = PluginCatalogStore.Load(idAbandonService.Root).State!.Plugins.Single();
        check(iaState.RecentReceipts.Any(r => r.VersionId == ia1.VersionId && r.Outcome == "failed"),
            "对账回收后的迟到结果按原身份入历史（不猜归属）");
        check(iaState.LastApplyReceipt is null || iaState.LastApplyReceipt.VersionId == ia1.VersionId,
            "对账场景的最新回执不携带其他版本身份");
    }

    private static async Task<string?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception error)
        {
            // 返回整条异常链：包装异常的中文说明在链上，只看最内层会漏掉。
            var messages = new List<string>();
            for (Exception? current = error; current is not null; current = current.InnerException)
            {
                if (current.Message.Length > 0) messages.Add(current.Message);
            }

            return string.Join(" / ", messages);
        }
    }

    /// <summary>用 mklink /J 建立 junction；符号链接在本机可能需要权限，失败即跳过该反例。</summary>
    private static bool MakeLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
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
}
