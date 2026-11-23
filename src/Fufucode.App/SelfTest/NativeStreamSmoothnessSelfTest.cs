using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;

namespace Fufucode.App.SelfTest;

/// <summary>
/// 真实窗口逐帧记录一次 mock 发送与流式输出：输入卡与时间线是否闪现、跳变、整块重建。
/// 判据只针对肉眼可见的突变：短时间内出现又消失（或反之）、透明度骤降、高度骤缩、滚动区收缩、已显示内容被重建。
/// </summary>
internal static class NativeStreamSmoothnessSelfTest
{
    private sealed record Sample(double Time, double ComposerTop, double ComposerHeight, double InputHeight, double ComposerOpacity,
        double Offset, double Extent, double TailHeight, int TailRebuilt, string Sections, string Blocks);

    internal static async Task<int> RunAsync(MainWindow window)
    {
        var evidence = Path.GetFullPath("tmp/stream-smoothness-evidence");
        Directory.CreateDirectory(evidence);
        var failures = new List<string>();
        var log = new StringBuilder();
        void Check(bool condition, string message)
        {
            Console.WriteLine((condition ? "PASS " : "FAIL ") + message);
            if (!condition) failures.Add(message);
        }
        await Task.Delay(1200);
        window.WindowState = WindowState.Normal;
        window.Width = 1280; window.Height = 860;
        await Task.Delay(400);
        Check(!Motion.Reduced, "smoothness regression runs with real animation timers");
        var vm = (MainViewModel)window.DataContext!;
        var page = window.GetVisualDescendants().OfType<ChatPage>().First();
        var composer = page.FindControl<Control>("ComposerCard")!;
        var input = page.FindControl<TextBox>("InputBox")!;
        var scroller = page.FindControl<ScrollViewer>("TimelineScroller")!;
        var items = page.FindControl<ItemsControl>("TimelineItems")!;
        var top = TopLevel.GetTopLevel(window)!;

        var rounds = int.TryParse(Environment.GetEnvironmentVariable("FUFUCODE_SMOOTH_ROUNDS"), out var r) ? r : 2;
        for (var round = 0; round < rounds; round++)
        {
            vm.Input.DraftText = "第 " + (round + 1) + " 轮：请检查项目结构\n并说明 README 的要点\n顺便跑一次构建";
            await Task.Delay(400);
            var samples = new List<Sample>();
            var states = new ConditionalWeakTable<Visual, Track>();
            var tracked = new List<(Visual Visual, Track Track)>();
            var flashes = new Dictionary<string, int>(StringComparer.Ordinal);
            var dips = new Dictionary<string, int>(StringComparer.Ordinal);
            var tailMembers = new HashSet<Visual>();
            string? tailId = null;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var settled = false;
            var finishedAt = double.MaxValue;
            var tcs = new TaskCompletionSource();

            void Frame(TimeSpan _)
            {
                var t = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                // 尾部消息：同一条消息里已显示的控件若被移出可视树，就是一次整块重建。
                var tailContainer = items.GetRealizedContainers().LastOrDefault() as Control;
                var tailMessage = (tailContainer as ContentPresenter)?.Content ?? tailContainer?.DataContext;
                var tailKey = tailMessage is null ? null : RuntimeHelpers.GetHashCode(tailMessage).ToString();
                var rebuilt = 0;
                if (tailContainer is not null)
                {
                    var now = tailContainer.GetVisualDescendants().Where(v => v is TextBlock or RichMarkdownView or Border).ToHashSet();
                    if (tailKey == tailId) rebuilt = tailMembers.Count(v => !now.Contains(v) && v is TextBlock or RichMarkdownView);
                    tailMembers = now; tailId = tailKey;
                }
                // 只跟踪输入卡与正在输出的消息：全页遍历本身会制造长帧。
                foreach (var visual in composer.GetVisualDescendants().Concat(tailContainer?.GetVisualDescendants() ?? Enumerable.Empty<Visual>()))
                {
                    // 三点指示器的圆点本身就在明暗循环，不算闪烁。
                    if (visual is not Control control || control.FindAncestorOfType<ActivityDots>() is not null) continue;
                    var visible = control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0;
                    var opacity = EffectiveOpacity(control);
                    if (!states.TryGetValue(visual, out var track))
                    {
                        track = new Track(visible, t, opacity);
                        states.Add(visual, track);
                        tracked.Add((visual, track));
                        continue;
                    }
                    if (visible != track.Visible)
                    {
                        // 同一控件在 400ms 内反向切换：肉眼可见的闪现（出现即消失，或消失即出现）。
                        if (t - track.ChangedAt < 400 && track.Changes > 0 && Describe(control) is { } name)
                            flashes[name] = flashes.GetValueOrDefault(name) + 1;
                        track.Visible = visible; track.ChangedAt = t; track.Changes++;
                    }
                    // 已稳定显示的控件透明度骤降（不是入场淡入）：闪烁。
                    if (visible && track.Opacity > .95 && opacity < .6 && t - track.ChangedAt > 200 && Describe(control) is { } dipName)
                        dips[dipName] = dips.GetValueOrDefault(dipName) + 1;
                    track.Opacity = opacity;
                }
                var composerTop = composer.TranslatePoint(default, page)?.Y ?? -1;
                var sections = string.Join(",", composer.GetVisualDescendants().OfType<Control>()
                    .Where(c => c.Parent is StackPanel sp && sp.Parent == composer && c.IsVisible).Select(Describe));
                // 尾部消息的块级构成：块视图模型类型与高度，用来定位哪一块收缩或跳变。
                var blockList = tailContainer?.GetVisualDescendants().OfType<ItemsControl>()
                    .FirstOrDefault(list => list.ItemsSource is System.Collections.IEnumerable && list.GetVisualAncestors().OfType<Control>()
                        .Any(a => AutomationProperties.GetAutomationId(a) == "MSG-ASSISTANT-BUBBLE"));
                string Segs(Control c) => "[" + string.Join("|", c.GetVisualDescendants().OfType<Control>().Where(x => x.DataContext is TextSegmentViewModel &&
                        x.GetVisualParent() is Control xp && xp.DataContext is not TextSegmentViewModel)
                    .Select(x => x.DataContext!.GetType().Name.Replace("SegmentViewModel", "") + "=" + x.Bounds.Height.ToString("0") +
                        (x.DataContext is CodeSegmentViewModel codeSeg ? "<" + codeSeg.Code.Replace("\n", "/") + ">" : "") +
                        (x.DataContext is PlainSegmentViewModel plain ? "'" + plain.Text[Math.Max(0, plain.Text.Length - 12)..].Replace("\n", "/") + "'" : ""))) + "]";
                var blocks = blockList is null ? "" : string.Join(" ", blockList.GetRealizedContainers().OfType<Control>()
                    .Select(c => (c.DataContext?.GetType().Name.Replace("ViewModel", "") ?? "?") + "=" + c.Bounds.Height.ToString("0") + Segs(c)));
                var bubble = tailContainer?.GetVisualDescendants().OfType<Control>().FirstOrDefault(a => AutomationProperties.GetAutomationId(a) == "MSG-ASSISTANT-BUBBLE");
                if (bubble is Panel bubblePanel)
                    blocks += " || " + string.Join(" ", bubblePanel.Children.Select(c => c.GetType().Name + (c.IsVisible ? "" : "(hidden)") + "=" + c.Bounds.Height.ToString("0")));
                samples.Add(new Sample(t, composerTop, composer.Bounds.Height, input.Bounds.Height, composer.Opacity,
                    scroller.Offset.Y, scroller.Extent.Height - items.Padding.Bottom, tailContainer?.Bounds.Height ?? 0, rebuilt, sections, blocks));
                if (settled && t - finishedAt > 1200) { tcs.TrySetResult(); return; }
                if (t > 20000) { tcs.TrySetResult(); return; }
                top.RequestAnimationFrame(Frame);
            }

            var wasBusy = false;
            void Watch(object? s, System.ComponentModel.PropertyChangedEventArgs e)
            {
                var busy = vm.Input.IsTurnActive;
                if (busy) wasBusy = true;
                if (wasBusy && !busy && !settled) { settled = true; finishedAt = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
            }
            vm.Input.PropertyChanged += Watch;
            top.RequestAnimationFrame(Frame);
            await Task.Delay(120);
            var send = page.FindControl<Button>("SendButton")!;
            var sendAt = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            send.Command!.Execute(send.CommandParameter);
            await tcs.Task;
            Save(window, Path.Combine(evidence, $"round{round}-settled.png"));
            vm.Input.PropertyChanged -= Watch;

            // 分析
            var after = samples.Where(s => s.Time >= sendAt).ToList();
            var composerJumps = after.Zip(after.Skip(1), (a, b) => (a, b))
                .Where(p => Math.Abs(p.b.ComposerHeight - p.a.ComposerHeight) > 10 || Math.Abs(p.b.ComposerTop - p.a.ComposerTop) > 10).ToList();
            var extentShrinks = after.Zip(after.Skip(1), (a, b) => (a, b))
                .Where(p => p.b.Extent < p.a.Extent - 4).ToList();
            var tailShrinks = after.Zip(after.Skip(1), (a, b) => (a, b))
                .Where(p => p.b.TailHeight < p.a.TailHeight - 4 && p.a.TailHeight > 0 && p.b.TailHeight > 0).ToList();
            var tailJumps = after.Zip(after.Skip(1), (a, b) => (a, b))
                .Where(p => p.b.TailHeight - p.a.TailHeight > 120).ToList();
            var rebuilds = after.Where(s => s.TailRebuilt > 0).ToList();
            var frameGaps = after.Zip(after.Skip(1), (a, b) => b.Time - a.Time).ToList();
            var longFrames = frameGaps.Count(g => g > 50);
            log.AppendLine($"== round {round}: frames {after.Count}, p50 {Percentile(frameGaps, .5):0.0}ms, p95 {Percentile(frameGaps, .95):0.0}ms, max {frameGaps.DefaultIfEmpty().Max():0.0}ms, >50ms {longFrames}");
            foreach (var (a, b) in composerJumps) log.AppendLine($"composer jump {a.Time - sendAt:0}ms: h {a.ComposerHeight:0}->{b.ComposerHeight:0} top {a.ComposerTop:0}->{b.ComposerTop:0} input {a.InputHeight:0}->{b.InputHeight:0} [{a.Sections}]->[{b.Sections}]");
            foreach (var (a, b) in extentShrinks) log.AppendLine($"extent shrink {a.Time - sendAt:0}ms: {a.Extent:0}->{b.Extent:0}");
            foreach (var (a, b) in tailShrinks) log.AppendLine($"tail shrink {a.Time - sendAt:0}ms: {a.TailHeight:0}->{b.TailHeight:0}\n    before {a.Blocks}\n    after  {b.Blocks}");
            foreach (var (a, b) in after.Zip(after.Skip(1), (a, b) => (a, b)).Where(p => p.b.Time - p.a.Time > 50))
                log.AppendLine($"long frame {a.Time - sendAt:0}ms +{b.Time - a.Time:0}ms  blocks {a.Blocks} -> {b.Blocks}");
            foreach (var (a, b) in tailJumps) log.AppendLine($"tail jump {a.Time - sendAt:0}ms: {a.TailHeight:0}->{b.TailHeight:0}");
            foreach (var s in rebuilds) log.AppendLine($"tail rebuild {s.Time - sendAt:0}ms: {s.TailRebuilt} text controls replaced");
            foreach (var f in flashes.OrderByDescending(p => p.Value)) log.AppendLine($"flash {f.Value}x: {f.Key}");
            foreach (var d in dips.OrderByDescending(p => p.Value)) log.AppendLine($"opacity dip {d.Value}x: {d.Key}");

            Check(composerJumps.Count == 0, $"round {round}: composer never jumps more than 10px in one frame ({composerJumps.Count})");
            Check(extentShrinks.Count == 0, $"round {round}: timeline content never shrinks while streaming ({extentShrinks.Count})");
            Check(tailShrinks.Count == 0, $"round {round}: streaming message never collapses ({tailShrinks.Count})");
            Check(rebuilds.Count == 0, $"round {round}: shown text is never torn down and rebuilt ({rebuilds.Sum(s => s.TailRebuilt)})");
            Check(flashes.Count == 0, $"round {round}: no control flashes in and out within 400ms ({flashes.Values.Sum()})");
            Check(dips.Count == 0, $"round {round}: no shown control dips in opacity ({dips.Values.Sum()})");
            await Task.Delay(600);
        }
        File.WriteAllText(Path.Combine(evidence, "report.txt"), log.ToString());
        Console.Write(log.ToString());
        Console.WriteLine($"Native stream smoothness failures: {failures.Count}; evidence: {evidence}");
        return failures.Count == 0 ? 0 : 1;
    }

    private sealed class Track(bool visible, double changedAt, double opacity)
    {
        public bool Visible = visible;
        public double ChangedAt = changedAt;
        public double Opacity = opacity;
        public int Changes;
    }

    private static double EffectiveOpacity(Visual visual)
    {
        var value = 1d;
        for (var v = visual; v is not null; v = v.GetVisualParent()) value *= v.Opacity;
        return value;
    }

    private static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[Math.Min(sorted.Count - 1, (int)(p * sorted.Count))];
    }

    /// <summary>可读名字：Name / AutomationId / 类型+类+最近命名祖先。叶子文字不单列，避免把正文逐字增长当闪烁。</summary>
    private static string? Describe(Control control)
    {
        var id = control.Name ?? AutomationProperties.GetAutomationId(control);
        var classes = string.Join(".", control.Classes.Where(c => !c.StartsWith(':')));
        var anchor = control.GetVisualAncestors().OfType<Control>().FirstOrDefault(a => a.Name is { Length: > 0 } || AutomationProperties.GetAutomationId(a) is { Length: > 0 });
        var anchorName = anchor is null ? "" : (anchor.Name ?? AutomationProperties.GetAutomationId(anchor));
        return $"{control.GetType().Name}{(id is { Length: > 0 } ? "#" + id : "")}{(classes.Length > 0 ? "." + classes : "")} in {anchorName}";
    }

    private static void Save(Window window, string path)
    {
        window.UpdateLayout();
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bitmap.Render(window);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }
}
