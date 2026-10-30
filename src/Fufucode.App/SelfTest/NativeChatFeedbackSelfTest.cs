using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Abstractions;
using Fufucode.App.Features.Skins;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;
using MessageText = Fufucode.App.Abstractions.TextBlock;
using TextBlock = Avalonia.Controls.TextBlock;
using static Fufucode.App.ViewModels.SettingsPageViewModel;

namespace Fufucode.App.SelfTest;

internal static class NativeChatFeedbackSelfTest
{
    internal static async Task<int> RunAsync(MainWindow main)
    {
        var evidence = Path.GetFullPath("tmp/chat-feedback-evidence");
        Directory.CreateDirectory(evidence);
        var failures = new List<string>();
        GetCursorPos(out var previousCursor);
        void Check(bool condition, string message)
        {
            Console.WriteLine((condition ? "PASS " : "FAIL ") + message);
            if (!condition) failures.Add(message);
        }
        await Task.Delay(500);
        var session = new MediaSession();
        var vm = new MainViewModel(session, new RecordingClipboardService(), SynchronousRefreshScheduler.Instance);
        vm.Settings.CurrentModelText = session.CurrentModel;
        vm.Settings.SelectedThinking = vm.Settings.ThinkingChoices.FirstOrDefault();
        vm.Input.SubmissionStarted += vm.Timeline.BeginSubmission;
        vm.Input.SubmissionFailed += vm.Timeline.FailSubmission;
        var page = new ChatPage { DataContext = vm };
        var window = new Window { Width = 1100, Height = 950, Content = page, Title = "Chat feedback native regression" };
        using var skin = SkinLease.Apply(window, SkinCatalog.Resolve("fluent"), Avalonia.Styling.ThemeVariant.Dark);
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Width = 1100;
        window.Height = 950;
        window.Activate();
        SetForegroundWindow(window.TryGetPlatformHandle()!.Handle);
        page.ApplyAppearance(new WorkbenchAppearance { ShowMessageTimes = true });
        vm.Timeline.ScrollToEndRequested += page.ScrollToEnd;
        try
        {
            await Settle();
            var input = page.FindControl<TextBox>("InputBox")!;
            var fixtures = new[] { "fixture-workspace.png", "fixture-chat.png", "fixture-composer.png" };
            var files = new List<AttachedFile>();
            foreach (var fixture in fixtures)
            {
                var source = fixture == "fixture-workspace.png" ? (Visual)main : fixture == "fixture-chat.png" ? page : page.FindControl<Border>("ComposerCard")!;
                Save(source, fixture);
                files.Add(await AttachedFile.LoadAsync(Path.Combine(evidence, fixture)));
            }
            var note = Path.Combine(evidence, "fixture-notes.txt");
            File.WriteAllText(note, "Attachment preview fixture.\nKeep file context intact.");
            files.Add(await AttachedFile.LoadAsync(note));
            files.Add(new AttachedFile(Path.Combine(evidence, "missing.pdf"), "missing historical document"));
            vm.Input.DraftText = "前文后文";
            input.Focus();
            input.CaretIndex = 2;
            vm.Input.AddAttachment(files[0]);
            Check(vm.Input.DraftText.StartsWith("前文〔▧ 01〕", StringComparison.Ordinal) && vm.Input.DraftText.EndsWith("后文", StringComparison.Ordinal),
                "adding an image inserts its numbered reference at the actual caret");
            foreach (var file in files.Skip(1)) vm.Input.AddAttachment(file);
            await Settle();
            var draftWallet = page.FindControl<StackPanel>("AttachedFiles")!.Children.OfType<AttachmentWallet>().Single();
            Check(draftWallet.Children.OfType<Grid>().Sum(row => row.Children.Count) == 3,
                "composer displays exactly three preview cards while collapsed");
            Check(draftWallet.GetVisualDescendants().OfType<Image>().Count(image => image.Source is not null) == 3,
                "composer renders actual image thumbnails");
            Save(window, "composer-previews.png");
            var draftRow = draftWallet.Children.OfType<Grid>().Single();
            var draftCard = (Border)draftRow.Children[0];
            ((Grid)draftCard.Child!).Children.OfType<Button>().First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            var lightbox = window.OwnedWindows.OfType<ImageLightboxWindow>().Single();
            Check(lightbox.Canvas.OriginalSize.Width > 240, "attachment opens the original image instead of the thumbnail");
            lightbox.Close();
            window.Activate();
            SetForegroundWindow(window.TryGetPlatformHandle()!.Handle);
            var prompt = vm.Input.ComposePrompt();
            var send = page.FindControl<Button>("SendButton")!;
            send.Command!.Execute(send.CommandParameter);
            await Task.Delay(25);
            var pending = vm.Timeline.Entries.OfType<UserMessageViewModel>().Last();
            Check(vm.Input.Attachments.Count == 0 && vm.Input.DraftText.Length == 0 && pending.DisplayAttachments.Count == 5,
                "send moves attachments from composer into the user message");
            Check(session.ImagesSent == 3 && session.Prompt == prompt,
                "the real attachment send path retains all images and full text context");
            window.UpdateLayout();
            var bubble = page.GetVisualDescendants().OfType<Border>().Single(control =>
                AutomationProperties.GetAutomationId(control) == "MSG-USER-BUBBLE");
            var body = bubble.GetVisualDescendants().OfType<RichMarkdownView>().Single();
            var messageLayout = (Grid)bubble.GetVisualParent()!;
            var wallet = messageLayout.Children.OfType<AttachmentWallet>().Single();
            Check(body.Markdown == pending.DisplayText && !body.Markdown.Contains("<attached_files>", StringComparison.Ordinal),
                "user body shows prose and references without the attachment transport envelope");
            Check(Math.Abs(wallet.Bounds.Width - bubble.Bounds.Width) < 1 &&
                Math.Abs(wallet.Bounds.X - bubble.Bounds.X) < 1,
                "attachment row and message bubble share both horizontal boundaries");
            var container = (Control)messageLayout.GetVisualParent()!.GetVisualParent()!;
            for (var frame = 0; frame < 12; frame++)
            {
                if (frame == 0) Save(window, "send-first-frame.png");
                Check(bubble.Opacity == 1 && container.Opacity == 1 && page.Opacity == 1, "send frame " + frame + " keeps message and page at full opacity");
                await Task.Delay(20);
            }
            var actions = page.GetVisualDescendants().OfType<MessageActionsView>().Single(control => ReferenceEquals(control.DataContext, pending));
            var times = container.GetVisualDescendants().OfType<TextBlock>().Where(text => text.Text == pending.TimeText && text.IsEffectivelyVisible).ToArray();
            Check(times.Length == 1 && actions.GetVisualDescendants().OfType<TextBlock>().Contains(times[0]),
                "user message has one timestamp, in its bottom action row");
            Save(window, "message-three-previews.png");
            wallet.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            Check(wallet.Children.OfType<Grid>().Sum(row => row.Children.Count) == 5,
                "wallet edge expands all five attachments downwards");
            Check(wallet.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("文件不可用", StringComparison.Ordinal) == true),
                "missing historical file stays visible as an informative card");
            Save(window, "message-expanded.png");
            session.Acknowledge(prompt);
            await Settle();
            Check(ReferenceEquals(pending, vm.Timeline.Entries.OfType<UserMessageViewModel>().Single()),
                "backend acknowledgement preserves the existing user bubble");

            var model = page.FindControl<Button>("ModelButton")!;
            model.IsEnabled = true;
            foreach (var keyword in new[] { "claude", "gpt", "gemini", "glm", "deepseek", "grok", "qwen", "free", "opus", "sonnet", "flash", "reasoning" })
                vm.Settings.ModelSearchKeywords.Add(new ModelKeywordItem(keyword, false));
            foreach (var id in new[] { "auto/gemini-3.8-flash", "antigravity/gemini-3.8-flash", "auto/claude-sonnet-5-5", "auto/deepseek-v4.1-flash", "auto/gpt-6-luna" })
                vm.Settings.ModelChoices.Add(new ModelChoice(id, id));
            model.Flyout!.ShowAt(model);
            await Settle();
            var popup = Popups().First(root => root.GetVisualDescendants().OfType<Border>().Any(control => control.Classes.Contains("modelPickerSurface")));
            var surface = popup.GetVisualDescendants().OfType<Border>().Single(control => control.Classes.Contains("modelPickerSurface"));
            var keywordItems = surface.GetVisualDescendants().OfType<ItemsControl>().Single(control => ReferenceEquals(control.ItemsSource, vm.Settings.ModelSearchKeywords));
            var chips = keywordItems.GetVisualDescendants().OfType<Border>().Where(control => control.Classes.Contains("keywordChip")).ToArray();
            Check(chips.Length == 12 && chips.All(chip => chip.TranslatePoint(default, keywordItems)!.Value.Y + chip.Bounds.Height <= keywordItems.Bounds.Height + .5),
                "all twelve keywords fit complete rows without clipping");
            var presenter = popup.GetVisualDescendants().OfType<FlyoutPresenter>().Single();
            Check(surface.Background is ISolidColorBrush { Color.A: 0 } && presenter.Background is not null,
                "model picker uses a single themed surface");
            var rowButton = surface.GetVisualDescendants().OfType<Button>().First(control => control.Classes.Contains("modelChoiceRow"));
            rowButton.Classes.Add("pointerHover");
            await Settle();
            var rowPresenter = rowButton.GetVisualDescendants().OfType<ContentPresenter>().First(control => control.Name == "PART_ContentPresenter");
            var hover = rowPresenter.Background as ISolidColorBrush;
            var rest = presenter.Background as ISolidColorBrush;
            Check(hover is not null && rest is not null && Luma(hover.Color) > Luma(rest.Color),
                "dark model hover is lighter than the panel resting surface");
            Save(popup, "model-picker.png");
            model.Flyout.Hide();
            await Settle();
            foreach (var target in new[]
            {
                page.GetVisualDescendants().OfType<Button>().Single(control => AutomationProperties.GetAutomationId(control) == "composer-permission"),
                model,
                page.FindControl<Button>("ThinkingButton")!,
                page.GetVisualDescendants().OfType<Button>().Single(control => AutomationProperties.GetAutomationId(control) == "ruler-turn-01"),
            })
            {
                input.Focus();
                target.IsEnabled = true;
                var hoverPoint = target.PointToScreen(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2));
                SetPhysicalCursorPos(hoverPoint.X, hoverPoint.Y);
                await Settle();
                var info = page.FindControl<Canvas>("ChatHintLayer")!.Children.SingleOrDefault();
                Check(info is not null && TopLevel.GetTopLevel(info) == window && !window.OpenedPopups.Any(),
                    "information draws inside the chat window without a popup for " + (target.Name ?? AutomationProperties.GetName(target)));
                Check(info is not null && info.Bounds.Height <= (target == model ? 30 : 60), "information bubble stays compact");
                if (info is not null)
                {
                    Save(info, "bubble-" + (target.Name ?? AutomationProperties.GetAutomationId(target)) + ".png");
                    Save(window, "bubble-context-" + (target.Name ?? AutomationProperties.GetAutomationId(target)) + ".png");
                }
                input.Focus();
                await Task.Delay(70);
            }
            window.WindowState = WindowState.Normal;
            window.Width = 650;
            await Settle();
            Check(Math.Abs(window.Bounds.Width - 650) < 1, "native window actually reaches the narrow width");
            Check(wallet.Bounds.Width <= page.Bounds.Width && body.Bounds.Width <= page.Bounds.Width,
                "narrow message keeps preview cards inside its content width");
            Save(window, "message-narrow.png");
        }
        finally
        {
            window.Close();
            SetPhysicalCursorPos(previousCursor.X, previousCursor.Y);
            vm.Timeline.Dispose();
            vm.WorkspacePanel.Dispose();
            vm.Sidebar.Dispose();
        }
        File.WriteAllLines(Path.Combine(evidence, "failures.txt"), failures);
        Console.WriteLine("Native chat feedback failures: " + failures.Count + "; evidence: " + evidence);
        return failures.Count == 0 ? 0 : 1;

        IEnumerable<TopLevel> Popups() => window.OpenedPopups.Select(popup => TopLevel.GetTopLevel(popup.Child!))
            .OfType<TopLevel>().Where(root => root.IsVisible);
        async Task Settle() { await Task.Delay(350); await Dispatcher.Yield(DispatcherPriority.Background); window.UpdateLayout(); }
        void Save(Visual target, string name)
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)target.Bounds.Width), Math.Max(1, (int)target.Bounds.Height)));
            bitmap.Render(target);
            bitmap.Save(Path.Combine(evidence, name), PngBitmapEncoderOptions.Default);
        }
        static double Luma(Color color) => color.R * .2126 + color.G * .7152 + color.B * .0722;
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool SetPhysicalCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X; public int Y; }

    private sealed class MediaSession : IChatSession, IChatAttachments, IExecutionModeSession
    {
        public SessionConnection Connection => SessionConnection.Connected;
        public SessionActivity Activity => SessionActivity.Idle;
        public SessionOutcome? LastOutcome => null;
        public string CurrentModel => "preview-model";
        public bool CanSend => true;
        public bool CanStop => false;
        public int QueuedCount => 0;
        public bool CanSendImages => true;
        public bool SupportsExecutionModes => true;
        public string ExecutionMode { get; private set; } = "manual";
        public int ImagesSent { get; private set; }
        public string? Prompt { get; private set; }
        public event EventHandler<SessionEvent>? Event;
        public event Action<string>? ExecutionModeChanged;
        public Task SendAsync(string text, CancellationToken cancellationToken = default) { Prompt = text; return Task.CompletedTask; }
        public Task SendWithImagesAsync(string text, IReadOnlyList<ChatImage> images, CancellationToken cancellationToken = default)
        { Prompt = text; ImagesSent = images.Count; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetExecutionModeAsync(string mode, CancellationToken cancellationToken = default)
        { ExecutionMode = mode; ExecutionModeChanged?.Invoke(mode); return Task.CompletedTask; }
        public void Acknowledge(string prompt)
        {
            Event?.Invoke(this, new SessionEvent.MessageStarted("native-user", MessageRole.User, null, 1));
            Event?.Invoke(this, new SessionEvent.MessageFinished("native-user", new ChatMessage("native-user", MessageRole.User, null, [new MessageText(prompt)], null, true), null));
        }
    }
}
