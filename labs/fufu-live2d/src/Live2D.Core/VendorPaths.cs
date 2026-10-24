namespace Live2D.Core;

/// <summary>
/// 第三方载荷（Cubism SDK 二进制、示例模型）不进仓库，统一放 %LOCALAPPDATA%\fufu-live2d\ 或命令行 --root 指定目录。
/// 目录约定见 labs/fufu-live2d/README.md。
/// </summary>
public static class VendorPaths
{
    public const string DefaultRootEnv = "FUFU_LIVE2D_ROOT";

    /// <summary>vendor 根目录；默认 %LOCALAPPDATA%\fufu-live2d。</summary>
    public static string Root =>
        Environment.GetEnvironmentVariable(DefaultRootEnv)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "fufu-live2d");

    public static string NativeCoreDir => Path.Combine(Root, "vendor", "native", "x86_64");
    public static string NativeCoreDll => Path.Combine(NativeCoreDir, "Live2DCubismCore.dll");
    public static string WebCoreDir => Path.Combine(Root, "vendor", "web");
    public static string ModelsDir => Path.Combine(Root, "models");

    /// <summary>按模型目录名解析 *.model3.json 绝对路径。</summary>
    public static string ResolveModelJson(string nameOrPath)
    {
        if (File.Exists(nameOrPath)) return Path.GetFullPath(nameOrPath);
        var dir = Path.Combine(ModelsDir, nameOrPath);
        if (Directory.Exists(dir))
        {
            var hit = Directory.GetFiles(dir, "*.model3.json").FirstOrDefault();
            if (hit != null) return hit;
        }
        throw new FileNotFoundException(
            $"model3.json not found for '{nameOrPath}'. Expected under '{dir}'. " +
            "Download per labs/fufu-live2d/README.md (SDK assets are license-restricted, not vendored).");
    }

    public static void EnsureNativeCore()
    {
        if (!File.Exists(NativeCoreDll))
            throw new FileNotFoundException(
                $"Live2DCubismCore.dll not found at '{NativeCoreDll}'. " +
                "Download per labs/fufu-live2d/README.md (SDK assets are license-restricted, not vendored).");
    }
}
