using Fufucode.Core;

namespace Fufucode.Fui;

internal sealed partial class TuiController
{
    private FuiPlanSnapshot CurrentPlan
    {
        get
        {
            if (_active is IFuiPlanSession { CurrentPlanSnippet: { Length: > 0 } snippet })
            {
                var live = FuiPlanProjection.FromSnippet(snippet);
                if (live.HasItems) return live;
            }
            return FuiPlanProjection.FromLines(_lines);
        }
    }

    private string CurrentExecutionMode => (_active as IFuiPlanSession)?.ExecutionMode ?? "未提供";

    private void Plan(string action)
    {
        var plan = CurrentPlan;
        if (!plan.HasItems)
        {
            Info("当前会话没有 update_plan 计划。切到 /mode plan 后让 FUMO 先规划。");
            return;
        }
        var choices = plan.Items.Select(item => new FuiChoice(
            item.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"{item.Glyph} {item.Number}. {item.Title}",
            item.StateLabel)).ToArray();
        OpenPicker(action, $"计划 · {plan.Completed}/{plan.Items.Count}", choices,
            options: new(PreserveOrder: true));
    }

    private void JumpToPlan(FuiChoice choice)
    {
        var title = TerminalText.SingleLine(choice.Label);
        var dot = title.IndexOf(". ", StringComparison.Ordinal);
        if (dot >= 0) title = title[(dot + 2)..];
        for (var index = _lines.Count - 1; index >= 0; index--)
        {
            if (_lines[index].Kind != "tool" || !_lines[index].Text.Contains("update_plan", StringComparison.OrdinalIgnoreCase))
                continue;
            if (title.Length != 0 && !_lines[index].Text.Contains(title, StringComparison.Ordinal)) continue;
            _expandTools = true;
            _scrollTarget = index;
            return;
        }
        Info(choice.Label + (choice.Detail is null ? "" : " · " + choice.Detail));
    }

    private void Stats()
    {
        var local = FuiStats.From(_lines, _attachments, _agentActivity, CurrentExecutionMode);
        if (_active is not IFuiWorkbenchSession backend)
        {
            Info(local.Detail(null));
            return;
        }
        Begin("stats", async ct =>
        {
            var usage = (await backend.ExecuteAsync("usage", "", ct)).Notice;
            return new Result(Action: new(local.Detail(usage)));
        }, cancellable: true);
    }

    private void Review()
    {
        Begin("review", async ct =>
        {
            var report = await ChangeRangeQuery.WorkspaceAsync(_active.Project, ct);
            return new Result(Action: new(FuiChangeProjection.Overview(report)));
        }, cancellable: true);
    }

    private void Changes(string argument)
    {
        if (argument.Length != 0)
        {
            Begin("changes", async ct => new Result(Action: new(await Files().DiffAsync(argument, ct))), cancellable: true);
            return;
        }
        Begin("changes", async ct =>
        {
            var report = await ChangeRangeQuery.WorkspaceAsync(_active.Project, ct);
            if (!report.Available) return new Result(Action: new(FuiChangeProjection.Overview(report)));
            if (report.Files.Count == 0) return new Result(Action: new("工作区干净。"));
            var entries = FuiChangeProjection.Entries(report);
            return new Result(Choices: entries.Select(entry => new FuiChoice(
                    entry.File.Path,
                    entry.Glyph + " " + entry.File.Path,
                    entry.Detail)).ToArray(),
                Title: $"Git 变更 · {report.Files.Count}");
        }, cancellable: true);
    }
}
