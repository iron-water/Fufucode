using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fufucode.Core.Security;

public sealed class SecurityPolicyStore
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly object _gate;
    private bool _observedFile;
    public string FilePath { get; }
    public SecurityPolicyStore(string dataRoot)
    {
        FilePath = Path.Combine(Path.GetFullPath(dataRoot), "security.json");
        _gate = Gates.GetOrAdd(FilePath, _ => new object());
        lock (_gate)
        {
            if (!File.Exists(FilePath)) Save(new SecuritySettings().Validate());
            _observedFile = true;
        }
    }
    // Always read: timestamp/length caches miss same-length replacements and cross-instance edits.
    public SecuritySettings Current
    {
        get { lock (_gate) return Load(); }
    }
    public SecuritySettings Update(Func<SecuritySettings, SecuritySettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            // A user can repair a malformed file from the settings UI, but tools remain blocked until saved.
            SecuritySettings before;
            try { before = Load(); } catch (SecurityPolicyException) { before = new SecuritySettings(); }
            var next = (update(before) ?? throw new ArgumentException("Policy cannot be null.")).Validate();
            Save(next);
            return next;
        }
    }
    private SecuritySettings Load()
    {
        try
        {
            if (!File.Exists(FilePath) && _observedFile) throw new IOException("Policy removed.");
            if (new FileInfo(FilePath).Length > 1024 * 1024) throw new IOException("Policy too large.");
            return (JsonSerializer.Deserialize<SecuritySettings>(File.ReadAllText(FilePath), Json)
                ?? throw new JsonException()).Validate();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            throw new SecurityPolicyException("安全策略不可读或无效；原生工具已停止。请在安全中心重新保存策略。", ex);
        }
    }
    private void Save(SecuritySettings value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Json);
                stream.Flush(true);
            }
            File.Move(temp, FilePath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed class SecurityPolicyException(string message, Exception? inner = null) : Exception(message, inner);
