using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Fufucode.App.Abstractions;

namespace Fufucode.App.ViewModels;

public sealed partial class TimelineViewModel
{
    internal void RefreshTurnSummaries()
    {
        var turn = new List<AssistantMessageViewModel>();
        foreach (var entry in Entries)
        {
            if (entry is UserMessageViewModel) { Summarize(turn); turn.Clear(); }
            else if (entry is AssistantMessageViewModel assistant) turn.Add(assistant);
        }
        Summarize(turn);
    }
    private static void Summarize(List<AssistantMessageViewModel> turn)
    {
        foreach (var message in turn) { message.ChangeSummary = ""; message.ArtifactSummary = ""; }
        if (turn.Count == 0) return;
        var changes = new List<string>(); var artifacts = new List<string>();
        foreach (var tool in turn.SelectMany(m => m.Blocks).OfType<ToolCallBlockViewModel>())
        {
            if (!tool.BadgeIsSuccess || tool.IsErrorOutput || tool.ToolName is not ("write" or "edit")) continue;
            try
            {
                using var json = JsonDocument.Parse(tool.InputJson);
                if (!json.RootElement.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String) continue;
                var file = path.GetString()!;
                changes.Add((tool.ToolName == "write" ? "写入 · " : "编辑 · ") + file);
                if (tool.ToolName == "write") artifacts.Add(file);
            }
            catch (JsonException) { }
        }
        turn[^1].ChangeSummary = string.Join("\n", changes.Distinct());
        turn[^1].ArtifactSummary = string.Join("\n", artifacts.Distinct());
    }
}
