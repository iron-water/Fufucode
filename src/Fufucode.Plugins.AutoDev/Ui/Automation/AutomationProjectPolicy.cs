using System;
using System.Collections.Generic;
using System.IO;

namespace Fufucode.App.Features.Automation;

/// <summary>项目首次进入工作台时的策略判断结果。</summary>
public sealed record AutomationEntryPolicy(
    bool IsFirstVisit,
    bool IsReady,
    string ModeText,
    string Reason,
    string NextAction)
{
    /// <summary>就绪且首次访问 = 新项目默认自动开发；已有项目沿用原模式与在途身份。</summary>
    public static AutomationEntryPolicy ForReady(bool hasGoal, bool hasBackend, bool firstVisit)
    {
        if (firstVisit) return new(true, true, "自动开发（默认）",
            hasGoal ? "新项目默认自动开发；已有目标，按委托范围自行规划推进。"
                    : "新项目默认自动开发；等待目标。未收到目标前不启动模型、不空转。",
            hasGoal ? "在委托范围内规划并派工" : "在主控对话提交目标或补充要求");
        if (!hasBackend) return new(false, false, "需配置执行后端",
            "没有已注册的执行身份或可用插件；不启动空模型，也不回显假成功。",
            "配置原生内核或后端插件后再提交目标");
        if (!hasGoal) return new(false, true, "等待目标",
            "已有项目但缺少当前目标版本；未收到目标前不派工。",
            "在主控对话提交目标或补充要求");
        return new(false, true, "按原模式继续", "沿用该项目已有模式、暂停状态与在途身份，不被默认变更覆盖。", "按当前状态对账后继续");
    }
}

/// <summary>
/// 逐项目只读观察标记：只区分“首次进入的工作台”和“已有项目”。
/// 这是本模块自己的文件，不写入 U1 数据库，也不改变任何已存在模式、暂停或在途身份。
/// </summary>
public sealed class AutomationWorkspaceMarker
{
    public const string FileName = "automation-v4-workspaces.json";
    private readonly string _path;
    private readonly HashSet<string> _seen;

    public AutomationWorkspaceMarker(string dataRoot)
    {
        var directory = string.IsNullOrWhiteSpace(dataRoot)
            ? Fufucode.Core.AppDataLayout.DefaultRoot()
            : dataRoot;
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, FileName);
        _seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(_path)) return;
            foreach (var line in File.ReadAllLines(_path))
                if (line.Trim().Length > 0) _seen.Add(line.Trim());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 读不到标记时按“已见过”处理：宁可沿用旧状态，也不把老项目当成新项目重新默认。
            _seen.Clear();
            _seen.Add("*");
        }
    }

    public bool IsFirstVisit(string projectKey) => !_seen.Contains("*") && !_seen.Contains(projectKey);

    /// <summary>标记已见过该项目。失败时保留原文件并把错误交给调用方显示。</summary>
    public bool TryMark(string projectKey, out string? error)
    {
        error = null;
        if (!_seen.Add(projectKey)) return true;
        try
        {
            File.WriteAllLines(_path, _seen);
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            _seen.Remove(projectKey);
            error = failure.Message;
            return false;
        }
    }
}
