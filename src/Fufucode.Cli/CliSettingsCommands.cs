using System.Text.Json;
using Fufucode.Core;

namespace Fufucode.Cli;

/// <summary>Settings commands never start Pi. Optional settings accept the literal null to clear them.</summary>
public static class CliSettingsCommands
{
    public const string CredentialStatusKey = "endpointCredentialConfigured";
    public const string CredentialKey = "endpointCredential";
    public const string SupportedKeys = "selectedModel, thinkingLevel, theme, proxyUrl, endpoint, endpointCredential; endpointCredentialConfigured is read-only";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <param name="arguments">Command arguments after settings: get [key] or set key value.</param>
    public static async Task RunAsync(IReadOnlyList<string> arguments, AppSettingsStore settings,
        ICredentialResolver credentials, bool json, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        if (arguments.Count is 1 or 2 && arguments[0] == "get")
        {
            var values = await ReadAsync(settings.Current, arguments.Count == 2 ? arguments[1] : null,
                credentials, cancellationToken);
            if (json) await output.WriteLineAsync(JsonSerializer.Serialize(values, JsonOptions));
            else foreach (var pair in values)
                await output.WriteLineAsync($"{pair.Key}={Format(pair.Value)}");
            return;
        }
        if (arguments.Count == 3 && arguments[0] == "set")
        {
            var key = arguments[1];
            var value = arguments[2];
            if (key == CredentialKey)
            {
                if (settings.Current.Endpoint is not { } endpoint)
                    throw new ArgumentException("Configure endpoint before its credential.");
                if (credentials is not ICredentialStore store)
                    throw new InvalidOperationException("Credential storage is unavailable on this platform; configure the endpoint environment variable externally.");
                if (value == "null") store.Delete(endpoint.ApiKeyEnvironmentVariable);
                else store.Save(endpoint.ApiKeyEnvironmentVariable, value);
                var configured = !string.IsNullOrWhiteSpace(await credentials.ResolveAsync(endpoint.ApiKeyEnvironmentVariable, cancellationToken));
                await output.WriteLineAsync(json ? JsonSerializer.Serialize(new { key, updated = true, configured })
                    : $"{CredentialStatusKey}={(configured ? "true" : "false")}");
                return;
            }
            ValidateWritableKey(key);
            // Validation and persistence are atomic in AppSettingsStore.Update.
            var updated = settings.Update(current => Change(current, key, value));
            if (json) await output.WriteLineAsync(JsonSerializer.Serialize(new { key, updated = true }));
            else await output.WriteLineAsync($"{key}={Format(Value(updated, key))}");
            return;
        }
        throw new ArgumentException("Usage: settings get [key] | settings set <key> <value>.");
    }

    public static async Task SetModelAsync(PersistentSession session, AppSettingsStore settings, string providerModel,
        bool json, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        var model = await SessionModelPreference.ApplyAsync(session, settings, providerModel, cancellationToken);
        if (json) await output.WriteLineAsync(JsonSerializer.Serialize(new { model, saved = true }));
        else await output.WriteLineAsync($"{model} (saved as application default)");
    }

    private static async Task<Dictionary<string, object?>> ReadAsync(AppSettings settings, string? key,
        ICredentialResolver credentials, CancellationToken cancellationToken)
    {
        if (key is not null && key is not (CredentialStatusKey or CredentialKey)) ValidateWritableKey(key);
        var values = new Dictionary<string, object?>();
        var keys = key is null ? new[] { SettingKeys.Model, SettingKeys.ThinkingLevel, SettingKeys.Theme,
            SettingKeys.ProxyUrl, SettingKeys.Endpoint, CredentialStatusKey } : new[] { key };
        foreach (var name in keys)
        {
            if (name is CredentialStatusKey or CredentialKey)
            {
                var resolved = settings.Endpoint is { } endpoint
                    ? await credentials.ResolveAsync(endpoint.ApiKeyEnvironmentVariable, cancellationToken) : null;
                values.Add(name, !string.IsNullOrWhiteSpace(resolved));
            }
            else values.Add(name, Value(settings, name));
        }
        return values;
    }

    private static object? Value(AppSettings settings, string key) => key switch
    {
        SettingKeys.Model => settings.SelectedModel,
        SettingKeys.ThinkingLevel => settings.ThinkingLevel,
        SettingKeys.Theme => settings.Theme,
        SettingKeys.ProxyUrl => settings.ProxyUrl,
        SettingKeys.Endpoint => settings.Endpoint,
        _ => throw new ArgumentException("Unsupported setting key. Supported: " + SupportedKeys)
    };

    private static void ValidateWritableKey(string key)
    {
        if (key is not (SettingKeys.Model or SettingKeys.ThinkingLevel or SettingKeys.Theme or SettingKeys.ProxyUrl or SettingKeys.Endpoint))
            throw new ArgumentException("Unsupported or read-only setting key. Supported: " + SupportedKeys);
    }

    private static AppSettings Change(AppSettings current, string key, string value)
    {
        var optional = value == "null" ? null : value;
        return key switch
        {
            SettingKeys.Model => current with { SelectedModel = ValidateModel(optional) },
            SettingKeys.ThinkingLevel => current with { ThinkingLevel = optional },
            SettingKeys.Theme => current with { Theme = value },
            SettingKeys.ProxyUrl => current with { ProxyUrl = optional },
            SettingKeys.Endpoint => current with { Endpoint = ParseEndpoint(optional) },
            _ => throw new ArgumentException("Unsupported setting key.")
        };
    }

    private static string? ValidateModel(string? value)
    {
        if (value is not null) SessionModelPreference.Parse(value);
        return value;
    }

    private static CompatibleEndpoint? ParseEndpoint(string? value)
    {
        if (value is null) return null;
        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 4)
                throw new ArgumentException("endpoint requires providerName, baseUrl, modelId and apiKeyEnvironmentVariable.");
            string Field(string name) => root.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(field.GetString()) ? field.GetString()! :
                throw new ArgumentException("endpoint requires providerName, baseUrl, modelId and apiKeyEnvironmentVariable.");
            return new CompatibleEndpoint(Field("providerName"), Field("baseUrl"), Field("modelId"), Field("apiKeyEnvironmentVariable"));
        }
        catch (JsonException) { throw new ArgumentException("endpoint must be a valid JSON object or null."); }
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        string text => text,
        bool flag => flag ? "true" : "false",
        _ => JsonSerializer.Serialize(value, JsonOptions)
    };
}
