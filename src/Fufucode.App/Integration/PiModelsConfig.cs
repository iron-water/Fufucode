using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fufucode.App.Integration;

/// <summary>models.json 里一个 provider 的呈现信息；apiKey 永不保留原始值。</summary>
public sealed record PiProviderInfo(string Name, string? BaseUrl, string? Api,
    IReadOnlyList<string> ModelIds, string KeyText, bool KeyIsCaution);

/// <summary>
/// 只读解析 Pi 的 models.json（{providers:{name:{baseUrl,api,apiKey,models:[{id}]}}}）。
/// apiKey 引用一律脱敏：$VAR/${VAR} 只呈现变量名与“是否已设置”（不读值），
/// 字面量只显示“字面密钥（已隐藏）”。解析失败如实返回错误。
/// </summary>
public static class PiModelsConfig
{
    public sealed record Snapshot(IReadOnlyList<PiProviderInfo> Providers,
        bool FileExists, string? Error, string Path);

    private static readonly Regex EnvReference =
        new(@"^\$\{?([A-Za-z_][A-Za-z0-9_]*)\}?$", RegexOptions.Compiled);

    public static Snapshot Read(string agentDirectory)
    {
        var path = Path.Combine(agentDirectory, "models.json");
        if (!File.Exists(path))
        {
            return new Snapshot([], false, null, path);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("providers", out var providers) ||
                providers.ValueKind != JsonValueKind.Object)
            {
                return new Snapshot([], true, "models.json 缺少 providers 对象", path);
            }

            var list = new List<PiProviderInfo>();
            foreach (var provider in providers.EnumerateObject())
            {
                if (provider.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var p = provider.Value;
                string? Text(JsonElement e, string name) =>
                    e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                        ? v.GetString()
                        : null;

                var modelIds = new List<string>();
                if (p.TryGetProperty("models", out var models) &&
                    models.ValueKind == JsonValueKind.Array)
                {
                    foreach (var model in models.EnumerateArray())
                    {
                        var id = Text(model, "id") ?? Text(model, "name");
                        if (id is { Length: > 0 })
                        {
                            modelIds.Add(id);
                        }
                    }
                }

                var (keyText, caution) = DescribeApiKey(Text(p, "apiKey"));
                list.Add(new PiProviderInfo(provider.Name, Text(p, "baseUrl"), Text(p, "api"),
                    modelIds, keyText, caution));
            }

            return new Snapshot(list, true, null, path);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new Snapshot([], true, $"models.json 解析失败：{ex.GetBaseException().Message}", path);
        }
    }

    /// <summary>apiKey 脱敏描述：环境变量引用只报告变量名与存在性；字面量隐藏。</summary>
    internal static (string Text, bool Caution) DescribeApiKey(string? apiKey)
    {
        if (apiKey is null)
        {
            return ("未配置 apiKey", false);
        }

        var match = EnvReference.Match(apiKey);
        if (match.Success)
        {
            var name = match.Groups[1].Value;
            var set = Environment.GetEnvironmentVariable(name) is { Length: > 0 };
            return ($"环境变量 {name}（{(set ? "已设置" : "未设置")}）", !set);
        }

        return ("字面密钥（已隐藏）", true);
    }
}
