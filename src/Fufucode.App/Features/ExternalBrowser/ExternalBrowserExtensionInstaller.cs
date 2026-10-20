using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Fufucode.App.Features.ExternalBrowser;

/// <summary>
/// 把仓库/安装目录里的 MV3 扩展复制到应用数据根，并写入当前端口与 PIN。
/// 用户只需在 Chrome/Edge 里“加载已解压的扩展程序”选择该目录，扩展即可自动配对。
/// </summary>
public static class ExternalBrowserExtensionInstaller
{
    public const string FolderName = "external-browser-extension";
    private const string SourceFolderName = "browser-extension-mv3";

    public static string Ensure(string dataRoot, int port, string pin)
    {
        var destination = Path.Combine(dataRoot, FolderName);
        var source = FindSource();
        if (source is not null)
        {
            CopyDirectory(source, destination);
            WriteConfig(destination, port, pin);
        }
        else if (!Directory.Exists(destination))
        {
            Directory.CreateDirectory(destination);
        }
        return destination;
    }

    public static void WriteConfig(string extensionDirectory, int port, string pin)
    {
        try
        {
            Directory.CreateDirectory(extensionDirectory);
            var json = JsonSerializer.Serialize(new
            {
                host = "http://127.0.0.1:" + (port > 0 ? port : 54321),
                pin = string.IsNullOrWhiteSpace(pin) ? null : pin,
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(extensionDirectory, "config.json"), json);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static string? FindSource()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "extensions", SourceFolderName),
            Path.Combine(Environment.CurrentDirectory, "extensions", SourceFolderName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extensions", SourceFolderName),
        };
        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(Path.Combine(full, "manifest.json"))) return full;
        }
        return null;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                var info = new FileInfo(file);
                var existing = new FileInfo(target);
                if (existing.Exists && existing.Length == info.Length &&
                    existing.LastWriteTimeUtc >= info.LastWriteTimeUtc) continue;
                File.Copy(file, target, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
