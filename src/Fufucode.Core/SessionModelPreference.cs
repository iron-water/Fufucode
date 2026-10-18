using System.Text.Json;

namespace Fufucode.Core;

/// <summary>Changes the connected session model and persists a default only after Pi confirms it.</summary>
public static class SessionModelPreference
{
    public static (string Provider, string ModelId) Parse(string providerModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerModel);
        var separator = providerModel.IndexOf('/');
        if (separator < 1 || separator == providerModel.Length - 1 || providerModel.Any(char.IsWhiteSpace))
            throw new ArgumentException("Model must have provider/modelId form.", nameof(providerModel));
        // A model ID may itself contain a slash (for example an organization-qualified ID).
        return (providerModel[..separator], providerModel[(separator + 1)..]);
    }

    public static async Task<string> ApplyAsync(PersistentSession session, AppSettingsStore settings,
        string providerModel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(settings);
        var (provider, modelId) = Parse(providerModel);
        cancellationToken.ThrowIfCancellationRequested();
        if (session.Coordinator.Snapshot is not { Connection: ConnectionState.Connected, Activity: ActivityState.Idle })
            throw new InvalidOperationException("Model selection requires a connected, idle session.");
        var selected = await session.Coordinator.SetModelAsync(provider, modelId, cancellationToken);
        if (!selected.Success) throw new RpcException("Pi set_model failed.");
        var response = await session.Coordinator.QueryStateAsync(cancellationToken);
        if (!response.Success || response.Data is not { ValueKind: JsonValueKind.Object } state ||
            !state.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.Object ||
            ReadString(model, "provider") != provider || ReadString(model, "id") != modelId)
            throw new RpcException("Pi did not confirm the requested model; application default was not saved.");
        cancellationToken.ThrowIfCancellationRequested();
        session.ApplyConfirmedState(state);
        settings.Update(current => current with { SelectedModel = providerModel });
        return providerModel;
    }

    private static string? ReadString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
