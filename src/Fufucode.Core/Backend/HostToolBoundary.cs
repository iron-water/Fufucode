namespace Fufucode.Core.Backend;

/// <summary>Composition-root capability restriction, applied after all optional tool wrappers.</summary>
internal sealed class HostToolBoundary(IPureToolDispatcher inner, Func<string, bool> allowed) : IPureToolDispatcher
{
    public Task<PureToolResult> DispatchAsync(PureToolCall call, CancellationToken cancellationToken = default) =>
        allowed(call.Name) ? inner.DispatchAsync(call, cancellationToken) :
            Task.FromResult(new PureToolResult(call.Id, false, "The host execution role does not permit this tool."));
}
