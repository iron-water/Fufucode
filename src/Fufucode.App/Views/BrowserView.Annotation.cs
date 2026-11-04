using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Fufucode.App.Features.BrowserExperience;

namespace Fufucode.App.Views;

public partial class BrowserView
{
    private CancellationTokenSource? _annotationCancellation;

    private void CancelAnnotation()
    {
        _annotationGeneration++;
        _annotationCancellation?.Cancel();
        _annotationCancellation?.Dispose();
        _annotationCancellation = null;
    }

    private async Task StartAnnotationAsync(BrowserTab tab, BrowserViewHost host)
    {
        if (_disposed || !host.IsReady || !tab.CanAnnotate) return;
        CancelAnnotation();
        var cancellation = _annotationCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = cancellation.Token;
        var generation = _annotationGeneration;
        var address = tab.NavigationAddress;
        var session = Guid.NewGuid().ToString("N");
        bool Current() => !token.IsCancellationRequested && !_disposed && generation == _annotationGeneration &&
            ReferenceEquals(tab, _viewModel.ActiveTab) && tab.NavigationAddress == address && host.IsReady;
        string? imagePath = null;
        Bitmap? preview = null;
        var delivered = false;
        try
        {
            var accent = this.TryFindResource("AppAccent", out var resource) && resource is ISolidColorBrush brush
                ? $"#{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}" : "#0078D4";
            var started = await host.ExecuteScriptAsync(BuildAnnotationScript(session, accent)).WaitAsync(token);
            if (!Current()) return;
            if (UnquoteJson(started ?? "") != "annotation-started")
                throw new InvalidOperationException("当前页面无法启动标注");
            string? payload = null;
            while (Current())
            {
                await Task.Delay(120, token);
                var result = await host.ExecuteScriptAsync("window.__fufuAnnotation?.result ?? null").WaitAsync(token);
                if (result is null or "null") continue;
                payload = result;
                break;
            }
            if (!Current() || payload is null) return;
            using var document = JsonDocument.Parse(payload);
            var selected = document.RootElement;
            if (selected.TryGetProperty("cancelled", out _)) return;
            var x = selected.GetProperty("x").GetDouble();
            var y = selected.GetProperty("y").GetDouble();
            var width = selected.GetProperty("w").GetDouble();
            var height = selected.GetProperty("h").GetDouble();
            var selector = selected.GetProperty("selector").GetString() ?? "";
            if (width < 2 || height < 2) throw new InvalidOperationException("选区太小，请重新标注");
            var screenshotsAllowed = _settings.AnnotationIncludeScreenshot && _settings.ScreenshotMode != "never";
            var automaticScreenshot = screenshotsAllowed && _settings.ScreenshotMode != "ask";
            if (automaticScreenshot)
                (imagePath, preview) = await CaptureAnnotationAsync(host, x, y, width, height, token);
            if (!Current()) return;
            var edit = await PromptAnnotationCommentAsync(preview, tab.DocumentTitle, selector,
                screenshotsAllowed, automaticScreenshot, css => Current()
                    ? ApplyAnnotationStyleAsync(host, session, selector, css) : Task.CompletedTask, token);
            if (!Current() || edit is null) return;
            if (edit.IncludeScreenshot && imagePath is null && ChatReferenceRequested is not null)
                (imagePath, preview) = await CaptureAnnotationAsync(host, x, y, width, height, token);
            if (!Current()) return;
            if (edit.IncludeScreenshot && imagePath is null && ChatReferenceRequested is not null)
                throw new InvalidOperationException("区域截图失败，标注尚未发送");
            var text = new StringBuilder("【网页标注】\n");
            if (!string.IsNullOrWhiteSpace(tab.DocumentTitle)) text.AppendLine("标题：" + tab.DocumentTitle);
            text.AppendLine("地址：" + address);
            if (selector.Length > 0) text.AppendLine("元素：" + selector);
            text.AppendLine(FormattableString.Invariant($"区域：{x:0.##},{y:0.##} {width:0.##}×{height:0.##}"));
            if (edit.Comment.Length > 0) text.AppendLine("说明：" + edit.Comment);
            if (edit.Css.Length > 0) text.AppendLine("样式调整：" + edit.Css);
            if (ChatReferenceRequested is { } receiver)
            {
                receiver.Invoke(this, new BrowserChatReference(text.ToString().TrimEnd(), edit.IncludeScreenshot ? imagePath : null));
                delivered = edit.IncludeScreenshot;
                ShowBrowserFeedback("标注已发送到对话输入框");
            }
            else
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is null) throw new InvalidOperationException("当前环境没有可用的剪贴板");
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text.ToString().TrimEnd());
                ShowBrowserFeedback("已复制标注");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is JsonException or InvalidOperationException or IOException or FormatException or TimeoutException or UnauthorizedAccessException)
        {
            if (Current()) ShowBrowserFeedback(error.Message);
        }
        finally
        {
            preview?.Dispose();
            if (!delivered && imagePath is not null)
            {
                try { File.Delete(imagePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            try
            {
                await host.ExecuteScriptAsync("if(window.__fufuAnnotation?.id===" + JsonSerializer.Serialize(session) +
                    "){window.__fufuAnnotation.dispose();delete window.__fufuAnnotation;}").WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException) { }
            if (ReferenceEquals(_annotationCancellation, cancellation)) _annotationCancellation = null;
            cancellation.Dispose();
        }
    }

    internal static async Task<(string? Path, Bitmap? Preview)> CaptureAnnotationAsync(
        BrowserViewHost host, double x, double y, double width, double height, CancellationToken token)
    {
        var parameters = JsonSerializer.Serialize(new
        {
            format = "png", captureBeyondViewport = false,
            clip = new { x, y, width, height, scale = 1.0 },
        });
        var result = await host.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", parameters)
            .WaitAsync(TimeSpan.FromSeconds(10), token);
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(result)) return (null, null);
        using var document = JsonDocument.Parse(result);
        if (!document.RootElement.TryGetProperty("data", out var data)) return (null, null);
        var bytes = Convert.FromBase64String(data.GetString() ?? "");
        if (bytes.Length == 0) return (null, null);
        var folder = Path.Combine(Path.GetTempPath(), "fufucode-browser-annotations");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "annotation-" + Guid.NewGuid().ToString("N") + ".png");
        using var stream = new MemoryStream(bytes);
        var bitmap = new Bitmap(stream);
        try
        {
            await File.WriteAllBytesAsync(path, bytes);
            return (path, bitmap);
        }
        catch { bitmap.Dispose(); throw; }
    }

    private async Task<BrowserAnnotationEdit?> PromptAnnotationCommentAsync(Bitmap? preview, string title,
        string selector, bool screenshotsAllowed, bool includeScreenshot, Func<string, Task> applyCss, CancellationToken token)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var dialog = new Window
        {
            Title = "标注", Width = 480, MaxHeight = 760, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = true, Classes = { "browserDialog" },
        };
        var panel = new StackPanel { Classes = { "browserDialogBody" } };
        panel.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(title) ? "当前页面" : title,
            TextWrapping = TextWrapping.Wrap, Classes = { "browserHeading" } });
        if (preview is not null) panel.Children.Add(new Image { Source = preview, Height = 160, Stretch = Stretch.Uniform });
        var comment = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, PlaceholderText = "添加标注" };
        panel.Children.Add(comment);
        var screenshot = new CheckBox { Content = "附加区域截图", IsChecked = includeScreenshot,
            IsVisible = screenshotsAllowed && ChatReferenceRequested is not null };
        panel.Children.Add(screenshot);
        var fields = new List<(string Property, TextBox Box)>();
        var rows = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Classes = { "browserAdjustRow" } };
        var specifications = new[] { ("字号", "font-size"), ("行高", "line-height"), ("文字颜色", "color"),
            ("背景颜色", "background-color"), ("内边距", "padding"), ("圆角", "border-radius") };
        for (var index = 0; index < specifications.Length; index++)
        {
            if (index % 2 == 0) rows.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var field = new TextBox { Classes = { "browserAdjustField" } };
            var cell = new StackPanel { Spacing = 4 };
            cell.Children.Add(new TextBlock { Text = specifications[index].Item1, Classes = { "browserMeta" } });
            cell.Children.Add(field);
            Grid.SetRow(cell, index / 2); Grid.SetColumn(cell, index % 2); rows.Children.Add(cell);
            fields.Add((specifications[index].Item2, field));
        }
        var adjust = new Expander { Header = "调整样式", Content = rows, IsEnabled = selector.Length > 0,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Width = 432, Classes = { "browserAdjust" } };
        rows.Width = 416;
        dialog.SizeChanged += (_, _) =>
        {
            adjust.Width = Math.Max(240, dialog.ClientSize.Width - 48);
            rows.Width = Math.Max(224, adjust.Width - 16);
        };
        panel.Children.Add(adjust);
        string BuildCss() => string.Join("", fields.Select(field => AnnotationCss(field.Property, field.Box.Text)));
        var updating = false;
        foreach (var field in fields)
            field.Box.TextChanged += async (_, _) => { if (!updating && !token.IsCancellationRequested) await applyCss(BuildCss()); };
        var reset = new Button { Content = "还原样式", HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += async (_, _) =>
        {
            updating = true;
            foreach (var field in fields) field.Box.Text = "";
            updating = false;
            await applyCss("");
        };
        panel.Children.Add(reset);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = "取消" };
        var send = new Button { Content = ChatReferenceRequested is null ? "复制标注" : "发送到对话", Classes = { "accent" } };
        actions.Children.Add(cancel); actions.Children.Add(send); panel.Children.Add(actions);
        dialog.Content = new ScrollViewer { Content = panel };
        var completion = new TaskCompletionSource<BrowserAnnotationEdit?>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancel.Click += (_, _) => dialog.Close();
        send.Click += (_, _) => { completion.TrySetResult(new BrowserAnnotationEdit((comment.Text ?? "").Trim(), BuildCss(), screenshot.IsChecked == true)); dialog.Close(); };
        dialog.Closed += (_, _) => completion.TrySetResult(null);
        using var registration = token.Register(() => Dispatcher.UIThread.Post(() => { completion.TrySetResult(null); dialog.Close(); }));
        token.ThrowIfCancellationRequested();
        if (owner is not null) _ = dialog.ShowDialog(owner); else dialog.Show();
        comment.Focus();
        return await completion.Task;
    }

    internal static string AnnotationCss(string property, string? input)
    {
        var value = (input ?? "").Trim();
        if (value.Length == 0) return "";
        if (property is "color" or "background-color")
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(value, "^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$|^[a-zA-Z]{3,20}$")) return "";
        }
        else if (property is "font-size" or "line-height" or "padding" or "border-radius")
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                !double.IsFinite(number) || number < 0 || number > (property == "line-height" ? 10 : 500)) return "";
            value = number.ToString(CultureInfo.InvariantCulture) + (property == "line-height" ? "" : "px");
        }
        else return "";
        return property + ":" + value + " !important;";
    }

    private static async Task ApplyAnnotationStyleAsync(BrowserViewHost host, string session, string selector, string css)
    {
        await host.ExecuteScriptAsync("((id,sel,css)=>{const a=window.__fufuAnnotation;if(a?.id!==id)return false;" +
            "let style=document.getElementById(a.styleId);if(!css){style?.remove();return true;}" +
            "if(document.querySelectorAll(sel).length!==1)return false;" +
            "if(!style){style=document.createElement('style');style.id=a.styleId;document.head.append(style);}" +
            "style.textContent=sel+'{'+css+'}';return true;})(" + JsonSerializer.Serialize(session) + "," +
            JsonSerializer.Serialize(selector) + "," + JsonSerializer.Serialize(css) + ")");
    }

    internal static string BuildAnnotationScript(string session, string accent) => "((id,accent)=>{" + AnnotationScript +
        "})(" + JsonSerializer.Serialize(session) + "," + JsonSerializer.Serialize(accent) + ")";

    private const string AnnotationScript = """
        window.__fufuAnnotation?.dispose();
        const layer=document.createElement('div'), box=document.createElement('div');
        Object.assign(layer.style,{position:'fixed',inset:'0',zIndex:'2147483647',cursor:'crosshair'});
        Object.assign(box.style,{position:'absolute',border:'2px solid '+accent,background:'transparent',display:'none',pointerEvents:'none'});
        layer.append(box);document.documentElement.append(layer);
        const state=window.__fufuAnnotation={id,result:null,styleId:'__fufu_annotation_style_'+id,dispose};
        let sx=0,sy=0,dragging=false;
        function cleanup(){layer.remove();document.removeEventListener('pointerdown',down,true);document.removeEventListener('pointermove',move,true);document.removeEventListener('pointerup',up,true);document.removeEventListener('keydown',key,true);}
        function dispose(){cleanup();document.getElementById(state.styleId)?.remove();}
        function path(el){
          if(el.id){const sel='#'+CSS.escape(el.id);if(document.querySelectorAll(sel).length===1)return sel;}
          const parts=[];
          while(el?.nodeType===1){let part=el.localName;const parent=el.parentElement;
            if(parent){const peers=Array.from(parent.children).filter(s=>s.localName===part);if(peers.length>1)part+=':nth-of-type('+(peers.indexOf(el)+1)+')';}
            parts.unshift(part);const sel=parts.join(' > ');if(document.querySelectorAll(sel).length===1)return sel;el=parent;}
          return '';
        }
        function down(e){if(e.button!==0)return;dragging=true;sx=e.clientX;sy=e.clientY;box.style.display='block';move(e);e.preventDefault();e.stopPropagation();}
        function move(e){if(!dragging)return;Object.assign(box.style,{left:Math.min(sx,e.clientX)+'px',top:Math.min(sy,e.clientY)+'px',width:Math.abs(e.clientX-sx)+'px',height:Math.abs(e.clientY-sy)+'px'});}
        function up(e){
          if(!dragging)return;dragging=false;
          let x=Math.min(sx,e.clientX),y=Math.min(sy,e.clientY),w=Math.abs(e.clientX-sx),h=Math.abs(e.clientY-sy);
          layer.style.pointerEvents='none';
          const el=document.elementFromPoint(x+w/2,y+h/2);
          if((w<4||h<4)&&el){const r=el.getBoundingClientRect();x=r.left;y=r.top;w=r.width;h=r.height;}
          const right=Math.min(innerWidth,x+w),bottom=Math.min(innerHeight,y+h);x=Math.max(0,x);y=Math.max(0,y);w=right-x;h=bottom-y;
          const selector=el?path(el):'';cleanup();
          state.result={x:x+scrollX,y:y+scrollY,w,h,selector};
          e.preventDefault();e.stopPropagation();
        }
        function key(e){if(e.key==='Escape'){cleanup();state.result={cancelled:true};e.preventDefault();e.stopPropagation();}}
        document.addEventListener('pointerdown',down,true);document.addEventListener('pointermove',move,true);document.addEventListener('pointerup',up,true);document.addEventListener('keydown',key,true);
        return 'annotation-started';
        """;
}
