using System;
using System.IO;
using System.Text.Json;

namespace Fufucode.App.Infrastructure;

/// <summary>浮窗投递渠道。三个渠道可独立开关；Windows 系统通知本轮预留，不投递真实 toast。</summary>
[Flags]
public enum FloatingDelivery
{
    None = 0,
    Toast = 1,
    Floating = 2,
    Windows = 4,
}

/// <summary>
/// 浮窗偏好。Pinned=true 时浮窗嵌在主窗口内（悬浮面板，不占停靠位）；
/// Pinned=false 时可以脱离成独立窗口。
/// IsOpen=false 表示用户关闭了浮窗，提醒回落到浮层（以及预留的 Windows 渠道），
/// 之后新事件不会再把浮窗弹回来。
/// 浮窗只在"有提醒"或用户手动唤出时可见，避免空面板长期压住工作区。
/// </summary>
public sealed record FloatingWindowPreferences(
    bool Pinned = true,
    bool IsOpen = true,
    FloatingDelivery Delivery = FloatingDelivery.Toast | FloatingDelivery.Floating,
    double Left = double.NaN,
    double Top = double.NaN,
    double Width = 360,
    double Height = 520)
{
    public bool DeliversToToast => (Delivery & FloatingDelivery.Toast) != 0;
    public bool DeliversToFloating => (Delivery & FloatingDelivery.Floating) != 0;
    public bool DeliversToWindows => (Delivery & FloatingDelivery.Windows) != 0;
    public bool HasPosition => double.IsFinite(Left) && double.IsFinite(Top);
}

/// <summary>应用自有偏好；不改动 Pi 设置，也不写会话记录。</summary>
public sealed class FloatingWindowPreferencesStore
{
    private readonly string _path;
    private readonly object _gate = new();
    // 位置用可空值：未定位时写 null，避免 double.NaN 无法序列化。
    private sealed record Document(int SchemaVersion, bool Pinned, bool IsOpen, int Delivery,
        double? Left, double? Top, double Width, double Height);

    public FloatingWindowPreferencesStore(string path) => _path = Path.GetFullPath(path);

    public FloatingWindowPreferences Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return new();
            if (new FileInfo(_path).Length > 8192) throw new InvalidDataException("浮窗偏好文件过大");
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path));
            if (document is null || document.SchemaVersion != 1 || document.Delivery is < 0 or > 7)
                throw new InvalidDataException("浮窗偏好格式无法识别");
            return new(document.Pinned, document.IsOpen, (FloatingDelivery)document.Delivery,
                document.Left ?? double.NaN, document.Top ?? double.NaN,
                Clamp(document.Width, 280, 900, 360), Clamp(document.Height, 240, 1200, 520));
        }
    }

    public void Save(FloatingWindowPreferences preferences)
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
                    JsonSerializer.Serialize(stream, new Document(1, preferences.Pinned, preferences.IsOpen,
                        (int)preferences.Delivery,
                        double.IsFinite(preferences.Left) ? preferences.Left : null,
                        double.IsFinite(preferences.Top) ? preferences.Top : null,
                        preferences.Width, preferences.Height));
                    stream.Flush(true);
                }
                File.Move(temporary, _path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
