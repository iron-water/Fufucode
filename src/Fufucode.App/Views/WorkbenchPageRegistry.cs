using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;

namespace Fufucode.App.Views;

internal sealed record WorkbenchPage(string Id, string Title, string Icon, Control Content, string ModuleId);

// Built-in registrations only. Page IDs are reserved for the lifetime of the window,
// including while an optional module is disabled.
internal sealed class WorkbenchPageRegistry : IDisposable
{
    private sealed record Registration(string Id, string Title, string Icon, string ModuleId,
        bool Optional, Func<Control> Create, Action<Control>? Release)
    {
        public WorkbenchPage? Page { get; set; }
    }

    private readonly Dictionary<string, Registration> _registrations = new(StringComparer.Ordinal);
    public IEnumerable<string> KnownPageIds => _registrations.Keys;
    public IEnumerable<WorkbenchPage> Values => _registrations.Values.Select(item => item.Page).OfType<WorkbenchPage>();
    public WorkbenchPage this[string id] => _registrations[id].Page ?? throw new InvalidOperationException("Page is disabled: " + id);
    public bool ContainsKey(string id) => _registrations.TryGetValue(id, out var item) && item.Page is not null;

    public void RegisterCore(string id, string title, string icon, Control content) =>
        Register(id, title, icon, "core", false, () => content, null);

    public void RegisterOptional(string id, string title, string icon, string moduleId,
        Func<Control> create, Action<Control> release) => Register(id, title, icon, moduleId, true, create, release);

    private void Register(string id, string title, string icon, string moduleId, bool optional,
        Func<Control> create, Action<Control>? release)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (_registrations.ContainsKey(id)) throw new InvalidOperationException("Page ID is already reserved: " + id);
        var item = new Registration(id, title, icon, moduleId, optional, create, release);
        item.Page = new(id, title, icon, create(), moduleId);
        _registrations.Add(id, item);
    }

    public void SetEnabled(string id, bool enabled, Action<Control> detach)
    {
        var item = _registrations[id];
        if (!item.Optional) throw new InvalidOperationException("Core pages cannot be disabled: " + id);
        if (enabled == (item.Page is not null)) return;
        if (enabled)
        {
            var content = item.Create();
            item.Page = new(item.Id, item.Title, item.Icon, content, item.ModuleId);
        }
        else
        {
            var page = item.Page!;
            detach(page.Content);
            item.Release?.Invoke(page.Content);
            item.Page = null;
        }
    }

    public void Dispose()
    {
        foreach (var item in _registrations.Values)
        {
            if (item.Page is { } page) item.Release?.Invoke(page.Content);
            item.Page = null;
        }
    }
}
