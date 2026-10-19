using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core.Backend;

/// <summary>模型目录条目（顶层类型，供 XAML 编译绑定引用）。</summary>
public sealed record ModelEntry(string Id, string DisplayName, long? ContextLength,
    IReadOnlyList<string> ReasoningEfforts, string Provider = "",
    IReadOnlyList<string>? InputModalities = null)
{
    /// <summary>是否声明支持图片输入；目录缺失该信息时为 null（未知，不伪造）。</summary>
    public bool? SupportsImages => InputModalities is { Count: > 0 } modalities
        ? modalities.Contains("image", StringComparer.OrdinalIgnoreCase)
        : null;

    /// <summary>写入设置时使用的展示文本：名称与 id 不同时带上 id，便于手填时对照。</summary>
    public string Label => string.Equals(DisplayName, Id, StringComparison.Ordinal)
        ? Id
        : $"{DisplayName} · {Id}";

    /// <summary>模型在其提供商内的短名（去掉 provider/ 前缀）。</summary>
    public string ShortName =>
        Provider.Length > 0 && Id.StartsWith(Provider + "/", StringComparison.OrdinalIgnoreCase)
            ? Id[(Provider.Length + 1)..]
            : Id;
}

/// <summary>按提供商分组的模型目录，对应"提供商 → 模型"的二级选择。</summary>
public sealed record ModelProviderGroup(string Provider, IReadOnlyList<ModelEntry> Models)
{
    public int Count => Models.Count;
    public string DisplayName => $"{Provider}（{Models.Count}）";

    public static IReadOnlyList<ModelProviderGroup> Group(IEnumerable<ModelEntry> entries) =>
        entries
            .GroupBy(entry => entry.Provider, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ModelProviderGroup(group.Key,
                group.OrderBy(entry => entry.ShortName, StringComparer.OrdinalIgnoreCase).ToArray()))
            .OrderBy(group => group.Provider, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

/// <summary>
/// OpenAI 兼容端点的模型目录：请求 GET {baseUrl}/models 并解析常见字段。
/// 适用于本机 Magpie（127.0.0.1:3425）、Ollama、LM Studio、vLLM 等任何兼容网关——
/// 网关更新模型后，下一次查询自动反映，不需要改客户端。
/// </summary>
public sealed class ModelCatalogClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public ModelCatalogClient(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _ownsHttp = httpClient is null;
    }

    /// <summary>查询模型目录。失败时抛出带可操作信息的异常，不静默返回空列表。</summary>
    public async Task<IReadOnlyList<ModelEntry>> ListAsync(string baseUrl, string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        var url = BuildModelsUrl(baseUrl);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // 许多本地网关不校验密钥，但要求存在 Authorization 头。
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            string.IsNullOrWhiteSpace(apiKey) ? "local" : apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"模型目录请求失败：HTTP {(int)response.StatusCode}（{url}）。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return Parse(document.RootElement);
    }

    /// <summary>解析 /models 响应。兼容 data[] 与 models[] 两种常见外壳。</summary>
    public static IReadOnlyList<ModelEntry> Parse(JsonElement root)
    {
        var array = root.ValueKind == JsonValueKind.Object
            ? new[] { "data", "models" }
                .Select(name => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
                    ? value
                    : (JsonElement?)null)
                .FirstOrDefault(value => value is not null)
            : root.ValueKind == JsonValueKind.Array ? root : null;
        if (array is not { } items) return [];

        var result = new List<ModelEntry>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = String(item, "id") ?? String(item, "name") ?? String(item, "model");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var display = String(item, "display_name") ?? String(item, "name") ?? id;
            long? context = Long(item, "context_length") ?? Long(item, "context_window")
                            ?? Long(item, "max_input_tokens") ?? Long(item, "max_context_length");
            // Magpie 的 id 形如 provider/model 或 group/auto-*；取第一段作为提供商。
            var slash = id!.IndexOf('/');
            var provider = slash > 0 ? id[..slash] : "default";
            result.Add(new ModelEntry(id, display!, context, Efforts(item), provider, Modalities(item)));
        }

        return result
            .GroupBy(entry => entry.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>读取输入模态：兼容 modalities.input（Magpie）与 architecture.input_modalities（Ollama）。</summary>
    private static IReadOnlyList<string> Modalities(JsonElement item)
    {
        JsonElement list;
        if (item.TryGetProperty("modalities", out var modalities) && modalities.ValueKind == JsonValueKind.Object &&
            modalities.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Array)
        {
            list = input;
        }
        else if (item.TryGetProperty("architecture", out var architecture) &&
                 architecture.ValueKind == JsonValueKind.Object &&
                 architecture.TryGetProperty("input_modalities", out var ollama) &&
                 ollama.ValueKind == JsonValueKind.Array)
        {
            list = ollama;
        }
        else
        {
            return [];
        }

        return list.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString()!)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> Efforts(JsonElement item)
    {
        if (!item.TryGetProperty("supported_reasoning_levels", out var levels) ||
            levels.ValueKind != JsonValueKind.Array)
            return [];
        return levels.EnumerateArray()
            .Select(level => level.ValueKind == JsonValueKind.Object ? String(level, "effort") : null)
            .OfType<string>()
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildModelsUrl(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        // 允许用户填写 .../v1 或 .../v1/chat/completions。
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^"/chat/completions".Length];
        return trimmed.EndsWith("/models", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + "/models";
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number)
            ? number
            : null;

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

/// <summary>
/// 模型能力探测：按端点目录判断当前模型是否声明图片输入，进程内缓存 5 分钟。
/// 目录不可达或未声明模态时返回 null（未知），调用方保留原有行为而不是猜测。
/// </summary>
public static class ModelCapabilityProbe
{
    /// <summary>缓存条目上限；超过后按写入顺序淘汰最旧的条目，防止长进程无限增长。</summary>
    public const int MaxCacheEntries = 256;

    private static readonly ConcurrentDictionary<string, (bool? Value, DateTimeOffset At, long Sequence)> Cache = new();
    private static long _sequence;

    /// <summary>当前缓存条目数（诊断与测试用）。</summary>
    public static int CacheCount => Cache.Count;

    /// <summary>清空缓存（测试与诊断用）。</summary>
    public static void ClearCache() => Cache.Clear();

    /// <summary>写入缓存并执行淘汰（测试与诊断用，不发起网络请求）。</summary>
    public static void Remember(string key, bool? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Cache[key] = (value, DateTimeOffset.UtcNow, Interlocked.Increment(ref _sequence));
        TrimCache();
    }

    public static async Task<bool?> SupportsImageInputAsync(string baseUrl, string? apiKey, string modelId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(modelId)) return null;
        var key = baseUrl + "|" + modelId;
        if (TryGet(key, out var cached)) return cached;

        try
        {
            using var client = new ModelCatalogClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var models = await client.ListAsync(baseUrl, apiKey, timeout.Token).ConfigureAwait(false);
            var value = models.FirstOrDefault(model => string.Equals(model.Id, modelId, StringComparison.Ordinal))
                ?.SupportsImages;
            Remember(key, value);
            return value;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGet(string key, out bool? value)
    {
        value = null;
        if (!Cache.TryGetValue(key, out var cached)) return false;
        if (DateTimeOffset.UtcNow - cached.At >= TimeSpan.FromMinutes(5)) return false;
        value = cached.Value;
        return true;
    }

    /// <summary>超过上限时删除写入时间最早的条目，直到回到上限之内（并发写入时以最终计数为准）。</summary>
    private static void TrimCache()
    {
        while (Cache.Count > MaxCacheEntries)
        {
            string? oldestKey = null;
            var oldestSequence = long.MaxValue;
            foreach (var pair in Cache)
            {
                if (pair.Value.Sequence >= oldestSequence) continue;
                oldestSequence = pair.Value.Sequence;
                oldestKey = pair.Key;
            }
            if (oldestKey is null || !Cache.TryRemove(oldestKey, out _)) break;
        }
    }
}
