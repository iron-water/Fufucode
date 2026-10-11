using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Abstractions;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;

namespace Fufucode.App.SelfTest;

/// <summary>
/// Wave5-R4 阶段3：队列条编辑/删除。
/// Core 只暴露整列清空（clear_queue，且仅「停止」路径调用），没有单条删除接口，
/// 因此界面镜像会被下一次 QueueChanged 覆盖——这里断言"越权操作被挡住"而不是假装能删。
/// </summary>
public static class QueueStripSelfTest
{
    public static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool ok, string label)
        {
            if (ok)
            {
                pass("W5-R4 队列条：" + label);
            }
            else
            {
                fail("W5-R4 队列条：" + label);
            }
        }

        var directory = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "tmp", "w5r4"));
        Directory.CreateDirectory(directory);

        var session = new ScriptedChatSession { Activity = SessionActivity.Running };
        var input = new InputViewModel(session);

        session.Raise(new SessionEvent.QueueChanged(3, new[] { "第一条排队", "第二条排队", "第三条排队" }));
        await Task.Delay(30);
        await Dispatcher.Yield(DispatcherPriority.Background);

        Check(input.QueuedTexts.Count == 3 && input.QueuedPreview.Count == 3, "QueueChanged 镜像出三条队列项");
        Check(input.QueuedPreview.All(item => !item.CanEdit && !item.CanDelete),
              "Core 已持有的队列项：编辑/删除均为禁用（界面无权单独改 Pi 队列）");
        var reason = input.QueuedPreview.First().BlockedReason;
        Check(reason.Contains("停止", StringComparison.Ordinal) && reason.Contains("Core", StringComparison.Ordinal),
              "禁用项 tooltip 说明受阻原因并指向今天真正可用的路径");
        Check(input.QueuedPreview.First(item => !item.CanEdit).IsLocallyOwned == false,
              "预览项一律标记为非界面持有");

        // ── 命令的守卫：把 Core 持有的项交给命令，必须什么都不发生 ──
        var before = input.QueuedTexts.ToArray();
        var coreItem = input.QueuedPreview.First();
        input.EditQueuedCommand.Execute(coreItem);
        input.DeleteQueuedCommand.Execute(coreItem);
        await Task.Delay(20);
        Check(input.QueuedTexts.SequenceEqual(before) && input.DraftText.Length == 0,
              "对 Core 持有项执行编辑/删除命令不改动队列、不污染草稿");

        // ── 本地持有项：编辑=回填输入框并移除；删除=计数正确 ──
        input.QueuedTexts.Add("本地待发改写");
        var localEdit = new QueuedItemViewModel("本地待发改写", "排队", IsLocallyOwned: true);
        Check(localEdit.CanEdit && localEdit.CanDelete && localEdit.BlockedReason.Length == 0,
              "界面持有的本地项：按钮可用且不再显示受阻说明");
        input.EditQueuedCommand.Execute(localEdit);
        Check(!input.QueuedTexts.Contains("本地待发改写") && input.DraftText == "本地待发改写",
              "编辑：回填输入框的同时原条从队列移除");

        input.DraftText = string.Empty;
        input.QueuedTexts.Add("本地删除甲");
        input.QueuedTexts.Add("本地删除乙");
        var countBefore = input.QueuedTexts.Count;
        input.DeleteQueuedCommand.Execute(new QueuedItemViewModel("本地删除甲", "排队", IsLocallyOwned: true));
        Check(input.QueuedTexts.Count == countBefore - 1 && !input.QueuedTexts.Contains("本地删除甲") &&
              input.QueuedTexts.Contains("本地删除乙"),
              "删除：仅移除目标条，计数与顺序正确");
        Check(input.DraftText.Length == 0, "删除不回填草稿");

        // 已有草稿时编辑要追加而非覆盖（与 ApplyExternalText 的不覆盖纪律一致）
        input.QueuedTexts.Add("本地追加丙");
        input.DraftText = "已有草稿";
        input.EditQueuedCommand.Execute(new QueuedItemViewModel("本地追加丙", "排队", IsLocallyOwned: true));
        Check(input.DraftText == "已有草稿\n本地追加丙", "编辑在已有草稿时追加，不覆盖用户已输入内容");

        // ── 关键边界：本地移除挡不住 Core 的下一次镜像，这正是不能假装支持单条删除的证据 ──
        input.QueuedTexts.Add("将被覆盖");
        session.Raise(new SessionEvent.QueueChanged(1, new[] { "第一条排队" }));
        await Task.Delay(30);
        await Dispatcher.Yield(DispatcherPriority.Background);
        Check(input.QueuedTexts.SequenceEqual(new[] { "第一条排队" }),
              "下一次 QueueChanged 会按 Core 权威列表整体覆盖镜像（本地改动不持久，故必须禁用而非伪删）");

        // ── 真实 ChatPage：按钮存在且处于禁用态，并截图留证 ──
        var window = new MainWindow();
        window.Show();
        try
        {
            window.FindControl<TransitioningContentControl>("PageHost")!.PageTransition = null;
            await window.NavigateForTestingAsync(AppPage.Chat);
            var vm = (MainViewModel)window.DataContext!;
            vm.Input.QueuedTexts.Add("真实队列条截图用条目");
            vm.Input.QueuedPreview.Add(new QueuedItemViewModel("真实队列条截图用条目", "排队"));
            await Task.Delay(120);
            await Dispatcher.Yield(DispatcherPriority.Background);
            window.UpdateLayout();

            var strip = window.GetVisualDescendants().OfType<ItemsControl>()
                .FirstOrDefault(control => control.DataContext is MainViewModel &&
                                           ReferenceEquals(control.ItemsSource, vm.Input.QueuedPreview));
            Check(strip is not null, "真实 ChatPage 渲染出队列条 ItemsControl");

            var buttons = window.GetVisualDescendants().OfType<Button>()
                .Where(button => button.Content is string label && (label == "编辑" || label == "删除"))
                .ToList();
            Check(buttons.Count == 2, $"队列条每行渲染编辑与删除两个入口（实测 {buttons.Count} 个）");
            // 必须先确认确实找到按钮，否则下面的 All() 会在空集上假通过。
            Check(buttons.Count == 2 && buttons.All(button => !button.IsEnabled),
                  "真实界面中两个按钮对 Core 持有项为禁用态");
            Check(buttons.Count == 2 && buttons.All(button => button.GetValue(ToolTip.TipProperty) is string tip &&
                                                              tip.Contains("停止", StringComparison.Ordinal)),
                  "真实界面按钮带受阻原因 tooltip");

            using var frame = window.CaptureRenderedFrame();
            if (frame is null)
            {
                fail("W5-R4 队列条：无头截图帧缺失");
            }
            else
            {
                frame.Save(Path.Combine(directory, "queue-strip.png"), PngBitmapEncoderOptions.Default);
                Check(File.Exists(Path.Combine(directory, "queue-strip.png")), "队列条截图已落盘 tmp/w5r4/queue-strip.png");
            }
        }
        finally
        {
            window.Close();
            await Task.Delay(60);
        }
    }
}
