using System;
using System.Collections.Generic;
using System.Text.Json;
using Fufucode.Core;

namespace Fufucode.App.Infrastructure;

public sealed record SessionPresentation(bool Pinned = false, bool Archived = false, bool Unread = false,
    List<string>? RowActions = null)
{
    private static string Key(string id) => "session:presentation:v1:" + id;
    public static SessionPresentation Load(DesktopMetadataStore store, string id)
    {
        var json = store.GetPreference(Key(id));
        if (json is null) return new();
        try { return JsonSerializer.Deserialize<SessionPresentation>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }
    public static void Save(DesktopMetadataStore store, string id, SessionPresentation state) => store.SetPreference(Key(id), JsonSerializer.Serialize(state));
}
