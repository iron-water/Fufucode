using Microsoft.Win32;

namespace Fufucode.Fufu.Integration;

/// <summary>
/// 登录自启动（任务书 §六.7）：HKCU Run 键，默认关闭，由用户显式开启。
/// 只注册当前 exe；禁用不删用户数据。
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FUFU";

    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string;
        }
        catch (Exception) { return false; }
    }

    /// <summary>写入或清除 Run 键。exePath 为当前进程路径；带 --autostart 标记。</summary>
    public static bool Set(bool enable, string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return false;
            if (enable) key.SetValue(ValueName, $"\"{exePath}\" --autostart");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception) { return false; }
    }
}
