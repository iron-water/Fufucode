using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Abstractions;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;
using LiveMarkdown.Avalonia;
using MessageText = Fufucode.App.Abstractions.TextBlock;
using MarkdownTextBlock = LiveMarkdown.Avalonia.MarkdownTextBlock;

namespace Fufucode.App.SelfTest;

internal static class NativeMarkdownTableSelfTest
{
    internal static async Task<int> RunAsync(MainWindow main)
    {
        var evidence = Path.GetFullPath("tmp/markdown-table-evidence");
        Directory.CreateDirectory(evidence);
        var failures = new List<string>();
        var checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            Console.WriteLine((condition ? "PASS " : "FAIL ") + message);
            if (!condition) failures.Add(message);
        }
        const string table = """
            | 项目 | 详细说明 | 记录 |
            | --- | --- | --- |
            | 中文换行 | 表格完成后应该自动根据聊天窗口的实际宽度重新排版，长句完整换行，并且保留所有文字。多行内容允许表格向下增高，但不能无限占据整个会话。 | **重要内容** 与 `中文 code` |
            | English | Long English text should wrap inside its column without requiring horizontal scrolling. Keep every word and retain selectable rich text. | [文档链接](https://example.com/docs) |
            | 长路径 | C:/workspace/very_long_unbroken_folder_name_with_many_segments/another_long_filename_that_must_remain_visible.txt | 最后一格 |
            """;
        var view = new RichMarkdownView { Markdown = table, IsStreaming = true, Margin = new Thickness(16) };
        var host = new Window
        {
            Width = 800, Height = 760, Topmost = true, Position = new PixelPoint(0, 0),
            Content = view, Title = "Markdown table native verification", FontFamily = main.FontFamily,
            RequestedThemeVariant = main.ActualThemeVariant,
        };
        foreach (var resource in main.Resources) host.Resources[resource.Key] = resource.Value;
        host.Bind(Window.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppWindowBackground"));
        GetCursorPos(out var previousCursor);
        host.Show();
        try
        {
            await Ready();
            for (var attempt = 0; attempt < 40 && !view.GetVisualDescendants().OfType<MarkdownTableView>().Any(); attempt++) await Task.Delay(100);
            var rendered = view.GetVisualDescendants().OfType<MarkdownTableView>().Single();
            var panel = rendered.Table;
            var scroll = Find<ScrollViewer>(rendered, "MarkdownTableScroll");
            Check(panel.IsStreaming && scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto,
                "streaming keeps natural column widths");
            Check(scroll.Extent.Width > scroll.Viewport.Width, "wide streaming table has the original compact rows");
            var first = panel.Children[0];
            view.Markdown = table + "\n| 新行 | 增量追加保留原来的单元格和选择能力。 | appended |";
            await Ready();
            Check(ReferenceEquals(first, panel.Children[0]), "streaming append retains existing cell controls");
            view.IsStreaming = false;
            await Ready();
            Check(!panel.IsStreaming && scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled,
                "completion switches to fitted wrapping");
            Check(scroll.Extent.Width <= scroll.Viewport.Width + 1, "completed table needs no horizontal scrolling");
            Check(panel.GetVisualDescendants().OfType<MarkdownTextBlock>().Any(text => text.TextLayout.TextLines.Count > 1),
                "completed long cells wrap to multiple visual lines");
            Check(panel.Children.All(cell => cell.Bounds.Right <= panel.Bounds.Width + 1), "all cell bounds fit inside the table width");
            Check(panel.GetVisualDescendants().OfType<MarkdownTextBlock>().Any(text => text.ActualText.Contains("filename_that_must_remain_visible.txt", StringComparison.Ordinal)),
                "unbroken path text is retained");
            Check(panel.GetVisualDescendants().OfType<MarkdownTextBlock>().All(text => text.TextLayout.Width <= text.Bounds.Width + 1),
                "wrapped text layouts fit the cell instead of being clipped");
            Check(panel.GetVisualDescendants().OfType<MarkdownTextBlock>().Any(text => text.Inlines?.OfType<CodeInline>().Any() == true),
                "table retains rich inline code");
            Save(host, "table-completed.png");
            host.Width = 350;
            await Ready();
            Check(scroll.Extent.Width <= scroll.Viewport.Width + 1 && panel.Bounds.Width <= view.Bounds.Width + 1,
                "narrow window refits without horizontal overflow");
            Check(scroll.Bounds.Height <= MarkdownTableView.InlineMaxHeight + 1, "narrow wrapped table respects the height cap");
            foreach (var text in panel.GetVisualDescendants().OfType<MarkdownTextBlock>().Where(text => text.TextLayout.Width > text.Bounds.Width + 1))
                Console.WriteLine($"OVERFLOW width={text.Bounds.Width} glyphWidth={text.TextLayout.Width} wrap={text.TextWrapping} text={text.ActualText}");
            Check(panel.GetVisualDescendants().OfType<MarkdownTextBlock>().All(text => text.TextLayout.Width <= text.Bounds.Width + 1),
                "narrow cells show wrapped glyphs without horizontal clipping");
            Save(host, "table-narrow.png");
            host.Width = 800;
            var manyRows = "| 编号 | 内容 |\n| --- | --- |\n" + string.Concat(Enumerable.Range(0, 60).Select(index =>
                $"| {index} | 第{index}行：纵向内容应该可以滚动查看，并保留末行文字。 |\n"));
            view.Markdown = manyRows;
            await Ready();
            rendered = view.GetVisualDescendants().OfType<MarkdownTableView>().Single();
            scroll = Find<ScrollViewer>(rendered, "MarkdownTableScroll");
            Check(scroll.Bounds.Height <= MarkdownTableView.InlineMaxHeight + 1 && scroll.Extent.Height > scroll.Viewport.Height,
                "large table stops at 360 DIP with vertical scrolling");
            scroll.Offset = new Vector(0, scroll.Extent.Height);
            await Ready();
            Check(scroll.Offset.Y > 0, "last rows remain reachable inside the bounded viewport");
            var expand = Find<Button>(rendered, "ExpandMarkdownTable");
            await Click(expand, host);
            var preview = host.OwnedWindows.OfType<TableLightboxWindow>().Single();
            await Ready();
            Check(preview.WindowState == WindowState.FullScreen, "upper right button opens a real fullscreen window");
            var fullScroll = Find<ScrollViewer>(preview, "FullScreenTableScroll");
            var fullTable = preview.GetVisualDescendants().OfType<MarkdownTableView>().Single();
            Check(fullScroll.Bounds.Height > MarkdownTableView.InlineMaxHeight,
                "fullscreen uses the available screen height instead of the inline cap");
            Check(!Find<Button>(fullTable, "ExpandMarkdownTable").IsVisible, "fullscreen does not create another expand button");
            Check(fullTable.Table.GetVisualDescendants().OfType<MarkdownTextBlock>().Any(text => text.ActualText.Contains("第59行", StringComparison.Ordinal)),
                "fullscreen contains the complete last row");
            Check(fullScroll.Extent.Width <= fullScroll.Viewport.Width + 1, "fullscreen table fits screen width");
            Save(preview, "table-fullscreen.png");
            ForceForeground(preview.TryGetPlatformHandle()!.Handle);
            keybd_event(0x1B, 0, 0, 0); keybd_event(0x1B, 0, 2, 0);
            await Ready();
            Check(!preview.IsVisible && expand.IsFocused, "Escape closes fullscreen and returns focus to the expand button");
            await Click(expand, host);
            preview = host.OwnedWindows.OfType<TableLightboxWindow>().Single();
            await Ready();
            await Click(Find<Button>(preview, "CloseTableLightbox"), preview);
            Check(!preview.IsVisible, "fullscreen close button works with a native pointer");

            view.Markdown = "表格前文不应进入预览。\n\n" + table + "\n\n中间段落\n\n| 第二张 | 内容 |\n| --- | --- |\n| 独立标记 | 只显示第二张表格 |\n\n表格后文不应进入预览。";
            host.Width = 800;
            await Ready();
            var tables = view.GetVisualDescendants().OfType<MarkdownTableView>().ToArray();
            Check(tables.Length == 2, "a message can render two independent tables");
            await Click(Find<Button>(tables[1], "ExpandMarkdownTable"), host);
            preview = host.OwnedWindows.OfType<TableLightboxWindow>().Single();
            await Ready();
            var fullView = Find<RichMarkdownView>(preview, "FullScreenTable");
            Check(fullView.Markdown.Contains("独立标记", StringComparison.Ordinal)
                && !fullView.Markdown.Contains("中文换行", StringComparison.Ordinal)
                && !fullView.Markdown.Contains("表格后文", StringComparison.Ordinal),
                "each expand button opens only its own complete table");
            preview.Close();
            await Ready();
            var shortTable = "| A | B |\n| --- | --- |\n| short | small |";
            view.Markdown = shortTable;
            await Ready();
            var compact = view.GetVisualDescendants().OfType<MarkdownTableView>().Single();
            Check(Find<ScrollViewer>(compact, "MarkdownTableScroll").Bounds.Height < 160,
                "short completed tables keep their compact natural height");
            foreach (var theme in new[] { Avalonia.Styling.ThemeVariant.Light, Avalonia.Styling.ThemeVariant.Dark })
            {
                host.RequestedThemeVariant = theme;
                await Ready();
                Check(compact.Table.GetVisualDescendants().OfType<MarkdownTextBlock>().All(text => text.Foreground is not null),
                    "table foreground is available in " + theme);
                Save(host, "table-" + theme + ".png");
            }
            var vm = (MainViewModel)main.DataContext!;
            var assistant = new AssistantMessageViewModel(new RecordingClipboardService()) { Id = "table-live" };
            assistant.UpdateFrom(new ChatMessage("table-part", MessageRole.Assistant, "mock-table", [new MessageText(table)], null, false));
            vm.Timeline.Entries.Clear();
            vm.Timeline.Entries.Add(assistant);
            await Ready();
            var chatTable = main.GetVisualDescendants().OfType<MarkdownTableView>().Single();
            Check(chatTable.Table.IsStreaming, "real ChatPage assistant template inherits streaming state");
            assistant.FinalizeTurn(SessionOutcome.Completed, TimeSpan.FromSeconds(1));
            await Ready();
            Check(!chatTable.Table.IsStreaming, "real turn completion refits the existing table without replacing it");
            Check(Find<ScrollViewer>(chatTable, "MarkdownTableScroll").Extent.Width <= chatTable.Bounds.Width + 1,
                "completed table fits actual chat width");
            Save(main, "chat-table-completed.png");
        }
        finally
        {
            foreach (var window in host.OwnedWindows.ToArray()) window.Close();
            host.Close();
            SetPhysicalCursorPos(previousCursor.X, previousCursor.Y);
        }
        File.WriteAllLines(Path.Combine(evidence, "failures.txt"), failures);
        Console.WriteLine($"Native markdown table checks: {checks}; failures: {failures.Count}; evidence: {evidence}");
        return failures.Count == 0 ? 0 : 1;

        static T Find<T>(Visual root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
        async Task Ready() { await Task.Delay(350); await Dispatcher.Yield(DispatcherPriority.Background); host.UpdateLayout(); main.UpdateLayout(); }
        async Task Click(Control target, Window window)
        {
            ForceForeground(window.TryGetPlatformHandle()!.Handle); window.Activate();
            var point = target.PointToScreen(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2));
            SetPhysicalCursorPos(point.X, point.Y);
            mouse_event(2, 0, 0, 0, 0); mouse_event(4, 0, 0, 0, 0);
            await Ready();
        }
        void Save(Visual target, string name)
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)target.Bounds.Width), Math.Max(1, (int)target.Bounds.Height)));
            bitmap.Render(target);
            bitmap.Save(Path.Combine(evidence, name), PngBitmapEncoderOptions.Default);
        }
    }

    private static void ForceForeground(IntPtr hwnd)
    {
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var currentThread = GetCurrentThreadId();
        if (foregroundThread != currentThread) AttachThreadInput(currentThread, foregroundThread, true);
        try { BringWindowToTop(hwnd); SetForegroundWindow(hwnd); SetFocus(hwnd); }
        finally { if (foregroundThread != currentThread) AttachThreadInput(currentThread, foregroundThread, false); }
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);
    [DllImport("user32.dll")] private static extern bool SetPhysicalCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X; public int Y; }
}
