using System.Text;
using Fufucode.Core;

namespace Fufucode.Fui;

internal static class FuiTranscript
{
    public static string Usage(SessionHistory history, bool active)
    {
        var messages = history.CurrentBranchMessages;
        var totals = SessionUsageQuery.Query(messages).Session;
        return (active ? "运行中 · 仅统计已保存的历史\n" : "") +
               $"消息 {messages.Count} · 用户 {messages.Count(m => m.Role == "user")} · 助手 {totals.AssistantMessageCount} · 工具结果 {messages.Count(m => m.Role == "toolResult")}\n" +
               $"Token：输入 {Counter(totals.Input)} · 输出 {Counter(totals.Output)} · 合计 {Counter(totals.TotalTokens)}\n" +
               $"缓存读取 {Counter(totals.CacheRead)} · 缓存写入 {Counter(totals.CacheWrite)}";
    }

    private static string Counter(UsageCounter value) => value.Value?.ToString() ?? (value.KnownSubtotal is { } subtotal
        ? $"不完整（已报告 {subtotal}，{value.UnknownMessageCount} 条未报告）" : "未提供");

    public static async Task<string> ExportAsync(SessionHistory history, PersistentSession session, AppDataLayout layout, string argument, CancellationToken ct)
    {
        var path = string.IsNullOrWhiteSpace(argument)
            ? Path.Combine(layout.ExportsDirectory, $"fui-{session.Session.Id}-{Guid.NewGuid():N}.md")
            : Path.GetFullPath(argument.Trim(), session.Project.Path);
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException($"导出目标已存在：{path}");
        var directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("无效的导出路径。");
        var temp = Path.Combine(directory, $".fui-export-{Guid.NewGuid():N}.tmp");
        var created = false;
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                await using var output = new StreamWriter(file, new UTF8Encoding(false));
                await output.WriteLineAsync($"# {session.Session.Name ?? "FUMO 会话"}\n\n会话：`{session.Session.Id}`\n");
                foreach (var line in FumoEventProjection.ReadHistory(session, history))
                {
                    ct.ThrowIfCancellationRequested();
                    var role = line.Kind switch { "user" => "用户", "assistant" => "助手", "thinking" => "思考", "tool" => "工具", _ => "消息" };
                    await output.WriteLineAsync($"## {role}\n");
                    await output.WriteLineAsync(line.Text.AsMemory(), ct);
                    await output.WriteLineAsync();
                }
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: false);
            return path;
        }
        finally { if (created && File.Exists(temp)) File.Delete(temp); }
    }
}
