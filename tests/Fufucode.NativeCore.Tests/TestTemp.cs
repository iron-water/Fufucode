namespace Fufucode.NativeCore.Tests;

/// <summary>
/// All test scratch data lives under a per-run unique system-temp directory and is deleted in finally.
/// Never uses the repository bin directory or a real user data root.
/// </summary>
internal static class TestTemp
{
    public static string Create(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), "fufucode-nativecore-tests",
            label + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static void Delete(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }
}
