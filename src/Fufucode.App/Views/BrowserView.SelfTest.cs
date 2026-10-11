using System;
using System.IO;
using Avalonia.Controls;
using Fufucode.App.Infrastructure.Browser;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

/// <summary>
/// 浏览器面的无头断言：地址归一化、文件预览分类、数据目录隔离、VM 状态机、控件骨架。
/// 真实导航与像素渲染依赖 WebView2 运行时，无头环境不可渲染——那部分列「用户实测清单」，
/// 不在本文件里放宽断言冒充通过。
/// </summary>
internal static class BrowserViewSelfTest
{
    public static void Run(Action<string> pass, Action<string> fail)
    {
        void Check(bool value, string description)
        {
            if (value) pass("E1 浏览器：" + description);
            else fail("E1 浏览器：" + description);
        }

        var root = Path.Combine(Path.GetTempPath(), "fufucode-browser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CheckAddressResolver(Check);
            CheckFilePreview(root, Check);
            CheckUserDataLayout(root, Check);
            CheckViewModel(root, Check);
            CheckSkeleton(Check);
            CheckPreviewEntryPoint(root, Check);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void CheckAddressResolver(Action<bool, string> check)
    {
        var bare = BrowserAddressResolver.Resolve("example.com");
        check(bare.Kind == BrowserTargetKind.Web && bare.Address == "https://example.com", "裸主机名补全为 https");

        var local = BrowserAddressResolver.Resolve("http://localhost:3000/path?q=1");
        check(local.Kind == BrowserTargetKind.Web && local.Address == "http://localhost:3000/path?q=1", "http 与端口/查询串原样保留");

        var about = BrowserAddressResolver.Resolve("   ");
        check(about.Kind == BrowserTargetKind.Blank && about.Address == "about:blank", "空输入落到 about:blank");

        var script = BrowserAddressResolver.Resolve("javascript:alert(1)");
        check(script.Kind == BrowserTargetKind.Rejected && !script.IsNavigable, "脚本协议被白名单拒绝且不导航");

        var ftp = BrowserAddressResolver.Resolve("ftp://host/a");
        check(ftp.Kind == BrowserTargetKind.Rejected, "白名单外的 ftp 被拒绝");

        var missing = BrowserAddressResolver.Resolve(Path.Combine(Path.GetTempPath(), "fufucode-no-such-file.txt"));
        check(missing.Kind == BrowserTargetKind.Rejected, "不存在的本地路径被拒绝");
    }

    private static void CheckFilePreview(string root, Action<bool, string> check)
    {
        var htmlPath = Write(root, "page.html", "<html><body>hi</body></html>");
        var html = BrowserFilePreview.Inspect(htmlPath);
        check(html.Kind == BrowserFilePreviewKind.Html && html.Address is not null && !html.NeedsInlineDocument,
            "html 以 file URI 直接渲染（保留相对资源）");

        var pngPath = WriteBytes(root, "dot.png",
            Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg=="));
        var image = BrowserFilePreview.Inspect(pngPath);
        check(image.Kind == BrowserFilePreviewKind.Image && image.NeedsInlineDocument, "小图内联为 data URI 文档");

        var textPath = Write(root, "note.txt", "a<b>&c\n<script>alert(1)</script>");
        var text = BrowserFilePreview.Inspect(textPath);
        check(text.Kind == BrowserFilePreviewKind.Text && text.Document is not null, "txt 走文本渲染");
        check(text.Document!.Contains("&lt;script&gt;", StringComparison.Ordinal) &&
              !text.Document.Contains("<script>alert", StringComparison.Ordinal),
            "文本内容经过 HTML 转义，不执行其中的脚本");

        var logPath = Write(root, "run.log", "line-1\nline-2\n");
        check(BrowserFilePreview.Inspect(logPath).Kind == BrowserFilePreviewKind.Text, "log 同样按文本预览");

        var binaryPath = WriteBytes(root, "blob.txt", new byte[] { 0x68, 0x00, 0x69 });
        check(!BrowserFilePreview.Inspect(binaryPath).IsSupported, "含 NUL 字节的文本扩展名判定为二进制，不预览");

        check(!BrowserFilePreview.Inspect(Path.Combine(root, "missing.txt")).IsSupported, "缺失文件只提示，不导航");

        var bigPath = Write(root, "big.log", new string('x', 600 * 1024));
        var big = BrowserFilePreview.Inspect(bigPath);
        check(big.Kind == BrowserFilePreviewKind.Text && big.Document!.Contains("已截断", StringComparison.Ordinal),
            "超过 512 KB 的文本截断并如实标注，不静默丢弃");
    }

    private static void CheckUserDataLayout(string root, Action<bool, string> check)
    {
        var defaultFolder = BrowserUserDataLayout.Resolve(null);
        check(defaultFolder.EndsWith(Path.Combine("Fufucode", "webview"), StringComparison.Ordinal),
            "默认 UserDataFolder 落在 %LocalAppData%\\Fufucode\\webview");

        var isolatedRoot = Path.Combine(root, "appdata");
        var isolated = BrowserUserDataLayout.Resolve(isolatedRoot);
        check(isolated == Path.Combine(Path.GetFullPath(isolatedRoot), "webview"),
            "自测隔离根下同样建 webview 子目录（不写用户真实目录）");

        check(BrowserUserDataLayout.TryEnsure(isolated) is null, "数据目录可创建");
    }

    private static void CheckViewModel(string root, Action<bool, string> check)
    {
        var vm = new BrowserViewModel();
        check(vm.Mode == BrowserSurfaceMode.Free && !vm.IsRuntimeAvailable && !vm.HasRuntimeNotice,
            "初始态为自由导航，未探测时不遮挡宿主");

        BrowserNavigationRequest? request = null;
        vm.NavigationRequested += (_, r) => request = r;

        vm.AddressText = "example.com";
        vm.NavigateCommand.Execute(null);
        check(request is not null && request.Address == "https://example.com" && vm.Mode == BrowserSurfaceMode.Free,
            "地址栏提交经归一化后请求导航");

        request = null;
        vm.AddressText = "javascript:alert(1)";
        vm.NavigateCommand.Execute(null);
        check(request is null && vm.StatusText.Contains("不支持", StringComparison.Ordinal),
            "脚本协议提交被拒绝，不改变当前页");

        var textPath = Write(root, "preview.txt", "hello");
        request = null;
        vm.PreviewFile(textPath);
        check(vm.Mode == BrowserSurfaceMode.File && vm.IsFileMode && request?.Document is not null,
            "接收本地路径进入文件预览模式并请求内联文档");

        request = null;
        vm.PreviewFile(Path.Combine(root, "missing.png"));
        check(request is null && vm.StatusText.Contains("不存在", StringComparison.Ordinal), "缺失文件只提示不导航");

        vm.ApplyNavigationState(true, false, false, null);
        check(vm.CanGoBack && !vm.CanGoForward &&
              vm.BackCommand.CanExecute(null) && !vm.ForwardCommand.CanExecute(null),
            "后退/前进可用性来自宿主历史，并驱动命令 CanExecute");

        BrowserSurfaceAction? action = null;
        vm.ActionRequested += (_, a) => action = a;
        vm.ApplyNavigationState(true, true, false, null);
        vm.BackCommand.Execute(null);
        check(action == BrowserSurfaceAction.Back, "后退命令转成宿主动作");

        vm.ApplyRuntimeProbe(new BrowserRuntimeProbe(BrowserRuntimeState.Available, "154.0.4258.48", "运行时 154.0.4258.48"));
        check(vm.IsRuntimeAvailable && !vm.HasRuntimeNotice, "运行时可用时不显示提示层");

        vm.ApplyRuntimeProbe(new BrowserRuntimeProbe(BrowserRuntimeState.Missing, null, "未检测到"));
        check(!vm.IsRuntimeAvailable && vm.HasRuntimeNotice && vm.RuntimeMessage.Length > 0,
            "运行时缺失时给出可操作提示而不是空白");
        check(vm.RuntimeLink.StartsWith("https://", StringComparison.Ordinal), "提示附带官方下载链接");
    }

    private static void CheckSkeleton(Action<bool, string> check)
    {
        try
        {
            var view = new BrowserView();
            check(ReferenceEquals(view.DataContext, view.ViewModel), "BrowserView 自带 VM，不继承外层 DataContext");
            check(!view.ViewModel.IsRuntimeAvailable, "构造完成即返回，不在无头环境触发 WebView2 初始化");
        }
        catch (Exception error)
        {
            check(false, "BrowserView 骨架可无头构造（异常：" + error.Message + "）");
        }
    }

    /// <summary>E2：D 轨调用点契约——按 x:Name 定位右栏内置浏览器并推进文件预览。</summary>
    private static void CheckPreviewEntryPoint(string root, Action<bool, string> check)
    {
        var path = Write(root, "entry.txt", "hello");
        check(!BrowserView.TryPreviewFile(null, path), "面板为空时入口返回 false，不抛异常");
        check(!BrowserView.TryPreviewFile(new Grid(), path), "面板上没有内置浏览器时入口返回 false");

        try
        {
            var panel = new WorkspacePanelView();
            check(BrowserView.TryPreviewFile(panel, path), "按 x:Name 契约可在真实右栏内定位内置浏览器");
            var found = panel.FindControl<BrowserView>(BrowserView.PreviewControlName);
            check(found is not null && found.ViewModel.IsFileMode, "定位成功后路径已进入文件预览模式");
        }
        catch (Exception error)
        {
            check(false, "真实右栏入口契约可用（异常：" + error.Message + "）");
        }
    }

    private static string Write(string root, string name, string content)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string WriteBytes(string root, string name, byte[] content)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, content);
        return path;
    }
}
