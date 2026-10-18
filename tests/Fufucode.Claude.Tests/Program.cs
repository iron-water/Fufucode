using Fufucode.Claude.Tests;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var checks = new Checks();
try
{
    if (args.Length >= 1 && args[0] == "--live")
    {
        var executable = args.Length >= 2 ? args[1] : "claude.exe";
        var model = args.Length >= 3 ? args[2] : "devin/swe-2";
        var prompt = args.Length >= 4 ? args[3] : "Reply with exactly: PONG";
        return await ClaudeLiveProbe.RunAsync(executable, model, prompt);
    }
    if (args.Length >= 1 && args[0] == "--live-tool")
    {
        var executable = args.Length >= 2 ? args[1] : "claude.exe";
        var model = args.Length >= 3 ? args[2] : "devin/swe-2";
        return await ClaudeLiveProbe.RunToolAsync(executable, model);
    }
    if (args.Contains("--frames-only"))
    {
        ClaudeFrameTests.Run(checks);
        Console.WriteLine($"\n帧解码通过：{checks.Passed} 项断言");
        return 0;
    }
    ClaudeFrameTests.Run(checks);
    await ClaudeSessionTests.RunAsync(checks, root);
    Console.WriteLine($"\n全部通过：{checks.Passed} 项断言");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
