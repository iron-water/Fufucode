using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Fufucode.App.SelfTest;

// 自测选择与分派集中维护；用例实现不再各自复制失败计数和返回码逻辑。
public static partial class SelfTestRunner
{
    private static async Task<int?> RunSelectedAsync(string[] args)
    {
        if (args.Contains("--loading-ui-repair-only"))
        {
            var code = 1;
            await RunOnHeadlessUiThreadAsync(async () => code = await LoadingUiRepairSelfTest.RunAsync());
            return code;
        }

        Task Ui(Func<Task> test) => RunOnHeadlessUiThreadAsync(test);
        Task Suite(Func<Action<string>, Action<string>, Task> test) => Ui(() => test(Pass, Fail));
        var tests = new Dictionary<string, Func<Task>>(StringComparer.Ordinal)
        {
            ["--session-repair-only"] = () => Ui(async () =>
            {
                await SessionRepairSelfTest.RunAsync(Pass, Fail);
                await TestSteerInputAsync();
                await TestSteerKeyboardAsync();
            }),
            ["--chat-attachments-only"] = () => Ui(() =>
            {
                ChatAttachmentsSelfTest.Run(Pass, Fail);
                return Task.CompletedTask;
            }),
            ["--new-session-project-only"] = () => Suite(NewSessionProjectSelfTest.RunAsync),
            ["--ui-refinement-only"] = () => Ui(() =>
            {
                DockInteractionSelfTest.Run(Pass, Fail);
                return UiRefinementSelfTest.RunAsync(Pass, Fail);
            }),
            ["--timeline-history-only"] = () => Suite(TimelineHistorySelfTest.RunAsync),
            ["--security-only"] = () => Suite(SecurityCenterSelfTest.RunAsync),
            ["--settings-only"] = () => Suite(SettingsDestinationSelfTest.RunAsync),
            ["--archive-only"] = () => Suite(ArchiveSelfTest.RunAsync),
            ["--speech-only"] = () => Suite(SpeechPluginSelfTest.RunAsync),
            ["--cross-global-only"] = () => Suite(CrossGlobalSelfTest.RunAsync),
            ["--plugin-reconciliation-only"] = () => Suite(LocalPluginReconciliationSelfTest.RunAsync),
            ["--local-plugins-only"] = () => Ui(async () =>
            {
                await LocalPluginIntegrationSelfTest.RunAsync(Pass, Fail);
                await LocalPluginReceiptSelfTest.RunAsync(Pass, Fail);
                await LocalPluginInFlightSelfTest.RunAsync(Pass, Fail);
                await LocalPluginCallbackJournalSelfTest.RunAsync(Pass, Fail);
                await LocalPluginReconciliationSelfTest.RunAsync(Pass, Fail);
            }),
            ["--local-plugins-real-only"] = () => Suite(LocalPluginRealSelfTest.RunAsync),
            ["--local-plugins-cancel-real-only"] = () => Suite(LocalPluginCancellationSelfTest.RunAsync),
            ["--notification-page-only"] = () => Ui(async () =>
            {
                await NotificationPageSelfTest.RunAsync(Pass, Fail);
                await ApprovalNotificationSelfTest.RunAsync(Pass, Fail);
            }),
            ["--background-only"] = () => Suite(BackgroundModeSelfTest.RunAsync),
            ["--floating-workbench-only"] = () => Suite(FloatingWorkbenchSelfTest.RunAsync),
            ["--workbench-only"] = () => Suite(WorkbenchDemoSelfTest.RunAsync),
            ["--sessions-only"] = () => Suite(SessionOrganizationSelfTest.RunAsync),
            ["--sessions-realpi-only"] = () => Suite(SessionManagementRealSelfTest.RunAsync),
            ["--tabs-only"] = () => Suite(WorkbenchTabsSelfTest.RunAsync),
            ["--browser-integration-only"] = () => Suite(BrowserIntegrationSelfTest.RunAsync),
            ["--r05-only"] = () => Suite(WorkbenchCustomizationSelfTest.RunAsync),
            ["--skins-integration-only"] = () => Suite(SkinIntegrationSelfTest.RunAsync),
            ["--settings-destination-only"] = () => Suite(SettingsDestinationSelfTest.RunAsync),
            ["--shortcuts-only"] = () => Suite(ShortcutManagerSelfTest.RunAsync),
            ["--steer-keyboard-only"] = () => Ui(TestSteerKeyboardAsync),
            ["--settings-apply-only"] = () => Ui(TestSettingsApplyAsync),
            ["--changes-shots-only"] = () => Suite(ChangesPanelShots.RunAsync),
            ["--event-filter-only"] = () =>
            {
                TestSessionEventFilter();
                return Task.CompletedTask;
            },
            ["--fork-only"] = () => ForkSelfTest.RunAsync(Pass, Fail),
            ["--automation-only"] = RunAutomationSelfTestsAsync,
            ["--r2-only"] = () => RunWave5R2ChecksAsync(args),
            ["--r3-only"] = RunWave5R3ChecksAsync,
            ["--d6-only"] = RunWave5D6ChecksAsync,
            ["--r4-only"] = RunWave5R4ChecksAsync,
        };
        foreach (var (option, test) in tests)
        {
            if (!args.Contains(option)) continue;
            await test();
            Console.WriteLine($"{option}: {Failures.Count} failures");
            return Failures.Count == 0 ? 0 : 1;
        }

        var unknown = args.FirstOrDefault(arg => arg.EndsWith("-only", StringComparison.Ordinal));
        if (unknown is null) return null;
        Console.Error.WriteLine("Unknown self-test selection: " + unknown);
        return 2;
    }
}
