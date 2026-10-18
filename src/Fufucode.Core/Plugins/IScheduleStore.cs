using System;
using System.Collections.Generic;

namespace Fufucode.Core.Plugins;

/// <summary>
/// 用户定时任务契约（automation.cron 插件）。
/// </summary>
public sealed record ScheduledTaskItem(
    string Id,
    string? SessionId,
    DateTimeOffset DueAt,
    string Prompt,
    bool AutoRun,
    string State = "pending",
    string? Error = null);

/// <summary>
/// 用户定时任务存储统一接口。
/// </summary>
public interface IScheduleStore
{
    IReadOnlyList<ScheduledTaskItem> Tasks { get; }
    string? LoadError { get; }
    void Reload();
    void Save(IReadOnlyList<ScheduledTaskItem> tasks);
    bool TryClaim(string id, string state);
    void Update(string id, Func<ScheduledTaskItem, ScheduledTaskItem> edit);
    void Mutate(Func<IReadOnlyList<ScheduledTaskItem>, IReadOnlyList<ScheduledTaskItem>> edit);
}

/// <summary>
/// 轻量内置的本地定时任务存储，供插件未加载或离线测试使用。
/// </summary>
public sealed class FallbackScheduleStore : IScheduleStore
{
    private readonly string _path;
    private readonly object _gate = new();
    public IReadOnlyList<ScheduledTaskItem> Tasks { get; private set; } = Array.Empty<ScheduledTaskItem>();
    public string? LoadError => null;

    public FallbackScheduleStore(string root)
    {
        _path = System.IO.Path.Combine(root, "scheduled-tasks.json");
        Reload();
    }

    public void Reload()
    {
        lock (_gate)
        {
            try
            {
                if (System.IO.File.Exists(_path))
                    Tasks = System.Text.Json.JsonSerializer.Deserialize<List<ScheduledTaskItem>>(System.IO.File.ReadAllText(_path)) ?? new();
            }
            catch { }
        }
    }

    public void Save(IReadOnlyList<ScheduledTaskItem> tasks)
    {
        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            System.IO.File.WriteAllText(_path, System.Text.Json.JsonSerializer.Serialize(tasks));
            Tasks = tasks;
        }
    }

    public bool TryClaim(string id, string state)
    {
        lock (_gate)
        {
            Reload();
            if (!System.Linq.Enumerable.Any(Tasks, t => t.Id == id && t.State == "pending")) return false;
            Save(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(Tasks, t => t.Id == id ? t with { State = state } : t)));
            return true;
        }
    }

    public void Update(string id, Func<ScheduledTaskItem, ScheduledTaskItem> edit)
    {
        lock (_gate)
        {
            Reload();
            Save(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(Tasks, t => t.Id == id ? edit(t) : t)));
        }
    }

    public void Mutate(Func<IReadOnlyList<ScheduledTaskItem>, IReadOnlyList<ScheduledTaskItem>> edit)
    {
        lock (_gate)
        {
            Reload();
            Save(edit(Tasks));
        }
    }
}
