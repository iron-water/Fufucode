using System;
using System.Collections.Generic;
using System.Text.Json;
using Fufucode.Core;

namespace Fufucode.App.Infrastructure;

public sealed record SessionPresentation(bool Pinned = false, bool Archived = false, bool Unread = false,
    List<string>? RowActions = null,
    /// <summary>用户亲手改过名字；自动取名永远不能覆盖它。</summary>
    bool NameUserSet = false,
    /// <summary>已经尝试过自动取名，避免每次回合结束重复调用。</summary>
    bool NameAutoAttempted = false,
    /// <summary>已经发送过第一条用户消息；未成立的空会话不进入会话列表。</summary>
    bool HasUserMessage = false)
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
