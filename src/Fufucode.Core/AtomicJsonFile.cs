using System.Text.Encodings.Web;
using System.Text.Json;

namespace Fufucode.Core;

/// <summary>
/// 会话文件的原子写入：流式序列化到同目录临时文件，再整体替换目标。
/// 避免先把整份 JSON（含缩进）拼成字符串，峰值内存约等于目标的大小而不是数倍。
/// </summary>
public static class AtomicJsonFile
{
    public static void Write(string path, Action<Utf8JsonWriter> write, JsonSerializerOptions? options = null)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       64 * 1024, FileOptions.SequentialScan))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
                   {
                       Indented = false,
                       SkipValidation = false,
                       Encoder = options?.Encoder ?? JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   }))
            {
                write(writer);
                writer.Flush();
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    }
}
