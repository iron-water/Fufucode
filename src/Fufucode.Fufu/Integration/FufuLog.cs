namespace Fufucode.Fufu.Integration;

/// <summary>最小启动/运行日志：写 <dataRoot>/fufu.log，一行一条，便于独立排障。失败时静默（日志不能影响程序）。</summary>
internal static class FufuLog
{
    private static string? _path;
    private static readonly object Gate = new();

    public static void Init(string dataRoot)
    {
        try
        {
            Directory.CreateDirectory(dataRoot);
            _path = Path.Combine(dataRoot, "fufu.log");
        }
        catch (Exception) { _path = null; }
    }

    public static void Write(string message)
    {
        var path = _path;
        if (path is null) return;
        try
        {
            lock (Gate)
                File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch (Exception) { /* 日志不可影响程序 */ }
    }
}
