using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fufucode.Fufu.Settings;

/// <summary>
/// FUFU 宠物自身设置（行为/外观/生命周期）。与 FUMO 会话用的 AppSettingsStore 分离：
/// FUMO 端点与模型配置走同一数据根下的 settings.json（Fufucode.Core schema），
/// 宠物行为设置走本文件 fufu-settings.json，两者互不覆盖。
/// </summary>
public sealed record FufuSettings(
    int Version = 1,
    // 自由移动总开关；关闭时桌宠停留在原地（仍可拖动）。
    bool FreeRoam = true,
    // 暂停移动（临时冻结；与 FreeRoam 区分：暂停不写入用户偏好语义，仅本次运行）。
    bool MovementPaused = false,
    // 登录自启动；默认关闭，由用户显式开启（任务书硬性要求）。
    bool AutoStart = false,
    // 透明区域鼠标穿透；关闭时整个包围盒都可命中。
    bool ClickThrough = true,
    // 角色缩放倍率（占位渲染期有效；Live2D 接入后同义生效）。
    double Scale = 1.0,
    // 上次退出时的窗口位置（屏幕坐标）；null 表示首次启动。
    double? PositionX = null,
    double? PositionY = null,
    // 主动行为总开关（FUFU-05）：关闭时事件规则与看门狗全部静默，授权随时可收回。
    bool ProactiveEnabled = true,
    // 关注全部 Fufucode 会话；false 时仅 WatchedSessionId 驱动通知与桌宠状态。
    bool WatchAllSessions = true,
    // 关注的会话 id（WatchAllSessions=false 时生效）。
    string? WatchedSessionId = null);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FufuSettings))]
internal partial class FufuSettingsJsonContext : JsonSerializerContext;

public sealed class FufuSettingsStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private FufuSettings _current = new();

    public FufuSettingsStore(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        _path = Path.Combine(dataRoot, "fufu-settings.json");
        _current = Load();
        LastLoadError = null;
    }

    public event Action<FufuSettings>? Changed;
    public FufuSettings Current { get { lock (_gate) return _current; } }
    public string? LastLoadError { get; private set; }

    private FufuSettings Load()
    {
        if (!File.Exists(_path)) return new FufuSettings();
        try
        {
            var settings = JsonSerializer.Deserialize(File.ReadAllText(_path),
                FufuSettingsJsonContext.Default.FufuSettings);
            return settings ?? new FufuSettings();
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            LastLoadError = error.GetType().Name;
            return new FufuSettings();
        }
    }

    public FufuSettings Update(Func<FufuSettings, FufuSettings> change)
    {
        FufuSettings next;
        lock (_gate)
        {
            next = change(_current) with { Version = 1 };
            if (next == _current) return _current;
            var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(next, FufuSettingsJsonContext.Default.FufuSettings));
                File.Move(temp, _path, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            _current = next;
        }
        Changed?.Invoke(next);
        return next;
    }
}
