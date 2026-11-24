using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.Fui;

/// <summary>One plugin registration and manager for the lifetime of a workbench project, not per session.</summary>
internal sealed class FumoProjectRuntime
{
    private int _references = 1; // The initial opener owns one temporary reference.
    public PersistentSessionManager Manager { get; }
    public PluginPackageManager Plugins { get; }

    public FumoProjectRuntime(AppDataLayout layout)
    {
        FileChangeJournal.Configure(layout.Root);
        // null asks Core to discover an external backend; explicit empty paths disable discovery.
        Manager = new("", "", layout, ompExecutablePath: "", claudeExecutablePath: "", codexExecutablePath: "", dshExecutablePath: "");
        var state = new PluginStateStore(layout);
        var tools = new PluginToolRegistry(new PluginCapabilityGateway(state));
        Plugins = new(layout, tools, state);
        Manager.NativePluginToolProviders = () => [tools];
        Manager.NativeSystemPromptProvider = new PluginStateSystemPromptProvider(state);
    }

    public void Retain() => Interlocked.Increment(ref _references);

    public ValueTask ReleaseAsync() => Interlocked.Decrement(ref _references) == 0
        ? Plugins.DisposeAsync() : ValueTask.CompletedTask;
}
