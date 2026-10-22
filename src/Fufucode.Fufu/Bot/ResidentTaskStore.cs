using System.Text.Json;

namespace Fufucode.Fufu.Bot;

public enum ResidentTaskState
{
    Queued,
    Running,
    Awaiting,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record ResidentTask(
    string Id,
    string Title,
    string Source,
    string Subject,
    string Prompt,
    ResidentTaskState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? SessionId = null,
    string? Result = null,
    string? Error = null);

/// <summary>FUFU 常驻任务实体的持久化存储；只管理状态，不执行任务。</summary>
public sealed class ResidentTaskStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();
    private List<ResidentTask> _tasks = new();

    public ResidentTaskStore(string residentRoot)
    {
        Directory.CreateDirectory(residentRoot);
        _path = System.IO.Path.Combine(residentRoot, "tasks.json");
        Reload();
    }

    public IReadOnlyList<ResidentTask> Tasks
    {
        get { lock (_gate) return _tasks.ToArray(); }
    }

    public string Path => _path;

    public ResidentTask Create(string title, string source, string subject, string prompt, string? sessionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var now = DateTimeOffset.UtcNow;
        var task = new ResidentTask(
            $"task-{Guid.NewGuid():N}", title.Trim(), source.Trim(), subject?.Trim() ?? "", prompt.Trim(),
            ResidentTaskState.Queued, now, now, sessionId?.Trim());
        lock (_gate)
        {
            _tasks.Add(task);
            Persist();
        }
        return task;
    }

    public ResidentTask? Find(string id)
    {
        lock (_gate) return _tasks.FirstOrDefault(x => x.Id == id);
    }

    public ResidentTask? FindBySession(string sessionId, bool activeOnly = true)
    {
        lock (_gate)
        {
            return _tasks.LastOrDefault(x => x.SessionId == sessionId &&
                (!activeOnly || x.State is ResidentTaskState.Queued or ResidentTaskState.Running or ResidentTaskState.Awaiting));
        }
    }

    public ResidentTask? Update(string id, Func<ResidentTask, ResidentTask> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        lock (_gate)
        {
            var index = _tasks.FindIndex(x => x.Id == id);
            if (index < 0) return null;
            var next = edit(_tasks[index]) with { UpdatedAt = DateTimeOffset.UtcNow };
            _tasks[index] = next;
            Persist();
            return next;
        }
    }

    public ResidentTask? SetState(string id, ResidentTaskState state, string? result = null, string? error = null) =>
        Update(id, task => task with { State = state, Result = result, Error = error });

    public void Reload()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) { _tasks = new(); return; }
                _tasks = JsonSerializer.Deserialize<List<ResidentTask>>(File.ReadAllText(_path), Options) ?? new();
            }
            catch { _tasks = new(); }
        }
    }

    private void Persist()
    {
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(_tasks, Options));
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
