using System.Text.Json;

namespace Fufucode.Fufu.Bot;

public enum ResidentTriggerKind
{
    Interval,
    BridgeEvent,
}

public sealed record ResidentTrigger(
    string Id,
    string Name,
    ResidentTriggerKind Kind,
    bool Enabled,
    DateTimeOffset? NextDueAt = null,
    int IntervalSeconds = 0,
    string? EventType = null,
    int CooldownSeconds = 300,
    DateTimeOffset? LastFiredAt = null);

/// <summary>持久化触发条件；条件只产生观察任务，不自动执行。</summary>
public sealed class ResidentTriggerStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();
    private List<ResidentTrigger> _triggers = new();

    public ResidentTriggerStore(string residentRoot)
    {
        Directory.CreateDirectory(residentRoot);
        _path = System.IO.Path.Combine(residentRoot, "triggers.json");
        Reload();
    }

    public IReadOnlyList<ResidentTrigger> Triggers
    {
        get { lock (_gate) return _triggers.ToArray(); }
    }

    public ResidentTrigger AddInterval(string name, TimeSpan interval, bool enabled = true)
    {
        if (interval < TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(interval));
        var now = DateTimeOffset.UtcNow;
        return Add(new ResidentTrigger($"trigger-{Guid.NewGuid():N}", name, ResidentTriggerKind.Interval,
            enabled, now + interval, (int)Math.Ceiling(interval.TotalSeconds)));
    }

    public ResidentTrigger AddBridgeEvent(string name, string eventType, TimeSpan cooldown, bool enabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        if (cooldown < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(cooldown));
        return Add(new ResidentTrigger($"trigger-{Guid.NewGuid():N}", name, ResidentTriggerKind.BridgeEvent,
            enabled, EventType: eventType.Trim(), CooldownSeconds: Math.Max(0, (int)cooldown.TotalSeconds)));
    }

    public ResidentTrigger? Find(string id)
    {
        lock (_gate) return _triggers.FirstOrDefault(x => x.Id == id);
    }

    public ResidentTrigger? Update(string id, Func<ResidentTrigger, ResidentTrigger> edit)
    {
        lock (_gate)
        {
            var index = _triggers.FindIndex(x => x.Id == id);
            if (index < 0) return null;
            var next = edit(_triggers[index]);
            _triggers[index] = next;
            Persist();
            return next;
        }
    }

    public void Reload()
    {
        lock (_gate)
        {
            try
            {
                _triggers = File.Exists(_path)
                    ? JsonSerializer.Deserialize<List<ResidentTrigger>>(File.ReadAllText(_path), Options) ?? new()
                    : new();
            }
            catch { _triggers = new(); }
        }
    }

    private ResidentTrigger Add(ResidentTrigger trigger)
    {
        lock (_gate)
        {
            _triggers.Add(trigger);
            Persist();
            return trigger;
        }
    }

    private void Persist()
    {
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(_triggers, Options));
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
