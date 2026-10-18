using System.Text.Json;
using Fufucode.Core.Backend;

namespace Fufucode.Plugins.Dsh;

/// <summary>
/// DSH 协议适配插件共用的本地历史文件读取：{ "messages": [...] }。
/// 解析失败时把原文件改名为 .corrupt-时间戳 保留现场，不覆盖唯一副本。
/// </summary>
internal static class DshSessionHistory
{
    public static List<PureAgentMessage> Load(string file)
    {
        if (!File.Exists(file)) return [];
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("messages", out var messages))
            {
                return JsonSerializer.Deserialize<List<PureAgentMessage>>(messages.GetRawText()) ?? [];
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Failed to restore session history: {0}", ex);
            try
            {
                File.Move(file, file + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss"),
                    overwrite: true);
            }
            catch { }
        }
        return [];
    }
}
