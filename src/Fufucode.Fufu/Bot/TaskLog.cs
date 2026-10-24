using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fufucode.Fufu.Bot;

/// <summary>
/// FUFU 任务日志（FUFU-05）：JSONL 追加到 &lt;dataRoot&gt;/task-log.jsonl。
/// 记录 FUFU 主动执行的动作与转发的桥接事件，供面板回看与事后审计。
/// </summary>
public sealed class TaskLog
{
    public sealed record Entry(DateTimeOffset Ts, string Source, string Kind, string Session, string Text);

    private readonly string _path;
    private readonly object _gate = new();

    public TaskLog(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        _path = Path.Combine(dataRoot, "task-log.jsonl");
    }

    /// <summary>追加一条；永不抛出（日志失败不能打断主流程）。</summary>
    public void Append(string source, string kind, string session, string text)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                ts = DateTimeOffset.UtcNow, source, kind, session, text,
            });
            lock (_gate) File.AppendAllText(_path, line + "\n");
        }
        catch { }
    }

    /// <summary>读最近 <paramref name="count"/> 条（旧→新）。</summary>
    public IReadOnlyList<Entry> ReadTail(int count)
    {
        try
        {
            lock (_gate)
            {
                if (!File.Exists(_path)) return Array.Empty<Entry>();
                var lines = File.ReadAllLines(_path);
                var result = new List<Entry>(Math.Min(count, lines.Length));
                foreach (var line in lines.Skip(Math.Max(0, lines.Length - count)))
                {
                    try
                    {
                        var node = JsonNode.Parse(line);
                        if (node is null) continue;
                        result.Add(new Entry(
                            node["ts"]?.GetValue<DateTimeOffset>() ?? default,
                            node["source"]?.GetValue<string>() ?? "",
                            node["kind"]?.GetValue<string>() ?? "",
                            node["session"]?.GetValue<string>() ?? "",
                            node["text"]?.GetValue<string>() ?? ""));
                    }
                    catch { }
                }
                return result;
            }
        }
        catch { return Array.Empty<Entry>(); }
    }
}
