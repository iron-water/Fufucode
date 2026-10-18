using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Abstractions;
using Fufucode.App.Infrastructure;
using Fufucode.App.Integration;
using Fufucode.App.Mock;
using Fufucode.App.ViewModels;
using Fufucode.Core;
using AbstractionsTextBlock = Fufucode.App.Abstractions.TextBlock;

namespace Fufucode.App.SelfTest;

/// <summary>
/// 无头自测（--selftest）：不依赖交互式桌面会话，验证任务书验收点。
/// 默认只跑 mock 段（无 Pi 环境保持全绿）；--realpi 追加真实 Pi 集成段（隔离数据目录）。
/// 全部断言结果输出到 stdout 与返回码；证据由人工摘录到 docs/archive/progress/progress-d.md。
/// </summary>
public static class SelfTestRunner
{
    private static readonly List<string> Failures = new();

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
            // 某些环境无法改输出编码，忽略
        }

        Console.WriteLine("== Fufucode 无头自测开始 ==");

        // 设置存储隔离：任何窗口创建前指向临时根，避免读写真实 %LOCALAPPDATA%/Fufucode/settings.json。
        Program.SelfTestDataRoot = Path.Combine(
            Path.GetTempPath(), "fufucode-selftest-" + Guid.NewGuid().ToString("N"));

        if (args.Contains("--automation-only"))
        {
            await RunOnHeadlessUiThreadAsync(() => AutomationControlSelfTest.RunAsync(Pass, Fail));
            Console.WriteLine($"Automation page failures: {Failures.Count}");
            return Failures.Count == 0 ? 0 : 1;
        }

        if (args.Contains("--plugin-reconciliation-only"))
        {
            await RunOnHeadlessUiThreadAsync(() => LocalPluginReconciliationSelfTest.RunAsync(Pass, Fail));
            Console.WriteLine($"Plugin reconciliation failures: {Failures.Count}");
            return Failures.Count == 0 ? 0 : 1;
        }
        if (args.Contains("--local-plugins-only"))
        {
            await RunOnHeadlessUiThreadAsync(() => LocalPluginIntegrationSelfTest.RunAsync(Pass, Fail));
            await RunOnHeadlessUiThreadAsync(() => LocalPluginStartupSelfTest.RunAsync(Pass, Fail));
            await RunOnHeadlessUiThreadAsync(() => LocalPluginReceiptSelfTest.RunAsync(Pass, Fail));
            await RunOnHeadlessUiThreadAsync(() => LocalPluginInFlightSelfTest.RunAsync(Pass, Fail));
            await RunOnHeadlessUiThreadAsync(() => LocalPluginCallbackJournalSelfTest.RunAsync(Pass, Fail));
            await RunOnHeadlessUiThreadAsync(() => LocalPluginReconciliationSelfTest.RunAsync(Pass, Fail));
            Console.WriteLine($"Local plugin integration failures: {Failures.Count}");
            return Failures.Count == 0 ? 0 : 1;
        }
        if (args.Contains("--local-plugins-real-only"))
        {
            await RunOnHeadlessUiThreadAsync(() => LocalPluginRealSelfTest.RunAsync(Pass, Fail));
            Console.WriteLine($"Real plugin shell failures: {Failures.Count}");
            return Failures.Count == 0 ? 0 : 1;
        }
        if (args.Contains("--local-plugins-cancel-real-only"))
        {
            await RunOnHeadlessUiThreadAsync(() => LocalPluginCancellationSelfTest.RunAsync(Pass, Fail));
            Console.WriteLine($"Real plugin cancellation failures: {Failures.Count}");
            return Failures.Count == 0 ? 0 : 1;
        }

        if (args.Contains("--notification-page-only"))
        {
            await RunOnHeadlessUiThreadAsync(() => NotificationPageSelfTest.RunAsync(Pass, Fail));
            Console.WriteLine($"Notification page failures: {Failures.Count}");
            return Failures.Count == 0 ? 0 : 1;
        }

        if (args.Contains("--terminal-integration-only"))
        {
            await RunOnHeadlessUiThreadAsync(() => TerminalIntegrationSelfTest.RunAsync(Pass, Fail));
            Console.WriteLine($"Terminal integration failures: {Failures.Count}");
            return Failures.Count == 0 ? 0 : 1;
        }

        if (args.Contains("--floating-workbench-only"))
        {
            await RunOnHeadlessUiThreadAsync(() => FloatingWorkbenchSelfTest.RunAsync(Pass, Fail));
            Console.WriteLine($"Floating workbench failures: {Failures.Count}");
            return Failures.Count == 0 ? 0 : 1;
        }

        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            Console.WriteLine("  [UI 线程未处理异常] " + args.Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Console.WriteLine("  [未观察任务异常] " + args.Exception);
            args.SetObserved();
        };

        if (args.Contains("--workbench-only"))
        {
            await RunOnHeadlessUiThreadAsync(() => WorkbenchDemoSelfTest.RunAsync(Pass, Fail));
            Console.WriteLine($"Workbench failures: {Failures.Count}");
            return Failures.Count == 0 ? 0 : 1;
        }
        if (args.Contains("--sessions-only"))
        {
            await RunOnHeadlessUiThreadAsync(() => SessionOrganizationSelfTest.RunAsync(Pass, Fail));
            Console.WriteLine($"Session management failures: {Failures.Count}");
            return Failures.Count == 0 ? 0 : 1;
        }
        if (args.Contains("--sessions-realpi-only"))
        {
            await RunOnHeadlessUiThreadAsync(() => SessionManagementRealSelfTest.RunAsync(Pass, Fail));
            Console.WriteLine($"Real session management failures: {Failures.Count}");
            return Failures.Count == 0 ? 0 : 1;
        }
        if (args.Contains("--tabs-only"))
        {
            await RunOnHeadlessUiThreadAsync(() => WorkbenchTabsSelfTest.RunAsync(Pass, Fail));
            Console.WriteLine($"Tabs failures: {Failures.Count}");
            return Failures.Count == 0 ? 0 : 1;
        }
        if (args.Contains("--browser-integration-only"))
        {
            await RunOnHeadlessUiThreadAsync(() => BrowserIntegrationSelfTest.RunAsync(Pass, Fail));
            Console.WriteLine($"Browser integration failures: {Failures.Count}");
            return Failures.Count == 0 ? 0 : 1;
        }
        if (args.Contains("--r05-only"))
        {
            await RunOnHeadlessUiThreadAsync(() => WorkbenchCustomizationSelfTest.RunAsync(Pass, Fail));
            Console.WriteLine($"R05 failures: {Failures.Count}");
            return Failures.Count == 0 ? 0 : 1;
        }
        if (args.Contains("--skins-integration-only"))
        {
            await RunOnHeadlessUiThreadAsync(() => SkinIntegrationSelfTest.RunAsync(Pass, Fail));
            Console.WriteLine($"Skin integration failures: {Failures.Count}");
            return Failures.Count == 0 ? 0 : 1;
        }
        if (args.Contains("--workbuddy-only"))
        {
            // WorkBuddy 皮肤已归档（它是手机遥控配套 App 的设计规范，非桌面皮肤）；
            // 保留该入口避免 CI/脚本 404，但不再有其专属测试。
            Console.WriteLine("WorkBuddy selftest archived: skin removed from catalog.");
            return 0;
        }
        if (args.Contains("--settings-destination-only"))
        {
            await RunOnHeadlessUiThreadAsync(() => SettingsDestinationSelfTest.RunAsync(Pass, Fail));
            Console.WriteLine($"Settings destination failures: {Failures.Count}");
            return Failures.Count == 0 ? 0 : 1;
        }
        if (args.Contains("--shortcuts-only"))
        {
            await RunOnHeadlessUiThreadAsync(() => ShortcutManagerSelfTest.RunAsync(Pass, Fail));
            Console.WriteLine($"Shortcuts failures: {Failures.Count}");
            return Failures.Count == 0 ? 0 : 1;
        }
        if (args.Contains("--conpty-only"))
        {
            try { Pass(await TerminalSelfTest.RunConPtySmokeAsync(Environment.CurrentDirectory)); }
            catch (Exception error) { Fail("真实 ConPTY：" + error); }
            Console.WriteLine($"== 自测结束：{Failures.Count} 项失败 ==");
            return Failures.Count == 0 ? 0 : 1;
        }
        if (args.Contains("--conpty-p0-only"))
        {
            try { Pass(await TerminalSelfTest.RunConPtyP0SmokeAsync(Environment.CurrentDirectory)); }
            catch (Exception error) { Fail("真实 ConPTY P0：" + error); }
            Console.WriteLine($"== 自测结束：{Failures.Count} 项失败 ==");
            return Failures.Count == 0 ? 0 : 1;
        }
        if (args.Contains("--r2-only"))
        {
            await RunWave5R2ChecksAsync(args);
            Console.WriteLine($"== 自测结束：{Failures.Count} 项失败 ==");
            return Failures.Count == 0 ? 0 : 1;
        }
        if (args.Contains("--r3-only"))
        {
            await RunWave5R3ChecksAsync();
            Console.WriteLine($"== 自测结束：{Failures.Count} 项失败 ==");
            return Failures.Count == 0 ? 0 : 1;
        }
        if (args.Contains("--d6-only"))
        {
            await RunWave5D6ChecksAsync();
            Console.WriteLine($"== 自测结束：{Failures.Count} 项失败 ==");
            return Failures.Count == 0 ? 0 : 1;
        }
        if (args.Contains("--r4-only"))
        {
            await RunWave5R4ChecksAsync();
            Console.WriteLine($"== 自测结束：{Failures.Count} 项失败 ==");
            return Failures.Count == 0 ? 0 : 1;
        }

        // 纯逻辑部分不需要 UI 线程
        TestImeGuard();
        TestTextSegmenter();
        TestRebuilderRules();
        TestUsageIsCumulative();

        // VM 部分：在无头 UI 线程上执行
        await RunOnHeadlessUiThreadAsync(TestMockSessionFlowAsync);
        await RunOnHeadlessUiThreadAsync(TestStopClearsQueueAndRestoresDraftAsync);
        await RunOnHeadlessUiThreadAsync(TestStopWindowErrorSeverityAsync);
        await RunOnHeadlessUiThreadAsync(TestQueueIndicationAsync);
        await RunOnHeadlessUiThreadAsync(TestSteerInputAsync);
        await RunOnHeadlessUiThreadAsync(TestUsageOverwriteAsync);
        await RunOnHeadlessUiThreadAsync(TestLongConversationPerformanceAsync);

        // 界面可构建并渲染
        await RunOnHeadlessUiThreadAsync(TestWindowRendersAsync);
        await RunOnHeadlessUiThreadAsync(TestEnterKeyAsync);
        await RunOnHeadlessUiThreadAsync(TestSteerKeyboardAsync);
        await RunOnHeadlessUiThreadAsync(TestPageRoundTripAsync);

        await RunWave5R2ChecksAsync(args);
        await RunWave5R3ChecksAsync();

        // E1 内置浏览器：地址归一化、文件预览分类、数据目录隔离、VM 状态机、控件骨架（无头可断言部分）
        await RunOnHeadlessUiThreadAsync(() =>
        {
            Fufucode.App.Views.BrowserViewSelfTest.Run(Pass, Fail);
            return Task.CompletedTask;
        });

        await RunWave5R4ChecksAsync();

        // D6：窄宽抽屉避让（D-R4-1）+ Files 选中/双击接通内置浏览器预览（截图 tmp/w5d6/）
        await RunWave5D6ChecksAsync();

        await RunOnHeadlessUiThreadAsync(() => WorkbenchDemoSelfTest.RunAsync(Pass, Fail));
        await RunOnHeadlessUiThreadAsync(() => WorkbenchTabsSelfTest.RunAsync(Pass, Fail));
        await RunOnHeadlessUiThreadAsync(() => FloatingWorkbenchSelfTest.RunAsync(Pass, Fail));
        await RunOnHeadlessUiThreadAsync(() => NotificationPageSelfTest.RunAsync(Pass, Fail));
        await RunOnHeadlessUiThreadAsync(() => AutomationControlSelfTest.RunAsync(Pass, Fail));
        await RunOnHeadlessUiThreadAsync(() => TerminalIntegrationSelfTest.RunAsync(Pass, Fail));
        await RunOnHeadlessUiThreadAsync(() => BrowserIntegrationSelfTest.RunAsync(Pass, Fail));
        await RunOnHeadlessUiThreadAsync(() => WorkbenchCustomizationSelfTest.RunAsync(Pass, Fail));

        await RunOnHeadlessUiThreadAsync(() => SkinIntegrationSelfTest.RunAsync(Pass, Fail));

        await RunOnHeadlessUiThreadAsync(() => SessionOrganizationSelfTest.RunAsync(Pass, Fail));

        // O1 快捷键模块：手势解析/保留键/冲突/持久化/损坏回退/编辑恢复/录制器
        await RunOnHeadlessUiThreadAsync(() => ShortcutManagerSelfTest.RunAsync(Pass, Fail));

        // H3 扩展管理页：get_commands 解析、发现扫描、填入规则、安全重启守卫
        await RunOnHeadlessUiThreadAsync(() => LocalPluginIntegrationSelfTest.RunAsync(Pass, Fail));
        await RunOnHeadlessUiThreadAsync(() => LocalPluginStartupSelfTest.RunAsync(Pass, Fail));
        await RunOnHeadlessUiThreadAsync(() => LocalPluginReceiptSelfTest.RunAsync(Pass, Fail));
        await RunOnHeadlessUiThreadAsync(() => LocalPluginInFlightSelfTest.RunAsync(Pass, Fail));
        await RunOnHeadlessUiThreadAsync(() => LocalPluginCallbackJournalSelfTest.RunAsync(Pass, Fail));
        await RunOnHeadlessUiThreadAsync(() => LocalPluginReconciliationSelfTest.RunAsync(Pass, Fail));
        TestExtensionCatalogParsing();
        TestExtensionDiscoveryScan();
        await RunOnHeadlessUiThreadAsync(TestCommandFillRuleAsync);
        await RunOnHeadlessUiThreadAsync(TestRestartGuardAsync);
        await RunOnHeadlessUiThreadAsync(TestRestartFailureFlowAsync);

        // H4 扩展启停：状态徽章/差异、持久化往返、缺失过滤、添加入口校验、启动失败停用并重试
        await RunOnHeadlessUiThreadAsync(TestExtensionManageStatesAsync);
        TestExtensionPreferencesPersistence();
        TestAddExtensionValidation();
        await RunOnHeadlessUiThreadAsync(TestStartupExtensionFailureAsync);
        await RunOnHeadlessUiThreadAsync(TestRemoveExtensionConfirmAsync);

        // W2/M3.1 项目选择：最近项目偏好、启动“选择项目”态、运行中切换在 realpi 段
        TestProjectPreferences();
        await RunOnHeadlessUiThreadAsync(TestProjectPickerAsync);

        // M6/W1-a 设置页：主题持久化与失败保护、models.json 脱敏、诊断摘要、空态插画
        await RunOnHeadlessUiThreadAsync(TestSettingsThemeAsync);
        TestModelsConfigParsing();
        // W2 设置应用链路：代理/端点校验、密钥路径、运行中仅保存、待重启提示、存会合写
        await RunOnHeadlessUiThreadAsync(TestSettingsApplyAsync);
        await RunOnHeadlessUiThreadAsync(TestW1ShotsAsync);

        // H2 视觉基座：Light/Dark 截图证据（tmp/h2-shots/）
        await RunOnHeadlessUiThreadAsync(TestVisualShotsAsync);

        // H3 扩展页截图（tmp/h3-shots/）：夹具快照 light/dark + 命令 Flyout
        await RunOnHeadlessUiThreadAsync(TestH3ShotsAsync);

        // 真实 Pi 集成段：仅 --realpi 显式启用（需要 runtime/node 与 runtime/pi）
        if (args.Contains("--realpi"))
        {
            await RunOnHeadlessUiThreadAsync(TestRealPiIntegrationAsync);
            await RunOnHeadlessUiThreadAsync(TestRealPiExtensionsAsync);
            await RunOnHeadlessUiThreadAsync(TestRealPiProjectSwitchAsync);
        }

        Console.WriteLine();
        Console.WriteLine($"== 自测结束：{Failures.Count} 项失败 ==");
        foreach (var failure in Failures)
        {
            Console.WriteLine("  FAIL: " + failure);
        }

        return Failures.Count == 0 ? 0 : 1;
    }

    private static async Task RunWave5R2ChecksAsync(string[] args)
    {
        await RunOnHeadlessUiThreadAsync(() => WorkspacePanelSelfTest.RunAsync(Pass, Fail));
        await RunOnHeadlessUiThreadAsync(() =>
        {
            TerminalSelfTest.Run((name, ok) => { if (ok) Pass(name); else Fail(name); });
            return TerminalSelfTest.RunScrollIsolationAsync((name, ok) => { if (ok) Pass(name); else Fail(name); });
        });
        await RunOnHeadlessUiThreadAsync(() => Wave5R2SelfTest.RunAsync(Pass, Fail));
        if (args.Contains("--conpty"))
        {
            try { Pass(await TerminalSelfTest.RunConPtySmokeAsync(Environment.CurrentDirectory)); }
            catch (Exception error) { Fail("真实 ConPTY：" + error); }
        }
    }

    private static async Task RunWave5R3ChecksAsync()
    {
        await RunOnHeadlessUiThreadAsync(() => SettingsDestinationSelfTest.RunAsync(Pass, Fail));
        await RunOnHeadlessUiThreadAsync(() => ExtensionsEmptySelfTest.RunAsync(Pass, Fail));
        await RunOnHeadlessUiThreadAsync(() => CrossGlobalSelfTest.RunAsync(Pass, Fail));
        await RunOnHeadlessUiThreadAsync(() => Wave5R3SelfTest.RunAsync(Pass, Fail));
    }

    private static async Task RunWave5R4ChecksAsync()
    {
        await RunOnHeadlessUiThreadAsync(() => MarkdownSelfTest.RunAsync(Pass, Fail));
        await RunOnHeadlessUiThreadAsync(() => QueueStripSelfTest.RunAsync(Pass, Fail));
    }

    private static async Task RunWave5D6ChecksAsync()
    {
        await RunOnHeadlessUiThreadAsync(() => Wave5D6SelfTest.RunAsync(Pass, Fail));
    }

    private static void Fail(string message)
    {
        Failures.Add(message);
        Console.WriteLine("  FAIL: " + message);
    }

    private static void Pass(string message) => Console.WriteLine("  ok: " + message);

    private static string EvidenceDirectory(string group)
    {
        var path = Path.Combine(Program.SelfTestDataRoot!, "evidence", group);
        Directory.CreateDirectory(path);
        return path;
    }

    // ── 输入法守卫状态机（信号=PreeditText 组合状态，假时钟） ──
    private static void TestImeGuard()
    {
        Console.WriteLine("[IME 守卫]");
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var guard = new ImeEnterGuard(() => now);

        // 无组合时回车应放行
        if (guard.ShouldSwallowEnter())
        {
            Fail("无组合时回车被误吞");
        }
        else
        {
            Pass("无组合时回车放行");
        }

        // 组合进行中（preedit 非空）必须吞回车
        guard.OnPreeditChanged("nihao");
        if (!guard.ShouldSwallowEnter())
        {
            Fail("preedit 非空时回车未被吞");
        }
        else
        {
            Pass("preedit 非空时吞回车");
        }

        // 组合刚结束 30ms（非空→空跳变）仍在吞回车窗口内
        guard.OnPreeditChanged(null);
        now += TimeSpan.FromMilliseconds(30);
        if (!guard.ShouldSwallowEnter())
        {
            Fail("组合结束 30ms 内回车未被吞");
        }
        else
        {
            Pass("组合结束 30ms 内仍吞回车");
        }

        // 组合结束 200ms 后恢复放行
        now += TimeSpan.FromMilliseconds(170);
        if (guard.ShouldSwallowEnter())
        {
            Fail("组合结束 200ms 后回车仍被吞");
        }
        else
        {
            Pass("组合结束 200ms 后回车放行");
        }
    }

    // ── 文本分段器：代码围栏拆分与行数 ──
    private static void TestTextSegmenter()
    {
        Console.WriteLine("[文本分段器]");
        var segments = TextSegmenter.Split("前面\n\n```csharp\nvar x = 1;\n```\n\n后面").ToArray();
        if (segments.Length != 3)
        {
            Fail($"应为 3 段（纯文本/代码/纯文本），实际 {segments.Length}");
        }
        else
        {
            if (segments[1].IsCode && segments[1].Language == "csharp" && segments[1].Text == "var x = 1;")
            {
                Pass("代码块正确拆分，语言与内容剥离");
            }
            else
            {
                Fail($"代码段内容错误：lang={segments[1].Language} text={segments[1].Text}");
            }
        }

        if (TextSegmenter.CountLines("a\nb\nc") != 3)
        {
            Fail("行数统计错误");
        }
        else
        {
            Pass("行数统计正确");
        }

        // 未闭合围栏（流式中）不应丢弃内容
        var open = TextSegmenter.Split("文本\n\n```csharp\nvar y = 2;").ToArray();
        if (open.Length != 2 || !open[1].IsCode)
        {
            Fail("未闭合围栏应按代码段保留");
        }
        else
        {
            Pass("未闭合围栏按代码段保留");
        }
    }

    // ── 重建器：分块不混淆、最终消息整体替换不重复 ──
    private static void TestRebuilderRules()
    {
        Console.WriteLine("[消息重建器]");
        var rebuilder = new Streaming.MessageRebuilder();
        const string id = "msg-1";

        rebuilder.Apply(new SessionEvent.MessageStarted(id, MessageRole.Assistant, "M", 3));
        rebuilder.Apply(new SessionEvent.BlockStarted(id, 0, MessageBlockKind.Thinking, null, null));
        rebuilder.Apply(new SessionEvent.ThinkingAppended(id, 0, "想"));
        rebuilder.Apply(new SessionEvent.ThinkingAppended(id, 0, "一想"));
        rebuilder.Apply(new SessionEvent.BlockStarted(id, 1, MessageBlockKind.Text, null, null));
        rebuilder.Apply(new SessionEvent.TextAppended(id, 1, "你"));
        rebuilder.Apply(new SessionEvent.TextAppended(id, 1, "好"));
        rebuilder.Apply(new SessionEvent.BlockStarted(id, 2, MessageBlockKind.ToolCall, "c1", "read"));
        rebuilder.Apply(new SessionEvent.ToolCallAppended(id, 2, "c1", "{\"a\":1"));

        var mid = rebuilder.GetEntry(id);
        var midMessage = mid?.Message;
        if (midMessage is null || midMessage.Blocks.Count != 3)
        {
            Fail($"流式中应有 3 个块，实际 {midMessage?.Blocks.Count ?? -1}");
            return;
        }

        var thinking = midMessage.Blocks[0] as ThinkingBlock;
        var text = midMessage.Blocks[1] as AbstractionsTextBlock;
        var tool = midMessage.Blocks[2] as ToolCallBlock;
        if (thinking?.Text != "想一想" || text?.Text != "你好" || tool?.InputJson != "{\"a\":1")
        {
            Fail($"分块内容混淆：think={thinking?.Text} text={text?.Text} tool={tool?.InputJson}");
            return;
        }
        Pass("文本/思考/工具调用分块互不混淆");

        // 块结束替换临时累积
        rebuilder.Apply(new SessionEvent.BlockFinished(id, 1, new AbstractionsTextBlock("你好。")));
        if ((rebuilder.GetEntry(id)!.Message!.Blocks[1] as AbstractionsTextBlock)!.Text != "你好。")
        {
            Fail("BlockFinished 未替换临时块");
        }
        else
        {
            Pass("BlockFinished 替换临时块");
        }

        // MessageFinished 整体替换：终态消息与流式累积不一致时以终态为准
        var finalBlocks = new MessageBlock[]
        {
            new AbstractionsTextBlock("最终文本"),
            new ToolCallBlock("c1", "read", "{\"a\":1}", ToolCallStatus.Completed, null, "输出", false),
        };
        rebuilder.Apply(new SessionEvent.MessageFinished(id,
            new ChatMessage(id, MessageRole.Assistant, "M", finalBlocks, null, true), null));

        var final = rebuilder.GetEntry(id)!.Message!;
        if (!final.IsComplete || final.Blocks.Count != 2)
        {
            Fail($"终态消息应为整体替换后 2 块，实际 {final.Blocks.Count}");
            return;
        }

        if ((final.Blocks[0] as AbstractionsTextBlock)!.Text != "最终文本")
        {
            Fail("终态消息未以 MessageFinished 的内容整体替换");
        }
        else
        {
            Pass("MessageFinished 整体替换，临时累积不残留");
        }
    }

    // ── usage 累计快照语义（覆盖式，不逐条相加） ──
    private static void TestUsageIsCumulative()
    {
        Console.WriteLine("[用量累计]");
        var rebuilder = new Streaming.MessageRebuilder();
        rebuilder.Apply(new SessionEvent.MessageStarted("a", MessageRole.Assistant, "M", 1));
        rebuilder.Apply(new SessionEvent.MessageFinished("a",
            new ChatMessage("a", MessageRole.Assistant, "M", Array.Empty<MessageBlock>(), new UsageInfo { InputTokens = 100, OutputTokens = 50 }, true),
            new UsageInfo { InputTokens = 100, OutputTokens = 50 }));
        rebuilder.Apply(new SessionEvent.MessageStarted("b", MessageRole.Assistant, "M", 1));
        rebuilder.Apply(new SessionEvent.MessageFinished("b",
            new ChatMessage("b", MessageRole.Assistant, "M", Array.Empty<MessageBlock>(), new UsageInfo { InputTokens = 260, OutputTokens = 130 }, true),
            new UsageInfo { InputTokens = 260, OutputTokens = 130 }));

        var b = rebuilder.GetEntry("b")!.Message!;
        if (b.CumulativeUsage?.InputTokens != 260)
        {
            Fail($"usage 应为累计快照（覆盖式），实际 {b.CumulativeUsage?.InputTokens}");
        }
        else
        {
            Pass("usage 按累计快照覆盖，不逐条相加");
        }
    }

    // ── mock 会话全流程（流式、工具卡片、错误条目） ──
    private static async Task TestMockSessionFlowAsync()
    {
        Console.WriteLine("[mock 会话流程]");
        var clipboard = new RecordingClipboardService();
        var session = new MockChatSession();
        var timeline = new TimelineViewModel(session, SynchronousRefreshScheduler.Instance, clipboard);
        await session.ConnectAsync();

        // 正常回合
        await session.SendAsync("请读一下 README 并总结");
        await WaitForSettledAsync(session);

        var assistant = timeline.Entries.OfType<AssistantMessageViewModel>().LastOrDefault();
        if (assistant is null)
        {
            Fail($"正常回合未产生助手消息（条目数={timeline.Entries.Count}，类型={string.Join(",", timeline.Entries.Select(e => e.GetType().Name))}）");
            return;
        }

        var hasThinking = assistant.Blocks.OfType<ThinkingBlockViewModel>().Any();
        var hasText = assistant.Blocks.OfType<TextBlockViewModel>().Any();
        var tools = assistant.Blocks.OfType<ToolCallBlockViewModel>().ToArray();
        var hasSucceededTool = tools.Any(t => t.Status == ToolCallStatus.Completed);
        var hasFailedTool = tools.Any(t => t.Status == ToolCallStatus.Failed);

        if (!hasThinking) Fail("缺少可折叠思考块");
        else Pass("包含可折叠思考块");
        if (!hasText) Fail("缺少助手文本块");
        else Pass("包含助手文本块");
        if (!hasSucceededTool) Fail("缺少成功的工具卡片");
        else Pass("包含成功的工具卡片");
        if (!hasFailedTool) Fail("缺少失败的工具卡片");
        else Pass("包含失败的工具卡片");

        // 代码块分段
        var textBlock = assistant.Blocks.OfType<TextBlockViewModel>().First();
        if (!textBlock.Segments.OfType<CodeSegmentViewModel>().Any())
        {
            Fail("文本块未拆出代码段");
        }
        else
        {
            Pass("文本块正确拆出代码段");
        }

        // 停止后终态
        if (session.LastOutcome != SessionOutcome.Completed)
        {
            Fail($"正常回合终态应为 Completed，实际 {session.LastOutcome}");
        }
        else
        {
            Pass("正常回合终态 Completed");
        }

        // 失败演示回合
        await session.SendAsync("请失败演示");
        await WaitForSettledAsync(session);

        var hasErrorEntry = timeline.Entries.OfType<ErrorEntryViewModel>().Any();
        if (!hasErrorEntry) Fail("失败演示未产生错误条目");
        else Pass("失败演示产生错误条目");

        if (session.LastOutcome != SessionOutcome.Failed)
        {
            Fail($"失败回合终态应为 Failed，实际 {session.LastOutcome}");
        }
        else
        {
            Pass("失败回合终态 Failed");
        }

        // 未完成消息残留（无 MessageFinished）
        var incomplete = timeline.Entries.OfType<AssistantMessageViewModel>()
            .FirstOrDefault(m => !m.Blocks.Any());
        if (incomplete is null)
        {
            // 至少应存在一条未携带完整文本的残留助手消息
            var lastFailed = timeline.Entries.OfType<AssistantMessageViewModel>().Last();
            var partialText = lastFailed.Blocks.OfType<TextBlockViewModel>().FirstOrDefault();
            if (partialText is null || partialText.Text != "正在回答，但")
            {
                Fail("失败回合的未完成消息未按原样残留");
            }
            else
            {
                Pass("失败回合的未完成消息按原样残留");
            }
        }
    }

    // ── 停止按钮：清空队列并恢复草稿 ──
    private static async Task TestStopClearsQueueAndRestoresDraftAsync()
    {
        Console.WriteLine("[停止与草稿恢复]");
        var session = new MockChatSession();
        var input = new InputViewModel(session);
        await session.ConnectAsync();

        // 先启动一个长回合
        var running = session.SendAsync("请读一下 README 并总结");
        await Task.Delay(100);

        if (session.Activity != SessionActivity.Running)
        {
            Fail($"启动后应为 Running，实际 {session.Activity}");
            return;
        }

        // 排队两条
        await session.SendAsync("排队的第一条");
        await session.SendAsync("排队的第二条");
        if (session.QueuedCount != 2)
        {
            Fail($"队列应有 2 条，实际 {session.QueuedCount}");
            return;
        }
        Pass("运行中提交的消息进入队列");

        // 草稿非空时停止：队列应被清空且草稿不被覆盖，待恢复文本就绪
        input.DraftText = "用户正在编辑的草稿";
        await session.StopAsync();
        await running;

        if (session.QueuedCount != 0)
        {
            Fail($"停止后队列应清空，实际剩余 {session.QueuedCount}");
        }
        else
        {
            Pass("停止后队列被清空");
        }

        if (input.DraftText != "用户正在编辑的草稿")
        {
            Fail("停止时已有草稿被覆盖");
        }
        else
        {
            Pass("停止时已有草稿未被覆盖");
        }

        if (!input.HasPendingRestore)
        {
            Fail("队列清空后应产生待恢复草稿");
        }
        else
        {
            Pass("队列清空后产生待恢复草稿（不覆盖已有草稿）");
        }

        // 应用待恢复草稿：追加到现有草稿后
        input.ApplyPendingRestoreCommand.Execute(null);
        if (!input.DraftText.Contains("排队的第一条") || !input.DraftText.Contains("用户正在编辑的草稿"))
        {
            Fail($"应用待恢复草稿后内容错误：{input.DraftText}");
        }
        else
        {
            Pass("待恢复草稿可追回，且保留原有草稿");
        }

        // 草稿为空时停止：直接恢复
        input.DraftText = string.Empty;
        var secondRunning = session.SendAsync("再次开始");
        await Task.Delay(80);
        await session.SendAsync("应被恢复的一条");
        await session.StopAsync();
        await secondRunning;

        if (input.DraftText != "应被恢复的一条")
        {
            Fail($"草稿为空时应直接恢复队首文本，实际：{input.DraftText}");
        }
        else
        {
            Pass("草稿为空时停止后直接恢复队首文本");
        }

        if (session.LastOutcome != SessionOutcome.Interrupted)
        {
            Fail($"停止终态应为 Interrupted，实际 {session.LastOutcome}");
        }
        else
        {
            Pass("停止终态 Interrupted");
        }
    }

    // ── ≥500 条长会话性能 ──
    private static async Task TestLongConversationPerformanceAsync()
    {
        Console.WriteLine("[长会话性能]");
        var session = new MockChatSession();
        var clipboard = new RecordingClipboardService();
        var timeline = new TimelineViewModel(session, SynchronousRefreshScheduler.Instance, clipboard);
        await session.ConnectAsync();

        var stopwatch = Stopwatch.StartNew();
        await session.GenerateLongConversationAsync(500);
        stopwatch.Stop();

        if (timeline.Entries.Count < 1000)
        {
            Fail($"500 条消息应产生约 1000 个条目（含用户消息），实际 {timeline.Entries.Count}");
            return;
        }
        Pass($"500 条消息 → {timeline.Entries.Count} 个时间线条目");

        // 虚拟化面板下应只实例化可见容器
        var containers = CountRealizedContainers(timeline);
        Console.WriteLine($"  信息：同步调度下条目 VM 数量={timeline.Entries.Count}");

        if (stopwatch.Elapsed.TotalSeconds > 30)
        {
            Fail($"500 条消息生成耗时 {stopwatch.Elapsed.TotalSeconds:F2}s，超过 30s 预算");
        }
        else
        {
            Pass($"500 条消息生成耗时 {stopwatch.Elapsed.TotalSeconds:F2}s（含每 8 条一次让出）");
        }

        // 首尾条目可访问（验证列表未损坏）
        var first = timeline.Entries[0];
        var last = timeline.Entries[^1];
        if (first is null || last is null)
        {
            Fail("长会话首尾条目访问失败");
        }
        else
        {
            Pass("长会话首尾条目可访问");
        }
    }

    private static int CountRealizedContainers(TimelineViewModel timeline) => timeline.Entries.Count;

    // ── F05：停止窗口内的中止错误按"已中断"中性条目展示 ──
    private static async Task TestStopWindowErrorSeverityAsync()
    {
        Console.WriteLine("[停止窗口错误归一]");
        var session = new ScriptedChatSession();
        var timeline = new TimelineViewModel(session, SynchronousRefreshScheduler.Instance,
            new RecordingClipboardService());

        // 停止窗口：Cancelling → Unknown("Request aborted") 改写为已中断（中性）
        session.Activity = SessionActivity.Cancelling;
        session.Raise(new SessionEvent.ActivityChanged(SessionActivity.Cancelling));
        session.Raise(new SessionEvent.SessionError(SessionErrorKind.Unknown, "Request aborted", null));
        await DrainUiQueueAsync();

        var aborted = timeline.Entries.OfType<ErrorEntryViewModel>().LastOrDefault();
        if (aborted is { KindText: "已中断" } && aborted.IsNeutral)
        {
            Pass("停止窗口内 Unknown(\"Request aborted\") → 已中断（中性）");
        }
        else
        {
            Fail($"停止窗口内错误未归一：kind={aborted?.KindText} severity={aborted?.Severity}");
        }

        // 中止收尾期间的瞬态 Running（实测序列 Cancelling→Running→err→Idle）不关窗
        session.Activity = SessionActivity.Running;
        session.Raise(new SessionEvent.ActivityChanged(SessionActivity.Running));
        session.Raise(new SessionEvent.SessionError(SessionErrorKind.Unknown, "Request aborted", null));
        await DrainUiQueueAsync();

        var midUnwind = timeline.Entries.OfType<ErrorEntryViewModel>().LastOrDefault();
        if (midUnwind is { KindText: "已中断" } && midUnwind.IsNeutral)
        {
            Pass("收尾瞬态 Running 期间的 Unknown 中止仍归一为已中断");
        }
        else
        {
            Fail($"收尾瞬态错误未归一：kind={midUnwind?.KindText} severity={midUnwind?.Severity}");
        }

        // 窗口在 TurnSettled 关闭；此后 Unknown 仍是真错误
        session.Activity = SessionActivity.Idle;
        session.Raise(new SessionEvent.ActivityChanged(SessionActivity.Idle));
        session.Raise(new SessionEvent.TurnSettled(SessionOutcome.Interrupted, "已停止"));
        session.Raise(new SessionEvent.SessionError(SessionErrorKind.Unknown, "boom", null));
        await DrainUiQueueAsync();

        var boom = timeline.Entries.OfType<ErrorEntryViewModel>().LastOrDefault();
        if (boom is { KindText: "未知错误" } && boom.IsError)
        {
            Pass("停止窗口外 Unknown 仍为错误级条目");
        }
        else
        {
            Fail($"窗口外 Unknown 被误归一：kind={boom?.KindText} severity={boom?.Severity}");
        }

        // 窗口外收到 UserCancelled（Core 修复后直发）：仍为中性
        session.Raise(new SessionEvent.SessionError(SessionErrorKind.UserCancelled, "cancelled", null));
        await DrainUiQueueAsync();

        var cancelled = timeline.Entries.OfType<ErrorEntryViewModel>().LastOrDefault();
        if (cancelled is { KindText: "已中断" } && cancelled.IsNeutral)
        {
            Pass("窗口外 UserCancelled 也为中性条目");
        }
        else
        {
            Fail($"UserCancelled 展示异常：kind={cancelled?.KindText} severity={cancelled?.Severity}");
        }
    }

    // ── F04：运行中发送显示排队 ──
    private static async Task TestQueueIndicationAsync()
    {
        Console.WriteLine("[排队提示]");
        var session = new ScriptedChatSession();
        var input = new InputViewModel(session);

        session.Activity = SessionActivity.Running;
        session.Raise(new SessionEvent.ActivityChanged(SessionActivity.Running));
        await DrainUiQueueAsync();

        if (input.SendButtonText != "排队" || !input.IsTurnActive)
        {
            Fail($"运行中按钮应为「排队」，实际 \"{input.SendButtonText}\"");
            return;
        }
        Pass("回合活跃时发送按钮显示「排队」");

        input.DraftText = "排队的任务一";
        input.SendCommand.Execute(null);
        await DrainUiQueueAsync();

        if (input.HasQueue && input.QueuedTexts.Contains("排队的任务一"))
        {
            Pass("运行中发送进入队列并显示排队条");
        }
        else
        {
            Fail($"排队内容未显示：count={input.QueuedTexts.Count}");
        }

        if (session.Sent.Contains("排队的任务一"))
        {
            Pass("排队发送仍被会话接受（FollowUp 语义）");
        }
        else
        {
            Fail("排队发送未到达会话层");
        }

        // 队列耗尽 + 回合收敛 → 提示消失、按钮复原
        session.DrainQueue();
        session.Activity = SessionActivity.Idle;
        session.Raise(new SessionEvent.ActivityChanged(SessionActivity.Idle));
        session.Raise(new SessionEvent.TurnSettled(SessionOutcome.Interrupted, "已停止"));
        await DrainUiQueueAsync();

        if (!input.HasQueue && input.SendButtonText == "发送")
        {
            Pass("队列耗尽后提示撤下、按钮恢复「发送」");
        }
        else
        {
            Fail($"回合收敛后状态异常：HasQueue={input.HasQueue} 按钮=\"{input.SendButtonText}\"");
        }
    }

    // ── P2-17：运行中插入（steer）——命令门控、草稿回填、排队条徽章 ──
    private sealed class StubSteering : IPromptSteering
    {
        public bool CanSteer { get; set; } = true;
        public bool FailNext { get; set; }
        public List<string> Steered { get; } = new();

        public Task SteerAsync(string text, CancellationToken cancellationToken = default)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("stub steer 拒绝");
            }

            Steered.Add(text);
            return Task.CompletedTask;
        }
    }

    private static async Task TestSteerInputAsync()
    {
        Console.WriteLine("[运行中插入 steer]");
        var session = new ScriptedChatSession();
        var input = new InputViewModel(session) { Steering = new StubSteering() };
        var steering = (StubSteering)input.Steering;
        string? failure = null;
        input.OperationFailed += message => failure = message;

        // 空闲时 steer 不可用（由 IsTurnActive 门控，与 Steering.CanSteer 无关）
        input.DraftText = "空闲时不可插入";
        if (input.CanSteer || input.SteerCommand.CanExecute(null))
        {
            Fail("空闲时 SteerCommand 应不可用");
            return;
        }
        Pass("空闲时插入入口禁用");

        session.Activity = SessionActivity.Running;
        session.Raise(new SessionEvent.ActivityChanged(SessionActivity.Running));
        await DrainUiQueueAsync();

        // / 开头（非 /skill:）的草稿禁用 steer（扩展命令立即执行，steer 不接受）
        input.DraftText = "/demo 参数";
        if (input.CanSteer)
        {
            Fail("扩展命令形状草稿应禁用 steer");
        }
        else
        {
            Pass("草稿为 / 扩展命令形状时插入入口禁用");
        }

        input.DraftText = "/skill:review";
        if (input.CanSteer)
        {
            Pass("/skill: 前缀不受扩展命令保守判定影响");
        }
        else
        {
            Fail("/skill: 草稿被误禁用 steer");
        }

        // 正常 steer：替身收到文本、草稿清空、排队条徽章为「插入」
        input.DraftText = "插入这一条";
        input.SteerCommand.Execute(null);
        await DrainUiQueueAsync();
        // ScriptedChatSession 不会因 steer 产生 QueueChanged（steer 走旁路），
        // 用 QueueChanged 模拟 coordinator 回报：先让它出现在队列里。
        session.SimulateQueuedTexts("插入这一条", "普通排队项");
        await DrainUiQueueAsync();

        if (steering.Steered.Contains("插入这一条") && string.IsNullOrEmpty(input.DraftText))
        {
            Pass("steer 命令投递到 Steering 并清空草稿");
        }
        else
        {
            Fail($"steer 未投递或草稿未清：steered={string.Join(",", steering.Steered)} draft=\"{input.DraftText}\"");
        }

        var badges = input.QueuedPreview.Select(item => $"{item.Text}:{item.BadgeText}").ToArray();
        if (badges.Length == 2 && badges[0] == "插入这一条:插入" && badges[1] == "普通排队项:排队")
        {
            Pass("排队条徽章：steer 项标「插入」、followUp 项标「排队」");
        }
        else
        {
            Fail("排队条徽章异常：" + string.Join(" | ", badges));
        }

        // 项离开队列后 steer 记录清理（重新入队同文本不再标「插入」）
        session.SimulateQueuedTexts("插入这一条");
        await DrainUiQueueAsync();
        if (input.QueuedPreview.Count == 1 && input.QueuedPreview[0].BadgeText == "插入")
        {
            session.DrainQueue();
            await DrainUiQueueAsync();
            session.SimulateQueuedTexts("插入这一条");
            await DrainUiQueueAsync();
            if (input.QueuedPreview.Count == 1 && input.QueuedPreview[0].BadgeText == "排队")
            {
                Pass("steer 项出队后记录清理（同文本再入队标「排队」）");
            }
            else
            {
                Fail("出队后 steer 记录未清理：" + input.QueuedPreview[0].BadgeText);
            }
        }
        else
        {
            Fail("同文本仍在队列时徽章异常");
        }

        // steer 失败：回填草稿 + OperationFailed
        steering.FailNext = true;
        input.DraftText = "会被拒绝的插入";
        input.SteerCommand.Execute(null);
        await DrainUiQueueAsync();
        if (input.DraftText == "会被拒绝的插入" && failure is not null && failure.Contains("插入失败"))
        {
            Pass("steer 失败回填草稿并报 OperationFailed");
        }
        else
        {
            Fail($"steer 失败处理异常：draft=\"{input.DraftText}\" failure=\"{failure}\"");
        }

        // Steering.CanSteer=false 时入口禁用
        steering.CanSteer = false;
        if (input.CanSteer || input.SteerCommand.CanExecute(null))
        {
            Fail("Steering.CanSteer=false 时插入入口仍可用");
        }
        else
        {
            Pass("Steering.CanSteer=false 时插入入口禁用");
        }

        // mock 会话端到端：SteerAsync 入队 + QueueChanged
        var mock = new MockChatSession();
        var mockInput = new InputViewModel(mock) { Steering = mock };
        await mock.ConnectAsync();
        mock.SimulateRunningQueue(Array.Empty<string>());
        await DrainUiQueueAsync();
        mockInput.DraftText = "mock 插入文本";
        mockInput.SteerCommand.Execute(null);
        await DrainUiQueueAsync();
        if (mock.QueuedCount == 1 && mockInput.QueuedTexts.Contains("mock 插入文本")
            && mockInput.QueuedPreview.Count == 1 && mockInput.QueuedPreview[0].IsSteer)
        {
            Pass("mock：steer 文本进入队列并标「插入」徽章");
        }
        else
        {
            Fail($"mock steer 异常：queue={mock.QueuedCount} badges={string.Join("|", mockInput.QueuedPreview.Select(i => i.BadgeText))}");
        }

        await mock.StopAsync();
    }

    // ── P2-17：Ctrl+Enter 键盘路径（运行中 steer / 空闲 send / 组合中不触发） ──
    private static async Task TestSteerKeyboardAsync()
    {
        Console.WriteLine("[Ctrl+Enter 插入]");
        var window = new MainWindow();
        window.Show();
        await Task.Delay(600);

        var vm = (MainViewModel)window.DataContext!;
        var mock = (MockChatSession)vm.Session;
        var inputBox = window.GetVisualDescendants().OfType<TextBox>()
            .FirstOrDefault(box => box.Name == "InputBox");
        if (inputBox is null)
        {
            Fail("找不到输入框 InputBox（Ctrl+Enter 测试）");
            window.Close();
            return;
        }

        inputBox.Focus();
        await Task.Delay(100);

        // 空闲时 Ctrl+Enter = 普通发送（Immediate）
        window.KeyTextInput("空闲 Ctrl 发送");
        window.KeyPress(Key.Enter, RawInputModifiers.Control, PhysicalKey.Enter, null);
        await Task.Delay(150);
        await DrainUiQueueAsync();
        if (vm.Timeline.Entries.OfType<UserMessageViewModel>().LastOrDefault()?.Text.Contains("空闲 Ctrl 发送") == true)
        {
            Pass("空闲 Ctrl+Enter 走普通发送");
        }
        else
        {
            Fail("空闲 Ctrl+Enter 未发送");
        }

        await WaitForSettledAsync(vm.Session);

        // 运行中 Ctrl+Enter = steer（mock 的 SteerAsync 入队）；Enter = 排队（SendAsync）
        mock.SimulateRunningQueue(Array.Empty<string>());
        await DrainUiQueueAsync();

        window.KeyTextInput("ctrl 插入文本");
        window.KeyPress(Key.Enter, RawInputModifiers.Control, PhysicalKey.Enter, null);
        await Task.Delay(150);
        await DrainUiQueueAsync();
        var steerBadge = vm.Input.QueuedPreview.FirstOrDefault(i => i.Text == "ctrl 插入文本");
        if (steerBadge is { IsSteer: true })
        {
            Pass("运行中 Ctrl+Enter 走 steer（徽章「插入」）");
        }
        else
        {
            Fail($"运行中 Ctrl+Enter 未走 steer：badge={steerBadge?.BadgeText} queue={mock.QueuedCount}");
        }

        window.KeyTextInput("普通排队文本");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Task.Delay(150);
        await DrainUiQueueAsync();
        var followBadge = vm.Input.QueuedPreview.FirstOrDefault(i => i.Text == "普通排队文本");
        if (followBadge is { IsSteer: false } && followBadge.BadgeText == "排队")
        {
            Pass("运行中 Enter 走排队（徽章「排队」）");
        }
        else
        {
            Fail($"运行中 Enter 徽章异常：badge={followBadge?.BadgeText}");
        }

        // 组合中 Ctrl+Enter 不触发 steer
        var presenter = inputBox.GetVisualDescendants()
            .OfType<Avalonia.Controls.Presenters.TextPresenter>().FirstOrDefault();
        if (presenter is null)
        {
            Console.WriteLine("  信息：未找到 TextPresenter，跳过 Ctrl+Enter 组合断言");
        }
        else
        {
            var queuedBefore = mock.QueuedCount;
            try
            {
                presenter.SetValue(Avalonia.Controls.Presenters.TextPresenter.PreeditTextProperty, "nihao");
                window.KeyTextInput("组合中草稿");
                window.KeyPress(Key.Enter, RawInputModifiers.Control, PhysicalKey.Enter, null);
                await Task.Delay(150);
                await DrainUiQueueAsync();
                if (mock.QueuedCount == queuedBefore)
                {
                    Pass("组合中 Ctrl+Enter 不触发 steer");
                }
                else
                {
                    Fail("组合中 Ctrl+Enter 被误投递");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  信息：PreeditText 不可写入（" + ex.GetType().Name + "），跳过组合断言");
            }
            finally
            {
                presenter.SetValue(Avalonia.Controls.Presenters.TextPresenter.PreeditTextProperty, null);
            }
        }

        await mock.StopAsync();
        window.Close();
    }

    private static Task TestUsageOverwriteAsync()
    {
        Console.WriteLine("[usage 覆盖]");
        var session = new ScriptedChatSession();
        var statusBar = new StatusBarViewModel(session);

        session.Raise(new SessionEvent.MessageFinished("a1",
            new ChatMessage("a1", MessageRole.Assistant, "M", Array.Empty<MessageBlock>(), null, true),
            new UsageInfo { InputTokens = 10, OutputTokens = 5 }));
        if (statusBar.UsageText.Contains("10"))
        {
            Pass("助手 usage 显示");
        }
        else
        {
            Fail($"助手 usage 未显示：\"{statusBar.UsageText}\"");
        }

        session.Raise(new SessionEvent.MessageFinished("u1",
            new ChatMessage("u1", MessageRole.User, null, Array.Empty<MessageBlock>(), null, true), null));
        if (statusBar.UsageText.Contains("10"))
        {
            Pass("用户 message_end 不触碰 usage");
        }
        else
        {
            Fail($"用户 message_end 误清 usage：\"{statusBar.UsageText}\"");
        }

        session.Raise(new SessionEvent.MessageFinished("a2",
            new ChatMessage("a2", MessageRole.Assistant, "M", Array.Empty<MessageBlock>(), null, true), null));
        if (statusBar.UsageText.Length == 0)
        {
            Pass("助手无 usage 时清空（未知），不残留旧值");
        }
        else
        {
            Fail($"助手无 usage 时残留旧值：\"{statusBar.UsageText}\"");
        }

        return Task.CompletedTask;
    }

    // ── F06：Enter 发送 / Shift+Enter 换行 / 中段插入后 Enter 仍发送 / 组合中 Enter 不发送 ──
    private static async Task TestEnterKeyAsync()
    {
        Console.WriteLine("[回车发送]");
        var window = new MainWindow();
        window.Show();
        await Task.Delay(600);

        var vm = (MainViewModel)window.DataContext!;
        // InputBox 位于 ChatPage 的 namescope 内，经可视树按 Name 查找。
        var inputBox = window.GetVisualDescendants().OfType<TextBox>()
            .FirstOrDefault(box => box.Name == "InputBox");
        if (inputBox is null)
        {
            Fail("找不到输入框 InputBox");
            window.Close();
            return;
        }

        inputBox.Focus();
        await Task.Delay(100);

        // Enter 发送
        window.KeyTextInput("你好");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Task.Delay(150);
        await DrainUiQueueAsync();

        var userEntry = vm.Timeline.Entries.OfType<UserMessageViewModel>().LastOrDefault();
        if (userEntry is not null && userEntry.Text.Contains("你好")
            && string.IsNullOrEmpty(vm.Input.DraftText))
        {
            Pass("Enter 发送草稿，时间线出现用户消息");
        }
        else
        {
            Fail($"Enter 未发送：draft=\"{vm.Input.DraftText}\" 用户条目=\"{userEntry?.Text}\"");
        }

        await WaitForSettledAsync(vm.Session);

        // Shift+Enter 插入换行，不发送
        var userCountBefore = vm.Timeline.Entries.OfType<UserMessageViewModel>().Count();
        window.KeyTextInput("第一行");
        window.KeyPress(Key.Enter, RawInputModifiers.Shift, PhysicalKey.Enter, null);
        await Task.Delay(100);
        await DrainUiQueueAsync();

        var draftAfterShift = vm.Input.DraftText;
        var userCountAfter = vm.Timeline.Entries.OfType<UserMessageViewModel>().Count();
        if (draftAfterShift.Contains('\n') && userCountAfter == userCountBefore)
        {
            Pass("Shift+Enter 插入换行且不发送");
        }
        else
        {
            Fail($"Shift+Enter 行为异常：draft=\"{draftAfterShift}\" 用户条目 {userCountBefore}→{userCountAfter}");
        }

        // 中段插入（光标不在末尾）后 Enter 仍发送（F06 回归：旧文本形状启发式会误吞）
        window.KeyTextInput("二");
        inputBox.CaretIndex = 0;
        window.KeyTextInput("头");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Task.Delay(150);
        await DrainUiQueueAsync();

        var midEntry = vm.Timeline.Entries.OfType<UserMessageViewModel>().LastOrDefault();
        if (midEntry is not null && midEntry.Text.Contains('头') && userCountBefore < vm.Timeline.Entries.OfType<UserMessageViewModel>().Count())
        {
            Pass("中段插入后 Enter 正常发送（无文本形状误吞）");
        }
        else
        {
            Fail($"中段插入后 Enter 未发送：draft=\"{vm.Input.DraftText}\"");
        }

        await WaitForSettledAsync(vm.Session);

        // 组合中（preedit 非空）Enter 不发送——若 PreeditText 可写
        var presenter = inputBox.GetVisualDescendants()
            .OfType<Avalonia.Controls.Presenters.TextPresenter>().FirstOrDefault();
        if (presenter is null)
        {
            Console.WriteLine("  信息：未找到 TextPresenter，跳过 preedit 断言");
        }
        else
        {
            try
            {
                presenter.SetValue(Avalonia.Controls.Presenters.TextPresenter.PreeditTextProperty, "nihao");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  信息：PreeditText 不可写入（" + ex.GetType().Name + "），跳过组合断言");
                presenter = null;
            }
        }

        if (presenter is not null)
        {
            var countBeforePreedit = vm.Timeline.Entries.OfType<UserMessageViewModel>().Count();
            window.KeyTextInput("abc");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await Task.Delay(100);
            await DrainUiQueueAsync();

            if (vm.Timeline.Entries.OfType<UserMessageViewModel>().Count() == countBeforePreedit)
            {
                Pass("preedit 非空（组合中）Enter 不发送");
            }
            else
            {
                Fail("组合中 Enter 被误发送");
            }

            presenter.SetValue(Avalonia.Controls.Presenters.TextPresenter.PreeditTextProperty, null);
        }

        window.Close();
    }

    // ── 页面往返：Chat → Extensions → Chat 后草稿保留、Enter 仍可发送且只发一次 ──
    private static async Task TestPageRoundTripAsync()
    {
        Console.WriteLine("[页面往返]");
        var window = new MainWindow();
        window.Show();
        await Task.Delay(600);

        var vm = (MainViewModel)window.DataContext!;

        // 等 mock 回合空闲后发一条，再写草稿
        await WaitForSettledAsync(vm.Session);
        var inputBox = window.GetVisualDescendants().OfType<TextBox>()
            .FirstOrDefault(box => box.Name == "InputBox");
        if (inputBox is null)
        {
            Fail("往返测试：找不到 InputBox");
            window.Close();
            return;
        }

        inputBox.Focus();
        window.KeyTextInput("往返前的草稿");
        await Task.Delay(100);

        vm.CurrentPage = AppPage.Extensions;
        await Task.Delay(400);
        vm.CurrentPage = AppPage.Chat;
        await Task.Delay(400);
        await DrainUiQueueAsync();

        var inputBoxAfter = window.GetVisualDescendants().OfType<TextBox>()
            .FirstOrDefault(box => box.Name == "InputBox");
        if (inputBoxAfter is null)
        {
            Fail("往返后找不到 InputBox");
            window.Close();
            return;
        }

        if (vm.Input.DraftText.Contains("往返前的草稿") &&
            inputBoxAfter.Text?.Contains("往返前的草稿") == true)
        {
            Pass("页面往返后草稿保留（视图与 VM 均一致）");
        }
        else
        {
            Fail($"往返后草稿丢失：draft=\"{vm.Input.DraftText}\" box=\"{inputBoxAfter.Text}\"");
        }

        var userCountBefore = vm.Timeline.Entries.OfType<UserMessageViewModel>().Count();
        inputBoxAfter.Focus();
        await Task.Delay(80);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Task.Delay(200);
        await DrainUiQueueAsync();

        var delta = vm.Timeline.Entries.OfType<UserMessageViewModel>().Count() - userCountBefore;
        if (delta == 1 && string.IsNullOrEmpty(vm.Input.DraftText))
        {
            Pass("往返后 Enter 发送且仅发送一次（Tunnel 接线未重复）");
        }
        else
        {
            Fail($"往返后 Enter 发送异常：新增用户消息 {delta} 条，draft=\"{vm.Input.DraftText}\"");
        }

        await WaitForSettledAsync(vm.Session);
        window.Close();
    }

    // ── 主窗口可构建并渲染为位图 ──
    private static async Task TestWindowRendersAsync()
    {
        Console.WriteLine("[界面渲染]");
        try
        {
            var window = new MainWindow();
            window.Show();

            // 等待初始连接与首次渲染
            await Task.Delay(800);

            var frame = window.CaptureRenderedFrame();
            if (frame is null)
            {
                Fail("无头模式未捕获到渲染帧");
                window.Close();
                return;
            }

            var path = Path.Combine(EvidenceDirectory("window"), "selftest-window.png");
            frame.Save(path, PngBitmapEncoderOptions.Default);
            var size = new FileInfo(path).Length;
            Console.WriteLine($"  信息：渲染位图 {path}（{size} 字节，{frame.Size.Width}x{frame.Size.Height}）");

            if (size < 5000)
            {
                Fail("渲染位图过小，疑似空白窗口");
            }
            else
            {
                Pass("主窗口在无头模式可构建并渲染");
            }

            window.Close();
        }
        catch (Exception ex)
        {
            Fail("窗口渲染异常：" + ex.GetType().Name + " " + ex.Message);
        }
    }

    // ── 真实 Pi 集成（--realpi）：连接与桥接探针、真实查询经真实 UI 显示、
    //    无凭据提交的终态收敛、停止链路证据、关闭重开的会话恢复。
    //    全程使用隔离的临时应用数据目录与临时项目目录，不触碰用户 LocalApplicationData。 ──
    private static async Task TestRealPiIntegrationAsync()
    {
        Console.WriteLine("[真实 Pi 集成]");
        string node;
        string cli;
        try
        {
            (node, cli) = RealSessionBootstrap.ResolveRuntime(
                new AppOptions(AppSessionMode.Real, null, null, null, null));
        }
        catch (FileNotFoundException ex)
        {
            Console.WriteLine("  SKIP: " + ex.Message);
            return;
        }

        // W1-a 关于页：真实运行时下 Pi/Node 版本探测（不启动 node 进程）。
        var piVersion = DiagnosticsSummary.ResolvePiVersion(cli);
        var nodeVersion = DiagnosticsSummary.ResolveNodeVersion(node);
        if (piVersion == "0.87.1" && nodeVersion == "24.15.0")
        {
            Pass($"关于页版本探测：Pi={piVersion} Node={nodeVersion}");
        }
        else
        {
            Fail($"关于页版本探测不符：Pi={piVersion} Node={nodeVersion}");
        }

        var root = Path.Combine(Path.GetTempPath(), "fufucode-realpi-" + Guid.NewGuid().ToString("N")[..8]);
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        var layout = new AppDataLayout(Path.Combine(root, "appdata"));
        var manager = new PersistentSessionManager(node, cli, layout);

        try
        {
            var session = await manager.CreateAsync(project);
            var chat = session.Chat;

            if (chat.Connection == SessionConnection.Connected)
            {
                Pass("真实 Pi 子进程已连接（get_state 有效响应）");
            }
            else
            {
                Fail($"真实 Pi 未连接：{chat.Connection}");
                return;
            }

            if (chat is IExtensionUiSession extensionUi)
            {
                if (extensionUi.ApprovalAvailability == ApprovalAvailability.Verified)
                {
                    Pass("审批桥接探针 Verified（fufucode-bridge.status 已注册）");
                }
                else
                {
                    Fail("审批桥接未验证：" + extensionUi.ApprovalDiagnostic);
                }
            }
            else
            {
                Fail("真实会话未实现 IExtensionUiSession");
            }

            // 真实查询经真实 UI（状态栏视图模型）显示
            var statusBar = new StatusBarViewModel(chat);
            await DrainUiQueueAsync();
            if (statusBar.ConnectionText == "已连接")
            {
                Pass("状态栏显示真实连接状态");
            }
            else
            {
                Fail($"状态栏连接文本异常：{statusBar.ConnectionText}");
            }
            Console.WriteLine($"  信息：模型文本=\"{statusBar.ModelText}\" 审批文本=\"{statusBar.ApprovalText}\"");

            // 时间线接真实会话；新会话历史为空
            var clipboard = new RecordingClipboardService();
            var timeline = new TimelineViewModel(chat, SynchronousRefreshScheduler.Instance, clipboard);
            timeline.LoadHistory(HistoryMapper.MapTranscript(session.History.CurrentBranchMessages, chat.CurrentModel));
            if (timeline.Entries.Count == 0)
            {
                Pass("新会话历史为空，时间线初始无条目");
            }
            else
            {
                Fail($"新会话时间线应为空，实际 {timeline.Entries.Count} 条");
            }

            // 无凭据提交：要么被拒绝（显式报告+草稿回填），要么运行收敛到失败终态；
            // 两条路径都不得留下永久"处理中"（计划 M2 完成条件）。
            var input = new InputViewModel(chat);
            SessionUiWiring.AttachExtensionUi(chat, timeline, input, statusBar);
            string? sendFailure = null;
            input.OperationFailed += message => sendFailure = message;
            input.DraftText = "你好";
            input.SendCommand.Execute(null);

            var reachedTerminal = await WaitForRealTerminalAsync(chat, () => sendFailure is not null);
            if (sendFailure is not null)
            {
                Pass($"无凭据提交未被接受并显式报告（{sendFailure}）");
                if (input.DraftText == "你好")
                {
                    Pass("发送失败后草稿已回填");
                }
                else
                {
                    Fail($"发送失败后草稿未回填：\"{input.DraftText}\"");
                }
            }
            else if (reachedTerminal)
            {
                if (chat.Activity == SessionActivity.Idle && chat.LastOutcome is not null)
                {
                    Pass($"无凭据运行收敛到终态 {chat.LastOutcome}，未留下永久处理中");
                }
                else
                {
                    Fail($"终态异常：Activity={chat.Activity} Outcome={chat.LastOutcome}");
                }

                await DrainUiQueueAsync();
                if (timeline.Entries.OfType<ErrorEntryViewModel>().Any())
                {
                    Pass("失败运行产生错误条目");
                }
                else
                {
                    Console.WriteLine("  信息：无错误条目（Outcome=" + chat.LastOutcome + "）");
                }
            }
            else
            {
                Fail("等待运行终态超时（90s）");
            }

            // 扩展命令（无需模型凭据）：handled 命令不永久等待运行结束（验证 P07），
            // 桥接 status 命令发出的 notify 应经占位链路显示为界面提示。
            string? commandFailure = null;
            input.OperationFailed += message => commandFailure ??= message;
            input.DraftText = "/fufucode-bridge.status";
            input.SendCommand.Execute(null);

            var sawRunning = false;
            var commandDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < commandDeadline && commandFailure is null)
            {
                if (chat.Activity != SessionActivity.Idle)
                {
                    sawRunning = true;
                }

                var noticeArrived = timeline.Entries.OfType<ErrorEntryViewModel>()
                    .Any(entry => entry.KindText == "提示" &&
                                  entry.Message.Contains("fufucode-bridge", StringComparison.Ordinal));
                if (noticeArrived || (sawRunning && chat.Activity == SessionActivity.Idle))
                {
                    break;
                }

                await Task.Delay(50);
            }

            await DrainUiQueueAsync();
            if (commandFailure is not null)
            {
                Fail("扩展命令提交失败：" + commandFailure);
            }
            else
            {
                Pass("扩展命令经真实 UI 链路完成，未永久等待运行结束（P07）");
            }

            var notices = timeline.Entries.OfType<ErrorEntryViewModel>()
                .Where(entry => entry.KindText == "提示" &&
                                entry.Message.Contains("fufucode-bridge", StringComparison.Ordinal))
                .ToArray();
            if (notices.Length > 0)
            {
                Pass("扩展 notify 经通知接线显示：" + notices[0].Message);
            }
            else
            {
                Fail("未观察到扩展 notify 的显示");
            }

            // 停止链路：真实 Pi 的 clear_queue+abort（审查 P1-3 关注的响应形状取证）
            try
            {
                await chat.StopAsync();
                Pass("真实 Pi 停止链路（clear_queue+abort）完成");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  信息：空闲停止抛异常（如实记录，交轨道 A 核实 P1-3）："
                                  + ex.GetBaseException().Message);
            }

            // 关闭并重开：会话恢复，历史树可读
            var sessionId = session.Session.Id;
            var transcriptBefore = session.History.CurrentBranchMessages.Count;
            await session.DisposeAsync();

            var resumed = await manager.ResumeAsync(sessionId);
            if (resumed.Chat.Connection == SessionConnection.Connected)
            {
                Pass("关闭后重开会话恢复连接");
            }
            else
            {
                Fail("恢复会话未连接");
            }

            var restored = HistoryMapper.MapTranscript(
                resumed.History.CurrentBranchMessages, resumed.Chat.CurrentModel);
            Console.WriteLine($"  信息：恢复历史消息 {restored.Count} 条（关闭前 {transcriptBefore} 条）");
            if (restored.Count >= transcriptBefore)
            {
                Pass("恢复历史不少于关闭前");
            }
            else
            {
                Fail($"恢复历史变少：{restored.Count} < {transcriptBefore}");
            }

            await resumed.DisposeAsync();

            // 组合根全路径（RealSessionHost.OpenAsync）：缺省恢复最近→按 id 恢复
            var first = await RealSessionHost.OpenAsync(
                new AppOptions(AppSessionMode.Real, project, null, null, null), CancellationToken.None, layout);
            if (first.Chat.Connection == SessionConnection.Connected && first.CurrentTranscript().Count == 0)
            {
                Pass("组合根启动：恢复最近会话并连接（历史 0 条）");
            }
            else
            {
                Fail($"组合根启动异常：Connection={first.Chat.Connection} 历史={first.CurrentTranscript().Count}");
            }

            var firstId = first.Current.Session.Id;
            await first.DisposeAsync();

            var third = await RealSessionHost.OpenAsync(
                new AppOptions(AppSessionMode.Real, project, firstId, null, null), CancellationToken.None, layout);
            if (third.Current.Session.Id == firstId)
            {
                Pass("组合根按 --session 指定 id 恢复");
            }
            else
            {
                Fail($"组合根 --session 恢复异常：id={third.Current.Session.Id}");
            }

            await third.DisposeAsync();

            // M4 会话管理：新建/切换/重命名/按会话隔离草稿（经 RealSessionHost，全程真实 Pi 进程）
            var hostM4 = await RealSessionHost.OpenAsync(
                new AppOptions(AppSessionMode.Real, project, null, null, null), CancellationToken.None, layout);
            var idA = hostM4.Current.Session.Id;
            hostM4.SaveDraft("草稿-A");

            await hostM4.NewSessionAsync();
            var idB = hostM4.Current.Session.Id;
            if (idB != idA && hostM4.Chat.Connection == SessionConnection.Connected)
            {
                Pass("M4：新建会话并切换（新 Pi 进程绑定新会话）");
            }
            else
            {
                Fail($"M4：新建会话异常（idB={idB} idA={idA}）");
            }

            if (string.IsNullOrEmpty(hostM4.LoadDraft()))
            {
                Pass("M4：新会话草稿为空（按会话隔离）");
            }
            else
            {
                Fail("M4：新会话不应带出旧草稿");
            }

            hostM4.SaveDraft("草稿-B");
            await hostM4.SwitchToAsync(idA);
            if (hostM4.Current.Session.Id == idA && hostM4.LoadDraft() == "草稿-A")
            {
                Pass("M4：切换回 A 会话，草稿按会话恢复");
            }
            else
            {
                Fail($"M4：切换/草稿恢复异常（id={hostM4.Current.Session.Id} 草稿={hostM4.LoadDraft()}）");
            }

            await hostM4.RenameCurrentAsync("会话-甲");
            if (hostM4.Sessions.FirstOrDefault(record => record.Id == idA)?.Name == "会话-甲")
            {
                Pass("M4：重命名经 set_session_name 落盘并可在列表读取");
            }
            else
            {
                Fail("M4：重命名未持久化");
            }

            await hostM4.SwitchToAsync(idB);
            if (hostM4.LoadDraft() == "草稿-B" && hostM4.Sessions.Count >= 2)
            {
                Pass("M4：B 会话草稿独立保留，会话列表完整");
            }
            else
            {
                Fail($"M4：B 会话草稿异常（{hostM4.LoadDraft()}）");
            }

            await hostM4.DisposeAsync();

            // F01：项目信任流程（隔离项目含 .pi/settings.json）
            await TestProjectTrustFlowAsync(node, cli);
        }
        catch (Exception ex)
        {
            Fail("真实 Pi 集成异常：" + ex.GetBaseException().Message);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不掩盖测试结果
            }
        }
    }

    // ── F01：项目信任（受保护资源要求显式决定；放弃不落盘；DoNotTrust 也可开会话） ──
    private static async Task TestProjectTrustFlowAsync(string node, string cli)
    {
        Console.WriteLine("[项目信任]");
        var root = Path.Combine(Path.GetTempPath(), "fufucode-trust-" + Guid.NewGuid().ToString("N")[..8]);
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(project, ".pi"));
        var settingsPath = Path.Combine(project, ".pi", "settings.json");
        File.WriteAllText(settingsPath, "{}");
        var layout = new AppDataLayout(Path.Combine(root, "appdata"));
        var options = new AppOptions(AppSessionMode.Real, project, null, null, null);
        var manager = new PersistentSessionManager(node, cli, layout);

        try
        {
            // (i) 未作决定时打开被拒，并报告受保护资源清单
            try
            {
                var denied = await RealSessionHost.OpenAsync(options, CancellationToken.None, layout);
                await denied.DisposeAsync();
                Fail("含受保护资源且未决定时 OpenAsync 未抛信任异常");
            }
            catch (ProjectTrustDecisionRequiredException required)
            {
                if (required.Status.ProtectedResources.Any(resource =>
                        Path.GetFullPath(resource).Equals(Path.GetFullPath(settingsPath),
                            StringComparison.OrdinalIgnoreCase)))
                {
                    Pass("OpenAsync 抛出信任要求，ProtectedResources 含 .pi/settings.json");
                }
                else
                {
                    Fail("信任异常缺少 settings.json：" +
                         string.Join(", ", required.Status.ProtectedResources));
                }
            }

            // (ii) 询问后放弃：抛 ProjectTrustDeclinedException，不落盘决定
            var askCount = 0;
            try
            {
                await ProjectTrustFlow.RunAsync(
                    () => RealSessionHost.OpenAsync(options, CancellationToken.None, layout),
                    status =>
                    {
                        askCount++;
                        return Task.FromResult<ProjectTrustDecision?>(null);
                    },
                    (_, _) => Fail("放弃时不应保存决定"));

                Fail("放弃信任决定未抛 ProjectTrustDeclinedException");
            }
            catch (ProjectTrustDeclinedException)
            {
                var decision = manager.GetProjectTrustStatus(project).Decision;
                if (decision is null)
                {
                    Pass("放弃信任决定：不落盘、不重试（ProjectTrustDeclinedException）");
                }
                else
                {
                    Fail($"放弃后决定被落盘：{decision}");
                }
            }

            // (iii) 询问→不信任：恰好询问一次、落盘 DoNotTrust、会话照样连通
            askCount = 0;
            var host = await ProjectTrustFlow.RunAsync(
                () => RealSessionHost.OpenAsync(options, CancellationToken.None, layout),
                status =>
                {
                    askCount++;
                    return Task.FromResult<ProjectTrustDecision?>(ProjectTrustDecision.DoNotTrust);
                },
                (status, decision) => RealSessionHost.SaveTrustDecision(options, status, decision, layout));

            try
            {
                var saved = manager.GetProjectTrustStatus(project).Decision;
                if (host.Chat.Connection == SessionConnection.Connected
                    && askCount == 1 && saved == ProjectTrustDecision.DoNotTrust)
                {
                    Pass("不信任决定落盘且会话连通（ask 恰好一次）");
                }
                else
                {
                    Fail($"信任流程异常：conn={host.Chat.Connection} ask={askCount} saved={saved}");
                }
            }
            finally
            {
                await host.DisposeAsync();
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不掩盖测试结果
            }
        }
    }

    /// <summary>等待一轮真实模型运行收敛：先等运行开始，再等 Idle 且有新助手条目（或终态变化/发送被拒）。</summary>
    private static async Task<bool> WaitForRealTurnAsync(IChatSession session, Func<bool> sendFailed,
        int assistantEntriesBefore, Func<int> assistantEntriesCount, SessionOutcome? previousOutcome,
        int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (sendFailed() || session.Activity != SessionActivity.Idle)
            {
                break;
            }

            await Task.Delay(50);
        }

        while (DateTime.UtcNow < deadline)
        {
            if (sendFailed())
            {
                return true;
            }

            if (session.Activity == SessionActivity.Idle && session.LastOutcome is not null &&
                (assistantEntriesCount() > assistantEntriesBefore || session.LastOutcome != previousOutcome))
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    /// <summary>等待真实运行到达终态（或发送被拒绝），返回 false 表示超时。</summary>
    private static async Task<bool> WaitForRealTerminalAsync(IChatSession session, Func<bool> sendFailed)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (sendFailed())
            {
                return true;
            }

            if (session.Activity == SessionActivity.Idle && session.LastOutcome is not null)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    /// <summary>
    /// 排空 UI 队列（decisions.md [轨道C] 线程同步约定）：后台线程先把终态事件 Post 到 UI 线程、
    /// 之后才把 Activity 置回 Idle；观察到 Idle 后须让已入队回调先跑完再断言。
    /// </summary>
    private static async Task DrainUiQueueAsync()
    {
        await Dispatcher.Yield(DispatcherPriority.Background);
        await Dispatcher.Yield(DispatcherPriority.Background);
    }

    // ── H3：get_commands 解析（测试夹具按 Pi 0.87.1 响应形状构造，见 runtime rpc-mode.js:542） ──
    private static void TestExtensionCatalogParsing()
    {
        Console.WriteLine("[扩展目录解析]");
        // 测试夹具：模拟 0.87.1 get_commands data.commands[]；末项故意缺 description/sourceInfo。
        var fixture = System.Text.Json.JsonDocument.Parse("""
            {
              "commands": [
                {
                  "name": "fufucode-bridge.status",
                  "description": "Report approval bridge status",
                  "source": "extension",
                  "sourceInfo": {
                    "path": "C:/Runtime/extensions/fufucode-bridge/index.ts",
                    "source": "extension",
                    "scope": "global",
                    "origin": "builtin",
                    "baseDir": "C:/Runtime/extensions/fufucode-bridge"
                  }
                },
                {
                  "name": "demo.ui-tour",
                  "description": "Walk the full ctx.ui surface",
                  "source": "extension",
                  "sourceInfo": {
                    "path": "C:/Repo/extensions/demo-extension/index.ts",
                    "source": "extension",
                    "scope": "project",
                    "baseDir": "C:/Repo/extensions/demo-extension"
                  }
                },
                {
                  "name": "review",
                  "description": "Review prompt template",
                  "source": "prompt",
                  "sourceInfo": { "path": "C:/Repo/.pi/prompts/review.md", "source": "prompt", "scope": "project" }
                },
                { "name": "skill:wechat-article", "source": "skill" }
              ]
            }
            """);

        var commands = ExtensionCatalog.ParseCommands(fixture.RootElement, out var parseError);
        if (parseError is null && commands.Count == 4)
        {
            Pass("get_commands 解析：4 条命令全部解析");
        }
        else
        {
            Fail($"get_commands 解析异常：count={commands.Count} error={parseError}");
        }

        var loaded = ExtensionCatalog.GroupLoadedExtensions(commands,
            new[] { new ExtensionErrorEntry(DateTimeOffset.Now,
                "C:/Repo/extensions/demo-extension/index.ts", "command", "boom") });
        var bridge = loaded.FirstOrDefault(extension => extension.IsApprovalBridge);
        var demo = loaded.FirstOrDefault(extension => extension.Name == "demo-extension");
        if (loaded.Count == 2 && bridge is not null &&
            bridge.Commands.Any(command => command.Name == "fufucode-bridge.status"))
        {
            Pass("扩展分组：baseDir 分组正确，审批桥识别");
        }
        else
        {
            Fail($"扩展分组异常：loaded={loaded.Count}");
        }

        if (demo is not null && demo.RecentErrors.Count == 1)
        {
            Pass("扩展错误按 ExtensionPath 归入对应扩展");
        }
        else
        {
            Fail($"扩展错误未归入（demo 组错误数 {demo?.RecentErrors.Count ?? -1}）");
        }

        var skill = commands.First(command => command.Name == "skill:wechat-article");
        var bridgeCmd = commands.First(command => command.Name == "fufucode-bridge.status");
        if (skill.Invocation == "/skill:wechat-article" &&
            bridgeCmd.Invocation == "/fufucode-bridge.status")
        {
            Pass("调用名规则：技能 /skill:x，扩展命令 /name");
        }
        else
        {
            Fail($"调用名异常：skill={skill.Invocation} ext={bridgeCmd.Invocation}");
        }

        if (skill.Description is null && skill.SourcePath is null)
        {
            Pass("缺字段容错：description/sourceInfo 缺失时按 null 处理");
        }
        else
        {
            Fail("缺字段容错失败");
        }

        // 整体形状不符 → QueryError 路径
        var bad = ExtensionCatalog.ParseCommands(
            System.Text.Json.JsonDocument.Parse("""{"unexpected": true}""").RootElement,
            out var badError);
        if (bad.Count == 0 && badError is not null)
        {
            Pass("异常响应填充 QueryError，不抛异常");
        }
        else
        {
            Fail($"异常响应处理失败：count={bad.Count} error={badError}");
        }
    }

    // ── H3：扩展发现扫描（Pi loader.js 规则镜像） ──
    private static void TestExtensionDiscoveryScan()
    {
        Console.WriteLine("[扩展发现扫描]");
        var root = Path.Combine(Path.GetTempPath(), "fufucode-scan-" + Guid.NewGuid().ToString("N")[..8]);
        var projectExt = Path.Combine(root, "project", ".pi", "extensions");
        var globalExt = Path.Combine(root, "appdata", "pi-agent", "extensions");
        try
        {
            Directory.CreateDirectory(projectExt);
            Directory.CreateDirectory(globalExt);

            File.WriteAllText(Path.Combine(projectExt, "direct.ts"), "export default function(){}");
            File.WriteAllText(Path.Combine(projectExt, "direct.js"), "module.exports = () => {};");
            Directory.CreateDirectory(Path.Combine(projectExt, "pkg-ts"));
            File.WriteAllText(Path.Combine(projectExt, "pkg-ts", "index.ts"), "");
            Directory.CreateDirectory(Path.Combine(projectExt, "pkg-js"));
            File.WriteAllText(Path.Combine(projectExt, "pkg-js", "index.js"), "");
            Directory.CreateDirectory(Path.Combine(projectExt, "pkg-declared"));
            File.WriteAllText(Path.Combine(projectExt, "pkg-declared", "package.json"),
                """{"pi":{"extensions":["./main.ts"]}}""");
            Directory.CreateDirectory(Path.Combine(projectExt, "no-entry"));
            File.WriteAllText(Path.Combine(projectExt, "no-entry", "readme.txt"), "x");
            File.WriteAllText(Path.Combine(globalExt, "global-tool.ts"), "");

            var found = ExtensionCatalog.ScanDiscovered(
                Path.Combine(root, "project"), Path.Combine(root, "appdata", "pi-agent"), []);

            var projectFound = found.Where(d => d.Scope == "project").ToArray();
            var globalFound = found.Where(d => d.Scope == "global").ToArray();
            var names = projectFound.Select(d => Path.GetFileName(d.Path)).ToArray();
            var expected = new[] { "direct.ts", "direct.js", "pkg-ts", "pkg-js", "pkg-declared" };
            if (expected.All(names.Contains) && !names.Contains("no-entry"))
            {
                Pass("发现扫描：直接文件 + index.ts/js + package.json 声明均计入，无入口目录排除");
            }
            else
            {
                Fail($"发现扫描异常：{string.Join(",", names)}");
            }

            if (globalFound.Length == 1 &&
                Path.GetFileName(globalFound[0].Path) == "global-tool.ts")
            {
                Pass("发现扫描：全局目录（AgentDirectory/extensions）生效");
            }
            else
            {
                Fail($"全局发现异常：{globalFound.Length} 项");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    // ── H3：命令填入规则 + Enter 只发送一次 ──
    private static async Task TestCommandFillRuleAsync()
    {
        Console.WriteLine("[命令填入]");
        var session = new ScriptedChatSession();
        var input = new InputViewModel(session);
        input.InsertInvocation("/demo.ui-tour");
        if (input.DraftText == "/demo.ui-tour ")
        {
            Pass("空草稿填入：草稿设为调用名+空格");
        }
        else
        {
            Fail($"空草稿填入异常：\"{input.DraftText}\"");
        }

        input.DraftText = "已有草稿";
        input.InsertInvocation("/skill:review");
        if (input.DraftText == "/skill:review 已有草稿")
        {
            Pass("非空草稿填入：调用名前置到草稿开头");
        }
        else
        {
            Fail($"非空草稿填入异常：\"{input.DraftText}\"");
        }

        // 填入后 Enter 恰好发送一次（经真实窗口按键路径）
        var window = new MainWindow();
        window.Show();
        await Task.Delay(600);
        var vm = (MainViewModel)window.DataContext!;
        var inputBox = window.GetVisualDescendants().OfType<TextBox>()
            .FirstOrDefault(box => box.Name == "InputBox");
        if (inputBox is null)
        {
            Fail("命令填入测试：找不到 InputBox");
            window.Close();
            return;
        }

        vm.Input.InsertInvocation("/demo.ui-tour");
        var countBefore = vm.Timeline.Entries.OfType<UserMessageViewModel>().Count();
        inputBox.Focus();
        await Task.Delay(80);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Task.Delay(200);
        await DrainUiQueueAsync();
        var delta = vm.Timeline.Entries.OfType<UserMessageViewModel>().Count() - countBefore;
        var entry = vm.Timeline.Entries.OfType<UserMessageViewModel>().LastOrDefault();
        if (delta == 1 && entry?.Text.Contains("/demo.ui-tour") == true)
        {
            Pass("填入命令后 Enter 发送一次且内容为调用名");
        }
        else
        {
            Fail($"填入后 Enter 发送异常：delta={delta} text=\"{entry?.Text}\"");
        }

        window.Close();
    }

    // ── H3：安全重启守卫（回合进行中/待决扩展请求时不可用） ──
    private static async Task TestRestartGuardAsync()
    {
        Console.WriteLine("[安全重启守卫]");
        var session = new StubExtensionUiSession();
        var vm = new ExtensionsPageViewModel();
        var restartCount = 0;
        vm.AttachRealSession(
            catalog: null, session, "C:/proj", "C:/agent",
            _ =>
            {
                restartCount++;
                return Task.CompletedTask;
            },
            (ProjectTrustStatus _) => Task.FromResult<ProjectTrustDecision?>(null),
            (_, _) => { },
            () => null,
            _ => { });
        await DrainUiQueueAsync();

        if (vm.CanRestart)
        {
            Pass("空闲且无待决请求时安全重启可用");
        }
        else
        {
            Fail($"空闲时重启不可用：{vm.RestartDisabledReason}");
        }

        session.Inner.Activity = SessionActivity.Running;
        session.Inner.Raise(new SessionEvent.ActivityChanged(SessionActivity.Running));
        await DrainUiQueueAsync();
        if (!vm.CanRestart && vm.RestartDisabledReason == "当前回合进行中")
        {
            Pass("回合进行中重启禁用并给出原因");
        }
        else
        {
            Fail($"运行中守卫异常：can={vm.CanRestart} reason=\"{vm.RestartDisabledReason}\"");
        }

        session.Inner.Activity = SessionActivity.Idle;
        session.Inner.Raise(new SessionEvent.ActivityChanged(SessionActivity.Idle));
        await DrainUiQueueAsync();
        var request = session.RaiseRequest();
        await DrainUiQueueAsync();
        if (!vm.CanRestart && vm.RestartDisabledReason == "有待处理的扩展请求")
        {
            Pass("待决扩展请求期间重启禁用并给出原因");
        }
        else
        {
            Fail($"待决请求守卫异常：can={vm.CanRestart} reason=\"{vm.RestartDisabledReason}\"");
        }

        session.RaiseClosed(request);
        await DrainUiQueueAsync();
        if (vm.CanRestart)
        {
            Pass("请求关闭后重启恢复可用");
        }
        else
        {
            Fail("请求关闭后重启仍禁用");
        }
    }

    /// <summary>
    /// 重启失败后的“无活动会话”流程（自测接缝注入失败）：旧进程已释放，
    /// 遮罩必须保持可见且显示错误 + 重试按钮，重试成功后恢复；
    /// 信任拒绝路径不得出现“已保留当前会话”的假文案。
    /// </summary>
    private static async Task TestRestartFailureFlowAsync()
    {
        Console.WriteLine("[重启失败流程]");
        var window = new MainWindow();
        window.Show();
        await Task.Delay(200);
        var vm = (MainViewModel)window.DataContext!;
        var session = new StubExtensionUiSession();

        var attempts = 0;
        window.RestartOperationForTesting = _ =>
        {
            attempts++;
            return attempts <= 2
                ? Task.FromException(new InvalidOperationException("selftest-restart-fail"))
                : Task.CompletedTask;
        };
        vm.Extensions.AttachRealSession(null, session, "proj", "agent",
            _ => window.RestartSessionForTestingAsync(),
            (ProjectTrustStatus _) => Task.FromResult<ProjectTrustDecision?>(null),
            (_, _) => { },
            () => null,
            _ => { });

        var overlay = window.FindControl<Border>("StartupOverlay")!;
        var retry = window.FindControl<Button>("StartupRetryButton")!;
        var text = window.FindControl<Avalonia.Controls.TextBlock>("StartupText")!;

        await vm.Extensions.RestartCommand.ExecuteAsync(null);
        await Task.Delay(300);
        if (attempts == 1 && overlay.IsVisible && retry.IsVisible &&
            text.Text?.Contains("重启失败") == true && vm.Extensions.HasStartupFailure)
        {
            Pass("重启失败流程：遮罩可见 + 错误消息 + 重试按钮 + LastStartupFailure");
        }
        else
        {
            Fail($"重启失败流程异常：attempts={attempts} overlay={overlay.IsVisible} " +
                 $"retry={retry.IsVisible} text=\"{text.Text}\" failure={vm.Extensions.HasStartupFailure}");
        }

        retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(400);
        if (attempts == 2 && overlay.IsVisible)
        {
            Pass("重启失败流程：首次重试仍失败，遮罩保留");
        }
        else
        {
            Fail($"重试一异常：attempts={attempts} overlay={overlay.IsVisible}");
        }

        retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(400);
        if (attempts == 3 && !overlay.IsVisible)
        {
            Pass("重启失败流程：重试成功后恢复，遮罩关闭");
        }
        else
        {
            Fail($"重试二异常：attempts={attempts} overlay={overlay.IsVisible}");
        }

        window.Close();

        // 信任拒绝路径：重启中途要求信任决定且用户放弃 → 不得谎称“已保留当前会话”。
        var window2 = new MainWindow();
        window2.Show();
        await Task.Delay(200);
        var vm2 = (MainViewModel)window2.DataContext!;
        window2.RestartOperationForTesting = _ => Task.FromException(
            new ProjectTrustDecisionRequiredException(new ProjectTrustStatus("C:/proj", null, ["AGENTS.md"])));
        window2.AskTrustForTesting = _ => Task.FromResult<ProjectTrustDecision?>(null);
        vm2.Extensions.AttachRealSession(null, new StubExtensionUiSession(), "proj", "agent",
            _ => window2.RestartSessionForTestingAsync(),
            (ProjectTrustStatus _) => Task.FromResult<ProjectTrustDecision?>(null),
            (_, _) => { },
            () => null,
            _ => { });
        await vm2.Extensions.RestartCommand.ExecuteAsync(null);
        await Task.Delay(300);
        var overlay2 = window2.FindControl<Border>("StartupOverlay")!;
        var text2 = window2.FindControl<Avalonia.Controls.TextBlock>("StartupText")!;
        var keptClaim = vm2.Timeline.Entries.OfType<ErrorEntryViewModel>()
            .Any(e => e.Message.Contains("已保留当前会话"));
        if (overlay2.IsVisible && text2.Text?.Contains("未作出信任决定") == true &&
            window2.FindControl<Button>("StartupRetryButton")!.IsVisible && !keptClaim)
        {
            Pass("重启信任拒绝：遮罩+未恢复说明+重试，无“已保留当前会话”假文案");
        }
        else
        {
            Fail($"重启信任拒绝异常：overlay={overlay2.IsVisible} text=\"{text2.Text}\" keptClaim={keptClaim}");
        }

        window2.Close();
    }

    /// <summary>
    /// H4 启停状态机（替身会话 + 注入上下文）：徽章状态、pending/active 差异、
    /// 撤销、应用成功/失败回滚、信任拒绝保留 pending、手动添加/移除。
    /// </summary>
    private static async Task TestExtensionManageStatesAsync()
    {
        Console.WriteLine("[扩展启停状态]");
        var root = Path.Combine(Path.GetTempPath(), "fufucode-h4-" + Guid.NewGuid().ToString("N")[..8]);
        var extA = Path.Combine(root, "ext-a");          // 已启用且已加载
        var extB = Path.Combine(root, "ext-b");          // 已发现未启用
        var extBroken = Path.Combine(root, "ext-broken");// 启用过但上次加载失败
        var extMissing = Path.Combine(root, "ext-gone"); // 持久列表里已删除的路径
        var extManual = Path.Combine(root, "ext-manual");// 手动添加
        try
        {
            foreach (var dir in new[] { extA, extB, extBroken, extManual })
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "index.ts"), "export default function() {}");
            }

            var session = new StubExtensionUiSession();
            var vm = new ExtensionsPageViewModel();
            IReadOnlyList<string>? appliedPaths = null;
            ExtensionApplyFailure? applyResult = null;
            var addedStore = new List<string>();
            var manage = new ExtensionManageContext(
                ActivePaths: [extA],
                EnabledPaths: [extA, extMissing],
                AddedPaths: [extManual],
                MissingPaths: [extMissing],
                LoadErrors: new Dictionary<string, string>
                {
                    [extA] = "残留旧错误（修复后再次启用成功应被清除）",
                    [extBroken] = "Pi 启动时扩展抛错",
                },
                ApplyAsync: (paths, _) =>
                {
                    appliedPaths = paths;
                    return Task.FromResult(applyResult);
                },
                PickExtensionAsync: null,
                AddExtension: p => addedStore.Add(p),
                RemoveAddedExtension: p => addedStore.Remove(p));
            vm.AttachRealSession(null, session, root, root,
                _ => Task.CompletedTask,
                (ProjectTrustStatus _) => Task.FromResult<ProjectTrustDecision?>(null),
                (_, _) => { }, () => null, _ => { },
                manage: manage);
            // 注入一份含审批桥的最小快照以产生行。
            var bridgeCmd = new CommandEntry("fufucode-bridge.status", null, "extension",
                "C:/Runtime/fufucode-bridge/index.ts", "C:/Runtime/fufucode-bridge", "global");
            vm.PreviewSnapshot(new ExtensionCatalogSnapshot
            {
                LoadedExtensions =
                [
                    new LoadedExtension("C:/Runtime/fufucode-bridge", "fufucode-bridge", "global",
                        [bridgeCmd], []),
                    new LoadedExtension(extA, "ext-a", "global",
                        [new CommandEntry("a.tool", null, "extension",
                            Path.Combine(extA, "index.ts"), extA, "global")], []),
                ],
                DiscoveredExtensions =
                [
                    new DiscoveredExtension(extB, "project", false),
                ],
            });
            await DrainUiQueueAsync();

            var rows = vm.Extensions;
            var rowA = rows.FirstOrDefault(r => r.Path == extA);
            var rowB = rows.FirstOrDefault(r => r.Path == extB);
            var rowBroken = rows.FirstOrDefault(r => r.Path == extBroken);
            var rowMissing = rows.FirstOrDefault(r => r.Path == extMissing);
            var rowManual = rows.FirstOrDefault(r => r.Path == extManual);
            var rowBridge = rows.FirstOrDefault(r => r.IsApprovalBridge);
            if (rowBridge is not null && rowBridge.ToggleIsEnabled == false &&
                rowBridge.ScopeText == "内置" && rowBridge.StatusText == "已加载" &&
                rowA?.StatusText == "已加载" && rowA.HasLoadError &&
                rowBroken?.StatusText == "加载失败" &&
                rowMissing?.StatusText == "路径缺失" && rowMissing.ToggleIsEnabled == false &&
                rowManual?.StatusText == "未启用" && rowManual.ScopeText == "手动添加" &&
                rowB?.StatusText == "未启用" && rowB.ScopeText == "项目")
            {
                Pass("启停徽章：内置桥/已加载/加载失败/路径缺失/未启用/手动添加/项目作用域");
            }
            else
            {
                Fail($"徽章异常：bridge={rowBridge?.StatusText} A={rowA?.StatusText} " +
                     $"broken={rowBroken?.StatusText} missing={rowMissing?.StatusText} " +
                     $"manual={rowManual?.StatusText} B={rowB?.StatusText}");
            }

            // 项目作用域 + 未信任（trustStatusSource 返回 null 决策）→ 启用 B 后出现信任提示
            rowB!.IsPendingEnabled = true;
            await DrainUiQueueAsync();
            rowB = vm.Extensions.First(r => r.Path == extB);
            if (vm.PendingChangeCount == 1 && rowB.StatusText == "待重启：启用" &&
                rowB.ShowTrustWarning && vm.HasPendingChanges)
            {
                Pass("pending 差异：待重启启用 + 项目未信任提示 + 变更 InfoBar");
            }
            else
            {
                Fail($"pending 异常：count={vm.PendingChangeCount} B={rowB.StatusText} " +
                     $"trustWarn={rowB.ShowTrustWarning}");
            }

            // 撤销回到 active
            vm.UndoChangesCommand.Execute(null);
            rowB = vm.Extensions.First(r => r.Path == extB);
            if (vm.PendingChangeCount == 0 && rowB.StatusText == "未启用")
            {
                Pass("撤销恢复 active 状态");
            }
            else
            {
                Fail($"撤销异常：count={vm.PendingChangeCount} B={rowB.StatusText}");
            }

            // 应用成功：期望集合（含缺失路径）整体提交持久化、active 更新、徽章转已加载
            rowB.IsPendingEnabled = true;
            await vm.RestartCommand.ExecuteAsync(null);
            rowB = vm.Extensions.First(r => r.Path == extB);
            if (applyResult is null && appliedPaths is not null &&
                appliedPaths.Contains(extB) && appliedPaths.Contains(extA) &&
                appliedPaths.Contains(extMissing) &&
                rowB.StatusText == "已加载" && vm.PendingChangeCount == 0)
            {
                Pass("应用变更成功：期望集合含缺失路径整体持久化、active 更新为已加载");
            }
            else
            {
                Fail($"应用成功异常：applied={appliedPaths?.Count} B={rowB.StatusText} " +
                     $"count={vm.PendingChangeCount}");
            }

            // 加载成功的路径清除旧错误；从未成功加载的 extBroken 错误保留。
            var rowA1 = vm.Extensions.First(r => r.Path == extA);
            var rowBroken1 = vm.Extensions.First(r => r.Path == extBroken);
            if (!rowA1.HasLoadError && rowA1.StatusText == "已加载" &&
                rowBroken1.StatusText == "加载失败" && rowBroken1.HasLoadError)
            {
                Pass("加载成功清除旧错误：ext-a 错误清空、未启用 ext-broken 错误保留");
            }
            else
            {
                Fail($"错误清除异常：A err={rowA1.HasLoadError} broken={rowBroken1.StatusText}");
            }

            // 停用 A（pending 移除）→ 待重启停用
            var rowA2 = vm.Extensions.First(r => r.Path == extA);
            rowA2.IsPendingEnabled = false;
            rowA2 = vm.Extensions.First(r => r.Path == extA);
            if (rowA2.StatusText == "待重启：停用" && vm.PendingChangeCount == 1)
            {
                Pass("停用扩展计入待重启停用");
            }
            else
            {
                Fail($"停用徽章异常：{rowA2.StatusText}");
            }

            // 应用失败：返回 failure → pending 回滚到 active；extA 仍已加载（优先级），
            // 行内 critical 错误文本保留展示。
            applyResult = new ExtensionApplyFailure(extA, "加载失败", "selftest-apply-fail");
            await vm.RestartCommand.ExecuteAsync(null);
            rowA2 = vm.Extensions.First(r => r.Path == extA);
            if (rowA2.StatusText == "已加载" && rowA2.HasLoadError &&
                rowA2.LoadError == "selftest-apply-fail" &&
                vm.PendingChangeCount == 0)
            {
                Pass("应用失败回滚：pending 复位、已加载徽章优先且行内错误文本保留");
            }
            else
            {
                Fail($"应用回滚异常：A={rowA2.StatusText} err={rowA2.LoadError} " +
                     $"count={vm.PendingChangeCount}");
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>启用列表按项目持久化往返 + 项目隔离 + 缺失路径过滤。</summary>
    private static void TestExtensionPreferencesPersistence()
    {
        Console.WriteLine("[扩展持久化]");
        var root = Path.Combine(Path.GetTempPath(), "fufucode-pref-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var metadata = new DesktopMetadataStore(new AppDataLayout(Path.Combine(root, "data")));
            var realPath = Path.Combine(root, "ext.ts");
            File.WriteAllText(realPath, "//x");
            ExtensionPreferences.SaveEnabled(metadata, "projA", [realPath, "C:/no/such/ext"]);
            var loadedA = ExtensionPreferences.LoadEnabled(metadata, "projA");
            var loadedB = ExtensionPreferences.LoadEnabled(metadata, "projB");
            var (existing, missing) = RealSessionHost.FilterExisting(loadedA);
            if (loadedA.Count == 2 && loadedB.Count == 0 &&
                existing.SequenceEqual(new[] { realPath }) &&
                missing.SequenceEqual(new[] { "C:/no/such/ext" }))
            {
                Pass("启用列表按项目隔离持久化，缺失路径被过滤");
            }
            else
            {
                Fail($"持久化异常：A={loadedA.Count} B={loadedB.Count} " +
                     $"existing={existing.Length} missing={missing.Length}");
            }

            var errors = new DesktopMetadataStore(new AppDataLayout(Path.Combine(root, "data2")));
            ExtensionPreferences.RecordError(errors, "p", "C:/x/ext", "boom");
            if (ExtensionPreferences.LoadErrors(errors, "p")["C:/x/ext"] == "boom")
            {
                Pass("加载错误按项目持久化可读回");
            }
            else
            {
                Fail("加载错误持久化读回失败");
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>“添加扩展”入口校验：index.ts、package.json pi.extensions、.ts 文件通过；空目录/非扩展文件拒绝。</summary>
    private static void TestAddExtensionValidation()
    {
        Console.WriteLine("[添加扩展校验]");
        var root = Path.Combine(Path.GetTempPath(), "fufucode-addext-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var withIndex = Path.Combine(root, "with-index");
            Directory.CreateDirectory(withIndex);
            File.WriteAllText(Path.Combine(withIndex, "index.ts"), "export default function() {}");
            var withPackage = Path.Combine(root, "with-pkg");
            Directory.CreateDirectory(withPackage);
            File.WriteAllText(Path.Combine(withPackage, "package.json"),
                """{"pi":{"extensions":{"entry":"src/main.ts"}}}""");
            var empty = Path.Combine(root, "empty");
            Directory.CreateDirectory(empty);
            var file = Path.Combine(root, "single.ts");
            File.WriteAllText(file, "//x");
            var txt = Path.Combine(root, "readme.txt");
            File.WriteAllText(txt, "x");

            if (ExtensionCatalog.IsValidExtensionPath(withIndex) &&
                ExtensionCatalog.IsValidExtensionPath(withPackage) &&
                ExtensionCatalog.IsValidExtensionPath(file) &&
                !ExtensionCatalog.IsValidExtensionPath(empty) &&
                !ExtensionCatalog.IsValidExtensionPath(txt))
            {
                Pass("添加入口校验：index.ts / pi.extensions / .ts 文件通过，空目录与文本文件拒绝");
            }
            else
            {
                Fail("添加入口校验异常");
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// 启动期扩展加载失败（替身注入 UserExtensionLoadException）：
    /// 遮罩显示“扩展 X 加载失败”并出现“停用该扩展并重试”；点击后从持久列表移除并重试。
    /// </summary>
    private static async Task TestStartupExtensionFailureAsync()
    {
        Console.WriteLine("[启动期扩展失败]");
        var badPath = @"C:\fake-ext\bad-ext";
        var window = new MainWindow(new AppOptions(AppSessionMode.Real, "C:/proj", null, null, null));
        var attempts = 0;
        string? disabledPath = null;
        window.OpenHostForTesting = _ =>
        {
            attempts++;
            return Task.FromException<RealSessionHost>(new UserExtensionLoadException(badPath));
        };
        window.DisableExtensionForTesting = path => disabledPath = path;
        window.Show();
        await Task.Delay(800);

        var overlay = window.FindControl<Border>("StartupOverlay")!;
        var text = window.FindControl<Avalonia.Controls.TextBlock>("StartupText")!;
        var disableButton = window.FindControl<Button>("StartupDisableExtensionButton")!;
        if (overlay.IsVisible && disableButton.IsVisible &&
            text.Text?.Contains("加载失败") == true && text.Text.Contains("bad-ext"))
        {
            Pass("启动失败遮罩：扩展加载失败文案 + “停用该扩展并重试”按钮");
        }
        else
        {
            Fail($"启动失败遮罩异常：overlay={overlay.IsVisible} disable={disableButton.IsVisible} " +
                 $"text=\"{text.Text}\"");
        }

        disableButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(500);
        if (disabledPath == badPath && attempts == 2)
        {
            Pass("停用并重试：路径已移除且重新尝试打开会话");
        }
        else
        {
            Fail($"停用重试异常：disabled={disabledPath} attempts={attempts}");
        }

        window.Close();
    }

    // ── H3 截图：扩展页 light/dark（夹具快照）+ 命令 Flyout（light） ──
    private static async Task TestH3ShotsAsync()
    {
        Console.WriteLine("[H3 扩展页截图]");
        var shotsDir = EvidenceDirectory("h3-shots");
        var previousTheme = Application.Current!.RequestedThemeVariant;
        try
        {
            foreach (var (variant, tag) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                Application.Current!.RequestedThemeVariant = variant;
                var window = new MainWindow { Width = 1280, Height = 800 };
                window.Show();
                await Task.Delay(700);
                var vm = (MainViewModel)window.DataContext!;
                vm.Extensions.PreviewSnapshot(BuildH3FixtureSnapshot());
                // H4 启停预览：已加载 demo、待启用 local-tool（项目未信任→提示）、
                // 待停用 global-tool、路径缺失、加载失败（未启用+错误）、手动添加。
                vm.Extensions.PreviewExtensionState(
                    activePaths:
                    [
                        @"C:\Repo\extensions\demo-extension",
                        @"C:\AppData\pi-agent\extensions\global-tool.ts",
                    ],
                    pendingPaths:
                    [
                        @"C:\Repo\extensions\demo-extension",
                        @"C:\Repo\.pi\extensions\local-tool",
                    ],
                    addedPaths: [@"D:\Tools\my-extension"],
                    missingPaths: [@"C:\Repo\extensions\deleted-ext"],
                    loadErrors: new Dictionary<string, string>
                    {
                        [@"C:\Repo\extensions\broken-ext"] =
                            "Error: Failed to load extension: fufucode-selftest-broken",
                    });
                vm.Extensions.PreviewTrustStatus(new ProjectTrustStatus(
                    @"C:\Projects\DemoRepo", null,
                    new[] { @"C:\Projects\DemoRepo\.pi\settings.json" }));
                vm.CurrentPage = AppPage.Extensions;
                await Task.Delay(400);
                await DrainUiQueueAsync();
                SaveShot(window, Path.Combine(shotsDir, $"extensions-{tag}.png"));
                vm.CurrentPage = AppPage.Chat;

                if (tag == "light")
                {
                    // 命令 Flyout 打开态
                    var button = window.GetVisualDescendants().OfType<Button>()
                        .FirstOrDefault(b => b.Name == "CommandFlyoutButton");
                    if (button is null)
                    {
                        Fail("找不到命令入口按钮/Flyout");
                    }
                    else
                    {
                        button.Flyout!.ShowAt(button);
                        await Task.Delay(400);
                        await DrainUiQueueAsync();
                        SaveShot(window, Path.Combine(shotsDir, "command-flyout-light.png"));
                        ((Flyout)button.Flyout!).Hide();
                    }
                }

                window.Close();
            }

            Pass($"H3 截图已输出到 {shotsDir}");
        }
        catch (Exception ex)
        {
            Fail("H3 截图生成异常：" + ex.GetType().Name + " " + ex.Message);
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = previousTheme;
        }
    }

    // ── M6/W1-a：设置页 ──

    private static SettingsPageViewModel.Context MockSettingsContext(AppSettingsStore store) => new(
        Store: store,
        IsMock: true,
        CurrentModel: () => null,
        QueryModels: null,
        AgentDirectory: null,
        DataRoot: null,
        LogsDirectory: null,
        ApprovalText: () => null,
        ApprovalDiagnostic: () => null,
        ActiveExtensionPaths: () => [],
        LastStartupFailure: () => null,
        ProjectPath: () => "(mock)",
        OpenFolder: _ => Task.CompletedTask,
        CopyText: _ => Task.CompletedTask,
        PiVersion: () => "不可用",
        NodeVersion: () => "不可用");

    private static Task TestSettingsThemeAsync()
    {
        Console.WriteLine("[设置页：主题持久化与失败保护]");
        var root = Path.Combine(Path.GetTempPath(),
            "fufucode-w1-theme-" + Guid.NewGuid().ToString("N")[..8]);
        var layout = new AppDataLayout(root);
        layout.EnsureCreated();
        var previousTheme = Application.Current!.RequestedThemeVariant;
        try
        {
            var store = new AppSettingsStore(layout);
            var changed = new List<string>();
            store.Changed += s => changed.Add(s.Theme);
            var vm = new SettingsPageViewModel();
            vm.Attach(MockSettingsContext(store));

            vm.ThemeIndex = 2;
            if (Application.Current.RequestedThemeVariant == ThemeVariant.Dark &&
                store.Current.Theme == "dark" &&
                File.Exists(Path.Combine(root, "settings.json")) &&
                changed.Contains("dark"))
            {
                Pass("设置 dark：主题即时生效、settings.json 落盘、Changed 触发");
            }
            else
            {
                Fail($"设置 dark 未生效：theme={store.Current.Theme} variant={Application.Current.RequestedThemeVariant}");
            }

            vm.ThemeIndex = 1;
            vm.ThemeIndex = 0;
            if (store.Current.Theme == "system" &&
                Application.Current.RequestedThemeVariant == ThemeVariant.Default)
            {
                Pass("light/system 往返：主题与落盘一致");
            }
            else
            {
                Fail($"light/system 往返异常：theme={store.Current.Theme}");
            }

            // 更高版本号的 settings.json：Update 必须失败且不覆写、不改主题。
            File.WriteAllText(Path.Combine(root, "settings.json"),
                "{\"version\":2,\"theme\":\"light\"}");
            var store2 = new AppSettingsStore(layout);
            var vm2 = new SettingsPageViewModel();
            vm2.Attach(MockSettingsContext(store2));
            var themeBefore = Application.Current.RequestedThemeVariant;
            vm2.ThemeIndex = 2;
            if (vm2.HasAppearanceError &&
                Application.Current.RequestedThemeVariant == themeBefore &&
                store2.Current.Theme != "dark")
            {
                Pass("newer-version 设置文件：Update 失败显示错误且主题不变");
            }
            else
            {
                Fail($"newer-version 保护失效：error={vm2.AppearanceError ?? "无"} theme={store2.Current.Theme}");
            }
        }
        catch (Exception ex)
        {
            Fail("设置主题测试异常：" + ex.GetType().Name + " " + ex.Message);
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = previousTheme;
        }

        return Task.CompletedTask;
    }

    private static void TestModelsConfigParsing()
    {
        Console.WriteLine("[设置页：models.json 解析与脱敏]");
        var root = Path.Combine(Path.GetTempPath(),
            "fufucode-w1-models-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        const string secretValue = "sekrit-selftest-value-42";
        const string literalKey = "LITERAL-SECRET-777";
        Environment.SetEnvironmentVariable("FUFUCODE_SELFTEST_KEY", secretValue);
        try
        {
            File.WriteAllText(Path.Combine(root, "models.json"), """
                {"providers":{
                  "env":{"baseUrl":"https://a.example","api":"openai","apiKey":"$FUFUCODE_SELFTEST_KEY","models":[{"id":"m1"}]},
                  "envUnset":{"apiKey":"${FUFUCODE_SELFTEST_UNSET}"},
                  "literal":{"apiKey":"LITERAL-SECRET-777"},
                  "nokey":{}
                }}
                """);
            var snapshot = PiModelsConfig.Read(root);
            var byName = snapshot.Providers.ToDictionary(p => p.Name);
            var allText = string.Join("\n", snapshot.Providers.Select(p =>
                $"{p.Name} {p.BaseUrl} {p.Api} {string.Join(",", p.ModelIds)} {p.KeyText}"));

            var ok =
                snapshot.FileExists && snapshot.Error is null && snapshot.Providers.Count == 4 &&
                byName["env"].KeyText == "环境变量 FUFUCODE_SELFTEST_KEY（已设置）" &&
                !byName["env"].KeyIsCaution &&
                byName["envUnset"].KeyText == "环境变量 FUFUCODE_SELFTEST_UNSET（未设置）" &&
                byName["envUnset"].KeyIsCaution &&
                byName["literal"].KeyText == "字面密钥（已隐藏）" &&
                byName["literal"].KeyIsCaution &&
                byName["nokey"].KeyText == "未配置 apiKey" &&
                byName["env"].ModelIds is ["m1"] &&
                !allText.Contains(secretValue) && !allText.Contains(literalKey);
            if (ok)
            {
                Pass("models.json：$VAR/${VAR}/字面量/缺省四种 apiKey 形态脱敏正确");
            }
            else
            {
                Fail("models.json 脱敏不符：" + allText.Replace("\n", " | "));
            }

            var missing = PiModelsConfig.Read(Path.Combine(root, "nope"));
            if (!missing.FileExists)
            {
                Pass("models.json 缺失如实报告");
            }
            else
            {
                Fail("缺失 models.json 未如实报告");
            }

            File.WriteAllText(Path.Combine(root, "models.json"), "{not json");
            var broken = PiModelsConfig.Read(root);
            if (broken.Error is not null)
            {
                Pass("坏 JSON 如实报告解析错误");
            }
            else
            {
                Fail("坏 JSON 未报告错误");
            }

            // 诊断摘要不得含任何密钥值（$VAR 引用与字面量两种夹具）。
            File.WriteAllText(Path.Combine(root, "models.json"), """
                {"providers":{"env":{"apiKey":"$FUFUCODE_SELFTEST_KEY"},"literal":{"apiKey":"LITERAL-SECRET-777"}}}
                """);
            var summary = DiagnosticsSummary.Build(new DiagnosticsSummary.Input(
                ProjectPath: "C:\\proj",
                ModeText: "真实 Pi 会话",
                ApprovalText: "Verified",
                ApprovalDiagnostic: null,
                ActiveExtensionPaths: ["C:\\ext\\a"],
                LastStartupFailure: null,
                AgentDirectory: root,
                DataRoot: root,
                LogsDirectory: root,
                Settings: new AppSettings(),
                SettingsLoadError: null,
                PiVersion: "0.0.0",
                NodeVersion: "0.0.0"));
            if (!summary.Contains(secretValue) && !summary.Contains(literalKey) &&
                summary.Contains("FUFUCODE_SELFTEST_KEY") && summary.Contains("字面密钥（已隐藏）"))
            {
                Pass("诊断摘要脱敏：含变量名/隐藏占位，不含密钥值");
            }
            else
            {
                Fail("诊断摘要脱敏失败");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("FUFUCODE_SELFTEST_KEY", null);
        }
    }

    // ── W2：设置页应用链路（代理/端点/模型/思考的保存与生效时机、密钥不落地） ──
    private static async Task TestSettingsApplyAsync()
    {
        Console.WriteLine("[设置页：W2 设置应用链路]");
        var root = Path.Combine(Path.GetTempPath(),
            "fufucode-w2-settings-" + Guid.NewGuid().ToString("N")[..8]);
        var layout = new AppDataLayout(root);
        layout.EnsureCreated();
        Directory.CreateDirectory(layout.AgentDirectory);
        const string secretKey = "SELFTEST-SECRET-DO-NOT-LEAK-9";
        try
        {
            var store = new AppSettingsStore(layout);
            var setModelCalls = new List<string>();
            var setThinkingCalls = new List<string>();
            var savedCredentials = new List<(string Name, string Key)>();
            var turnActive = false;
            var restartCalls = 0;
            string? appliedHeader = null;
            string? resolveResult = "resolved";

            var vm = new SettingsPageViewModel();
            vm.Attach(MockSettingsContext(store) with
            {
                AgentDirectory = layout.AgentDirectory,
                IsTurnActive = () => turnActive,
                SetModel = (p, m, _) =>
                {
                    setModelCalls.Add($"{p}/{m}");
                    return Task.FromResult(new RpcResponse("1", "set_model", true, null, null));
                },
                SetThinkingLevel = (l, _) =>
                {
                    setThinkingCalls.Add(l);
                    return Task.FromResult(new RpcResponse("1", "set_thinking_level", true, null, null));
                },
                QueryState = _ => Task.FromResult(new RpcResponse("1", "get_state", true,
                    JsonDocument.Parse("""{"model":{"provider":"openai","id":"gpt-x"}}""").RootElement, null)),
                SaveCredential = (n, k) => savedCredentials.Add((n, k)),
                ResolveCredential = _ => ValueTask.FromResult<string?>(resolveResult),
                ModelApplied = m => appliedHeader = m,
                RestartSession = () => { restartCalls++; return Task.CompletedTask; },
            });

            // 1) 代理非法值：报错且不保存
            vm.ProxyInput = "not-a-url";
            vm.SaveProxyCommand.Execute(null);
            if (vm.HasProxyError && store.Current.ProxyUrl is null)
            {
                Pass("代理非法值报错且不保存");
            }
            else
            {
                Fail($"代理非法值处理异常：err={vm.ProxyError} saved={store.Current.ProxyUrl}");
            }

            vm.ProxyInput = "http://user:pw@127.0.0.1:8080";
            vm.SaveProxyCommand.Execute(null);
            if (vm.HasProxyError && store.Current.ProxyUrl is null)
            {
                Pass("代理带 userinfo 拒绝且不保存");
            }
            else
            {
                Fail("代理 userinfo 校验缺失");
            }

            // 2) 合法代理保存 + 重启 InfoBar
            vm.ProxyInput = " http://127.0.0.1:8080 ";
            vm.SaveProxyCommand.Execute(null);
            if (!vm.HasProxyError && store.Current.ProxyUrl == "http://127.0.0.1:8080" &&
                vm.HasRestartNotice && vm.RestartNoticeText!.Contains("重启"))
            {
                Pass("合法代理保存并提示重启生效");
            }
            else
            {
                Fail($"合法代理异常：err={vm.ProxyError} saved={store.Current.ProxyUrl} notice={vm.RestartNoticeText}");
            }

            // 「立即重启」走 Context.RestartSession
            await vm.RestartNowCommand.ExecuteAsync(null);
            if (restartCalls == 1)
            {
                Pass("立即重启回调接通");
            }
            else
            {
                Fail($"立即重启未调用（calls={restartCalls}）");
            }

            // 3) 同一 store 主题+代理并存（Update 整记录写回，不互相覆盖）
            vm.ThemeIndex = 1;
            var json = File.ReadAllText(Path.Combine(root, "settings.json"));
            if (json.Contains("\"light\"") && json.Contains("127.0.0.1:8080"))
            {
                Pass("同一 store 主题+代理并存（合写无覆盖）");
            }
            else
            {
                Fail("settings.json 合写缺失：" + json);
            }

            // 4) 端点校验：非法提供方名 / 非法 URL / 非法环境变量名
            vm.EndpointProviderName = "bad name!";
            vm.EndpointBaseUrl = "https://api.example.com/v1";
            vm.EndpointModelId = "m1";
            vm.EndpointKeyModeIndex = 1;
            vm.EndpointEnvName = "GOOD_VAR";
            await vm.SaveEndpointAsync(null);
            var invalidName = vm.HasEndpointError;
            vm.EndpointProviderName = "ok-name";
            vm.EndpointBaseUrl = "ftp://x";
            await vm.SaveEndpointAsync(null);
            var invalidUrl = vm.HasEndpointError;
            vm.EndpointBaseUrl = "https://api.example.com/v1";
            vm.EndpointEnvName = "9BAD";
            await vm.SaveEndpointAsync(null);
            var invalidEnv = vm.HasEndpointError;
            if (invalidName && invalidUrl && invalidEnv && store.Current.Endpoint is null)
            {
                Pass("端点字段校验：名称/URL/变量名非法均拒绝且不保存");
            }
            else
            {
                Fail($"端点校验缺失：name={invalidName} url={invalidUrl} env={invalidEnv}");
            }

            // 5) models.json 同名非自有 provider 冲突拒绝
            File.WriteAllText(Path.Combine(layout.AgentDirectory, "models.json"), """
                {"providers":{"dup":{"baseUrl":"https://dup.example","apiKey":"$X","models":[{"id":"m"}]}}}
                """);
            vm.EndpointProviderName = "dup";
            vm.EndpointEnvName = "GOOD_VAR";
            await vm.SaveEndpointAsync(null);
            if (vm.HasEndpointError && vm.EndpointError!.Contains("同名 provider") &&
                store.Current.Endpoint is null)
            {
                Pass("同名非自有 provider 拒绝覆盖");
            }
            else
            {
                Fail($"同名冲突未拒绝：err={vm.EndpointError}");
            }

            // 6) API Key 路径：派生名写入 SaveCredential；ResolveCredential=null → 不保存
            resolveResult = null;
            vm.EndpointProviderName = "my-provider";
            vm.EndpointBaseUrl = "https://api.example.com/v1";
            vm.EndpointModelId = "m1";
            vm.EndpointKeyModeIndex = 0;
            await vm.SaveEndpointAsync(secretKey);
            if (savedCredentials.Count == 1 &&
                savedCredentials[0].Name == "FUFUCODE_MY_PROVIDER_API_KEY" &&
                savedCredentials[0].Key == secretKey &&
                vm.HasEndpointError && store.Current.Endpoint is null)
            {
                Pass("密钥路径：凭据写派生名；解析失败不保存端点");
            }
            else
            {
                Fail($"密钥路径异常：saved={savedCredentials.Count} " +
                     $"name={savedCredentials.FirstOrDefault().Name} err={vm.EndpointError}");
            }

            // 7) 密钥不出现：任何公开 VM 字符串属性与诊断摘要均不含密钥
            var leaked = typeof(SettingsPageViewModel).GetProperties()
                .Where(p => p.PropertyType == typeof(string) && p.CanRead)
                .Select(p => p.GetValue(vm) as string)
                .Any(s => s?.Contains(secretKey) == true);
            var diag = DiagnosticsSummary.Build(new DiagnosticsSummary.Input(
                ProjectPath: "C:\\proj", ModeText: "mock", ApprovalText: null,
                ApprovalDiagnostic: null, ActiveExtensionPaths: [], LastStartupFailure: null,
                AgentDirectory: layout.AgentDirectory, DataRoot: root, LogsDirectory: root,
                Settings: store.Current, SettingsLoadError: null,
                PiVersion: "0", NodeVersion: "0"));
            if (!leaked && !diag.Contains(secretKey) && !json.Contains(secretKey))
            {
                Pass("密钥安全：VM 属性/诊断摘要/settings.json 均无密钥");
            }
            else
            {
                Fail("密钥泄漏到 VM 属性或诊断摘要");
            }

            // 8) 解析成功 → 端点保存 + SelectedModel 联动 + 重启提示
            resolveResult = "resolved";
            await vm.SaveEndpointAsync(secretKey);
            if (!vm.HasEndpointError && store.Current.Endpoint is { } ep &&
                ep.ProviderName == "my-provider" &&
                ep.ApiKeyEnvironmentVariable == "FUFUCODE_MY_PROVIDER_API_KEY" &&
                store.Current.SelectedModel == "my-provider/m1" &&
                vm.HasRestartNotice)
            {
                Pass("端点保存成功并联动 SelectedModel + 重启提示");
            }
            else
            {
                Fail($"端点保存异常：err={vm.EndpointError} endpoint={store.Current.Endpoint} " +
                     $"model={store.Current.SelectedModel}");
            }

            // 9) 回合进行中改模型：仅保存不调用 RPC
            turnActive = true;
            var choice = new SettingsPageViewModel.ModelChoice("openai/gpt-x", "openai/gpt-x");
            vm.ModelChoices.Add(choice);
            vm.SelectedModel = choice;
            await Task.Delay(20);
            if (setModelCalls.Count == 0 && store.Current.SelectedModel == "openai/gpt-x" &&
                vm.ModelsInfo?.Contains("当前会话未切换") == true)
            {
                Pass("回合进行中改模型：仅保存，不调用 live RPC");
            }
            else
            {
                Fail($"运行中改模型异常：calls={setModelCalls.Count} saved={store.Current.SelectedModel} " +
                     $"info={vm.ModelsInfo}");
            }

            // 10) 空闲改模型：先 RPC 成功后保存，页头 chip 刷新
            turnActive = false;
            await Task.Delay(20);
            var idleChoice = new SettingsPageViewModel.ModelChoice("openai/gpt-y", "openai/gpt-y");
            vm.ModelChoices.Add(idleChoice);
            vm.SelectedModel = idleChoice;
            await Task.Delay(20);
            if (setModelCalls.Contains("openai/gpt-y") &&
                store.Current.SelectedModel == "openai/gpt-y" &&
                appliedHeader == "openai/gpt-x" /* QueryState 假数据返回 gpt-x */ &&
                vm.CurrentModelText == "openai/gpt-x")
            {
                Pass("空闲改模型：RPC 成功后保存并刷新页头");
            }
            else
            {
                Fail($"空闲改模型异常：calls={string.Join(",", setModelCalls)} " +
                     $"saved={store.Current.SelectedModel} header={appliedHeader}");
            }

            // 11) set_model 失败：不保存并回退选择
            vm.Attach(MockSettingsContext(store) with
            {
                IsTurnActive = () => false,
                SetModel = (_, _, _) => Task.FromResult(
                    new RpcResponse("1", "set_model", false, null, "model not found")),
            });
            var failing = new SettingsPageViewModel.ModelChoice("bad/m", "bad/m");
            vm.ModelChoices.Add(failing);
            vm.SelectedModel = failing;
            await Task.Delay(20);
            if (vm.ModelsError == "model not found" &&
                store.Current.SelectedModel == "openai/gpt-y" &&
                vm.SelectedModel?.Value == "openai/gpt-y")
            {
                Pass("set_model 失败不保存且回退选择");
            }
            else
            {
                Fail($"set_model 失败处理异常：err={vm.ModelsError} " +
                     $"saved={store.Current.SelectedModel} sel={vm.SelectedModel?.Value}");
            }

            // 12) Attach 自动拉取模型列表：填充/回填不触发 set_model、不改写 settings.json
            var settingsPath = Path.Combine(root, "settings.json");
            var jsonBefore = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
            var autoSetModelCalls = new List<string>();
            using var modelsDoc = JsonDocument.Parse(
                "{\"models\":[{\"provider\":\"prov-a\",\"id\":\"m1\"}," +
                "{\"provider\":\"prov-b\",\"id\":\"m2\",\"name\":\"Model Two\"}]}");
            vm.Attach(MockSettingsContext(store) with
            {
                IsMock = false,
                QueryModels = _ => Task.FromResult(new RpcResponse(
                    "1", "get_available_models", true, modelsDoc.RootElement.Clone(), null)),
                SetModel = (m, _, _) =>
                {
                    autoSetModelCalls.Add(m);
                    return Task.FromResult(new RpcResponse("1", "set_model", true, null, null));
                },
            });
            for (var i = 0; i < 100 && vm.ModelChoices.Count < 3; i++)
            {
                await Task.Delay(20);
            }

            var jsonAfter = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
            if (vm.ModelChoices.Any(c => c.Value == "prov-a/m1") &&
                vm.ModelChoices.Any(c => c.Value == "prov-b/m2") &&
                vm.SelectedModel?.Value == store.Current.SelectedModel &&
                autoSetModelCalls.Count == 0 &&
                jsonAfter == jsonBefore)
            {
                Pass("Attach 自动拉取模型列表：填充/回填不触发 set_model 与设置写入");
            }
            else
            {
                Fail($"Attach 自动拉取异常：choices={vm.ModelChoices.Count} " +
                     $"sel={vm.SelectedModel?.Value} saved={store.Current.SelectedModel} " +
                     $"setModel={autoSetModelCalls.Count} jsonChanged={jsonAfter != jsonBefore}");
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ── W1 截图：设置页 light/dark（顶部+关于滚底）、对话空态、扩展 mock 空态 ──
    private static async Task TestW1ShotsAsync()
    {
        Console.WriteLine("[W1 截图]");
        var shotsDir = EvidenceDirectory("w1-shots");
        var previousTheme = Application.Current!.RequestedThemeVariant;
        try
        {
            foreach (var (variant, tag) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                Application.Current!.RequestedThemeVariant = variant;
                var window = new MainWindow { Width = 1280, Height = 800 };
                window.Show();
                await Task.Delay(700);
                var vm = (MainViewModel)window.DataContext!;

                vm.CurrentPage = AppPage.Settings;
                await Task.Delay(400);
                await DrainUiQueueAsync();
                SaveShot(window, Path.Combine(shotsDir, $"settings-{tag}.png"));
                var scroller = window.GetVisualDescendants().OfType<Views.SettingsPage>()
                    .FirstOrDefault()?
                    .GetVisualDescendants().OfType<ScrollViewer>()
                    .FirstOrDefault();
                scroller?.ScrollToEnd();
                await Task.Delay(300);
                await DrainUiQueueAsync();
                SaveShot(window, Path.Combine(shotsDir, $"settings-{tag}-about.png"));

                vm.CurrentPage = AppPage.Chat;
                vm.Timeline.Reset();
                await Task.Delay(300);
                await DrainUiQueueAsync();
                SaveShot(window, Path.Combine(shotsDir, $"chat-empty-{tag}.png"));

                vm.CurrentPage = AppPage.Extensions;
                await Task.Delay(300);
                await DrainUiQueueAsync();
                SaveShot(window, Path.Combine(shotsDir, $"extensions-mock-{tag}.png"));

                // P2-17：运行中 composer——steer+followUp 两种徽章、插入按钮、活动 chip。
                vm.CurrentPage = AppPage.Chat;
                var shotMock = (MockChatSession)vm.Session;
                shotMock.SimulateRunningQueue(new[] { "排队：继续补齐单元测试" });
                await DrainUiQueueAsync();
                vm.Input.DraftText = "插入：把 README 里的示例也更新";
                vm.Input.SteerCommand.Execute(null);
                await Task.Delay(150);
                await DrainUiQueueAsync();
                SaveShot(window, Path.Combine(shotsDir, $"composer-running-{tag}.png"));
                await shotMock.StopAsync();

                window.Close();
            }

            Pass($"W1 截图已输出到 {shotsDir}");
        }
        catch (Exception ex)
        {
            Fail("W1 截图生成异常：" + ex.GetType().Name + " " + ex.Message);
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = previousTheme;
        }
    }

    /// <summary>截图夹具快照：覆盖已加载+错误+已发现未加载+三类命令+审批桥状态。</summary>
    private static ExtensionCatalogSnapshot BuildH3FixtureSnapshot()
    {
        var fixture = System.Text.Json.JsonDocument.Parse("""
            {
              "commands": [
                {
                  "name": "fufucode-bridge.status",
                  "description": "Report approval bridge status",
                  "source": "extension",
                  "sourceInfo": { "path": "C:/Runtime/extensions/fufucode-bridge/index.ts",
                                  "scope": "global", "baseDir": "C:/Runtime/extensions/fufucode-bridge" }
                },
                {
                  "name": "demo.ui-tour",
                  "description": "Walk the full ctx.ui surface",
                  "source": "extension",
                  "sourceInfo": { "path": "C:/Repo/extensions/demo-extension/index.ts",
                                  "scope": "project", "baseDir": "C:/Repo/extensions/demo-extension" }
                },
                {
                  "name": "review",
                  "description": "审查当前改动的提示词模板",
                  "source": "prompt",
                  "sourceInfo": { "path": "C:/Repo/.pi/prompts/review.md" }
                },
                { "name": "skill:wechat-article", "source": "skill" }
              ]
            }
            """);
        var commands = ExtensionCatalog.ParseCommands(fixture.RootElement, out _);
        var loaded = ExtensionCatalog.GroupLoadedExtensions(commands,
            new[]
            {
                new ExtensionErrorEntry(DateTimeOffset.Now.AddMinutes(-5),
                    "C:/Repo/extensions/demo-extension/index.ts", "command",
                    "tool call timed out after 15s"),
            });
        return new ExtensionCatalogSnapshot
        {
            LoadedExtensions = loaded,
            Commands = commands,
            DiscoveredExtensions =
            [
                new DiscoveredExtension(@"C:\Repo\.pi\extensions\local-tool", "project", false),
                new DiscoveredExtension(@"C:\AppData\pi-agent\extensions\global-tool.ts", "global", false),
            ],
            ApprovalAvailability = ApprovalAvailability.Verified,
            ApprovalDiagnostic = null,
            QueriedAt = DateTimeOffset.Now,
        };
    }

    // ── H3 realpi：真实扩展目录、demo.ui-tour 四类弹窗、安全重启、加载失败 ──
    private static async Task TestRealPiExtensionsAsync()
    {
        Console.WriteLine("[扩展管理 realpi]");
        string node;
        string cli;
        try
        {
            (node, cli) = RealSessionBootstrap.ResolveRuntime(
                new AppOptions(AppSessionMode.Real, null, null, null, null));
        }
        catch (FileNotFoundException ex)
        {
            Console.WriteLine("  SKIP: " + ex.Message);
            return;
        }

        var repoRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var root = Path.Combine(Path.GetTempPath(), "fufucode-ext-" + Guid.NewGuid().ToString("N")[..8]);
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        var layout = new AppDataLayout(Path.Combine(root, "appdata"));

        try
        {
            // 1) 真实扩展目录：fufucode-bridge 已加载、命令含 status、路径与实际目录一致
            var host = await RealSessionHost.OpenAsync(
                new AppOptions(AppSessionMode.Real, project, null, null, null),
                CancellationToken.None, layout);
            var catalog = new ExtensionCatalog(host.Current.Coordinator, host.ProjectPath,
                host.AgentDirectory);
            var snapshot = await catalog.QueryAsync();
            var bridge = snapshot.LoadedExtensions.FirstOrDefault(e => e.IsApprovalBridge);
            var expectedBridgeDir = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(node)!, "..", "..", "extensions", "fufucode-bridge"));
            var pathMatches = bridge is not null &&
                Path.GetFullPath(bridge.Path).TrimEnd(Path.DirectorySeparatorChar)
                    .Equals(expectedBridgeDir.TrimEnd(Path.DirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase);
            if (snapshot.QueryError is null && bridge is not null &&
                bridge.Commands.Any(c => c.Name == "fufucode-bridge.status") && pathMatches)
            {
                Pass($"扩展目录：fufucode-bridge 已加载且命令/路径一致（{bridge.Path}）");
            }
            else
            {
                Fail($"扩展目录异常：error={snapshot.QueryError} bridge={bridge?.Path} " +
                     $"expected={expectedBridgeDir}");
            }

            // 2) 安全重启真实链路：同一 session id、历史不减、草稿保留、目录重新可查
            var sessionId = host.Current.Session.Id;
            var historyBefore = host.Current.History.CurrentBranchMessages.Count;
            // 草稿：窗口路径会先把输入框草稿落盘再重启，这里等价地先写持久层再验证恢复。
            const string restartDraft = "h3-restart-draft-保留";
            host.SaveDraft(restartDraft);
            await host.RestartCurrentAsync();
            if (host.Current.Session.Id == sessionId &&
                host.Chat.Connection == SessionConnection.Connected)
            {
                Pass("安全重启：同一 session id 且重新连接");
            }
            else
            {
                Fail($"重启异常：id={host.Current.Session.Id} conn={host.Chat.Connection}");
            }

            var historyAfter = host.Current.History.CurrentBranchMessages.Count;
            if (historyAfter >= historyBefore)
            {
                Pass($"重启后历史不减（{historyBefore} → {historyAfter}）");
            }
            else
            {
                Fail($"重启后历史减少：{historyBefore} → {historyAfter}");
            }

            var draftAfter = host.LoadDraft();
            if (draftAfter == restartDraft)
            {
                Pass("重启后草稿保留");
            }
            else
            {
                Fail($"重启后草稿丢失：{draftAfter ?? "null"}");
            }

            var catalog2 = new ExtensionCatalog(host.Current.Coordinator, host.ProjectPath,
                host.AgentDirectory);
            var snapshot2 = await catalog2.QueryAsync();
            if (snapshot2.QueryError is null && snapshot2.Commands.Count > 0)
            {
                Pass("重启后扩展目录重新查询成功");
            }
            else
            {
                Fail($"重启后目录查询失败：{snapshot2.QueryError}");
            }

            // 3) 产品路径启用 demo 扩展（“添加→启用→应用并重启”对应
            //    ApplyExtensionsAndRestartAsync，demo 位于项目外的仓库目录）
            var demoPath = Path.GetFullPath(Path.Combine(repoRoot, "extensions", "demo-extension"));
            var historyPreApply = host.Current.History.CurrentBranchMessages.Count;
            var applyDemo = await host.ApplyExtensionsAndRestartAsync([demoPath]);
            var catalogAfterEnable = new ExtensionCatalog(host.Current.Coordinator,
                host.ProjectPath, host.AgentDirectory);
            var snapshot3 = await catalogAfterEnable.QueryAsync();
            var demoLoaded = snapshot3.LoadedExtensions.FirstOrDefault(e =>
                e.Path.Equals(demoPath, StringComparison.OrdinalIgnoreCase));
            var demoCmd = snapshot3.Commands.Any(c => c.Name == "demo.ui-tour");
            if (applyDemo is null && host.HasLiveSession &&
                host.Current.Session.Id == sessionId &&
                host.Current.History.CurrentBranchMessages.Count >= historyPreApply &&
                host.ActiveExtensionPaths.Contains(demoPath) &&
                host.EnabledExtensionPaths.Contains(demoPath) &&
                host.Chat.Connection == SessionConnection.Connected &&
                snapshot3.ApprovalAvailability == ApprovalAvailability.Verified &&
                demoLoaded is not null && demoCmd)
            {
                Pass("启用 demo 扩展：同 session id、历史不减、审批桥仍 Verified、命令已注册");
            }
            else
            {
                Fail($"启用 demo 异常：failure={applyDemo} live={host.HasLiveSession} " +
                     $"id={host.Current.Session.Id} cmd={demoCmd} approval={snapshot3.ApprovalAvailability}");
            }

            // 4) demo.ui-tour 四类弹窗经 App 应答路径（ExtensionUiDialogHost）
            var demoChat = host.Chat;
            var demoTimeline = new TimelineViewModel(demoChat, SynchronousRefreshScheduler.Instance,
                new RecordingClipboardService());
            var demoStatusBar = new StatusBarViewModel(demoChat);
            var demoInput = new InputViewModel(demoChat);
            var dialogHost = new ExtensionUiDialogHost(owner: null);
            dialogHost.Attach(demoChat);
            SessionUiWiring.AttachExtensionUi(demoChat, demoTimeline, demoInput, demoStatusBar);
            dialogHost.AutoResponder = request => request.Method switch
            {
                "select" => PiExtensionUiResponse.WithValue("banana"),
                "confirm" => PiExtensionUiResponse.Confirm(true),
                "input" => PiExtensionUiResponse.WithValue("fufucode"),
                "editor" => PiExtensionUiResponse.WithValue("edited by selftest"),
                _ => PiExtensionUiResponse.Cancel(),
            };

            string? demoSendFailure = null;
            demoInput.OperationFailed += message => demoSendFailure = message;
            var resultFile = Path.Combine(project, "ui-tour-result.json");
            if (File.Exists(resultFile))
            {
                File.Delete(resultFile);
            }

            demoInput.DraftText = "/demo.ui-tour";
            demoInput.SendCommand.Execute(null);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (DateTime.UtcNow < deadline && !File.Exists(resultFile))
            {
                await Task.Delay(200);
            }

            await DrainUiQueueAsync();
            if (!File.Exists(resultFile))
            {
                Fail($"demo.ui-tour 未产出结果文件（sendFailure={demoSendFailure}）");
            }
            else
            {
                var result = System.Text.Json.JsonDocument.Parse(
                    File.ReadAllText(resultFile)).RootElement;
                var select = result.TryGetProperty("select", out var s) ? s.GetString() : null;
                var confirm = result.TryGetProperty("confirm", out var c) &&
                              c.ValueKind == System.Text.Json.JsonValueKind.True;
                var inputText = result.TryGetProperty("input", out var i) ? i.GetString() : null;
                var editor = result.TryGetProperty("editor", out var e) ? e.GetString() : null;
                if (select == "banana" && confirm && inputText == "fufucode" &&
                    editor == "edited by selftest" && dialogHost.AutoAnsweredCount >= 4)
                {
                    Pass("demo.ui-tour：select/confirm/input/editor 四类弹窗经 App 应答路径完成" +
                         $"（应答 {dialogHost.AutoAnsweredCount} 次）");
                }
                else
                {
                    Fail($"demo.ui-tour 结果异常：select={select} confirm={confirm} " +
                         $"input={inputText} editor={editor} answered={dialogHost.AutoAnsweredCount}");
                }
            }

            dialogHost.Detach();

            // 5) 停用 demo → 应用重启 → 命令消失，行状态等价于未启用（不在 active/pending）
            var applyOff = await host.ApplyExtensionsAndRestartAsync([]);
            var catalogOff = new ExtensionCatalog(host.Current.Coordinator, host.ProjectPath,
                host.AgentDirectory);
            var snapshotOff = await catalogOff.QueryAsync();
            var demoGone = !snapshotOff.Commands.Any(c => c.Name == "demo.ui-tour");
            if (applyOff is null && demoGone &&
                !host.ActiveExtensionPaths.Any() && !host.EnabledExtensionPaths.Any() &&
                host.Current.Session.Id == sessionId)
            {
                Pass("停用 demo 扩展：重启后 demo.ui-tour 消失、启用列表清空");
            }
            else
            {
                Fail($"停用异常：failure={applyOff} cmdGone={demoGone} " +
                     $"active={host.ActiveExtensionPaths.Count}");
            }

            // 6) 启用加载期抛错的扩展 → UserExtensionLoadException(PiStartupFailure)
            //    → 回滚旧列表、会话恢复可用、持久列表不变、错误持久化。
            var brokenExt = Path.Combine(root, "broken-ext");
            Directory.CreateDirectory(brokenExt);
            File.WriteAllText(Path.Combine(brokenExt, "index.ts"),
                "export default function() { throw new Error(\"fufucode-selftest-broken\"); }");
            var applyBroken = await host.ApplyExtensionsAndRestartAsync([brokenExt]);
            var catalogAfterBroken = new ExtensionCatalog(host.Current.Coordinator,
                host.ProjectPath, host.AgentDirectory);
            var snapshotBroken = await catalogAfterBroken.QueryAsync();
            var errorRecorded = host.ExtensionLoadErrors.Any(pair =>
                RealSessionHost.PathEquals(pair.Key, brokenExt) &&
                pair.Value.Contains("fufucode-selftest-broken"));

            // 断言：失败类型/错误文本/回滚后可用/持久列表未变/错误已持久化。
            if (applyBroken is { Kind: "加载失败" } &&
                applyBroken.Message.Contains("fufucode-selftest-broken") &&
                host.HasLiveSession && snapshotBroken.QueryError is null &&
                host.EnabledExtensionPaths.Count == 0 && errorRecorded &&
                snapshotBroken.ApprovalAvailability == ApprovalAvailability.Verified)
            {
                Pass("启用坏扩展：PiStartupFailure 回滚、会话恢复、持久列表未变、桥仍 Verified");
            }
            else
            {
                Fail($"坏扩展回滚异常：failure={applyBroken} live={host.HasLiveSession} " +
                     $"qerr={snapshotBroken.QueryError} enabled={host.EnabledExtensionPaths.Count} " +
                     $"errRecorded={errorRecorded}");
            }

            // 7) 项目设为 DoNotTrust 后启用项目内扩展 → ProjectExtensionNotTrustedException → 回滚
            var projExt = Path.Combine(project, ".pi", "extensions", "proj-ext");
            Directory.CreateDirectory(projExt);
            File.WriteAllText(Path.Combine(projExt, "index.ts"),
                "export default function() {}");
            host.Manager.SetProjectTrustDecision(host.ProjectPath, ProjectTrustDecision.DoNotTrust);
            var applyUntrusted = await host.ApplyExtensionsAndRestartAsync([projExt]);
            var catalogUntrusted = new ExtensionCatalog(host.Current.Coordinator,
                host.ProjectPath, host.AgentDirectory);
            var snapshotUntrusted = await catalogUntrusted.QueryAsync();
            if (applyUntrusted is { Kind: "需要信任项目" } &&
                host.HasLiveSession && snapshotUntrusted.QueryError is null)
            {
                Pass("项目未信任时启用项目内扩展：NotTrusted 回滚、会话恢复");
            }
            else
            {
                Fail($"未信任回滚异常：failure={applyUntrusted} live={host.HasLiveSession}");
            }

            var hostProjectId = host.Current.Project.Id;
            await host.DisposeAsync();

            // 8) 持久启用列表含已删除路径 → 打开成功且被跳过（SkippedMissingExtensionPaths）
            var gonePath = Path.Combine(root, "deleted-ext");
            host.Manager.Metadata.SetPreference(
                ExtensionPreferences.EnabledKey(hostProjectId),
                System.Text.Json.JsonSerializer.Serialize(new[] { gonePath }));
            var host2 = await RealSessionHost.OpenAsync(
                new AppOptions(AppSessionMode.Real, project, null, null, null),
                CancellationToken.None, layout);
            if (host2.SkippedMissingExtensionPaths.Any(p =>
                    RealSessionHost.PathEquals(p, gonePath)) &&
                host2.Chat.Connection == SessionConnection.Connected &&
                host2.ActiveExtensionPaths.Count == 0)
            {
                Pass("缺失路径：打开成功、被过滤、SkippedMissingExtensionPaths 可见");
            }
            else
            {
                Fail($"缺失路径异常：skipped={host2.SkippedMissingExtensionPaths.Count} " +
                     $"conn={host2.Chat.Connection}");
            }

            await host2.DisposeAsync();
        }
        catch (Exception ex)
        {
            Fail("扩展管理 realpi 异常：" + ex);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    // ── 辅助 ──

    private static async Task WaitForSettledAsync(IChatSession session)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            // 阶段一：等到运行真正开始（Task.Run 可能尚未调度，此时 Activity 仍是 Idle，直接退出会漏掉整轮）
            var startWatch = Stopwatch.StartNew();
            while (session.Activity == SessionActivity.Idle && startWatch.Elapsed < TimeSpan.FromSeconds(3))
            {
                await Task.Delay(30, cts.Token);
            }

            // 阶段二：等到运行结束
            while (session.Activity != SessionActivity.Idle)
            {
                cts.Token.ThrowIfCancellationRequested();
                await Task.Delay(50, cts.Token);
            }

            // 阶段三：排空 UI 队列。后台线程先把终态事件（MessageFinished/TurnSettled）
            // Post 到 UI 线程、最后才把 Activity 置为 Idle，因此"刚读到 Idle"时队列里可能
            // 还压着未处理的回调，此时去查时间线会看到缺失的助手消息（曾出现的偶发失败）。
            // 用 Background 优先级让出一次（实测值 -2，低于事件 Post 默认的 Default=0）：
            // dispatcher 按优先级出队，所有已入队的更高优先级回调必然先执行完，再回到断言。
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
            Fail("等待回合结束超时（20s）");
        }
    }

    // ── H2 视觉基座截图：1280×800 PNG 落到临时证据目录，作为人工视觉审查证据 ──
    private static async Task TestVisualShotsAsync()
    {
        Console.WriteLine("[H2 视觉截图]");
        var shotsDir = EvidenceDirectory("h2-shots");

        var previousTheme = Application.Current!.RequestedThemeVariant;
        try
        {
            // 主窗口：富内容 + 排队条（mock 会话置为运行中）
            foreach (var (variant, tag) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                Application.Current!.RequestedThemeVariant = variant;
                var window = new MainWindow { Width = 1280, Height = 800 };
                window.Show();
                await Task.Delay(900);
                var vm = (MainViewModel)window.DataContext!;
                PopulateRichTimeline(vm);
                await Task.Delay(300);
                await DrainUiQueueAsync();

                SaveShot(window, Path.Combine(shotsDir, $"main-{tag}.png"));

                if (tag == "light")
                {
                    CheckBlockSpacing(window);
                }

                // 滚动到底：Running/Failed 工具卡、错误卡、中性「已中断」卡都在尾部
                var chatPage = window.GetVisualDescendants().OfType<Views.ChatPage>().FirstOrDefault();
                if (chatPage is null)
                {
                    Fail("截图：找不到 ChatPage 视图");
                }
                else
                {
                    chatPage.ScrollToEnd();
                    await Task.Delay(300);
                    await DrainUiQueueAsync();
                    SaveShot(window, Path.Combine(shotsDir, $"main-{tag}-bottom.png"));
                }

                // 扩展页空状态（两种主题）
                vm.CurrentPage = AppPage.Extensions;
                await Task.Delay(400);
                SaveShot(window, Path.Combine(shotsDir, $"extensions-{tag}.png"));
                vm.CurrentPage = AppPage.Chat;

                window.Close();
            }

            // 信任对话框（两种主题）
            foreach (var (variant, tag) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                Application.Current!.RequestedThemeVariant = variant;
                var trust = new ProjectTrustDialog(new ProjectTrustStatus(
                    @"C:\Projects\DemoRepo",
                    null,
                    new[]
                    {
                        @"C:\Projects\DemoRepo\.pi\settings.json",
                        @"C:\Projects\DemoRepo\.pi\skills\review.md",
                        @"C:\Projects\DemoRepo\AGENTS.md",
                    }));
                trust.Show();
                await Task.Delay(400);
                SaveShot(trust, Path.Combine(shotsDir, $"trust-dialog-{tag}.png"));
                trust.Close();
            }

            // 扩展 confirm 对话框（两种主题）
            foreach (var (variant, tag) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                Application.Current!.RequestedThemeVariant = variant;
                var payload = System.Text.Json.JsonDocument.Parse(
                    """{"title":"扩展请求确认","message":"扩展 demo-ext 请求执行命令 `dotnet test`，是否允许？"}""");
                var confirm = new ExtensionUiDialogWindow(
                    new ExtensionUiRequest(Guid.NewGuid(), "req-shot", "confirm",
                        payload.RootElement, DateTimeOffset.Now, TimeSpan.FromMinutes(1)),
                    (_, _) => Task.CompletedTask);
                confirm.Show();
                await Task.Delay(400);
                SaveShot(confirm, Path.Combine(shotsDir, $"confirm-dialog-{tag}.png"));
                confirm.Close();
            }

            Pass($"H2 截图已输出到 {shotsDir}");
        }
        catch (Exception ex)
        {
            Fail("视觉截图生成异常：" + ex.GetType().Name + " " + ex.Message);
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = previousTheme;
        }
    }

    /// <summary>往 mock 时间线追加截图所需的富内容：用户/助手消息（含思考+代码块）、
    /// 三种终态工具卡、中性已中断、Info 提示、Error 条目、排队条。</summary>
    private static void PopulateRichTimeline(MainViewModel vm)
    {
        var now = DateTimeOffset.Now;
        vm.Timeline.Reset();

        vm.Timeline.Entries.Add(new UserMessageViewModel
        {
            Text = "帮我审查一下构建脚本，看看有没有可以简化的地方。",
            Timestamp = now.AddMinutes(-3),
        });

        var assistant = new AssistantMessageViewModel(new RecordingClipboardService())
        {
            Timestamp = now.AddMinutes(-2),
        };
        assistant.UpdateFrom(new ChatMessage(
            "shot-a1",
            MessageRole.Assistant,
            "mock-model",
            new MessageBlock[]
            {
                new Fufucode.App.Abstractions.ThinkingBlock("先列出仓库里的构建入口，再对比每一步的耗时……"),
                new AbstractionsTextBlock(
                    "构建入口只有一个 `build.ps1`，可以这样跑一遍验证：\n\n```powershell\ndotnet restore Fufucode.sln\ndotnet build Fufucode.sln -nologo -c Release\n```\n\n主要问题有两个：打包脚本重复解析了参数，发布步骤没有区分 RID。"),
                new ToolCallBlock("t1", "read_file", "{\"path\": \"scripts/build.ps1\"}",
                    ToolCallStatus.Completed, "已完成",
                    "# build.ps1\nparam($Configuration)\ndotnet build Fufucode.sln -c $Configuration", false),
                new ToolCallBlock("t2", "run_command", "{\"command\": \"dotnet build Fufucode.sln\"}",
                    ToolCallStatus.Running, "运行中", null, false),
                new ToolCallBlock("t3", "apply_patch", "{\"file\": \"scripts/pack.ps1\"}",
                    ToolCallStatus.Failed, "失败", "patch does not apply cleanly at line 42", true),
            },
            null,
            true));
        vm.Timeline.Entries.Add(assistant);

        // 中性「已中断」条目（H1 停止窗口语义）
        var stopped = new ErrorEntryViewModel { Timestamp = now.AddMinutes(-1) };
        stopped.UpdateFrom(new Streaming.ErrorEntry(SessionErrorKind.UserCancelled, "Request aborted", null));
        vm.Timeline.Entries.Add(stopped);

        // Info 提示 + Error 条目
        vm.Timeline.AddNotification("已恢复上次会话的上下文（3 条消息）。");
        var error = new ErrorEntryViewModel { Timestamp = now.AddSeconds(-30) };
        error.UpdateFrom(new Streaming.ErrorEntry(SessionErrorKind.ProtocolError, "收到无法识别的消息帧", "req-7"));
        vm.Timeline.Entries.Add(error);

        // 排队条 + 运行中 chip / 发送按钮「排队」文案
        if (vm.Session is MockChatSession mock)
        {
            mock.SimulateRunningQueue(new[] { "接着把打包脚本改成按 RID 分支", "跑一遍 dotnet test 验证" });
        }
    }

    /// <summary>布局断言：段落文本底边与相邻代码块卡片顶边的间距 ≤16px（回归 H2 复审 A）。</summary>
    private static void CheckBlockSpacing(Window window)
    {
        // 代码块卡片在 Segments ItemsControl 里与段落项是同级 ContentPresenter 兄弟，
        // 两者 Bounds 同坐标系，直接相减即得可视间距。
        var codeCard = window.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("codeCard"));
        var presenter = codeCard?.GetVisualAncestors().OfType<Avalonia.Controls.Presenters.ContentPresenter>().FirstOrDefault();
        var panel = presenter?.GetVisualParent() as Panel;
        if (codeCard is null || presenter is null || panel is null)
        {
            var borders = window.GetVisualDescendants().OfType<Border>().ToArray();
            Fail($"块间距检查：找不到代码块卡片或其同级容器 " +
                 $"(Border数={borders.Length}, codeCard={(codeCard is not null)}, presenter={(presenter is not null)}, " +
                 $"panel类型={(presenter?.Parent?.GetType().Name ?? "无")})");
            return;
        }

        var siblings = panel.Children;
        var index = siblings.IndexOf(presenter);
        if (index <= 0)
        {
            Fail("块间距检查：代码块前方没有段落兄弟");
            return;
        }

        static double TopIn(Visual v, Visual panel) =>
            v.TranslatePoint(new Point(0, 0), panel)?.Y ?? 0;
        static double BottomIn(Visual v, Visual panel) =>
            v.TranslatePoint(new Point(0, v.Bounds.Height), panel)?.Y ?? 0;
        // 取 ContentPresenter 的实际内容元素量起（内部的 Border/TextBlock 边距也计入）
        static Visual Content(Control c) => (c as Avalonia.Controls.Presenters.ContentPresenter)?.Child ?? c;

        var cardTop = TopIn(codeCard, panel);
        var cardBottom = BottomIn(codeCard, panel);
        var previous = Content((Control)siblings[index - 1]);
        var gap = cardTop - BottomIn(previous, panel);
        if (gap <= 16 && gap >= 0)
        {
            Pass($"段落与代码块间距 {gap:F0}px ≤ 16px");
        }
        else
        {
            Fail($"段落与代码块间距 {gap:F0}px，超过 16px");
        }

        // 代码块下方到后续段落的间距同样受控
        if (index + 1 < siblings.Count)
        {
            var next = Content((Control)siblings[index + 1]);
            var nextGap = TopIn(next, panel) - cardBottom;
            if (nextGap <= 16 && nextGap >= 0)
            {
                Pass($"代码块与后续段落间距 {nextGap:F0}px ≤ 16px");
            }
            else
            {
                Fail($"代码块与后续段落间距 {nextGap:F0}px，超过 16px");
            }
        }
    }

    private static void SaveShot(Window window, string path)
    {
        var frame = window.CaptureRenderedFrame();
        if (frame is null)
        {
            Fail("无头模式未捕获到渲染帧：" + Path.GetFileName(path));
            return;
        }

        frame.Save(path, PngBitmapEncoderOptions.Default);
        var size = new FileInfo(path).Length;
        Console.WriteLine($"  信息：截图 {path}（{size} 字节）");
        if (size < 5000)
        {
            Fail("截图疑似空白：" + Path.GetFileName(path));
        }
        else
        {
            Pass("已渲染 " + Path.GetFileName(path));
        }
    }

    // ── W2/M3.1：项目选择 ──

    /// <summary>ProjectPreferences：规范化去重/最近在前/大小写不敏感/上限 10。</summary>
    private static void TestProjectPreferences()
    {
        Console.WriteLine("[最近项目偏好]");
        var root = Path.Combine(Path.GetTempPath(),
            "fufucode-projpref-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var metadata = new DesktopMetadataStore(new AppDataLayout(Path.Combine(root, "data")));
            var p1 = Directory.CreateDirectory(Path.Combine(root, "p1")).FullName;
            var p2 = Directory.CreateDirectory(Path.Combine(root, "p2")).FullName;

            ProjectPreferences.RecordOpened(metadata, p1);
            ProjectPreferences.RecordOpened(metadata, p2);
            // 大小写不同视为同一项目（Windows 去重）
            ProjectPreferences.RecordOpened(metadata, p1.ToUpperInvariant());
            var list = ProjectPreferences.LoadRecent(metadata);
            if (list.Count == 2 && RealSessionHost.PathEquals(list[0], p1) &&
                RealSessionHost.PathEquals(list[1], p2))
            {
                Pass("最近项目去重/排序/大小写不敏感");
            }
            else
            {
                Fail("最近项目异常：" + string.Join(" | ", list));
            }

            // 上限 10
            for (var i = 0; i < 15; i++)
            {
                var extra = Directory.CreateDirectory(Path.Combine(root, "x" + i)).FullName;
                ProjectPreferences.RecordOpened(metadata, extra);
            }

            if (ProjectPreferences.LoadRecent(metadata).Count == ProjectPreferences.MaxRecent)
            {
                Pass("最近项目上限 10");
            }
            else
            {
                Fail("最近项目未封顶：" + ProjectPreferences.LoadRecent(metadata).Count);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        // ResolveStartupProject 四种情形（纯函数）
        var exists = new Func<string, bool>(p => p.Contains("yes"));
        if (ProjectPreferences.ResolveStartupProject("C:/explicit", ["C:/yes"], exists) == "C:/explicit" &&
            ProjectPreferences.ResolveStartupProject(null, ["C:/no", "C:/yes"], exists) == "C:/yes" &&
            ProjectPreferences.ResolveStartupProject(null, ["C:/no"], exists) is null &&
            ProjectPreferences.ResolveStartupProject(null, [], exists) is null)
        {
            Pass("ResolveStartupProject：显式优先/最近存在回退/全缺失与无最近为 null");
        }
        else
        {
            Fail("ResolveStartupProject 判定异常");
        }
    }

    /// <summary>
    /// 无项目启动遮罩“选择项目”态：无可用最近项目时显示文案 + “打开项目文件夹…”按钮；
    /// 有存在的最近项目时直接解析并打开（OpenHostForTesting 被调一次，不弹选择态）。
    /// </summary>
    private static async Task TestProjectPickerAsync()
    {
        Console.WriteLine("[项目选择启动态]");
        var root = Path.Combine(Path.GetTempPath(),
            "fufucode-projpick-" + Guid.NewGuid().ToString("N")[..8]);
        var dataRoot = Path.Combine(root, "appdata");
        var projA = Path.Combine(root, "项目 甲");
        Directory.CreateDirectory(projA);
        try
        {
            // 情形 1：无最近项目 → 选择项目态
            var window = new MainWindow(new AppOptions(
                AppSessionMode.Real, null, null, null, null, dataRoot));
            window.OpenHostForTesting = _ => new TaskCompletionSource<RealSessionHost>().Task;
            window.Show();
            await Task.Delay(500);

            var pickPanel = window.FindControl<StackPanel>("StartupPickProject");
            var openButton = window.FindControl<Button>("StartupOpenProjectButton");
            var overlay = window.FindControl<Border>("StartupOverlay")!;
            var text = window.FindControl<Avalonia.Controls.TextBlock>("StartupText")!;
            var recents = window.FindControl<ItemsControl>("StartupRecentProjects");
            if (overlay.IsVisible && pickPanel?.IsVisible == true &&
                openButton?.IsVisible == true &&
                text.Text == "选择一个项目文件夹开始" &&
                recents?.ItemsSource is IEnumerable<string> { } empty && !empty.Any())
            {
                Pass("无项目启动遮罩：选择项目态渲染（主按钮可见、最近列表为空）");
            }
            else
            {
                Fail($"选择项目态异常：overlay={overlay.IsVisible} pick={pickPanel?.IsVisible} " +
                     $"btn={openButton?.IsVisible} text=\"{text.Text}\"");
            }

            window.Close();
            await Task.Delay(300);

            // 情形 2：最近项目存在 → 直接解析，不经选择态
            var metadata = new DesktopMetadataStore(new AppDataLayout(dataRoot));
            ProjectPreferences.RecordOpened(metadata, projA);
            var window2 = new MainWindow(new AppOptions(
                AppSessionMode.Real, null, null, null, null, dataRoot));
            var opened = 0;
            window2.OpenHostForTesting = _ =>
            {
                opened++;
                return new TaskCompletionSource<RealSessionHost>().Task;
            };
            window2.Show();
            await Task.Delay(600);

            var pick2 = window2.FindControl<StackPanel>("StartupPickProject");
            if (opened == 1 && pick2?.IsVisible != true &&
                window2.OptionsForTesting.ProjectPath is { } resolved &&
                RealSessionHost.PathEquals(resolved, projA))
            {
                Pass("最近项目存在时直接解析打开（不经选择项目态）");
            }
            else
            {
                Fail($"最近项目回退异常：opened={opened} pick={pick2?.IsVisible} " +
                     $"path={window2.OptionsForTesting.ProjectPath}");
            }

            window2.Close();

            // 情形 3：打开失败 → 失败态显示“打开其他项目…”，点击进入选择态且隐藏重试
            var window3 = new MainWindow(new AppOptions(
                AppSessionMode.Real, projA, null, null, null, dataRoot));
            window3.OpenHostForTesting = _ => Task.FromException<RealSessionHost>(
                new InvalidOperationException("selftest-open-fail"));
            window3.Show();
            await Task.Delay(600);

            var overlay3 = window3.FindControl<Border>("StartupOverlay")!;
            var openOther3 = window3.FindControl<Button>("StartupOpenOtherButton");
            var retry3 = window3.FindControl<Button>("StartupRetryButton");
            if (overlay3.IsVisible && openOther3?.IsVisible == true && retry3?.IsVisible == true)
            {
                Pass("启动失败态：重试 + 打开其他项目… 均可见");
            }
            else
            {
                Fail($"失败态按钮异常：overlay={overlay3.IsVisible} openOther={openOther3?.IsVisible} retry={retry3?.IsVisible}");
            }

            openOther3!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(300);
            var pick3 = window3.FindControl<StackPanel>("StartupPickProject");
            var recents3 = window3.FindControl<ItemsControl>("StartupRecentProjects");
            var hasA = recents3?.ItemsSource is IEnumerable<string> r3 &&
                       r3.Any(p => RealSessionHost.PathEquals(p, projA));
            if (pick3?.IsVisible == true && openOther3.IsVisible == false &&
                retry3?.IsVisible == false && hasA)
            {
                Pass("打开其他项目…→选择项目态：重试隐藏、最近列表含失败项目");
            }
            else
            {
                Fail($"转入选择态异常：pick={pick3?.IsVisible} openOther={openOther3.IsVisible} " +
                     $"retry={retry3?.IsVisible} hasA={hasA}");
            }

            window3.Close();
            await Task.Delay(300);

            // 情形 4：信任放弃 → 同一“打开其他项目…”入口
            var window4 = new MainWindow(new AppOptions(
                AppSessionMode.Real, projA, null, null, null, dataRoot));
            window4.OpenHostForTesting = _ => Task.FromException<RealSessionHost>(
                new ProjectTrustDecisionRequiredException(
                    new ProjectTrustStatus(projA, null, ["AGENTS.md"])));
            window4.AskTrustForTesting = _ => Task.FromResult<ProjectTrustDecision?>(null);
            window4.Show();
            await Task.Delay(600);

            var openOther4 = window4.FindControl<Button>("StartupOpenOtherButton");
            var text4 = window4.FindControl<Avalonia.Controls.TextBlock>("StartupText");
            if (openOther4?.IsVisible == true &&
                text4?.Text?.Contains("未作出信任决定") == true)
            {
                openOther4.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(300);
                var pick4 = window4.FindControl<StackPanel>("StartupPickProject");
                var retry4 = window4.FindControl<Button>("StartupRetryButton");
                if (pick4?.IsVisible == true && retry4?.IsVisible == false &&
                    openOther4.IsVisible == false)
                {
                    Pass("信任放弃态：打开其他项目…→选择项目态、重试隐藏");
                }
                else
                {
                    Fail($"信任放弃转入异常：pick={pick4?.IsVisible} retry={retry4?.IsVisible} openOther={openOther4.IsVisible}");
                }
            }
            else
            {
                Fail($"信任放弃态异常：openOther={openOther4?.IsVisible} text=\"{text4?.Text}\"");
            }

            window4.Close();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>扩展行“移除”改为确认 Flyout：行按钮无直接 Command，确认按钮才执行删除。</summary>
    private static async Task TestRemoveExtensionConfirmAsync()
    {
        Console.WriteLine("[扩展移除确认]");
        var root = Path.Combine(Path.GetTempPath(),
            "fufucode-rmext-" + Guid.NewGuid().ToString("N")[..8]);
        var extManual = Path.Combine(root, "ext-manual");
        Directory.CreateDirectory(extManual);
        File.WriteAllText(Path.Combine(extManual, "index.ts"), "export default function() {}");
        try
        {
            var session = new StubExtensionUiSession();
            var vm = new ExtensionsPageViewModel();
            var addedStore = new List<string> { extManual };
            var manage = new ExtensionManageContext(
                ActivePaths: [], EnabledPaths: [], AddedPaths: [extManual],
                MissingPaths: [], LoadErrors: new Dictionary<string, string>(),
                ApplyAsync: (_, _) => Task.FromResult<ExtensionApplyFailure?>(null),
                PickExtensionAsync: null,
                AddExtension: p => addedStore.Add(p),
                RemoveAddedExtension: p => addedStore.Remove(p));
            vm.AttachRealSession(null, session, root, root,
                _ => Task.CompletedTask,
                (ProjectTrustStatus _) => Task.FromResult<ProjectTrustDecision?>(null),
                (_, _) => { }, () => null, _ => { },
                manage: manage);
            vm.PreviewSnapshot(new ExtensionCatalogSnapshot());
            await DrainUiQueueAsync();

            var page = new Views.ExtensionsPage { DataContext = vm };
            var window = new Window { Content = page, Width = 900, Height = 700 };
            window.Show();
            await Task.Delay(600);
            await DrainUiQueueAsync();

            var removeButtons = window.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Name == "RemoveExtensionButton").ToArray();
            if (removeButtons.Length == 0)
            {
                Fail("找不到移除按钮（行模板未实现或名称不符）");
            }
            else
            {
                var remove = removeButtons[0];
                // 确认前不删除：行按钮本身不带删除命令，删除命令在 Flyout 内的确认按钮上。
                if (remove.Command is null && remove.Flyout is Flyout)
                {
                    Pass("移除改为确认 Flyout：行按钮不直接删除");
                }
                else
                {
                    Fail($"移除结构异常：cmd={remove.Command != null} flyout={remove.Flyout != null}");
                }

                // 弹出确认层：Flyout 内容在打开时才继承 DataContext。
                ((Flyout)remove.Flyout!).ShowAt(remove);
                await Task.Delay(400);
                await DrainUiQueueAsync();
                Button? confirm = window.GetVisualDescendants().OfType<Button>()
                    .FirstOrDefault(b => b.Name == "ConfirmRemoveButton");
                if (confirm is null && (remove.Flyout as Flyout)?.Content is Control flyoutContent)
                {
                    confirm = flyoutContent.GetLogicalDescendants().OfType<Button>()
                        .FirstOrDefault(b => b.Name == "ConfirmRemoveButton");
                }
                if (confirm?.Command is not null)
                {
                    Pass("确认 Flyout 内含独立确认按钮（绑定删除命令）");
                }
                else
                {
                    Fail($"确认按钮异常：found={confirm != null} cmd={confirm?.Command != null}");
                }

                // 执行确认命令 → 真正删除
                confirm?.Command?.Execute(null);
                await DrainUiQueueAsync();
                if (!addedStore.Contains(extManual) &&
                    vm.Extensions.All(r => r.Path != extManual))
                {
                    Pass("确认后移除手动添加记录");
                }
                else
                {
                    Fail($"确认移除未生效：added={addedStore.Count}");
                }
            }

            window.Close();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// realpi：运行中项目切换（先开后放）。A→B 信任放弃保留 A；A→B→A 会话隔离、
    /// 草稿各自保留；最近项目列表顺序正确。
    /// </summary>
    private static async Task TestRealPiProjectSwitchAsync()
    {
        Console.WriteLine("[项目切换 realpi]");
        string node;
        string cli;
        try
        {
            (node, cli) = RealSessionBootstrap.ResolveRuntime(
                new AppOptions(AppSessionMode.Real, null, null, null, null));
        }
        catch (FileNotFoundException ex)
        {
            Console.WriteLine("  SKIP: " + ex.Message);
            return;
        }

        var root = Path.Combine(Path.GetTempPath(),
            "fufucode-projsw-" + Guid.NewGuid().ToString("N")[..8]);
        var projA = Path.Combine(root, "项目 甲");
        var projB = Path.Combine(root, "项目 乙");
        var appdata = Path.Combine(root, "appdata");
        Directory.CreateDirectory(projA);
        Directory.CreateDirectory(Path.Combine(projB, ".pi"));
        File.WriteAllText(Path.Combine(projB, ".pi", "settings.json"), "{}");
        File.WriteAllText(Path.Combine(projA, "a.txt"), "aaa");

        var window = new MainWindow(new AppOptions(
            AppSessionMode.Real, projA, null, node, cli, appdata));
        try
        {
            window.Show();
            // 等初始打开完成（遮罩隐藏）
            var deadline = DateTime.Now + TimeSpan.FromSeconds(90);
            while (window.FindControl<Border>("StartupOverlay")!.IsVisible &&
                   DateTime.Now < deadline)
            {
                await Task.Delay(300);
            }

            var vm = (MainViewModel)window.DataContext!;
            if (!RealSessionHost.PathEquals(vm.Sidebar.ProjectText, projA))
            {
                Fail($"初始项目异常：{vm.Sidebar.ProjectText}");
                return;
            }

            Pass($"初始打开项目 A（{vm.Sidebar.ProjectText}）");
            var sessionA = vm.Sidebar.Sessions.FirstOrDefault()?.Id;
            vm.Input.DraftText = "草稿-甲";

            // B 信任放弃 → A 保留可用
            window.AskTrustForTesting = _ => Task.FromResult<ProjectTrustDecision?>(null);
            await window.SwitchProjectForTestingAsync(projB);
            await DrainUiQueueAsync();
            vm = (MainViewModel)window.DataContext!;
            var declinedNote = vm.Timeline.Entries.OfType<ErrorEntryViewModel>()
                .LastOrDefault(e => e.Message?.Contains("未切换项目") == true);
            if (RealSessionHost.PathEquals(vm.Sidebar.ProjectText, projA) &&
                declinedNote is not null)
            {
                Pass("B 信任放弃：旧项目 A 保持可用并给出提示");
            }
            else
            {
                Fail($"信任放弃异常：project={vm.Sidebar.ProjectText} note={declinedNote?.Message}");
            }

            // B 信任 → 切换成功，会话/草稿隔离
            window.AskTrustForTesting = _ => Task.FromResult<ProjectTrustDecision?>(ProjectTrustDecision.Trust);
            await window.SwitchProjectForTestingAsync(projB);
            await DrainUiQueueAsync();
            vm = (MainViewModel)window.DataContext!;
            var sessionB = vm.Sidebar.Sessions.FirstOrDefault()?.Id;
            if (RealSessionHost.PathEquals(vm.Sidebar.ProjectText, projB) &&
                sessionB is not null && sessionB != sessionA &&
                vm.Input.DraftText == string.Empty)
            {
                Pass("切换到项目 B：会话不串线、A 草稿不泄漏");
            }
            else
            {
                Fail($"切 B 异常：project={vm.Sidebar.ProjectText} " +
                     $"sessA={sessionA} sessB={sessionB} draft=\"{vm.Input.DraftText}\"");
            }

            vm.Input.DraftText = "草稿-乙";
            // 切回 A：A 的草稿恢复
            await window.SwitchProjectForTestingAsync(projA);
            await DrainUiQueueAsync();
            vm = (MainViewModel)window.DataContext!;
            var sessionA2 = vm.Sidebar.Sessions.FirstOrDefault()?.Id;
            if (RealSessionHost.PathEquals(vm.Sidebar.ProjectText, projA) &&
                sessionA2 == sessionA && vm.Input.DraftText == "草稿-甲")
            {
                Pass("切回项目 A：会话恢复、草稿按会话隔离保留");
            }
            else
            {
                Fail($"切回 A 异常：project={vm.Sidebar.ProjectText} " +
                     $"sess={sessionA2} draft=\"{vm.Input.DraftText}\"");
            }

            // 最近项目顺序：A 最新、B 次之
            var recent = ProjectPreferences.LoadRecent(
                new DesktopMetadataStore(new AppDataLayout(appdata)));
            if (recent.Count >= 2 && RealSessionHost.PathEquals(recent[0], projA) &&
                RealSessionHost.PathEquals(recent[1], projB))
            {
                Pass("最近项目列表顺序正确（最近在前）");
            }
            else
            {
                Fail("最近项目异常：" + string.Join(" | ", recent));
            }
        }
        catch (Exception ex)
        {
            Fail("项目切换 realpi 异常：" + ex.GetBaseException().Message);
        }
        finally
        {
            try { window.Close(); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task RunOnHeadlessUiThreadAsync(Func<Task> action)
    {
        var tcs = new TaskCompletionSource<bool>();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await action();
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                Fail("测试抛出异常：" + ex);
                tcs.SetResult(false);
            }
        }, DispatcherPriority.Normal);

        await tcs.Task;
    }
}
