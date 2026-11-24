using Fufucode.Core;

namespace Fufucode.Fui;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        TuiOptions options;
        try { options = TuiOptions.Parse(args); }
        catch (ArgumentException error) { return Fail(error.Message, 2); }
        if (options.Help) { Console.WriteLine(TuiOptions.Usage); return 0; }
        if (!TerminalSurface.IsSupported)
            return Fail("FUI 需要交互终端；脚本和管道请使用 fufucode（CLI）。", 2);

        using var lifetime = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; lifetime.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var layout = new AppDataLayout(options.AppData);
            // Also isolate tools which consult the process environment rather than the session's layout.
            Environment.SetEnvironmentVariable(AppDataLayout.RootEnvironmentVariable, layout.Root);
            using var surface = new TerminalSurface();
            var session = await FumoTuiSession.OpenAsync(options.Project, layout.Root, options.SessionId, options.Continue, lifetime.Token);
            var controller = new TuiController(session, lifetime.Token, layout.Root);
            try
            {
                while (!controller.Exiting)
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    controller.Update();
                    for (var n = 0; n < 128 && !controller.Exiting && surface.PollKey() is { } key; n++)
                        controller.Key(key, surface.ViewportRows, surface.MaxScroll, surface.PickerRows);
                    if (controller.Dirty || surface.SizeChanged)
                    {
                        surface.Draw(controller.View());
                        if (controller.TakeScrollTarget() is { } target)
                        {
                            controller.ScrollTo(surface.ScrollToLine(target));
                            surface.Draw(controller.View());
                        }
                        controller.Painted(surface.MaxScroll);
                    }
                    if (!controller.Exiting) await Task.Delay(30, lifetime.Token);
                }
                return 0;
            }
            finally
            {
                lifetime.Cancel();
                await controller.FinishAsync();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return 130; }
        catch (Exception error) { return Fail(error.GetBaseException().Message, 3); }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static int Fail(string message, int code)
    {
        // Exception messages can contain a project path or other untrusted terminal text.
        Console.Error.WriteLine(TerminalText.SingleLine(message));
        return code;
    }
}

internal sealed record TuiOptions(string Project, string? AppData, string? SessionId, bool Continue, bool Help)
{
    internal const string Usage = """
        FUI · FUMO

        fui [--project <目录>] [--app-data <目录>] [--session <ID> | --continue]

        默认新建 FUMO 会话；--continue 恢复本项目最近的 FUMO 会话。
        Ctrl+P 打开命令面板，F1 查看按键。无界面、管道和 JSON 输出使用独立的 fufucode CLI。
        """;

    internal static TuiOptions Parse(string[] args)
    {
        string? project = null, appData = null, session = null;
        bool resume = false, help = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!seen.Add(arg)) throw new ArgumentException($"重复参数：{arg}");
            string Value() => i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]) &&
                !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] :
                throw new ArgumentException($"{arg} 缺少参数值。");
            switch (arg)
            {
                case "--project": project = Value(); break;
                case "--app-data": appData = Value(); break;
                case "--session": session = Value(); break;
                case "--continue": resume = true; break;
                case "--help" or "-h": help = true; break;
                default: throw new ArgumentException($"未知参数：{arg}。使用 --help 查看用法。");
            }
        }
        if (session is not null && resume) throw new ArgumentException("--session 与 --continue 不能同时使用。");
        return new(project ?? Environment.CurrentDirectory, appData, session, resume, help);
    }
}
