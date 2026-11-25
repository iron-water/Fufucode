using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core.Backend;

/// <summary>
/// 会话图片附件的外部存储。
///
/// 之前图片以 base64 直接写进会话 JSON：实测一个会话 20.1 MB 里约 19 MB 是两张图，
/// 而长回合每 5 秒节流落盘一次，等于每 5 秒重写 20 MB。这里把字节挪到
/// <c>&lt;会话文件&gt;.assets/</c> 下，会话 JSON 只留引用，读取时按路径回取。
/// </summary>
public static class SessionImageStore
{
    private const int MaxCachedImages = 8;
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>把图片字节落盘并返回引用路径；<paramref name="sessionFilePath"/> 为空时返回 null（无持久化会话内联即可）。</summary>
    public static string? Save(string? sessionFilePath, string base64, string mimeType)
    {
        if (string.IsNullOrEmpty(sessionFilePath) || string.IsNullOrEmpty(base64)) return null;
        try
        {
            var directory = sessionFilePath + ".assets";
            Directory.CreateDirectory(directory);
            var extension = ExtensionFor(mimeType);
            var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(base64)))[..24];
            var path = Path.Combine(directory, key + extension);
            if (!File.Exists(path))
                File.WriteAllBytes(path, Convert.FromBase64String(base64));
            return path;
        }
        catch
        {
            // 存不下就退回内联：正确性优先于体积。
            return null;
        }
    }

    /// <summary>取回图片字节：优先缓存，其次读盘；文件缺失时返回 null。</summary>
    public static string? Resolve(PureImageAttachment image)
    {
        if (!string.IsNullOrEmpty(image.Base64Data)) return image.Base64Data;
        if (image.FilePath is not { Length: > 0 } path) return null;
        if (Cache.TryGetValue(path, out var cached)) return cached;
        try
        {
            if (!File.Exists(path)) return null;
            var data = Convert.ToBase64String(File.ReadAllBytes(path));
            // 单张图可达数 MB，缓存必须有界；图片数量有限，超限直接清空重填比维护 LRU 更稳。
            if (Cache.Count >= MaxCachedImages) Cache.Clear();
            Cache[path] = data;
            return data;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 落盘成功后补上引用路径。内存里继续保留 base64：本轮请求不必回读磁盘；
    /// 会话 JSON 由 <c>WithoutInlineImages</c> 在序列化时剥离字节，只留引用。
    /// </summary>
    public static PureImageAttachment Externalize(this PureImageAttachment image, string? sessionFilePath)
    {
        if (image.FilePath is { Length: > 0 }) return image;
        var path = Save(sessionFilePath, image.Base64Data, image.MimeType);
        return path is null ? image : image with { FilePath = path };
    }

    private static string ExtensionFor(string mimeType) => mimeType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        _ => ".img",
    };
}
