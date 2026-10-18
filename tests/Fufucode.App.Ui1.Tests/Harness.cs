using System;
using System.Threading;
using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Fufucode.App.Ui1.Tests;

// 与 App.axaml 相同的样式链（FluentTheme + FufucodeTheme + WorkbenchStyles），
// 供 UI-1 交互守卫/排版在真实无头 Application 上解析资源与控件属性。
internal sealed class TestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Fufucode.App/"))
        {
            Source = new Uri("avares://Fufucode.App/Styles/FufucodeTheme.axaml"),
        });
        Styles.Add(new StyleInclude(new Uri("avares://Fufucode.App/"))
        {
            Source = new Uri("avares://Fufucode.App/Styles/WorkbenchStyles.axaml"),
        });
    }
}

internal static class Harness
{
    public static bool Pump(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            Thread.Sleep(15);
        }

        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    public static void PumpTicks(int rounds = 4)
    {
        for (var index = 0; index < rounds; index++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        }
    }
}
