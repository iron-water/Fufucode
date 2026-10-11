using System;
using System.IO;
using System.Text.Json;

namespace Fufucode.App.Infrastructure;

public enum CompletionNotificationPolicy { Never, Unfocused, Always }
public sealed record NotificationPreferences(CompletionNotificationPolicy CompletionPolicy = CompletionNotificationPolicy.Unfocused,
    bool NotifyApprovals = true);

/// <summary>App-owned preferences; never changes Pi settings or conversation records.</summary>
public sealed class NotificationPreferencesStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private sealed record Document(int SchemaVersion, int CompletionPolicy, bool NotifyApprovals);

    public NotificationPreferencesStore(string path) => _path = Path.GetFullPath(path);

    public NotificationPreferences Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return new();
            if (new FileInfo(_path).Length > 8192) throw new InvalidDataException("通知偏好文件过大");
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path));
            if (document is null || document.SchemaVersion != 1 || document.CompletionPolicy is < 0 or > 2)
                throw new InvalidDataException("通知偏好格式无法识别");
            return new((CompletionNotificationPolicy)document.CompletionPolicy, document.NotifyApprovals);
        }
    }

    public void Save(NotificationPreferences preferences)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, new Document(1, (int)preferences.CompletionPolicy, preferences.NotifyApprovals));
                    stream.Flush(true);
                }
                File.Move(temporary, _path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
