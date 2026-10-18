using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Linq;
using System.Threading;
using System.Security.Cryptography;
using System.Text;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.CronSchedules;

public sealed class WorkbenchScheduleStore : IScheduleStore
{
    private readonly string _path;
    public IReadOnlyList<ScheduledTaskItem> Tasks { get; private set; } = Array.Empty<ScheduledTaskItem>();
    public string? LoadError { get; private set; }
    public WorkbenchScheduleStore(string root)
    {
        _path = Path.Combine(root, "scheduled-tasks.json");
        Reload();
    }
    public void Reload()
    {
        LoadError = null;
        try { if (File.Exists(_path)) Tasks = JsonSerializer.Deserialize<List<ScheduledTaskItem>>(File.ReadAllText(_path)) ?? new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { LoadError = error.Message; }
    }
    public void Save(IReadOnlyList<ScheduledTaskItem> tasks)
    {
        if (LoadError is not null) throw new InvalidOperationException("计划任务文件不可覆盖：" + LoadError);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(tasks, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, _path, true); Tasks = tasks; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private Mutex Lock() => new(false, "FufucodeSchedules-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(_path).ToUpperInvariant()))));
    public bool TryClaim(string id, string state)
    {
        using var mutex = Lock();
        try { if (!mutex.WaitOne(0)) return false; } catch (AbandonedMutexException) { }
        try
        {
            Reload();
            if (!Tasks.Any(t => t.Id == id && t.State == "pending")) return false;
            Save(Tasks.Select(t => t.Id == id ? t with { State = state } : t).ToArray()); return true;
        }
        finally { mutex.ReleaseMutex(); }
    }
    public void Update(string id, Func<ScheduledTaskItem, ScheduledTaskItem> edit) =>
        Mutate(tasks => tasks.Select(t => t.Id == id ? edit(t) : t).ToArray());
    public void Mutate(Func<IReadOnlyList<ScheduledTaskItem>, IReadOnlyList<ScheduledTaskItem>> edit)
    {
        using var mutex = Lock();
        try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
        try { Reload(); Save(edit(Tasks)); } finally { mutex.ReleaseMutex(); }
    }
}
