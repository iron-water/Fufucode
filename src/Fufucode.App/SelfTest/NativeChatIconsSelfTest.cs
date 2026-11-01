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
    private static readonly Color MsRed = Color.Parse("#F25022");
    private static readonly Color MsGreen = Color.Parse("#7FBA00");
    private static readonly Color MsBlue = Color.Parse("#00A4EF");
    private static readonly Color MsYellow = Color.Parse("#FFB900");

    internal static async Task<int> RunAsync(MainWindow main)
    {
        var evidence = Path.GetFullPath("tmp/chat-icons-evidence");
        Directory.CreateDirectory(evidence);
        var failures = new List<string>();
        var checks = 0;

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

        // 注入相关文件与变更卡数据
        assistantMsg.Files.Add(new MessageFileViewModel("src/Views/ChatPage.axaml"));
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

            AssertIconColor(FindIcon(headerMore), MsBlue, "标题栏更多操作图标");
            AssertIconColor(FindIcon(headerInfo), MsBlue, "标题栏会话详情图标");

            // 2. 消息头像（用户：蓝，助手：绿）
            var userAvatar = page.GetVisualDescendants().OfType<Border>().First(b => Avalonia.Automation.AutomationProperties.GetAutomationId(b) == "MSG-USER-AVATAR");
            AssertIconColor(FindIcon(userAvatar), MsBlue, "用户头像图标");

            var asstAvatar = page.GetVisualDescendants().OfType<Border>().First(b => Avalonia.Automation.AutomationProperties.GetAutomationId(b) == "MSG-ASSISTANT-AVATAR");
            AssertIconColor(FindIcon(asstAvatar), MsGreen, "AI助手头像图标");

            // 3. 正文块内图标：思考（黄）、工具（蓝）、代码复制（蓝）、相关文件（黄）、变更卡（绿）、跳转打开（蓝）
            var thinkingPanel = page.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("thinkingBlock"));
            var brainIcon = thinkingPanel.GetVisualDescendants().OfType<PathIcon>().First();
            AssertIconColor(brainIcon, MsYellow, "思考过程大脑图标");

            var toolCard = page.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("toolGroupCard"));
            var wrenchIcon = toolCard.GetVisualDescendants().OfType<PathIcon>().First();
            AssertIconColor(wrenchIcon, MsBlue, "工具组扳手图标");
            Check((brainIcon.Foreground as ISolidColorBrush)?.Color != (wrenchIcon.Foreground as ISolidColorBrush)?.Color, "输出中的思考与工具图标颜色不能相同");

            var relatedCard = page.GetVisualDescendants().OfType<Border>().First(b => Avalonia.Automation.AutomationProperties.GetAutomationId(b) == "MSG-RELATED-FILES-CARD");
            AssertIconColor(relatedCard.GetVisualDescendants().OfType<PathIcon>().First(), MsYellow, "相关文件文档图标");

            var changeCard = page.GetVisualDescendants().OfType<Border>().First(b => Avalonia.Automation.AutomationProperties.GetAutomationId(b) == "MSG-TURN-CHANGE-CARD");
            AssertIconColor(changeCard.GetVisualDescendants().OfType<PathIcon>().First(), MsGreen, "代码变更卡编辑图标");
            AssertIconColor(changeCard.GetVisualDescendants().OfType<PathIcon>().Last(), MsBlue, "变更文件打开跳转图标");

            // 4. 错误卡（红圈、红叉）
            var errorCard = page.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("errorCard"));
            var errorIcons = errorCard.GetVisualDescendants().OfType<PathIcon>().ToArray();
            var errorCircle = errorIcons.First(i => i.IsVisible);
            AssertIconColor(errorCircle, MsRed, "错误卡提示红圈");
            var errorDismiss = errorIcons.Last();
            AssertIconColor(errorDismiss, MsRed, "错误卡关闭按钮图标");

            // 5. 底部输入栏按钮图标：更多（蓝）、附件（黄）、项目（黄）、模型（绿）、思考（黄）、停止（红）、恢复草稿（黄）、移除引用（红）
            var composerMore = page.FindControl<Button>("ComposerMoreButton")!;
            AssertIconColor(FindIcon(composerMore), MsBlue, "输入栏附加功能加号图标");

            var attachBtn = page.FindControl<Button>("AttachContextButton")!;
            AssertIconColor(FindIcon(attachBtn), MsYellow, "输入栏附件回形针图标");

            var projectBtn = page.FindControl<Button>("SessionProjectChip")!;
            AssertIconColor(FindIcon(projectBtn), MsYellow, "输入栏项目文件夹图标");

            var modelBtn = page.FindControl<Button>("ModelButton")!;
            AssertIconColor(FindIcon(modelBtn), MsGreen, "输入栏模型星芒图标");

            var thinkBtn = page.FindControl<Button>("ThinkingButton")!;
            AssertIconColor(FindIcon(thinkBtn), MsYellow, "输入栏思考大脑图标");

            var stopBtn = page.FindControl<Button>("StopButton")!;
            AssertIconColor(FindIcon(stopBtn), MsRed, "输入栏停止按钮红圈图标");

            var restartBtn = page.FindControl<Button>("RestartButton")!;
            AssertIconColor(FindIcon(restartBtn), MsYellow, "输入栏重试按钮图标");

            var restoreBorder = page.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("restoreInfo"));
            AssertIconColor(FindIcon(restoreBorder), MsYellow, "待恢复草稿历史图标");

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
                    var isMsColor = brush is not null && (brush.Color == MsRed || brush.Color == MsGreen || brush.Color == MsBlue || brush.Color == MsYellow);
                    Check(isMsColor, $"动作图标用色应在微软四色范围内（实测：{brush?.Color}）");
                }
            }

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
