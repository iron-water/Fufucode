using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.App.Integration;

internal sealed class GraphApplicationToolDispatcher(IPluginToolHandler handler)
    : IPureToolDispatcher, IPluginToolCatalog, IApplicationToolCatalog
{
    public IReadOnlyList<string> ActiveToolNames => handler.Registrations.Select(item => item.Name).ToArray();
    public IReadOnlyList<string> ApplicationToolNames => ActiveToolNames;
    public IReadOnlyList<PluginToolRegistration> ActiveRegistrations => handler.Registrations;
    public Task<PureToolResult> DispatchAsync(PureToolCall call, CancellationToken cancellationToken = default) =>
        handler.ExecuteAsync(call, cancellationToken);
}
