using System.Collections.ObjectModel;
using Avalonia.Controls;
using Fufucode.Fufu.Bot;

namespace Fufucode.Fufu.Pet;

public sealed record ResidentTaskRow(string State, string Title, string SourceText, string Detail, string Updated);

/// <summary>FUFU 常驻任务状态窗口；只读观察，不开放自动执行。</summary>
public sealed partial class ResidentTaskWindow : Window
{
    private readonly ResidentTaskStore _tasks;
    private readonly ObservableCollection<ResidentTaskRow> _rows = new();

    public ResidentTaskWindow(ResidentTaskStore tasks)
    {
        _tasks = tasks;
        InitializeComponent();
        TaskList.ItemsSource = _rows;
        RefreshButton.Click += (_, _) => Refresh();
        CloseButton.Click += (_, _) => Close();
        Refresh();
    }

    private void Refresh()
    {
        _rows.Clear();
        foreach (var task in _tasks.Tasks.OrderByDescending(x => x.UpdatedAt))
        {
            var detail = task.State switch
            {
                ResidentTaskState.Succeeded => task.Result ?? "任务已完成",
                ResidentTaskState.Failed => task.Error ?? "任务失败",
                ResidentTaskState.Awaiting => task.Error ?? "等待审批",
                _ => task.Prompt,
            };
            _rows.Add(new(
                task.State switch
                {
                    ResidentTaskState.Queued => "排队",
                    ResidentTaskState.Running => "运行中",
                    ResidentTaskState.Awaiting => "待审批",
                    ResidentTaskState.Succeeded => "已完成",
                    ResidentTaskState.Failed => "失败",
                    ResidentTaskState.Cancelled => "已取消",
                    _ => task.State.ToString(),
                },
                task.Title,
                $"{task.Source} · {task.SessionId ?? task.Subject}",
                detail,
                task.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm")));
        }
    }
}
