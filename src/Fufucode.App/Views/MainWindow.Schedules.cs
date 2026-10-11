using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Fufucode.App.Infrastructure;

namespace Fufucode.App;

public partial class MainWindow
{
    private WorkbenchScheduleStore? _schedules;
    private bool _checkingSchedules;
    private async void CheckSchedules(object? sender, EventArgs e)
    {
        if (_checkingSchedules || _closed || _switching || _vm is null || _vm.Input.IsTurnActive || _activeScheduledId is not null) return;
        _checkingSchedules = true;
        try
        {
            _schedules ??= new WorkbenchScheduleStore(_customization.DataRoot);
            _schedules.Reload();
            foreach (var task in _schedules.Tasks.Where(t => t.State == "pending" && t.DueAt <= DateTimeOffset.UtcNow && t.SessionId == CurrentSessionId).ToArray())
            {
                if (!task.AutoRun)
                {
                    if (!_schedules.TryClaim(task.Id, "ready")) continue;
                    _vm.Timeline.AddNotification("计划任务已到期，等待确认：" + task.Prompt); continue;
                }
                // Persist the dispatch boundary first. A crash must never blindly repeat tools.
                if (!_schedules.TryClaim(task.Id, "dispatching")) continue;
                _activeScheduledId = task.Id;
                try
                {
                    await _vm.Session.SendAsync(task.Prompt);
                    _schedules.Update(task.Id, t => t.State == "dispatching" ? t with { State = "accepted" } : t);
                }
                catch (Exception error)
                { _activeScheduledId = null; _schedules.Update(task.Id, t => t with { State = "failed", Error = error.Message }); _vm.Timeline.AddNotification("计划任务提交失败：" + error.Message); }
                break;
            }
        }
        catch (Exception error) { _vm.Timeline.AddNotification("计划任务读取失败：" + error.Message); _statusTimer.Tick -= CheckSchedules; }
        finally { _checkingSchedules = false; }
    }
    private Task ShowSchedulesAsync()
    {
        _schedules ??= new WorkbenchScheduleStore(_customization.DataRoot);
        _schedules.Reload();
        var panel = new StackPanel { Spacing = 12 };
        var date = new DatePicker { SelectedDate = DateTimeOffset.Now };
        var time = new TimePicker { SelectedTime = DateTime.Now.AddMinutes(10).TimeOfDay };
        var prompt = new TextBox { AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, PlaceholderText = "任务内容", MinHeight = 80 };
        var automatic = new CheckBox { Content = "到期时自动提交（应用须运行，会话须空闲）" };
        var add = new Button { Content = "创建计划任务" };
        add.Click += (_, _) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(prompt.Text) || date.SelectedDate is null || time.SelectedTime is null) throw new InvalidOperationException("请填写日期、时间和任务内容");
                var due = new DateTimeOffset(date.SelectedDate.Value.Date + time.SelectedTime.Value, DateTimeOffset.Now.Offset);
                if (due <= DateTimeOffset.Now) throw new InvalidOperationException("请选择未来时间");
                var task = new WorkbenchScheduledTask(Guid.NewGuid().ToString("N"), CurrentSessionId, due, prompt.Text, automatic.IsChecked == true);
                _schedules.Mutate(tasks => [..tasks, task]); prompt.Text = ""; _vm?.Timeline.AddNotification("计划任务已保存：" + due.ToString("g"));
            }
            catch (Exception error) { _vm?.Timeline.AddNotification(error.Message); }
        };
        panel.Children.Add(date); panel.Children.Add(time); panel.Children.Add(prompt); panel.Children.Add(automatic); panel.Children.Add(add);
        foreach (var task in _schedules.Tasks.Where(t => t.SessionId == CurrentSessionId))
        {
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(new TextBlock { Text = $"{task.DueAt.LocalDateTime:g} · {task.State}\n{task.Prompt}\n{task.Error}", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var remove = new Button { Content = "删除" }; remove.Click += (_, _) => { _schedules.Mutate(tasks => tasks.Where(t => t.Id != task.Id).ToArray()); row.IsVisible = false; };
            controls.Children.Add(remove);
            if (task.State is "ready" or "failed" or "dispatching")
            {
                var run = new Button { Content = "编辑后确认发送" };
                run.Click += (_, _) => { if (_vm is not null) _vm.Input.ApplyExternalText(task.Prompt); _schedules.Update(task.Id, t => t with { State = "draft" }); };
                controls.Children.Add(run);
            }
            row.Children.Add(controls); panel.Children.Add(row);
        }
        return ShowPanelAsync("计划任务（应用运行期间）", panel);
    }
}
