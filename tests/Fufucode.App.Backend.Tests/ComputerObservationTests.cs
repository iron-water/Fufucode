using System.Text.Json;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;
using Fufucode.Plugins.ComputerUse;

namespace Fufucode.App.Backend.Tests;

internal static class ComputerObservationTests
{
    internal const string FixtureSocket = "fufucode-offline-protocol-fixture";

    // Match the one-shot CLI, including real v0.28.2 failures that exit zero.
    internal static int Reply(string tool, string json)
    {
        using var request = JsonDocument.Parse(json);
        var args = request.RootElement;
        string response = tool switch
        {
            "list_windows" when args.GetProperty("pid").GetInt32() == 7 && args.EnumerateObject().Count() == 1 =>
                """{"windows":[{"pid":7,"window_id":8,"title":"one"},{"pid":7,"window_id":9,"title":"two"},{"pid":10,"window_id":11,"title":"other-process"}]}""",
            "get_window_state" when args.GetProperty("window_id").GetInt32() == 9 =>
                """{"pid":7,"window_id":8,"snapshot_id":"s1","elements":[]}""",
            "get_window_state" when args.GetProperty("include_screenshot").GetBoolean() =>
                """{"window_bounds":{"width":500,"height":300},"screenshot_png_b64":"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+pWZkAAAAASUVORK5CYII="}""",
            "get_window_state" when args.GetProperty("pid").GetInt32() == 7 && args.GetProperty("window_id").GetInt32() == 8 &&
                !args.GetProperty("include_screenshot").GetBoolean() && args.GetProperty("include_accessibility_tree").GetBoolean() &&
                args.GetProperty("query").GetString() == "按钮" && args.GetProperty("max_elements").GetInt32() == 1 && args.GetProperty("session").GetString() == "t" =>
                """{"pid":7,"window_id":8,"snapshot_id":"s1","elements_complete":true,"total_element_count":2,"tree_markdown":"omit-me","elements":[{"element_token":"s1:0","role":"Button","label":"按钮","value":"omit-me","actions":["invoke"]},{"label":"omit-me"}]}""",
            "click" or "type_text" when args.TryGetProperty("element_token", out var token) && token.GetString() == "stale" =>
                """{"status":"refused","refusal":{"code":"stale_element_token","message":"refresh"}}""",
            "click" when args.TryGetProperty("element_token", out var token) && token.GetString() == "plain-error" => "window_id belongs to a different pid",
            "click" when args.GetProperty("element_token").GetString() == "s1:0" && args.GetProperty("pid").GetInt32() == 7 &&
                args.GetProperty("window_id").GetInt32() == 8 && args.GetProperty("session").GetString() == "t" &&
                args.GetProperty("delivery_mode").GetString() == "background" && !args.TryGetProperty("x", out _) && !args.TryGetProperty("y", out _) =>
                """{"delivery":{"mode":"background"},"effect":"unverifiable","route":"accessibility"}""",
            "type_text" when args.GetProperty("text").GetString() == "测试" && args.GetProperty("element_token").GetString() == "s1:0" =>
                """{"effect":"confirmed","route":"accessibility"}""",
            _ => "fixture: unexpected payload",
        };
        Console.WriteLine(response);
        return 0;
    }

    internal static async Task RunAsync(Checks checks)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Fufucode.App.Backend.Tests" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        var cua = new CuaDriverComputerUseBackend(exe, TimeSpan.FromSeconds(10), FixtureSocket);
        var plugin = new ComputerUsePlugin(backends: [cua]);
        Task<PureToolResult> Call(string tool, string json) => plugin.ExecuteAsync(new("test", tool, json));
        var list = await Call(ComputerUsePlugin.ObserveTool, """{"processId":7}""");
        checks.Check(list.Success && list.Content.Contains("two") && list.Content.Contains("one") && !list.Content.Contains("other-process"), "观察先列出进程内全部候选，不猜测窗口");
        var observed = await Call(ComputerUsePlugin.ObserveTool, """{"processId":7,"windowId":8,"session":"t","maxElements":1,"query":"按钮"}""");
        checks.Check(observed.Success && observed.Content.Contains("按钮") && observed.Content.Contains("s1:0") && observed.Content.Contains("\"complete\":false") && !observed.Content.Contains("omit-me"), "精简树保留后端引用、限制行数，不泄露编辑值或重复 Markdown");
        var wrong = await Call(ComputerUsePlugin.ObserveTool, """{"processId":7,"windowId":9}""");
        checks.Check(!wrong.Success && wrong.Content.Contains("invalid_observation"), "快照窗口与请求不符时拒绝");
        var shot = await Call(ComputerUsePlugin.ScreenshotTool, """{"processId":7,"windowId":8}""");
        checks.Check(shot.Success && shot.Content.Contains("1×1"), "像素路径报告真实 PNG 尺寸，不误用窗口边界");
        var click = await Call(ComputerUsePlugin.MouseTool, """{"action":"click","processId":7,"windowId":8,"elementToken":"s1:0","session":"t"}""");
        checks.Check(!click.Success && click.Content.Contains("effect_unverified"), "纯引用点击无需坐标；已投递不等于效果已确认");
        var typed = await Call(ComputerUsePlugin.KeyboardTool, """{"action":"type","processId":7,"windowId":8,"elementToken":"s1:0","text":"测试"}""");
        checks.Check(typed.Success && typed.Content.Contains("确认生效"), "按控件输入并保留真实确认回执");
        var mixed = await Call(ComputerUsePlugin.MouseTool, """{"action":"click","processId":7,"windowId":8,"elementToken":"s1:0","x":2,"y":3}""");
        checks.Check(!mixed.Success && mixed.Content.Contains("不能混用"), "拒绝引用和像素混合，防止错误引用变成坐标点击");
        foreach (var (token, code) in new[] { ("stale", "stale_element_token"), ("plain-error", "invalid_response") })
        {
            var refused = await Call(ComputerUsePlugin.MouseTool, $$"""{"action":"click","processId":7,"windowId":8,"elementToken":"{{token}}"}""");
            checks.Check(!refused.Success && refused.Content.Contains(code), $"退出码 0 仍保留失败：{code}");
        }
        var disabled = new ComputerUsePlugin(backends: [cua]) { ScreenCaptureEnabled = false };
        var denied = await disabled.ExecuteAsync(new("disabled", ComputerUsePlugin.ObserveTool, """{"processId":7}"""));
        checks.Check(!denied.Success && denied.Content.Contains("关闭"), "关闭观察权限后无法枚举窗口标题或控件");
    }
}
