using System.IO;
using System.Text;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;

namespace Fufucode.App.ViewModels;

public sealed record AttachedFile(string Path, string Context, ChatImage? Image = null)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public static async Task<AttachedFile> LoadAsync(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("文件不存在", path);
        var mime = info.Extension.ToLowerInvariant() switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif", _ => null };
        if (mime is not null)
        {
            if (info.Length > 10 * 1024 * 1024) throw new IOException("图片超过 10 MB");
            var data = await File.ReadAllBytesAsync(path);
            using var stream = new MemoryStream(data);
            using var decoded = new Avalonia.Media.Imaging.Bitmap(stream);
            if (decoded.PixelSize.Width <= 0 || decoded.PixelSize.Height <= 0) throw new IOException("图片无法解码");
            return new(path, "图片：" + path, new(System.Convert.ToBase64String(data), mime));
        }
        if (info.Extension.Equals(".pdf", System.StringComparison.OrdinalIgnoreCase) || info.Length > 128 * 1024)
            return new(path, "文件引用（未提取内容，请使用工具读取）：" + path);
        var bytes = await File.ReadAllBytesAsync(path);
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            if (!text.Contains('\0')) return new(path, "文件：" + path + "\n" + text);
        }
        catch (DecoderFallbackException) { }
        return new(path, "二进制文件引用（未提取内容）：" + path);
    }
}
