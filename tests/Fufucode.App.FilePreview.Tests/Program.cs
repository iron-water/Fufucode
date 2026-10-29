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

    var relative = await reader.ReadAsync(new(root, "bom.txt"));
    Check(relative.Success && relative.Text == "中文文本", "相对路径按项目根解析");

    var binaryPath = Path.Combine(root, "blob.bin");
    await File.WriteAllBytesAsync(binaryPath, [0x00, 0x01, 0x02, 0x00]);
    var binaryRead = await reader.ReadAsync(new(root, binaryPath));
    Check(binaryRead.Success && binaryRead.Text is null, "含 NUL 的内容不当作文本");

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
    var pdfPath = Path.Combine(root, "sample.pdf");
    await File.WriteAllTextAsync(pdfPath, "%PDF-1.7\n1 0 obj\n<< /Type /Page >>\nBT (Hello PDF) Tj ET\nendobj\n%%EOF");
    var pdfResult = await pdfPlugin!.LoadAsync(new(root, pdfPath), CancellationToken.None);
    if (pdfResult.Kind == FilePreviewKind.Structured)
        Check(!pdfResult.IsError && pdfResult.Text?.Contains("Hello PDF") == true && pdfResult.Tree?.Count == 1, "PDF 签名、页数和文本提取");
    else
        Check(!pdfResult.IsError && pdfResult.Format == "PDF" && pdfResult.Size > 0, "PDF 控件渲染模式签名与大小校验");
    var videoPlugin = registry.Plugins.Resolve(new(root, Path.Combine(root, "sample.mp4")));
    Check(videoPlugin is not null && videoPlugin.Descriptor.MediaKind == FilePreviewPluginMediaKind.Audio,
        "音视频扩展由媒体预览插件契约接管");

    var textControl = new TextPreviewControl("alpha\nbeta alpha\nALPHA");
    Check(textControl.Find("alpha") == "1/3 · 第 1 行", "文本查找首个匹配");
    Check(textControl.Find("alpha") == "2/3 · 第 2 行", "文本查找前进循环");
    Check(textControl.Find("alpha", forward: false) == "1/3 · 第 1 行", "文本查找后退");
    textControl.SetWordWrap(true);
    Check(textControl.IsWordWrapEnabled, "文本自动换行开关");

    var svgPath = Path.Combine(root, "shape.svg");
    await File.WriteAllTextAsync(svgPath,
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\" fill=\"red\"/></svg>");
    var svg = await registry.Resolve(new(root, svgPath))!.LoadAsync(new(root, svgPath), CancellationToken.None);
    Check(svg.Kind == FilePreviewKind.Image && svg.Bytes is { Length: > 8 } png && png[0] == 0x89,
        "标准 SVG 渲染为 PNG 预览");
    var scriptSvgPath = Path.Combine(root, "script.svg");
    await File.WriteAllTextAsync(scriptSvgPath,
        "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
    var scriptSvg = await registry.Resolve(new(root, scriptSvgPath))!.LoadAsync(new(root, scriptSvgPath), CancellationToken.None);
    Check(scriptSvg.IsError && scriptSvg.Error!.Contains("脚本"), "SVG 脚本被拒绝渲染");

    var imageControl = new ImagePreviewControl(Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
    imageControl.ZoomBy(2);
    imageControl.RotateBy(90);
    var checkerboard = imageControl.IsCheckerboard;
    imageControl.ToggleCheckerboard();
    Check(imageControl.Zoom == 2 && imageControl.RotationDegrees == 90 && imageControl.IsCheckerboard != checkerboard,
        "图片缩放、旋转与棋盘切换");
    imageControl.SetFitMode();
    Check(imageControl.IsFitMode, "图片适配模式");
    imageControl.SetOriginalSize();
    Check(!imageControl.IsFitMode && imageControl.Zoom == 1, "原始尺寸回到 1:1");
    imageControl.Dispose();

    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var cancelled = false;
    try { await reader.ReadAsync(new(root, textPath), cancellationToken: cancellation.Token); }
    catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled, "取消令牌中止加载");

    // 真实交互链：工作区文件树必须能展开、选中嵌套文件并加载预览，而不只是根目录列表。
    var project = Path.Combine(root, "project");
    Directory.CreateDirectory(Path.Combine(project, "src", "nested"));
    await File.WriteAllTextAsync(Path.Combine(project, "src", "Program.cs"), "class Program { }");
    await File.WriteAllTextAsync(Path.Combine(project, "src", "nested", "Deep.cs"), "class Deep { }");
    var vm = new Fufucode.App.ViewModels.WorkspacePanelViewModel(new FakeClipboard());
    vm.AttachMock(project);
    await vm.RefreshFilesAsync();
    var src = vm.Files.Single(item => item.Name == "src");
    await vm.ToggleDirectoryAsync(src);
    Check(src.IsExpanded && src.Children.Count >= 2, "目录可懒加载展开");
    Check(vm.VisibleFiles.Any(item => item.Name == "nested"), "展开后的子目录进入可见文件列表");
    var nested = src.Children.Single(item => item.Name == "nested");
    await vm.ToggleDirectoryAsync(nested);
    var deep = nested.Children.Single(item => item.Name == "Deep.cs");
    Check(vm.VisibleFiles.Contains(deep) && deep.Depth == 2, "嵌套文件按层级进入可见列表");
    vm.SelectedFile = deep;
    Check(vm.CanPreviewSelectedFile, "选中嵌套文件后可预览");
    using var deepRegistry = FilePreviewRegistry.CreateDefault();
    var deepProvider = deepRegistry.Resolve(new(project, deep.FullPath));
    var deepPreview = await deepProvider!.LoadAsync(new(project, deep.FullPath), CancellationToken.None);
    Check(deepPreview.Kind == FilePreviewKind.Text && deepPreview.Text?.Contains("class Deep") == true,
        "嵌套文件按完整路径加载出文本预览");

    // 宿主接线：同步完成加载时不跨线程，可以在无消息循环的无头测试里验证视图与元数据。
    using (var host = new FilePreviewHost(new FilePreviewRegistry([new SynchronousPreviewProvider()])))
    {
        await host.OpenAsync(project, "src/nested/Deep.cs");
        Check(host.Result?.Kind == FilePreviewKind.Text && host.Content is Avalonia.Controls.TextBlock,
            "宿主加载结果并创建 provider 视图");
        Check(host.CurrentRequest?.Path.EndsWith("Deep.cs", StringComparison.OrdinalIgnoreCase) == true,
            "宿主记录完整预览请求");
        host.Close();
        Check(host.Result is null && host.Content is null, "关闭预览清空结果与视图");
    }

    await vm.ToggleDirectoryAsync(src);
    Check(!src.IsExpanded && !vm.VisibleFiles.Contains(nested), "折叠目录后子项从可见列表移除");

    vm.FileFilter = "Deep";
    await WaitUntil(() => !vm.IsFileSearchLoading && vm.VisibleFiles.Any(item => item.Name.Contains("Deep")));
    Check(vm.VisibleFiles.Any(item => item.RelativePath == "src/nested/Deep.cs"), "筛选搜索整个项目而非仅当前层");
    vm.FileFilter = "";
    await WaitUntil(() => !vm.IsFileSearchLoading);
    Check(vm.VisibleFiles.Any(item => item.Name == "src"), "清空筛选恢复文件树");

    var reference = new Fufucode.App.ViewModels.MessageFileViewModel("src/Program.cs");
    var changedProperties = new List<string>();
    reference.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName ?? "");
    reference.Resolve(project);
    Check(reference.Exists && reference.ResolvedPath is not null, "相关文件按项目根解析");
    Check(changedProperties.Contains(nameof(Fufucode.App.ViewModels.MessageFileViewModel.Exists)),
        "相关文件解析后通知按钮状态");
    var outsideReference = new Fufucode.App.ViewModels.MessageFileViewModel("../outside.txt");
    outsideReference.Resolve(project);
    Check(!outsideReference.Exists && outsideReference.ResolvedPath is null, "相关文件拒绝越界路径");

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

static async Task WaitUntil(Func<bool> condition)
{
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
}

sealed class FakeClipboard : Fufucode.App.Infrastructure.IClipboardService
{
    public Task SetTextAsync(string text) => Task.CompletedTask;
}

sealed class SynchronousPreviewProvider : IFilePreviewProvider
{
    public FilePreviewKind Kind => FilePreviewKind.Text;
    public bool CanPreview(FilePreviewRequest request) => true;
    public Task<FilePreviewResult> LoadAsync(FilePreviewRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new FilePreviewResult(FilePreviewKind.Text, request.FullPath, 1, "Text", "UTF-8", false, null, "sync"));
    public Avalonia.Controls.Control CreateView(FilePreviewResult result) => new Avalonia.Controls.TextBlock { Text = result.Text };
    public void Dispose() { }
}
