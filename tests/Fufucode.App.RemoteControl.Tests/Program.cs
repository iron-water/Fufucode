namespace Fufucode.App.RemoteControl.Tests;

/// <summary>
/// M1 手机远控独立测试入口（未注册到共享 SelfTestRunner）：
///   dotnet run --project tests/Fufucode.App.RemoteControl.Tests
/// 覆盖回环启动/停止/状态、PIN 认证与锁定、会话数据与消息投递、审批流转、紧急停止、
/// 内嵌移动端页面契约与配对二维码契约。真实回环 HTTP/WebSocket，不使用模拟网络层。
/// </summary>
internal static class Program
{
    private static int _passed;

    private static async Task<int> Main()
    {
        try
        {
            await RemoteControlTests.RunAllAsync(Check);
            await RemoteAuditTests.RunAsync(Check);
            Console.WriteLine($"\n全部通过：{_passed} 项断言");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new CheckFailed(name);
        _passed++;
        Console.WriteLine("PASS " + name);
    }
}
