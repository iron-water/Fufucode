using System;
using System.IO;
using System.Text.Json;

namespace Fufucode.App.Infrastructure;

public sealed record NotificationWindowPosition(int X, int Y, double Width, double Height);
public sealed record NotificationDeliveryPreferences(int Version = 1, bool Toast = true, bool Page = true,
    bool Windows = false, NotificationWindowPosition? LegacyPosition = null);

/// <summary>Independent notification channels. Legacy floating preferences remain untouched.</summary>
public sealed class NotificationDeliveryPreferencesStore(string root)
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetFullPath(root), "notification-delivery.json");
    public bool CanWrite { get; private set; } = true;
    public string? MigrationNote { get; private set; }

    public NotificationDeliveryPreferences Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                if (new FileInfo(Path).Length > 8192) throw new InvalidDataException("通知提示设置过大，原文件未修改。");
                var value = JsonSerializer.Deserialize<NotificationDeliveryPreferences>(File.ReadAllText(Path));
                if (value is null || value.Version != 1) throw new InvalidDataException("通知提示设置版本无法识别，原文件未修改。");
                return value.LegacyPosition is { } position
                    ? value with { LegacyPosition = double.IsFinite(position.Width) && double.IsFinite(position.Height)
                        ? position with { Width = Math.Clamp(position.Width, 320, 2560), Height = Math.Clamp(position.Height, 200, 1600) } : null }
                    : value;
            }
            var legacyPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, "floating-window.json");
            if (!File.Exists(legacyPath)) return new();
            var legacy = new FloatingWindowPreferencesStore(legacyPath).Load();
            NotificationWindowPosition? legacyPosition = !legacy.Pinned && legacy.IsOpen && legacy.DeliversToFloating && legacy.HasPosition &&
                legacy.Left >= int.MinValue && legacy.Left <= int.MaxValue && legacy.Top >= int.MinValue && legacy.Top <= int.MaxValue
                ? new((int)legacy.Left, (int)legacy.Top, Math.Clamp(legacy.Width, 320, 2560), Math.Clamp(legacy.Height, 200, 1600)) : null;
            var migrated = new NotificationDeliveryPreferences(Toast: legacy.DeliversToToast,
                Page: legacy.IsOpen && legacy.DeliversToFloating, Windows: legacy.DeliversToWindows, LegacyPosition: legacyPosition);
            Save(migrated);
            MigrationNote = "旧提醒渠道已迁移，原偏好文件保留。";
            return migrated;
        }
        catch { CanWrite = false; throw; }
    }

    public void Save(NotificationDeliveryPreferences value)
    {
        if (!CanWrite) throw new InvalidOperationException("原提醒设置无法识别，本次改动仅在当前窗口生效。");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, value); stream.Flush(true); }
            File.Move(temporary, Path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
