namespace Fufucode.Core;

/// <summary>
/// 未显式指定时的数据根：环境变量 <c>FUFUCODE_DATA_ROOT</c> 覆盖 → <c>%LOCALAPPDATA%\Fufucode</c>。
/// 内核与 Core 的 <c>AppDataLayout</c> 共用这一条规则。
/// </summary>
public static class FumoDataRoot
{
    public const string EnvironmentVariable = "FUFUCODE_DATA_ROOT";

    public static string Default()
    {
        var overridden = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fufucode")
            : overridden;
    }
}
