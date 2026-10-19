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

    /// <summary>
    /// 汇总一轮所有助手段落里的文件改动，只放到本回合最后一条消息上，
    /// 避免中间过程每段都挂一张卡。
    /// </summary>
    private static void Summarize(List<AssistantMessageViewModel> turn)
    {
        foreach (var message in turn)
        {
            message.ChangeSummary = "";
            message.ArtifactSummary = "";
            message.ChangeCard.Clear();
        }

        if (turn.Count == 0) return;

        var changes = new List<string>();
        var artifacts = new List<string>();
        var card = turn[^1].ChangeCard;

        foreach (var tool in turn.SelectMany(m => m.Blocks).OfType<ToolCallBlockViewModel>())
        {
            if (!tool.BadgeIsSuccess || tool.IsErrorOutput) continue;
            if (!TryReadPath(tool, out var file)) continue;

            // 各后端的工具命名不同（write/write_file/Write/create_file…），按语义归类而不是按后端分支。
            if (!TryClassify(tool.ToolName, out var isWrite)) continue;

            changes.Add((isWrite ? "写入 · " : "编辑 · ") + file);
            if (isWrite) artifacts.Add(file);

            var (additions, deletions) = StatsFor(tool, isWrite);
            card.Add(tool.ToolCallId, file, additions, deletions);
        }

        var last = turn[^1];
        last.ChangeSummary = string.Join(Environment.NewLine, changes.Distinct());
        // ArtifactSummary 的赋值会一并刷新 ShowArtifactBlock（依赖变更卡是否已列出文件）。
        last.ArtifactSummary = string.Join(Environment.NewLine, artifacts.Distinct());
        last.RaiseSummariesChanged();
    }

    /// <summary>不同后端对同一语义的工具命名不同，这里按名字归类，视图层不判断后端身份。</summary>
    private static bool TryClassify(string toolName, out bool isWrite)
    {
        isWrite = false;
        var name = toolName.Trim().ToLowerInvariant();
        if (name.Length == 0) return false;

        // 只认"改文件"的工具；读取/搜索/执行命令不进入变更清单。
        if (name is "write" or "write_file" or "create_file" or "writefile" or "createfile")
        {
            isWrite = true;
            return true;
        }

        return name is "edit" or "edit_file" or "editfile" or "str_replace" or "str_replace_editor"
            or "apply_patch" or "replace_in_file";
    }

    private static bool TryReadPath(ToolCallBlockViewModel tool, out string path)
    {
        path = string.Empty;
        try
        {
            using var json = JsonDocument.Parse(tool.InputJson);
            foreach (var key in new[] { "path", "file_path", "filePath", "filename", "file" })
            {
                if (!json.RootElement.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
                    continue;
                path = value.GetString() ?? string.Empty;
                if (path.Length > 0) return true;
            }
            return false;
        }
        catch (JsonException) { return false; }
    }

    private static (int Additions, int Deletions) StatsFor(ToolCallBlockViewModel tool, bool isWrite)
    {
        try
        {
            using var json = JsonDocument.Parse(tool.InputJson);
            return TurnChangeSummaryViewModel.EstimateStats(json.RootElement, isWrite);
        }
        catch (JsonException) { return (0, 0); }
    }
}
