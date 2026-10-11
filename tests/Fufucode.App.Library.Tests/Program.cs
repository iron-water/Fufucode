using System.Text;
using Fufucode.App.Features.Library;

// O3 资料库定向测试（独立入口，未注册到全量自测）：
//   dotnet run --project tests/Fufucode.App.Library.Tests
// 覆盖任务书验收点：中文路径/同名文件、重复导入、源移动/删除、内容变化、
// 索引重建、批量导出/再导入、删除不删源、引用可回溯、编码与不支持的格式。
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var work = Path.Combine(root, "tmp", "o3-library-tests", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
var source = Path.Combine(work, "源资料");
var libraryRoot = Path.Combine(work, "library");
var exportRoot = Path.Combine(work, "导出包");
Directory.CreateDirectory(source);

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    passed++;
    Console.WriteLine("PASS " + name);
}

try
{
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    using var service = new LibraryService(libraryRoot);
    Check(File.Exists(service.DatabasePath), "库数据库创建于独立目录：" + service.DatabasePath);
    Check(service.DatabasePath.StartsWith(libraryRoot, StringComparison.OrdinalIgnoreCase), "库数据库不与 desktop.db 混用");

    // 1. 中文文件名/中文目录 + 副本收藏
    var chinese = Path.Combine(source, "季度报告 2026.txt");
    await File.WriteAllTextAsync(chinese, "第一季度报告：营收增长，包含中文关键词 季度报告。\n", new UTF8Encoding(false));
    var copyImport = await service.ImportAsync(new LibraryImportRequest(LibraryEntryKind.File, LibraryStorageMode.Copy, chinese, Tags: new[] { "工作", "财务" }));
    Check(copyImport.IsImported && copyImport.EntryId is not null, "中文路径文件导入成功");
    var copyEntry = service.GetEntry(copyImport.EntryId!)!;
    Check(copyEntry.Title == "季度报告 2026.txt", "标题取自中文文件名");
    Check(copyEntry.HasLocalCopy && File.Exists(copyEntry.StoredPath!), "副本内容已写入资料库");
    Check(copyEntry.Tags.SequenceEqual(new[] { "工作", "财务" }), "标签写入正确");
    Check(copyEntry.TextState == LibraryTextState.Indexed, "副本已索引");
    Check(service.GetPreviewText(copyEntry.Id)!.Contains("营收增长"), "预览文本可读");

    // 2. 全文检索（中文 bigram）
    var hits = service.Search("季度报告");
    Check(hits.Any(hit => hit.Entry.Id == copyEntry.Id), "中文全文检索命中");
    Check(hits[0].Snippet is { Length: > 0 }, "检索返回片段");
    Check(service.Search("季度报告", new[] { "财务" }).Any(hit => hit.Entry.Id == copyEntry.Id), "标签过滤保留命中");
    Check(service.Search("季度报告", new[] { "不存在标签" }).Count == 0, "标签过滤排除不匹配条目");

    // 3. 同名文件（不同目录、不同内容）
    var otherDirectory = Path.Combine(source, "另一目录");
    Directory.CreateDirectory(otherDirectory);
    var sameNameA = Path.Combine(source, "笔记.md");
    var sameNameB = Path.Combine(otherDirectory, "笔记.md");
    await File.WriteAllTextAsync(sameNameA, "# 笔记 A\n关于检索的条目 A。\n", new UTF8Encoding(false));
    await File.WriteAllTextAsync(sameNameB, "# 笔记 B\n关于检索的条目 B。\n", new UTF8Encoding(false));
    var importA = await service.ImportAsync(new LibraryImportRequest(LibraryEntryKind.File, LibraryStorageMode.Copy, sameNameA));
    var importB = await service.ImportAsync(new LibraryImportRequest(LibraryEntryKind.File, LibraryStorageMode.Copy, sameNameB));
    Check(importA.IsImported && importB.IsImported, "同名文件都可导入");
    Check(importA.EntryId != importB.EntryId, "同名文件条目 ID 不同");
    Check(service.GetEntry(importA.EntryId!)!.Title == service.GetEntry(importB.EntryId!)!.Title, "同名文件标题相同");
    Check(service.GetEntry(importA.EntryId!)!.SourcePath != service.GetEntry(importB.EntryId!)!.SourcePath, "同名文件来源路径可区分");

    // 4. 重复导入（内容哈希相同）
    var duplicate = await service.ImportAsync(new LibraryImportRequest(LibraryEntryKind.File, LibraryStorageMode.Copy, sameNameA));
    Check(duplicate.Status == LibraryImportStatus.Duplicate, "重复导入被识别为重复");
    Check(duplicate.DuplicateOfEntryId == importA.EntryId, "重复导入指向原条目");
    Check(service.ListEntries().Count == 3, "重复导入未新增条目");

    // 5. 引用模式：源移动/删除后标记不可达，副本模式保留内容
    var reference = Path.Combine(source, "引用资料.txt");
    await File.WriteAllTextAsync(reference, "引用模式的资料内容，关键词 引用资料。\n", new UTF8Encoding(false));
    var referenceImport = await service.ImportAsync(new LibraryImportRequest(LibraryEntryKind.File, LibraryStorageMode.Reference, reference));
    var referenceEntry = service.GetEntry(referenceImport.EntryId!)!;
    Check(!referenceEntry.HasLocalCopy && referenceEntry.StoredPath is null, "引用模式不复制内容");
    var moved = Path.Combine(source, "引用资料-已移动.txt");
    File.Move(reference, moved);
    var movedState = await service.RefreshStatusAsync(referenceEntry.Id, acceptChange: false);
    Check(!movedState.Exists, "源移动后报告不可达");
    Check(service.GetEntry(referenceEntry.Id)!.Status == LibraryEntryStatus.Missing, "源移动后条目状态为 Missing");
    Check(movedState.Reason!.Contains("不存在"), "不可达原因如实说明");
    File.Move(moved, reference);

    var restoredState = await service.RefreshStatusAsync(referenceEntry.Id, acceptChange: false);
    Check(restoredState.Exists && !restoredState.Changed, "源恢复后状态回到正常");
    Check(service.GetEntry(referenceEntry.Id)!.Status == LibraryEntryStatus.Ok, "源恢复后条目状态为 Ok");

    File.Delete(sameNameA);
    var missingCopy = await service.RefreshStatusAsync(importA.EntryId!, acceptChange: false);
    Check(!missingCopy.Exists, "副本条目源删除后报告不可达");
    Check(service.GetEntry(importA.EntryId!)!.ContentAvailable, "副本条目源删除后内容仍可读");
    Check(service.GetPreviewText(importA.EntryId!)!.Contains("条目 A"), "副本条目源删除后预览仍可读");

    // 6. 内容变化：先标记，接受后记录新版本并重索引
    var changedFile = Path.Combine(source, "变化资料.txt");
    await File.WriteAllTextAsync(changedFile, "旧版本内容：磁盘阵列校验码与旧索引关键词。\n", new UTF8Encoding(false));
    var changeImport = await service.ImportAsync(new LibraryImportRequest(LibraryEntryKind.File, LibraryStorageMode.Copy, changedFile, Tags: new[] { "版本" }));
    var changeEntry = service.GetEntry(changeImport.EntryId!)!;
    await File.WriteAllTextAsync(changedFile, "全新版本内容：月度总结已替换全部旧数据，长度不同。\n", new UTF8Encoding(false));
    File.SetLastWriteTimeUtc(changedFile, DateTime.UtcNow.AddMinutes(1));
    var changedState = await service.RefreshStatusAsync(changeEntry.Id, acceptChange: false);
    Check(changedState.Exists && changedState.Changed, "来源变化被检测");
    Check(service.GetEntry(changeEntry.Id)!.Status == LibraryEntryStatus.Changed, "未接受变化时状态为 Changed");
    Check(service.GetEntry(changeEntry.Id)!.ContentHash == changeEntry.ContentHash, "未接受变化时不覆盖已记录版本");
    Check(service.Search("磁盘阵列").Any(hit => hit.Entry.Id == changeEntry.Id), "未接受变化时旧索引仍在");

    var accepted = await service.RefreshStatusAsync(changeEntry.Id, acceptChange: true);
    Check(accepted.Changed, "接受变化返回 Changed 事实");
    var acceptedEntry = service.GetEntry(changeEntry.Id)!;
    Check(acceptedEntry.VersionCount == 2 && acceptedEntry.ContentHash != changeEntry.ContentHash, "接受变化后记录 v2 新哈希");
    Check(acceptedEntry.Status == LibraryEntryStatus.Ok && acceptedEntry.TextState == LibraryTextState.Indexed, "接受变化后状态恢复并重索引");
    var versions = service.ListVersions(changeEntry.Id);
    Check(versions.Count == 2 && versions[1].Origin == "accepted-change", "版本历史记录来源");
    Check(versions[1].StoredPath is not null && File.Exists(versions[1].StoredPath!), "副本模式保存新版本内容快照");
    Check(File.Exists(versions[0].StoredPath!), "旧版本快照保留");
    Check(service.Search("月度总结").Any(hit => hit.Entry.Id == changeEntry.Id), "新内容可检索");
    Check(!service.Search("磁盘阵列").Any(hit => hit.Entry.Id == changeEntry.Id), "旧内容 token 已移除");

    // 7. 索引重建
    var rebuilt = await service.RebuildIndexAsync();
    Check(rebuilt.Completed == rebuilt.Total && rebuilt.Total == service.ListEntries().Count, "索引重建覆盖全部条目");
    Check(service.Search("月度总结").Any(hit => hit.Entry.Id == changeEntry.Id), "重建后检索仍命中");
    var pendingAfter = await service.IndexPendingAsync();
    Check(pendingAfter.Total == 0, "重建后无待索引条目");

    // 8. 网页链接（无快照，不联网）
    var web = await service.ImportAsync(new LibraryImportRequest(LibraryEntryKind.WebPage, LibraryStorageMode.Reference, Url: "https://example.com/资料"));
    var webEntry = service.GetEntry(web.EntryId!)!;
    Check(webEntry.TextState == LibraryTextState.Unsupported, "仅链接条目标记为未索引");
    Check(webEntry.TextError!.Contains("未抓取"), "未抓取原因如实标注");
    Check(service.BuildCitation(webEntry.Id)!.Snippet.Contains("无可用文本"), "无内容时引用如实说明");

    // 9. 会话片段（文本）
    var snippet = await service.ImportAsync(new LibraryImportRequest(LibraryEntryKind.SessionSnippet, LibraryStorageMode.Copy,
        Text: "这是从会话复制的片段，包含 引用回溯 关键词。", Title: "会话片段 1", Tags: new[] { "会话" }));
    Check(snippet.IsImported && service.GetEntry(snippet.EntryId!)!.TextState == LibraryTextState.Indexed, "会话片段导入并索引");
    Check(service.Search("引用回溯").Any(hit => hit.Entry.Id == snippet.EntryId), "会话片段可检索");

    // 10. 不支持的格式：保留原件、显示原因
    var binary = Path.Combine(source, "图片.png");
    await File.WriteAllBytesAsync(binary, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02, 0x03 });
    var binaryImport = await service.ImportAsync(new LibraryImportRequest(LibraryEntryKind.Artifact, LibraryStorageMode.Copy, binary));
    var binaryEntry = service.GetEntry(binaryImport.EntryId!)!;
    Check(binaryEntry.TextState == LibraryTextState.Unsupported, "二进制内容不索引");
    Check(binaryEntry.TextError is { Length: > 0 }, "不索引原因可见");
    Check(File.Exists(binaryEntry.StoredPath!), "不支持的格式仍保留库内副本");

    // 11. GB18030 编码文本可索引
    var gbk = Path.Combine(source, "国标编码.txt");
    await File.WriteAllBytesAsync(gbk, Encoding.GetEncoding("GB18030").GetBytes("国标编码文件，关键词 兼容中文。"));
    var gbkImport = await service.ImportAsync(new LibraryImportRequest(LibraryEntryKind.File, LibraryStorageMode.Copy, gbk));
    var gbkEntry = service.GetEntry(gbkImport.EntryId!)!;
    Check(gbkEntry.TextState == LibraryTextState.Indexed, "GB18030 文本解码后索引");
    Check(gbkEntry.TextError!.Contains("GB18030"), "编码回退原因如实标注");
    Check(service.Search("兼容中文").Any(hit => hit.Entry.Id == gbkEntry.Id), "GB18030 内容可检索");

    // 12. 显式聊天引用可回溯
    var citation = service.BuildCitation(copyEntry.Id, "季度报告")!;
    Check(citation.EntryId == copyEntry.Id && citation.Snippet.Contains("营收增长"), "引用片段来自真实内容");
    Check(citation.Markdown.Contains(copyEntry.Title) && citation.Markdown.Contains(copyEntry.SourceDisplay), "引用块包含标题与来源");
    Check(citation.Markdown.Contains(citation.ShortHash), "引用块包含版本哈希可回溯");

    // 13. 批量导出与再导入
    var exportIds = new[] { copyEntry.Id, changeEntry.Id, importB.EntryId! };
    var export = await service.ExportAsync(exportIds, exportRoot);
    Check(export.EntryCount == 3 && File.Exists(export.ManifestPath), "导出清单生成");
    Check(export.ContentFiles == 3, "导出内容文件齐全");
    Check(Directory.GetFiles(exportRoot, "manifest.json", SearchOption.AllDirectories).Length == 1, "清单唯一");

    var secondRoot = Path.Combine(work, "library-2");
    using (var second = new LibraryService(secondRoot))
    {
        var bundle = await second.ImportBundleAsync(exportRoot);
        Check(bundle.Imported == 3 && bundle.Failed == 0, "导出包可再导入");
        Check(second.ListEntries().Count == 3, "再导入后条目数正确");
        Check(second.GetEntry(second.ListEntries().First(entry => entry.Title == "笔记.md").Id)!.Tags is not null, "再导入保留标签");
        var again = await second.ImportBundleAsync(exportRoot);
        Check(again.Duplicates == 3 && again.Imported == 0, "重复导入导出包被识别为重复");
    }

    // 14. 删除条目默认不删除用户源文件
    var beforeDelete = service.ListEntries().Count;
    var copyDirectory = Path.Combine(libraryRoot, "copies", importB.EntryId!);
    Check(Directory.Exists(copyDirectory), "副本目录存在");
    Check(service.DeleteEntry(importB.EntryId!), "删除条目返回成功");
    Check(File.Exists(sameNameB), "删除条目未删除用户源文件");
    Check(!Directory.Exists(copyDirectory), "删除条目清理库内副本");
    Check(service.ListEntries().Count == beforeDelete - 1, "删除后条目数减少");
    Check(service.GetEntry(importB.EntryId!) is null, "删除后条目不可见");

    // 15. 持久化：重开服务后数据仍在
    using (var reopened = new LibraryService(libraryRoot))
    {
        Check(reopened.ListEntries().Count == service.ListEntries().Count, "重开资料库条目一致");
        Check(reopened.Search("月度总结").Any(hit => hit.Entry.Id == changeEntry.Id), "重开后索引仍可检索");
        Check(reopened.ListTags().Any(tag => tag.Tag == "工作" && tag.Count == 1), "重开后标签保留");
    }

    // 16. 大文件：超限截断如实标注，仍可检索
    var large = Path.Combine(source, "大文件日志.txt");
    var builder = new StringBuilder();
    while (builder.Length < 6 * 1024 * 1024) builder.Append("大文件内容行，含关键词 海量日志。\n");
    await File.WriteAllTextAsync(large, builder.ToString(), new UTF8Encoding(false));
    var largeImport = await service.ImportAsync(new LibraryImportRequest(LibraryEntryKind.File, LibraryStorageMode.Copy, large));
    var largeEntry = service.GetEntry(largeImport.EntryId!)!;
    Check(largeEntry.TextState == LibraryTextState.Indexed, "大文件仍完成索引");
    Check(largeEntry.TextError is { Length: > 0 } note && note.Contains("截断"), "超限截断如实标注");
    var largeHits = service.Search("海量日志");
    var largePreview = service.GetPreviewText(largeEntry.Id) ?? string.Empty;
    Check(largeHits.Any(hit => hit.Entry.Id == largeEntry.Id),
        $"大文件内容可检索（命中 {largeHits.Count} 条 / 预览 {largePreview.Length} 字符 / 含关键词 {largePreview.Contains("海量日志")}）");

    // 17. 文本资料包保真：SessionSnippet / Text 导出再导入必须保留原文、中文与内容哈希
    const string snippetText = "会话片段原文：中文内容、标点，含关键词 引用回溯。\n第二行 & 特殊字符 <tag> 与 emoji 一并保留。";
    const string plainText = "纯文本条目原文：中文与 ASCII mixed 123，含关键词 引用回溯。";
    var snippetImport = await service.ImportAsync(new LibraryImportRequest(
        LibraryEntryKind.SessionSnippet, LibraryStorageMode.Copy, Text: snippetText, Title: "会话片段保真", Tags: new[] { "会话" }));
    var plainImport = await service.ImportAsync(new LibraryImportRequest(
        LibraryEntryKind.Text, LibraryStorageMode.Copy, Text: plainText, Title: "纯文本保真", Tags: new[] { "会话" }));
    Check(snippetImport.IsImported && plainImport.IsImported, "两种文本类型导入成功");
    var snippetEntry = service.GetEntry(snippetImport.EntryId!)!;
    var plainEntry = service.GetEntry(plainImport.EntryId!)!;
    Check(service.GetPreviewText(snippetEntry.Id) == snippetText, "导入时 SessionSnippet 原文与输入一致");
    Check(service.GetPreviewText(plainEntry.Id) == plainText, "导入时 Text 原文与输入一致");

    var textBundle = Path.Combine(work, "导出包-文本");
    var textExport = await service.ExportAsync(new[] { snippetEntry.Id, plainEntry.Id }, textBundle);
    Check(textExport.EntryCount == 2 && textExport.ContentFiles == 2, "两种文本条目导出均含内容文件");

    var textRoot = Path.Combine(work, "library-文本");
    using (var third = new LibraryService(textRoot))
    {
        var bundle = await third.ImportBundleAsync(textBundle);
        Check(bundle.Imported == 2 && bundle.Failed == 0, "文本资料包可导入");
        var items = third.ListEntries();
        var snippet2 = items.Single(entry => entry.Kind == LibraryEntryKind.SessionSnippet);
        var plain2 = items.Single(entry => entry.Kind == LibraryEntryKind.Text);
        Check(third.GetPreviewText(snippet2.Id) == snippetText, "SessionSnippet 再导入保留原文与中文");
        Check(third.GetPreviewText(plain2.Id) == plainText, "Text 再导入保留原文与中文");
        Check(!third.GetPreviewText(snippet2.Id)!.Contains('\0'), "再导入文本不含填充字节");
        Check(snippet2.ContentHash == snippetEntry.ContentHash, "SessionSnippet 再导入内容哈希一致");
        Check(plain2.ContentHash == plainEntry.ContentHash, "Text 再导入内容哈希一致");
        Check(snippet2.SourceSize == snippetEntry.SourceSize && plain2.SourceSize == plainEntry.SourceSize, "再导入内容大小一致");
        Check(snippet2.Tags.Contains("会话") && plain2.Tags.Contains("会话"), "再导入保留标签");
        Check(third.Search("引用回溯").Any(hit => hit.Entry.Id == snippet2.Id), "再导入的片段中文内容可检索");
        Check(third.BuildCitation(snippet2.Id)!.Snippet.Contains("会话片段原文"), "再导入条目可生成可回溯引用");
        var again = await third.ImportBundleAsync(textBundle);
        Check(again.Imported == 0 && again.Duplicates == 2, "文本资料包重复导入识别为重复");
        Check(third.ListEntries().Count == 2, "文本包重复导入未新增条目");
    }

    // 18. 缺少文本内容必须报错，不能生成空文本成功条目
    var beforeMissing = service.ListEntries().Count;
    var missingText = await service.ImportAsync(new LibraryImportRequest(
        LibraryEntryKind.SessionSnippet, LibraryStorageMode.Copy, Title: "缺失文本"));
    Check(missingText.Status == LibraryImportStatus.Failed, "缺少文本内容时导入报错");
    Check(service.ListEntries().Count == beforeMissing, "缺少文本内容不产生空条目");

    var emptyText = await service.ImportAsync(new LibraryImportRequest(
        LibraryEntryKind.Text, LibraryStorageMode.Copy, Text: string.Empty, Title: "空文本"));
    Check(emptyText.Status == LibraryImportStatus.Failed, "空文本导入报错");
    Check(service.ListEntries().Count == beforeMissing, "空文本不产生条目");

    var brokenBundle = Path.Combine(work, "导出包-缺内容");
    Directory.CreateDirectory(brokenBundle);
    await File.WriteAllTextAsync(Path.Combine(brokenBundle, "manifest.json"), """
        {
          "FormatVersion": 1,
          "ExportedUtc": "2026-10-10T00:00:00+00:00",
          "Entries": [
            {
              "Id": "missing-text-1",
              "Title": "缺失内容的片段",
              "Kind": 2,
              "StorageMode": 1,
              "ContentHash": null,
              "Size": 0,
              "AddedUtc": "2026-10-10T00:00:00+00:00",
              "Tags": ["会话"],
              "ContentFile": "content.md",
              "Versions": []
            }
          ]
        }
        """);
    var brokenResult = await service.ImportBundleAsync(brokenBundle);
    Check(brokenResult.Failed == 1 && brokenResult.Imported == 0, "内容文件缺失的资料包报失败");
    Check(brokenResult.Messages.Count == 1 && brokenResult.Messages[0].Contains("缺失内容的片段"),
        "失败信息指名条目");
    Check(service.ListEntries().Count == beforeMissing, "内容缺失不生成空文本成功条目");

    // 19. 无头视图冒烟：真实 Window 下的渲染、移出重挂、刷新/检索/引用与最终释放
    Fufucode.App.Library.Tests.UiSmoke.Run(service, source, Check);

    Console.WriteLine($"\n全部通过：{passed} 项断言");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    Console.Error.WriteLine(error);
    return 1;
}
