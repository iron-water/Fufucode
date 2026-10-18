using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.Abstractions;

namespace Fufucode.App.ViewModels;

/// <summary>工具组状态：由组内子工具聚合得出。</summary>
public enum ToolGroupState
{
    Running,
    Completed,
    Error,
    Cancelled,
}

/// <summary>
/// 一次回合里连续工具调用的展示组。
///
/// 设计依据（Claude Code / Codex 的共同做法）：
/// - 组默认折叠成一行；运行中只显示动态状态（"正在读取 foo…"），完成后显示计数摘要（"3 个工具"）。
/// - 组状态由子工具聚合，不单独维护；工具结果只回填子工具，不重建组。
/// - 失败或需要授权时强制展开，不能因为折叠而隐藏需要人处理的状态。
/// </summary>
public sealed partial class ToolGroupViewModel : ObservableObject
{
    private readonly List<ToolCallBlockViewModel> _tools = new();
    private bool _isExpanded;
    private ToolGroupState _state = ToolGroupState.Running;
    private string _summaryText = string.Empty;
    private string? _latestHint;

    public ToolGroupViewModel(string groupId)
    {
        GroupId = groupId;
    }

    /// <summary>稳定身份：取首个子工具的 ToolCallId，流式刷新与历史重放都保持一致。</summary>
    public string GroupId { get; }

    public ObservableCollection<ToolCallBlockViewModel> Tools { get; } = new();

    public ToolGroupState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(IsCancelled));
        }
    }

    public bool IsActive => State == ToolGroupState.Running;
    public bool HasError => State == ToolGroupState.Error;
    public bool IsCancelled => State == ToolGroupState.Cancelled;

    /// <summary>一行摘要：运行中是现在进行时，结束后是计数。</summary>
    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    /// <summary>运行中最新一条动作提示（"└ path" 那一行）。</summary>
    public string? LatestHint
    {
        get => _latestHint;
        private set
        {
            if (!SetProperty(ref _latestHint, value)) return;
            OnPropertyChanged(nameof(HasHint));
        }
    }

    public bool HasHint => IsActive && !string.IsNullOrEmpty(LatestHint);

    /// <summary>是否展开明细。用户手动展开后置 <see cref="IsUserPinned"/>，回合结束不再自动收起。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            if (value) IsUserPinned = true;
            OnPropertyChanged(nameof(ExpandGlyph));
        }
    }

    /// <summary>用户是否手动展开过。回合结束折叠时跳过这些组。</summary>
    public bool IsUserPinned { get; private set; }

    public string ExpandGlyph => IsExpanded ? "▾" : "▸";

    public int Count => _tools.Count;
    public int FailureCount => _tools.Count(tool => tool.Status == ToolCallStatus.Failed || tool.IsErrorOutput);

    public void Add(ToolCallBlockViewModel tool)
    {
        _tools.Add(tool);
        Tools.Add(tool);
        tool.PropertyChanged += OnToolChanged;
        Recompute();
    }

    /// <summary>移除订阅，避免组被丢弃后仍收到子工具通知。</summary>
    public void Detach()
    {
        foreach (var tool in _tools) tool.PropertyChanged -= OnToolChanged;
    }

    private void OnToolChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ToolCallBlockViewModel.Status)
            or nameof(ToolCallBlockViewModel.StatusText)
            or nameof(ToolCallBlockViewModel.Output)
            or nameof(ToolCallBlockViewModel.IsErrorOutput))
        {
            Recompute();
        }
    }

    /// <summary>按子工具状态聚合组状态，并重算摘要。</summary>
    public void Recompute()
    {
        var running = _tools.Where(tool => tool.Status is ToolCallStatus.Pending or ToolCallStatus.Running).ToArray();
        var failed = _tools.Any(tool => tool.Status == ToolCallStatus.Failed || tool.IsErrorOutput);
        var cancelled = _tools.Count > 0 && _tools.All(tool => tool.Status == ToolCallStatus.Cancelled);

        State = running.Length > 0
            ? ToolGroupState.Running
            : cancelled ? ToolGroupState.Cancelled
            : failed ? ToolGroupState.Error
            : ToolGroupState.Completed;

        if (State == ToolGroupState.Running && running.Length > 0)
        {
            var active = running[0];
            LatestHint = DescribeTarget(active);
            SummaryText = running.Length > 1
                ? $"正在并行运行 {running.Length} 个工具…"
                : $"正在{DescribeAction(active)}…";
        }
        else
        {
            LatestHint = null;
            SummaryText = BuildCompletedSummary();
        }

        // 失败或取消需要人看见：强制展开，不能被折叠隐藏。
        if (State is ToolGroupState.Error or ToolGroupState.Cancelled && !IsExpanded)
            IsExpanded = true;
    }

    private string BuildCompletedSummary()
    {
        var parts = new List<string> { $"{_tools.Count} 个工具" };
        var failures = FailureCount;
        if (failures > 0) parts.Add($"{failures} 个失败");
        return string.Join(" · ", parts);
    }

    /// <summary>把工具名翻译成中文动作。</summary>
    private static string DescribeAction(ToolCallBlockViewModel tool) => tool.ToolName switch
    {
        "read_file" or "read" => "读取",
        "write_file" or "write" => "写入",
        "edit_file" or "edit" or "apply_patch" => "编辑",
        "run_command" or "bash" or "shell" or "exec" => "运行命令",
        "list_dir" or "list" => "浏览目录",
        "grep_search" or "grep" => "搜索",
        "glob_files" or "glob" => "查找文件",
        _ => "调用 " + tool.ToolName,
    };

    /// <summary>从工具入参里取一个便于辨认的目标；参数尚未组装完成时返回 null。</summary>
    private static string? DescribeTarget(ToolCallBlockViewModel tool)
    {
        if (string.IsNullOrWhiteSpace(tool.InputJson)) return null;
        try
        {
            using var json = JsonDocument.Parse(tool.InputJson);
            var root = json.RootElement;
            foreach (var key in new[] { "path", "file", "pattern", "query", "command" })
            {
                if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString();
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    return text.Length <= 80 ? text : text[..77] + "…";
                }
            }
        }
        catch (JsonException)
        {
            // 半截 JSON：不展示未完成的参数。
        }

        return null;
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>回合结束：收起用户未手动展开的组。</summary>
    public void CollapseOnTurnSettled()
    {
        if (!IsUserPinned) IsExpanded = false;
    }
}
