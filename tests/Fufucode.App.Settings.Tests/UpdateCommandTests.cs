using System;
using System.Reflection;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Settings.Tests;

internal static class UpdateCommandTests
{
    public static void Run(Action<bool, string> check)
    {
        // 阻止旧实现真的覆盖测试程序；仍能观察到它误入部署路径。
        var mode = typeof(SettingsPageViewModel).Assembly.GetType("Fufucode.App.Program")!
            .GetProperty("SelfTestMode", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = mode.GetValue(null);
        mode.SetValue(null, true);
        try
        {
            var settings = new SettingsPageViewModel();
            check(!settings.IsReleaseBuild, "无发布标记的离线测试按本地渠道运行");
            settings.RefreshBuildChannel();
            var status = settings.UpdateStatusText;
            settings.CheckForUpdatesAsync().GetAwaiter().GetResult();
            check(settings.UpdateStatusText == status && !settings.IsCheckingUpdate,
                "本地渠道的更新检查不进入部署路径");

            settings.CheckAndUpgradeAsync().GetAwaiter().GetResult();
            check(settings.UpdateStatusText == "测试模式不部署本地构建。",
                "显式按钮仍进入本地部署路径并遵守测试模式保护");
        }
        finally
        {
            mode.SetValue(null, previous);
        }
    }
}
