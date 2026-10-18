using System.Diagnostics;
using System.Text;
using Fufucode.App.Features.Plugins;
using Fufucode.Core;

// E3-LOCAL 本地插件固定版本模块定向测试（独立入口，未注册到全量自测；注册属 Codex 集成范围）：
//   dotnet run --project tests/Fufucode.App.Plugins.Tests
// 无头页面绑定/生命周期测试（真实 Avalonia 控件与编译绑定）：
//   dotnet run --project tests/Fufucode.App.Plugins.Tests -c Release -- --ui
// 全部使用临时真实目录与真实文件；宿主快照/应用由测试夹具注入，
// 只验证模块接线、状态来源与失败语义，不冒充真实 Pi 加载成功。

if (args.FirstOrDefault() == "--host-real")
{
    if (args.Length != 3) throw new ArgumentException("--host-real <node> <Pi CLI>");
    return await Fufucode.App.Plugins.Tests.PiHostTests.RunAsync(args[1], args[2]);
}
if (args.FirstOrDefault() == "--load-proof")
    return await Fufucode.App.Plugins.Tests.PiLoadProofTests.RunAsync();
if (args.FirstOrDefault() == "--load-proof-real")
{
    if (args.Length != 3) throw new ArgumentException("--load-proof-real <node executable> <Pi CLI>");
    return await Fufucode.App.Plugins.Tests.PiLoadProofTests.RunAsync(args[1], args[2]);
}
if (args.FirstOrDefault() == "--ui")
{
    void UiCheck(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL " + name);
        Console.WriteLine("PASS " + name);
    }
    var uiTotal = Fufucode.App.Plugins.Tests.UiTests.Run(UiCheck);
    Console.WriteLine($"UI ALL PASS ({uiTotal})");
    return 0;
}

var serviceResult = await Fufucode.App.Plugins.Tests.ServiceTests.RunAsync();
return serviceResult != 0 ? serviceResult : await Fufucode.App.Plugins.Tests.PiLoadProofTests.RunAsync();
