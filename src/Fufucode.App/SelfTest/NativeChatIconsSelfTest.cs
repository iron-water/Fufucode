using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Abstractions;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;
using MessageText = Fufucode.App.Abstractions.TextBlock;

namespace Fufucode.App.SelfTest;

internal static class NativeChatIconsSelfTest
{
    private static readonly Color MsRed = Color.Parse("#E81123");

    private static Color ResolveBrush(StyledElement root, string key, Color fallback) =>
        root.TryFindResource(key, null, out var value) && value is ISolidColorBrush brush ? brush.Color : fallback;

    internal static async Task<int> RunAsync(MainWindow main)
    {
        var evidence = Path.GetFullPath("tmp/chat-icons-evidence");
        Directory.CreateDirectory(evidence);
        var failures = new List<string>();
        var checks = 0;
        // 绿色跟随主题强调色、黄色跟随强调文字色：断言读取窗口实际资源，避免写死十六进制。
        var accent = ResolveBrush(main, "AppAccent", Color.Parse("#26FF8E"));
        var emphasis = ResolveBrush(main, "AppEmphasis", Color.Parse("#E8B86A"));

        void Check(bool condition, string message)
        {
            checks++;
            Console.WriteLine((condition ? "PASS " : "FAIL ") + message);
            if (!condition) failures.Add(message);
        }

        void AssertIconColor(PathIcon icon, Color expected, string name)
        {
            var brush = icon.Foreground as ISolidColorBrush;
            var match = brush is not null && brush.Color == expected;
            Check(match, $"{name} 前景应为 {expected}（实测：{brush?.Color}）");
        }

        await Task.Delay(400);
        var page = main.GetVisualDescendants().OfType<ChatPage>().FirstOrDefault();
        if (page is null)
        {
            await main.NavigateForTestingAsync(AppPage.Chat);
            await Settle();
            page = main.GetVisualDescendants().OfType<ChatPage>().Single();
        }

        var vm = (MainViewModel)main.DataContext!;
        vm.Timeline.Entries.Clear();

        // 构造用户消息与助手消息（含思考块、工具块、文件变更卡、相关文件卡、网页链接等）
        var userMsg = new UserMessageViewModel
        {
            Id = "user-1",
            Text = "请帮我修改表格与图标渲染。",
        };
        vm.Timeline.Entries.Add(userMsg);

        var assistantMsg = new AssistantMessageViewModel(new RecordingClipboardService())
        {
            Id = "asst-1",
            Model = "gpt-5",
        };
        assistantMsg.UpdateFrom(new ChatMessage("msg-1", MessageRole.Assistant, "gpt-5",
        [
            new ThinkingBlock("正在思考颜色体系如何精准映射到微软红黄蓝绿四色…"),
            new ToolCallBlock("tool-1", "edit_file", "{\"path\": \"ChatPage.axaml\"}", ToolCallStatus.Completed, "ok", "ok", false, 450),
            new MessageText("这是回复内容，包含网页链接：https://microsoft.com 以及相关文件。"),
        ], null, true));

        // 注入隔离相关文件；验证真实预览而非仅检测文字。
        var fixtureRoot = Path.Combine(evidence, "project");
        Directory.CreateDirectory(Path.Combine(fixtureRoot, "src", "Views"));
        File.WriteAllText(Path.Combine(fixtureRoot, "src", "Views", "ChatPage.axaml"), "<Grid />");
        vm.WorkspacePanel.AttachMock(fixtureRoot);
        assistantMsg.Files.Add(new MessageFileViewModel("src/Views/ChatPage.axaml"));
        assistantMsg.ResolveReferencedFiles(fixtureRoot);
        assistantMsg.RelatedFilesExpanded = true;
        assistantMsg.ChangeCard.Files.Add(new TurnFileChangeViewModel("src/Views/ChatPage.axaml", 12, 4));
        assistantMsg.ChangeCard.VisibleFiles.Add(new TurnFileChangeViewModel("src/Views/ChatPage.axaml", 12, 4));

        vm.Timeline.Entries.Add(assistantMsg);

        // 注入错误与提示条目
        var errorEntry = new ErrorEntryViewModel
        {
            KindText = "编译错误",
            Message = "这是一条用于测试红绿蓝黄图标配色的错误提示条目。",
        };
        vm.Timeline.Entries.Add(errorEntry);

        // 打开草稿待恢复与引用状态进行测试
        typeof(InputViewModel).GetMethod("StashPendingRestore", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.Invoke(vm.Input, ["恢复已清空的排队文本"]);
        vm.Input.ReferenceContext = "已引用选区上下文";

        await Settle();

        static PathIcon FindIcon(Control control) =>
            (control as PathIcon) ??
            (control is ContentControl cc && cc.Content is PathIcon pi ? pi : null) ??
            control.GetVisualDescendants().OfType<PathIcon>().First();

        try
        {
            // 1. 标题栏按钮图标（均为微软蓝）
            var headerMore = page.FindControl<Button>("HeaderActionsButton")!;
            var headerInfo = page.FindControl<Button>("SessionDetailsButton")!;

            AssertIconColor(FindIcon(headerMore), accent, "标题栏更多操作图标");
            AssertIconColor(FindIcon(headerInfo), accent, "标题栏会话详情图标");

            // 2. 消息头像（用户：蓝，助手：绿）
            var userAvatar = page.GetVisualDescendants().OfType<Border>().First(b => Avalonia.Automation.AutomationProperties.GetAutomationId(b) == "MSG-USER-AVATAR");
            AssertIconColor(FindIcon(userAvatar), accent, "用户头像图标");

            var asstAvatar = page.GetVisualDescendants().OfType<Border>().First(b => Avalonia.Automation.AutomationProperties.GetAutomationId(b) == "MSG-ASSISTANT-AVATAR");
            AssertIconColor(FindIcon(asstAvatar), accent, "AI助手头像图标");

            // 3. 正文块内图标：思考（黄）、工具（蓝）、代码复制（蓝）、相关文件（黄）、变更卡（绿）、跳转打开（蓝）
            var thinkingPanel = page.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("thinkingBlock"));
            var brainIcon = thinkingPanel.GetVisualDescendants().OfType<PathIcon>().First();
            AssertIconColor(brainIcon, emphasis, "思考过程大脑图标");

            var toolCard = page.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("toolGroupCard"));
            var wrenchIcon = toolCard.GetVisualDescendants().OfType<PathIcon>().First();
            AssertIconColor(wrenchIcon, accent, "工具组扳手图标");
            Check((brainIcon.Foreground as ISolidColorBrush)?.Color != (wrenchIcon.Foreground as ISolidColorBrush)?.Color, "输出中的思考与工具图标颜色不能相同");

            var relatedCard = page.GetVisualDescendants().OfType<Border>().First(b => Avalonia.Automation.AutomationProperties.GetAutomationId(b) == "MSG-RELATED-FILES-CARD");
            AssertIconColor(relatedCard.GetVisualDescendants().OfType<PathIcon>().First(), emphasis, "相关文件文档图标");

            var fileRow = relatedCard.GetVisualDescendants().OfType<Button>().Single(button => button.DataContext is MessageFileViewModel { Exists: true });
            fileRow.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Settle();
            Check(vm.WorkspacePanel.IsFilePreviewOpen && vm.WorkspacePanel.PreviewFilePath.EndsWith("ChatPage.axaml", StringComparison.OrdinalIgnoreCase),
                "相关文件卡通过文件管理的同一预览宿主打开隔离文件");
            vm.WorkspacePanel.CloseFilePreviewCommand.Execute(null);

            var changeCard = page.GetVisualDescendants().OfType<Border>().First(b => Avalonia.Automation.AutomationProperties.GetAutomationId(b) == "MSG-TURN-CHANGE-CARD");
            AssertIconColor(changeCard.GetVisualDescendants().OfType<PathIcon>().First(), accent, "代码变更卡编辑图标");
            AssertIconColor(changeCard.GetVisualDescendants().OfType<PathIcon>().Last(), accent, "变更文件打开跳转图标");

            // 3b. 变更增删配色：新增=主题强化色，删除=大红
            var diffAdded = ResolveBrush(main, "AppDiffAddedForeground", accent);
            var diffRemoved = ResolveBrush(main, "AppDiffRemovedForeground", MsRed);
            Check(diffAdded == accent, $"diff 新增前景应等于主题强调色（实测：{diffAdded}）");
            Check(diffRemoved == MsRed, $"diff 删除前景应为大红 {MsRed}（实测：{diffRemoved}）");
            var addedText = changeCard.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().First(t => t.Classes.Contains("additions"));
            var removedText = changeCard.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().First(t => t.Classes.Contains("deletions"));
            var addedColor = (addedText.Foreground as ISolidColorBrush)?.Color;
            var removedColor = (removedText.Foreground as ISolidColorBrush)?.Color;
            Check(addedColor == accent, $"变更卡新增文字应为主题绿色（实测：{addedColor}）");
            Check(removedColor == MsRed, $"变更卡删除文字应为大红 {MsRed}（实测：{removedColor}）");

            // 4. 错误卡（红圈、红叉）
            var errorCard = page.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("errorCard"));
            var errorIcons = errorCard.GetVisualDescendants().OfType<PathIcon>().ToArray();
            var errorCircle = errorIcons.First(i => i.IsVisible);
            AssertIconColor(errorCircle, MsRed, "错误卡提示红圈");
            var errorDismiss = errorIcons.Last();
            AssertIconColor(errorDismiss, MsRed, "错误卡关闭按钮图标");

            // 5. 输入栏内的图标应回到界面默认中性色（深色主题下接近白色），不再使用彩色
            void AssertNeutral(PathIcon icon, string name)
            {
                var color = (icon.Foreground as ISolidColorBrush)?.Color;
                var neutral = color is { } c && Math.Abs(c.R - c.G) < 24 && Math.Abs(c.G - c.B) < 24 && c.R > 140;
                Check(neutral, $"{name} 应回到界面默认白/中性色（实测：{color}）");
            }

            var composerMore = page.FindControl<Button>("ComposerMoreButton")!;
            AssertNeutral(FindIcon(composerMore), "输入栏附加功能加号图标");

            var attachBtn = page.FindControl<Button>("AttachContextButton")!;
            AssertNeutral(FindIcon(attachBtn), "输入栏附件回形针图标");

            var projectBtn = page.FindControl<Button>("SessionProjectChip")!;
            AssertNeutral(FindIcon(projectBtn), "输入栏项目文件夹图标");

            var modelBtn = page.FindControl<Button>("ModelButton")!;
            AssertNeutral(FindIcon(modelBtn), "输入栏模型星芒图标");

            var thinkBtn = page.FindControl<Button>("ThinkingButton")!;
            AssertNeutral(FindIcon(thinkBtn), "输入栏思考大脑图标");

            var stopBtn = page.FindControl<Button>("StopButton")!;
            AssertNeutral(FindIcon(stopBtn), "输入栏停止按钮图标");

            var restartBtn = page.FindControl<Button>("RestartButton")!;
            AssertNeutral(FindIcon(restartBtn), "输入栏重试按钮图标");

            var restoreBorder = page.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("restoreInfo"));
            AssertNeutral(FindIcon(restoreBorder), "待恢复草稿历史图标");

            var modeBtn = page.GetVisualDescendants().OfType<Button>()
                .Single(control => Avalonia.Automation.AutomationProperties.GetAutomationId(control) == "composer-permission");
            AssertNeutral(FindIcon(modeBtn), "输入栏权限模式盾牌图标");

            // 6. 消息操作浮动条（MessageActionsView）
            var actionsViews = page.GetVisualDescendants().OfType<MessageActionsView>().ToArray();
            var asstActions = actionsViews.FirstOrDefault(v => v.DataContext is AssistantMessageViewModel);
            if (asstActions is not null)
            {
                var actionIcons = asstActions.GetVisualDescendants().OfType<PathIcon>().ToArray();
                Check(actionIcons.Length >= 5, $"助手消息操作条至少包含5个动作图标（实测 {actionIcons.Length}）");
                foreach (var icon in actionIcons)
                {
                    var brush = icon.Foreground as ISolidColorBrush;
                    var isMsColor = brush is not null && (brush.Color == MsRed || brush.Color == accent || brush.Color == emphasis);
                    Check(isMsColor, $"动作图标使用主题色、强调色或错误红，不再使用蓝色（实测：{brush?.Color}）");
                }
            }

            var cascade = new AnimatedMenuFlyout();
            var firstGroup = new AnimatedSubmenuItem { Header = "第一组" };
            var secondGroup = new AnimatedSubmenuItem { Header = "第二组" };
            firstGroup.Items.Add(new MenuItem { Header = "第一项" });
            secondGroup.Items.Add(new MenuItem { Header = "第二项" });
            cascade.Items.Add(firstGroup);
            cascade.Items.Add(secondGroup);
            cascade.ShowAt(headerMore);
            await Settle();
            firstGroup.IsSubMenuOpen = true;
            await Settle();
            secondGroup.IsSubMenuOpen = true;
            firstGroup.IsSubMenuOpen = false;
            await Settle();
            Check(secondGroup.IsSubMenuOpen && !firstGroup.IsSubMenuOpen,
                "原生级联菜单切换保持单一展开组");
            Save(main, "cascade-handoff.png");
            cascade.Hide();

            var modeMenu = (MenuFlyout)modeBtn.Flyout!;
            modeMenu.ShowAt(modeBtn);
            await Settle();
            var selectedMode = modeMenu.Items.OfType<MenuItem>().Single(item => item.Classes.Contains("permissionSelected"));
            Check(selectedMode.Header is string modeLabel && !modeLabel.Contains('✓'), "权限选中项不再使用勾字符");
            Check(selectedMode.BorderBrush is ISolidColorBrush { Color: var modeColor } && modeColor == accent,
                "权限选中项使用主题色描边");
            modeMenu.Hide();
            await main.ActionsForTesting.ExecuteAsync("commands");
            await Settle();
            var paletteSearch = main.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "SearchBox" && box.IsEffectivelyVisible);
            paletteSearch.Focus();
            await Settle();
            var underline = paletteSearch.GetVisualDescendants().OfType<Border>().First(border => border.Name == "PART_BorderElement");
            Check(underline.BorderBrush is ISolidColorBrush { Color: var underlineColor } && underlineColor == accent,
                "原生命令面板搜索焦点底线精确匹配主题色");
            Save(main, "palette-search-underline.png");
            await main.ActionsForTesting.ExecuteAsync("commands");
            await Settle();
            Check(!main.FindControl<Grid>("GlobalOverlay")!.IsVisible, "原生窗口再次点击搜索关闭命令面板");

            var toolHeader = toolCard.GetVisualDescendants().OfType<Button>().First(button => button.Classes.Contains("toolGroupHeader"));
            toolHeader.Focus();
            await Settle();
            Check(toolCard.BorderBrush is ISolidColorBrush { Color: var focusColor } && focusColor == accent,
                "工具组键盘焦点轮廓使用主题色");
            Check(toolHeader.FocusAdorner is null, "工具组不叠加偏移的默认白色焦点框");
            Save(main, "tool-keyboard-focus.png");

            // 7. 回到最新：上滑出现，停手约 2.4 秒后自动淡出
            for (var index = 0; index < 40; index++)
                vm.Timeline.Entries.Add(new ErrorEntryViewModel { KindText = "填充", Message = "用于撑高时间线的占位内容。" });
            await Settle();
            var scroller = page.FindControl<ScrollViewer>("TimelineScroller")!;
            var host = page.FindControl<Border>("BackToLatestHost")!;
            scroller.Offset = new Vector(0, 240);
            typeof(ChatPage).GetMethod("OnTimelineWheel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(page, [scroller, null!]);
            await Settle();
            Check(host.Opacity > 0.9, $"上滑时显示“回到最新”（实测 opacity={host.Opacity}）");
            await Task.Delay(2900);
            Check(host.Opacity < 0.1, $"停止滚动后自动隐藏“回到最新”（实测 opacity={host.Opacity}）");

            // 保存真机渲染截图
            Save(main, "chat-colorful-icons.png");
        }
        catch (Exception ex)
        {
            Console.WriteLine("测试执行异常: " + ex);
            failures.Add("异常: " + ex.Message);
        }

        File.WriteAllLines(Path.Combine(evidence, "failures.txt"), failures);
        Console.WriteLine($"Native chat icons checks: {checks}; failures: {failures.Count}; evidence: {evidence}");
        return failures.Count == 0 ? 0 : 1;

        async Task Settle() { await Task.Delay(350); await Dispatcher.Yield(DispatcherPriority.Background); main.UpdateLayout(); }
        void Save(Visual target, string name)
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)target.Bounds.Width), Math.Max(1, (int)target.Bounds.Height)));
            bitmap.Render(target);
            bitmap.Save(Path.Combine(evidence, name), PngBitmapEncoderOptions.Default);
        }
    }
}
