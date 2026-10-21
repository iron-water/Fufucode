using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fufucode.Core;

public static class SettingKeys
{
    public const string Model = "selectedModel";
    public const string ThinkingLevel = "thinkingLevel";
    public const string Theme = "theme";
    public const string ProxyUrl = "proxyUrl";
    public const string Endpoint = "endpoint";
    public const string UpdateFeedUrl = "updateFeedUrl";
    public const string AutoCheckUpdates = "autoCheckUpdates";
    public const string GitHubRepository = "githubRepository";
    public const string Performance = "performance";
}

/// <summary>
/// 可选的内存优化开关。默认全部关闭 = 高性能模式；
/// 每项都可独立开启，开启后由各子系统在运行时读取。
/// </summary>
public sealed record PerformanceSettings(
    // 软件渲染：不加载 ANGLE/D3D/显卡驱动，工作集可降 130 MB 以上；需要重启。
    bool SoftwareRendering = false,
    // 关闭玻璃/模糊等重特效采样；立即生效。
    bool DisableVisualEffects = false,
    // 终端回滚从 1500 行降到 500 行；对新终端生效。
    bool ReduceTerminalScrollback = false,
    // 浏览器后台页签降内存：非活动页签设 MemoryUsageTargetLevel.Low，且不提前创建 controller。
    bool ReduceBackgroundBrowserMemory = false);

/// <summary>
/// 一个 OpenAI 兼容端点。ModelId 是当前选中的模型；Models 是该端点可用的模型清单
/// （从 /models 拉取后保存，避免每次重新获取）。
/// </summary>
public sealed record CompatibleEndpoint(string ProviderName, string BaseUrl, string ModelId,
    string ApiKeyEnvironmentVariable,
    // 端点下可用的全部模型 id；旧配置没有该字段时为空，回退到 ModelId。
    IReadOnlyList<string>? Models = null)
{
    /// <summary>可用于选择的模型：保存的清单，缺失时回退到当前模型。</summary>
    public IReadOnlyList<string> AvailableModels =>
        Models is { Count: > 0 } models ? models :
        string.IsNullOrWhiteSpace(ModelId) ? [] : [ModelId];
}

/// <summary>
/// 后台常驻行为。默认关闭：关闭窗口即退出，不隐式常驻。
/// 独立于性能设置，避免把行为开关混进渲染/内存档位。
/// </summary>
public sealed record BackgroundSettings(bool CloseToTray = false);

public sealed record AppSettings(int Version = 1, string? SelectedModel = null,
    string? ThinkingLevel = null, string Theme = "system", string? ProxyUrl = null,
    CompatibleEndpoint? Endpoint = null,
    string? UpdateFeedUrl = null,
    bool AutoCheckUpdates = false,
    // 双渠道更新：开发构建默认只扫描本地；正式发布版在构建时写入 GitHub 仓库坐标，
    // 使其自动对齐 GitHub Release。设置页不暴露这个字段。
    string? GitHubRepository = null,
    // 模型选择器里置顶的模型 id（可跨多个提供商）。
    IReadOnlyList<string>? PinnedModels = null,
    // 用户手动选择的 Subagent 档位；插件安装后的首次默认值为 faster。
    string SubagentTier = "faster",
    // 用户在模型选择器里关闭掉的关键字快捷项。
    IReadOnlyList<string>? DismissedModelKeywords = null,
    // 后台常驻（托盘）行为；旧配置没有该字段时按默认“关窗即退出”。
    BackgroundSettings? Background = null,
    // 内存优化开关；null 视为全部关闭（高性能模式）。
    PerformanceSettings? Performance = null);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal partial class AppSettingsJsonContext : JsonSerializerContext;

/// <summary>Atomic JSON settings with a change event. Invalid files are preserved for diagnosis.</summary>
public sealed class AppSettingsStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private AppSettings _current;
    private bool _newerVersion;

    public AppSettingsStore(AppDataLayout layout)
    {
        layout.EnsureCreated();
        _path = Path.Combine(layout.Root, "settings.json");
        _current = Load();
    }

    public event Action<AppSettings>? Changed;
    public string? LastLoadError { get; private set; }
    public AppSettings Current { get { lock (_gate) return _current; } }

    private AppSettings Load()
    {
        if (!File.Exists(_path)) return new AppSettings();
        try
        {
            var json = File.ReadAllText(_path);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Settings root must be an object.");
            var version = document.RootElement.TryGetProperty("version", out var value) ? value.GetInt32() : 0;
            if (version > 1)
            {
                _newerVersion = true;
                throw new InvalidOperationException("Settings were written by a newer app version.");
            }
            if (version == 0)
            {
                // Migrate the early flat model/thinking names without rewriting the file until the next edit.
                var root = document.RootElement;
                return Validate(new AppSettings(SelectedModel: ReadString(root, "model"),
                    ThinkingLevel: ReadString(root, "thinking"), Theme: ReadString(root, "theme") ?? "system"));
            }
            return Validate(JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)
                ?? throw new JsonException("Settings are empty."));
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidOperationException or ArgumentException)
        {
            LastLoadError = error.GetType().Name;
            return new AppSettings();
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        AppSettings next;
        lock (_gate)
        {
            if (_newerVersion) throw new InvalidOperationException("A newer settings file cannot be overwritten.");
            next = Validate(change(_current) with { Version = 1 });
            if (next == _current) return _current;
            var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(next, AppSettingsJsonContext.Default.AppSettings));
                if (LastLoadError is not null && File.Exists(_path))
                    File.Copy(_path, _path + ".before-repair-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff") + ".bak");
                ReplaceSettings(temp);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            _current = next;
            LastLoadError = null;
        }
        Changed?.Invoke(next);
        return next;
    }

    private void ReplaceSettings(string temp)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(temp, _path, overwrite: true); return; }
            catch (IOException error) when (OperatingSystem.IsWindows() && attempt < 4 &&
                (error.HResult & 0xffff) is 32 or 33)
            { Thread.Sleep(50 * (attempt + 1)); }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows() && attempt < 4 &&
                File.Exists(_path) && (File.GetAttributes(_path) & FileAttributes.ReadOnly) == 0)
            { Thread.Sleep(50 * (attempt + 1)); }
        }
    }

    private static AppSettings Validate(AppSettings settings)
    {
        if (settings.Version != 1) throw new ArgumentException("Unsupported settings version.");
        if (settings.Theme is not ("system" or "light" or "dark")) throw new ArgumentException("Invalid theme.");
        // 档位由各引擎自行声明（Pi 动态上报，其余后端按实现给出）；这里只挡住明显的脏值，
        // 不再维护跨后端的固定白名单，否则新档位或新后端会被设置层提前拒绝。
        if (settings.ThinkingLevel is { } level &&
            (level.Length is 0 or > 32 || !level.All(c => char.IsAsciiLetterLower(c) || c is '-' or '_')))
            throw new ArgumentException("Invalid thinking level.");
        if (settings.ProxyUrl is { } proxy && (!Uri.TryCreate(proxy, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0))
            throw new ArgumentException("Invalid proxy URL.");
        if (settings.Endpoint is { } endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint.ProviderName) ||
                !endpoint.ProviderName.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ||
                (string.IsNullOrWhiteSpace(endpoint.ModelId) && (endpoint.Models is not { Count: > 0 })) ||
                !Uri.TryCreate(endpoint.BaseUrl, UriKind.Absolute, out var endpointUri) ||
                endpointUri.Scheme is not ("http" or "https") || endpointUri.UserInfo.Length > 0 || endpointUri.Query.Length > 0 ||
                string.IsNullOrWhiteSpace(endpoint.ApiKeyEnvironmentVariable) ||
                !(char.IsAsciiLetter(endpoint.ApiKeyEnvironmentVariable[0]) || endpoint.ApiKeyEnvironmentVariable[0] == '_') ||
                !endpoint.ApiKeyEnvironmentVariable.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                throw new ArgumentException("Invalid compatible endpoint.");
        }
        if (settings.UpdateFeedUrl is { Length: > 0 } feed &&
            (!Uri.TryCreate(feed, UriKind.Absolute, out var feedUri) ||
             feedUri.Scheme is not ("http" or "https" or "file")))
            throw new ArgumentException("Invalid update feed URL.");
        if (settings.SubagentTier is not ("faster" or "stronger"))
            throw new ArgumentException("Invalid subagent tier.");
        return settings;
    }

    /// <summary>Builds Pi's models.json shape; no secret values enter settings or this export.</summary>
    public string? BuildPiModelsJson()
    {
        var endpoint = Current.Endpoint;
        if (endpoint is null) return null;
        var model = new { providers = new Dictionary<string, object>
        {
            [endpoint.ProviderName] = new { baseUrl = endpoint.BaseUrl, api = "openai-completions",
                apiKey = "$" + endpoint.ApiKeyEnvironmentVariable,
                models = endpoint.AvailableModels.Select(id => new { id }).ToArray() }
        } };
        return JsonSerializer.Serialize(model);
    }
}

public interface ICredentialResolver
{
    ValueTask<string?> ResolveAsync(string environmentVariable, CancellationToken cancellationToken = default);
}

/// <summary>Optional credential mutation capability; secret values never enter AppSettings.</summary>
public interface ICredentialStore : ICredentialResolver
{
    void Save(string environmentVariable, string password);
    void Delete(string environmentVariable);
}

/// <summary>环境变量凭据解析（进程环境优先）。</summary>
public sealed class EnvironmentCredentialResolver : ICredentialResolver
{
    public ValueTask<string?> ResolveAsync(string environmentVariable, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Environment.GetEnvironmentVariable(environmentVariable));
    }
}
