using System.Collections.Concurrent;
using System.Text.Json;

namespace Fufucode.Core.Security;

public sealed record SecurityAuditEntry(DateTimeOffset At, string Category, string Action, string Summary);

public sealed class SecurityAuditStore
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    private readonly object _gate;
    public SecurityAuditStore(string dataRoot)
    {
        _path = Path.Combine(Path.GetFullPath(dataRoot), "security-audit.jsonl");
        _gate = Gates.GetOrAdd(_path, _ => new object());
    }
    public IReadOnlyList<SecurityAuditEntry> ReadRecent(int limit = 100)
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return [];
            var entries = new Queue<SecurityAuditEntry>();
            foreach (var line in File.ReadLines(_path))
            {
                try
                {
                    if (JsonSerializer.Deserialize<SecurityAuditEntry>(line) is not { } entry) continue;
                    entries.Enqueue(entry with { Summary = SecurityContentScanner.Redact(entry.Summary) });
                    while (entries.Count > Math.Clamp(limit, 1, 10000)) entries.Dequeue();
                }
                catch (JsonException) { }
            }
            return entries.Reverse().ToArray();
        }
    }
    public void Export(string path)
    {
        var entries = ReadRecent(10000);
        File.WriteAllText(path, JsonSerializer.Serialize(entries, SecurityPolicyStore.Json));
    }
    public void Clear() { lock (_gate) { if (File.Exists(_path)) File.Delete(_path); } }
    internal void Record(string category, SecurityAction action, string summary, SecuritySettings settings)
    {
        if (!settings.AuditEnabled) return;
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Bound the log. Summaries are fixed internal reason codes, never arguments or paths.
            if (File.Exists(_path) && new FileInfo(_path).Length > 4 * 1024 * 1024) File.Delete(_path);
            var entry = new SecurityAuditEntry(DateTimeOffset.UtcNow, category, action.ToString(),
                SecurityContentScanner.Redact(SecurityContentScanner.Redact(summary, settings)));
            File.AppendAllText(_path, JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
    }
}
