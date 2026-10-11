using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Fufucode.Core;

namespace Fufucode.App;

public partial class MainWindow
{
    private async Task ShowTracePanelAsync()
    {
        var path = _host?.Current.DiagnosticFilePath;
        if (path is null || !File.Exists(path)) { await ShowTextAsync("调用轨迹", "当前没有真实 Pi 调用日志"); return; }
        var rows = new ListBox { MinHeight = 180 };
        var detail = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinHeight = 120 };
        var filter = new ComboBox { ItemsSource = new[] { "全部", "失败", "未结束" }, SelectedIndex = 0, MinWidth = 120 };
        var refresh = new Button { Content = "刷新" };
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        TraceRow[] snapshot = [];
        void Apply()
        {
            rows.ItemsSource = snapshot.Where(item => filter.SelectedIndex switch { 1 => item.Failed, 2 => item.Pending, _ => true }).ToArray();
            detail.Text = "";
        }
        async Task Refresh()
        {
            refresh.IsEnabled = false;
            try
            {
                var events = await DiagnosticExport.ReadRecentAsync(path);
                snapshot = events.GroupBy(item => item.RequestId ?? item.Timestamp.ToString("O"))
                    .Select(group =>
                    {
                        var last = group.Last();
                        var failed = group.Any(item => item.EventType is "request_failed" or "connection_failed" || item.ExitCode is not null and not 0);
                        var pending = group.Any(item => item.EventType == "request_started") && !group.Any(item => item.EventType is "request_completed" or "request_failed");
                        var title = $"{group.First().Timestamp:HH:mm:ss}  {last.Command ?? last.EventType}  {(failed ? "失败" : pending ? "未结束" : "已记录")}  {(last.DurationMs is { } duration ? duration + " ms" : "")}";
                        var details = string.Join("\n", group.Select(item => $"{item.Timestamp:O}  {item.EventType}\n请求：{item.RequestId ?? "无"}  命令：{item.Command ?? "无"}  耗时：{item.DurationMs?.ToString() ?? "未知"} ms\n错误：{item.ErrorCode ?? "无"}  退出码：{item.ExitCode?.ToString() ?? "无"}  结果：{item.Outcome ?? "无"}"));
                        return new TraceRow(title, details, failed, pending);
                    }).Reverse().ToArray();
                status.Text = $"最近 {events.Count} 条脱敏事件 · {snapshot.Length} 组 · {DateTimeOffset.Now:HH:mm:ss}";
                Apply();
            }
            catch (Exception error) { status.Text = "读取失败：" + error.Message; rows.ItemsSource = Array.Empty<TraceRow>(); }
            finally { refresh.IsEnabled = true; }
        }
        rows.SelectionChanged += (_, _) => detail.Text = (rows.SelectedItem as TraceRow)?.Details ?? "";
        filter.SelectionChanged += (_, _) => Apply();
        refresh.Click += async (_, _) => await Refresh();
        await Refresh();
        await ShowPanelAsync("调用轨迹", new StackPanel { Spacing = 10, Children = {
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { filter, refresh } }, status, rows, detail } });
    }
    private sealed record TraceRow(string Title, string Details, bool Failed, bool Pending)
    { public override string ToString() => Title; }
}
