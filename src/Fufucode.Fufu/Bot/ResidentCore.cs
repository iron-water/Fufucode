using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fufucode.Fufu.Bot;

/// <summary>FUFU 常驻实体的四类长期记忆作用域。</summary>
public enum ResidentMemoryScope
{
    Identity,
    User,
    Project,
    Agent,
}

/// <summary>一条可审计的常驻观察记录；观察阶段不触发执行。</summary>
public sealed record ResidentObservation(
    DateTimeOffset Timestamp,
    string Source,
    string Kind,
    string Subject,
    string Summary,
    string? CorrelationId = null);

/// <summary>
/// FUFU 的会话外常驻核心（第一阶段）。
/// 负责身份元数据、用户可编辑记忆和观察账本，不负责模型调用、任务执行或权限放行。
/// </summary>
public sealed class ResidentCore
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly JsonSerializerOptions CompactJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _root;
    private readonly string _memoryRoot;
    private readonly string _observationPath;
    private readonly object _gate = new();

    public ResidentCore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _root = Path.Combine(Path.GetFullPath(dataRoot), "resident");
        _memoryRoot = Path.Combine(_root, "memory");
        _observationPath = Path.Combine(_root, "observations.jsonl");
        Directory.CreateDirectory(_memoryRoot);
        EnsureMemoryFiles();
    }

    public string Root => _root;
    public string MemoryRoot => _memoryRoot;
    public string ObservationPath => _observationPath;

    /// <summary>常驻实体的固定身份元数据，不随聊天会话重建。</summary>
    public ResidentIdentity Identity => ReadJson(Path.Combine(_root, "identity.json"), ResidentIdentity.Default);

    public ResidentIdentity UpdateIdentity(Func<ResidentIdentity, ResidentIdentity> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var next = change(Identity) with { SchemaVersion = SchemaVersion };
            WriteJson(Path.Combine(_root, "identity.json"), next);
            return next;
        }
    }

    /// <summary>读取某个作用域的 Markdown 记忆；文件不存在时返回空正文。</summary>
    public string ReadMemory(ResidentMemoryScope scope)
    {
        var path = MemoryPath(scope);
        lock (_gate) return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    /// <summary>由用户或受控设置界面写入可编辑 Markdown 记忆。</summary>
    public void WriteMemory(ResidentMemoryScope scope, string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        lock (_gate)
        {
            Directory.CreateDirectory(_memoryRoot);
            File.WriteAllText(MemoryPath(scope), markdown);
        }
    }

    /// <summary>
    /// 记录观察事件。事件只进入账本，不触发 FUMO、工具、任务或外部消息。
    /// </summary>
    public void Observe(string source, string kind, string subject, string summary, string? correlationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        var observation = new ResidentObservation(
            DateTimeOffset.UtcNow,
            source.Trim(),
            kind.Trim(),
            subject?.Trim() ?? string.Empty,
            summary.Trim(),
            correlationId?.Trim());
        try
        {
            var line = JsonSerializer.Serialize(observation, CompactJsonOptions);
            lock (_gate) File.AppendAllText(_observationPath, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Integration.FufuLog.Write($"resident observation failed: {ex.GetType().Name}");
        }
    }

    /// <summary>读取最近的观察事件，结果按旧到新排列。</summary>
    public IReadOnlyList<ResidentObservation> ReadObservations(int count = 100)
    {
        if (count <= 0) return Array.Empty<ResidentObservation>();
        try
        {
            lock (_gate)
            {
                if (!File.Exists(_observationPath)) return Array.Empty<ResidentObservation>();
                var lines = File.ReadAllLines(_observationPath);
                var result = new List<ResidentObservation>(Math.Min(count, lines.Length));
                foreach (var line in lines.Skip(Math.Max(0, lines.Length - count)))
                {
                    try
                    {
                        var item = JsonSerializer.Deserialize<ResidentObservation>(line, JsonOptions);
                        if (item is not null) result.Add(item);
                    }
                    catch { }
                }
                return result;
            }
        }
        catch (Exception ex)
        {
            Integration.FufuLog.Write($"resident observation read failed: {ex.GetType().Name}");
            return Array.Empty<ResidentObservation>();
        }
    }

    private void EnsureMemoryFiles()
    {
        foreach (var scope in Enum.GetValues<ResidentMemoryScope>())
        {
            var path = MemoryPath(scope);
            if (File.Exists(path)) continue;
            File.WriteAllText(path, DefaultMemory(scope));
        }
        if (!File.Exists(Path.Combine(_root, "identity.json")))
            WriteJson(Path.Combine(_root, "identity.json"), ResidentIdentity.Default);
    }

    private string MemoryPath(ResidentMemoryScope scope) => Path.Combine(_memoryRoot, scope switch
    {
        ResidentMemoryScope.Identity => "IDENTITY.md",
        ResidentMemoryScope.User => "USER.md",
        ResidentMemoryScope.Project => "PROJECT.md",
        ResidentMemoryScope.Agent => "AGENTS.md",
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    });

    private static string DefaultMemory(ResidentMemoryScope scope) => scope switch
    {
        ResidentMemoryScope.Identity => "# FUFU Identity\n\nThis file is user-editable. Keep stable identity and behavior principles here.\n",
        ResidentMemoryScope.User => "# User Memory\n\nAdd durable user preferences and explicitly confirmed facts here.\n",
        ResidentMemoryScope.Project => "# Project Memory\n\nAdd project-specific goals and durable context here.\n",
        ResidentMemoryScope.Agent => "# Agent Memory\n\nRecord observed agent ownership, handoffs, and durable work context here.\n",
        _ => "",
    };

    private static T ReadJson<T>(string path, T fallback)
    {
        try
        {
            if (!File.Exists(path)) return fallback;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ?? fallback;
        }
        catch { return fallback; }
    }

    private static void WriteJson<T>(string path, T value)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}

public sealed record ResidentIdentity(
    int SchemaVersion,
    string Name,
    string Description,
    string Personality,
    DateTimeOffset CreatedAt)
{
    public static ResidentIdentity Default { get; } = new(
        1,
        "FUFU",
        "一个独立运行的桌面 AI 玩偶和长期个人 Bot。",
        "温柔、清晰、可解释；观察先于行动，敏感操作必须获得明确授权。",
        DateTimeOffset.UtcNow);
}
