using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Abstractions;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;
using LiveMarkdown.Avalonia;
using MessageText = Fufucode.App.Abstractions.TextBlock;
using MarkdownTextBlock = LiveMarkdown.Avalonia.MarkdownTextBlock;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Fufucode.App.SelfTest;

internal static class NativeTypographySelfTest
{
    internal static async Task<int> RunAsync(MainWindow window)
    {
        var evidence = Path.GetFullPath("tmp/typography-evidence");
        Directory.CreateDirectory(evidence);
        var failures = new List<string>();
        void Check(bool condition, string message)
        {
            Console.WriteLine((condition ? "PASS " : "FAIL ") + message);
            if (!condition) failures.Add(message);
        }
        await Task.Delay(600);
        window.WindowState = WindowState.Normal;
        window.Width = 1440;
        window.Height = 1050;
        window.CustomizationForTesting.Change(current => current with
        {
            Theme = "dark", Skin = "fluent", Accent = "#26FF8E", Emphasis = "#E8B86A", FontSize = 15,
            EnglishFont = "system", ChineseFont = "system",
            BaseBackground = "#1C1C1C", BaseBar = "#2C2C2C", BaseHighlight = "#3A3A3A",
        });
        window.ToggleDockForTesting(DockZone.Right);
        var vm = (MainViewModel)window.DataContext!;
        const string content = """
            ## 清晰、安静的阅读体验

            中文正文应当清楚、均匀：软件界面首先要易读，长时间阅读也不刺眼。
            这里有一段 **重要内容**，以及行内标记 `AppSettingsStore`、`正文强调` 和 `字体渲染`。

            English text stays neutral and legible: Preview, Settings, 0123456789.

            - 主题色仅标识当前操作，正文以浅灰为主。
            - 强调色降低饱和度，行内底框与一级背景一致。
            - 中英文字体分别选择，切换后立即生效。

            ```csharp
            var message = "代码保留等宽字体";
            ```

            > 引用内容与正文保持相近亮度，不再突然出现纯黑色块。

            | 检查项目 | 结果 |
            | --- | --- |
            | 中文字形 | 清晰、中性 |
            | 背景层级 | 深灰、一般栏、高亮 |
            """;
        vm.Timeline.LoadHistory([
            new ChatMessage("type-user", MessageRole.User, null, [new MessageText("把正文调得柔和一点，字体清楚，不要彩色毛边。")], null, true),
            new ChatMessage("type-assistant", MessageRole.Assistant, "preview", [new MessageText(content)], null, true),
        ]);
        var host = window.FindControl<TransitioningContentControl>("PageHost")!;
        var page = (ChatPage)host.Content!;
        await Settle();
        for (var attempt = 0; attempt < 30 && !page.GetVisualDescendants().OfType<MarkdownTextBlock>()
                 .Any(block => string.Concat(Flatten(block.Inlines).OfType<Run>().Select(run => run.Text)).Contains("背景层级", StringComparison.Ordinal)); attempt++)
            await Task.Delay(100);
        var view = page.GetVisualDescendants().OfType<RichMarkdownView>().First(control => control.Markdown.Contains("清晰、安静", StringComparison.Ordinal));
        var blocks = view.GetVisualDescendants().OfType<MarkdownTextBlock>().ToArray();
        Save(window, "typography-initial.png");
        Console.WriteLine("MARKDOWN blocks=" + blocks.Length + " values=" + string.Join(" | ", blocks.Select(block => block.Text ?? string.Concat(Flatten(block.Inlines).OfType<Run>().Select(run => run.Text)))));
        var body = blocks.First(block => (block.Text ?? string.Concat(Flatten(block.Inlines).OfType<Run>().Select(run => run.Text)))
            .Contains("中文正文应当", StringComparison.Ordinal));
        Check(TextOptions.GetTextRenderingMode(window) == TextRenderingMode.Antialias &&
              TextOptions.GetTextRenderingMode(view) == TextRenderingMode.Antialias,
            "window and Markdown explicitly use grayscale antialiasing");
        Check(body.Foreground is ISolidColorBrush { Color: var foreground } && foreground == Color.Parse("#E4E4E4"),
            "actual Markdown body restores readable near-white with grayscale edges");
        var firstCodes = blocks.SelectMany(block => Flatten(block.Inlines)).OfType<CodeInline>().ToArray();
        Check(firstCodes.Length >= 3 && firstCodes.All(code => code.Foreground is ISolidColorBrush front && front.Color == Color.Parse("#E8B86A") &&
            code.Background is ISolidColorBrush back && back.Color == Color.Parse("#1C1C1C")),
            "chat data-template inline colors are correct before any appearance refresh");
        Console.WriteLine("BODY family=" + body.FontFamily + " foreground=" + body.Foreground);
        Check(body.FontFamily.Equals(window.FontFamily) && !body.FontFamily.ToString().Contains("Noto", StringComparison.Ordinal),
            "Markdown follows the restored default UI family");
        var regular = Shape("汉字 中文标点，。English", body.FontFamily);
        Check(regular.Any(run => run.Text.Contains('汉') && run.Face.Weight == FontWeight.Normal),
            "default Chinese body actually shapes at regular weight rather than Thin");
        foreach (var run in regular) Console.WriteLine("SHAPED " + run.Text + " => " + run.Face.FamilyName + " " + run.Face.Weight);
        var englishChoices = AppTypography.EnglishChoices;
        var chineseChoices = AppTypography.ChineseChoices;
        Console.WriteLine($"FONT CHOICES English={englishChoices.Count} Chinese={chineseChoices.Count}");
        Check(englishChoices.Any(choice => choice.Value == "Inter") && chineseChoices.Count > 1,
            "font picker includes bundled Inter and installed Chinese families");
        Check(chineseChoices.All(choice => choice.Value == "system" ||
            (FontManager.Current.TryGetGlyphTypeface(new Typeface(new FontFamily(choice.Value)), out var face) &&
             "汉字审批权限规划继续确认变化".All(character => face.CharacterToGlyphMap.ContainsGlyph(character)))),
            "Chinese pool contains complete Chinese UI coverage rather than partial Japanese or Korean Han coverage");
        var rare = Shape("𠮷𰻞", body.FontFamily);
        Check(rare.All(run => run.Face.Weight == FontWeight.Normal && run.Face.FamilyName.StartsWith("SimSun-Ext", StringComparison.Ordinal)),
            "extended Han glyphs use installed Chinese extension fonts at regular weight");
        var chineseChoice = chineseChoices.First(choice => choice.Value is "宋体" or "SimSun");
        var chineseFace = new Typeface(new FontFamily(chineseChoice.Value)).GlyphTypeface.FamilyName;
        window.CustomizationForTesting.Change(current => current with { EnglishFont = "Inter", ChineseFont = chineseChoice.Value });
        await Settle();
        var selected = Shape("English 汉字，。", body.FontFamily);
        foreach (var run in selected) Console.WriteLine("SELECTED " + run.Text + " => " + run.Face.FamilyName + " " + run.Face.Weight);
        Check(selected.Any(run => run.Text.Contains("English", StringComparison.Ordinal) && run.Face.FamilyName.Contains("Inter", StringComparison.Ordinal)) &&
            selected.Any(run => run.Text.Contains('汉') && run.Face.FamilyName == chineseFace),
            "actual text uses independent selected English and Chinese faces");
        Check(page.FindControl<TextBox>("InputBox")!.FontFamily.Equals(body.FontFamily),
            "composer follows the same live font selection");
        var persisted = new WorkbenchAppearanceStore(window.CustomizationForTesting.DataRoot).Current;
        Check(persisted.EnglishFont == "Inter" && persisted.ChineseFont == chineseChoice.Value, "font pair survives reopening the settings store");
        window.CustomizationForTesting.Change(current => current with { ChineseFont = "Microsoft YaHei UI" });
        await Settle();
        Check(Shape("English 汉字", body.FontFamily).Any(run => run.Text.Contains("English", StringComparison.Ordinal) && run.Face.FamilyName.Contains("Inter", StringComparison.Ordinal)),
            "changing Chinese font preserves the selected English face");
        var codes = blocks.SelectMany(block => Flatten(block.Inlines)).OfType<CodeInline>().ToArray();
        foreach (var code in codes) Console.WriteLine("INLINE COLORS " + code.Text + " front=" + code.Foreground + " back=" + code.Background +
            " parent=" + code.Parent?.GetType().Name + " theme=" + code.ActualThemeVariant);
        Console.WriteLine("MAIN EMPHASIS " + window.Resources["AppEmphasis"] + " renderer=" +
            (view.TryFindResource("AppEmphasis", view.ActualThemeVariant, out var resolvedEmphasis) ? resolvedEmphasis : "missing"));
        Check(codes.Length >= 3 && codes.All(code => code.Foreground is ISolidColorBrush { Color: var c } && c == Color.Parse("#E8B86A")),
            "inline emphasis uses the warm amber palette");
        Check(codes.All(code => code.Background is ISolidColorBrush { Color: var c } && c == Color.Parse("#1C1C1C") && code.BorderThickness == 0),
            "inline boxes match the actual first background tier and have no outline");
        page.FindControl<ScrollViewer>("TimelineScroller")!.Offset = default;
        await Settle();
        Save(window, "typography-dark.png");
        var monoBlocks = page.GetVisualDescendants().OfType<HighlightedCodeView>().SelectMany(code => code.GetVisualDescendants().OfType<TextBlock>())
            .Where(block => block.Name == "PART_CodeTextBlock").ToArray();
        Console.WriteLine("CODE FONTS " + string.Join(" | ", monoBlocks.Select(block => block.GetType().FullName + "=" + block.FontFamily)));
        Check(monoBlocks.Length > 0 && monoBlocks.All(block => block.FontFamily.Equals(AppTypography.MonoFamily) &&
            new Typeface(block.FontFamily).GlyphTypeface.Metrics.IsFixedPitch),
            "code blocks use the shared monospace family with the selected Chinese fallback");
        Check(monoBlocks.SelectMany(block => Flatten(block.Inlines)).OfType<Run>()
            .All(run => run.Background is ISolidColorBrush brush && brush.Color.A == 0),
            "syntax highlight tokens no longer paint a separate black background");
        var textControl = new TextBlock
        {
            Text = "清晰正文 Open source 0123456789", FontFamily = body.FontFamily, FontSize = 20,
            Foreground = new SolidColorBrush(Color.Parse("#E4E4E4")), Background = new SolidColorBrush(Color.Parse("#1C1C1C")),
            Padding = new Thickness(10),
        };
        TextOptions.SetTextRenderingMode(textControl, TextRenderingMode.Antialias);
        textControl.Measure(new Size(900, 100));
        textControl.Arrange(new Rect(textControl.DesiredSize));
        Save(textControl, "glyph-grayscale.png");
        await CheckFreshInlineRendering();
        window.CustomizationForTesting.Change(current => current with { BaseBackground = "#161616" });
        await Settle();
        Check(codes.All(code => code.Background is ISolidColorBrush { Color: var c } && c == Color.Parse("#161616")),
            "inline backgrounds update when the user changes the first background tier");
        window.CustomizationForTesting.Change(current => current with { Theme = "light" });
        await Settle();
        Check(body.Foreground is ISolidColorBrush { Color: var lightText } && lightText == Color.Parse("#343434"),
            "light theme retains a readable dark body color");
        Check(codes.All(code => code.Foreground is ISolidColorBrush { Color: var c } && c == Color.Parse("#8A570F")),
            "light emphasis uses its darker readable counterpart");
        Check(monoBlocks.SelectMany(block => Flatten(block.Inlines)).OfType<Run>()
            .All(run => run.Background is ISolidColorBrush brush && brush.Color.A == 0),
            "syntax token backgrounds stay transparent after changing theme");
        Save(window, "typography-light.png");
        window.CustomizationForTesting.Change(current => current with { Theme = "dark" });
        await window.NavigateForTestingAsync(AppPage.Settings);
        vm.Settings.SelectCategory("appearance");
        await Settle();
        var englishPicker = window.GetVisualDescendants().OfType<FontPicker>()
            .Single(control => AutomationProperties.GetAutomationId(control) == "appearance-english-font");
        var chinesePicker = window.GetVisualDescendants().OfType<FontPicker>()
            .Single(control => AutomationProperties.GetAutomationId(control) == "appearance-chinese-font");
        window.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "SettingsScroller").Offset = new Vector(0, 780);
        await Settle();
        englishPicker.IsDropDownOpen = true;
        englishPicker.SearchBox.Text = "Segoe UI";
        await Settle();
        englishPicker.SearchBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await Settle();
        Check(window.CustomizationForTesting.Current.EnglishFont == "Segoe UI" &&
            window.CustomizationForTesting.Current.ChineseFont == "Microsoft YaHei UI", "appearance English picker saves without changing Chinese");
        chinesePicker.IsDropDownOpen = true;
        chinesePicker.SearchBox.Text = chineseChoice.Label;
        await Settle();
        chinesePicker.SearchBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await Settle();
        Check(window.CustomizationForTesting.Current.ChineseFont == chineseChoice.Value &&
            window.CustomizationForTesting.Current.EnglishFont == "Segoe UI", "appearance Chinese picker saves without changing English");
        Save(window, "font-settings-selected.png");
        window.CustomizationForTesting.Change(current => current with { EnglishFont = "system", ChineseFont = "system" });
        await Settle();
        Save(window, "font-settings.png");
        chinesePicker.IsDropDownOpen = true;
        chinesePicker.SearchBox.Text = chineseChoice.Label[..1];
        await Settle();
        var popup = window.OpenedPopups.Select(open => open.Child).OfType<Control>()
            .Select(child => TopLevel.GetTopLevel(child)).FirstOrDefault();
        Check(popup is not null && TextOptions.GetTextRenderingMode(popup) == TextRenderingMode.Antialias,
            "font dropdown host uses grayscale antialiasing");
        if (popup is not null) Save(popup, "font-dropdown.png");
        chinesePicker.Results.SelectedItem = chineseChoice;
        chinesePicker.Results.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await Settle();
        Check(window.CustomizationForTesting.Current.ChineseFont == chineseChoice.Value,
            "choosing a dropdown item applies the font");
        chinesePicker.IsDropDownOpen = true;
        await Settle();
        Check(string.IsNullOrEmpty(chinesePicker.SearchBox.Text) && chinesePicker.Results.ItemCount == chineseChoices.Count,
            "reopening the Chinese picker restores the complete pool and an independent empty query");
        chinesePicker.IsDropDownOpen = false;
        window.CustomizationForTesting.Change(current => current with { EnglishFont = "system", ChineseFont = "system" });
        var toolCards = new StackPanel { Spacing = 6 };
        foreach (var state in new[] { "success", "failed", "running" })
        {
            var card = new Border { Classes = { "toolCard", state }, Padding = new Thickness(12, 8), Child = new TextBlock { Text = "read_file · " + state } };
            toolCards.Children.Add(card);
        }
        var groupCard = new Border { Classes = { "toolGroupCard" }, Padding = new Thickness(10), Child = toolCards };
        var toolWindow = new Window { Width = 620, Height = 240, Content = groupCard, Topmost = true };
        toolWindow.Classes.Add("fluentWorkbench");
        toolWindow.Show();
        toolWindow.WindowState = WindowState.Normal;
        toolWindow.Width = 620;
        toolWindow.Height = 240;
        await Task.Delay(200);
        toolWindow.Position = new PixelPoint(0, 0);
        toolWindow.Activate();
        SetForegroundWindow(toolWindow.TryGetPlatformHandle()!.Handle);
        foreach (var card in toolCards.Children.OfType<Border>())
        {
            var state = card.Classes.First(value => value is "success" or "failed" or "running");
            var expected = Color.Parse(state == "success" ? "#22C55E" : state == "failed" ? "#EF4444" : "#F59E0B");
            Check(card.BorderBrush is ISolidColorBrush { Color: var c } && c == expected, "tool " + state + " retains its status border");
            card.Classes.Add("pointerHover");
            Check(card.BorderBrush is ISolidColorBrush { Color: var hover } && hover == expected, "tool " + state + " hover preserves its status border");
            var point = card.PointToScreen(new Point(card.Bounds.Width / 2, card.Bounds.Height / 2));
            SetPhysicalCursorPos(point.X, point.Y);
            await Task.Delay(120);
            Check(card.IsPointerOver && card.BorderBrush is ISolidColorBrush { Color: var pointer } && pointer == expected,
                "tool " + state + " real pointer hover retains its status border");
            Check(card.CornerRadius.TopLeft > 0 && card.CornerRadius == groupCard.CornerRadius,
                "nested tool card keeps the normal card corner radius");
        }
        Save(toolWindow, "tool-status-borders.png");
        toolWindow.Close();
        File.WriteAllLines(Path.Combine(evidence, "failures.txt"), failures);
        Console.WriteLine("Native typography failures: " + failures.Count + "; evidence: " + evidence);
        return failures.Count == 0 ? 0 : 1;

        async Task CheckFreshInlineRendering()
        {
            var fresh = new RichMarkdownView { Markdown = "首屏强调 `FIRST_FRAME_01`，普通正文保持清楚。" };
            var sample = new Window
            {
                Width = 720, Height = 220, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark,
                Content = new Border { Background = new SolidColorBrush(Color.Parse("#1C1C1C")), Padding = new Thickness(20), Child = fresh },
            };
            AppTypography.Apply(sample, window.CustomizationForTesting.Current);
            sample.Resources["AppInlineCodeBackground"] = new SolidColorBrush(Color.Parse("#1C1C1C"));
            sample.Resources["AppEmphasis"] = new SolidColorBrush(Color.Parse("#E8B86A"));
            sample.Show(window);
            try
            {
                var first = await WaitForCode("FIRST_FRAME_01");
                Check(CodePaint(first.Code, "#1C1C1C", "#E8B86A"), "fresh Markdown inline starts with amber and the first background tier without a settings refresh");
                Save(sample, "inline-first-frame.png");
                CheckPixels(first.Block, first.Code, "#1C1C1C", "initial");
                fresh.Markdown += "\n\n流式追加 `STREAMED_02`，新片段保持同一背景。";
                var streamed = await WaitForCode("STREAMED_02");
                Check(CodePaint(streamed.Code, "#1C1C1C", "#E8B86A"), "streamed inline code receives the same colors when created asynchronously");
                CheckPixels(streamed.Block, streamed.Code, "#1C1C1C", "streamed");
                sample.Resources["AppInlineCodeBackground"] = new SolidColorBrush(Color.Parse("#161616"));
                ((Border)sample.Content!).Background = new SolidColorBrush(Color.Parse("#161616"));
                await Task.Delay(150);
                Check(CodePaint(first.Code, "#161616", "#E8B86A") && CodePaint(streamed.Code, "#161616", "#E8B86A"),
                    "existing and appended inline colors track changed dynamic resources");
                CheckPixels(streamed.Block, streamed.Code, "#161616", "changed-background");
                Save(sample, "inline-streamed.png");
            }
            finally { sample.Close(); }

            async Task<(MarkdownTextBlock Block, CodeInline Code)> WaitForCode(string text)
            {
                for (var attempt = 0; attempt < 60; attempt++)
                {
                    await Task.Delay(30);
                    sample.UpdateLayout();
                    foreach (var block in fresh.GetVisualDescendants().OfType<MarkdownTextBlock>())
                    {
                        var code = Flatten(block.Inlines).OfType<CodeInline>().FirstOrDefault(code => code.Text == text);
                        if (code is null) continue;
                        await Dispatcher.Yield(DispatcherPriority.Render);
                        return (block, code);
                    }
                }
                throw new InvalidOperationException("Missing asynchronously rendered inline: " + text);
            }
            static bool CodePaint(CodeInline code, string background, string foreground) =>
                code.Background is ISolidColorBrush back && back.Color == Color.Parse(background) &&
                code.Foreground is ISolidColorBrush front && front.Color == Color.Parse(foreground);
            void CheckPixels(MarkdownTextBlock block, CodeInline code, string background, string phase)
            {
                var rects = block.GetCodeInlineRects(code);
                Check(rects.Count > 0, phase + " inline painted bounds are available");
                var scale = sample.RenderScaling;
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(sample.Bounds.Width * scale),
                    (int)Math.Ceiling(sample.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
                bitmap.Render(sample);
                using var readable = new WriteableBitmap(bitmap.PixelSize, bitmap.Dpi, Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
                using var pixels = readable.Lock();
                bitmap.CopyPixels(pixels);
                var backgroundColor = Color.Parse(background);
                var darker = 0; var inspected = 0;
                foreach (var rect in rects)
                {
                    var origin = block.TranslatePoint(rect.Position, sample)!.Value;
                    for (var y = Math.Max(0, (int)Math.Ceiling(origin.Y * scale)); y < Math.Min(bitmap.PixelSize.Height, (int)Math.Floor((origin.Y + rect.Height) * scale)); y++)
                    for (var x = Math.Max(0, (int)Math.Ceiling(origin.X * scale)); x < Math.Min(bitmap.PixelSize.Width, (int)Math.Floor((origin.X + rect.Width) * scale)); x++)
                    {
                        var pixel = Marshal.ReadInt32(pixels.Address, y * pixels.RowBytes + x * 4);
                        var blue = pixel & 255; var green = (pixel >> 8) & 255; var red = (pixel >> 16) & 255;
                        inspected++;
                        if (red < backgroundColor.R - 1 || green < backgroundColor.G - 1 || blue < backgroundColor.B - 1) darker++;
                    }
                }
                Console.WriteLine($"INLINE PIXELS {phase} samples={inspected} darkerThanBackground={darker} scale={scale}");
                Check(inspected > 20 && darker == 0, phase + " rendered inline has no darker black backing pixels");
            }
        }

        async Task Settle() { await Task.Delay(500); await Dispatcher.Yield(DispatcherPriority.Background); window.UpdateLayout(); }
        void Save(Visual visual, string file)
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)visual.Bounds.Width), Math.Max(1, (int)visual.Bounds.Height)));
            bitmap.Render(visual);
            bitmap.Save(Path.Combine(evidence, file), PngBitmapEncoderOptions.Default);
        }
        static (string Text, GlyphTypeface Face)[] Shape(string text, FontFamily family)
        {
            using var layout = new TextLayout(text, new Typeface(family), 15, Brushes.White);
            return layout.TextLines.SelectMany(line => line.TextRuns).OfType<ShapedTextRun>()
                .Select(run => (run.Text.ToString(), run.GlyphRun.GlyphTypeface)).ToArray();
        }
        static IEnumerable<Inline> Flatten(InlineCollection? inlines)
        {
            if (inlines is null) yield break;
            foreach (var inline in inlines)
            {
                yield return inline;
                if (inline is Span span) foreach (var child in Flatten(span.Inlines)) yield return child;
            }
        }
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool SetPhysicalCursorPos(int x, int y);
}
