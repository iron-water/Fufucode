using System.Text;
using Avalonia;
using Avalonia.Headless;
using Fufucode.App.Features.FilePreview;

AppBuilder.Configure<Application>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
    .SetupWithoutStarting();
// SetupWithoutStarting 会安装 Avalonia 的 SynchronizationContext，但没有消息循环。
// 测试里的 await 若继续 Post 回该 context 会永久等待；这里改用线程池继续。
SynchronizationContext.SetSynchronizationContext(null);

var passed = 0;
var root = Path.Combine(Path.GetTempPath(), "fufucode-file-preview-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var reader = new SafeFileReader();
    var textPath = Path.Combine(root, "bom.txt");
    await File.WriteAllBytesAsync(textPath, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("中文文本")).ToArray());
    var text = await reader.ReadAsync(new(root, textPath));
    Check(text.Success && text.Encoding == "UTF-8 BOM" && text.Text == "中文文本", "UTF-8 BOM 编码探测");

    var emptyPath = Path.Combine(root, "empty.txt");
    File.WriteAllBytes(emptyPath, []);
    var empty = await reader.ReadAsync(new(root, emptyPath));
    Check(empty.Success && empty.Text == string.Empty && empty.Size == 0, "空文件可读");

    var badPath = Path.Combine(root, "bad.txt");
    File.WriteAllBytes(badPath, [0xFF, 0xFE, 0x00]);
    var bad = await reader.ReadAsync(new(root, badPath));
    Check(!bad.Success && bad.Error is not null, "损坏编码给出可读错误");

    var hugePath = Path.Combine(root, "huge.txt");
    await using (var stream = File.Create(hugePath)) stream.SetLength(SafeFileReader.DefaultMaxBytes + 1);
    var huge = await reader.ReadAsync(new(root, hugePath));
    Check(!huge.Success && huge.Error?.Contains("过大") == true, "超大文本拒绝读取");

    var outside = Path.Combine(Path.GetTempPath(), "fufucode-outside-" + Guid.NewGuid().ToString("N") + ".txt");
    await File.WriteAllTextAsync(outside, "outside");
    try
    {
        var escaped = await reader.ReadAsync(new(root, outside));
        Check(!escaped.Success && escaped.Error?.Contains("范围") == true, "路径越界拒绝读取");
    }
    finally { File.Delete(outside); }

    var link = Path.Combine(root, "outside-link.txt");
    File.CreateSymbolicLink(link, outside);
    Check(true, "符号链接测试夹具已创建");
    var linked = await reader.ReadAsync(new(root, link));
    Check(!linked.Success && linked.Error?.Contains("范围") == true, "符号链接越界拒绝读取");
    File.Delete(link);

    var jsonPath = Path.Combine(root, "data.json");
    await File.WriteAllTextAsync(jsonPath, "{\"name\":\"demo\",\"items\":[1,2]}");
    using var registry = FilePreviewRegistry.CreateDefault();
    var jsonProvider = registry.Resolve(new(root, jsonPath));
    var json = await jsonProvider!.LoadAsync(new(root, jsonPath), CancellationToken.None);
    Check(json.Kind == FilePreviewKind.Structured && json.Tree?.Count == 1, "JSON 类型解析与树结果");

    var csvPath = Path.Combine(root, "data.csv");
    await File.WriteAllTextAsync(csvPath, "name,value\na,1\nb,2");
    var csvProvider = registry.Resolve(new(root, csvPath));
    var csv = await csvProvider!.LoadAsync(new(root, csvPath), CancellationToken.None);
    Check(csv.Kind == FilePreviewKind.Table && csv.Headers?.Count == 2 && csv.Rows?.Count == 2, "CSV 表头与行解析");

    var markdownPath = Path.Combine(root, "readme.md");
    await File.WriteAllTextAsync(markdownPath, "# Demo\n\n```csharp\nvar x = 1;\n```");
    var markdown = await registry.Resolve(new(root, markdownPath))!.LoadAsync(new(root, markdownPath), CancellationToken.None);
    Check(markdown.Kind == FilePreviewKind.Markdown && markdown.Text!.Contains("```csharp"), "Markdown 保留代码块");

    var pdfPlugin = registry.Plugins.Resolve(new(root, Path.Combine(root, "sample.pdf")));
    Check(pdfPlugin is not null && pdfPlugin.Descriptor.MediaKind == FilePreviewPluginMediaKind.Document,
        "PDF 复杂文件由文档预览插件契约接管");
    var pdfResult = await pdfPlugin!.LoadAsync(new(root, Path.Combine(root, "sample.pdf")), CancellationToken.None);
    Check(pdfResult.IsError && pdfResult.Error!.Contains("PDF"), "未安装 PDF 插件显示可读降级提示");
    var videoPlugin = registry.Plugins.Resolve(new(root, Path.Combine(root, "sample.mp4")));
    Check(videoPlugin is not null && videoPlugin.Descriptor.MediaKind == FilePreviewPluginMediaKind.Audio,
        "音视频扩展由媒体预览插件契约接管");

    var textControl = new TextPreviewControl("alpha\nbeta alpha\nALPHA");
    Check(textControl.Find("alpha") == "1/3 · 第 1 行", "文本查找首个匹配");
    Check(textControl.Find("alpha") == "2/3 · 第 2 行", "文本查找前进循环");
    Check(textControl.Find("alpha", forward: false) == "1/3 · 第 1 行", "文本查找后退");
    textControl.SetWordWrap(true);
    Check(textControl.IsWordWrapEnabled, "文本自动换行开关");

    var imageControl = new ImagePreviewControl(Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
    imageControl.ZoomBy(2);
    imageControl.RotateBy(90);
    imageControl.ToggleCheckerboard();
    Check(imageControl.Zoom == 2 && imageControl.RotationDegrees == 90 && imageControl.IsCheckerboard,
        "图片缩放旋转棋盘状态");
    imageControl.SetFitMode();
    Check(imageControl.Zoom == 1 && imageControl.IsFitMode, "图片适配模式");

    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var cancelled = false;
    try { await reader.ReadAsync(new(root, textPath), cancellationToken: cancellation.Token); }
    catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled, "取消令牌中止加载");

    Console.WriteLine($"FilePreview tests passed: {passed}");
}
finally
{
    Directory.Delete(root, true);
}

return;

void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    passed++;
    Console.WriteLine("PASS " + name);
}
